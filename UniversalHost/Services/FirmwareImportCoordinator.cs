using DynamicData;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Symbols;

namespace UniversalHost.Services;

public sealed partial class FirmwareImportCoordinator : ReactiveObject
{
    public static FirmwareImportCoordinator Instance { get; } = new();
    [Reactive] private bool _isBusy;
    private CancellationTokenSource? _cancellation;
    public void Cancel() => _cancellation?.Cancel();

    public async Task ReloadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            var settings = ProjectSaveService.Instance.Settings;
            var report = await FirmwareSymbolService.UpdateAsync(settings, ProjectSaveService.Instance.ProjectFilePath,
                cancellation.Token, stillCurrent: () => ReferenceEquals(settings, ProjectSaveService.Instance.Settings));
            ShowReport(report);
        }
        catch (OperationCanceledException) { NotificationService.Show("已取消固件解析", "保存的变量配置未改变", NotificationType.Info); }
        catch (Exception ex)
        {
            LogService.ForModule(LogModule.Symbols).Error(ex, "主动更新固件失败");
            NotificationService.Show("更新失败", ex.Message, NotificationType.Error);
        }
        finally { _cancellation = null; IsBusy = false; }
    }

    public async Task ImportAsync(string path)
    {
        if (IsBusy) return;
        FirmwareSymbolService.EnsureLayoutEditable();
        IsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            var settings = ProjectSaveService.Instance.Settings;
            var projectPath = ProjectSaveService.Instance.ProjectFilePath;
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (settings.DeviceConfig.SymbolFilePaths.Items.Any(p => comparer.Equals(ProjectFilePathService.ResolvePath(p, projectPath), path)))
                throw new InvalidOperationException("文件已存在于列表中，请使用“更新符号表”");
            var storedPath = ProjectFilePathService.ToStoredPath(path, projectPath);
            var image = (await new FirmwareParser().ParseAsync(path, cancellation.Token)) with { SourcePath = storedPath };
            FirmwareSymbolService.EnsureLayoutEditable();
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(settings, ProjectSaveService.Instance.Settings)) throw new OperationCanceledException("解析期间已切换工程");
            settings.DeviceConfig.SymbolFilePaths.Add(storedPath);
            ShowReport(FirmwareSymbolService.ApplySnapshots(settings, [image]));
        }
        catch (OperationCanceledException) { NotificationService.Show("已取消固件解析", "未应用解析结果", NotificationType.Info); }
        catch (Exception ex)
        {
            LogService.ForModule(LogModule.Symbols).Error(ex, "导入固件失败");
            NotificationService.Show("导入失败", ex.Message, NotificationType.Error);
        }
        finally { _cancellation = null; IsBusy = false; }
    }

    private static void ShowReport(FirmwareUpdateReport report)
    {
        NotificationService.Show("符号表已更新",
            $"本次解析 {report.ParsedSymbolCount} 个根符号；符号表共 {report.SymbolCount} 项（含成员）；确认删除 {report.Removed.Count} 项；诊断 {report.Diagnostics.Count} 项",
            NotificationType.Success);
        if (report.Removed.Count > 0)
        {
            var message = string.Join(Environment.NewLine, report.Removed.Select(r => $"{r.Path}: {r.Reason}"));
            LogService.ForModule(LogModule.Symbols).Warning("更新删除 {Count} 项: {Reasons}", report.Removed.Count, message);
            NotificationService.Show($"删除 {report.Removed.Count} 个失效访问项", message, NotificationType.Warning);
        }
        if (report.Diagnostics.Count > 0)
        {
            var message = string.Join(Environment.NewLine, report.Diagnostics);
            LogService.ForModule(LogModule.Symbols).Warning("固件解析诊断: {Diagnostics}", message);
            NotificationService.Show("固件解析诊断", message, NotificationType.Warning);
        }
    }
}
