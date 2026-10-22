using System;
using System.Collections.Generic;
using System.Linq;
using UniversalHost.Models;
using UniversalHost.Symbols;

namespace UniversalHost.Services;

public sealed record PreparedCalibration(SymbolRuntime Runtime, uint Address, byte[] Bytes, string OldValue, string NewValue);

/// <summary>Pure preflight: no transport calls and no changes to pending values.</summary>
public static class CalibrationPreflight
{
    public static void ValidateAccess(DeviceConfig device, SymbolRuntime runtime, int granularityBytes, bool littleEndian)
    {
        var s = runtime.Symbol;
        if (!runtime.LayoutMatches) throw new InvalidOperationException($"{s.Name}: 运行时布局已过期");
        if (s.DataType == ScalarType.Unknown) throw new InvalidOperationException($"{s.Name}: 类型未知");
        if (granularityBytes is not (1 or 2 or 4)) throw new InvalidOperationException("未知 XCP 地址粒度");
        if (s.Mode == AccessMode.Automatic)
        {
            if (s.VerificationStatus != AccessStatus.Verified || s.Definition == null || s.Access == null)
                throw new InvalidOperationException($"{s.Name}: 自动访问未通过验证；{s.VerificationReason}");
            var image = FirmwareSymbolService.FindImage(device, s) ?? throw new InvalidOperationException($"{s.Name}: 缺少保存的类型快照");
            if (!image.Symbols.Contains(s.Definition)) throw new InvalidOperationException($"{s.Name}: 根变量身份已过期");
            var actual = AccessPathResolver.Resolve(image, s.Definition, s.MemberPath);
            if (actual.Status != AccessStatus.Verified || actual != s.Access ||
                actual.StorageBits != s.StorageBits ||
                actual.Scalar != s.DataType || s.Offset != 0 || s.Address != s.Definition.Address || s.Target != image.Target ||
                runtime.ValueSizeInBytes * 8 != actual.StorageBits)
                throw new InvalidOperationException($"{s.Name}: 自动路径的类型、位宽或地址与验证快照不一致");
            if (s.Target.AddressUnitBits != granularityBytes * 8 || s.Target.LittleEndian != littleEndian)
                throw new InvalidOperationException($"{s.Name}: 固件地址单位或端序与 XCP 连接信息冲突");
        }
        else if (s.Target.AddressUnitBits != 0 && s.Target.AddressUnitBits != granularityBytes * 8)
            throw new InvalidOperationException($"{s.Name}: 手动偏移地址单位与 XCP 不一致");

        ulong address = s.ActualAddress;
        // The existing XCP encoder pads sub-granularity values. Validate the
        // complete write interval including padding before any request is sent.
        ulong units = (ulong)Math.Max(1, (runtime.ValueSizeInBytes + granularityBytes - 1) / granularityBytes);
        if (s.Mode == AccessMode.Manual && s.InputKind == SymbolInputKind.ArrayIndex)
        {
            var image = FirmwareSymbolService.FindImage(device, s);
            var element = image != null && s.Definition != null && image.Symbols.Contains(s.Definition)
                ? AccessPathResolver.ResolveObject(image, s.Definition, s.MemberPath) : null;
            if (element?.Status != AccessStatus.Verified || element.Address != address || units > element.SizeUnits ||
                element != s.Access)
                throw new InvalidOperationException($"{s.Name}: 数组元素地址或访问边界不一致");
        }
        if (checked(address + units - 1) > uint.MaxValue) throw new InvalidOperationException($"{s.Name}: 地址溢出");
        if (s.Size > 0 && (address < s.Address || checked(address + units) > (ulong)s.Address + s.Size))
            throw new InvalidOperationException($"{s.Name}: 访问超出根符号边界");
        if (s.Mode == AccessMode.Automatic && units != s.Access!.SizeUnits)
            throw new InvalidOperationException($"{s.Name}: XCP 对齐填充超出自动成员边界");
    }

    public static IReadOnlyList<PreparedCalibration> Prepare(DeviceConfig device, IEnumerable<SymbolRuntime> runtimes,
        int granularityBytes, bool littleEndian)
    {
        var prepared = new List<PreparedCalibration>();
        var intervals = new List<(ulong Start, ulong End, string Name)>();
        foreach (var runtime in runtimes)
        {
            ValidateAccess(device, runtime, granularityBytes, littleEndian);
            var bytes = runtime.EncodeValue(runtime.ValueString);
            if (bytes.Length < granularityBytes) Array.Resize(ref bytes, granularityBytes);
            ulong start = runtime.Symbol.ActualAddress;
            ulong end = checked(start + (ulong)Math.Max(1, (bytes.Length + granularityBytes - 1) / granularityBytes));
            if (intervals.Any(i => start < i.End && i.Start < end))
                throw new InvalidOperationException($"{runtime.Symbol.Name}: 本批标定地址区间重叠（包括联合体成员和不同来源文件）");
            intervals.Add((start, end, runtime.Symbol.Name));
            prepared.Add(new(runtime, (uint)start, bytes, runtime.ValueToStringWithoutUpdate(), runtime.ValueString));
        }
        return prepared;
    }
}
