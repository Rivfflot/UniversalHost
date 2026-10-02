using System;
using System.Buffers.Binary;
using System.IO;

namespace UniversalHost.Models;

public class IapProtocol
{
    public enum Stage : byte
    {
        Handshake = 0x00,
        SendInformation = 0x01,
        SendData = 0x02,
        SendComplete = 0x03,
        StartErase = 0x04,
        StartWrite = 0x05,
        StartCheck = 0x06,
        Reboot = 0x07
    }

    // 可忽略或重试的响应；设备明确拒绝当前请求时直接抛出异常，停止升级。
    public enum Status
    {
        Success,
        HostCheckError,
        StageError,
        LengthError,
        FunctionCodeError,
        DeviceIDError,
        FrameIndexMismatch,
        DeviceFrameCheckError,
        DeviceLengthError
    }

    public const int MaxBytesPerFrame = 1372;
    private const byte IAP_FUNCTION_CODE = 0x01;
    private readonly string _iapFilePath;
    private readonly byte _deviceID;
    private byte[]? _binData;
    private uint _fileCrc32;
    private ushort _bytesPerFrame;

    public uint FrameNum { get; private set; }
    public bool IsFlashPerFrame { get; private set; }

    public IapProtocol(string iapFilePath, byte deviceID)
    {
        _iapFilePath = iapFilePath;
        _deviceID = deviceID;
    }

    public void ReadFile()
    {
        using var fs = new FileStream(_iapFilePath, FileMode.Open, FileAccess.Read);
        if (fs.Length == 0)
            throw new InvalidDataException("所选 BIN 文件为空，无法执行 IAP 升级");
        if (fs.Length > Array.MaxLength)
            throw new InvalidDataException("所选 BIN 文件过大，超出上位机支持的范围");

        _binData = new byte[(int)fs.Length];
        fs.ReadExactly(_binData);
        _fileCrc32 = Crc.Crc32.Calculate(_binData);
        _bytesPerFrame = 0;
        FrameNum = 0;
        IsFlashPerFrame = false;
    }

    public int GetSendPacket(Span<byte> buffer, Stage stage, uint sendFrameIndex = 0)
    {
        int payloadLength;
        int romOffset = 0;
        switch (stage)
        {
            case Stage.Handshake:
            case Stage.SendComplete:
            case Stage.StartErase:
            case Stage.StartWrite:
            case Stage.StartCheck:
            case Stage.Reboot:
                payloadLength = 2;
                break;
            case Stage.SendInformation:
            case Stage.SendData:
                if (_binData == null || _bytesPerFrame == 0)
                    throw new InvalidOperationException("IAP 必须先读取 BIN 并完成握手");
                if (stage == Stage.SendInformation)
                {
                    payloadLength = 12;
                }
                else
                {
                    if (sendFrameIndex >= FrameNum)
                        throw new ArgumentOutOfRangeException(nameof(sendFrameIndex));
                    romOffset = checked((int)((long)sendFrameIndex * _bytesPerFrame));
                    payloadLength = Math.Min(_bytesPerFrame, _binData.Length - romOffset) + 4;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage));
        }

