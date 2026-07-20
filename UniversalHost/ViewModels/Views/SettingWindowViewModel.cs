using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using DynamicData;
using DynamicData.Binding;
using ELFSharp.ELF;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Linq.Expressions;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Services;


namespace UniversalHost.ViewModels.Views;

public partial class SettingWindowViewModel : ReactiveObject
{
    public static SettingWindowViewModel Instance { get; private set; } = new SettingWindowViewModel();

    [Reactive] private int _selectedSettingTab = 0;
    public ProjectSaveService ProjectSaveServiceInstance => ProjectSaveService.Instance;//用于UI绑定
    public GlobalStatus GlobalStatusInstance => GlobalStatus.Instance;//用于UI绑定
                                                                      // 设备设置
    public static IEnumerable<CommunicationMode> AvailableCommunicationModes => Enum.GetValues<CommunicationMode>();
    [Reactive] private byte? _deviceIDTemp;
    private readonly ReadOnlyObservableCollection<string> _symbolFilePaths;
    public ReadOnlyObservableCollection<string> SymbolFilePaths => _symbolFilePaths;
    public ReactiveCommand<Unit, Unit> ReloadSymbolFileCommand { get; }
    public ReactiveCommand<Window, Unit> SelectSymbolFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearSymbolFilePathsCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearSymbolsCommand { get; }
    public ReactiveCommand<string, Unit> ShowSymbolFileInformationCommand { get; }
    public ReactiveCommand<string, Unit> RemoveSymbolFileCommand { get; }
    public ReactiveCommand<(Window window, string path), Unit> CopySymbolFilePathCommand { get; }
    public ReactiveCommand<string, Unit> OpenSymbolFileFolderCommand { get; }

    // 符号表过滤
    [Reactive] private string _symbolSearchText = string.Empty;

    // 使用 DynamicData 的过滤功能
    private readonly ReadOnlyObservableCollection<SymbolInfo> _filteredSymbols;
    public ReadOnlyObservableCollection<SymbolInfo> FilteredSymbols => _filteredSymbols;

    // 串口设置
    [Reactive] private int? _baudRateTemp;
    [Reactive] private UInt16? _serialTimeoutTemp;
    [Reactive] private UInt16? _serialRetryTimesTemp;
    public ReactiveCommand<Unit, Unit> RefreshPortsCommand { get; }

    // 串口和IP的SourceList，用于更新数据
    private readonly SourceList<string> _serialPortSource;
    private readonly SourceList<string> _localIPSource;

    private readonly ReadOnlyObservableCollection<string> _availableSerialPorts;
    public ReadOnlyObservableCollection<string> AvailableSerialPorts => _availableSerialPorts;
    public static IEnumerable<System.IO.Ports.Parity> AvailableParities => Enum.GetValues<System.IO.Ports.Parity>();
    public static IEnumerable<int> AvailableDataBits => [7, 8, 9];
    public static IEnumerable<System.IO.Ports.StopBits> AvailableStopBits => Enum.GetValues<System.IO.Ports.StopBits>();

    // UDP设置
    private readonly ReadOnlyObservableCollection<string> _availableIPv4Addresses;
    public ReadOnlyObservableCollection<string> AvailableIPv4Addresses => _availableIPv4Addresses;
    public ReactiveCommand<Unit, Unit> RefreshIPCommand { get; }
    [Reactive] private string? _remoteAddressTemp;//IP
    [Reactive] private string? _remoteAddressError;//IP错误提示
    [Reactive] private int? _iapLocalPortTemp;
    [Reactive] private int? _iapRemotePortTemp;
    [Reactive] private int? _xcpLocalPortTemp;
    [Reactive] private int? _xcpRemotePortTemp;
    [Reactive] private int? _udpTimeoutTemp = 10;
    [Reactive] private UInt16? _udpRetryTimesTemp = 10;

