using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models;
using static UniversalHost.Models.IapProtocol;

namespace UniversalHost.Services.Communication;

public class IapService
{
    private enum StageProgress
    {
        Start = 0,
        ReadFile = 2,
        HandShake = 4,
        Infomation = 6,
        SendDataStart = 8,
        SendDataEnd = 75,
        StartErase = 80,
        StartWrite = 85,
        StartCheck = 90,
        StartReboot = 95,
        RebootComplete = 100
    }
    //进度条更新
    private readonly IProgress<double> _progress;
    private readonly IProgress<string> _stage;
    //接收和发送缓存
    private readonly byte[] _receiveBuffer = new byte[1436];
    private readonly byte[] _sendBuffer = new byte[1436];
    public IapService(IProgress<double> iap_progress, IProgress<string> stage)
    {
        _progress = iap_progress;
        _stage = stage;
    }

    // 通信服务工厂
    private ICommService CreateCommService()
    {
        return ProjectSaveService.Instance.Settings.DeviceConfig.Mode switch
        {
            CommunicationMode.UDP => new UdpService(ProjectSaveService.Instance.Settings.UdpConfig.LocalAddress, ProjectSaveService.Instance.Settings.UdpConfig.IapLocalPort, ProjectSaveService.Instance.Settings.UdpConfig.RemoteAddress, ProjectSaveService.Instance.Settings.UdpConfig.IapRemotePort, ProjectSaveService.Instance.Settings.UdpConfig.TimeoutMilliseconds),
            //CommunicationMode.Serial => new IapSerialService(ProjectSaveService.Instance.Settings.SerialConfig),//TODO : 串口通信
            _ => throw new Exception("Unsupported communication Mode")
        };
    }
    // 主入口：启动 IAP 流程
    public async Task RunIapSequenceAsync(CancellationToken ct)
    {
        _progress.Report((double)StageProgress.Start);//开始
        _stage.Report("开始升级");
        // 创建通信服务
        await using var comm = CreateCommService();
        var retryTimes = ProjectSaveService.Instance.Settings.DeviceConfig.Mode switch
        {
            CommunicationMode.UDP => ProjectSaveService.Instance.Settings.UdpConfig.RetryTimes + 1,
            _ => ProjectSaveService.Instance.Settings.SerialConfig.RetryTimes + 1
        };

        // 创建IAP协议
        var protocol = new IapProtocol(ProjectSaveService.Instance.Settings.IapConfig, ProjectSaveService.Instance.Settings.DeviceConfig);
        // 读取BIN
        _stage.Report("读取文件");
        protocol.ReadFile();
        _progress.Report((double)StageProgress.ReadFile);//文件读取完成

        //阶段1：握手
        _stage.Report("开始握手");
        Serilog.Log.Verbose("IAP 开始握手");

        await RunHandShakeAsync(comm, protocol, TimeSpan.FromSeconds(ProjectSaveService.Instance.Settings.IapConfig.WaitForHandShakeTimeoutSeconds), ct);

        _progress.Report((double)StageProgress.HandShake);//握手信息发送完成

        //阶段2：发送信息，包括总帧数和每帧字节数
        _stage.Report("发送信息");
        Serilog.Log.Verbose("IAP 开始发送信息");
        await RunStageAsync(comm, protocol, IapProtocol.Stage.SendInformation, retryTimes, ct);
        _progress.Report((double)StageProgress.Infomation);//IAP信息发送完成

        //阶段3：发送数据，帧数=文件大小/每帧字节数。最后一帧可变长度。
        _stage.Report("发送数据");
        Serilog.Log.Verbose("IAP 开始发送数据");
        await RunDataFramesAsync(comm, protocol, retryTimes, ct);


        //阶段4：发送完成信号，等待设备擦除，写入，校验，重启开始，重启完成
        await RunStageAsync(comm, protocol, IapProtocol.Stage.SendComplete, retryTimes, ct);

        //整体写入，需要等待擦除，写入
        if (!protocol.IsFlashPerFrame)
        {
            _progress.Report((double)StageProgress.StartErase);
            _stage.Report("开始擦除");
            Serilog.Log.Verbose("IAP 开始擦除");
            //等待擦除开始信号
            await WaitForStatusWithTimeoutAsync(comm, protocol, IapProtocol.Status.DeviceStartErase, TimeSpan.FromSeconds(1), ct);

            //等待擦除完成后的写入开始信号，等待时间 = 擦除时间
            _progress.Report((double)StageProgress.StartWrite);
            _stage.Report("开始写入");
            Serilog.Log.Verbose("IAP 开始写入");
            await WaitForStatusWithTimeoutAsync(comm, protocol, IapProtocol.Status.DeviceStartWrite, TimeSpan.FromSeconds(ProjectSaveService.Instance.Settings.IapConfig.WaitForWriteTimeoutSeconds), ct);
        }

        _progress.Report((double)StageProgress.StartCheck);

        _stage.Report("开始校验");
        Serilog.Log.Verbose("IAP 开始校验");
        //等待校验开始信号。等待时间=写入时间。逐帧写入时等待时间≈0
        await WaitForStatusWithTimeoutAsync(comm, protocol, IapProtocol.Status.DeviceStartFalshCheck, TimeSpan.FromSeconds(ProjectSaveService.Instance.Settings.IapConfig.WaitForCheckTimeoutSeconds), ct);

        //等待重启开始信号。等待时间=校验时间
        _stage.Report("开始重启");
        Serilog.Log.Verbose("IAP 开始重启");
        await WaitForStatusWithTimeoutAsync(comm, protocol, IapProtocol.Status.DeviceStartReboot, TimeSpan.FromSeconds(ProjectSaveService.Instance.Settings.IapConfig.WaitForRebootStartTimeoutSeconds), ct);
        _progress.Report((double)StageProgress.StartReboot);

        //等待重启开始开始后的重启完成信号，等待时间 = 重启+初始化时间
        await WaitForStatusWithTimeoutAsync(comm, protocol, IapProtocol.Status.DeviceRebootComplete, TimeSpan.FromSeconds(ProjectSaveService.Instance.Settings.IapConfig.WaitForRebootCompleteTimeoutSeconds), ct);
        _progress.Report((double)StageProgress.RebootComplete);
    }