        int frameLength = payloadLength + 7;
        if (buffer.Length < frameLength)
            throw new ArgumentException("IAP 发送缓存长度不足", nameof(buffer));
        var packet = buffer[..frameLength];
        packet[0] = IAP_FUNCTION_CODE;
        packet[1] = (byte)stage;
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..4], (ushort)payloadLength);
        packet[4] = _deviceID;
        var payload = packet.Slice(5, payloadLength);
        switch (stage)
        {
            case Stage.Handshake:
                payload.Fill(0xAA);
                break;
            case Stage.SendInformation:
                BinaryPrimitives.WriteUInt32BigEndian(payload[..4], FrameNum);
                BinaryPrimitives.WriteUInt32BigEndian(payload[4..8], (uint)_binData!.Length);
                BinaryPrimitives.WriteUInt32BigEndian(payload[8..12], _fileCrc32);
                break;
            case Stage.SendData:
                BinaryPrimitives.WriteUInt32BigEndian(payload[..4], sendFrameIndex);
                _binData!.AsSpan(romOffset, payloadLength - 4).CopyTo(payload[4..]);
                break;
            default:
                payload.Clear();
                break;
        }

        BinaryPrimitives.WriteUInt16BigEndian(packet[^2..], Crc.Crc16Modbus.Calculate(packet[..^2]));
        return frameLength;
    }

    public Status ReceivePacketAnalysis(Stage stage, ReadOnlySpan<byte> data, uint sendFrameIndex = 0)
    {
        if (data.Length < 7)
            return Status.LengthError;
        // 非目标从站的响应（包括通用错误）不能影响当前升级。
        if (data[4] != _deviceID)
            return Status.DeviceIDError;
        if (BinaryPrimitives.ReadUInt16BigEndian(data[2..4]) + 7 != data.Length)
            return Status.LengthError;
        if (BinaryPrimitives.ReadUInt16BigEndian(data[^2..]) != Crc.Crc16Modbus.Calculate(data[..^2]))
            return Status.HostCheckError;
        if (data[0] != IAP_FUNCTION_CODE)
            return Status.FunctionCodeError;

        var payload = data[5..^2];
        if (data[1] == 0xFF)
        {
            if (payload.Length != 2)
                return Status.LengthError;
            return BinaryPrimitives.ReadUInt16BigEndian(payload) switch
            {
                0xFF00 => Status.DeviceFrameCheckError,
                0xFD00 => Status.DeviceLengthError,
                0xFE00 => throw new InvalidOperationException("IAP 设备拒绝功能码（0xFE00）"),
                0xFC00 => throw new InvalidOperationException("IAP 设备拒绝阶段码（0xFC00）"),
                var result => throw new InvalidOperationException($"IAP 设备返回未知通用错误（0x{result:X4}）")
            };
        }
        if (data[1] != (byte)stage)
            return Status.StageError;

        switch (stage)
        {
            case Stage.Handshake:
                return ReceiveHandshake(payload);
            case Stage.SendInformation:
                if (payload.Length != 1)
                    return Status.LengthError;
                return payload[0] switch
                {
                    0x00 => Status.Success,
                    0xF0 => throw new InvalidOperationException("IAP ROM 长度无效或超过设备容量，请检查所选 BIN 文件（0xF0）"),
                    0xF1 => throw new InvalidOperationException("IAP ROM 总帧数异常，请重新握手开始升级（0xF1）"),
                    0xF2 => throw new InvalidOperationException("IAP ROM 信息与当前会话不一致，请重新握手开始升级（0xF2）"),
                    var result => throw new InvalidOperationException($"IAP 信息请求被拒绝（0x{result:X2}）")
                };
            case Stage.SendData:
                if (payload.Length != 5)
                    return Status.LengthError;
                if (BinaryPrimitives.ReadUInt32BigEndian(payload[..4]) != sendFrameIndex)
                    return Status.FrameIndexMismatch;
                return payload[4] switch
                {
                    0x00 => Status.Success,
                    0x01 => throw new InvalidOperationException($"IAP 第 {sendFrameIndex} 帧帧号错误（0x01）"),
                    0x02 => throw new InvalidOperationException($"IAP 第 {sendFrameIndex} 帧写入失败，请重新握手开始升级（0x02）"),
                    var result => throw new InvalidOperationException($"IAP 第 {sendFrameIndex} 帧请求被拒绝（0x{result:X2}）")
                };
            case Stage.SendComplete:
            case Stage.StartErase:
            case Stage.StartWrite:
            case Stage.StartCheck:
            case Stage.Reboot:
                if (payload.Length != 2)
                    return Status.LengthError;
                return ReceiveOperationResult(stage, BinaryPrimitives.ReadUInt16BigEndian(payload));
            default:
                return Status.StageError;
        }
    }

    private Status ReceiveHandshake(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 2)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(payload) switch
            {
                0xFFFF => throw new InvalidOperationException("IAP 设备忙，当前无法升级"),
                0x0104 => throw new InvalidOperationException("IAP 握手参数无效（0x0104）"),
                var result => throw new InvalidDataException($"IAP 握手确认无效（0x{result:X4}）")
            };
        }
        if (payload.Length != 4 || payload[0] != 0x00)
            throw new InvalidDataException("IAP 握手确认格式无效");

        ushort bytesPerFrame = BinaryPrimitives.ReadUInt16BigEndian(payload[2..4]);
        if (bytesPerFrame == 0 || bytesPerFrame > MaxBytesPerFrame)
            throw new InvalidDataException($"IAP 每帧字节数不支持：{bytesPerFrame}，有效范围为 1～{MaxBytesPerFrame}");
        if (_binData == null)
            throw new InvalidOperationException("IAP 文件尚未读取");

        // bit0 以外均为保留位，不再读取旧协议的重启标志。
        IsFlashPerFrame = (payload[1] & 0x01) != 0;
        _bytesPerFrame = bytesPerFrame;
        FrameNum = (uint)((_binData.Length + (long)bytesPerFrame - 1) / bytesPerFrame);
        return Status.Success;
    }

    private static Status ReceiveOperationResult(Stage stage, ushort result)
    {
        if (result == 0x00FF)
            return Status.Success;

        string reason = result switch
        {
            0x0100 => "帧数或接收长度错误",
            0x0101 => "BIN 接收 CRC32 或目标回读 CRC32 错误",
            0x0102 => "存储、擦除、写入、回读或启动目标提交失败",
            0x0103 => "设备当前状态不允许该请求",
            0x0104 => "请求参数无效",
            _ => "未知操作结果"
        };
        throw new InvalidOperationException($"IAP 阶段 {stage} 失败：{reason}（0x{result:X4}），请检查设备并重新握手开始升级");
    }
}
