using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using UniversalHost.Symbols;

namespace UniversalHost.Models;

public enum SymbolInputKind { Offset, ArrayIndex }

/// <summary>
/// 从固件文件读取的根符号信息；具体成员访问由 UserSymbolInfo 保存。
/// </summary>
public partial class SymbolInfo : ReactiveObject
{
    // 保存的固件路径，关联类型快照并区分不同目录中的同名文件。
    [Reactive] private string _sourceKey = string.Empty;
    // 根定义保留链接名称和类型 ID；成员路径不替代根符号身份。
    [Reactive] private FirmwareSymbol? _definition;
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
    private uint _offset;
    public uint Offset
    {
        get => _offset;
        set
        {
            this.RaiseAndSetIfChanged(ref _offset, value);
            this.RaisePropertyChanged(nameof(ActualAddressHex));
        }
    }
    [JsonIgnore]
    public uint ActualAddress => Mode == AccessMode.Automatic
        ? checked((uint)(Access?.Address ?? throw new InvalidOperationException("自动变量没有保存的解析地址")))
        : checked(Address + Offset);
    [Reactive] private AccessMode _mode = AccessMode.Manual;
    [Reactive] private string _memberPath = string.Empty;
    [Reactive] private AccessStatus _verificationStatus = AccessStatus.Unverified;
    [Reactive] private string _verificationReason = "手动配置，未经自动验证";
    [Reactive] private TargetInfo _target = new();
    [Reactive] private SymbolInputKind _inputKind;
    // One snapshot holds resolved address, bounds, scalar type and storage width.
    [Reactive] private AccessResolution? _access;
    [JsonIgnore]
    public int StorageBits => Access is { StorageBits: 8 or 16 or 32 or 64 } access && access.Scalar == DataType
        ? access.StorageBits : DataType switch
        {
            ScalarType.Int16 or ScalarType.Uint16 => 16,
            ScalarType.Int32 or ScalarType.Uint32 or ScalarType.Float32 => 32,
            ScalarType.Int64 or ScalarType.Uint64 or ScalarType.Float64 => 64,
            ScalarType.Boolean => Math.Max(8, Target.AddressUnitBits),
            _ => 8
        };
    [JsonIgnore] public string RootName => Definition?.Name ?? Name;
    [JsonIgnore] public bool CanEditType => Mode != AccessMode.Automatic || VerificationStatus != AccessStatus.Verified;
    [JsonIgnore]
    public string ActualAddressHex
    {
        get
        {
            try { return $"0x{ActualAddress:X8}"; }
            catch (OverflowException) { return "地址溢出"; }
            catch (InvalidOperationException) { return "地址未验证"; }
        }
    }

    public void RefreshAccessDisplay()
    {
        this.RaisePropertyChanged(nameof(CanEditType));
        this.RaisePropertyChanged(nameof(ActualAddressHex));
        this.RaisePropertyChanged(nameof(StorageBits));
    }
    //用于标定变量限制的最大值/最小值。监控变量不需要。
    [Reactive] private double? _maxValue;
    [Reactive] private double? _minValue;

    [Reactive] private bool _isMonitored = false;
    [Reactive] private string _alias = string.Empty;
    [Reactive] private string _unit = string.Empty;
    [Reactive] private string _description = string.Empty;
    [Reactive] private ScalarType _dataType = ScalarType.Unknown;

    private readonly Dictionary<int, SymbolBitAlias> _bitAliases = [];

    // 只保存非空别名；没有位别名的变量不输出此属性。
    [JsonPropertyName("BitAliases")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<int, string>? BitAliasesStorage
    {
        get
        {
            // 自动保存在线程池读取快照，窗口可能同时按需创建位别名。
            lock (_bitAliases)
            {
                Dictionary<int, string>? aliases = null;
                foreach (var (index, bitAlias) in _bitAliases)
                {
                    var alias = bitAlias.Alias;
                    if (string.IsNullOrWhiteSpace(alias)) continue;
                    (aliases ??= [])[index] = alias;
                }
                return aliases;
            }
        }
        set
        {
            lock (_bitAliases)
            {
                foreach (var bitAlias in _bitAliases.Values.ToArray())
                    bitAlias.Alias = string.Empty;

                if (value == null) return;
                foreach (var (index, alias) in value)
                {
                    if (index >= 0 && index < 64 && !string.IsNullOrWhiteSpace(alias))
                        GetBitAlias(index).Alias = alias;
                }
            }
        }
    }

    public SymbolBitAlias GetBitAlias(int index)
    {
        if (index < 0 || index >= 64)
            throw new ArgumentOutOfRangeException(nameof(index));

        lock (_bitAliases)
        {
            if (!_bitAliases.TryGetValue(index, out var bitAlias))
            {
                bitAlias = new SymbolBitAlias(index,
                    () => this.RaisePropertyChanged(nameof(BitAliasesStorage)));
                _bitAliases.Add(index, bitAlias);
            }
            return bitAlias;
        }
    }

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
