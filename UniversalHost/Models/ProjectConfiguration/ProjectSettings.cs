using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;

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
    /// <summary>
    /// 更新所有符号，包含符号表、监控变量、标定变量。先更新符号表，随后根据名称和来源更新监控和标定变量。若不存在则删除监控/标定变量。
    /// </summary>
    /// <returns>
    /// 移除的监控/标定变量。
    /// </returns>
    public List<UserSymbolInfo> ReloadAllSymbols(string projectFilePath)
    {
        var removedSymbols = new List<UserSymbolInfo>();
        //更新符号表
        this.DeviceConfig.ReloadSymbolTable(projectFilePath);

        // 构建快速查找表：Key = (sourceFileName, Name)
        var symbolLookup = this.DeviceConfig.Symbols.Items
            .GroupBy(s => (s.SourceFileName, s.Name))
            .ToDictionary(g => g.Key, g => g.First());

        // 使用 SourceList 的 Edit 方法进行批量操作，提高性能。
        // 根据符号的源文件和名称更新MonitoredSymbols地址和大小，各个监控窗口会自动更新。
        this.MonitorConfig.MonitoredSymbols.Edit(list =>
        {
            foreach (var userSymbol in list.Items)
            {
                var key = (userSymbol.SourceFileName, userSymbol.Name);
                if (symbolLookup.TryGetValue(key, out var updated_symbol))
                {
                    // 直接修改对象属性，这些更改会反映在原始集合中。
                    userSymbol.Address = updated_symbol.Address;
                    userSymbol.Size = updated_symbol.Size;
                }
                else
                {
                    removedSymbols.Add(userSymbol);
                    Debug.WriteLine($"Warning: Symbol not found for {userSymbol.SourceFileName}:{userSymbol.Name}");
                }
            }
            // 批量移除不需要的项
            foreach (var item in removedSymbols)
            {
                list.Remove(item);
            }
        });

        this.CalibrateConfig.CalibratedSymbols.Edit(list =>
        {
            foreach (var userSymbol in list.Items)
            {
                var key = (userSymbol.SourceFileName, userSymbol.Name);
                if (symbolLookup.TryGetValue(key, out var updated_symbol))
                {
                    // 直接修改对象属性，这些更改会反映在原始集合中。
                    userSymbol.Address = updated_symbol.Address;
                    userSymbol.Size = updated_symbol.Size;
                }
                else
                {
                    removedSymbols.Add(userSymbol);
                    Debug.WriteLine($"Warning: Symbol not found for {userSymbol.SourceFileName}:{userSymbol.Name}");
                }
            }
            // 批量移除不需要的项
            foreach (var item in removedSymbols)
            {
                list.Remove(item);
            }
        });

        return removedSymbols;
    }
}

public sealed class ProjectMetadata
{
    // 仅在工程文件结构或字段含义发生不兼容变化时递增。
    public const int CurrentFormatVersion = 1;

    [JsonRequired]
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string AppVersion { get; init; } = "未知";
}

public partial class LogConfig : ReactiveObject
{

    [Reactive] private bool _logEnabled = true;
    [Reactive] private bool _logWriteToFileEnabled = true;
    [Reactive] private Serilog.Events.LogEventLevel _logEventLevelSetting = Serilog.Events.LogEventLevel.Information;
}
