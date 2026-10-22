namespace UniversalHost.Symbols;

internal sealed class DwarfModelBuilder
{
    private readonly RawImage _raw;
    private readonly Dictionary<string, RawNode> _nodes;
    private readonly Dictionary<string, TypeLayout> _types = [];
    private readonly HashSet<string> _building = [];
    private readonly HashSet<string> _invalidCycles = [];
    private readonly CancellationToken _token;
    private const uint Name = 3, Size = 11, Type = 73, Encoding = 62;

    public DwarfModelBuilder(RawImage raw, CancellationToken token = default)
    {
        _token = token;
        _raw = raw;
        _nodes = (raw.Nodes ?? []).ToDictionary(n => n.ID);
    }

    public FirmwareImage Build(string path)
    {
        foreach (var variable in _nodes.Values.Where(n => n.Tag == 0x34))
            variable.Location = InheritedLocation(variable, []);
        var variables = _nodes.Values.Where(n => n.Tag == 0x34 && n.Location.HasValue).ToList();
        var byAddress = variables.ToLookup(n => n.Location!.Value);
        var byName = _nodes.Values.Where(n => n.Tag == 0x34).ToLookup(LinkName);
        var symbols = new List<FirmwareSymbol>();
        foreach (var s in _raw.Symbols)
        {
            _token.ThrowIfCancellationRequested();
            var candidates = byAddress[s.Address].Where(n =>
                LinkName(n) == s.Name || Text(n, Name) == s.Name).ToList();
            var symbol = new FirmwareSymbol
            {
                Name = s.Name,
                LinkageName = s.Name,
                Address = s.Address,
                SizeUnits = s.Size
            };
            if (candidates.Count > 1)
                symbol = symbol with { Status = AccessStatus.Conflict, Reason = "名称和地址匹配到多个 DWARF 定义" };
            else if (candidates.Count == 0 && byName[s.Name].Any())
                symbol = symbol with { Reason = "DWARF 位置表达式不是受支持的固定内存地址，或与符号地址不一致" };
            else if (candidates.Count == 1)
            {
                var n = candidates[0];
                var typeId = Reference(n, Type);
                var t = BuildType(typeId);
                bool conflict = s.Size != 0 && t.SizeUnits.HasValue && s.Size != t.SizeUnits;
                symbol = symbol with
                {
                    Name = Text(n, Name).Length > 0 ? Text(n, Name) : s.Name,
                    TypeId = typeId,
                    SizeUnits = s.Size != 0 ? s.Size : t.SizeUnits ?? 0,
                    Status = conflict ? AccessStatus.Conflict : AccessStatus.Verified,
                    Reason = conflict ? "符号大小与 DWARF 类型存储宽度冲突" : ""
                };
            }
            if (!_raw.Linked || _raw.AddressUnitBits == 0)
                symbol = symbol with { Status = AccessStatus.Unverified, Reason = !_raw.Linked ? "未链接对象没有已解析的设备地址" : "未知目标地址单位" };
            symbols.Add(symbol);
        }
        // A fixed DW_OP_addr definition may survive after .symtab stripping.
        // Import it, but never infer deletion from an incomplete symbol table.
        var imported = symbols.SelectMany(s => new[] { (s.Address, s.LinkageName), (s.Address, s.Name) }).ToHashSet();
        var excluded = new HashSet<string>(_raw.ExcludedSymbolNames ?? []);
        foreach (var n in variables)
        {
            _token.ThrowIfCancellationRequested();
            // A stripped symbol table must not let private DWARF variables
            // bypass the container reader's global-symbol filter.
            if (Number(n, 0x3f) != 1)
            {
                if (!imported.Contains((n.Location!.Value, LinkName(n))) && !imported.Contains((n.Location.Value, Text(n, Name))))
                {
                    excluded.Add(LinkName(n));
                    excluded.Add(Text(n, Name));
                }
                continue;
            }
            if (imported.Contains((n.Location!.Value, LinkName(n))) || imported.Contains((n.Location.Value, Text(n, Name)))) continue;
            string typeId = Reference(n, Type);
            var type = BuildType(typeId);
            symbols.Add(new FirmwareSymbol
            {
                Name = Text(n, Name),
                LinkageName = LinkName(n),
                Address = n.Location!.Value,
                TypeId = typeId,
                SizeUnits = type.SizeUnits ?? 0,
                Status = _raw.Linked && _raw.AddressUnitBits != 0 ? AccessStatus.Verified : AccessStatus.Unverified,
                Reason = _raw.Linked ? "" : "未链接对象没有已解析的设备地址"
            });
        }
        return new FirmwareImage
        {
            SourcePath = path,
            Backend = _raw.Backend,
            Sha256 = _raw.Sha256,
            Target = new TargetInfo
            {
                Format = _raw.Format,
                Platform = _raw.Platform,
                Abi = _raw.ABI,
                AddressBits = _raw.AddressBits,
                AddressUnitBits = _raw.AddressUnitBits,
                Linked = _raw.Linked,
                LittleEndian = _raw.LittleEndian
            },
            Symbols = symbols,
            ExcludedSymbolNames = excluded.Where(name => name.Length > 0).ToList(),
            Types = _types,
            Diagnostics = _raw.Diagnostics,
            SymbolsComplete = _raw.SymbolsComplete,
            TypesComplete = _raw.TypesComplete && variables.Count > 0,
            DwarfVersions = _raw.DwarfVersions ?? []
        };
    }

