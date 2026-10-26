using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Dock.Model.Controls;
using Dock.Model.Core;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.Services.Communication;
using UniversalHost.Symbols;
using UniversalHost.ViewModels.Windows;
using UniversalHost.Views.Windows;

namespace UniversalHost.ViewModels;

public partial class MainWindowViewModel : ReactiveObject, IDisposable
{
    public string AppVersion { get; } = AppInfo.Version;

    private readonly ObservableAsPropertyHelper<string> _windowTitle;
    public string WindowTitle => _windowTitle.Value;

    // CompositeDisposable 字段来管理所有订阅
    private readonly CompositeDisposable _disposables = [];
    //-------------菜单栏：文件 相关指令-------------
    //手动保存文件
    public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadSymbolsCommand { get; }
    //设置窗口
    public ReactiveCommand<int, Unit> OpenSettingWindowCommand { get; }
    private SettingWindow? _settingWindow;
    private LogViewerWindow? _logViewerWindow;
    public ReactiveCommand<Unit, Unit> StartMonitorCommand { get; }
    public ReactiveCommand<Unit, Unit> StopMonitorCommand { get; }
    //-------------在线升级----------------
    public ReactiveCommand<Unit, Unit> OpenIapWindowCommand { get; }

    public Interaction<string, string?> ShowRenameDialog { get; } = new Interaction<string, string?>();
    public Interaction<Documentation, Unit> ShowDocumentationWindow { get; } = new();

