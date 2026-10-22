using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UniversalHost.Models;
using UniversalHost.Symbols;

namespace UniversalHost.Services;

public static partial class SymbolSelectionService
{
    [GeneratedRegex(@"\[(-?\d+)\]")]
    private static partial Regex Indices();

    public static string TypeName(FirmwareImage? image, TypeLayout? type) => type?.Kind switch
    {
        LayoutKind.Scalar or LayoutKind.Enumeration => type.Scalar.ToString(),
        LayoutKind.Structure => "结构体",
        LayoutKind.Union => "联合体",
        LayoutKind.Array => TypeName(image, image == null ? null : ElementType(image, type)) + "数组",
        LayoutKind.Pointer => "指针",
        _ => "Unknown"
    };

    public static IReadOnlyList<SymbolTableEntry> BuildRows(DeviceConfig device)
    {
        var rows = new List<SymbolTableEntry>();
        foreach (var root in device.Symbols.Items)
        {
            var image = FirmwareSymbolService.FindImage(device, root);
            TypeLayout? type = null;
            if (root.Definition != null) image?.Types.TryGetValue(root.Definition.TypeId, out type);
            var visiting = new HashSet<string>();
            Visit("", type, 0, true);

            void Visit(string path, TypeLayout? current, ulong? offset, bool canAdd)
            {
                var dimensions = ArrayDimensions(image, current);
                string lengths = string.Concat(dimensions.Select(d => $"[{d.Count?.ToString(CultureInfo.InvariantCulture) ?? "?"}]"));
                ulong? address = offset.HasValue && offset <= uint.MaxValue - (ulong)root.Address ? root.Address + offset : null;
                rows.Add(new(root, path, root.Name + path + lengths, TypeName(image, current), address,
                    current?.SizeUnits ?? (path.Length == 0 ? root.Size : null), canAdd && offset.HasValue &&
                    !dimensions.Any(d => d.Count == 0)));
                if (current == null || image == null || visiting.Count >= 64 || !visiting.Add(current.Id)) return;
                try
                {
                    if (current.Kind == LayoutKind.Array)
                    {
                        string elementPath = DefaultElementPath(image, current, path);
                        var element = ElementType(image, current);
                        if (dimensions.All(d => d.Count is > 0) && element?.Kind is LayoutKind.Structure or LayoutKind.Union)
                            Members(elementPath, element, offset);
                    }
                    else if (current.Kind is LayoutKind.Structure or LayoutKind.Union) Members(path, current, offset);
                }
                finally { visiting.Remove(current.Id); }
            }
            void Members(string path, TypeLayout parent, ulong? offset)
            {
                foreach (var member in parent.Members)
                {
                    if (member.Name.Length == 0) continue;
                    image!.Types.TryGetValue(member.TypeId, out var child);
                    ulong? memberOffset = offset.HasValue && member.OffsetUnits.HasValue && member.OffsetUnits <= ulong.MaxValue - offset
                        ? offset + member.OffsetUnits : null;
                    Visit(path + "." + member.Name, child, memberOffset, !member.BitField);
                }
            }
        }
        return rows;
    }

    private static IReadOnlyList<ArrayDimension> ArrayDimensions(FirmwareImage? image, TypeLayout? type)
    {
        var dimensions = new List<ArrayDimension>();
        var seen = new HashSet<string>();
        while (type?.Kind == LayoutKind.Array && seen.Add(type.Id))
        {
            dimensions.AddRange(type.Dimensions);
            if (image == null || !image.Types.TryGetValue(type.ElementTypeId, out type)) break;
        }
        return dimensions;
    }

    private static TypeLayout? ElementType(FirmwareImage image, TypeLayout type)
    {
        var seen = new HashSet<string>();
        while (type.Kind == LayoutKind.Array)
            if (!seen.Add(type.Id) || !image.Types.TryGetValue(type.ElementTypeId, out type!)) return null;
        return type;
    }

    private static string DefaultElementPath(FirmwareImage image, TypeLayout type, string path)
    {
        var seen = new HashSet<string>();
        while (type.Kind == LayoutKind.Array && seen.Add(type.Id))
        {
            path += string.Concat(type.Dimensions.Select(d => $"[{d.LowerBound.ToString(CultureInfo.InvariantCulture)}]"));
            if (!image.Types.TryGetValue(type.ElementTypeId, out type!)) break;
        }
        return path;
    }

    public static UserSymbolInfo CreateAccess(DeviceConfig device, SymbolTableEntry row)
    {
        FirmwareSymbolService.EnsureLayoutEditable();
        if (!row.CanAdd) throw new InvalidOperationException("此成员的位置不确定或为位域，不能直接添加");
        var image = FirmwareSymbolService.FindImage(device, row.Root);
        var definition = row.Root.Definition;
        string path = row.Path;
        if (image != null && definition != null)
        {
            var objectAccess = AccessPathResolver.ResolveObject(image, definition, path);
            if (image.Types.TryGetValue(objectAccess.TypeId, out var type)) path = DefaultElementPath(image, type, path);
            var scalar = AccessPathResolver.Resolve(image, definition, path);
            if (scalar.Status == AccessStatus.Verified)
            {
                var automatic = FirmwareSymbolService.CreateAccess(device, row.Root, path, false);
                automatic.InputKind = Indices().IsMatch(path) ? SymbolInputKind.ArrayIndex : SymbolInputKind.Offset;
                return automatic;
            }
        }
        var manual = FirmwareSymbolService.CreateAccess(device, row.Root, "", true);
        manual.MemberPath = path;
        manual.Name = row.Root.Name + path;
        manual.Offset = checked((uint)((row.Address ?? row.Root.Address) - row.Root.Address));
        manual.InputKind = Indices().IsMatch(path) ? SymbolInputKind.ArrayIndex : SymbolInputKind.Offset;
        if (manual.InputKind == SymbolInputKind.ArrayIndex && image != null && definition != null)
        {
            var element = AccessPathResolver.ResolveObject(image, definition, path);
            if (element.Status != AccessStatus.Verified) throw new InvalidOperationException(element.Reason);
            manual.Access = element;
        }
        return manual;
    }

