using System;
using System.Threading.Tasks;
using UniversalHost.Models;

namespace UniversalHost.Services.Communication;

public class FaultRecordService
{
    private readonly SymbolInfo _recordStatusSymbol;
    private readonly SymbolInfo _recordDataSymbol;
    private readonly IProgress<double> _progress;

    private enum RecorderStatus : byte
    {
        Running = 0,
        Post = 1,
        Finish = 2,
    }

    private const int HeaderBytes = 50;
    private const int ChannelInfoBytes = 38;

    public FaultRecordService(SymbolInfo recordStatus, SymbolInfo recordData, IProgress<double> progress)
    {
        _recordStatusSymbol = recordStatus;
        _recordDataSymbol = recordData;
        _progress = progress;
    }

    public async Task<string> RunFaultRecordSequence()
    {
        var client = XcpService.Client!;
        _progress.Report(0);

        // 检查录波状态
        var recorderStatus = await client.UploadSymbol(1, _recordStatusSymbol.Address);
        if (recorderStatus == null || recorderStatus.Length == 0)
            throw new InvalidOperationException("录波状态错误");
        if (recorderStatus[0] != (byte)RecorderStatus.Finish)
            throw new InvalidOperationException("录波未完成");
        _progress.Report(1.0);

        // 读取 Header
        byte headerAgLen = client.CalculateAgLen(HeaderBytes);
        uint agOffset = 0;
        byte[] header = await client.UploadAgAsync(headerAgLen, _recordDataSymbol.Address + agOffset);
        if (header == null || header.Length < HeaderBytes)
            throw new InvalidOperationException("录波数据帧头长度错误");

        FaultRecord faultRecord = new();
        faultRecord.HeaderPacketAnalysis(header);
        agOffset += headerAgLen;

        // 读取通道信息
        byte channelAgLen = client.CalculateAgLen(ChannelInfoBytes);
        for (int i = 0; i < faultRecord.ChannelNum; i++)
        {
            byte[] channelInfo = await client.UploadAgAsync(channelAgLen, _recordDataSymbol.Address + agOffset);
            if (channelInfo == null || channelInfo.Length < ChannelInfoBytes)
                throw new InvalidOperationException($"录波第 {i} 通道信息长度错误");

            var name = channelInfo.AsSpan()[..20];
            ushort dataType = client.ReadUInt16(channelInfo.AsSpan()[20..22]);
            faultRecord.AddRecordSymbol(name, dataType);
            agOffset += channelAgLen;
            _progress.Report(3.0 + (i + 1.0) / faultRecord.ChannelNum * 10.0);
        }

        // 读取录波数据（按时间顺序）
        uint dataStartAg = _recordDataSymbol.Address + agOffset; // 第一个通道数据的 AG 起始地址

        for (int i = 0; i < faultRecord.RecordSymbolRuntimes.Count; i++)
        {
            var runtime = faultRecord.RecordSymbolRuntimes[i];
            byte elementAg = client.CalculateAgLen(runtime.ValueSizeInBytes);
            uint totalElements = faultRecord.RecordLength;
            uint currentIdx = faultRecord.CurrentIndex;

            // 第一段：最旧数据 [currentIdx, 末尾)
            if (currentIdx < totalElements)
            {
                uint startAg = dataStartAg + currentIdx * elementAg;
                uint segElements = totalElements - currentIdx;
                await ReadSegmentAsync(startAg, segElements, elementAg, runtime);
            }
            // 第二段：较新数据 [0, currentIdx)
            if (currentIdx > 0)
            {
                uint startAg = dataStartAg;
                uint segElements = currentIdx;
                await ReadSegmentAsync(startAg, segElements, elementAg, runtime);
            }

            dataStartAg += totalElements * elementAg;
            _progress.Report(10.0 + (i + 1.0) / faultRecord.RecordSymbolRuntimes.Count * 80.0);
        }

        var path = DataSaveService.SaveToCsv(faultRecord.RecordSymbolRuntimes, "fault");
        _progress.Report(100.0);
        return path;
    }

    /// <summary>
    /// 从指定 AG 地址读取指定数量的元素，并按时间顺序写入 SymbolRuntime。
    /// </summary>
    private async Task ReadSegmentAsync(uint agStart, uint elementCount, byte elementAgSize, SymbolRuntime runtime)
    {
        var client = XcpService.Client!;
        uint remaining = elementCount;
        uint currentAg = agStart;
        byte maxCtoBytes = (byte)(client.DeviceStatus.ConnectRes.MaxCtoLen - 1); // 扣除 1 字节命令头
        byte maxElementsPerPacket = (byte)(maxCtoBytes / runtime.ValueSizeInBytes); // 保守计算

        while (remaining > 0)
        {
            byte items = (byte)Math.Min(maxElementsPerPacket, remaining);
            byte agToRead = (byte)(items * elementAgSize);

            byte[] data = await client.UploadAgAsync(agToRead, currentAg);

            for (int j = 0; j < items; j++)
            {
                var slice = data.AsSpan(j * runtime.ValueSizeInBytes, runtime.ValueSizeInBytes);
                runtime.UpdateValueFromBytes(slice, client.DeviceStatus.ConnectRes.IsLittleEndian);
            }

            currentAg += agToRead;
            remaining -= items;
        }
    }
}