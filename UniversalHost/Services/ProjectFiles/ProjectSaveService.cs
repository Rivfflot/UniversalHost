using Dock.Model.Controls;
using ReactiveUI.Binding.Reactive;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models;

namespace UniversalHost.Services;

public partial class GlobalStatus : ReactiveObject
{
    // 静态单例实例
    public static GlobalStatus Instance { get; } = new();

    // 定义您需要绑定到 UI 的全局状态属性
    [Reactive] private bool _isProjectOpened = false;
    [Reactive] private bool _isMonitoring = false;
    [Reactive] private bool _isConnected = false;
    [Reactive] private bool _isXcpSessionActive = false;
    [Reactive] private bool _isIapRunning = false;
    [Reactive] private bool _isSerialIapActive = false;


    private readonly ObservableAsPropertyHelper<bool> _canEditMonitorSymbol;
    public bool CanEditMonitorSymbol => _canEditMonitorSymbol.Value;
    private readonly ObservableAsPropertyHelper<bool> _canEditFirmwareSymbols;
    public bool CanEditFirmwareSymbols => _canEditFirmwareSymbols.Value;

    private readonly ObservableAsPropertyHelper<bool> _canStartMonitor;
    public bool CanStartMonitor => _canStartMonitor.Value;

    private readonly ObservableAsPropertyHelper<bool> _canConnectDevice;
    public bool CanConnectDevice => _canConnectDevice.Value;
    private readonly ObservableAsPropertyHelper<bool> _canEditCommunicationSettings;
    public bool CanEditCommunicationSettings => _canEditCommunicationSettings.Value;
    private GlobalStatus()
    {
        this.WhenAnyValue(
                x => x.IsProjectOpened,
                x => x.IsConnected,
                x => x.IsXcpSessionActive,
                x => x.IsSerialIapActive,
                (opened, connected, xcpActive, serialIapActive) => opened && !connected && !xcpActive && !serialIapActive)
            .ToProperty(this,
                        x => x.CanConnectDevice,
                        out _canConnectDevice);

        this.WhenAnyValue(
                x => x.IsConnected,
                x => x.IsXcpSessionActive,
                x => x.IsIapRunning,
                (connected, xcpActive, iapRunning) => !connected && !xcpActive && !iapRunning)
            .ToProperty(this,
                        x => x.CanEditCommunicationSettings,
                        out _canEditCommunicationSettings);

        this.WhenAnyValue(
                x => x.IsProjectOpened,
                x => x.IsMonitoring,
                (opened, monitoring) => opened && !monitoring)
            .ToProperty(this,
                        x => x.CanEditMonitorSymbol,
                        out _canEditMonitorSymbol);

        this.WhenAnyValue(
                x => x.IsProjectOpened,
                x => x.IsConnected,
                x => x.IsXcpSessionActive,
                x => x.IsMonitoring,
                (opened, connected, xcpActive, monitoring) => opened && !connected && !xcpActive && !monitoring)
            .ToProperty(this, x => x.CanEditFirmwareSymbols, out _canEditFirmwareSymbols);

        this.WhenAnyValue(
                x => x.IsConnected,
                x => x.IsMonitoring,
                (connected, monitoring) => connected && !monitoring)
            .ToProperty(this,
                        x => x.CanStartMonitor,
                        out _canStartMonitor);
    }
}
public class ProjectSaveService : ReactiveObject
{
    private static readonly ProjectSaveService _instance = new ProjectSaveService();
    public static ProjectSaveService Instance => _instance;

    private string _projectFilePath = "";
    public string ProjectFilePath
    {
        get => _projectFilePath;
        set
        {
            this.RaiseAndSetIfChanged(ref _projectFilePath, value);
        }
    }

    ProjectSettings _settings = new ProjectSettings();

    public ProjectSettings Settings
    {
        get => _settings;
        set
        {
            this.RaiseAndSetIfChanged(ref _settings, value);
        }
    }

