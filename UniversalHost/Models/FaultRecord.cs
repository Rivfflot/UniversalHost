using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using UniversalHost.Symbols;

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
        if (packet.Length < 50)
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
        ScalarType symbolDataType = dataType switch
        {
            0 => ScalarType.Boolean,
            1 => ScalarType.Int8,
            2 => ScalarType.Uint8,
            3 => ScalarType.Int16,
            4 => ScalarType.Uint16,
            5 => ScalarType.Int32,
            6 => ScalarType.Uint32,
            7 => ScalarType.Float32,
            8 => ScalarType.Int64,
            9 => ScalarType.Uint64,
            10 => ScalarType.Float64,
            _ => throw new ArgumentOutOfRangeException(nameof(dataType)),
        };

        UserSymbolInfo userSymbolInfo = new()
        {
            Name = Encoding.ASCII.GetString(name),
            DataType = symbolDataType
        };
        var runtime = SymbolRuntime.CreateSymbolRuntime(userSymbolInfo, (int)RecordLength);

        RecordSymbolRuntimes.Add(runtime);
    }
}
