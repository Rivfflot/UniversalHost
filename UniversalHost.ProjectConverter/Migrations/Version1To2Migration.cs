using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.Symbols;

namespace UniversalHost.ProjectConverter.Migrations;

internal sealed class Version1To2Migration : IProjectMigration
{
    public int FromVersion => 1;
    public int ToVersion => 2;

    public async Task<IReadOnlyList<ProjectConversionRemoval>> ApplyAsync(ZipArchive archive, string projectPath, CancellationToken token)
    {
        var settings = await ProjectJson.ReadAsync(archive, "settings.json", token);
        var device = settings["DeviceConfig"] as JsonObject
            ?? throw new InvalidDataException("工程缺少设备设置。");
        var images = new List<FirmwareImage>();
        // 转换器与主程序共享 dependencies 下唯一的一份解析工具。
        var parser = new FirmwareParser(new ParseOptions { WorkerPath = FirmwareParser.GetWorkerPath(AppContext.BaseDirectory) });
        if (device["SymbolFilePaths"] is not null and not JsonArray)
            throw new InvalidDataException("工程固件路径列表格式无效。");
        var paths = device["SymbolFilePaths"] as JsonArray ?? [];
        if (paths.Count == 0 && (device["Symbols"] is JsonArray { Count: > 0 } ||
                                device["FirmwareSnapshots"] is JsonArray { Count: > 0 }))
            throw new InvalidDataException("工程包含符号但没有固件路径，无法重建类型快照。");
        var resolvedPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var item in paths)
        {
            token.ThrowIfCancellationRequested();
            var key = item?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("工程包含空固件路径。");
            var fullPath = ProjectFilePathService.ResolvePath(key, projectPath);
            if (!resolvedPaths.Add(fullPath)) throw new InvalidDataException($"固件路径重复: {key}");
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"转换工程需要原固件文件，请恢复该文件后重试: {fullPath}", fullPath);
            try
            {
                images.Add((await parser.ParseAsync(fullPath, token)) with { SourcePath = key });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new InvalidDataException($"固件转换失败 ({key}): {ex.Message}", ex); }
        }

        // 1 曾同时用于旧符号表和类型快照结构。按行识别，避免破坏过渡版本的自动访问。
        var removed = new List<ProjectConversionRemoval>();
        var removedMonitorIds = new HashSet<Guid>();
        var removedCalibrateIds = new HashSet<Guid>();
        foreach (var (configName, rowsName, group, removedIds) in new[]
        {
            ("MonitorConfig", "MonitoredSymbols", "监控", removedMonitorIds),
            ("CalibrateConfig", "CalibratedSymbols", "标定", removedCalibrateIds)
        })
        {
            var savedRows = settings[configName]?[rowsName];
            if (savedRows == null) continue;
            var rows = savedRows as JsonArray ?? throw new InvalidDataException($"{rowsName} 列表格式无效。");
            foreach (var node in rows.ToArray())
            {
                token.ThrowIfCancellationRequested();
                var row = node as JsonObject ?? throw new InvalidDataException($"{rowsName} 包含无效变量。");
                var reason = ConvertAccess(row, images, projectPath);
                if (reason == null) continue;
                var id = row["Id"]!.GetValue<Guid>();
                var source = row["SourceKey"]?.GetValue<string>();
                if (string.IsNullOrEmpty(source)) source = row["SourceFileName"]?.GetValue<string>() ?? "";
                removed.Add(new(id, group, row["Name"]?.GetValue<string>() ?? "",
                    source, reason));
                removedIds.Add(id);
                rows.Remove(row);
            }
        }
        device.Remove("Symbols");
        device["FirmwareSnapshots"] = JsonSerializer.SerializeToNode(images);
        await ProjectJson.WriteAsync(archive, "settings.json", settings, token);
        await RemoveContextReferencesAsync(archive, removedMonitorIds, removedCalibrateIds, token);
        return removed;
    }

    private static string? ConvertAccess(JsonObject row, List<FirmwareImage> images, string projectPath)
    {
        var name = row["Name"]?.GetValue<string>() ?? "";
        if (!Guid.TryParse(row["Id"]?.GetValue<string>(), out var id) || id == Guid.Empty)
            throw new InvalidDataException($"变量 {name} 缺少有效 ID，无法保留窗口关联。");
        var key = row["SourceKey"]?.GetValue<string>() ?? "";
        var sourceName = row["SourceFileName"]?.GetValue<string>() ?? "";
        var oldDefinition = row["Definition"]?.Deserialize<FirmwareSymbol>();
        var linkage = oldDefinition?.LinkageName ?? "";
        var rootName = oldDefinition?.Name ?? name;
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sources = images.Where(image => key.Length > 0
            ? pathComparer.Equals(ProjectFilePathService.ResolvePath(image.SourcePath, projectPath),
                ProjectFilePathService.ResolvePath(key, projectPath))
            : sourceName.Length == 0 || pathComparer.Equals(Path.GetFileName(image.SourcePath), sourceName));
        var matches = sources.SelectMany(image => image.Symbols
            .Where(symbol => linkage.Length > 0 ? symbol.LinkageName == linkage : symbol.Name == rootName)
            .Select(symbol => (Image: image, Symbol: symbol))).ToArray();
        if (matches.Length == 0) return "在固件中找不到根符号";
        if (matches.Length > 1) return "符号来源或名称匹配不唯一";
        var (image, root) = matches[0];
        if (root.Address > uint.MaxValue || root.SizeUnits > uint.MaxValue)
            throw new InvalidDataException($"变量 {name} 超出当前 32 位地址或大小范围。");
        var memberPath = row["MemberPath"]?.GetValue<string>() ?? "";
        var objectAccess = AccessPathResolver.ResolveObject(image, root, memberPath);
        if (memberPath.Length > 0 && (objectAccess.IsDefinitivelyInvalid || objectAccess.Status is AccessStatus.InvalidPath or AccessStatus.Conflict))
            throw new InvalidDataException($"变量 {name} 的保存路径无法重新验证: {objectAccess.Reason}");
        // 与新添加监控/标定变量相同：数组默认展开到各维起始下标，再解析最终标量。
        if (image.Types.TryGetValue(objectAccess.TypeId, out var type))
            memberPath = AccessPathResolver.GetDefaultElementPath(image, type, memberPath);
        var access = AccessPathResolver.Resolve(image, root, memberPath);
        bool automatic = access.Status == AccessStatus.Verified;
        int inputKind = memberPath.Contains('[') ? 1 : 0;
        row["SourceKey"] = image.SourcePath;
        row["SourceFileName"] = Path.GetFileName(image.SourcePath);
        row["Definition"] = JsonSerializer.SerializeToNode(root);
        row["Target"] = JsonSerializer.SerializeToNode(image.Target);
        row["Address"] = (uint)root.Address;
        row["Size"] = (uint)root.SizeUnits;
        row["Mode"] = automatic ? (int)AccessMode.Automatic : (int)AccessMode.Manual;
        row["Name"] = root.Name + memberPath;
        row["MemberPath"] = memberPath;
        row["InputKind"] = inputKind;
        // 不读取旧的手动类型；无法解析时也不能把旧类型冒充为自动解析结果。
        row["DataType"] = automatic ? (int)access.Scalar : (int)ScalarType.Unknown;
        row["Offset"] = 0;
        row["Access"] = automatic ? JsonSerializer.SerializeToNode(access) : null;
        if (!automatic)
        {
            uint offset = 0;
            if (inputKind == 1)
            {
                var element = AccessPathResolver.ResolveObject(image, root, memberPath);
                if (element.Status != AccessStatus.Verified)
                    throw new InvalidDataException($"变量 {name} 的数组路径无法重新验证: {element.Reason}");
                row["Access"] = JsonSerializer.SerializeToNode(element);
                offset = checked((uint)element.OffsetUnits);
            }
            else if (objectAccess.Status == AccessStatus.Verified)
                offset = checked((uint)objectAccess.OffsetUnits);
            _ = checked((uint)root.Address + offset);
            row["Offset"] = offset;
        }
        row["VerificationStatus"] = automatic ? (int)AccessStatus.Verified : (int)AccessStatus.Unverified;
        row["VerificationReason"] = automatic
            ? "工程转换时已根据固件重新验证访问路径"
            : $"工程转换时未能自动解析类型，请重新选择类型：{access.Reason}";
        return null;
    }

    private static async Task RemoveContextReferencesAsync(ZipArchive archive, HashSet<Guid> monitorIds,
        HashSet<Guid> calibrateIds, CancellationToken token)
    {
        if ((monitorIds.Count == 0 && calibrateIds.Count == 0) || archive.GetEntry("context.json") == null) return;
        var context = await ProjectJson.ReadAsync(archive, "context.json", token);
        bool changed = false;
        foreach (var (mapName, listName, objectItems, removedIds) in new[]
        {
            ("GridMonitorIdLayoutPairs", "DisplaySymbols", false, monitorIds),
            // 曲线列表的历史 JSON 字段名同样为 BitsMonitorSymbols。
            ("CurveMonitorIdLayoutPairs", "BitsMonitorSymbols", true, monitorIds),
            ("BitsMonitorIdLayoutPairs", "BitsMonitorSymbols", true, monitorIds),
            ("GridClibrateIdLayoutPairs", "DisplaySymbols", false, calibrateIds)
        })
        {
            if (removedIds.Count == 0 || context[mapName] is not JsonObject layouts) continue;
            foreach (var (_, node) in layouts)
            {
                token.ThrowIfCancellationRequested();
                if (node is not JsonObject layout) continue;
                if (layout[listName] is JsonArray items)
                    for (int index = items.Count - 1; index >= 0; index--)
                    {
                        var savedId = objectItems ? items[index]?["Id"] : items[index];
                        if (Guid.TryParse(savedId?.GetValue<string>(), out var id) && removedIds.Contains(id))
                        {
                            items.RemoveAt(index);
                            changed = true;
                        }
                    }
                if (mapName == "CurveMonitorIdLayoutPairs" &&
                    Guid.TryParse(layout["SelectedCurveId"]?.GetValue<string>(), out var selectedId) && monitorIds.Contains(selectedId))
                {
                    layout["SelectedCurveId"] = null;
                    changed = true;
                }
            }
        }
        if (changed) await ProjectJson.WriteAsync(archive, "context.json", context, token);
    }
}
