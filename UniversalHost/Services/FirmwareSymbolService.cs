using DynamicData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Symbols;

namespace UniversalHost.Services;

public sealed record RemovedAccess(Guid Id, string SourcePath, string Path, string Reason);
public sealed record FirmwareFileUpdate(string SourcePath, int ParsedSymbolCount, int UpdatedCount, int RemovedCount);
public sealed record FirmwareFileFailure(string Path, Exception Error);
public sealed record FirmwareUpdateReport(List<FirmwareFileUpdate> UpdatedFiles, List<RemovedAccess> Removed,
    List<string> Diagnostics, List<FirmwareFileFailure> Failures, int SymbolCount);

/// <summary>Explicit import/update only. Project loading must never call this service.</summary>
public static class FirmwareSymbolService
{
    public static FirmwareImage? FindImage(DeviceConfig device, SymbolInfo symbol) =>
        device.FirmwareSnapshots.SingleOrDefault(f => f.SourcePath == symbol.SourceKey);

    public static SymbolInfo ToSymbolInfo(FirmwareImage image, FirmwareSymbol symbol) => new()
    {
        SourceFileName = Path.GetFileName(image.SourcePath),
        SourceKey = image.SourcePath,
        Name = symbol.Name,
        Address = checked((uint)symbol.Address),
        Size = checked((uint)Math.Min(symbol.SizeUnits, uint.MaxValue)),
        Definition = symbol
    };

    public static UserSymbolInfo CreateAccess(DeviceConfig device, SymbolInfo root, string path, bool manual)
    {
        // 新建访问项不影响已有变量，允许在监控期间创建标定变量。
        var user = new UserSymbolInfo
        {
            Name = root.Name,
            SourceFileName = root.SourceFileName,
            SourceKey = root.SourceKey,
            Address = root.Address,
            Size = root.Size,
            Definition = root.Definition
        };
        var image = FindImage(device, root);
        if (image != null) user.Target = image.Target;
        if (!manual && image != null && root.Definition != null)
        {
            var resolution = AccessPathResolver.Resolve(image, root.Definition, path);
            if (resolution.Status != AccessStatus.Verified) throw new InvalidOperationException(resolution.Reason);
            ApplyVerified(user, image, root.Definition, resolution);
        }
        else if (!manual && path.Length > 0) throw new InvalidOperationException("成员访问需要完整的类型快照");
        user.RefreshAccessDisplay();
        return user;
    }

    public static void ChangePath(DeviceConfig device, UserSymbolInfo item, string path)
    {
        EnsureSymbolEditable();
        var image = FindImage(device, item) ?? throw new InvalidOperationException("缺少类型快照，请主动更新固件");
        var root = item.Definition ?? throw new InvalidOperationException("缺少根变量身份");
        var access = AccessPathResolver.Resolve(image, root, path);
        if (access.Status != AccessStatus.Verified) throw new InvalidOperationException(access.Reason);
        ApplyVerified(item, image, root, access);
    }

    public static void MakeManual(UserSymbolInfo item)
    {
        EnsureSymbolEditable();
        if (item.Mode == AccessMode.Automatic && item.Access != null)
            item.Offset = checked((uint)item.Access.OffsetUnits);
        item.Mode = AccessMode.Manual;
        item.RefreshAccessDisplay();
    }

    public static void EnsureSymbolEditable()
    {
        if (!GlobalStatus.Instance.CanEditMonitorSymbol)
            throw new InvalidOperationException("请先打开工程并停止监控，再修改变量");
    }

    public static void EnsureLayoutEditable()
    {
        if (!GlobalStatus.Instance.CanEditFirmwareSymbols)
            throw new InvalidOperationException("请打开工程并断开设备后再修改固件符号表");
    }

    public static void RemoveSnapshot(ProjectSettings settings, string? source = null)
    {
        EnsureLayoutEditable();
        settings.DeviceConfig.FirmwareSnapshots = settings.DeviceConfig.FirmwareSnapshots
            .Where(f => source != null && f.SourcePath != source).ToList();
        settings.DeviceConfig.Symbols.Edit(list => list.RemoveMany(list.Where(s => source == null || s.SourceKey == source).ToArray()));
    }

