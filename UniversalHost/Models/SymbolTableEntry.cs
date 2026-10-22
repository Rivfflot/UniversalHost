namespace UniversalHost.Models;

/// <summary>Symbol-table presentation only; saved identities always refer to Root.</summary>
public sealed record SymbolTableEntry(SymbolInfo Root, string Path, string Name, string TypeName,
    ulong? Address, ulong? Size, bool CanAdd = true)
{
    public string AddressHex => Address.HasValue ? $"0x{Address:X8}" : "";
    public string SourceFileName => Root.SourceFileName;
}