    public static bool CanEditInput(DeviceConfig device, UserSymbolInfo symbol)
    {
        var image = FirmwareSymbolService.FindImage(device, symbol);
        if (symbol.Definition != null && image != null && image.Types.TryGetValue(symbol.Definition.TypeId, out var root)
            && root.Kind is LayoutKind.Scalar or LayoutKind.Enumeration) return false;
        return symbol.Mode != AccessMode.Automatic || Indices().IsMatch(symbol.MemberPath);
    }

    public static IReadOnlyList<(long Value, long Minimum, long Maximum)> GetInputValues(
        DeviceConfig device, UserSymbolInfo symbol, SymbolInputKind kind)
    {
        if (kind == SymbolInputKind.Offset)
        {
            var value = symbol.Mode == AccessMode.Automatic ? symbol.Access?.OffsetUnits ?? symbol.Offset : symbol.Offset;
            long maximum = Math.Min(uint.MaxValue - symbol.Address, symbol.Size > 0 ? symbol.Size - 1 : uint.MaxValue);
            return [(checked((long)value), 0, maximum)];
        }
        var (image, root, template) = ArrayInputContext(device, symbol);
        var dimensions = AccessPathResolver.GetArrayDimensions(image, root, template);
        var matches = Indices().Matches(template);
        return dimensions.Select((dimension, index) => (
            long.Parse(matches[index].Groups[1].Value, CultureInfo.InvariantCulture),
            dimension.LowerBound,
            (long)Math.Min(long.MaxValue, (decimal)dimension.LowerBound + dimension.Count!.Value - 1))).ToArray();
    }

    public static void ApplyInput(DeviceConfig device, UserSymbolInfo symbol, SymbolInputKind kind, IReadOnlyList<long> values)
    {
        FirmwareSymbolService.EnsureLayoutEditable();
        if (!CanEditInput(device, symbol)) throw new InvalidOperationException("基本类型变量的偏移量/下标不可编辑");
        if (kind == SymbolInputKind.Offset)
        {
            if (values.Count != 1 || values[0] < 0 || values[0] > uint.MaxValue)
                throw new InvalidOperationException("偏移量超出目标地址范围");
            uint offset = (uint)values[0];
            if ((ulong)symbol.Address + offset > uint.MaxValue || (symbol.Size > 0 && offset >= symbol.Size))
                throw new InvalidOperationException("偏移量超出根变量范围");
            // Initial binding does not call ApplyInput. An explicit offset edit
            // is the user's request for manual access, including an equal value.
            FirmwareSymbolService.MakeManual(symbol);
            symbol.Offset = offset;
            symbol.InputKind = kind;
            return;
        }
        var (image, root, template) = ArrayInputContext(device, symbol);
        int count = Indices().Matches(template).Count;
        if (values.Count != count) throw new InvalidOperationException($"请填写全部 {count} 维下标");
        int i = 0;
        string path = Indices().Replace(template, _ => "[" + values[i++].ToString(CultureInfo.InvariantCulture) + "]");
        var access = AccessPathResolver.ResolveObject(image, root, path);
        if (access.Status != AccessStatus.Verified) throw new InvalidOperationException(access.Reason);
        if (access.Scalar != ScalarType.Unknown) FirmwareSymbolService.ChangePath(device, symbol, path);
        else
        {
            FirmwareSymbolService.MakeManual(symbol);
            symbol.MemberPath = path;
            symbol.Name = root.Name + path;
            symbol.Offset = checked((uint)access.OffsetUnits);
            symbol.Access = access;
        }
        symbol.InputKind = kind;
        symbol.RefreshAccessDisplay();
    }

    private static (FirmwareImage Image, FirmwareSymbol Root, string Template) ArrayInputContext(DeviceConfig device, UserSymbolInfo symbol)
    {
        var image = FirmwareSymbolService.FindImage(device, symbol) ?? throw new InvalidOperationException("缺少数组类型信息，请更新符号表");
        var root = symbol.Definition ?? throw new InvalidOperationException("缺少根变量信息");
        string template = symbol.MemberPath;
        if (!Indices().IsMatch(template) && image.Types.TryGetValue(root.TypeId, out var type))
            template = DefaultElementPath(image, type, template);
        if (!Indices().IsMatch(template)) throw new InvalidOperationException("此变量没有可编辑的数组下标，请使用偏移量");
        return (image, root, template);
    }
}
