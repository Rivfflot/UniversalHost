using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;

namespace UniversalHost.Models;

public partial class ProjectSettings : ReactiveObject
{
    // 通用设置
    [Reactive] private DeviceConfig _deviceConfig = new();
    // UDP相关设置
    [Reactive] private UdpConfig _udpConfig = new();
    // 串口相关设置
    [Reactive] private SerialConfig _serialConfig = new();
    [Reactive] private MonitorConfig _monitorConfig = new();
    [Reactive] private CalibrateConfig _calibrateConfig = new();
    [Reactive] private UserCommandConfig _userCommandConfig = new();
    [Reactive] private IapConfig _iapConfig = new();
    [Reactive] private LogConfig _logConfig = new();
}

public partial class LogConfig : ReactiveObject
{

    [Reactive] private bool _logEnabled = true;
    [Reactive] private bool _logWriteToFileEnabled = true;
    [Reactive] private Serilog.Events.LogEventLevel _logEventLevelSetting = Serilog.Events.LogEventLevel.Information;
}
