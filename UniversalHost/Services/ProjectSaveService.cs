using Dock.Model.Controls;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Text;
using System.Text.Json;
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


    private readonly ObservableAsPropertyHelper<bool> _canEditMonitorSymbol;
    public bool CanEditMonitorSymbol => _canEditMonitorSymbol.Value;

    private readonly ObservableAsPropertyHelper<bool> _canStartMonitor;
    public bool CanStartMonitor => _canStartMonitor.Value;
    private GlobalStatus()
    {
        this.WhenAnyValue(
                x => x.IsProjectOpened,
                x => x.IsMonitoring,
                (opened, monitoring) => opened && !monitoring)
            .ToProperty(this,
                        x => x.CanEditMonitorSymbol,
                        out _canEditMonitorSymbol);

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
    private static readonly Dock.Serializer.DockSerializer _dockSerializer = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };
    private const string SettingsEntryName = "settings.json";//设置项
    private const string LayoutEntryName = "layout.json";//布局
    private const string ContextEntryName = "context.json";//窗口上下文

    public static async Task SaveSettingsAsync()
    {
        await using var fileStream = new FileStream(
            Instance.ProjectFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            65536,
            true);

        using var archive =
            new ZipArchive(fileStream, ZipArchiveMode.Update);

        //删除旧 ProjectSaveService.Instance.Settings.json
        archive.GetEntry(SettingsEntryName)?.Delete();

        var settingsEntry =
            archive.CreateEntry(
                SettingsEntryName,
                CompressionLevel.Optimal);

        await using (var stream = settingsEntry.Open())
        {
            await JsonSerializer.SerializeAsync(
                stream,
                Instance.Settings,
                _jsonOptions);
        }
    }
    public static async Task SaveProjectAsync(string path, IRootDock layout)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        await using var fileStream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            65536,
            true);

        using var archive =
            new ZipArchive(fileStream, ZipArchiveMode.Create);

        // settings.json
        var settingsEntry =
            archive.CreateEntry(
                SettingsEntryName,
                CompressionLevel.Optimal);

        await using (var stream = settingsEntry.Open())
        {
            await JsonSerializer.SerializeAsync(
                stream,
                Instance.Settings,
                options);
        }

        // layout.json
        var layoutEntry =
            archive.CreateEntry(
                LayoutEntryName,
                CompressionLevel.Optimal);
        await using (var stream = layoutEntry.Open())
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(
                _dockSerializer.Serialize(layout));

        }

        // context.json
        var contextEntry =
            archive.CreateEntry(
                ContextEntryName,
                CompressionLevel.Optimal);

        await using (var stream = contextEntry.Open())
        {
            DockableRegistry.UpdateContextSave();
            await JsonSerializer.SerializeAsync(
                stream,
                DockableRegistry.ContextSave,
                options);
        }
    }
    //顺序：读取settings，若读取失败则报错。读取成功后更新工程路径，销毁之前的订阅，订阅自动保存，
    //      初始化日志，订阅日志设置更新，重建监控标定变量集合，读取布局上下文，重建ViewModel，
    //      读取布局，重建布局。
    public static IRootDock? LoadProject(string path)
    {
        using var fileStream = new FileStream(path, FileMode.Open);

        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);

        // 读取 settings.json
        ProjectSettings? settings = null;
        var settingsEntry =
            archive.GetEntry(SettingsEntryName);

        if (settingsEntry != null)
        {
            string settingsJson;

            using (var reader = new StreamReader(
                       settingsEntry.Open()))
            {
                settingsJson = reader.ReadToEnd();
            }

            settings = JsonSerializer.Deserialize<ProjectSettings>(settingsJson);
        }
        if (settings != null)
        {
            Instance.Settings = settings;
            Update(path);
        }
        else
        {
            throw new System.Exception("读取设置错误");
        }
        // 读取 context.json
        IRootDock? layout = null;
        DockableRegistry.ContextSaveClass? context = null;
        var contextEntry =
            archive.GetEntry(ContextEntryName);

        if (contextEntry != null)
        {
            string contextJson;

            using (var reader = new StreamReader(
                       contextEntry.Open()))
            {
                contextJson = reader.ReadToEnd();
            }
            context = JsonSerializer.Deserialize<DockableRegistry.ContextSaveClass>(contextJson);
        }
        if (context != null)
        {
            //重建VM
            DockableRegistry.RebuildViewModels(context);
            // 读取 layout.json
            var layoutEntry = archive.GetEntry(LayoutEntryName);
            if (layoutEntry != null)
            {

                string layoutJson;

                using (var reader = new StreamReader(layoutEntry.Open()))
                {
                    layoutJson = reader.ReadToEnd();
                }

                layout = _dockSerializer.Deserialize<IRootDock>(layoutJson);
            }
        }
        else
        {
            DockableRegistry.RebuildViewModels(new DockableRegistry.ContextSaveClass());
        }
        return layout;
    }
    public static void Update(string path)
    {
        Instance._disposables.Clear();
        Instance.ProjectFilePath = path;
        // 自动保存设置
        Instance.Settings.SubscribeToAllChanges(async () =>
        {
            await SaveSettingsAsync();
        }).DisposeWith(Instance._disposables);
        //日志初始化
        LogService.LogServiceConfig(System.IO.Path.GetDirectoryName(Instance.ProjectFilePath)!,
                     Instance.Settings.LogConfig.LogWriteToFileEnabled, Instance._disposables);
        //订阅日志设置更新
        Instance.Settings.LogConfig.SubscribeToAllChanges(() =>
        {
            if (Instance.Settings.LogConfig.LogEnabled)
            {
                LogService.SetLogLevel(Instance.Settings.LogConfig.LogEventLevelSetting);
            }
            else
            {
                LogService.SetLogLevel(Serilog.Events.LogEventLevel.Fatal + 1);
            }
            LogService.IsWriteToFileEnabled = Instance.Settings.LogConfig.LogWriteToFileEnabled;
        }).DisposeWith(Instance._disposables);
        //重建变量集合
        SymbolRuntimeService.RebuildSymbolRuntimes();
    }
}

