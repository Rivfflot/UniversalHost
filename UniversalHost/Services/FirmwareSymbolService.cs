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

public sealed record RemovedAccess(Guid Id, string Path, string Reason);
public sealed record FirmwareUpdateReport(List<RemovedAccess> Removed, List<string> Diagnostics, int ParsedSymbolCount, int SymbolCount);

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
        item.VerificationStatus = AccessStatus.Unverified;
        item.VerificationReason = "手动类型和目标地址单位偏移，未经自动验证";
        item.RefreshAccessDisplay();
    }

    public static void EnsureSymbolEditable()
    {
        if (!GlobalStatus.Instance.CanEditMonitorSymbol)
            throw new InvalidOperationException("请先打开工程并停止监控，再修改变量");
    }

    public static void EnsureLayoutEditable()
    {
        if (GlobalStatus.Instance.IsMonitoring || GlobalStatus.Instance.IsXcpSessionActive || GlobalStatus.Instance.IsConnected)
            throw new InvalidOperationException("请先停止采集并断开设备，再修改变量访问布局");
    }

    public static void RemoveSnapshot(ProjectSettings settings, string? source = null)
    {
        EnsureLayoutEditable();
        settings.DeviceConfig.FirmwareSnapshots = settings.DeviceConfig.FirmwareSnapshots
            .Where(f => source != null && f.SourcePath != source).ToList();
        settings.DeviceConfig.Symbols.Edit(list => list.RemoveMany(list.Where(s => source == null || s.SourceKey == source).ToArray()));
        foreach (var item in settings.MonitorConfig.MonitoredSymbols.Items.Concat(settings.CalibrateConfig.CalibratedSymbols.Items)
            .Where(s => source == null || s.SourceKey == source))
            Unverify(item, AccessStatus.Unverified, "符号快照已移除；保留配置，请重新导入或主动更新固件");
    }

    public static async Task<FirmwareUpdateReport> UpdateAsync(ProjectSettings settings, string projectPath,
        CancellationToken token = default, FirmwareParser? parser = null, Func<bool>? stillCurrent = null)
    {
        EnsureLayoutEditable();
        parser ??= new FirmwareParser();
        var snapshots = new List<FirmwareImage>();
        var failures = new Dictionary<string, string>();
        // Stage every file before mutating any saved configuration.
        foreach (var key in settings.DeviceConfig.SymbolFilePaths.Items.ToArray())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                snapshots.Add((await parser.ParseAsync(ProjectFilePathService.ResolvePath(key, projectPath), token)) with { SourcePath = key });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures[key] = ex.Message; }
        }
        token.ThrowIfCancellationRequested();
        if (stillCurrent != null && !stillCurrent()) throw new OperationCanceledException("解析期间已切换工程");
        EnsureLayoutEditable();
        return ApplySnapshots(settings, snapshots, failures);
    }

    public static FirmwareUpdateReport ApplySnapshots(ProjectSettings settings, IReadOnlyList<FirmwareImage> snapshots,
        IReadOnlyDictionary<string, string>? failures = null)
    {
        EnsureLayoutEditable();
        var removed = new List<RemovedAccess>();
        var diagnostics = new List<string>();
        var all = settings.MonitorConfig.MonitoredSymbols.Items.Concat(settings.CalibrateConfig.CalibratedSymbols.Items).ToArray();
        foreach (var image in snapshots)
        {
            diagnostics.AddRange(image.Diagnostics.Select(d => $"{image.SourcePath}: {d}"));
            var oldImages = settings.DeviceConfig.FirmwareSnapshots.Where(f => f.SourcePath != image.SourcePath).ToList();
            oldImages.Add(image);
            settings.DeviceConfig.FirmwareSnapshots = oldImages;
            settings.DeviceConfig.Symbols.Edit(list =>
            {
                list.RemoveMany(list.Where(s => s.SourceKey == image.SourcePath).ToArray());
                list.AddRange(image.Symbols.Where(s => s.Address <= uint.MaxValue).Select(s => ToSymbolInfo(image, s)));
            });
            foreach (var item in all.Where(s => s.SourceKey == image.SourcePath))
            {
                var (root, ambiguous) = MatchRoot(image, item);
                if (root == null)
                {
                    if (!ambiguous && image.SymbolsComplete && image.TypesComplete && image.Target.Linked && image.Target.AddressUnitBits is 8 or 16)
                        removed.Add(new(item.Id, item.Name, "根变量已不存在"));
                    else Unverify(item, ambiguous ? AccessStatus.Conflict : AccessStatus.Unverified,
                        ambiguous ? "同名根变量匹配不唯一" : "符号或类型信息不足，保留原配置");
                    continue;
                }
                string path = item.MemberPath;
                if (item.Mode == AccessMode.Manual)
                {
                    if (item.InputKind == SymbolInputKind.ArrayIndex)
                    {
                        var element = AccessPathResolver.ResolveObject(image, root, path);
                        if (element.IsDefinitivelyInvalid) removed.Add(new(item.Id, item.Name, element.Reason));
                        else if (element.Status == AccessStatus.Verified)
                        {
                            UpdateRoot(item, image, root);
                            item.Offset = checked((uint)element.OffsetUnits);
                            item.Access = element;
                            Unverify(item, AccessStatus.Unverified, "数组元素边界已检查，聚合类型按手动数值类型访问");
                        }
                        else Unverify(item, element.Status, element.Reason);
                        continue;
                    }
                    if (image.Types.TryGetValue(root.TypeId, out var type) && type.Complete && type.SizeUnits.HasValue && item.Offset >= type.SizeUnits.Value)
                    {
                        removed.Add(new(item.Id, item.Name, "手动偏移已越界"));
                        continue;
                    }
                    UpdateRoot(item, image, root);
                    Unverify(item, AccessStatus.Unverified, "手动类型和目标地址单位偏移");
                    continue;
                }
                var access = AccessPathResolver.Resolve(image, root, path);
                if (access.IsDefinitivelyInvalid)
                    removed.Add(new(item.Id, item.Name, access.Reason));
                else if (access.Status == AccessStatus.Verified)
                    ApplyVerified(item, image, root, access);
                else
                {
                    // Do not replace a saved automatic address with an unverified one.
                    if (item.Mode == AccessMode.Manual) UpdateRoot(item, image, root);
                    Unverify(item, access.Status, access.Reason);
                }
            }
        }
        if (failures != null)
            foreach (var failure in failures)
            {
                diagnostics.Add($"{failure.Key}: {failure.Value}；保留旧符号和配置");
                foreach (var item in all.Where(s => s.SourceKey == failure.Key))
                    Unverify(item, AccessStatus.Unverified, "固件解析失败：" + failure.Value);
            }
        foreach (var item in removed)
        {
            settings.MonitorConfig.MonitoredSymbols.RemoveKey(item.Id);
            settings.CalibrateConfig.CalibratedSymbols.RemoveKey(item.Id);
        }
        return new(removed, diagnostics,
            snapshots.Sum(image => image.Symbols.Count(symbol => symbol.Address <= uint.MaxValue)),
            SymbolSelectionService.BuildRows(settings.DeviceConfig).Count);
    }

    private static (FirmwareSymbol? Symbol, bool Ambiguous) MatchRoot(FirmwareImage image, UserSymbolInfo item)
    {
        var old = item.Definition;
        string name = old?.LinkageName is { Length: > 0 } linkage ? linkage : old?.Name ?? item.Name;
        var candidates = image.Symbols.Where(s => s.LinkageName == name || (old == null && s.Name == name)).ToList();
        return candidates.Count == 1 ? (candidates[0], false) : (null, candidates.Count > 1);
    }

    private static void UpdateRoot(UserSymbolInfo item, FirmwareImage image, FirmwareSymbol root)
    {
        if (root.Address > uint.MaxValue) return;
        item.SourceKey = image.SourcePath;
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
        item.VerificationStatus = AccessStatus.Verified;
        item.VerificationReason = "使用与设备运行固件同次构建的符号文件";
        item.RefreshAccessDisplay();
    }

    private static void Unverify(UserSymbolInfo item, AccessStatus status, string reason)
    {
        item.VerificationStatus = status;
        item.VerificationReason = reason;
        item.RefreshAccessDisplay();
    }
}
