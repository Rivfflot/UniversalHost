using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Dock.Model.Controls;
using Dock.Model.Core;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.Services.Communication;
using UniversalHost.ViewModels.Views;
using UniversalHost.Views.Views;

namespace UniversalHost.ViewModels;

public partial class MainWindowViewModel : ReactiveObject, IDisposable
{
    // CompositeDisposable 字段来管理所有订阅
    private readonly CompositeDisposable _disposables = [];
    //-------------菜单栏：文件 相关指令-------------
    //手动保存文件
    public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadSymbolsCommand { get; }
    //设置窗口
    public ReactiveCommand<int, Unit> OpenSettingWindowCommand { get; }
    private SettingWindow? _SettingWindow;
    public ReactiveCommand<Unit, Unit> StartMonitorCommand { get; }
    public ReactiveCommand<Unit, Unit> StopMonitorCommand { get; }
    //-------------在线升级----------------
    public ReactiveCommand<Unit, Unit> OpenIapWindowCommand { get; }

    public Interaction<string, string?> ShowRenameDialog { get; } = new Interaction<string, string?>();

    [Reactive] private IRootDock _layout;
    [Reactive] private UniversalHost.DockFactory _factory;
    //主题图标
    [Reactive] private string _currentThemeIcon = "";
    public MainWindowViewModel()
    {
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
        ReloadSymbolsCommand = ReactiveCommand.Create((Action)(() =>
        {
            try
            {
                var removedSymbols = ProjectSaveService.Instance.Settings.ReloadAllSymbols();
                Serilog.Log.Information($"符号表重新加载，从{ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Count}个文件中加载了{ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Count}个变量");
                NotificationService.Show("符号表重新加载成功", $"从{ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Count}个文件中加载了{ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Count}个变量", NotificationType.Success);
                if (removedSymbols.Count > 0)
                {
                    List<string> removedSymbolsMessage = [];
                    foreach (var symbol in removedSymbols)
                    {
                        removedSymbolsMessage.Add($"{symbol.SourceFileName} : {symbol.Name}");
                    }
                    Serilog.Log.Information($"删除{removedSymbols.Count}个不存在于新文件中的监控/标定变量");
                    NotificationService.Show($"删除{removedSymbols.Count}个不存在于新文件中的监控/标定变量", string.Join(Environment.NewLine, removedSymbolsMessage), NotificationType.Warning);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error($"重新加载错误，{ex.Message}");
                NotificationService.Show("重新加载错误", $"{ex.Message}", NotificationType.Error);
            }
        }));
        #endregion
        //开启设置窗口
        OpenSettingWindowCommand = ReactiveCommand.Create<int>((Action<int>)(tabIndex =>
        {
            if (_SettingWindow != null)
            {
                SettingWindowViewModel.Instance.SelectedSettingTab = tabIndex;
                _SettingWindow.Activate();
            }
            else
            {
                SettingWindowViewModel.Instance.SelectedSettingTab = tabIndex;
                _SettingWindow = new SettingWindow
                {
                    DataContext = SettingWindowViewModel.Instance
                };
                _SettingWindow.Closed += (_, _) => _SettingWindow = null;
                _SettingWindow.Show();
            }
        }));
        #region 监控
        StartMonitorCommand = ReactiveCommand.CreateFromTask((Func<Task>)(async () =>
        {
            var unknownSymbols = SymbolRuntimeService.MonitorSymbolRuntimesSource.Items
                                                     .Where(x => (x.Symbol.DataType == SymbolDataType.Unknown));

            if (unknownSymbols.Any())
            {
                NotificationService.Show($"以下 {unknownSymbols.Count()} 个变量类型为 Unknown",
                            $"{String.Join(", ", unknownSymbols.Select(x => x.Symbol.Name))}",
                            NotificationType.Warning);
                return;
            }

            try
            {
                await XcpService.Client!.StartDaq(SymbolRuntimeService.MonitorSymbolRuntimesSource.Items);
                Serilog.Log.Information("监控开始");
                NotificationService.Show("监控开始", "", NotificationType.Info);
            }
            catch (Exception ex)
            {
                NotificationService.Show("监控开始异常", ex.Message, NotificationType.Warning);
                Serilog.Log.Warning($"监控开始异常,{ex.Message}");
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
            var tool = Factory.FindDockable(Layout, x => x.Id == "IapTool");

            if (tool == null)
            {
                if (Layout.HiddenDockables != null)
                {
                    foreach (var item in Layout.HiddenDockables)
                    {
                        if (item.Id == "IapTool")
                        {
                            tool = item;
                            break;
                        }
                    }
                }
            }

            var activeTab = Factory.FirstOrDefaultActiveTab(Layout);
            if (activeTab == null) return;

            if (tool == null)
            {
                tool = Factory.CreateIapTool();
                Factory.AddDockable(activeTab, tool);
                Factory.SetActiveDockable(tool);
            }
            else
            {
                if (tool.Owner is IDock parentDock)
                {
                    var visibleList = parentDock.VisibleDockables;

                    if (visibleList!.Contains(tool))
                    {
                        Factory.HideDockable(tool);

                    }
                    else
                    {
                        Factory.RestoreDockable(tool);
                        Factory.MoveDockable((IDock)tool.Owner, activeTab, tool, null);
                        Factory.SetActiveDockable(tool);
                    }
                }
            }
        });
    }
    #region 文件指令
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
                ProjectSaveService.Instance.Settings = new ProjectSettings();
                ResetDockLayout();

                await ProjectSaveService.SaveProjectAsync(result.Path.AbsolutePath, Layout);

                ProjectSaveService.Update(result.Path.AbsolutePath);

                GlobalStatus.Instance.IsProjectOpened = true;
                Serilog.Log.Information($"新建工程成功，路径: {result.Path.AbsolutePath}");
                NotificationService.Show("新建工程成功", $"路径: {result.Path.AbsolutePath}", NotificationType.Success);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error($"新建错误，{ex.Message}");
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
                _disposables.Clear();
                DockableRegistry.GridMonitorDocuments.Clear();
                var layout = ProjectSaveService.LoadProject(path);
                if (layout == null)
                {
                    layout = Factory.CreateLayout();
                    NotificationService.Show("布局读取错误", "已创建默认布局", NotificationType.Warning);
                }
                Layout = layout;
                Factory.InitLayout(Layout);

                // 绑定监控符号运行时集合

                GlobalStatus.Instance.IsProjectOpened = true;
                Serilog.Log.Information($"已加载文件: {path}");
                NotificationService.Show("读取成功", $"已加载文件: {path}", NotificationType.Success);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error($"读取错误，{ex.Message}");
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
                await ProjectSaveService.SaveProjectAsync(result.Path.AbsolutePath, Layout);

                Serilog.Log.Information($"成功另存为至 {result.Path.AbsolutePath}");
                NotificationService.Show("另存为成功", $"路径: {result.Path.AbsolutePath}", NotificationType.Success);
            }
            else
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error($"另存为错误, {ex.Message}");
            NotificationService.Show("另存为错误", ex.Message, NotificationType.Error);
        }
    }
    #endregion
    #region 设备
    [ReactiveCommand]
    private async Task ConnectDevice()
    {
        await XcpService.CreateClientAsync();
        try
        {
            await XcpService.Client!.ConnectAsync();
            Serilog.Log.Information($"设备连接成功");
            NotificationService.Show("设备已连接", "", NotificationType.Success);
        }
        catch (TaskCanceledException)
        {
            Serilog.Log.Error($"设备连接超时");
            NotificationService.Show("设备连接错误", "连接超时", NotificationType.Error);
            await XcpService.DisposeClientAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error($"设备连接错误，{ex.Message}");
            NotificationService.Show("设备连接错误", ex.Message, NotificationType.Error);
            await XcpService.DisposeClientAsync();
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
            await XcpService.Client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error($"设备断开连接错误，{ex.Message}");
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
            Serilog.Log.Information($"监控数据保存至 {path}");
        }
        catch (Exception ex)
        {
            NotificationService.Show("保存失败", ex.Message, NotificationType.Warning);
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
                      .Where(item => !string.IsNullOrEmpty(item.ValueString) && item.Symbol.DataType != SymbolDataType.Unknown);
        if (!items.Any())
        {
            NotificationService.Show("下载标定变量错误", "可下载变量数量为 0", NotificationType.Warning);
            return;
        }
        try
        {
            foreach (var item in items)
            {
                await XcpService.Client!.DownloadSymbol(item);
            }
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
                                                item.Symbol.DataType == SymbolDataType.Unknown).ToList();

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
            Serilog.Log.Information($"参数已保存, {path}");
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
                Serilog.Log.Information($"标定参数读取, {path}");
            }
            catch (Exception ex)
            {
                NotificationService.Show("参数读取失败", ex.Message, NotificationType.Warning);
            }
        }
    }
    #endregion
    [ReactiveCommand]
    private async Task UploadFaultRecorderData()
    {
        var recordProgress = new Progress<double>();
        try
        {
            var recordStatus = ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Items.First(s => s.Name == "fault_recorder_state");
            var recordData = ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Items.First(s => s.Name == "recorder_data");
            FaultRecordService faultRecordService = new FaultRecordService(recordStatus, recordData, recordProgress);
            var path = await Task.Run(() => faultRecordService.RunFaultRecordSequence());
            NotificationService.Show("故障数据上传成功", $"保存至 {path}", NotificationType.Success);
            Serilog.Log.Information($"故障录波数据上传成功，保存至 {path}");
        }
        catch (Exception ex)
        {
            NotificationService.Show("故障数据上传失败", ex.Message, NotificationType.Warning);
            Serilog.Log.Warning($"故障数据上传失败 : {ex.Message}");
        }
    }

    private CancellationTokenSource randomCts = new();

    [ReactiveCommand]
    private void ToggleRandomData()
    {
        if (randomCts.IsCancellationRequested)
        {
            randomCts = new CancellationTokenSource();
            GlobalStatus.Instance.IsConnected = true;
            GlobalStatus.Instance.IsMonitoring = true;
            Task.Run(() =>
            {
                long intervalTicks = Stopwatch.Frequency / 10000L;
                long nextTriggerTick = Stopwatch.GetTimestamp();

                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                while (!randomCts.Token.IsCancellationRequested)
                {
                    long currentTick = Stopwatch.GetTimestamp();

                    if (currentTick < nextTriggerTick)
                    {
                        Thread.SpinWait(10);
                        continue;
                    }
                    nextTriggerTick += intervalTicks;

                    //TODO 生成随机数用来测试 调试完成后删除 252746382
                    foreach (var item in SymbolRuntimeService.MonitorSymbolRuntimesSource.Items)
                    {
                        item.AddRandomData();
                    }
                }
            }, randomCts.Token);
        }
        else
        {
            randomCts.Cancel();
            GlobalStatus.Instance.IsConnected = false;
            GlobalStatus.Instance.IsMonitoring = false;
        }
    }

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
        _disposables.Dispose();
    }
}
