using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI.Primitives.Reactive.Concurrency;
using ReactiveUI.Reactive;
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
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.Symbols;


namespace UniversalHost.ViewModels.Windows;

public partial class SettingWindowViewModel : ReactiveObject, IDisposable
{
    public FirmwareImportCoordinator ImportCoordinator => FirmwareImportCoordinator.Instance;
    private bool CanChangeLayout()
    {
        try
        {
            FirmwareSymbolService.EnsureLayoutEditable();
            if (ImportCoordinator.IsBusy) throw new InvalidOperationException("请等待固件解析完成或取消解析");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            NotificationService.Show("变量访问设置", ex.Message, NotificationType.Warning);
            return false;
        }
    }
    [ReactiveCommand] private void CancelFirmwareParsing() => ImportCoordinator.Cancel();

    private void AddSymbol(SymbolTableEntry row, bool calibrate)
    {
        try
        {
            if (!CanChangeLayout()) return;
            var settings = ProjectSaveService.Instance.Settings;
            if (!settings.DeviceConfig.Symbols.Items.Contains(row.Root)) throw new InvalidOperationException("符号快照已变化，请重新选择变量");
            var access = SymbolSelectionService.CreateAccess(settings.DeviceConfig, row);
            var otherSymbols = calibrate
                ? settings.MonitorConfig.MonitoredSymbols.Items
                : settings.CalibrateConfig.CalibratedSymbols.Items;
            // 按来源、根符号和访问位置判断同一变量，不受别名、数据类型或监控勾选状态影响。
            if (otherSymbols.Any(symbol => symbol.SourceKey == access.SourceKey
                && (symbol.Definition?.LinkageName ?? symbol.RootName) == (access.Definition?.LinkageName ?? access.RootName)
                && symbol.Address == access.Address
                && symbol.MemberPath == access.MemberPath
                && (symbol.Mode == AccessMode.Automatic ? symbol.Access?.OffsetUnits : symbol.Offset)
                    == (access.Mode == AccessMode.Automatic ? access.Access?.OffsetUnits : access.Offset)))
            {
                string existingList = calibrate ? "监控" : "标定";
                string targetList = calibrate ? "标定" : "监控";
                NotificationService.Show("添加变量",
                    $"{access.SourceFileName} : {access.Name} 已存在于{existingList}列表，无法添加至{targetList}列表，请先从{existingList}列表移除。",
                    NotificationType.Warning);
                return;
            }
            if (calibrate) settings.CalibrateConfig.CalibratedSymbols.AddOrUpdate(access);
            else settings.MonitorConfig.MonitoredSymbols.AddOrUpdate(access);
            NotificationService.Show(calibrate ? "符号已添加至标定列表" : "符号已添加至监控列表",
                $"{access.SourceFileName} : {access.Name}", NotificationType.Success);
        }
        catch (Exception ex) { NotificationService.Show("添加变量", ex.Message, NotificationType.Warning); }
    }
    public static SettingWindowViewModel Instance { get; private set; } = new SettingWindowViewModel();
    private readonly CompositeDisposable _disposables = [];
    private readonly CompositeDisposable _projectSubscriptions = [];

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
    private readonly SourceList<SymbolTableEntry> _symbolRows = new();
    private readonly ReadOnlyObservableCollection<SymbolTableEntry> _filteredSymbols;
    public ReadOnlyObservableCollection<SymbolTableEntry> FilteredSymbols => _filteredSymbols;

    // 串口设置
    [Reactive] private int? _baudRateTemp;
    [Reactive] private UInt16? _serialTimeoutTemp;
    [Reactive] private UInt16? _serialRetryTimesTemp;
    [Reactive] private UInt16? _serialTurnaroundDelayTemp;
    public ReactiveCommand<Unit, Unit> RefreshPortsCommand { get; }

    // 串口和IP的SourceList，用于更新数据
    private readonly SourceList<string> _serialPortSource;
    private readonly SourceList<string> _localIPSource;

