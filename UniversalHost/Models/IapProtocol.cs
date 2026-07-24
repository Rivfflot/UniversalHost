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
        SendComplete = 0x03
    }

    public enum Status
    {
        Success,
        HostCheckError,
        StageError,
        LengthError,
        DeviceBusy,
        DeviceIDError,
        InformationError,
        DeviceFrameCheckError,
        DeviceFunctionCodeError,
        DeviceStationAddressError,
        DeviceFrameCountError,
        DeviceReceiveFrameIndexError,
        DeviceFlashCheckError,
        DeviceStartErase,
        DeviceStartWrite,
        DeviceStartFalshCheck,
        DeviceStartReboot,
        DeviceRebootComplete
    }

    private const byte IAP_FUNCTION_CODE = 0x01;

    private const byte IAP_HANDSHAKE_INFORMATION = 0xAA;

    private readonly IapConfig _iapConfig;
    private readonly DeviceConfig _deviceConfig;

    private byte[]? readBinData;
    private UInt32 fileCrc32 = 0;
    public UInt32 FrameNum { get; private set; } = 0;
    public bool IsRebootBeforeIapRequired { get; private set; } = false;
    public bool IsFlashPerFrame { get; private set; } = false;
    public IapProtocol(IapConfig iapConfig, DeviceConfig deviceConfig)
    {
        _iapConfig = iapConfig;
        _deviceConfig = deviceConfig;
    }

    public void ReadFile()
    {
        using (FileStream fs = new FileStream(_iapConfig.IapFilePath, FileMode.Open, FileAccess.Read))
        {
            int fileLen = (int)fs.Length;

            FrameNum = (uint)Math.Ceiling((double)fileLen / _iapConfig.BytesPerFrame);
            readBinData = new byte[fileLen];
            fs.ReadExactly(readBinData, 0, fileLen);
            // 计算 Crc
            fileCrc32 = Crc.Crc32.Calculate(readBinData);
        }
    }
    public int GetSendPacket(Span<byte> buffer, Stage stage, UInt32 sendFrameIndex)
    {
        switch (stage)
        {
            case Stage.Handshake:
                return SendHandshakePacket(buffer);
            case Stage.SendInformation:
                return SendInformationPacket(buffer);
            case Stage.SendData:
                return SendDataPacket(buffer, sendFrameIndex);
            case Stage.SendComplete:
                return SendCompletePacket(buffer);
            default:
                throw new Exception("IAP Stage error");
        }
    }
    private static void CopyWithPadding(ReadOnlySpan<byte> source, Span<byte> target, int sourceStart, int targetStart, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (sourceStart + i < source.Length) // 检查源数组是否越界
            {
                target[targetStart + i] = source[sourceStart + i];
            }
            else
            {
                target[targetStart + i] = 0; // 源数组不足时填充 0
            }
        }
    }
    public Status ReceivePacketAnalysis(Stage stage, ReadOnlySpan<byte> data, UInt32 sendFrameIndex)
    {
        if (data.Length < 8)
        {
            return Status.LengthError;
        }
        else
        {
            ushort crcReceive = BinaryPrimitives.ReadUInt16BigEndian(data[^2..]);
            ushort crcCalculate = Crc.Crc16Modbus.Calculate(data[..^2]);
            if (crcReceive != crcCalculate)
            {
                return Status.HostCheckError;
            }
            else
            {
                if (data[1] == 0xFF)
                {
                    return data[5] switch
                    {
                        0xFF => Status.DeviceFrameCheckError,//当前帧校验错误
                        0xFE => throw new Exception("IAP 设备功能码错误"),
                        0xFD => throw new Exception($"IAP 从站地址错误。当前连接的设备从站地址为{data[4]}"),
                        _ => Status.DeviceFrameCheckError,
                    };
                }
                else if (data[4] != _deviceConfig.DeviceID)
                {
                    throw new Exception($"IAP 从站地址错误。当前连接的设备从站地址为{data[4]}");
                }
                else
                {
                    switch (stage)
                    {
                        case Stage.Handshake:
                            return ReceiveHandshakePacketAnalysis(data);
                        case Stage.SendInformation:
                            return ReceiveInformationPacketAnalysis(data);
                        case Stage.SendData:
                            return ReceiveDataAnalysis(data, sendFrameIndex);
                        case Stage.SendComplete:
                            return ReceiveCompletePacketAnalysis(data);
                        default:
                            return Status.StageError;
                    }

                }
            }
        }
    }
    private int SendHandshakePacket(Span<byte> data)
    {
        data[0] = IAP_FUNCTION_CODE;
        data[1] = (byte)Stage.Handshake;
        data[2] = 0x00;
        data[3] = 0x02;
        data[4] = _deviceConfig.DeviceID;
        data[5] = IAP_HANDSHAKE_INFORMATION;
        data[6] = IAP_HANDSHAKE_INFORMATION;
        // 7 8
        var crc = Crc.Crc16Modbus.Calculate(data, 7);
        BinaryPrimitives.WriteUInt16BigEndian(data[7..9], crc);
        return 9;
    }

    private Status ReceiveHandshakePacketAnalysis(ReadOnlySpan<byte> data)
    {
        if (data[5] == 0x00)
        {
            IsFlashPerFrame = (data[6] & 0b0000_0001) != 0;
            IsRebootBeforeIapRequired = (data[6] & 0b0000_0010) != 0;

            return Status.Success;
        }
        else
        {
            throw new Exception("IAP 设备忙，当前无法升级");
        }

    }

    private int SendInformationPacket(Span<byte> data)
    {
        data[0] = IAP_FUNCTION_CODE;
        data[1] = (byte)Stage.SendInformation;
        // 2 3 数据区长度
        data[2] = 0x00;
        data[3] = 0x0E;//14
        data[4] = _deviceConfig.DeviceID;
        // 数据区
        // 5 6 7 8 总帧数
        BinaryPrimitives.WriteUInt32BigEndian(data[5..9], FrameNum);
        // 9 10 每帧字节数
        BinaryPrimitives.WriteUInt16BigEndian(data[9..11], _iapConfig.BytesPerFrame);
        // 11 12 13 14 ROM长度
        BinaryPrimitives.WriteInt32BigEndian(data[11..15], readBinData!.Length);
        // 15 16 17 18 ROM Crc32
        BinaryPrimitives.WriteUInt32BigEndian(data[15..19], fileCrc32);
        // 19 20
        var crc = Crc.Crc16Modbus.Calculate(data, 19);
        BinaryPrimitives.WriteUInt16BigEndian(data[19..21], crc);
        return 21;
    }

    private Status ReceiveInformationPacketAnalysis(ReadOnlySpan<byte> data)
    {
        var deviceCondition = data[5];
        if (deviceCondition == 0x00)
        {
            return Status.Success;
        }
        else
        {
            var receiveBytesPerFrame = BinaryPrimitives.ReadUInt16BigEndian(data[6..8]);
            if (deviceCondition == 0x01)
            {
                throw new Exception($"IAP 每帧字节数过大，当前/设备最大 = {_iapConfig.BytesPerFrame}/{receiveBytesPerFrame}");
            }
            else
            {
                throw new Exception($"IAP 每帧字节数不合法，当前/设备支持 = {_iapConfig.BytesPerFrame}/{receiveBytesPerFrame}");
            }
        }
    }
    private int SendDataPacket(Span<byte> data, UInt32 sendFrameIndex)
    {
        if (readBinData == null)
        {
            throw new Exception("The iap file was not read.");
        }
        else if (sendFrameIndex >= FrameNum)
        {
            throw new Exception("Current send frame out of range.");
        }
        else
        {
            bool isLastFrame = (sendFrameIndex == FrameNum - 1);
            int offset = (int)(sendFrameIndex * _iapConfig.BytesPerFrame);
            int currentPayloadLen = isLastFrame ? (readBinData.Length - offset) : _iapConfig.BytesPerFrame;
            //                   0          1         2  3            4              5 6 7 8      ...
            //数组长度 = 帧头5(功能码1 + 当前阶段1 + 数据区长度 + 设备ID 1) + 数据区(当前帧号4 + 每帧字节数) + 2CRC
            //         = 每帧字节数 + 帧信息9 + 2CRC
            UInt16 sendDataLen = (UInt16)(currentPayloadLen + 9);

            //填充协议头
            data[0] = IAP_FUNCTION_CODE;
            data[1] = (byte)Stage.SendData;
            // 2 3 数据区长度 = 当前帧号4 + 每帧字节数
            BinaryPrimitives.WriteUInt16BigEndian(data[2..4], (ushort)(currentPayloadLen + 4));
            // 4
            data[4] = _deviceConfig.DeviceID;
            // 5 6 7 8
            BinaryPrimitives.WriteUInt32BigEndian(data[5..9], sendFrameIndex);

            ReadOnlySpan<byte> sourceSpan = readBinData.AsSpan(offset, currentPayloadLen);
            // 9..
            sourceSpan.CopyTo(data[9..]);

            //最后两位
            var crc = Crc.Crc16Modbus.Calculate(data, sendDataLen);
            BinaryPrimitives.WriteUInt16BigEndian(data.Slice(sendDataLen, 2), crc);
            return sendDataLen + 2;
        }
    }
    private Status ReceiveDataAnalysis(ReadOnlySpan<byte> data, UInt32 sendFrame)
    {
        // 5 6 7 8
        var receiveFrameIndex = BinaryPrimitives.ReadUInt32BigEndian(data[5..9]);
        if (receiveFrameIndex == sendFrame)
        {
            return Status.Success;
        }
        else
        {
            return Status.DeviceReceiveFrameIndexError;
        }

    }

    private int SendCompletePacket(Span<byte> data)
    {
        data[0] = IAP_FUNCTION_CODE;
        data[1] = (byte)Stage.SendComplete;
        data[2] = 0x00;
        data[3] = 0x02;
        data[4] = _deviceConfig.DeviceID;
        data[5] = 0x00;
        data[6] = 0x00;
        // 7 8
        var crc = Crc.Crc16Modbus.Calculate(data, 7);
        BinaryPrimitives.WriteUInt16BigEndian(data[7..9], crc);
        return 9;
    }

    private Status ReceiveCompletePacketAnalysis(ReadOnlySpan<byte> data)
    {
        if (data[5] == 0x00)
        {
            return data[6] switch
            {
                0xFF => Status.Success,
                0x00 => Status.DeviceStartErase,
                0x01 => Status.DeviceStartWrite,
                0x02 => Status.DeviceStartFalshCheck,
                0x03 => Status.DeviceStartReboot,
                _ => Status.DeviceRebootComplete,
            };
        }
        else
        {
            return data[6] switch
            {
                0x00 => throw new Exception("IAP 设备接收到的总帧数错误"),
                _ => throw new Exception("IAP 设备校验错误"),
            };
        }
    }
}
