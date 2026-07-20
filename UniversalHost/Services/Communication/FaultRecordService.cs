using System;
using System.Threading.Tasks;
using UniversalHost.Models;

namespace UniversalHost.Services.Communication;

public class FaultRecordService
{
    private readonly SymbolInfo _recordStatusSymbol;
    private readonly SymbolInfo _recordDataSymbol;
    private readonly IProgress<double> _progress;
    public FaultRecordService(SymbolInfo recordStatus, SymbolInfo recordData, IProgress<double> progress)
    {
        _recordStatusSymbol = recordStatus;
        _recordDataSymbol = recordData;
        _progress = progress;
    }
    private enum RecorderStatus : byte
    {
        Running = 0,
        Post = 1,
        Finish = 2,
    }
    private enum DataLen : byte
    {
        Header = 50,
        ChannelInfo = 38,
    }
    public async Task<string> RunFaultRecordSequence()
    {
        _progress.Report(0);
        //获取故障录波状态
        var recorderStatus = await XcpService.Client!.UploadSymbol(1, _recordStatusSymbol.Address);
        if (recorderStatus == null || recorderStatus.Length == 0)
        {
            throw new NotImplementedException("录波状态错误");
        }
        if (recorderStatus[0] != (byte)RecorderStatus.Finish)
        {
            throw new NotImplementedException("录波未完成");
        }
        _progress.Report(1.0);
        //获取故障录波header
        var header = await XcpService.Client!.UploadSymbol(50, _recordStatusSymbol.Address);
        if (header == null || header.Length != (int)DataLen.Header)
        {
            throw new NotImplementedException("录波数据帧头长度错误");
        }
        FaultRecord faultRecord = new();
        faultRecord.HeaderPacketAnalysis(header);
        _progress.Report(3.0);
        //获取故障录波通道信息
        uint offset = XcpService.Client.CalculateAgLen((byte)DataLen.Header);
        for (int i = 0; i < faultRecord.ChannelNum; i++)
        {
            var channelInfo = await XcpService.Client!.UploadSymbol((int)DataLen.ChannelInfo, _recordDataSymbol.Address + offset);
            if (channelInfo == null || channelInfo.Length != (byte)DataLen.ChannelInfo)
            {
                throw new NotImplementedException($"录波第 {i} 通道信息长度错误");
            }
            var name = channelInfo.AsSpan()[..20];
            var dataType = XcpService.Client.ReadUInt16(channelInfo.AsSpan()[20..22]);
            faultRecord.AddRecordSymbol(name, dataType);
            offset += XcpService.Client.CalculateAgLen((byte)DataLen.ChannelInfo);
            _progress.Report(3.0 + (double)(i + 1.0) / (double)faultRecord.ChannelNum * 10.0);
        }
        //获取故障录波数据
        for (int i = 0; i < faultRecord.RecordSymbolRuntimes.Count; i++)
        {
            var runtime = faultRecord.RecordSymbolRuntimes[i];
            uint uploadItemCnt = 0;
            while (uploadItemCnt < faultRecord.RecordLength)
            {
                byte itemCnt = (byte)(byte.MaxValue / runtime.ValueSizeInBytes);
                if (uploadItemCnt + itemCnt > faultRecord.RecordLength)
                {
                    itemCnt = (byte)(faultRecord.RecordLength - itemCnt);
                }
                byte agCnt = XcpService.Client!.CalculateAgLen((byte)(itemCnt * runtime.ValueSizeInBytes));
                var data = await XcpService.Client!.UploadSymbol(agCnt, _recordDataSymbol.Address + offset);
                for (uint j = 0; j < itemCnt; j++)
                {
                    var temp = data.AsSpan().Slice((int)(j * runtime.ValueSizeInBytes), runtime.ValueSizeInBytes);
                    runtime.UpdateValueFromBytes(temp, XcpService.Client!.DeviceStatus.ConnectRes.IsLittleEndian);
                }
                offset += itemCnt;
                uploadItemCnt += itemCnt;
            }
            _progress.Report(10.0 + (double)(i + 1) / (double)faultRecord.RecordSymbolRuntimes.Count * 80.0);
        }
        var path = DataSaveService.SaveToCsv(faultRecord.RecordSymbolRuntimes, "fault");
        _progress.Report(100.0);
        return path;
    }
}