    //发送握手信息
    private async Task RunHandShakeAsync(
        ICommService comm,
        IapProtocol protocol,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var last_update_time = TimeSpan.Zero;
        var sendDataLen = protocol.GetSendPacket(_sendBuffer, IapProtocol.Stage.Handshake, 0);
        ReadOnlyMemory<byte> sendData = _sendBuffer.AsMemory(0, sendDataLen);
        await comm.SendAsync(sendData, ct);

        while (sw.Elapsed < timeout)
        {
            var receivedDataLen = await comm.ReceiveAsync(_receiveBuffer, ct);
            if (receivedDataLen <= 0)
            {
                if (sw.Elapsed - last_update_time >= TimeSpan.FromSeconds(1))
                {
                    await comm.SendAsync(sendData, ct);
                    Serilog.Log.Verbose($"IAP 阶段 HandShake 1s无应答，再次发送握手包");
                    last_update_time = sw.Elapsed;
                }
            }
            else
            {
                ReadOnlySpan<byte> recvData = _receiveBuffer.AsSpan(0, receivedDataLen);
                var status = protocol.ReceivePacketAnalysis(IapProtocol.Stage.Handshake, recvData, 0);

                Serilog.Log.Verbose($"IAP 阶段 HandShake 状态: {status}");
                if (status == IapProtocol.Status.Success)
                    return;
            }
        }
        Serilog.Log.Debug($"IAP 阶段 HandShake 超时");
        throw new Exception($"IAP 阶段 HandShake 超时");
    }

