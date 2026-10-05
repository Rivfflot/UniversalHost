using System;

namespace UniversalHost.Models;

/// <summary>普通 COBS；发送时保留末尾结束块，分隔符由传输层添加。</summary>
public static class Cobs
{
    public static int GetMaxEncodedLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return checked(length + length / 254 + 1);
    }

    public static int Encode(ReadOnlySpan<byte> data, Span<byte> output)
    {
        if (output.Length < GetMaxEncodedLength(data.Length))
            throw new ArgumentException("COBS 编码缓存不足", nameof(output));

        int codeOffset = 0;
        int offset = 1;
        byte code = 1;
        foreach (byte value in data)
        {
            if (value == 0)
            {
                output[codeOffset] = code;
                codeOffset = offset++;
                code = 1;
            }
            else
            {
                output[offset++] = value;
                if (++code == 0xFF)
                {
                    output[codeOffset] = code;
                    codeOffset = offset++;
                    code = 1;
                }
            }
        }
        output[codeOffset] = code;
        return offset;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, Span<byte> output, out int length)
    {
        length = 0;
        if (data.IsEmpty)
            return false;
        int offset = 0;
        while (offset < data.Length)
        {
            byte code = data[offset++];
            int count = code - 1;
            if (code == 0 || count > data.Length - offset || count > output.Length - length)
                return false;
            var block = data.Slice(offset, count);
            if (block.Contains((byte)0))
                return false;
            block.CopyTo(output[length..]);
            offset += count;
            length += count;
            if (code != 0xFF && offset < data.Length)
            {
                if (length == output.Length)
                    return false;
                output[length++] = 0;
            }
        }
        return true;
    }
}
