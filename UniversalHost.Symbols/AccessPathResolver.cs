using System.Globalization;
using System.Text.RegularExpressions;

namespace UniversalHost.Symbols;

public static partial class AccessPathResolver
{
    [GeneratedRegex(@"\G(?:\.([\p{L}_][\p{L}\p{N}_]*)|\[(-?\d+)\])", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();

    public static AccessResolution Resolve(FirmwareImage image, FirmwareSymbol root, string path)
        => ResolveCore(image, root, path, true);

    public static AccessResolution ResolveObject(FirmwareImage image, FirmwareSymbol root, string path)
        => ResolveCore(image, root, path, false);

    public static IReadOnlyList<ArrayDimension> GetArrayDimensions(FirmwareImage image, FirmwareSymbol root, string path)
    {
        var dimensions = new List<ArrayDimension>();
        var access = ResolveCore(image, root, path, false, dimensions);
        if (access.Status != AccessStatus.Verified) throw new InvalidOperationException(access.Reason);
        return dimensions;
    }

    private static AccessResolution ResolveCore(FirmwareImage image, FirmwareSymbol root, string path, bool requireScalar,
        List<ArrayDimension>? dimensions = null)
    {
        AccessResolution Fail(AccessStatus status, string reason) => new() { Status = status, Reason = reason, Path = path };
        if (path.Length > 1024) return Fail(AccessStatus.InvalidPath, "路径长度超过限制");
        if (root.Status != AccessStatus.Verified) return Fail(root.Status, root.Reason);
        if (!image.Target.Linked || image.Target.AddressUnitBits is not (8 or 16)) return Fail(AccessStatus.Unverified, "没有确定的目标地址模型");
        if (!image.Types.TryGetValue(root.TypeId, out var type)) return Fail(AccessStatus.Unverified, "缺少类型定义");
        try
        {
            ulong offset = 0;
            int pos = 0, dimension = 0, segments = 0;
            // Every aggregate contributes a bound, not only the outer symbol.
            var bounds = new List<(ulong Start, ulong Size)>();
            if (root.SizeUnits > 0) bounds.Add((0, root.SizeUnits));
            if (type.SizeUnits.HasValue) bounds.Add((0, type.SizeUnits.Value));
            while (pos < path.Length)
            {
                if (++segments > 64) return Fail(AccessStatus.InvalidPath, "路径层数超过限制");
                var match = SegmentPattern().Match(path, pos);
                if (!match.Success) return Fail(AccessStatus.InvalidPath, "使用 .成员名 和 [下标]，不支持指针解引用");
                pos += match.Length;
                if (match.Groups[1].Success)
                {
                    if (type.Kind is not (LayoutKind.Structure or LayoutKind.Union)) return Fail(AccessStatus.Unsupported, "此层不是结构体或联合体");
                    var members = type.Members.Where(m => m.Name == match.Groups[1].Value).ToList();
                    if (members.Count == 0) return Fail(type.Complete ? AccessStatus.MissingMember : AccessStatus.Unverified,
                        type.Complete ? $"成员 {match.Groups[1].Value} 已不存在" : "类型布局不完整，不能确认成员已删除");
                    if (members.Count != 1) return Fail(AccessStatus.Conflict, "成员名称不唯一");
                    var member = members[0];
                    if (member.BitField || !member.OffsetUnits.HasValue) return Fail(AccessStatus.Unsupported, member.Reason);
                    offset = checked(offset + member.OffsetUnits.Value);
                    if (!image.Types.TryGetValue(member.TypeId, out type)) return Fail(AccessStatus.Unverified, "缺少成员类型定义");
                }
                else
                {
                    if (type.Kind != LayoutKind.Array || dimension >= type.Dimensions.Count) return Fail(AccessStatus.Unsupported, "此层不是数组或维度不完整");
                    var dim = type.Dimensions[dimension];
                    if (!long.TryParse(match.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
                        return Fail(AccessStatus.InvalidPath, "下标超出整数范围");
                    if (!dim.Count.HasValue || !dim.StrideUnits.HasValue || !type.Complete) return Fail(AccessStatus.Unverified, "数组长度或步长不完整");
                    if (index < dim.LowerBound || checked((ulong)(index - dim.LowerBound)) >= dim.Count.Value)
                        return Fail(AccessStatus.OutOfBounds, $"下标 {index} 越界，起点 {dim.LowerBound}，长度 {dim.Count}");
                    dimensions?.Add(dim);
                    offset = checked(offset + checked((ulong)(index - dim.LowerBound) * dim.StrideUnits.Value));
                    dimension++;
                    if (dimension < type.Dimensions.Count) continue;
                    dimension = 0;
                    if (!image.Types.TryGetValue(type.ElementTypeId, out type)) return Fail(AccessStatus.Unverified, "缺少数组元素类型");
                }
                if (type.SizeUnits.HasValue) bounds.Add((offset, type.SizeUnits.Value));
            }
            if (dimension != 0) return Fail(AccessStatus.Unsupported, "请填写数组的所有维度下标");
            bool scalar = type.Kind is LayoutKind.Scalar or LayoutKind.Enumeration;
            if (!type.Complete || (requireScalar && (!scalar || type.Scalar == ScalarType.Unknown)))
                return Fail(AccessStatus.Unsupported, type.Reason.Length > 0 ? type.Reason : "请选择最终的标量成员或数组元素");
            ulong size = type.SizeUnits ?? 0;
            if (size == 0 || (scalar && type.StorageBits != checked((int)size * image.Target.AddressUnitBits))) return Fail(AccessStatus.Conflict, "存储位宽与地址单位不一致");
            ulong end = checked(offset + size);
            if (bounds.Any(b => offset < b.Start || end > checked(b.Start + b.Size))) return Fail(AccessStatus.Conflict, "访问超出符号或成员存储边界");
            ulong address = checked(root.Address + offset);
            if (address > uint.MaxValue || checked(address + size - 1) > uint.MaxValue) return Fail(AccessStatus.Conflict, "地址超过当前 XCP 32 位地址范围");
            return new AccessResolution
            {
                Status = AccessStatus.Verified,
                Path = path,
                Address = address,
                OffsetUnits = offset,
                SizeUnits = size,
                Scalar = type.Scalar,
                StorageBits = type.StorageBits,
                TypeId = type.Id
            };
        }
        catch (OverflowException) { return Fail(AccessStatus.Conflict, "地址、下标或类型大小溢出"); }
    }

}