    private readonly CompositeDisposable _disposables = [];
    private static readonly Dock.Serializer.SystemTextJson.DockSerializer _dockSerializer = new();
    private static readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };
    private const string SettingsEntryName = "settings.json";//设置项
    private const string LayoutEntryName = "layout.json";//布局
    private const string ContextEntryName = "context.json";//窗口上下文

    public static Task SaveSettingsAsync() => SaveSettingsAsync(Instance.ProjectFilePath, Instance.Settings);

    private static async Task SaveSettingsAsync(string path, ProjectSettings settings)
    {
        var settingsSnapshot = CreateSettingsSnapshot(settings, path, path);
        await SaveArchiveAsync(path, ZipArchiveMode.Update, async archive =>
        {
            // 更新前检查格式版本，避免覆盖当前程序无法读取的工程。
            ProjectMetadataService.Read(archive);
            archive.GetEntry(SettingsEntryName)?.Delete();
            await WriteJsonEntryAsync(archive, SettingsEntryName, settingsSnapshot);
            await ProjectMetadataService.SaveAsync(archive);
        });
    }

    public static async Task SaveProjectAsync(string path, IRootDock layout)
    {
        var settingsSnapshot = CreateSettingsSnapshot(Instance.Settings, Instance.ProjectFilePath, path);
        await SaveArchiveAsync(path, ZipArchiveMode.Create, async archive =>
        {
            await ProjectMetadataService.SaveAsync(archive);
            await WriteJsonEntryAsync(archive, SettingsEntryName, settingsSnapshot);

            var layoutEntry = archive.CreateEntry(LayoutEntryName, CompressionLevel.Optimal);
            await using (var stream = await layoutEntry.OpenAsync())
            await using (var writer = new StreamWriter(stream))
                await writer.WriteAsync(_dockSerializer.Serialize(layout));

            DockableRegistry.UpdateContextSave();
            await WriteJsonEntryAsync(archive, ContextEntryName, DockableRegistry.ContextSave);
        });
    }

    private static async Task SaveArchiveAsync(string path, ZipArchiveMode mode, Func<ZipArchive, Task> writeEntries)
    {
        path = Path.GetFullPath(path);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".uhprj-{Guid.NewGuid():N}.tmp");
        bool temporaryCreated = false;
        // 自动保存和手动保存依次进行，设置更新始终基于上次提交的归档。
        await _saveGate.WaitAsync();
        try
        {
            await using (var temporary = new FileStream(temporaryPath, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true))
            {
                temporaryCreated = true;
                if (mode == ZipArchiveMode.Update)
                {
                    await using var original = new FileStream(path, FileMode.Open,
                        FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                    await original.CopyToAsync(temporary);
                    temporary.Position = 0;
                }

                await using (var archive = await ZipArchive.CreateAsync(temporary, mode, leaveOpen: true, entryNameEncoding: null))
                    await writeEntries(archive);

                await Task.Run(() => temporary.Flush(flushToDisk: true));
            }

            if (mode == ZipArchiveMode.Update || File.Exists(path))
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, path);
            temporaryCreated = false;
        }
        finally
        {
            _saveGate.Release();
            if (temporaryCreated)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogService.ForModule(LogModule.Project).Warning(ex, "清理工程临时文件失败: {TemporaryPath}", temporaryPath);
                }
            }
        }
    }

    private static async Task WriteJsonEntryAsync<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = await entry.OpenAsync();
        await JsonSerializer.SerializeAsync(stream, value, _jsonOptions);
    }


    // 仅调整保存快照：另存为到其他目录时继续指向原文件，不修改当前工程设置。
    private static JsonNode CreateSettingsSnapshot(ProjectSettings settings, string sourcePath, string targetPath)
    {
        var snapshot = JsonSerializer.SerializeToNode(settings, _jsonOptions)!;
        snapshot[nameof(ProjectSettings.IapConfig)]![nameof(IapConfig.IapFilePath)] =
            ProjectFilePathService.RebasePath(settings.IapConfig.IapFilePath, sourcePath, targetPath);
        var symbolPaths = new JsonArray();
        foreach (var filePath in settings.DeviceConfig.SymbolFilePaths.Items)
            symbolPaths.Add(ProjectFilePathService.RebasePath(filePath, sourcePath, targetPath));
        snapshot[nameof(ProjectSettings.DeviceConfig)]!["SymbolFilePaths"] = symbolPaths;
        // Snapshot identities must follow the same rebasing as their source list.
        // This changes only the serialized copy, never the loaded access layout.
        foreach (var row in snapshot[nameof(ProjectSettings.DeviceConfig)]!["FirmwareSnapshots"]!.AsArray())
            row!["SourcePath"] = ProjectFilePathService.RebasePath(row["SourcePath"]!.GetValue<string>(), sourcePath, targetPath);
        foreach (var rows in new[]
        {
            snapshot[nameof(ProjectSettings.MonitorConfig)]!["MonitoredSymbols"]!.AsArray(),
            snapshot[nameof(ProjectSettings.CalibrateConfig)]!["CalibratedSymbols"]!.AsArray()
        })
            foreach (var row in rows)
                if (row?[nameof(SymbolInfo.SourceKey)]?.GetValue<string>() is { Length: > 0 } key)
                    row[nameof(SymbolInfo.SourceKey)] = ProjectFilePathService.RebasePath(key, sourcePath, targetPath);
        return snapshot;
    }

    internal sealed record LoadedProject(ProjectSettings Settings, DockableRegistry.ContextSaveClass? Context, IRootDock? Layout);

    internal static Task<(LoadedProject Project, ProjectConversionResult? Conversion)> ReadProjectAsync(string path) =>
        Task.Run<(LoadedProject, ProjectConversionResult?)>(async () =>
        {
            int formatVersion;
            using (var stream = OpenProjectFile(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                formatVersion = ProjectMetadataService.Read(archive, allowOlderVersion: true).FormatVersion;
                if (formatVersion == ProjectMetadata.CurrentFormatVersion)
                    return (ReadProjectEntries(archive), null);
            }

            var conversion = await ProjectConversionService.ConvertAsync(path, formatVersion);
            using var convertedStream = OpenProjectFile(path);
            using var convertedArchive = new ZipArchive(convertedStream, ZipArchiveMode.Read);
            ProjectMetadataService.Read(convertedArchive);
            return (ReadProjectEntries(convertedArchive), conversion);
        });

    private static FileStream OpenProjectFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static LoadedProject ReadProjectEntries(ZipArchive archive)
    {
        var settings = ReadJsonEntry<ProjectSettings>(archive.GetEntry(SettingsEntryName))
            ?? throw new InvalidDataException("读取设置错误");
        var context = ReadJsonEntry<DockableRegistry.ContextSaveClass>(archive.GetEntry(ContextEntryName));

        IRootDock? layout = null;
        if (context != null && archive.GetEntry(LayoutEntryName) is { } layoutEntry)
        {
            using var reader = new StreamReader(layoutEntry.Open());
            layout = _dockSerializer.Deserialize<IRootDock>(reader.ReadToEnd());
        }

        return new LoadedProject(settings, context, layout);
    }

    private static T? ReadJsonEntry<T>(ZipArchiveEntry? entry) where T : class
    {
        if (entry == null)
            return null;

        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream);
    }

    internal static void ApplyProject(string path, LoadedProject project)
    {
        // 数据已完整读取，再切换当前工程并恢复引用新运行时的窗口。
        DockableRegistry.ClearAllDocuments();
        Update(path, project.Settings);
        DockableRegistry.RebuildViewModels(project.Context ?? new DockableRegistry.ContextSaveClass());
    }

    public static void Update(string path, ProjectSettings? settings = null)
    {
        Instance._disposables.Clear();
        Instance.ProjectFilePath = path;
        if (settings != null)
        {
            Instance.Settings = settings;
        }
        var currentSettings = Instance.Settings;
        // 自动保存设置
        currentSettings.SubscribeToAllChanges(async () =>
        {
            // 捕获所属工程，避免旧工程的延迟回调保存到新工程路径。
            try
            {
                await SaveSettingsAsync(path, currentSettings);
            }
            catch (Exception ex)
            {
                LogService.ForModule(LogModule.Project).Error(ex, "自动保存设置失败: {ProjectPath}", path);
            }
        }).DisposeWith(Instance._disposables);
        //日志初始化
        LogService.LogServiceConfig(System.IO.Path.GetDirectoryName(Instance.ProjectFilePath)!,
                     Instance.Settings.LogConfig.LogWriteToFileEnabled, Instance._disposables);
        //订阅日志设置更新
        var isInitialLogConfiguration = true;
        currentSettings.LogConfig.WhenAnyValue(
            x => x.LogEnabled,
            x => x.LogEventLevelSetting,
            x => x.LogWriteToFileEnabled,
            (enabled, level, writeToFile) => (Enabled: enabled, Level: level, WriteToFile: writeToFile))
        .Subscribe(values =>
        {
            var effectiveLevel = values.Enabled
                ? values.Level
                : Serilog.Events.LogEventLevel.Fatal + 1;
            // WhenAnyValue 首次推送的是已保存配置，并非用户修改。
            LogService.SetLogLevel(effectiveLevel, logChange: !isInitialLogConfiguration);
            isInitialLogConfiguration = false;
            LogService.IsWriteToFileEnabled = values.WriteToFile;
        }).DisposeWith(Instance._disposables);
        //重建变量集合
        SymbolRuntimeService.RebuildSymbolRuntimes();
    }
}