    // 通用阶段执行
    private async Task RunStageAsync(
        ICommService comm,
        IapProtocol protocol,
        IapProtocol.Stage stage,
        int retryLimit,
        CancellationToken ct,
        uint frameIndex = 0)
    {
        for (int i = 0; i < retryLimit; i++)
        {
            var sendDataLen = protocol.GetSendPacket(_sendBuffer, stage, frameIndex);
            ReadOnlyMemory<byte> sendData = _sendBuffer.AsMemory(0, sendDataLen);
            await comm.SendAsync(sendData, ct);

            var receivedDataLen = await comm.ReceiveAsync(_receiveBuffer, ct);
            if (receivedDataLen <= 0)
            {
                if (stage == IapProtocol.Stage.SendData && (frameIndex % 10 == 0))
                {
                    Serilog.Log.Verbose($"IAP 阶段 {stage} 第{frameIndex}/{protocol.FrameNum} 帧 第 {i + 1} 次尝试，状态: 接收超时");
                }
                else
                {
                    Serilog.Log.Verbose($"IAP 阶段 {stage} 第 {i + 1} 次尝试，状态: 接收超时");
                }
            }
            else
            {
                ReadOnlySpan<byte> recvData = _receiveBuffer.AsSpan(0, receivedDataLen);
                var status = protocol.ReceivePacketAnalysis(stage, recvData, frameIndex);
                if (stage == IapProtocol.Stage.SendData && (frameIndex % 10 == 0))
                {
                    Serilog.Log.Verbose($"IAP 阶段 {stage} 第{frameIndex}/{protocol.FrameNum} 帧 第 {i + 1} 次尝试，状态: {status}");
                }
                else
                {
                    Serilog.Log.Verbose($"IAP 阶段 {stage} 第 {i + 1} 次尝试，状态: {status}");
                }
                if (status == IapProtocol.Status.Success)
                    return;
            }
        }
        Serilog.Log.Debug($"IAP 阶段 {stage} 超过重试次数，失败");
        throw new Exception($"IAP 阶段 {stage} 超过重试次数，失败");
    }
    // 数据帧接收
    private async Task RunDataFramesAsync(
        ICommService comm,
        IapProtocol protocol,
        int retryLimit,
        CancellationToken ct)
    {
        for (uint j = 0; j < protocol.FrameNum; j++)
        {
            await RunStageAsync(comm, protocol, IapProtocol.Stage.SendData, retryLimit, ct, j);
            if (j % 10 == 0)
            {
                double progress_val = (double)j * (((double)StageProgress.SendDataEnd - (double)StageProgress.SendDataStart) / (double)protocol.FrameNum) + (double)StageProgress.SendDataStart;
                _progress.Report(progress_val);
            }
        }
    }
    private async Task WaitForStatusWithTimeoutAsync(
        ICommService comm,
        IapProtocol protocol,
        IapProtocol.Status expected,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var last_update_time = TimeSpan.Zero;

        while (sw.Elapsed < timeout)
        {
            var receivedDataLen = await comm.ReceiveAsync(_receiveBuffer, ct);
            if (receivedDataLen <= 0)
            {
                if (sw.Elapsed - last_update_time >= TimeSpan.FromSeconds(1))
                {
                    Serilog.Log.Verbose($"IAP 等待设备状态 {expected} 第 {sw.Elapsed.TotalSeconds:F0} s，当前状态: 等待设备返回完成信号");
                    last_update_time = sw.Elapsed;
                }
            }
            else
            {
                ReadOnlySpan<byte> recvData = _receiveBuffer.AsSpan(0, receivedDataLen);
                var status = protocol.ReceivePacketAnalysis(IapProtocol.Stage.SendComplete, recvData, 0);
                Serilog.Log.Verbose($"IAP 等待设备状态 {expected} 第 {sw.Elapsed.TotalSeconds:F0} s，当前状态: {status}");
                if (status == expected)
                    return;
            }
        }
        Serilog.Log.Debug($"IAP 等待设备状态 {expected} 超过 {timeout.TotalSeconds} 秒");
        throw new TimeoutException($"IAP 等待设备状态 {expected} 超过 {timeout.TotalSeconds} 秒");
    }
}