    [Reactive] private IRootDock _layout;
    [Reactive] private UniversalHost.DockFactory _factory;
    //主题图标
    [Reactive] private string _currentThemeIcon = "";
    public MainWindowViewModel()
    {
        GlobalStatus.Instance.WhenAnyValue(x => x.IsProjectOpened)
            .CombineLatest(
                ProjectSaveService.Instance.WhenAnyValue(x => x.ProjectFilePath),
                (opened, path) => $"UniversalHost  -  {(opened ? path : "未打开工程")}")
            .ToProperty(this, x => x.WindowTitle, out _windowTitle);

        if (Application.Current is { } app)
        {
            CurrentThemeIcon = app.ActualThemeVariant == ThemeVariant.Dark ? "🌙" : "🔆";

        }
        _factory = new DockFactory();

        _layout = _factory.CreateLayout()!;

        _factory.InitLayout(_layout);
        #region 文件选项卡

        SaveProjectCommand = ReactiveCommand.CreateFromTask((async () =>
        {
            try
            {
                if (GlobalStatus.Instance.IsProjectOpened == false)
                {
                    throw new Exception("未打开工程");
                }
                else if (ProjectSaveService.Instance.ProjectFilePath == "")
                {
                    throw new Exception("路径为空");
                }
                else
                {
                    await ProjectSaveService.SaveProjectAsync(ProjectSaveService.Instance.ProjectFilePath, Layout);
                    NotificationService.Show("保存成功", $"保存至: {ProjectSaveService.Instance.ProjectFilePath}", NotificationType.Success);
                }
            }
            catch (Exception ex)
            {
                NotificationService.Show("保存错误", $"{ex.Message}", NotificationType.Error);
            }
        }));
        ReloadSymbolsCommand = ReactiveCommand.CreateFromTask(FirmwareImportCoordinator.Instance.ReloadAsync);
        #endregion
        //开启设置窗口
        OpenSettingWindowCommand = ReactiveCommand.Create<int>((Action<int>)(tabIndex =>
        {
            if (_settingWindow != null)
            {
                SettingWindowViewModel.Instance.SelectedSettingTab = tabIndex;
                _settingWindow.Activate();
            }
            else
            {
                SettingWindowViewModel.Instance.SelectedSettingTab = tabIndex;
                _settingWindow = new SettingWindow
                {
                    DataContext = SettingWindowViewModel.Instance
                };
                WindowScreenSizing.FitAndCenter(_settingWindow,
                    (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow);
                _settingWindow.Closed += (_, _) => _settingWindow = null;
                _settingWindow.Show();
            }
        }));
        #region 监控
        StartMonitorCommand = ReactiveCommand.CreateFromTask((Func<Task>)(async () =>
        {
            var unknownSymbols = SymbolRuntimeService.MonitorSymbolRuntimesSource.Items
                                                     .Where(x => (x.Symbol.DataType == ScalarType.Unknown));

            if (unknownSymbols.Any())
            {
                NotificationService.Show($"以下 {unknownSymbols.Count()} 个变量类型为 Unknown",
                            $"{String.Join(", ", unknownSymbols.Select(x => x.Symbol.Name))}",
                            NotificationType.Warning);
                return;
            }

            try
            {
                foreach (var item in SymbolRuntimeService.MonitorSymbolRuntimesSource.Items)
                {
                    item.ClearHistory();
                }
                await XcpService.Client!.StartDaq(SymbolRuntimeService.MonitorSymbolRuntimesSource.Items);
                LogService.ForModule(LogModule.Monitoring).Information("监控开始");
                NotificationService.Show("监控开始", "", NotificationType.Info);
            }
            catch (Exception ex)
            {
                NotificationService.Show("监控开始异常", ex.Message, NotificationType.Warning);
                LogService.ForModule(LogModule.Monitoring).Warning(ex, "监控开始异常,{Reason}", ex.Message);
            }
        }));
        //停止监控命令
        StopMonitorCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            try
            {
                await XcpService.Client!.StopDaq();
            }
            catch (Exception ex)
            {
                NotificationService.Show("监控停止异常", ex.Message, NotificationType.Warning);
            }
        });
        #endregion
        //开启IAP窗口            
        OpenIapWindowCommand = ReactiveCommand.Create(() =>
        {
            Factory.ShowOrCreateTool(Layout, DockableRegistry.IapToolId);
        });
    }
    [ReactiveCommand]
    private void ToggleAllCurveYAxisAutoScale()
    {
        var curveWindows = DockableRegistry.CurveMonitorDocuments.Values;
        if (curveWindows.Count == 0) return;

        // 仅在全部开启时统一关闭；状态不一致时统一开启。
        var enableAutoScale = !curveWindows.All(window => window.CurvesLayout.IsYAxisAutoScaleEnabled);
        foreach (var window in curveWindows)
        {
            window.CurvesLayout.IsYAxisAutoScaleEnabled = enableAutoScale;
        }
    }
    #region 文件指令
    [ReactiveCommand]
    private void OpenLogViewer()
    {
        if (_logViewerWindow != null)
        {
            if (_logViewerWindow.WindowState == WindowState.Minimized)
                _logViewerWindow.WindowState = WindowState.Normal;
            _logViewerWindow.Show();
            _logViewerWindow.Activate();
            return;
        }

        string? folder = GlobalStatus.Instance.IsProjectOpened &&
            !string.IsNullOrWhiteSpace(ProjectSaveService.Instance.ProjectFilePath)
                ? Path.GetDirectoryName(Path.GetFullPath(ProjectSaveService.Instance.ProjectFilePath)) : null;
        var viewModel = new LogViewerWindowViewModel();
        var window = new LogViewerWindow(viewModel);
        WindowScreenSizing.FitAndCenter(window,
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow);
        _logViewerWindow = window;
        window.Closed += (_, _) => _logViewerWindow = null;
        // 使用独立窗口，让主窗口和日志窗口随焦点正常切换前后层级。
        window.Show();
        viewModel.Initialize(folder);
    }

    [ReactiveCommand]
    private async Task NewProjectFileAsync(Window window)
    {
        try
        {
            var result = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "新建工程",
                SuggestedFileName = "NewProject",
                FileTypeChoices = new List<FilePickerFileType>
                    {
                        new("工程文件") { Patterns = ["*.uhprj"] },
                        new("所有文件") { Patterns = ["*"] }
                    }
            });
            if (result != null)
            {
                _disposables.Clear();
                //创建默认设置
                ResetDockLayout();
                ProjectSaveService.Update(result.Path.LocalPath, new ProjectSettings());

                await ProjectSaveService.SaveProjectAsync(result.Path.LocalPath, Layout);

                GlobalStatus.Instance.IsProjectOpened = true;
                LogService.ForModule(LogModule.Project).Information("新建工程成功，路径: {ProjectPath}", result.Path.LocalPath);
                NotificationService.Show("新建工程成功", $"路径: {result.Path.AbsolutePath}", NotificationType.Success);
            }
        }
        catch (Exception ex)
        {
            LogService.ForModule(LogModule.Project).Error(ex, "新建错误，{Reason}", ex.Message);
            NotificationService.Show("新建错误", ex.Message, NotificationType.Error);
        }
    }
    [ReactiveCommand]
    private async Task OpenProjectFileAsync(Window window)
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择工程文件",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("工程文件") { Patterns = ["*.uhprj"] },
                new("所有文件") { Patterns = ["*"] }
            }
        });

        if (files is { Count: > 0 })
        {
            var path = files[0].Path.LocalPath;
            try
            {
                var conversion = await ProjectConversionService.EnsureCurrentAsync(path);
                if (conversion is { RemovedSymbols.Count: > 0 })
                    NotificationService.Show($"工程转换完成：已删除 {conversion.RemovedSymbols.Count} 个无法匹配的变量",
                        string.Join(Environment.NewLine, conversion.RemovedSymbols.Select(
                            item => $"{item.Group}: {item.Name} ({item.Source})，{item.Reason}")), NotificationType.Warning);
                var layout = ProjectSaveService.LoadProject(path);
                _disposables.Clear();
                if (layout == null)
                {
                    layout = Factory.CreateLayout();
                    NotificationService.Show("布局读取错误", "已创建默认布局", NotificationType.Warning);
                }
                Layout = layout;
                Factory.InitLayout(Layout);

                // 绑定监控符号运行时集合

                GlobalStatus.Instance.IsProjectOpened = true;
                LogService.ForModule(LogModule.Project).Information("已加载文件: {ProjectPath}", path);
                NotificationService.Show("读取成功", conversion?.BackupPath == null
                    ? $"已加载文件: {path}"
                    : $"已转换并加载文件: {path}\n原工程备份: {conversion.BackupPath}", NotificationType.Success);
            }
            catch (Exception ex)
            {
                LogService.ForModule(LogModule.Project).Error(ex, "读取错误，{Reason}", ex.Message);
                NotificationService.Show("读取错误", ex.Message, NotificationType.Error);
            }
        }
    }
    [ReactiveCommand]
    private async Task SaveAsProjectFileAsync(Window window)
    {
        try
        {
            var result = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "另存为工程",
                SuggestedFileName = "SaveAsProject",
                FileTypeChoices = new List<FilePickerFileType>
                    {
                        new("工程文件") { Patterns = ["*.uhprj"] },
                        new("所有文件") { Patterns = ["*"] }
                    }
            });
            if (result != null)
            {
                await ProjectSaveService.SaveProjectAsync(result.Path.LocalPath, Layout);

                LogService.ForModule(LogModule.Project).Information("成功另存为至 {ProjectPath}", result.Path.LocalPath);
                NotificationService.Show("另存为成功", $"路径: {result.Path.AbsolutePath}", NotificationType.Success);
            }
            else
            {
                return;
            }
        }
        catch (Exception ex)
        {
            LogService.ForModule(LogModule.Project).Error(ex, "另存为错误, {Reason}", ex.Message);
            NotificationService.Show("另存为错误", ex.Message, NotificationType.Error);
        }
    }
    #endregion
    #region 设备
    [ReactiveCommand]
    private async Task ConnectDevice()
    {
        try
        {
            await XcpService.ConnectAsync();
            // 新连接清空标定值，以免迷惑用户。
            foreach (var runtime in SymbolRuntimeService.CalibrateSymbolRuntimesSource.Items)
            {
                runtime.ClearHistory();
                runtime.ValueString = string.Empty;
            }
            LogService.ForModule(LogModule.Xcp).Information("设备连接成功");
            NotificationService.Show("设备已连接", "", NotificationType.Success);
        }
        catch (Exception ex) when (ex is TimeoutException || ex is TaskCanceledException)
        {
            LogService.ForModule(LogModule.Xcp).Error(ex, "设备连接超时");
            NotificationService.Show("设备连接错误", "连接超时", NotificationType.Error);
        }
        catch (Exception ex)
        {
            LogService.ForModule(LogModule.Xcp).Error(ex, "设备连接错误，{Reason}", ex.Message);
            NotificationService.Show("设备连接错误", ex.Message, NotificationType.Error);
        }
    }
    [ReactiveCommand]
    private async Task DisconnectDevice()
    {
        if (XcpService.Client == null)
        {
            return;
        }
        try
        {
            await XcpService.DisconnectAsync();
            NotificationService.Show("设备已断开", "", NotificationType.Success);
            LogService.ForModule(LogModule.Xcp).Information("设备已断开");
        }
        catch (Exception ex)
        {
            LogService.ForModule(LogModule.Xcp).Error(ex, "设备断开连接错误，{Reason}", ex.Message);
            NotificationService.Show("设备断开连接错误", ex.Message, NotificationType.Error);
        }
    }

    [ReactiveCommand]
    private void ShowDeviceInfomation()
    {
        if (XcpService.Client == null) return;

        List<string> info = [];

        string endian = XcpService.Client.DeviceStatus.ConnectRes.IsLittleEndian ? "端序：小端序" : "端序：大端序";
        info.Add(endian);
        info.Add(XcpService.Client.DeviceStatus.ConnectRes.Granularity.ToString());

        NotificationService.Show("设备信息", String.Join(Environment.NewLine, info));
    }

    #endregion
    [ReactiveCommand]
    private async Task SaveMonitorData()
    {
        try
        {
            var path = await Task.Run(() => DataSaveService.SaveToCsv(SymbolRuntimeService.MonitorSymbolRuntimesSource.Items, "data"));
            NotificationService.Show("保存成功", path, NotificationType.Success);
            LogService.ForModule(LogModule.Monitoring).Information("监控数据保存至 {Path}", path);
        }
        catch (Exception ex)
        {
            NotificationService.Show("保存失败", ex.Message, NotificationType.Warning);
        }
    }

    [ReactiveCommand]
    private Task OpenShortcutsAsync() => OpenDocumentationAsync("快捷键");

    [ReactiveCommand]
    private Task OpenUserManualAsync() => OpenDocumentationAsync("使用说明");

    [ReactiveCommand]
    private void OpenGitHubRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/Rivfflot/UniversalHost",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            NotificationService.Show("打开 GitHub 失败", ex.Message, NotificationType.Warning);
        }
    }

    private async Task OpenDocumentationAsync(string title)
    {
        try
        {
            var document = await DocumentationService.LoadAsync(title);
            await ShowDocumentationWindow.Handle(document);
        }
        catch (Exception ex)
        {
            LogService.ForModule(LogModule.Application).Error(ex, "打开{Title}文档失败", title);
            NotificationService.Show($"打开{title}失败", ex.Message, NotificationType.Error);
        }
    }

    [ReactiveCommand]
    private void ResetDockLayout()
    {
        DockableRegistry.ClearAllDocuments();
        Layout = Factory.CreateLayout();
        Factory.InitLayout(Layout!);
    }
    #region 新建各种窗口
    private void AddPanelToTab(IDockable panel)
    {
        var tab = Factory.FirstOrDefaultActiveTab(Layout);
        if (tab == null) return;
        Factory.AddDockable(tab, panel);
        Factory.SetActiveDockable(panel);
    }
    [ReactiveCommand]
    private void AddGridMonitorPanel()
    {
        var panel = Factory.GetOrCreateGridMonitorDocument(Guid.NewGuid().ToString());
        AddPanelToTab(panel);
    }

    [ReactiveCommand]
    private void AddCurveMonitorPanel()
    {
        var panel = Factory.GetOrCreateCurveMonitorDocument(Guid.NewGuid().ToString());
        AddPanelToTab(panel);
    }
    [ReactiveCommand]
    private void AddBitsMonitorPanel()
    {
        var panel = Factory.GetOrCreateBitsMonitorDocument(Guid.NewGuid().ToString());
        AddPanelToTab(panel);
    }
    [ReactiveCommand]
    private void AddGridCalibratePanel()
    {
        var panel = Factory.GetOrCreateGridCalibrateDocument(Guid.NewGuid().ToString());
        AddPanelToTab(panel);
    }
    [ReactiveCommand]
    private void AddCommandPanel()
    {
        var panel = Factory.GetOrCreateUserCommandDocument(Guid.NewGuid().ToString());
        AddPanelToTab(panel);
    }

    [ReactiveCommand]
    private void CreateNewTab()
    {
        Factory.CreateNewTab(Layout);
    }
    [ReactiveCommand]
    private async Task RenameTab(IDock tab)
    {
        if (tab == null) return;

        string? newName = await ShowRenameDialog.Handle(tab.Title);

        if (!string.IsNullOrWhiteSpace(newName))
        {
            tab.Title = newName;
        }
    }
    #endregion
    #region CAL command
    [ReactiveCommand]
    private async Task DownloadValuesAsync()
    {
        var items = SymbolRuntimeService.CalibrateSymbolRuntimesSource.Items
                      .Where(item => !string.IsNullOrEmpty(item.ValueString));
        if (!items.Any())
        {
            NotificationService.Show("下载标定变量错误", "可下载变量数量为 0", NotificationType.Warning);
            return;
        }
        try
        {
            await XcpService.Client!.DownloadSymbols(items);
            NotificationService.Show("标定成功", $"共下载 {items.Count()} 个变量", NotificationType.Success);
        }
        catch (Exception ex)
        {
            NotificationService.Show("标定错误", ex.Message, NotificationType.Error);
        }

    }
    [ReactiveCommand]
    private async Task UploadValuesAsync()
    {
        var unknownItems = SymbolRuntimeService.CalibrateSymbolRuntimesSource.Items.Where(item =>
                                                item.Symbol.DataType == ScalarType.Unknown).ToList();

        if (unknownItems.Count > 0)
        {
            var errorDetails = string.Join(", ", unknownItems.Select(i => $"{i.Symbol.Name}({i.Symbol.Alias})"));
            NotificationService.Show("上传标定变量错误", $"{unknownItems.Count}个变量: {errorDetails} 类型为 Unkown", NotificationType.Warning);
            return;
        }
        try
        {
            foreach (var item in SymbolRuntimeService.CalibrateSymbolRuntimesSource.Items)
            {
                await XcpService.Client!.UploadSymbol(item);
            }
            NotificationService.Show("上传标定变量成功", $"共上传{SymbolRuntimeService.CalibrateSymbolRuntimesSource.Count}个变量", NotificationType.Success);
        }
        catch (Exception ex)
        {
            NotificationService.Show("上传标定变量错误", ex.Message, NotificationType.Error);
        }
    }

    [ReactiveCommand]
    private async Task SaveCalibrateParameters()
    {
        try
        {
            string path = await CalibrateParametersSaveService.SaveAsync();
            NotificationService.Show("参数已保存", path, NotificationType.Success);
            LogService.ForModule(LogModule.Calibration).Information("参数已保存, {Path}", path);
        }
        catch (Exception ex)
        {
            NotificationService.Show("参数保存失败", ex.Message, NotificationType.Warning);
        }
    }
    [ReactiveCommand]
    private async Task LoadCalibrateParameters(Window window)
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择参数文件",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("参数文件") { Patterns = ["*.json"] },
                new("所有文件") { Patterns = ["*"] }
            }
        });

        if (files is { Count: > 0 })
        {
            var path = files[0].Path.LocalPath;
            try
            {
                await CalibrateParametersSaveService.LoadAsync(path);
                NotificationService.Show("参数已读取", path, NotificationType.Success);
                LogService.ForModule(LogModule.Calibration).Information("标定参数读取, {Path}", path);
            }
            catch (Exception ex)
            {
                NotificationService.Show("参数读取失败", ex.Message, NotificationType.Warning);
            }
        }
    }
    #endregion
    [ReactiveCommand]
    private async Task OpenFaultRecordTool()
    {
        Factory.ShowOrCreateTool(Layout, DockableRegistry.FaultRecordToolId);
    }

