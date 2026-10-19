using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Models.Communication;
using UniversalHost.Services.Communication.Serial;
using static UniversalHost.Models.Communication.IapProtocol;

namespace UniversalHost.Services.Communication;

public class IapService
{
    private static readonly TimeSpan OperationRetryInterval = TimeSpan.FromMilliseconds(500);
    private readonly IProgress<double> _progress;
    private readonly IProgress<string> _stage;
    private readonly IapProtocol _protocol;
    private readonly IapConfig _config;
    private readonly TimeSpan _receiveTimeout;
    private readonly int _retryTimes;
    private readonly Func<ICommService> _createCommService;
    private readonly CommunicationMode _mode;
    private readonly SerialConnectionOptions? _serialOptions;
    // UDP 数据报可包含多条完整 ACK，不能将拼接应答当成单帧解析。
    private readonly byte[] _receiveBuffer = new byte[ushort.MaxValue];
    private readonly byte[] _sendBuffer = new byte[MaxBytesPerFrame + 11];

    public IapService(IProgress<double> iapProgress, IProgress<string> stage)
        : this(iapProgress, stage, ProjectSaveService.Instance.Settings)
    {
    }

    public IapService(IProgress<double> iapProgress, IProgress<string> stage,
        ProjectSettings settings)
        : this(iapProgress, stage, settings, null)
    {
    }

    internal IapService(IProgress<double> iapProgress, IProgress<string> stage,
        ProjectSettings settings, Func<ICommService>? createCommService)
    {
        _progress = iapProgress;
        _stage = stage;
        var iapFilePath = ProjectFilePathService.ResolvePath(settings.IapConfig.IapFilePath, ProjectSaveService.Instance.ProjectFilePath);
        _protocol = new IapProtocol(iapFilePath, settings.DeviceConfig.DeviceID);
        // 升级期间即使修改设置或切换工程，本次会话仍使用启动时的参数。
        var config = settings.IapConfig;
        _config = new IapConfig
        {
            WaitForHandShakeTimeoutSeconds = config.WaitForHandShakeTimeoutSeconds,
            WaitForEraseTimeoutSeconds = config.WaitForEraseTimeoutSeconds,
            WaitForWriteTimeoutSeconds = config.WaitForWriteTimeoutSeconds,
            WaitForCheckTimeoutSeconds = config.WaitForCheckTimeoutSeconds,
            WaitForRebootTimeoutSeconds = config.WaitForRebootTimeoutSeconds
        };
        _mode = settings.DeviceConfig.Mode;
        _serialOptions = _mode == CommunicationMode.Serial ? new SerialConnectionOptions(settings.SerialConfig) : null;
        int timeoutMilliseconds = _mode == CommunicationMode.UDP
            ? settings.UdpConfig.TimeoutMilliseconds : _serialOptions!.TimeoutMilliseconds;
        _receiveTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        _retryTimes = _mode == CommunicationMode.UDP
            ? settings.UdpConfig.RetryTimes : settings.SerialConfig.RetryTimes;
        var localAddress = settings.UdpConfig.LocalAddress;
        var localPort = settings.UdpConfig.IapLocalPort;
        var remoteAddress = settings.UdpConfig.RemoteAddress;
        var remotePort = settings.UdpConfig.IapRemotePort;
        _createCommService = createCommService ?? (() => _mode switch
        {
            // 连接的 UDP socket 只接收配置的远端 IP/端口，隔离其他发送端。
            CommunicationMode.UDP => new UdpService(localAddress, localPort, remoteAddress, remotePort, timeoutMilliseconds),
            CommunicationMode.Serial => new SerialTransportService(_serialOptions!, SerialProtocol.Iap),
            _ => throw new NotSupportedException("不支持该 IAP 通信模式")
        });
    }