    // 监控
    [Reactive] private int? _maxSaveLenTemp;
    public ReactiveCommand<Unit, Unit> ClearMonitorSymbolsCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveDuplicateMonitorSymbolsCommand { get; }
    public ReactiveCommand<UserSymbolInfo, Unit> RemoveMonitorSymbolCommand { get; }
    public static IEnumerable<Models.SymbolDataType> AvailableDataTypes => Enum.GetValues<Models.SymbolDataType>();
    // 监控符号表
    [Reactive] private string _monitorSymbolSearchText = string.Empty;

    private readonly ReadOnlyObservableCollection<UserSymbolInfo> _filteredMonitorSymbols;
    public ReadOnlyObservableCollection<UserSymbolInfo> FilteredMonitorSymbols => _filteredMonitorSymbols;
    [Reactive] private bool? _selectAllMonitorSymbolsBox;
    //标定
    [Reactive] private string _calibrateSymbolSearchText = string.Empty;
    private readonly ReadOnlyObservableCollection<UserSymbolInfo> _filteredCalibrateSymbols;
    public ReadOnlyObservableCollection<UserSymbolInfo> FilteredCalibrateSymbols => _filteredCalibrateSymbols;

    //指令
    [Reactive] private string _userCommandSearchText = string.Empty;
    private readonly ReadOnlyObservableCollection<UserCommand> _filteredUserCommands;
    public ReadOnlyObservableCollection<UserCommand> FilteredUserCommands => _filteredUserCommands;

    // IAP设置
    [Reactive] private string? _iapFilePathTemp;
    [Reactive] private string? _iapFilePathError;
    [Reactive] private UInt16? _iapBytesPerFrameTemp;
    [Reactive] private UInt16? _waitForHandShakeTimeoutSecondsTemp;
    [Reactive] private UInt16? _waitForWriteTimeoutSecondsTemp;
    [Reactive] private UInt16? _waitForCheckTimeoutSecondsTemp;
    [Reactive] private UInt16? _waitForRebootStartTimeoutSecondsTemp;
    [Reactive] private UInt16? _waitForRebootCompleteTimeoutSecondsTemp;

    //日志设置
    public static IEnumerable<Serilog.Events.LogEventLevel> AvaliableLogEventLevel => Enum.GetValues<Serilog.Events.LogEventLevel>();


