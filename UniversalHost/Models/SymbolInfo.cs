using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System;

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
    [JsonIgnore] public uint ActualAddress => Address + _offset ?? 0;
    //用于标定变量限制的最大值/最小值。监控变量不需要。
    [Reactive] private double? _maxValue;
    [Reactive] private double? _minValue;

    [Reactive] private bool _isMonitored = false;
    [Reactive] private string _alias = string.Empty;
    [Reactive] private string _unit = string.Empty;
    [Reactive] private string _description = string.Empty;
    [Reactive] private SymbolDataType _dataType = SymbolDataType.Unknown;

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