    private readonly ReadOnlyObservableCollection<string> _availableSerialPorts;
    public ReadOnlyObservableCollection<string> AvailableSerialPorts => _availableSerialPorts;
    public static IEnumerable<System.IO.Ports.Parity> AvailableParities => Enum.GetValues<System.IO.Ports.Parity>();
    public static IEnumerable<System.IO.Ports.StopBits> AvailableStopBits =>
        [System.IO.Ports.StopBits.One, System.IO.Ports.StopBits.OnePointFive, System.IO.Ports.StopBits.Two];
    public static IEnumerable<SerialDuplexMode> AvailableSerialDuplexModes => Enum.GetValues<SerialDuplexMode>();
    public static IEnumerable<SerialDirectionControl> AvailableSerialDirectionControls => Enum.GetValues<SerialDirectionControl>();

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
    [Reactive] private int _monitoredSymbolCount;
    [Reactive] private long _monitoredSymbolsTotalSize;
    public ReactiveCommand<Unit, Unit> ClearMonitorSymbolsCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveDuplicateMonitorSymbolsCommand { get; }
    public ReactiveCommand<UserSymbolInfo, Unit> RemoveMonitorSymbolCommand { get; }
    public static IEnumerable<ScalarType> AvailableDataTypes => Enum.GetValues<ScalarType>();
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
    [Reactive] private UInt16? _waitForHandShakeTimeoutSecondsTemp;
    [Reactive] private UInt16? _waitForEraseTimeoutSecondsTemp;
    [Reactive] private UInt16? _waitForWriteTimeoutSecondsTemp;
    [Reactive] private UInt16? _waitForCheckTimeoutSecondsTemp;
    [Reactive] private UInt16? _waitForRebootTimeoutSecondsTemp;

    //日志设置
    public static IEnumerable<Serilog.Events.LogEventLevel> AvaliableLogEventLevel => Enum.GetValues<Serilog.Events.LogEventLevel>();