    public Task RunIapSequenceAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 在读文件、开串口之前占用，直到底层串口释放后才解除互斥。
        var session = CommunicationSessionCoordinator.AcquireIap(_mode);
        return Task.Run(async () =>
        {
            using (session)
                await RunSequenceCoreAsync(ct);
        });
    }

    private async Task RunSequenceCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateTimeouts();
        Report("读取文件", 0);
        _protocol.ReadFile();
        ct.ThrowIfCancellationRequested();
        _progress.Report(2);

        // 空 BIN 等文件错误在建立通信前拒绝。
        await using var comm = _createCommService();
        Report("等待就绪", 2);
        await RunRequestAsync(comm, Stage.Handshake, _config.WaitForHandShakeTimeoutSeconds, ct);
        _progress.Report(4);

        Report("发送信息", 4);
        await RunRequestAsync(comm, Stage.SendInformation, null, ct,
            _retryTimes, _receiveTimeout);
        _progress.Report(6);

        if (_protocol.IsFlashPerFrame)
        {
            Report("擦除目标", 6);
            await RunRequestAsync(comm, Stage.StartErase, _config.WaitForEraseTimeoutSeconds, ct);
        }

        // 逐帧模式发送时同时写入，占 40%；整体模式仅传输数据，占 30%。
        double sendStart = _protocol.IsFlashPerFrame ? 41 : 6;
        double sendEnd = _protocol.IsFlashPerFrame ? 81 : 36;
        Report("发送数据 0%", sendStart);
        int sentPercent = 0;
        for (uint frameIndex = 0; frameIndex < _protocol.FrameNum; frameIndex++)
        {
            await RunRequestAsync(comm, Stage.SendData, null, ct,
                _retryTimes, _receiveTimeout, frameIndex);
            int percent = (int)(100L * (frameIndex + 1) / _protocol.FrameNum);
            if (percent != sentPercent)
            {
                bool logProgress = percent / 10 > sentPercent / 10;
                sentPercent = percent;
                Report($"发送数据 {percent}%", sendStart + (sendEnd - sendStart) * (frameIndex + 1) / _protocol.FrameNum, logProgress);
            }
        }

        Report("确认接收", sendEnd);
        await RunRequestAsync(comm, Stage.SendComplete, null, ct,
            _retryTimes, _receiveTimeout);
        _progress.Report(sendEnd + 3);

        if (!_protocol.IsFlashPerFrame)
        {
            Report("擦除目标", 39);
            await RunRequestAsync(comm, Stage.StartErase, _config.WaitForEraseTimeoutSeconds, ct);
            Report("写入目标", 64);
            await RunRequestAsync(comm, Stage.StartWrite, _config.WaitForWriteTimeoutSeconds, ct);
        }

        Report("校验目标", 84);
        await RunRequestAsync(comm, Stage.StartCheck, _config.WaitForCheckTimeoutSeconds, ct);
        Report("等待重启", 95);
        await RunRequestAsync(comm, Stage.Reboot, _config.WaitForRebootTimeoutSeconds, ct);
        // 只有本次所有前序请求成功，且收到 APP 就绪 ACK，才完成升级。
        Report("升级完成", 100);
    }

    private void ValidateTimeouts()
    {
        if (_receiveTimeout <= TimeSpan.Zero ||
            _config.WaitForHandShakeTimeoutSeconds == 0 ||
            _config.WaitForEraseTimeoutSeconds == 0 ||
            _config.WaitForWriteTimeoutSeconds == 0 ||
            _config.WaitForCheckTimeoutSeconds == 0 ||
            _config.WaitForRebootTimeoutSeconds == 0)
            throw new InvalidOperationException("IAP 通信接收超时及各阶段超时必须大于 0");
    }

    private void Report(string stage, double progress, bool logProgress = true)
    {
        _stage.Report(stage);
        _progress.Report(progress);
        if (logProgress)
            LogService.ForModule(LogModule.Iap).Verbose("IAP {Stage}", stage);
    }

    private void ReportOperationProgress(Stage stage, byte percent, ref int loggedPercent, bool logProgress = true)
    {
        // 将当前操作的百分比映射到总进度；逐帧写入在发送数据前擦除。
        var (text, start, end) = stage switch
        {
            Stage.StartErase when _protocol.IsFlashPerFrame => ("擦除目标", 6.0, 41.0),
            Stage.StartErase => ("擦除目标", 39.0, 64.0),
            Stage.StartWrite => ("写入目标", 64.0, 84.0),
            Stage.StartCheck => ("校验目标", 84.0, 95.0),
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        // 界面仍按实际进度刷新；设备进度每个 10% 区间只记录一次，成功 ACK 补齐进度时不记录。
        bool shouldLog = logProgress && percent / 10 > loggedPercent / 10;
        if (shouldLog)
            loggedPercent = percent;
        Report($"{text} {percent}%", start + (end - start) * percent / 100.0, shouldLog);
    }

    // retryTimes 为 null 时按阶段期限等待；擦除、写入、校验仅在有效进度超过历史最大值时延长期限。
    // 信息帧、数据帧及发送完成不设独立总超时，每次等待仅由通信接收超时决定。
    private async Task RunRequestAsync(ICommService comm, Stage stage, ushort? timeoutSeconds,
        CancellationToken ct, int? retryTimes = null, TimeSpan? retryInterval = null, uint frameIndex = 0)
    {
        var interval = retryInterval ?? OperationRetryInterval;
        TimeSpan? serialResponseWait = null;
        if (_serialOptions != null)
        {
            int acknowledgementLength = stage switch
            {
                Stage.Handshake => 11,
                Stage.SendData => 12,
                Stage.SendInformation => 8,
                _ => 9
            };
            var serialWait = _serialOptions.GetResponseTimeout(acknowledgementLength);
            serialResponseWait = serialWait;
            if (retryTimes.HasValue)
                interval = serialWait;
        }
        TimeSpan? timeout = timeoutSeconds.HasValue ? TimeSpan.FromSeconds(timeoutSeconds.Value) : null;
        byte highestProgress = 0;
        bool hasReportedProgress = false;
        int loggedPercent = 0;
        var sw = Stopwatch.StartNew();
        TimeSpan? deadline = timeout;
        int packetLength = _protocol.GetSendPacket(_sendBuffer, stage, frameIndex);
        var packet = _sendBuffer.AsMemory(0, packetLength);
        int attempts = 0;
        var nextSendTime = TimeSpan.Zero;
        while (!deadline.HasValue || sw.Elapsed < deadline.Value)
        {
            ct.ThrowIfCancellationRequested();
            if (attempts == 0 || sw.Elapsed >= nextSendTime)
            {
                if (retryTimes.HasValue && attempts >= retryTimes.Value + 1)
                    throw new TimeoutException($"IAP 阶段 {stage}，帧号 {frameIndex}，超过重试次数（{retryTimes} 次重试）");
                // 每次重发完全相同的请求，不因迟到、无关 ACK 或进度帧改变发送节奏。
                nextSendTime = sw.Elapsed + interval;
                // 每次 I/O 按当前截止时间设置取消预算，避免旧阶段定时器在进度延长期限后取消等待。
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (deadline.HasValue)
                {
                    var remainingSendTime = deadline.Value - sw.Elapsed;
                    if (remainingSendTime <= TimeSpan.Zero)
                        break;
                    sendCts.CancelAfter(remainingSendTime);
                }
                try
                {
                    await comm.SendAsync(packet, sendCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && sendCts.IsCancellationRequested)
                {
                    break;
                }
                // 保留耗时操作 0.5 秒的发送周期；低波特率时至少留出发送完成后的完整应答窗口。
                if (serialResponseWait.HasValue && sw.Elapsed + serialResponseWait.Value > nextSendTime)
                    nextSendTime = sw.Elapsed + serialResponseWait.Value;
                attempts++;
            }

            var receiveWait = nextSendTime - sw.Elapsed;
            if (deadline.HasValue)
            {
                var remainingStageTime = deadline.Value - sw.Elapsed;
                if (remainingStageTime < receiveWait)
                    receiveWait = remainingStageTime;
            }
            if (receiveWait <= TimeSpan.Zero)
                continue;

            using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            receiveCts.CancelAfter(receiveWait);
            int receivedLength;
            try
            {
                receivedLength = await comm.ReceiveAsync(_receiveBuffer, receiveCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && receiveCts.IsCancellationRequested)
            {
                receivedLength = 0;
            }
            ct.ThrowIfCancellationRequested();
            var receivedAt = sw.Elapsed;
            if (deadline.HasValue && receivedAt >= deadline.Value)
                break;
            if (receivedLength > 0)
            {
                if (HasMatchingAcknowledgement(stage, frameIndex, receivedLength, out byte? progress))
                {
                    if (stage is Stage.StartErase or Stage.StartWrite or Stage.StartCheck)
                        ReportOperationProgress(stage, 100, ref loggedPercent, logProgress: false);
                    return;
                }
                if (progress.HasValue && (!hasReportedProgress || progress.Value > highestProgress))
                {
                    ReportOperationProgress(stage, Math.Max(highestProgress, progress.Value), ref loggedPercent);
                    hasReportedProgress = true;
                }
                if (timeout.HasValue && progress.HasValue && progress.Value > highestProgress)
                {
                    highestProgress = progress.Value;
                    deadline = receivedAt + timeout.Value;
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        if (highestProgress > 0)
            throw new TimeoutException($"IAP 阶段 {stage}，无进展超时（{timeoutSeconds} 秒，最高进度 {highestProgress}%）");
        throw new TimeoutException($"IAP 阶段 {stage}，帧号 {frameIndex}，等待应答超时（{timeoutSeconds} 秒）");
    }

    private bool HasMatchingAcknowledgement(Stage stage, uint frameIndex, int receivedLength, out byte? highestProgress)
    {
        highestProgress = null;
        var datagram = _receiveBuffer.AsSpan(0, receivedLength);
        int offset = 0;
        while (offset < datagram.Length)
        {
            var remaining = datagram[offset..];
            if (remaining.Length < 7)
                break;
            int frameLength = BinaryPrimitives.ReadUInt16BigEndian(remaining[2..4]) + 7;
            if (frameLength > remaining.Length)
                break;
            var status = _protocol.ReceivePacketAnalysis(stage, remaining[..frameLength], out byte progress, frameIndex);
            if (status == Status.Progress)
            {
                highestProgress = Math.Max(highestProgress ?? 0, progress);
            }
            else if (stage != Stage.SendData || status != Status.Success)
                LogService.ForModule(LogModule.Iap).Verbose("IAP 阶段 {Stage}，帧号 {FrameIndex}，响应状态 {Status}", stage, frameIndex, status);
            if (status == Status.Success)
                return true;
            offset += frameLength;
        }
        return false;
    }
}
