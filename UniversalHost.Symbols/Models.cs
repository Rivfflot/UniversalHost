namespace UniversalHost.Symbols;

// Numeric values are persisted in project files; keep their existing meanings.
public enum ScalarType
{
    Int8 = 0,
    Uint8 = 1,
    Int16 = 2,
    Uint16 = 3,
    Int32 = 4,
    Uint32 = 5,
    Int64 = 6,
    Uint64 = 7,
    Float32 = 8,
    Float64 = 9,
    Boolean = 10,
    Unknown = 11
}
public enum LayoutKind { Unsupported, Scalar, Enumeration, Array, Structure, Union, Pointer }
public enum AccessStatus { Unverified, Verified, Conflict, Unsupported, MissingMember, OutOfBounds, InvalidPath }
public enum AccessMode { Manual, Automatic }

public sealed record TargetInfo
{
    public string Format { get; init; } = "Unknown";
    public string Platform { get; init; } = "Unknown";
    public string Abi { get; init; } = "Unknown";
    public int AddressUnitBits { get; init; }
    public int AddressBits { get; init; }
    public bool LittleEndian { get; init; }
    public bool Linked { get; init; }
}

public sealed record ArrayDimension(long LowerBound, ulong? Count, ulong? StrideUnits);
public sealed record MemberLayout(string Name, string TypeId, ulong? OffsetUnits, bool BitField, string Reason = "");

// References use IDs rather than nested objects: recursive types remain finite
// and the exact same graph can be persisted without a serializer-specific model.
public sealed record TypeLayout
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public LayoutKind Kind { get; init; }
    public ScalarType Scalar { get; init; } = ScalarType.Unknown;
    public int StorageBits { get; init; }
    public ulong? SizeUnits { get; init; }
    public string ElementTypeId { get; init; } = "";
    public List<ArrayDimension> Dimensions { get; init; } = [];
    public List<MemberLayout> Members { get; init; } = [];
    public bool Complete { get; init; }
    public string Reason { get; init; } = "";
}

public sealed record FirmwareSymbol
{
    public string Name { get; init; } = "";
    public string LinkageName { get; init; } = "";
    public string TypeId { get; init; } = "";
    public ulong Address { get; init; }
    public ulong SizeUnits { get; init; }
    public AccessStatus Status { get; init; }
    public string Reason { get; init; } = "未检测到变量类型调试信息";
}

public sealed record FirmwareImage
{
    public string SourcePath { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Backend { get; init; } = "";
    public TargetInfo Target { get; init; } = new();
    public List<FirmwareSymbol> Symbols { get; init; } = [];
    // Transient parser diagnostics, never selectable or stored in project files.
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> ExcludedSymbolNames { get; init; } = [];
    public Dictionary<string, TypeLayout> Types { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public List<int> DwarfVersions { get; init; } = [];
    public bool SymbolsComplete { get; init; }
    public bool TypesComplete { get; init; }
}

public sealed record AccessResolution
{
    public AccessStatus Status { get; init; }
    public string Reason { get; init; } = "";
    public string Path { get; init; } = "";
    public ulong Address { get; init; }
    public ulong OffsetUnits { get; init; }
    public ulong SizeUnits { get; init; }
    public ScalarType Scalar { get; init; } = ScalarType.Unknown;
    public int StorageBits { get; init; }
    public string TypeId { get; init; } = "";
    public bool IsDefinitivelyInvalid => Status is AccessStatus.MissingMember or AccessStatus.OutOfBounds;
}

public sealed record ParseOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxOutputBytes { get; init; } = 128 * 1024 * 1024;
    public long MaxWorkerMemoryBytes { get; init; } = 1024L * 1024 * 1024;
    // Intended for tests and explicit administrator overrides, never PATH lookup.
    public string? WorkerPath { get; init; }
}