    public SettingWindowViewModel()
    {

        //符号表路径
        ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Connect()
                                                .Bind(out _symbolFilePaths)
                                                .RefCount()
                                                .Subscribe();
        // 符号过滤逻辑
        var symbolFilter = this.WhenValueChanged(x => x.SymbolSearchText)
                              .Throttle(TimeSpan.FromMilliseconds(100))
                              .Select(SymbolInfo.CreateSymbolInfoSearchFilter);

        ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Connect()
                    .Filter(symbolFilter)
                    .ObserveOn(AvaloniaScheduler.Instance)
                    .Bind(out _filteredSymbols)
                    .Subscribe();
        // 监控符号过滤逻辑
        var monitorSymbolFilter = this.WhenValueChanged(x => x.MonitorSymbolSearchText)
                                     .Throttle(TimeSpan.FromMilliseconds(100))
                                     .Select(UserSymbolInfo.CreateUserSymbolInfoSearchFilter);

        ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Connect()
                           .AutoRefresh(symbol => symbol.Alias, changeSetBuffer: TimeSpan.FromSeconds(1))
                           .AutoRefresh(symbol => symbol.Description, changeSetBuffer: TimeSpan.FromSeconds(1))
                           .Filter(monitorSymbolFilter)
                           .ObserveOn(AvaloniaScheduler.Instance)
                           .Bind(out _filteredMonitorSymbols)
                           .Subscribe();

        // 在 ViewModel 初始化时，订阅列表变化以自动更新“全选框”的状态
        ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Connect()
                            .AutoRefresh(symbol => symbol.IsMonitored, changeSetBuffer: TimeSpan.FromMilliseconds(100))
                            .QueryWhenChanged(cache =>
                            {
                                var totalCount = cache.Count;
                                if (totalCount == 0) return false;

                                // 计算当前有多少项被勾选了
                                var monitoredCount = cache.Items.Count(x => x.IsMonitored);

                                if (monitoredCount == 0)
                                {
                                    return false; // 全不选
                                }
                                else if (monitoredCount == totalCount)
                                {
                                    return true; // 全选
                                }
                                else
                                {
                                    return (bool?)null; // 部分选择（第三态）
                                }
                            })
                            .ObserveOn(AvaloniaScheduler.Instance)
                            .Subscribe(state =>
                            {
                                SelectAllMonitorSymbolsBox = state;
                            });
        //CAL
        var clibrateSymbolFilter = this.WhenValueChanged(x => x.CalibrateSymbolSearchText)
                                     .Throttle(TimeSpan.FromMilliseconds(100))
                                     .Select(UserSymbolInfo.CreateUserSymbolInfoSearchFilter);

        ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols.Connect()
                          .AutoRefresh(symbol => symbol.Alias, changeSetBuffer: TimeSpan.FromSeconds(1))
                          .AutoRefresh(symbol => symbol.Description, changeSetBuffer: TimeSpan.FromSeconds(1))
                          .Filter(clibrateSymbolFilter)
                          .ObserveOn(AvaloniaScheduler.Instance)
                          .Bind(out _filteredCalibrateSymbols)
                          .Subscribe();

        // 初始化串口列表
        _serialPortSource = new SourceList<string>();
        _serialPortSource.AddRange(SerialPort.GetPortNames());
        _serialPortSource.Connect()
                       .ObserveOn(AvaloniaScheduler.Instance)
                       .Bind(out _availableSerialPorts)
                       .Subscribe();

        // 初始化本地IP列表
        _localIPSource = new SourceList<string>();
        _localIPSource.AddRange(GetLocalIPv4Addresses());
        _localIPSource.Connect()
                    .ObserveOn(AvaloniaScheduler.Instance)
                    .Bind(out _availableIPv4Addresses)
                    .Subscribe();

        ReloadSymbolFileCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                ProjectSaveService.Instance.Settings.ReloadAllSymbols();
                NotificationService.Show("符号表重新加载成功", $"从{ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Count}个文件中加载了{ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Count}个变量", NotificationType.Success);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error($"读取错误{ex.Message}");
                NotificationService.Show("读取错误", ex.Message, NotificationType.Error);
            }
        });
        SelectSymbolFileCommand = ReactiveCommand.CreateFromTask<Window>(SelectSymbolFileAsync);
        ClearSymbolFilePathsCommand = ReactiveCommand.Create(() =>
        {
            var count = ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Count;
            ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Clear();
            ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Clear();
            SymbolRuntimeService.MonitorSymbolRuntimesSource.Clear();
            NotificationService.Show("符号文件列表已清空", $"删除了{count}个路径", NotificationType.Success);
        });
        ClearSymbolsCommand = ReactiveCommand.Create(() =>
        {
            var count = ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Count;
            ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Clear();
            SymbolRuntimeService.MonitorSymbolRuntimesSource.Clear();
            NotificationService.Show("符号表已清空", $"删除了{count}个符号", NotificationType.Success);
        });
        RemoveSymbolFileCommand = ReactiveCommand.Create<string>(path =>
        {
            if (!string.IsNullOrEmpty(path))
                ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Remove(path);
            NotificationService.Show("文件已移除", $"移除文件: {path}", NotificationType.Success);
        });
        ShowSymbolFileInformationCommand = ReactiveCommand.Create<string>(ShowSymbolFileInformation);
        CopySymbolFilePathCommand = ReactiveCommand.CreateFromTask<(Window window, string path)>(async tuple =>
        {
            var (window, path) = tuple;
            if (!string.IsNullOrEmpty(path))
            {
                var topLevel = TopLevel.GetTopLevel(window);
                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(path);
                }
            }
            NotificationService.Show("文件路径已复制至剪贴板", path, NotificationType.Success);
        });
        OpenSymbolFileFolderCommand = ReactiveCommand.Create<string>(path =>
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                // 根据操作系统打开文件夹
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Error($"打开错误 {ex.Message}");
                NotificationService.Show("打开错误", ex.Message, NotificationType.Error);
            }
        });

        // 串口相关
        DeviceIDTemp = ProjectSaveService.Instance.Settings.DeviceConfig.DeviceID;
        BaudRateTemp = ProjectSaveService.Instance.Settings.SerialConfig.BaudRate;
        SerialTimeoutTemp = ProjectSaveService.Instance.Settings.SerialConfig.TimeoutMilliseconds;
        SerialRetryTimesTemp = ProjectSaveService.Instance.Settings.SerialConfig.RetryTimes;

        this.BindWithDefault(x => x.DeviceIDTemp, v => ProjectSaveService.Instance.Settings.DeviceConfig.DeviceID = v, (byte)0);
        RefreshPortsCommand = ReactiveCommand.Create(RefreshPorts);
        this.BindWithDefault(x => x.BaudRateTemp, v => ProjectSaveService.Instance.Settings.SerialConfig.BaudRate = v, 115200);
        this.BindWithDefault(x => x.SerialTimeoutTemp, v => ProjectSaveService.Instance.Settings.SerialConfig.TimeoutMilliseconds = v, (UInt16)50);
        this.BindWithDefault(x => x.SerialRetryTimesTemp, v => ProjectSaveService.Instance.Settings.SerialConfig.RetryTimes = v, (UInt16)10);

        // UDP相关
        RefreshIPCommand = ReactiveCommand.Create(RefreshIPs);
        // 检查UI输入的IP是否合法
        RemoteAddressTemp = ProjectSaveService.Instance.Settings.UdpConfig.RemoteAddress;
        this.WhenAnyValue<SettingWindowViewModel, string?>(x => x.RemoteAddressTemp)
        .Subscribe((Action<string?>)(ip =>
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                ProjectSaveService.Instance.Settings.UdpConfig.RemoteAddress = "127.0.0.1";
                RemoteAddressError = null;
            }
            else if (IsStrictIPv4(ip))
            {
                ProjectSaveService.Instance.Settings.UdpConfig.RemoteAddress = ip;
                RemoteAddressError = null;
            }
            else
            {
                // 如果不合法则不做任何操作
                RemoteAddressError = "请输入合法的 IPv4 地址";
            }
        }));

        // init
        IapLocalPortTemp = ProjectSaveService.Instance.Settings.UdpConfig.IapLocalPort;
        IapRemotePortTemp = ProjectSaveService.Instance.Settings.UdpConfig.IapRemotePort;
        XcpLocalPortTemp = ProjectSaveService.Instance.Settings.UdpConfig.XcpLocalPort;
        XcpRemotePortTemp = ProjectSaveService.Instance.Settings.UdpConfig.XcpRemotePort;
        UdpTimeoutTemp = ProjectSaveService.Instance.Settings.UdpConfig.TimeoutMilliseconds;
        UdpRetryTimesTemp = ProjectSaveService.Instance.Settings.UdpConfig.RetryTimes;

        // check
        this.BindWithDefault(x => x.IapLocalPortTemp, v => ProjectSaveService.Instance.Settings.UdpConfig.IapLocalPort = v, 50002);
        this.BindWithDefault(x => x.IapRemotePortTemp, v => ProjectSaveService.Instance.Settings.UdpConfig.IapRemotePort = v, 50001);
        this.BindWithDefault(x => x.XcpLocalPortTemp, v => ProjectSaveService.Instance.Settings.UdpConfig.XcpLocalPort = v, 50010);
        this.BindWithDefault(x => x.XcpRemotePortTemp, v => ProjectSaveService.Instance.Settings.UdpConfig.XcpRemotePort = v, 50011);
        this.BindWithDefault(x => x.UdpTimeoutTemp, v => ProjectSaveService.Instance.Settings.UdpConfig.TimeoutMilliseconds = v, 50);
        this.BindWithDefault(x => x.UdpRetryTimesTemp, v => ProjectSaveService.Instance.Settings.UdpConfig.RetryTimes = v, (UInt16)10);
        // 监控
        MaxSaveLenTemp = ProjectSaveService.Instance.Settings.MonitorConfig.MaxSaveLen;
        this.BindWithDefault(x => x.MaxSaveLenTemp, v => ProjectSaveService.Instance.Settings.MonitorConfig.MaxSaveLen = v, 100000);

        ClearMonitorSymbolsCommand = ReactiveCommand.Create(() =>
        {
            var count = ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Count;
            ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Clear();
            NotificationService.Show("监控符号列表已清空", $"移除了{count}个变量", NotificationType.Success);
        });
        RemoveDuplicateMonitorSymbolsCommand = ReactiveCommand.Create(() =>
        {
            var cnt = ProjectSaveService.Instance.Settings.MonitorConfig.RemoveDuplicateSymbols();
            NotificationService.Show("移除成功", $"已移除 {cnt} 个重复变量", NotificationType.Success);
        });
        RemoveMonitorSymbolCommand = ReactiveCommand.Create<UserSymbolInfo>(symbol =>
        {
            ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Remove(symbol);
            NotificationService.Show("符号已移除出监控列表", $"{symbol.SourceFileName} : {symbol.Name}", NotificationType.Success);
        });
        // IAP相关
        IapFilePathTemp = ProjectSaveService.Instance.Settings.IapConfig.IapFilePath;
        this.WhenAnyValue(x => x.IapFilePathTemp)
        .Subscribe(path =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                // 空路径 → 使用应用程序当前地址
                ProjectSaveService.Instance.Settings.IapConfig.IapFilePath = AppContext.BaseDirectory;
                IapFilePathError = null;
            }
            else if (IsValidPath(path))
            {
                // 合法路径 → 同步
                ProjectSaveService.Instance.Settings.IapConfig.IapFilePath = path;
                IapFilePathError = null;
            }
            else
            {
                // 非法路径 → 提示错误
                IapFilePathError = "请输入合法路径";
            }
        });
        IapBytesPerFrameTemp = ProjectSaveService.Instance.Settings.IapConfig.BytesPerFrame;
        WaitForHandShakeTimeoutSecondsTemp = ProjectSaveService.Instance.Settings.IapConfig.WaitForHandShakeTimeoutSeconds;
        WaitForWriteTimeoutSecondsTemp = ProjectSaveService.Instance.Settings.IapConfig.WaitForWriteTimeoutSeconds;
        WaitForCheckTimeoutSecondsTemp = ProjectSaveService.Instance.Settings.IapConfig.WaitForCheckTimeoutSeconds;
        WaitForRebootStartTimeoutSecondsTemp = ProjectSaveService.Instance.Settings.IapConfig.WaitForRebootStartTimeoutSeconds;
        WaitForRebootCompleteTimeoutSecondsTemp = ProjectSaveService.Instance.Settings.IapConfig.WaitForRebootCompleteTimeoutSeconds;
        this.BindWithDefault(x => x.IapBytesPerFrameTemp, v => ProjectSaveService.Instance.Settings.IapConfig.BytesPerFrame = v, (UInt16)256);

        this.BindWithDefault(x => x.WaitForHandShakeTimeoutSecondsTemp, v => ProjectSaveService.Instance.Settings.IapConfig.WaitForHandShakeTimeoutSeconds = v, (UInt16)10);

        this.BindWithDefault(x => x.WaitForWriteTimeoutSecondsTemp, v => ProjectSaveService.Instance.Settings.IapConfig.WaitForWriteTimeoutSeconds = v, (UInt16)10);

        this.BindWithDefault(x => x.WaitForCheckTimeoutSecondsTemp, v => ProjectSaveService.Instance.Settings.IapConfig.WaitForCheckTimeoutSeconds = v, (UInt16)10);

        this.BindWithDefault(x => x.WaitForRebootStartTimeoutSecondsTemp, v => ProjectSaveService.Instance.Settings.IapConfig.WaitForRebootStartTimeoutSeconds = v, (UInt16)10);

        this.BindWithDefault(x => x.WaitForRebootCompleteTimeoutSecondsTemp, v => ProjectSaveService.Instance.Settings.IapConfig.WaitForRebootCompleteTimeoutSeconds = v, (UInt16)30);

        // 用户指令过滤
        var userCommandFilter = this.WhenValueChanged(x => x.UserCommandSearchText)
                                        .Throttle(TimeSpan.FromMilliseconds(100))
                                        .Select(UserCommand.CreateUserSymbolInfoSearchFilter);

        ProjectSaveService.Instance.Settings.UserCommandConfig.UserCommands.Connect()
                           .AutoRefresh(c => c.Name, changeSetBuffer: TimeSpan.FromSeconds(1))
                           .AutoRefresh(c => c.Description, changeSetBuffer: TimeSpan.FromSeconds(1))
                           .Filter(userCommandFilter)
                           .ObserveOn(AvaloniaScheduler.Instance)
                           .Bind(out _filteredUserCommands)
                           .Subscribe();
    }
    #region MEA
    [ReactiveCommand]
    private void ApplyChangeSaveLength()
    {
        foreach (var symbolRuntime in SymbolRuntimeService.MonitorSymbolRuntimesSource.Items)
        {
            var newRuntime = SymbolRuntime.CreateSymbolRuntime(symbolRuntime.Symbol,
                                                   ProjectSaveService.Instance.Settings.MonitorConfig.MaxSaveLen);
            SymbolRuntimeService.MonitorSymbolRuntimesSource.AddOrUpdate(newRuntime);
        }

        foreach (var monitor in DockableRegistry.GridMonitorDocuments.Values)
        {
            monitor.RefreshDisplay();
        }
        NotificationService.Show("应用成功", $"记录长度已变更为{ProjectSaveService.Instance.Settings.MonitorConfig.MaxSaveLen}", NotificationType.Success);
    }
    [ReactiveCommand]
    private void HeaderOfIsMonitoredChanged()
    {
        SelectAllMonitorSymbolsBox ??= false;
        //点击后为全选
        if (SelectAllMonitorSymbolsBox == true)
        {
            foreach (var item in ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Items)
            {
                item.IsMonitored = true;
            }
        }
        //点击后为全不选或部分选
        else
        {
            foreach (var item in ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Items)
            {
                item.IsMonitored = false;
            }
        }
    }
    [ReactiveCommand]
    private void AddMonitorSymbol(SymbolInfo symbol)
    {
        var userSymbol = new UserSymbolInfo
        {
            Name = symbol.Name,
            SourceFileName = symbol.SourceFileName,
            Address = symbol.Address,
            Size = symbol.Size
        };
        foreach (var existSymbol in ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Items)
        {
            if (userSymbol.SourceFileName == existSymbol.SourceFileName &&
                userSymbol.Name == existSymbol.Name &&
                userSymbol.Offset == existSymbol.Offset)
            {
                NotificationService.Show("符号已存在", $"{userSymbol.SourceFileName} : {userSymbol.Name} , offset : {userSymbol.Offset} 已存在于监控变量列表", NotificationType.Warning);
                return;
            }
        }
        foreach (var existSymbol2 in ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols.Items)
        {
            if (userSymbol.SourceFileName == existSymbol2.SourceFileName &&
                userSymbol.Name == existSymbol2.Name &&
                userSymbol.Offset == existSymbol2.Offset)
            {
                NotificationService.Show("符号已存在", $"{userSymbol.SourceFileName} : {userSymbol.Name} , offset : {userSymbol.Offset} 已存在于标定变量列表", NotificationType.Warning);
                return;
            }
        }
        ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.AddOrUpdate(userSymbol);
        NotificationService.Show("符号已添加至监控列表", $"{symbol.SourceFileName} : {symbol.Name}", NotificationType.Success);
    }
    #endregion
    #region CAL
    [ReactiveCommand]
    private void ClearCalibrateSymbols()
    {
        var count = ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols.Count;
        ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols.Clear();
        NotificationService.Show("监控符号列表已清空", $"移除了{count}个变量", NotificationType.Success);
    }
    [ReactiveCommand]
    private void RemoveDuplicateCalibrateSymbols()
    {
        var cnt = ProjectSaveService.Instance.Settings.CalibrateConfig.RemoveDuplicateSymbols();
        NotificationService.Show("移除成功", $"已移除 {cnt} 个重复变量", NotificationType.Success);
    }
    [ReactiveCommand]
    private void AddCalibrateSymbol(SymbolInfo symbol)
    {
        var userSymbol = new UserSymbolInfo
        {
            Name = symbol.Name,
            SourceFileName = symbol.SourceFileName,
            Address = symbol.Address,
            Size = symbol.Size
        };
        foreach (var existSymbol in ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Items)
        {
            if (userSymbol.SourceFileName == existSymbol.SourceFileName &&
                userSymbol.Name == existSymbol.Name &&
                userSymbol.Offset == existSymbol.Offset)
            {
                NotificationService.Show("符号已存在", $"{userSymbol.SourceFileName} : {userSymbol.Name} , offset : {userSymbol.Offset} 已存在于监控变量列表", NotificationType.Warning);
                return;
            }
        }

        foreach (var existSymbol2 in ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols.Items)
        {
            if (userSymbol.SourceFileName == existSymbol2.SourceFileName &&
                userSymbol.Name == existSymbol2.Name &&
                userSymbol.Offset == existSymbol2.Offset)
            {
                NotificationService.Show("符号已存在", $"{userSymbol.SourceFileName} : {userSymbol.Name} , offset : {userSymbol.Offset} 已存在于标定变量列表", NotificationType.Warning);
                return;
            }
        }
        ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols.AddOrUpdate(userSymbol);
        NotificationService.Show("符号已添加至标定列表", $"{symbol.SourceFileName} : {symbol.Name}", NotificationType.Success);
    }
    [ReactiveCommand]
    private void RemoveCalibrateSymbol(UserSymbolInfo symbol)
    {
        ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols.Remove(symbol);
        NotificationService.Show("符号已移除出标定列表", $"{symbol.SourceFileName} : {symbol.Name}", NotificationType.Success);
    }
    #endregion

    private async Task SelectSymbolFileAsync(Window window)
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择符号表文件",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("ELF 文件") { Patterns = ["*.elf"] },
                new("所有文件") { Patterns = ["*"] }
            }
        });

        if (files is { Count: > 0 })
        {
            try
            {
                var path = files[0].Path.LocalPath;
                var elf = ELFReader.Load(path);

                var errors = new List<string>();
                if (elf.Class != Class.Bit32)
                {
                    throw new InvalidDataException("ELF错误：类型未知");
                }
                if (ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Items.Contains(path))
                {
                    throw new Exception("文件已存在于列表中");
                }
                else
                {
                    ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Add(path);
                    var symbol_count = ProjectSaveService.Instance.Settings.DeviceConfig.ReadElfFile(path);
                    NotificationService.Show("添加成功", $"从{path}加载了{symbol_count}个变量", NotificationType.Success);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error($"读取elf文件错误 {ex.Message}");
                NotificationService.Show("读取错误", ex.Message, NotificationType.Error);
                return;
            }
        }
    }
    private void ShowSymbolFileInformation(string symbol_file_path)
    {
        try
        {
            var elf = ELFReader.Load(symbol_file_path);

            var information = new List<string>
                {
                    $"平台：{elf.Machine}",
                    $"类型：{elf.Type}",
                    $"位宽：{elf.Class}",
                    $"端序：{elf.Endianess}"
                };
            NotificationService.Show("文件信息", string.Join(Environment.NewLine, information), NotificationType.Info);
            return;

        }
        catch (Exception ex)
        {
            Serilog.Log.Error($"读取elf文件错误 {ex.Message}");
            NotificationService.Show("读取错误", ex.Message, NotificationType.Error);
            return;
        }
    }
    [ReactiveCommand]
    private void ClearUserCommands()
    {
        foreach (var item in ProjectSaveService.Instance.Settings.UserCommandConfig.UserCommands.Items)
        {
            item.Name = string.Empty;
            item.Description = string.Empty;
        }
    }
    // 刷新串口列表
    private void RefreshPorts()
    {
        var current_selection = ProjectSaveService.Instance.Settings.SerialConfig.SelectedSerialPort;

        // 更新串口SourceList
        _serialPortSource.Edit(list =>
        {
            list.Clear();
            list.AddRange(SerialPort.GetPortNames());
        });

        // 刷新后恢复原选择。若没有原选择且有串口，默认选择第一个串口。
        if (_availableSerialPorts.Contains(current_selection))
        {
            ProjectSaveService.Instance.Settings.SerialConfig.SelectedSerialPort = current_selection;
        }
        else if (!string.IsNullOrEmpty(current_selection) && _availableSerialPorts.Count > 0)
        {
            ProjectSaveService.Instance.Settings.SerialConfig.SelectedSerialPort = _availableSerialPorts[0];
        }
        NotificationService.Show("串口刷新成功", String.Join(", ", _availableSerialPorts), NotificationType.Success);
    }

    // 刷新本地IP
    private static List<string> GetLocalIPv4Addresses()
    {
        var ipList = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    ipList.Add(ua.Address.ToString());
            }
        }
        return ipList;
    }
    private void RefreshIPs()
    {
        var current = ProjectSaveService.Instance.Settings.UdpConfig.LocalAddress;

        // 更新IP SourceList
        _localIPSource.Edit(list =>
        {
            list.Clear();
            list.AddRange(GetLocalIPv4Addresses());
        });

        if (_availableIPv4Addresses.Contains(current))
            ProjectSaveService.Instance.Settings.UdpConfig.LocalAddress = current;

        NotificationService.Show("本地IP刷新成功", String.Join(" ,", ProjectSaveService.Instance.Settings.UdpConfig.LocalAddress), NotificationType.Success);
    }

    // 判断ipv4是否合法
    private static bool IsStrictIPv4(string ip)
    {
        return Regex.IsMatch(ip, @"^((25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\.){3}(25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)$");
    }

    // 判断路径是否合法
    private static bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        // 检查是否包含非法字符
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;
        try
        {
            // 获取完整路径，这会验证路径格式
            string fullPath = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public static class ReactiveExtensions
{
    public static IDisposable BindWithDefault<TViewModel, TProperty>(
        this TViewModel vm,
        Expression<Func<TViewModel, TProperty?>> sourceExpr,
        Action<TProperty> apply,
        TProperty defaultValue)
        where TViewModel : ReactiveObject
        where TProperty : struct
    {
        var source = sourceExpr.Compile();
        var memberExpr = (MemberExpression)sourceExpr.Body;
        var propertyName = memberExpr.Member.Name;

        var subscription = vm.WhenAnyValue(sourceExpr)
            .Subscribe(x =>
            {
                apply(x ?? defaultValue);
                if (x == null)
                {
                    typeof(TViewModel).GetProperty(propertyName)?.SetValue(vm, defaultValue);
                }
            });
        return subscription;
    }
}