    private ulong? InheritedLocation(RawNode n, HashSet<string> seen)
    {
        if (!seen.Add(n.ID) || seen.Count > 128) return null;
        if (n.Location.HasValue || n.HasLocation) return n.Location;
        foreach (uint attr in new uint[] { 0x47, 0x31 })
            if (n.Attributes.TryGetValue(attr, out var reference) && _nodes.TryGetValue(reference.Ref, out var target))
            {
                var location = InheritedLocation(target, seen);
                if (location.HasValue) return location;
            }
        return null;
    }
    private string LinkName(RawNode n) => new[] { Text(n, 0x6e), Text(n, 0x2001), Text(n, 0x2007), Text(n, Name) }.FirstOrDefault(s => s.Length > 0) ?? "";

    private RawAttribute? Attribute(RawNode n, uint attr, HashSet<string>? seen = null)
    {
        seen ??= [];
        if (!seen.Add(n.ID) || seen.Count > 128) return null;
        if (n.Attributes.TryGetValue(attr, out var value)) return value;
        foreach (uint reference in new uint[] { 0x47, 0x31 })
            if (n.Attributes.TryGetValue(reference, out var origin) && _nodes.TryGetValue(origin.Ref, out var target))
            {
                var inherited = Attribute(target, attr, seen);
                if (inherited != null) return inherited;
            }
        return null;
    }
    private string Text(RawNode n, uint attr) => Attribute(n, attr)?.Text ?? "";
    private long? Number(RawNode n, uint attr) => Attribute(n, attr)?.Number;
    private string Reference(RawNode n, uint attr)
    {
        var reference = Attribute(n, attr)?.Ref ?? "";
        return reference.StartsWith("sig:") ? _raw.Signatures.GetValueOrDefault(reference[4..], reference) : reference;
    }
    private static ulong? NonNegative(long? value) => value is >= 0 ? (ulong)value.Value : null;

    private TypeLayout BuildType(string id)
    {
        _token.ThrowIfCancellationRequested();
        if (_types.TryGetValue(id, out var cached)) return cached;
        var invalid = new TypeLayout { Id = id, Reason = "类型引用缺失、不完整或循环" };
        if (!_nodes.TryGetValue(id, out var n)) return invalid;
        if (_building.Count >= 128 || !_building.Add(id))
        {
            _invalidCycles.UnionWith(_building);
            return invalid;
        }
        TypeLayout result;
        try { result = BuildTypeCore(n); }
        catch (OverflowException) { result = invalid with { Reason = "类型大小或数组步长溢出" }; }
        finally { _building.Remove(id); }
        if (_invalidCycles.Contains(id)) result = invalid;
        _types[id] = result;
        return result;
    }

