using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace UniversalHost.Models;

internal class FaultRecord
{
    // Header
    public ushort ChannelNum { get; private set; } = 0;
    public uint SampleRate { get; private set; } = 0;
    public uint RecordLength { get; private set; } = 0;
    public uint CurrentIndex { get; private set; } = 0;

    public List<SymbolRuntime> RecordSymbolRuntimes = [];

    public void HeaderPacketAnalysis(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != 50)
        {
            throw new ArgumentException("故障录波 Header长度错误");
        }
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(packet[0..4]);
        if (magic != 0x52454344)
        {
            throw new Exception("故障录波 Header Magic错误");
        }
        ChannelNum = BinaryPrimitives.ReadUInt16LittleEndian(packet[4..6]);
        SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(packet[6..10]);
        RecordLength = BinaryPrimitives.ReadUInt32LittleEndian(packet[10..14]);
        CurrentIndex = BinaryPrimitives.ReadUInt32LittleEndian(packet[14..18]);
    }

    /// <summary>
    /// 读取通道数据时增加符号
    /// </summary>
    /// <param name="name">名称</param>
    /// <param name="dataType">数据类型</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public void AddRecordSymbol(ReadOnlySpan<byte> name, ushort dataType)
    {
        SymbolDataType symbolDataType = dataType switch
        {
            0 => SymbolDataType.Boolean,
            1 => SymbolDataType.Int8,
            2 => SymbolDataType.Uint8,
            3 => SymbolDataType.Int16,
            4 => SymbolDataType.Uint16,
            5 => SymbolDataType.Int32,
            6 => SymbolDataType.Uint32,
            7 => SymbolDataType.Float32,
            8 => SymbolDataType.Int64,
            9 => SymbolDataType.Uint64,
            10 => SymbolDataType.Float64,
            _ => throw new ArgumentOutOfRangeException(nameof(dataType)),
        };

        UserSymbolInfo userSymbolInfo = new()
        {
            Name = Encoding.ASCII.GetString(name),
            DataType = symbolDataType
        };
        var runtime = SymbolRuntime.CreateSymbolRuntime(userSymbolInfo, (int)RecordLength);
        //预先填充，使环形缓冲区从CurrentIndex开始，便于后续解析
        byte[] temp = new byte[runtime.ValueSizeInBytes];
        for (int i = 0; i < CurrentIndex; i++)
        {
            runtime.UpdateValueFromBytes(temp, true);
        }
        RecordSymbolRuntimes.Add(runtime);
    }

    /// <summary>
    /// runtime中存储的是原始数组，按照实际时间顺序返回字符串。
    /// </summary>
    /// <param name="index">0最旧，RecordLength-1最新</param>
    /// <param name="runtime"></param>
    /// <returns></returns>
    public string GetIndexString(uint index, SymbolRuntime runtime)
    {
        if (index >= RecordLength)
        {
            throw new IndexOutOfRangeException();
        }
        uint actualIdx = CurrentIndex + index + 1;
        if (actualIdx >= RecordLength)
        {
            actualIdx -= RecordLength;
        }
        return runtime.GetValueHistoryIndexString((int)actualIdx)!;
    }
}