public static class CalibrateParametersSaveService
{
    private readonly struct CalibrateParameter
    {
        public Guid Id { get; init; }
        public string Name { get; init; }
        public string Alias { get; init; }
        public string ValueString { get; init; }
    }
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        // 允许序列化所有 Unicode 字符（如中文），防止被转义为 \uXXXX
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    /// <summary>
    /// 保存标定变量为json文件
    /// </summary>
    /// <returns></returns>
    public static async Task<string> SaveAsync()
    {
        string savePath = Path.Combine(Path.GetDirectoryName(ProjectSaveService.Instance.ProjectFilePath)!,
                                                        $"parameters_{DateTime.Now:yyyyMMdd_HHmmss}.json");

        var parameters = SymbolRuntimeService.CalibrateSymbolRuntimesSource.Items
            .Where(item => !string.IsNullOrEmpty(item.ValueString)) // 过滤空值
            .Select(item => new CalibrateParameter                  // 投影转换为新对象
            {
                Id = item.Symbol.Id,
                Name = item.Symbol.Name,
                Alias = item.Symbol.Alias,
                ValueString = item.ValueString
            })
            .ToArray();
        if (parameters.Length == 0)
        {
            throw new NotImplementedException("无可保存参数");
        }
        await using var fileStream = new FileStream(savePath,
                                                    FileMode.Create,
                                                    FileAccess.Write,
                                                    FileShare.None,
                                                    4096,
                                                    true);

        await JsonSerializer.SerializeAsync(fileStream, parameters, _jsonOptions);
        return savePath;
    }
    /// <summary>
    /// 从json文件读取标定变量并复制到标定变量集合
    /// </summary>
    /// <param name="path">json路径</param>
    /// <returns></returns>
    public static async Task LoadAsync(string path)
    {
        if (!File.Exists(path)) return;

        // 异步读取并反序列化 JSON 文件
        await using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var parameters = await JsonSerializer.DeserializeAsync<CalibrateParameter[]>(fileStream, _jsonOptions);

        if (parameters == null || parameters.Length == 0) throw new NullReferenceException("文件格式错误");

        // 遍历参数
        foreach (var param in parameters)
        {
            var optional = SymbolRuntimeService.CalibrateSymbolRuntimesSource.Lookup(param.Id);

            if (optional.HasValue)
            {
                optional.Value.ValueString = param.ValueString;
            }
        }
    }

}

public static class DataSaveService
{
    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;

        // 如果字段中包含逗号、双引号，需使用双引号包裹，并将原双引号转义为两个双引号
        if (field.Contains(',') || field.Contains('"'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }
    public static string SaveToCsv(IReadOnlyList<SymbolRuntime> runtimes, string fileName)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        string savePath = Path.Combine(Path.GetDirectoryName(ProjectSaveService.Instance.ProjectFilePath)!,
                                                      $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        using var fs = new FileStream(
                                    savePath,
                                    FileMode.Create,
                                    FileAccess.Write,
                                    FileShare.Read,
                                    bufferSize: 131072,
                                    useAsync: true);

        using var writer = new StreamWriter(fs, Encoding.UTF8, 131072);

        List<string> names = new List<string>();

        int maxLen = 0;
        foreach (var item in runtimes)
        {
            if (item.Symbol.Alias != "")
            {
                names.Add(EscapeCsvField($"{item.Symbol.Name}({item.Symbol.Alias})"));
            }
            else
            {
                names.Add(item.Symbol.Name);
            }

            if (item.PlotHistory.Count > maxLen)
            {
                maxLen = item.PlotHistory.Count;
            }
        }

        if (maxLen == 0) throw new NotImplementedException("当前历史数据为空");

        string firstLine = string.Join(",", names);

        writer.WriteLine(firstLine);

        StringBuilder rowBuilder = new StringBuilder();

        for (int i = 0; i < maxLen; i++)
        {
            rowBuilder.Clear();

            foreach (var item in runtimes)
            {
                rowBuilder.Append(item.GetValueHistoryIndexString(i)).Append(',');
            }

            writer.WriteLine(rowBuilder.ToString());
        }

        writer.Flush();

        watch.Stop();
        Debug.WriteLine($"写入CSV耗时: {watch.ElapsedMilliseconds} ms");

        return savePath;
    }
}