    public static async Task<FirmwareUpdateReport> UpdateAsync(ProjectSettings settings, string projectPath,
        CancellationToken token = default, FirmwareParser? parser = null, Func<bool>? stillCurrent = null)
    {
        EnsureLayoutEditable();
        parser ??= new FirmwareParser();
        List<FirmwareImage> snapshots = [];
        List<FirmwareFileFailure> failures = [];
        List<string> missingFiles = [];
        // Stage every file before mutating any saved configuration.
        foreach (var key in settings.DeviceConfig.SymbolFilePaths.Items.ToArray())
        {
            token.ThrowIfCancellationRequested();
            var path = key;
            try
            {
                path = ProjectFilePathService.ResolvePath(key, projectPath);
                try { File.GetAttributes(path); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    missingFiles.Add($"{path}：找不到原固件文件，已跳过");
                    continue;
                }
                var image = (await parser.ParseAsync(path, token)) with { SourcePath = key };
                EnsureUsableImage(image);
                snapshots.Add(image);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add(new(path, ex)); }
        }
        token.ThrowIfCancellationRequested();
        if (stillCurrent != null && !stillCurrent()) throw new OperationCanceledException("解析期间已切换工程");
        EnsureLayoutEditable();
        var report = ApplySnapshots(settings, snapshots);
        report.Diagnostics.AddRange(missingFiles);
        report.Failures.AddRange(failures);
        return report;
    }

    internal static void EnsureUsableImage(FirmwareImage image)
    {
        if (image.SymbolsComplete && image.TypesComplete && image.Target.Linked && image.Target.AddressUnitBits is 8 or 16 &&
            image.Symbols.Any(symbol => AccessPathResolver.ResolveObject(image, symbol, "").Status == AccessStatus.Verified))
            return;

        var reason = "固件缺少完整符号表、变量类型信息或受支持的目标地址布局";
        if (image.Diagnostics.Count > 0)
            reason += $"：{string.Join(Environment.NewLine, image.Diagnostics)}";
        throw new InvalidDataException(reason);
    }

    internal static FirmwareUpdateReport ApplySnapshots(ProjectSettings settings, IReadOnlyList<FirmwareImage> snapshots) =>
        ApplySnapshots(settings, snapshots, null);

    internal static FirmwareUpdateReport ReplaceSnapshot(ProjectSettings settings, string previousPath, FirmwareImage image)
    {
        EnsureLayoutEditable();
        var paths = settings.DeviceConfig.SymbolFilePaths;
        var index = paths.Items.ToList().IndexOf(previousPath);
        if (index < 0)
            throw new InvalidOperationException("原固件路径已不在列表中，请重新选择");

        var report = ApplySnapshots(settings, [image], previousPath);
        if (previousPath != image.SourcePath)
            paths.Edit(list => list[index] = image.SourcePath);
        return report;
    }

