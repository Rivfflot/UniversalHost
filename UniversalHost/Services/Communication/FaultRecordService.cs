using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UniversalHost.Models;

namespace UniversalHost.Services.Communication;

internal sealed class FaultRecordNotCompletedException : InvalidOperationException
{
    public FaultRecordNotCompletedException() : base("录波未完成") { }
}

public class FaultRecordService
{
    private readonly SymbolInfo _recordStatusSymbol;
    private readonly SymbolInfo _recordDataSymbol;
    private readonly IProgress<double> _progress;
    private readonly IProgress<string> _stage;

    private enum RecorderStatus : byte
    {
        Running = 0,
        Post = 1,
        Finish = 2,
    }

    private const int HeaderBytes = 50;
    private const int ChannelInfoBytes = 38;

    public FaultRecordService(SymbolInfo recordStatus, SymbolInfo recordData, IProgress<double> progress, IProgress<string> stage)
    {
        _recordStatusSymbol = recordStatus;
        _recordDataSymbol = recordData;
        _progress = progress;
        _stage = stage;
    }

    public async Task<string> RunFaultRecordSequence()
    {
#if DEBUG
        var watch = System.Diagnostics.Stopwatch.StartNew();
#endif
        var client = XcpService.Client ?? throw new InvalidOperationException("设备未连接");
        _stage.Report("获取录波状态");
        _progress.Report(0);
        // 检查录波状态
        var recorderStatus = await ReadAgAsync(client, client.CalculateAgLen(1), _recordStatusSymbol.Address, "获取录波状态");
        if (recorderStatus == null || recorderStatus.Length == 0)
        {
            _stage.Report("录波状态错误");
            throw new InvalidOperationException("录波状态错误");
        }
        if (recorderStatus[0] != (byte)RecorderStatus.Finish)
        {
            _stage.Report("录波未完成");
            throw new FaultRecordNotCompletedException();
        }
        _stage.Report("获取录波变量数据");
        _progress.Report(1.0);
        // 读取 Header
        byte headerAgLen = client.CalculateAgLen(HeaderBytes);
        uint agOffset = 0;
        byte[] header = await ReadAgAsync(client, headerAgLen, _recordDataSymbol.Address + agOffset, "获取录波头");
        if (header == null || header.Length < HeaderBytes)
        {
            _stage.Report("录波数据帧头长度错误");
            throw new InvalidOperationException("录波数据帧头长度错误");
        }

        FaultRecord faultRecord = new();
        faultRecord.HeaderPacketAnalysis(header);
        agOffset += headerAgLen;

        // 读取通道信息
        byte channelAgLen = client.CalculateAgLen(ChannelInfoBytes);
        for (int i = 0; i < faultRecord.ChannelNum; i++)
        {
            byte[] channelInfo = await ReadAgAsync(client, channelAgLen, _recordDataSymbol.Address + agOffset, $"获取第 {i + 1} 通道信息");
            if (channelInfo == null || channelInfo.Length < ChannelInfoBytes)
            {
                _stage.Report($"录波第 {i} 通道信息长度错误");
                throw new InvalidOperationException($"录波第 {i} 通道信息长度错误");
            }

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
            _stage.Report($"上传第 {i + 1} / {faultRecord.RecordSymbolRuntimes.Count} 个变量");
            var runtime = faultRecord.RecordSymbolRuntimes[i];
            byte elementAg = client.CalculateAgLen(runtime.ValueSizeInBytes);
            uint totalElements = faultRecord.RecordLength;
            uint currentIdx = faultRecord.CurrentIndex;

            // 第一段：最旧数据 [currentIdx, 末尾)
            if (currentIdx < totalElements)
            {
                uint startAg = dataStartAg + currentIdx * elementAg;
                uint segElements = totalElements - currentIdx;
                await ReadSegmentAsync(client, startAg, segElements, elementAg, runtime);
            }
            // 第二段：较新数据 [0, currentIdx)
            if (currentIdx > 0)
            {
                uint startAg = dataStartAg;
                uint segElements = currentIdx;
                await ReadSegmentAsync(client, startAg, segElements, elementAg, runtime);
            }

            dataStartAg += totalElements * elementAg;
            _progress.Report(10.0 + (i + 1.0) / faultRecord.RecordSymbolRuntimes.Count * 80.0);
        }

        var path = DataSaveService.SaveToCsv(faultRecord.RecordSymbolRuntimes, "fault");
        _stage.Report("录波完成");
        _progress.Report(100.0);
#if DEBUG
        watch.Stop();
        Debug.WriteLine($"故障录波耗时: {watch.ElapsedMilliseconds} ms");
#endif
        return path;
    }

    /// <summary>
    /// 从指定 AG 地址读取指定数量的元素，并按时间顺序写入 SymbolRuntime。
    /// </summary>
    private async Task ReadSegmentAsync(XcpClient client, uint agStart, uint elementCount, byte elementAgSize, SymbolRuntime runtime)
    {
        uint remaining = elementCount;
        uint currentAg = agStart;
        byte maxCtoBytes = (byte)(client.DeviceStatus.ConnectRes.MaxCtoLen - 1); // 扣除 1 字节命令头
        byte maxElementsPerPacket = (byte)(maxCtoBytes / runtime.ValueSizeInBytes); // 保守计算

        while (remaining > 0)
        {
            byte items = (byte)Math.Min(maxElementsPerPacket, remaining);
            byte agToRead = (byte)(items * elementAgSize);

            byte[] data = await ReadAgAsync(client, agToRead, currentAg, $"上传通道 {runtime.Symbol.Name.TrimEnd('\0')}");

            for (int j = 0; j < items; j++)
            {
                var slice = data.AsSpan(j * runtime.ValueSizeInBytes, runtime.ValueSizeInBytes);
                runtime.UpdateValueFromBytes(slice, client.DeviceStatus.ConnectRes.IsLittleEndian);
            }

            currentAg += agToRead;
            remaining -= items;
        }
    }

    private static async Task<byte[]> ReadAgAsync(XcpClient client, byte agNumber, uint address, string stage)
    {
        try
        {
            byte[] data = await client.UploadAgAsync(agNumber, address);
            int expectedBytes = client.CalculateByteLen(agNumber);
            if (data.Length < expectedBytes)
                throw new InvalidOperationException($"应答数据不完整：期望 {expectedBytes} 字节，实际 {data.Length} 字节");
            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"{stage}失败：AG 地址 0x{address:X8}，长度 {agNumber} AG。{ex.Message}", ex);
        }
    }
}
