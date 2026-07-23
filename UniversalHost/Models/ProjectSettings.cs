using DynamicData;
using ELFSharp.ELF;
using ELFSharp.ELF.Sections;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json.Serialization;

namespace UniversalHost.Models;

public enum SymbolDataType
{
    Int8,
    Uint8,
    Int16,
    Uint16,
    Int32,
    Uint32,
    Int64,
    Uint64,
    Float32,
    Float64,
    Boolean,
    Unknown
}
public enum CommunicationMode
{
    UDP,
    Serial
}
/// <summary>
/// 从elf文件中读取到的原始符号信息
/// </summary>
public partial class SymbolInfo : ReactiveObject
{
    [Reactive] private string _sourceFileName = string.Empty;
    [Reactive] private string _name = string.Empty;

    private uint _address = 0;
    public uint Address
    {
        get => _address;
        set
        {
            this.RaiseAndSetIfChanged(ref _address, value);
            this.RaisePropertyChanged(nameof(AddressHex));
        }
    }
    [Reactive] private uint _size = 0;
    [JsonIgnore] public string AddressHex => $"0x{Address:X8}";//用于UI显示
    public static Func<SymbolInfo, bool> CreateSymbolInfoSearchFilter(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return _ => true; // 显示所有项
        }

        var lowerSearchText = searchText.ToLowerInvariant();
        return symbol =>
            symbol.Name?.ToLowerInvariant().Contains(lowerSearchText) == true ||
            symbol.AddressHex?.ToLowerInvariant().Contains(lowerSearchText) == true ||
            symbol.SourceFileName?.ToLowerInvariant().Contains(lowerSearchText) == true;
    }
}
/// <summary>
/// 在SymbolInfo的基础上增加了用户可自定义的信息
/// </summary>
public partial class UserSymbolInfo : SymbolInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();
    private uint? _offset = 0;
    public uint? Offset
    {
        get => _offset;
        set
        {
            this.RaiseAndSetIfChanged(ref _offset, value ?? 0);
        }
    }
    //用于标定变量限制的最大值/最小值。监控变量不需要。
    [Reactive] private double? _maxValue;
    [Reactive] private double? _minValue;

    [Reactive] private bool _isMonitored = false;
    [Reactive] private string _alias = string.Empty;
    [Reactive] private string _unit = string.Empty;
    [Reactive] private string _description = string.Empty;
    [Reactive] private SymbolDataType _dataType = SymbolDataType.Unknown;

    public static Func<UserSymbolInfo, bool> CreateUserSymbolInfoSearchFilter(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return _ => true; // 显示所有项
        }

        var lowerSearchText = searchText.ToLowerInvariant();
        return symbol =>
            symbol.Name?.ToLowerInvariant().Contains(lowerSearchText) == true ||
            symbol.AddressHex?.ToLowerInvariant().Contains(lowerSearchText) == true ||
            symbol.SourceFileName?.ToLowerInvariant().Contains(lowerSearchText) == true ||
            symbol.Alias?.ToLowerInvariant().Contains(lowerSearchText) == true ||
            symbol.Description?.ToLowerInvariant().Contains(lowerSearchText) == true;
    }
}
public partial class UdpConfig : ReactiveObject
{
    [Reactive] private string _localAddress = "127.0.0.1";
    [Reactive] private string _remoteAddress = "127.0.0.1";
    [Reactive] private int _iapLocalPort = 50002;
    [Reactive] private int _iapRemotePort = 50001;
    [Reactive] private int _xcpLocalPort = 50010;
    [Reactive] private int _xcpRemotePort = 50011;
    [Reactive] private int _timeoutMilliseconds = 50;
    [Reactive] private UInt16 _retryTimes = 10;
}
public partial class SerialConfig : ReactiveObject
{
    [Reactive] private string _selectedSerialPort = "";
    [Reactive] private int _baudRate = 115200;
    [Reactive] private Parity _paritySetting = System.IO.Ports.Parity.None;
    [Reactive] private StopBits _stopBitsSetting = StopBits.None;
    [Reactive] private UInt16 _timeoutMilliseconds = 50;
    [Reactive] private UInt16 _retryTimes = 10;
}
public partial class IapConfig : ReactiveObject
{
    [Reactive] private string _iapFilePath = Path.Combine(AppContext.BaseDirectory);
    [Reactive] private UInt16 _bytesPerFrame = 256;
    [Reactive] private UInt16 _waitForHandShakeTimeoutSeconds = 10;
    [Reactive] private UInt16 _waitForWriteTimeoutSeconds = 10;
    [Reactive] private UInt16 _waitForCheckTimeoutSeconds = 10;
    [Reactive] private UInt16 _waitForRebootStartTimeoutSeconds = 10;
    [Reactive] private UInt16 _waitForRebootCompleteTimeoutSeconds = 30;
}
public partial class DeviceConfig : ReactiveObject
{
    [Reactive] private CommunicationMode _mode = CommunicationMode.UDP;
    [Reactive] private byte _deviceID = 0x00;
    [JsonIgnore] public SourceList<string> SymbolFilePaths { get; } = new();
    [JsonPropertyName("SymbolFilePaths")] // 确保 JSON 中的名称保持不变
    public List<string> SymbolFilePathsStorage
    {
        get => SymbolFilePaths.Items.ToList(); // 保存时：从 SourceList 转换到 List
        set // 读取时：用 List 的数据填充 SourceList
        {
            SymbolFilePaths.Edit(list =>
            {
                list.Clear();
                list.AddRange(value);
            });
        }
    }
    [JsonIgnore] public SourceList<SymbolInfo> Symbols { get; } = new();
    [JsonPropertyName("Symbols")]
    public List<SymbolInfo> SymbolsStorage
    {
        get => Symbols.Items.ToList(); // 保存时
        set // 读取时
        {
            Symbols.Edit(list =>
            {
                list.Clear();
                list.AddRange(value);
            });
        }
    }
    public uint ReadElfFile(string path)
    {
        var elf = ELFReader.Load(path);
        var sourceFileName = System.IO.Path.GetFileName(path);
        uint addSymbolCount = 0;
        foreach (var section in elf.Sections)
        {
            if (section.Type != SectionType.SymbolTable)
                continue;
            // 针对 32 位 ELF
            if (elf.Class == Class.Bit32)
            {
                var symbol_table = (SymbolTable<uint>)section;
                foreach (var symbol in symbol_table.Entries)
                {
                    if (string.IsNullOrEmpty(symbol.Name) || symbol.Name.Contains('.') || symbol.Name.StartsWith("$"))
                    {
                        continue;
                    }
                    //仅加入全局变量
                    //if (symbol.Binding != SymbolBinding.Global)
                    //{
                    //    continue;
                    //}

                    if (symbol.Type == SymbolType.Object || symbol.Type == SymbolType.NotSpecified)
                    {
                        this.Symbols.Add(new SymbolInfo
                        {
                            SourceFileName = sourceFileName,
                            Name = symbol.Name,
                            Address = symbol.Value,
                            Size = symbol.Size
                        });
                        addSymbolCount++;
                    }
                }
            }
            // 针对 64 位 ELF
            else if (elf.Class == Class.Bit64)
            {
                //var symbol_table = (SymbolTable<ulong>)section;
                //foreach (var symbol in symbol_table.Entries)
                //{
                //    if (symbol.Name.Contains('.'))
                //    {
                //        continue;
                //    }
                //    if (symbol.Type == SymbolType.Object || symbol.Type == SymbolType.NotSpecified)
                //    {
                //        if (symbol.Size == 0)
                //        {
                //            continue;
                //        }
                //        this.Symbols.Add(new SymbolInfo
                //        {
                //            Name = symbol.Name,
                //            Address = symbol.ValueString,
                //            Size = symbol.Size
                //        });
                //        addSymbolCount++;
                //    }
                //}
            }
            else
            {
                throw new InvalidDataException("ELF错误：类型未知");
            }
        }
        return addSymbolCount;
    }
    public void ReloadSymbolTable()
    {
        this.Symbols.Clear();
        foreach (var path in this.SymbolFilePaths.Items)
        {
            ReadElfFile(path);
        }
    }
}
public partial class MonitorConfig : ReactiveObject
{
    [Reactive] private int _maxSaveLen = 100000;//最大存储长度
    [JsonIgnore]
    public SourceCache<UserSymbolInfo, Guid> MonitoredSymbols { get; } =
                                                 new SourceCache<UserSymbolInfo, Guid>(x => x.Id);
    [JsonPropertyName("MonitoredSymbols")] // 确保 JSON 中的名称保持不变
    public List<UserSymbolInfo> MonitoredSymbolsStorage
    {
        get => MonitoredSymbols.Items.ToList(); // 保存时
        set//读取
        {
            MonitoredSymbols.Edit(updater =>
            {
                updater.Clear();

                if (value is null)
                    return;

                updater.AddOrUpdate(value);
            });
        }
    }
    public int RemoveDuplicateSymbols()
    {
        var duplicates = MonitoredSymbols.Items
            .GroupBy(x => (x.Name, x.SourceFileName, x.Offset))
            .SelectMany(g => g.Skip(1))
            .ToList();

        if (duplicates.Count == 0)
            return duplicates.Count;

        MonitoredSymbols.Edit(updater =>
        {
            foreach (var item in duplicates)
            {
                updater.Remove(item.Id);
            }
        });

        return duplicates.Count;
    }

}
public partial class CalibrateConfig : ReactiveObject
{
    [JsonIgnore]
    public SourceCache<UserSymbolInfo, Guid> CalibratedSymbols { get; } =
                                                 new SourceCache<UserSymbolInfo, Guid>(x => x.Id);
    [JsonPropertyName("CalibratedSymbols")] // 确保 JSON 中的名称保持不变
    public List<UserSymbolInfo> CalibratedSymbolsStorage
    {
        get => CalibratedSymbols.Items.ToList(); // 保存时
        set//读取
        {
            CalibratedSymbols.Edit(updater =>
            {
                updater.Clear();

                if (value is null)
                    return;

                updater.AddOrUpdate(value);
            });
        }
    }
    public int RemoveDuplicateSymbols()
    {
        var duplicates = CalibratedSymbols.Items
            .GroupBy(x => (x.Name, x.SourceFileName, x.Offset))
            .SelectMany(g => g.Skip(1))
            .ToList();

        if (duplicates.Count == 0)
            return duplicates.Count;

        CalibratedSymbols.Edit(updater =>
        {
            foreach (var item in duplicates)
            {
                updater.Remove(item.Id);
            }
        });

        return duplicates.Count;
    }

}