    private static FirmwareUpdateReport ApplySnapshots(ProjectSettings settings, IReadOnlyList<FirmwareImage> snapshots,
        string? previousPath)
    {
        EnsureLayoutEditable();
        List<FirmwareFileUpdate> updatedFiles = [];
        List<RemovedAccess> removed = [];
        List<string> diagnostics = [];
        UserSymbolInfo[] all = [.. settings.MonitorConfig.MonitoredSymbols.Items, .. settings.CalibrateConfig.CalibratedSymbols.Items];
        foreach (var image in snapshots)
        {
            var source = previousPath ?? image.SourcePath;
            var items = all.Where(item => item.SourceKey == source).ToArray();
            int removedBefore = removed.Count;
            diagnostics.AddRange(image.Diagnostics.Select(d => $"{image.SourcePath}: {d}"));
            var oldImages = settings.DeviceConfig.FirmwareSnapshots.Where(f => f.SourcePath != source).ToList();
            oldImages.Add(image);
            settings.DeviceConfig.FirmwareSnapshots = oldImages;
            settings.DeviceConfig.Symbols.Edit(list =>
            {
                list.RemoveMany(list.Where(s => s.SourceKey == source).ToArray());
                list.AddRange(image.Symbols.Where(s => s.Address <= uint.MaxValue).Select(s => ToSymbolInfo(image, s)));
            });
            foreach (var item in items)
            {
                var (root, ambiguous) = MatchRoot(image, item);
                if (root == null)
                {
                    removed.Add(new(item.Id, image.SourcePath, item.Name, ambiguous ? "同名根变量匹配不唯一" : "根变量已不存在"));
                    continue;
                }
                string path = item.MemberPath;
                if (item.Mode == AccessMode.Manual)
                {
                    if (item.InputKind == SymbolInputKind.ArrayIndex)
                    {
                        var element = AccessPathResolver.ResolveObject(image, root, path);
                        if (element.Status == AccessStatus.Verified)
                        {
                            UpdateRoot(item, image, root);
                            item.Offset = checked((uint)element.OffsetUnits);
                            item.Access = element;
                            item.RefreshAccessDisplay();
                        }
                        else removed.Add(new(item.Id, image.SourcePath, item.Name, element.Reason));
                        continue;
                    }
                    ulong size = image.Types.TryGetValue(root.TypeId, out var type) && type.Complete && type.SizeUnits.HasValue
                        ? type.SizeUnits.Value : root.SizeUnits;
                    if (size == 0 || item.Offset >= size || root.Address > uint.MaxValue || root.Address + item.Offset > uint.MaxValue)
                    {
                        string reason = size == 0 ? "缺少根变量大小，无法确定手动偏移范围" : "手动偏移或地址已越界";
                        removed.Add(new(item.Id, image.SourcePath, item.Name, reason));
                        continue;
                    }
                    UpdateRoot(item, image, root);
                    item.RefreshAccessDisplay();
                    continue;
                }
                var access = AccessPathResolver.Resolve(image, root, path);
                if (access.Status == AccessStatus.Verified)
                    ApplyVerified(item, image, root, access);
                else
                    removed.Add(new(item.Id, image.SourcePath, item.Name, access.Reason));
            }
            int removedCount = removed.Count - removedBefore;
            updatedFiles.Add(new(image.SourcePath, image.Symbols.Count(symbol => symbol.Address <= uint.MaxValue),
                items.Length - removedCount, removedCount));
        }
        foreach (var item in removed)
        {
            settings.MonitorConfig.MonitoredSymbols.RemoveKey(item.Id);
            settings.CalibrateConfig.CalibratedSymbols.RemoveKey(item.Id);
        }
        return new(updatedFiles, removed, diagnostics, [], SymbolSelectionService.BuildRows(settings.DeviceConfig).Count);
    }

    internal static (FirmwareSymbol? Symbol, bool Ambiguous) MatchRoot(FirmwareImage image, UserSymbolInfo item)
    {
        var old = item.Definition;
        string name = old?.LinkageName is { Length: > 0 } linkage ? linkage : old?.Name ?? item.Name;
        FirmwareSymbol? match = null;
        foreach (var candidate in image.Symbols)
        {
            if (candidate.LinkageName != name && ((old?.LinkageName.Length ?? 0) != 0 || candidate.Name != name)) continue;
            if (match != null) return (null, true);
            match = candidate;
        }
        return (match, false);
    }

    private static void UpdateRoot(UserSymbolInfo item, FirmwareImage image, FirmwareSymbol root)
    {
        if (root.Address > uint.MaxValue) return;
        item.SourceKey = image.SourcePath;
        item.SourceFileName = Path.GetFileName(image.SourcePath);
        item.Definition = root;
        item.Target = image.Target;
        item.Address = (uint)root.Address;
        item.Size = checked((uint)Math.Min(root.SizeUnits, uint.MaxValue));
    }

    private static void ApplyVerified(UserSymbolInfo item, FirmwareImage image, FirmwareSymbol root, AccessResolution access)
    {
        UpdateRoot(item, image, root);
        item.Name = root.Name + access.Path;
        item.MemberPath = access.Path;
        item.Mode = AccessMode.Automatic;
        item.Offset = 0;
        item.DataType = access.Scalar;
        item.Access = access;
        item.InputKind = access.Path.Contains('[') ? SymbolInputKind.ArrayIndex : SymbolInputKind.Offset;
        item.RefreshAccessDisplay();
    }
}