    private TypeLayout BuildTypeCore(RawNode n)
    {
        string name = Text(n, Name);
        ulong? size = NonNegative(Number(n, Size));
        var type = new TypeLayout { Id = n.ID, Name = name, SizeUnits = size };
        if (n.Tag is 0x16 or 0x26 or 0x35 or 0x37 or 0x47 or 0x4080) // typedef / cv / restrict / atomic / TI_far_type
        {
            var target = BuildType(Reference(n, Type));
            return target with { Id = n.ID, Name = name.Length > 0 ? name : target.Name };
        }
        if (n.Tag is 0x0f or 0x10 or 0x42)
            return type with { Kind = LayoutKind.Pointer, Reason = "指针和引用不生成自动数值访问项" };
        if (n.Tag is 0x24 or 0x04)
        {
            long? encoding = Number(n, Encoding);
            TypeLayout? underlying = null;
            if (n.Tag == 0x04 && Reference(n, Type).Length > 0)
            {
                underlying = BuildType(Reference(n, Type));
                size ??= underlying.SizeUnits;
            }
            int bits = checked((int)(size ?? 0) * _raw.AddressUnitBits);
            ScalarType scalar = encoding.HasValue ? MapScalar(encoding.Value, bits) :
                underlying?.StorageBits == bits ? underlying.Scalar : ScalarType.Unknown;
            bool supported = scalar != ScalarType.Unknown && bits is 8 or 16 or 32 or 64;
            return type with
            {
                Kind = n.Tag == 0x04 ? LayoutKind.Enumeration : LayoutKind.Scalar,
                Scalar = scalar,
                StorageBits = bits,
                SizeUnits = size,
                Complete = supported,
                Reason = supported ? "" : "缺少明确的基础编码和受支持存储宽度"
            };
        }
        if (n.Tag is 0x13 or 0x17 or 0x02)
        {
            var members = new List<MemberLayout>();
            bool complete = Number(n, 0x3c) != 1 && size.HasValue;
            foreach (var childId in n.Children)
            {
                if (!_nodes.TryGetValue(childId, out var child)) { complete = false; continue; }
                if (child.Tag == 0x1c) { complete = false; continue; } // C++ inheritance requires more layout semantics
                if (child.Tag != 0x0d) continue;
                var memberName = Text(child, Name);
                string memberType = Reference(child, Type);
                bool bitfield = Number(child, 0x0d).HasValue || Number(child, 0x6b).HasValue || Number(child, 0x0c).HasValue;
                // DWARF 4 section 5.5.6 permits both location attributes to be
                // absent for a member at the beginning of its containing object.
                // An existing but unsupported expression is never treated as zero.
                ulong? offset = child.MemberOffset ?? (!child.HasMemberLocation && !bitfield &&
                    Number(child, 0x3c) != 1 && (child.Version >= 4 || n.Tag == 0x17) ? 0UL : null);
                if (memberName.Length == 0 || (!offset.HasValue && !bitfield)) complete = false;
                members.Add(new(memberName, memberType, offset, bitfield,
                    bitfield ? "位域不支持自动访问" : offset.HasValue ? "" : "成员位置不是受支持的常量表达式"));
                // Populate referenced layouts. Recursion through pointers is not followed.
                if (BuildType(memberType).Kind == LayoutKind.Unsupported) complete = false;
            }
            return type with
            {
                Kind = n.Tag == 0x17 ? LayoutKind.Union : LayoutKind.Structure,
                Members = members,
                Complete = complete,
                Reason = complete ? "" : "聚合类型声明、匿名成员或动态布局不完整"
            };
        }
        if (n.Tag == 0x01)
        {
            var elementId = Reference(n, Type);
            var element = BuildType(elementId);
            var dimensions = new List<ArrayDimension>();
            bool supportedOrder = Number(n, 0x09) is null or 0;
            if (Attribute(n, 0x51) != null && !Number(n, 0x51).HasValue) supportedOrder = false;
            foreach (var childId in n.Children)
            {
                if (!_nodes.TryGetValue(childId, out var child) || child.Tag != 0x21) continue;
                long lower = Number(child, 0x22) ?? 0;
                if (Attribute(child, 0x22) != null && !Number(child, 0x22).HasValue) supportedOrder = false;
                var count = NonNegative(Number(child, 0x37));
                if (Attribute(child, 0x37) != null && !count.HasValue) supportedOrder = false;
                if (!count.HasValue && Number(child, 0x2f) is long upper && upper >= lower)
                    count = checked((ulong)(upper - lower) + 1);
                ulong? stride = NonNegative(Number(child, 0x51));
                if (Attribute(child, 0x51) != null && !stride.HasValue) supportedOrder = false;
                if (Number(child, 0x2e) is long bitStride)
                {
                    stride = _raw.AddressUnitBits > 0 && bitStride % _raw.AddressUnitBits == 0 ? NonNegative(bitStride / _raw.AddressUnitBits) : null;
                    if (!stride.HasValue) supportedOrder = false;
                }
                dimensions.Add(new(lower, count, stride));
            }
            ulong? running = NonNegative(Number(n, 0x51)) ?? element.SizeUnits;
            if (Number(n, 0x2e).HasValue) supportedOrder = false; // packed bit arrays are not addressable scalars
            for (int i = dimensions.Count - 1; i >= 0; --i)
            {
                var dim = dimensions[i];
                ulong? stride = dim.StrideUnits ?? running;
                dimensions[i] = dim with { StrideUnits = stride };
                running = stride.HasValue && dim.Count.HasValue ? checked(stride.Value * dim.Count.Value) : null;
            }
            size ??= running;
            bool complete = supportedOrder && size.HasValue && dimensions.Count > 0 && dimensions.All(d => d.Count.HasValue && d.StrideUnits.HasValue);
            return type with
            {
                Kind = LayoutKind.Array,
                ElementTypeId = elementId,
                Dimensions = dimensions,
                SizeUnits = size,
                Complete = complete,
                Reason = complete ? "" : "数组边界、步长或排列方式不受支持"
            };
        }
        return type with { Reason = $"不支持的 DWARF 类型标签 0x{n.Tag:X}" };
    }

    private static ScalarType MapScalar(long encoding, int bits) => (encoding, bits) switch
    {
        (2, 8 or 16 or 32 or 64) => ScalarType.Boolean,
        (4, 32) => ScalarType.Float32,
        (4, 64) => ScalarType.Float64,
        (5 or 6, 8) => ScalarType.Int8,
        (5 or 6, 16) => ScalarType.Int16,
        (5 or 6, 32) => ScalarType.Int32,
        (5 or 6, 64) => ScalarType.Int64,
        (7 or 8, 8) => ScalarType.Uint8,
        (7 or 8, 16) => ScalarType.Uint16,
        (7 or 8, 32) => ScalarType.Uint32,
        (7 or 8, 64) => ScalarType.Uint64,
        _ => ScalarType.Unknown
    };
}