public partial class LogConfig : ReactiveObject
{

    [Reactive] private bool _logEnabled = true;
    [Reactive] private bool _logWriteToFileEnabled = true;
    [Reactive] private Serilog.Events.LogEventLevel _logEventLevelSetting = Serilog.Events.LogEventLevel.Information;
}

public partial class UserCommand : ReactiveObject
{
    public byte Id { get; init; } = 0;
    public string IdHex => Id.ToString("X2");
    [Reactive] private string _name = string.Empty;
    [Reactive] private string _description = string.Empty;
    public static Func<UserCommand, bool> CreateUserSymbolInfoSearchFilter(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return _ => true; // 显示所有项
        }
        var lowerSearchText = searchText.ToLowerInvariant();
        return symbol =>
            symbol.IdHex.ToLowerInvariant().Contains(lowerSearchText) == true ||
            symbol.Name?.ToLowerInvariant().Contains(lowerSearchText) == true ||
            symbol.Description?.ToLowerInvariant().Contains(lowerSearchText) == true;
    }
}
public partial class UserCommandConfig : ReactiveObject
{
    [JsonIgnore] public SourceCache<UserCommand, byte> UserCommands { get; } = new SourceCache<UserCommand, byte>(x => x.Id);
    [JsonPropertyName("UserCommands")] // 确保 JSON 中的名称保持不变
    public List<UserCommand> UserCommandsStorage
    {
        get => UserCommands.Items.ToList(); // 保存时
        set
        {
            if (value == null)
                value = new List<UserCommand>();

            var dict = value.ToDictionary(x => x.Id);

            UserCommands.Edit(updater =>
            {
                updater.Clear();
                for (int i = 0; i <= byte.MaxValue; i++)
                {
                    dict.TryGetValue((byte)i, out var item);

                    var cmd = new UserCommand
                    {
                        Id = (byte)i,
                        Name = item?.Name ?? string.Empty,
                        Description = item?.Description ?? string.Empty
                    };

                    updater.AddOrUpdate(cmd);
                }
            });
        }
    }
    public UserCommandConfig()
    {
        for (int i = 0; i <= byte.MaxValue; i++)
        {
            var cmd = new UserCommand()
            {
                Id = (byte)i,
            };
            UserCommands.AddOrUpdate(cmd);
        }
    }
}

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
    public List<UserSymbolInfo> ReloadAllSymbols()
    {
        var removedSymbols = new List<UserSymbolInfo>();
        //更新符号表
        this.DeviceConfig.ReloadSymbolTable();

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

public static class AppSettingsReactiveObjectExtensions
{
    public static IDisposable SubscribeToAllChanges(
        this ReactiveObject root,
        Action callback)
    {
        // 递归地获取所有嵌套的 ReactiveObject 的 Changed 事件流和 SourceList 变化流
        var allChanges = GetAllChanges(root);

        // 使用 Observable.Merge 合并所有事件流
        return Observable.Merge(allChanges)
            //.Do(_ => Debug.WriteLine($"Change detected in {root.GetType().Name}")) // 可选：用于调试哪些对象触发了变更
            .Throttle(TimeSpan.FromSeconds(1)) // 1s 防抖
            .Skip(1)
            .Subscribe(_ =>
            {
                Debug.WriteLine("Auto save triggered."); // 确保这个回调被触发
                callback();
            });
    }
    private static IEnumerable<IObservable<Unit>> GetAllChanges(object obj, HashSet<object>? visited = null)
    {
        visited ??= new HashSet<object>();

        if (obj == null || obj is Type)
            yield break;

        // 如果已经访问过，直接返回，避免无限递归
        if (!visited.Add(obj))
            yield break;

        if (obj is ReactiveObject reactiveObj)
        {
            yield return reactiveObj.Changed.Select(_ => Unit.Default);
        }

        //添加SourceList
        if (obj is ISourceList<UserSymbolInfo> userSymbols)
            yield return Observable.Merge(
            userSymbols.Connect().Select(_ => Unit.Default),
            userSymbols.Connect().MergeMany(item => item.Changed.Select(_ => Unit.Default))
        );

        if (obj is ISourceList<SymbolInfo> symbols)
            yield return Observable.Merge(
            symbols.Connect().Select(_ => Unit.Default),
            symbols.Connect().MergeMany(item => item.Changed.Select(_ => Unit.Default))
        );

        if (obj is ISourceList<string> paths)
            yield return paths.Connect().Select(_ => Unit.Default);

        //添加SourceCache
        if (obj is ISourceCache<UserSymbolInfo, Guid> userSymbolCache)
        {
            yield return Observable.Merge(
                userSymbolCache.Connect().Select(_ => Unit.Default),
                userSymbolCache.Connect()
                    .MergeMany(item => item.Changed.Select(_ => Unit.Default))
            );
        }

        if (obj is ISourceCache<UserCommand, byte> userCommandCache)
        {
            yield return Observable.Merge(
                userCommandCache.Connect().Select(_ => Unit.Default),
                userCommandCache.Connect()
                    .MergeMany(item => item.Changed.Select(_ => Unit.Default))
            );
        }

        //添加其他项
        var properties = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            if (prop.GetIndexParameters().Length > 0)
                continue;

            if (prop.DeclaringType?.IsGenericParameter == true)
                continue;

            object? value;
            try
            {
                value = prop.GetValue(obj);
            }
            catch
            {
                continue;
            }

            if (value != null)
            {
                foreach (var observable in GetAllChanges(value, visited))
                {
                    yield return observable;
                }
            }
        }
    }


}
