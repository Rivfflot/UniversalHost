using DynamicData;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System;
using System.Linq;
using System.Threading.Tasks;
using UniversalHost.Symbols;

namespace UniversalHost.Services;

public sealed partial class FirmwareImportCoordinator : ReactiveObject
{
    public static FirmwareImportCoordinator Instance { get; } = new();
    [Reactive] private bool _isBusy;

    public async Task ReloadAsync()
    {
        if (IsBusy || !GlobalStatus.Instance.CanEditFirmwareSymbols) return;
        IsBusy = true;
        try
        {
            var settings = ProjectSaveService.Instance.Settings;
            var projectPath = ProjectSaveService.Instance.ProjectFilePath;
            var report = await FirmwareSymbolService.UpdateAsync(settings, projectPath,
                stillCurrent: () => ReferenceEquals(settings, ProjectSaveService.Instance.Settings) &&
                    projectPath == ProjectSaveService.Instance.ProjectFilePath);
            ShowReport(report);
        }
        catch (OperationCanceledException) { NotificationService.Show("已取消固件解析", "保存的变量配置未改变", NotificationType.Info); }
        catch (Exception ex)
        {
            LogService.ForModule(LogModule.Symbols).Error(ex, "主动更新固件失败");
            NotificationService.Show("更新符号表失败", ex.Message, NotificationType.Error);
        }
        finally { IsBusy = false; }
    }

    public async Task ImportAsync(string path, string? previousPath = null)
    {
        if (IsBusy || !GlobalStatus.Instance.CanEditFirmwareSymbols) return;
        IsBusy = true;
        try
        {
            var settings = ProjectSaveService.Instance.Settings;
            var projectPath = ProjectSaveService.Instance.ProjectFilePath;
            var paths = settings.DeviceConfig.SymbolFilePaths;
            if (previousPath != null && !paths.Items.Contains(previousPath))
                throw new InvalidOperationException("原固件路径已不在列表中，请重新选择");
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            path = ProjectFilePathService.ResolvePath(path, projectPath);
            if (paths.Items.Any(p => p != previousPath && comparer.Equals(ProjectFilePathService.ResolvePath(p, projectPath), path)))
                throw new InvalidOperationException(previousPath == null
                    ? "文件已存在于列表中，请使用“更新符号表”"
                    : "所选文件已在列表中，请选择其他文件");
            var storedPath = ProjectFilePathService.ToStoredPath(path, projectPath);
            var image = (await new FirmwareParser().ParseAsync(path)) with { SourcePath = storedPath };
            FirmwareSymbolService.EnsureUsableImage(image);
            FirmwareSymbolService.EnsureLayoutEditable();
            if (!ReferenceEquals(settings, ProjectSaveService.Instance.Settings) || projectPath != ProjectSaveService.Instance.ProjectFilePath)
                throw new OperationCanceledException("解析期间工程已变更");
            if (previousPath != null)
                ShowReport(FirmwareSymbolService.ReplaceSnapshot(settings, previousPath, image));
            else
            {
                paths.Add(storedPath);
                ShowReport(FirmwareSymbolService.ApplySnapshots(settings, [image]));
            }
        }
        catch (OperationCanceledException) { NotificationService.Show("已取消固件解析", "未应用解析结果", NotificationType.Info); }
        catch (Exception ex)
        {
            var title = previousPath == null ? "导入固件失败" : "更新路径失败";
            LogService.ForModule(LogModule.Symbols).Error(ex, "{Operation}，固件文件 {Path}", title, path);
            NotificationService.Show(title, ex.Message, NotificationType.Error);
        }
        finally { IsBusy = false; }
    }

    private static void ShowReport(FirmwareUpdateReport report)
    {
        var log = LogService.ForModule(LogModule.Symbols);
        foreach (var file in report.UpdatedFiles)
            log.Information("符号表更新完成，文件 {Path}，解析 {ParsedCount} 个根符号，更新 {UpdatedCount} 项变量，删除 {RemovedCount} 项变量",
                file.SourcePath, file.ParsedSymbolCount, file.UpdatedCount, file.RemovedCount);
        if (report.UpdatedFiles.Count > 0)
            NotificationService.Show("符号表已更新",
                $"已更新 {report.UpdatedFiles.Count} 个文件，符号表共 {report.SymbolCount} 项（含成员），删除 {report.Removed.Count} 项变量",
                NotificationType.Success);
        if (report.Removed.Count > 0)
        {
            foreach (var item in report.Removed)
                log.Warning("符号表更新删除变量 {Variable}，文件 {Path}，原因 {Reason}", item.Path, item.SourcePath, item.Reason);
            var message = string.Join(Environment.NewLine, report.Removed.Select(r => $"{r.SourcePath} · {r.Path}：{r.Reason}"));
            NotificationService.Show($"删除 {report.Removed.Count} 个失效访问项", message, NotificationType.Warning);
        }
        if (report.Diagnostics.Count > 0)
        {
            var message = string.Join(Environment.NewLine, report.Diagnostics);
            log.Warning("固件解析诊断: {Diagnostics}", message);
            NotificationService.Show("固件解析诊断", message, NotificationType.Warning);
        }
        if (report.Failures.Count > 0)
        {
            foreach (var failure in report.Failures)
                log.Error(failure.Error, "固件解析失败，已跳过 {Path}", failure.Path);
            var message = string.Join(Environment.NewLine, report.Failures.Select(f => $"{f.Path}：{f.Error.Message}"));
            NotificationService.Show("固件解析失败，已跳过该文件", message, NotificationType.Error);
        }
    }
}