public static class DataSaveService
{
    private static string GetCsvVariableName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            builder.Append(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'
                ? character
                : '_');
        }
        return builder.ToString();
    }
    public static string SaveToCsv(IReadOnlyList<SymbolRuntime> runtimes, string fileName)
    {
#if DEBUG
        var watch = System.Diagnostics.Stopwatch.StartNew();
#endif
        string savePath = Path.Combine(Path.GetDirectoryName(ProjectSaveService.Instance.ProjectFilePath)!,
                                                      $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        using var fs = new FileStream(
                                    savePath,
                                    FileMode.Create,
                                    FileAccess.Write,
                                    FileShare.Read,
                                    bufferSize: 131072,
                                    useAsync: true);

        using var writer = new StreamWriter(fs, new UTF8Encoding(false), 131072);

        List<string> names = [];

        int maxLen = 0;
        foreach (var item in runtimes)
        {
            names.Add(GetCsvVariableName(item.Symbol.Name));

            if (item.SampleHistory.Count > maxLen)
            {
                maxLen = item.SampleHistory.Count;
            }
        }

        if (maxLen == 0) throw new NotImplementedException("当前历史数据为空");

        string firstLine = string.Join(",", names);

        writer.WriteLine(firstLine);

        StringBuilder rowBuilder = new StringBuilder();

        for (int i = 0; i < maxLen; i++)
        {
            rowBuilder.Clear();

            for (int j = 0; j < runtimes.Count; j++)
            {
                if (j > 0) rowBuilder.Append(',');
                rowBuilder.Append(runtimes[j].GetValueHistoryIndexString(i));
            }

            writer.WriteLine(rowBuilder.ToString());
        }

        writer.Flush();
#if DEBUG
        watch.Stop();
        Debug.WriteLine($"写入CSV耗时: {watch.ElapsedMilliseconds} ms");
#endif
        return savePath;
    }
}
