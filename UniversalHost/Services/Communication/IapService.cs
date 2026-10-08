using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Services.Communication.Serial;
using static UniversalHost.Models.IapProtocol;

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

        Report("发送数据", 10);
        for (uint frameIndex = 0; frameIndex < _protocol.FrameNum; frameIndex++)
        {
            await RunRequestAsync(comm, Stage.SendData, null, ct,
                _retryTimes, _receiveTimeout, frameIndex);
            if (frameIndex % 10 == 0 || frameIndex == _protocol.FrameNum - 1)
                _progress.Report(10 + 65.0 * (frameIndex + 1) / _protocol.FrameNum);
        }

        Report("确认接收", 75);
        await RunRequestAsync(comm, Stage.SendComplete, null, ct,
            _retryTimes, _receiveTimeout);
        _progress.Report(78);

        if (!_protocol.IsFlashPerFrame)
        {
            Report("擦除目标", 78);
            await RunRequestAsync(comm, Stage.StartErase, _config.WaitForEraseTimeoutSeconds, ct);
            Report("写入目标", 82);
            await RunRequestAsync(comm, Stage.StartWrite, _config.WaitForWriteTimeoutSeconds, ct);
        }

        Report("校验目标", 90);
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
            throw new InvalidOperationException("IAP 通信接收超时及各阶段总超时必须大于 0");
    }

    private void Report(string stage, double progress)
    {
        _stage.Report(stage);
        _progress.Report(progress);
        Serilog.Log.Verbose("IAP {Stage}", stage);
    }

    // retryTimes 为 null 时仅受总超时限制；其他请求首次发送外最多重发 K 次。
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
        var sw = Stopwatch.StartNew();
        using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue)
            stageCts.CancelAfter(timeout.Value);
        int packetLength = _protocol.GetSendPacket(_sendBuffer, stage, frameIndex);
        var packet = _sendBuffer.AsMemory(0, packetLength);
        int attempts = 0;
        var nextSendTime = TimeSpan.Zero;
        try
        {
            while (!timeout.HasValue || sw.Elapsed < timeout.Value)
            {
                ct.ThrowIfCancellationRequested();
                stageCts.Token.ThrowIfCancellationRequested();
                if (attempts == 0 || sw.Elapsed >= nextSendTime)
                {
                    if (retryTimes.HasValue && attempts >= retryTimes.Value + 1)
                        throw new TimeoutException($"IAP 阶段 {stage}，帧号 {frameIndex}，超过重试次数（{retryTimes} 次重试）");
                    // 每次重发完全相同的请求，不因迟到或无关 ACK 立即重发。
                    nextSendTime = sw.Elapsed + interval;
                    await comm.SendAsync(packet, stageCts.Token);
                    // 保留耗时操作 0.5 秒的发送周期；低波特率时至少留出发送完成后的完整应答窗口。
                    if (serialResponseWait.HasValue && sw.Elapsed + serialResponseWait.Value > nextSendTime)
                        nextSendTime = sw.Elapsed + serialResponseWait.Value;
                    attempts++;
                    if (attempts > 1)
                        Serilog.Log.Verbose("IAP 阶段 {Stage}，帧号 {FrameIndex}，第 {Attempt} 次发送", stage, frameIndex, attempts);
                }

                var receiveWait = nextSendTime - sw.Elapsed;
                if (timeout.HasValue)
                {
                    var remainingStageTime = timeout.Value - sw.Elapsed;
                    if (remainingStageTime < receiveWait)
                        receiveWait = remainingStageTime;
                }
                if (receiveWait <= TimeSpan.Zero)
                    continue;

                using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(stageCts.Token);
                receiveCts.CancelAfter(receiveWait);
                int receivedLength;
                try
                {
                    receivedLength = await comm.ReceiveAsync(_receiveBuffer, receiveCts.Token);
                }
                catch (OperationCanceledException) when (!stageCts.IsCancellationRequested && receiveCts.IsCancellationRequested)
                {
                    receivedLength = 0;
                }
                ct.ThrowIfCancellationRequested();
                stageCts.Token.ThrowIfCancellationRequested();
                if (timeout.HasValue && sw.Elapsed >= timeout.Value)
                    break;
                if (receivedLength > 0 && HasMatchingAcknowledgement(stage, frameIndex, receivedLength))
                    return;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && stageCts.IsCancellationRequested)
        {
            throw new TimeoutException($"IAP 阶段 {stage}，帧号 {frameIndex}，等待应答超时（{timeoutSeconds} 秒）");
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException($"IAP 阶段 {stage}，帧号 {frameIndex}，等待应答超时（{timeoutSeconds} 秒）");
    }

    private bool HasMatchingAcknowledgement(Stage stage, uint frameIndex, int receivedLength)
    {
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
            var status = _protocol.ReceivePacketAnalysis(stage, remaining[..frameLength], frameIndex);
            if (stage != Stage.SendData || frameIndex % 10 == 0 || status != Status.Success)
                Serilog.Log.Verbose("IAP 阶段 {Stage}，帧号 {FrameIndex}，响应状态 {Status}", stage, frameIndex, status);
            if (status == Status.Success)
                return true;
            offset += frameLength;
        }
        return false;
    }
}