    public SettingWindowViewModel()
    {

        //符号表路径
        // 使用 DynamicData 的 Switch：切换工程时退订旧集合，并清除旧项。
        ProjectSaveService.Instance.WhenAnyValue(x => x.Settings.DeviceConfig.SymbolFilePaths)
                                                .Select(source => source.Connect())
                                                .Switch()
                                                .Bind(out _symbolFilePaths)
                                                .RefCount()
                                                .Subscribe().DisposeWith(_disposables);
        // 符号过滤逻辑
        var symbolFilter = this.WhenValueChanged(x => x.SymbolSearchText)
                              .Throttle(TimeSpan.FromMilliseconds(100))
                              .Select(text => new Func<SymbolTableEntry, bool>(row => string.IsNullOrWhiteSpace(text) ||
                                  row.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || row.TypeName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                  row.AddressHex.Contains(text, StringComparison.OrdinalIgnoreCase) || row.SourceFileName.Contains(text, StringComparison.OrdinalIgnoreCase)));

        _symbolRows.DisposeWith(_disposables);
        Observable.Switch(ProjectSaveService.Instance.WhenAnyValue(x => x.Settings.DeviceConfig)
                    .Select(device => device.Symbols.Connect().QueryWhenChanged(_ => SymbolSelectionService.BuildRows(device))))
                    .ObserveOn(AvaloniaScheduler.Instance)
                    .Subscribe(rows => _symbolRows.Edit(list => { list.Clear(); list.AddRange(rows); }))
                    .DisposeWith(_disposables);
        _symbolRows.Connect()
                    .Filter(symbolFilter)
                    .ObserveOn(AvaloniaScheduler.Instance)
                    .Bind(out _filteredSymbols)
                    .Subscribe().DisposeWith(_disposables);
        // 监控符号过滤逻辑
        var monitorSymbolFilter = this.WhenValueChanged(x => x.MonitorSymbolSearchText)
                                     .Throttle(TimeSpan.FromMilliseconds(100))
                                     .Select(UserSymbolInfo.CreateUserSymbolInfoSearchFilter);

        ProjectSaveService.Instance.WhenAnyValue(x => x.Settings.MonitorConfig.MonitoredSymbols)
                           .Select(source => source.Connect())
                           .Switch()
                           .AutoRefresh(symbol => symbol.Alias, changeSetBuffer: TimeSpan.FromSeconds(1))
                           .AutoRefresh(symbol => symbol.Description, changeSetBuffer: TimeSpan.FromSeconds(1))
                           .Filter(monitorSymbolFilter)
                           .ObserveOn(AvaloniaScheduler.Instance)
                           .Bind(out _filteredMonitorSymbols)
                           .Subscribe().DisposeWith(_disposables);

        // 按用户指定的数据类型统计已勾选的监控变量，不受搜索筛选影响。
        ProjectSaveService.Instance.WhenAnyValue(x => x.Settings.MonitorConfig.MonitoredSymbols)
                            .Select(source => source.Connect())
                            .Switch()
                            .AutoRefresh(symbol => symbol.IsMonitored)
                            .AutoRefresh(symbol => symbol.DataType)
                            .AutoRefresh(symbol => symbol.Access)
                            .AutoRefresh(symbol => symbol.Target)
                            .Filter(symbol => symbol.IsMonitored)
                            .QueryWhenChanged(cache => (Count: cache.Count, TotalSize: cache.Items.Sum(symbol => symbol.DataType switch
                            {
                                ScalarType.Int16 or ScalarType.Uint16 => 2L,
                                ScalarType.Int32 or ScalarType.Uint32 or ScalarType.Float32 => 4L,
                                ScalarType.Int64 or ScalarType.Uint64 or ScalarType.Float64 => 8L,
                                ScalarType.Boolean => symbol.StorageBits / 8L,
                                _ => 1L, // Int8、Uint8、Boolean，以及运行时按 byte 处理的 Unknown。
                            })))
                            .ObserveOn(AvaloniaScheduler.Instance)
                            .Subscribe(statistics =>
                            {
                                MonitoredSymbolCount = statistics.Count;
                                MonitoredSymbolsTotalSize = statistics.TotalSize;
                            }).DisposeWith(_disposables);

        // 在 ViewModel 初始化时，订阅列表变化以自动更新“全选框”的状态
        ProjectSaveService.Instance.WhenAnyValue(x => x.Settings.MonitorConfig.MonitoredSymbols)
                            .Select(source => source.Connect())
                            .Switch()
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
                            }).DisposeWith(_disposables);
        //CAL
        var clibrateSymbolFilter = this.WhenValueChanged(x => x.CalibrateSymbolSearchText)
                                     .Throttle(TimeSpan.FromMilliseconds(100))
                                     .Select(UserSymbolInfo.CreateUserSymbolInfoSearchFilter);

        ProjectSaveService.Instance.WhenAnyValue(x => x.Settings.CalibrateConfig.CalibratedSymbols)
                          .Select(source => source.Connect())
                          .Switch()
                          .AutoRefresh(symbol => symbol.Alias, changeSetBuffer: TimeSpan.FromSeconds(1))
                          .AutoRefresh(symbol => symbol.Description, changeSetBuffer: TimeSpan.FromSeconds(1))
                          .Filter(clibrateSymbolFilter)
                          .ObserveOn(AvaloniaScheduler.Instance)
                          .Bind(out _filteredCalibrateSymbols)
                          .Subscribe().DisposeWith(_disposables);

        // 初始化串口列表
        _serialPortSource = new SourceList<string>();
        _serialPortSource.AddRange(SerialPort.GetPortNames());
        _serialPortSource.Connect()
                       .Bind(out _availableSerialPorts)
                       .Subscribe().DisposeWith(_disposables);

        // 初始化本地IP列表
        _localIPSource = new SourceList<string>();
        _localIPSource.AddRange(GetLocalIPv4Addresses());
        _localIPSource.Connect()
                    .Bind(out _availableIPv4Addresses)
                    .Subscribe().DisposeWith(_disposables);

        ReloadSymbolFileCommand = ReactiveCommand.CreateFromTask(FirmwareImportCoordinator.Instance.ReloadAsync);
        SelectSymbolFileCommand = ReactiveCommand.CreateFromTask<Window>(SelectSymbolFileAsync);
        ClearSymbolFilePathsCommand = ReactiveCommand.Create(() =>
        {
            if (!CanChangeLayout()) return;
            var count = ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Count;
            ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Clear();
            FirmwareSymbolService.RemoveSnapshot(ProjectSaveService.Instance.Settings);
            NotificationService.Show("符号文件列表已清空", $"删除了{count}个路径", NotificationType.Success);
        });
        ClearSymbolsCommand = ReactiveCommand.Create(() =>
        {
            if (!CanChangeLayout()) return;
            var count = ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Count;
            FirmwareSymbolService.RemoveSnapshot(ProjectSaveService.Instance.Settings);
            NotificationService.Show("符号表已清空", $"删除了{count}个符号", NotificationType.Success);
        });
        RemoveSymbolFileCommand = ReactiveCommand.Create<string>(path =>
        {
            if (!CanChangeLayout()) return;
            if (!string.IsNullOrEmpty(path))
            {
                ProjectSaveService.Instance.Settings.DeviceConfig.SymbolFilePaths.Remove(path);
                FirmwareSymbolService.RemoveSnapshot(ProjectSaveService.Instance.Settings, path);
            }
            NotificationService.Show("文件已移除", $"移除文件: {path}", NotificationType.Success);
        });
        ShowSymbolFileInformationCommand = ReactiveCommand.Create<string>(ShowSymbolFileInformation);
        CopySymbolFilePathCommand = ReactiveCommand.CreateFromTask<(Window window, string path)>(async tuple =>
        {
            var (window, path) = tuple;
            if (!string.IsNullOrEmpty(path))
            {
                path = ProjectFilePathService.ResolvePath(path, ProjectSaveService.Instance.ProjectFilePath);
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
                var directory = Path.GetDirectoryName(ProjectFilePathService.ResolvePath(path, ProjectSaveService.Instance.ProjectFilePath));
                // 根据操作系统打开文件夹
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogService.ForModule(LogModule.Symbols).Error(ex, "打开错误 {Reason}", ex.Message);
                NotificationService.Show("打开错误", ex.Message, NotificationType.Error);
            }
        });

        RefreshPortsCommand = ReactiveCommand.Create(RefreshPorts);
        RefreshIPCommand = ReactiveCommand.Create(RefreshIPs);

        ClearMonitorSymbolsCommand = ReactiveCommand.Create(() =>
        {
            if (!CanChangeLayout()) return;
            var count = ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Count;
            ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Clear();
            NotificationService.Show("监控符号列表已清空", $"移除了{count}个变量", NotificationType.Success);
        });
        RemoveDuplicateMonitorSymbolsCommand = ReactiveCommand.Create(() =>
        {
            if (!CanChangeLayout()) return;
            var cnt = ProjectSaveService.Instance.Settings.MonitorConfig.RemoveDuplicateSymbols();
            NotificationService.Show("移除成功", $"已移除 {cnt} 个重复变量", NotificationType.Success);
        });
        RemoveMonitorSymbolCommand = ReactiveCommand.Create<UserSymbolInfo>(symbol =>
        {
            if (!CanChangeLayout()) return;
            ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Remove(symbol);
            NotificationService.Show("符号已移除出监控列表", $"{symbol.SourceFileName} : {symbol.Name}", NotificationType.Success);
        });
        // 用户指令过滤
        var userCommandFilter = this.WhenValueChanged(x => x.UserCommandSearchText)
                                        .Throttle(TimeSpan.FromMilliseconds(100))
                                        .Select(UserCommand.CreateUserSymbolInfoSearchFilter);

        ProjectSaveService.Instance.WhenAnyValue(x => x.Settings.UserCommandConfig.UserCommands)
                           .Select(source => source.Connect())
                           .Switch()
                           .AutoRefresh(c => c.Name, changeSetBuffer: TimeSpan.FromSeconds(1))
                           .AutoRefresh(c => c.Description, changeSetBuffer: TimeSpan.FromSeconds(1))
                           .Filter(userCommandFilter)
                           .ObserveOn(AvaloniaScheduler.Instance)
                           .Bind(out _filteredUserCommands)
                           .Subscribe().DisposeWith(_disposables);
        ProjectSaveService.Instance.WhenAnyValue(x => x.Settings)
            .Subscribe(BindProjectSettings)
            .DisposeWith(_disposables);
    }

    private void BindProjectSettings(ProjectSettings settings)
    {
        // 先停止旧输入回写，再读取新工程；所有回写都捕获本次工程的设置。
        _projectSubscriptions.Clear();
        // 串口相关
        DeviceIDTemp = settings.DeviceConfig.DeviceID;
        BaudRateTemp = settings.SerialConfig.BaudRate;
        SerialTimeoutTemp = settings.SerialConfig.TimeoutMilliseconds;
        SerialRetryTimesTemp = settings.SerialConfig.RetryTimes;
        SerialTurnaroundDelayTemp = settings.SerialConfig.TurnaroundDelayMilliseconds;

        this.BindWithDefault(x => x.DeviceIDTemp, v => settings.DeviceConfig.DeviceID = v, (byte)0).DisposeWith(_projectSubscriptions);
        this.BindWithDefault(x => x.BaudRateTemp, v => settings.SerialConfig.BaudRate = v, 115200).DisposeWith(_projectSubscriptions);
        this.BindWithDefault(x => x.SerialTimeoutTemp, v => settings.SerialConfig.TimeoutMilliseconds = v, (UInt16)50).DisposeWith(_projectSubscriptions);
        this.BindWithDefault(x => x.SerialRetryTimesTemp, v => settings.SerialConfig.RetryTimes = v, (UInt16)10).DisposeWith(_projectSubscriptions);
        this.BindWithDefault(x => x.SerialTurnaroundDelayTemp, v => settings.SerialConfig.TurnaroundDelayMilliseconds = v, (UInt16)2).DisposeWith(_projectSubscriptions);

        // UDP相关
        // 检查UI输入的IP是否合法
        RemoteAddressTemp = settings.UdpConfig.RemoteAddress;
        this.WhenAnyValue<SettingWindowViewModel, string?>(x => x.RemoteAddressTemp)
        .Subscribe((Action<string?>)(ip =>
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                settings.UdpConfig.RemoteAddress = "127.0.0.1";
                RemoteAddressError = null;
            }
            else if (IsStrictIPv4(ip))
            {
                settings.UdpConfig.RemoteAddress = ip;
                RemoteAddressError = null;
            }
            else
            {
                // 如果不合法则不做任何操作
                RemoteAddressError = "请输入合法的 IPv4 地址";
            }
        })).DisposeWith(_projectSubscriptions);

        // init
        IapLocalPortTemp = settings.UdpConfig.IapLocalPort;
        IapRemotePortTemp = settings.UdpConfig.IapRemotePort;
        XcpLocalPortTemp = settings.UdpConfig.XcpLocalPort;
        XcpRemotePortTemp = settings.UdpConfig.XcpRemotePort;
        UdpTimeoutTemp = settings.UdpConfig.TimeoutMilliseconds;
        UdpRetryTimesTemp = settings.UdpConfig.RetryTimes;

        // check
        this.BindWithDefault(x => x.IapLocalPortTemp, v => settings.UdpConfig.IapLocalPort = v, 50002).DisposeWith(_projectSubscriptions);
        this.BindWithDefault(x => x.IapRemotePortTemp, v => settings.UdpConfig.IapRemotePort = v, 50001).DisposeWith(_projectSubscriptions);
        this.BindWithDefault(x => x.XcpLocalPortTemp, v => settings.UdpConfig.XcpLocalPort = v, 50010).DisposeWith(_projectSubscriptions);
        this.BindWithDefault(x => x.XcpRemotePortTemp, v => settings.UdpConfig.XcpRemotePort = v, 50011).DisposeWith(_projectSubscriptions);
        this.BindWithDefault(x => x.UdpTimeoutTemp, v => settings.UdpConfig.TimeoutMilliseconds = v, 50).DisposeWith(_projectSubscriptions);
        this.BindWithDefault(x => x.UdpRetryTimesTemp, v => settings.UdpConfig.RetryTimes = v, (UInt16)10).DisposeWith(_projectSubscriptions);
        // 监控
        MaxSaveLenTemp = settings.MonitorConfig.MaxSaveLen;
        this.BindWithDefault(x => x.MaxSaveLenTemp, v => settings.MonitorConfig.MaxSaveLen = v, 100000).DisposeWith(_projectSubscriptions);

        // IAP相关
        IapFilePathTemp = settings.IapConfig.IapFilePath;
        this.WhenAnyValue(x => x.IapFilePathTemp)
        .Subscribe(path =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                // 空路径 → 使用应用程序当前地址
                settings.IapConfig.IapFilePath = AppContext.BaseDirectory;
                IapFilePathError = null;
            }
            else if (IsValidPath(path))
            {
                // 合法路径 → 同步
                settings.IapConfig.IapFilePath = path;
                IapFilePathError = null;
            }
            else
            {
                // 非法路径 → 提示错误
                IapFilePathError = "请输入合法路径";
            }
        }).DisposeWith(_projectSubscriptions);

        WaitForHandShakeTimeoutSecondsTemp = settings.IapConfig.WaitForHandShakeTimeoutSeconds;
        WaitForEraseTimeoutSecondsTemp = settings.IapConfig.WaitForEraseTimeoutSeconds;
        WaitForWriteTimeoutSecondsTemp = settings.IapConfig.WaitForWriteTimeoutSeconds;
        WaitForCheckTimeoutSecondsTemp = settings.IapConfig.WaitForCheckTimeoutSeconds;
        WaitForRebootTimeoutSecondsTemp = settings.IapConfig.WaitForRebootTimeoutSeconds;

        this.BindWithDefault(x => x.WaitForHandShakeTimeoutSecondsTemp, v => settings.IapConfig.WaitForHandShakeTimeoutSeconds = v, (UInt16)5).DisposeWith(_projectSubscriptions);

        this.BindWithDefault(x => x.WaitForEraseTimeoutSecondsTemp, v => settings.IapConfig.WaitForEraseTimeoutSeconds = v, (UInt16)10).DisposeWith(_projectSubscriptions);

        this.BindWithDefault(x => x.WaitForWriteTimeoutSecondsTemp, v => settings.IapConfig.WaitForWriteTimeoutSeconds = v, (UInt16)10).DisposeWith(_projectSubscriptions);

        this.BindWithDefault(x => x.WaitForCheckTimeoutSecondsTemp, v => settings.IapConfig.WaitForCheckTimeoutSeconds = v, (UInt16)10).DisposeWith(_projectSubscriptions);

        this.BindWithDefault(x => x.WaitForRebootTimeoutSecondsTemp, v => settings.IapConfig.WaitForRebootTimeoutSeconds = v, (UInt16)30).DisposeWith(_projectSubscriptions);

        // 升级工具也可以选择文件，设置窗口同步显示同一份路径。
        settings.IapConfig.WhenAnyValue(x => x.IapFilePath)
            .Subscribe(path => IapFilePathTemp = path)
            .DisposeWith(_projectSubscriptions);
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _projectSubscriptions.Dispose();
        _serialPortSource.Dispose();
        _localIPSource.Dispose();
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
        // CheckBox 会先切换自身状态再执行命令，因此以各行状态决定操作。
        var symbols = ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols.Items.ToArray();
        var selectAll = symbols.Any(symbol => !symbol.IsMonitored);
        foreach (var symbol in symbols)
            symbol.IsMonitored = selectAll;
        SelectAllMonitorSymbolsBox = selectAll;
    }
    [ReactiveCommand]
    private void AddMonitorSymbol(SymbolTableEntry symbol) => AddSymbol(symbol, false);
    #endregion
    #region CAL
    [ReactiveCommand]
    private void ClearCalibrateSymbols()
    {
        if (!CanChangeLayout()) return;
        var count = ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols.Count;
        ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols.Clear();
        NotificationService.Show("监控符号列表已清空", $"移除了{count}个变量", NotificationType.Success);
    }
    [ReactiveCommand]
    private void RemoveDuplicateCalibrateSymbols()
    {
        if (!CanChangeLayout()) return;
        var cnt = ProjectSaveService.Instance.Settings.CalibrateConfig.RemoveDuplicateSymbols();
        NotificationService.Show("移除成功", $"已移除 {cnt} 个重复变量", NotificationType.Success);
    }
    [ReactiveCommand]
    private void AddCalibrateSymbol(SymbolTableEntry symbol) => AddSymbol(symbol, true);
    [ReactiveCommand]
    private void RemoveCalibrateSymbol(UserSymbolInfo symbol)
    {
        if (!CanChangeLayout()) return;
        ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols.Remove(symbol);
        NotificationService.Show("符号已移除出标定列表", $"{symbol.SourceFileName} : {symbol.Name}", NotificationType.Success);
    }
    #endregion

    [ReactiveCommand]
    private void ClearAllBitAliases()
    {
        var settings = ProjectSaveService.Instance.Settings;
        var count = 0;
        foreach (var symbol in settings.MonitorConfig.MonitoredSymbols.Items
            .Concat(settings.CalibrateConfig.CalibratedSymbols.Items))
        {
            if (symbol.BitAliasesStorage != null)
                count++;
            symbol.BitAliasesStorage = null;
        }
        NotificationService.Show("位别名已清空", $"已清空 {count} 个变量的位别名", NotificationType.Success);
    }

    private async Task SelectSymbolFileAsync(Window window)
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择符号表文件",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("固件符号文件") { Patterns = ["*.elf", "*.axf", "*.out"] },
                new("所有文件") { Patterns = ["*"] }
            }
        });

        if (files is { Count: > 0 })
            await FirmwareImportCoordinator.Instance.ImportAsync(files[0].Path.LocalPath);
    }
    private void ShowSymbolFileInformation(string path)
    {
        var image = ProjectSaveService.Instance.Settings.DeviceConfig.FirmwareSnapshots.FirstOrDefault(f => f.SourcePath == path);
        NotificationService.Show("保存的固件信息", image == null ? "尚无解析快照，请主动更新固件" :
            $"{image.Target.Format} / {image.Target.Platform} / {image.Target.Abi}\n地址单位 {image.Target.AddressUnitBits} bit；{(image.Target.LittleEndian ? "小端" : "大端")}\n{image.Backend}\nSHA256: {image.Sha256}", NotificationType.Info);
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

        // 同步增量更新，保留仍可用的列表项及其选择。
        UpdateAvailableItems(_serialPortSource, SerialPort.GetPortNames());

        // 刷新后恢复原选择。若原串口已不可用且有串口，默认选择第一个串口。
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

        UpdateAvailableItems(_localIPSource, GetLocalIPv4Addresses());

        if (_availableIPv4Addresses.Contains(current))
            ProjectSaveService.Instance.Settings.UdpConfig.LocalAddress = current;

        NotificationService.Show("本地IP刷新成功", String.Join(" ,", _availableIPv4Addresses), NotificationType.Success);
    }

    // 由 UI 线程上的初始化/刷新调用；不要清空仍有效的项，以免 ComboBox 丢失选择。
    private static void UpdateAvailableItems(SourceList<string> source, IEnumerable<string> items)
    {
        var available = items.Distinct().ToList();
        source.Edit(list =>
        {
            foreach (var item in list.Where(item => !available.Contains(item)).ToList())
                list.Remove(item);

            foreach (var item in available)
            {
                if (!list.Contains(item))
                    list.Add(item);
            }
        });
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
            string fullPath = ProjectFilePathService.ResolvePath(path, ProjectSaveService.Instance.ProjectFilePath);
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
        var memberExpr = (MemberExpression)sourceExpr.Body;
        var propertyName = memberExpr.Member.Name;

        // 泛型辅助方法接收运行时表达式，无法使用绑定源生成器的编译期分派。
        var subscription = vm.WhenAnyValueUnsafe(sourceExpr)
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
