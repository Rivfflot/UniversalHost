using System;
using System.Buffers.Binary;

namespace UniversalHost.Models;

public enum SerialProtocol : byte
{
    Xcp = 0x00,
    Iap = 0x01
}

public static class SerialTransportProtocol
{
    // 固定上限，不根据接收帧中尚未验证的 LEN 分配内存。
    public const int MaxXcpRawLength = ushort.MaxValue + 7;
    public const int MaxIapRawLength = IapProtocol.MaxBytesPerFrame + 11;

    public static int GetMaxRawLength(SerialProtocol protocol) => protocol switch
    {
        SerialProtocol.Xcp => MaxXcpRawLength,
        SerialProtocol.Iap => MaxIapRawLength,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    public static int GetMaxWireLength(int rawLength) => Cobs.GetMaxEncodedLength(rawLength) + 1;

    public static int Encode(SerialProtocol protocol, ReadOnlySpan<byte> packet,
        Span<byte> rawBuffer, Span<byte> wireBuffer)
    {
        int rawLength;
        if (protocol == SerialProtocol.Xcp)
        {
            if (packet.Length < 5 || packet.Length > ushort.MaxValue + 4 ||
                BinaryPrimitives.ReadUInt16LittleEndian(packet) != packet.Length - 4)
                throw new ArgumentException("串口每帧必须包含一个完整 XCP 包", nameof(packet));
            rawLength = packet.Length + 3;
            if (rawBuffer.Length < rawLength)
                throw new ArgumentException("串口原始帧缓存不足", nameof(rawBuffer));
            rawBuffer[0] = (byte)SerialProtocol.Xcp;
            packet.CopyTo(rawBuffer[1..]);
            BinaryPrimitives.WriteUInt16BigEndian(rawBuffer.Slice(rawLength - 2, 2),
                Crc.Crc16Modbus.Calculate(rawBuffer[..(rawLength - 2)]));
        }
        else if (protocol == SerialProtocol.Iap)
        {
            if (!TryValidate(packet, protocol, out _, out _))
                throw new ArgumentException("串口 IAP 包长度或 CRC16 无效", nameof(packet));
            rawLength = packet.Length;
            if (rawBuffer.Length < rawLength)
                throw new ArgumentException("串口原始帧缓存不足", nameof(rawBuffer));
            packet.CopyTo(rawBuffer);
        }
        else
            throw new ArgumentOutOfRangeException(nameof(protocol));

        if (wireBuffer.Length < GetMaxWireLength(rawLength))
            throw new ArgumentException("串口线上帧缓存不足", nameof(wireBuffer));
        int length = Cobs.Encode(rawBuffer[..rawLength], wireBuffer);
        wireBuffer[length] = 0;
        return length + 1;
    }

    public static bool TryValidate(ReadOnlySpan<byte> raw, SerialProtocol protocol,
        out int packetOffset, out int packetLength)
    {
        packetOffset = 0;
        packetLength = 0;
        if (raw.IsEmpty || raw[0] != (byte)protocol || raw.Length > GetMaxRawLength(protocol))
            return false;
        if (protocol == SerialProtocol.Xcp)
        {
            if (raw.Length < 8)
                return false;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(raw[1..3]);
            if (length < 1 || raw.Length != length + 7)
                return false;
            packetOffset = 1;
            packetLength = raw.Length - 3;
        }
        else
        {
            if (raw.Length < 7 || raw.Length != BinaryPrimitives.ReadUInt16BigEndian(raw[2..4]) + 7)
                return false;
            packetLength = raw.Length;
        }
        return BinaryPrimitives.ReadUInt16BigEndian(raw[^2..]) == Crc.Crc16Modbus.Calculate(raw[..^2]);
    }
}