#if DEBUG
    private CancellationTokenSource? randomCts;

    [ReactiveCommand]
    private void ToggleRandomData()
    {
        if (randomCts is null || randomCts.IsCancellationRequested)
        {
            randomCts?.Dispose();
            randomCts = new CancellationTokenSource();
            var token = randomCts.Token;
            GlobalStatus.Instance.IsConnected = true;
            GlobalStatus.Instance.IsMonitoring = true;
            Task.Run(() =>
            {
                long intervalTicks = Stopwatch.Frequency / 10000L;
                long nextTriggerTick = Stopwatch.GetTimestamp();

                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                while (!token.IsCancellationRequested)
                {
                    long currentTick = Stopwatch.GetTimestamp();

                    if (currentTick < nextTriggerTick)
                    {
                        Thread.SpinWait(10);
                        continue;
                    }
                    nextTriggerTick += intervalTicks;

                    // 生成随机数用来测试。
                    foreach (var item in SymbolRuntimeService.MonitorSymbolRuntimesSource.Items)
                    {
                        item.AddRandomData();
                    }
                }
            }, token);
        }
        else
        {
            randomCts.Cancel();
            GlobalStatus.Instance.IsConnected = false;
            GlobalStatus.Instance.IsMonitoring = false;
        }
    }
#endif

    [ReactiveCommand]
    private void ToggleTheme()
    {
        if (Application.Current is { } app)
        {
            if (app.ActualThemeVariant == ThemeVariant.Dark)
            {
                CurrentThemeIcon = "🔆";//🔆🌙
                app.RequestedThemeVariant = ThemeVariant.Light;
            }
            else
            {
                CurrentThemeIcon = "🌙";
                app.RequestedThemeVariant = ThemeVariant.Dark;
            }
        }
    }



    public void Dispose()
    {
#if DEBUG
        randomCts?.Cancel();
        randomCts?.Dispose();
#endif
        _windowTitle.Dispose();
        _disposables.Dispose();
    }
}
