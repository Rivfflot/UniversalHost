using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Services.Communication.Serial;
using static UniversalHost.Models.XcpProtocol;

namespace UniversalHost.Services.Communication;

public static class XcpService
{
    public static XcpClient? Client { get; private set; }
    private static readonly SemaphoreSlim ClientGate = new(1, 1);

    static XcpService()
    {
        GlobalStatus.Instance.WhenAnyValue(x => x.IsConnected)
            .Subscribe(isConnected =>
            {
                if (!isConnected && Client is { } client)
                    _ = DisposeDisconnectedClientAsync(client);
            });
    }

    public static async Task ConnectAsync()
    {
        // 重复入口立即拒绝，不能排队后清理另一个入口刚建立的连接。
        if (!await ClientGate.WaitAsync(0))
            throw new InvalidOperationException("设备连接正在建立或清理，请稍后再试");
        try
        {
            if (Client != null)
                throw new InvalidOperationException("设备连接尚未关闭，请先断开连接");
            var settings = ProjectSaveService.Instance.Settings;
            Client = await Task.Run(() => new XcpClient(settings));
            try
            {
                await Client.ConnectAsync();
            }
            catch
            {
                try { await Client.DisposeAsync(); }
                catch (Exception ex) { Serilog.Log.Error(ex, "连接失败后的 XCP 清理异常"); }
                Client = null;
                GlobalStatus.Instance.IsConnected = false;
                GlobalStatus.Instance.IsMonitoring = false;
                throw;
            }
        }
        finally { ClientGate.Release(); }
    }

    public static async Task DisconnectAsync()
    {
        await ClientGate.WaitAsync();
        try
        {
            if (Client is not { } client)
                return;
            try { await client.DisconnectAsync(); }
            finally
            {
                try { await client.DisposeAsync(); }
                finally
                {
                    Client = null;
                    GlobalStatus.Instance.IsConnected = false;
                    GlobalStatus.Instance.IsMonitoring = false;
                }
            }
        }
        finally { ClientGate.Release(); }
    }

    private static async Task DisposeDisconnectedClientAsync(XcpClient expectedClient)
    {
        try { await DisposeClientAsync(expectedClient); }
        catch (Exception ex) { Serilog.Log.Error(ex, "设备连接清理失败"); }
    }

    public static Task DisposeClientAsync() => DisposeClientAsync(null);

    private static async Task DisposeClientAsync(XcpClient? expectedClient)
    {
        await ClientGate.WaitAsync();
        try
        {
            if (expectedClient != null && !ReferenceEquals(Client, expectedClient))
                return;
            if (Client is { } client)
            {
                try { await client.DisposeAsync(); }
                finally { Client = null; }
            }
            GlobalStatus.Instance.IsConnected = false;
            GlobalStatus.Instance.IsMonitoring = false;
        }
        finally { ClientGate.Release(); }
    }
}

public class XcpClient : IAsyncDisposable
{
    public readonly struct DaqFrame : IDisposable
    {
        public readonly byte Pid;
        public readonly long Timestamp;

        public readonly IMemoryOwner<byte> Owner;
        public readonly int Length;

        public ReadOnlyMemory<byte> Data
            => Owner.Memory[..Length];

        public DaqFrame(
            byte pid,
            long timestamp,
            IMemoryOwner<byte> owner,
            int length)
        {
            Pid = pid;
            Timestamp = timestamp;
            Owner = owner;
            Length = length;
        }
        public void Dispose()
        {
            Owner.Dispose();
        }
    }
    public struct DaqEntry
    {
        public SymbolRuntime Symbol { get; init; }

        public byte Size { get; init; }
    }

    public struct OdtLayout
    {
        // First Pid
        public byte Pid;
        // DAQ 列表号
        public ushort DaqList;
        // Odt编号
        public byte Odt;
        // ODT Entries 数量
        public List<DaqEntry> Entries { get; }
        public OdtLayout()
        {
            Pid = 0;
            DaqList = 0;
            Odt = 0;
            Entries = new List<DaqEntry>();
        }
    }

    // 在发送 START 前一次性发布完整布局，避免 DAQ 读取线程看到布局被修改。
    private volatile Dictionary<byte, OdtLayout> _pidOdtMap = [];
    private bool _acceptDaqFrames;

    private volatile TaskCompletionSource<byte[]>? _ctoTcs;

    private readonly struct SendItem
    {
        public readonly IMemoryOwner<byte> Owner;
        public readonly int Length;
        public readonly TaskCompletionSource Sent;

        public SendItem(IMemoryOwner<byte> owner, int length, TaskCompletionSource sent)
        {
            Owner = owner;
            Length = length;
            Sent = sent;
        }
    }

    private readonly Channel<SendItem> _highQueue = Channel.CreateUnbounded<SendItem>(
                                        new UnboundedChannelOptions
                                        {
                                            SingleReader = true,
                                            SingleWriter = false
                                        });

    private readonly ICommService _comm;
    private readonly IDisposable _session;
    private readonly SerialConnectionOptions? _serialOptions;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _started;
    private int _disconnecting;

    private readonly CancellationTokenSource _cts = new();

    private readonly SemaphoreSlim _ctoSemaphore = new(1, 1);

    private readonly MemoryPool<byte> _pool = MemoryPool<byte>.Shared;

    private readonly Channel<DaqFrame> _daqChannel;
    public XcpReceiveStatistics ReceiveStatistics { get; } = new();

    private int _ctr;

    private readonly int _timeoutMs;

    private Task? _txTask;
    private Task? _rxTask;
    private Task? _daqTask;
    private Task? _heartbeatTask;

    private XcpStd Std { get; init; }
    private XcpCal Cal { get; init; }
    private XcpDaq Daq { get; init; }

    public struct DeviceResponse
    {
        public XcpProtocol.Std.ConnectResponse ConnectRes;
        public XcpProtocol.Std.GetStatusResponse GetStatusRes;
        public XcpProtocol.Daq.GetDaqListInfoResponse GetDaqListInfoRes;
    }
    public DeviceResponse DeviceStatus = default;

    public XcpClient() : this(ProjectSaveService.Instance.Settings) { }

    internal XcpClient(ProjectSettings settings, Func<ICommService>? createCommService = null)
    {
        // 1024帧队列，10kHz下0.1s
        _daqChannel = Channel.CreateBounded<DaqFrame>(
            new BoundedChannelOptions(1024)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            }, itemDropped: frame =>
            {
                ReceiveStatistics.RecordQueueDrop();
                frame.Dispose();
            });
        var mode = settings.DeviceConfig.Mode;
        _serialOptions = mode == CommunicationMode.Serial ? new SerialConnectionOptions(settings.SerialConfig) : null;
        _timeoutMs = mode == CommunicationMode.Serial
            ? _serialOptions!.TimeoutMilliseconds : settings.UdpConfig.TimeoutMilliseconds;
        if (_timeoutMs <= 0)
            throw new InvalidOperationException("XCP 通信超时必须大于 0");
        _session = CommunicationSessionCoordinator.AcquireXcp(mode, _serialOptions);
        try
        {
            _comm = createCommService?.Invoke() ?? (mode switch
            {
                CommunicationMode.UDP => new UdpService(settings.UdpConfig.LocalAddress, settings.UdpConfig.XcpLocalPort,
                    settings.UdpConfig.RemoteAddress, settings.UdpConfig.XcpRemotePort, _timeoutMs * 10,
                    receiveBufferSize: 4 * 1024 * 1024),
                CommunicationMode.Serial => new SerialTransportService(_serialOptions!, SerialProtocol.Xcp,
                    ReceiveStatistics.RecordMalformedDatagram),
                _ => throw new NotSupportedException("不支持该 XCP 通信模式")
            });
        }
        catch
        {
            _session.Dispose();
            throw;
        }

        Std = new XcpStd(this);
        Cal = new XcpCal(this);
        Daq = new XcpDaq(this);
    }
    public async Task ConnectAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("XCP 会话不能重复连接");
        try
        {
            _txTask = Task.Run(() => RunLoopAsync(SenderLoop));
            _rxTask = Task.Run(() => RunLoopAsync(ReceiverLoop));
            DeviceStatus.ConnectRes = await Std.ConnectAsync();
            DeviceStatus.GetStatusRes = await Std.GetStatusAsync();
            lock (_disposeGate)
            {
                _cts.Token.ThrowIfCancellationRequested();
                GlobalStatus.Instance.IsConnected = true;
                _heartbeatTask = Task.Run(() => RunLoopAsync(HeartbeatLoop));
                _daqTask = Task.Run(() => RunLoopAsync(DaqLoop));
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }
    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disconnecting, 1) != 0)
            throw new InvalidOperationException("XCP 会话正在断开");
        try
        {
            if (_heartbeatTask != null)
                await _heartbeatTask;
            if (Volatile.Read(ref _acceptDaqFrames) || DeviceStatus.GetStatusRes.DaqRunning)
                await StopDaq();
            await Std.DisonnectAsync();
        }
        finally
        {
            await DisposeAsync();
        }
    }

    public async Task ExecuteUserCmd(byte subCommand)
    {
        await Std.UserCmdAsync(subCommand, []);
    }

    private async Task<TResponse> SendCtoAsync<TCommand, TParams, TResponse>(TParams args)
                        where TCommand : IXcpCommand<TParams, TResponse>
    {
        await _ctoSemaphore.WaitAsync(_cts.Token);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            IMemoryOwner<byte> owner = _pool.Rent(280);
            ushort ctr = (ushort)Interlocked.Increment(ref _ctr);
            try
            {
                Memory<byte> mem = owner.Memory;
                int xcpLen = TCommand.Encode(mem.Span[4..], args);

                BinaryPrimitives.WriteUInt16LittleEndian(mem.Span, (ushort)xcpLen);
                BinaryPrimitives.WriteUInt16LittleEndian(mem.Span[2..], ctr);

                _ctoTcs = tcs;

                // 入队成功后由发送线程归还内存。
                await _highQueue.Writer.WriteAsync(new SendItem(owner, xcpLen + 4, sent), _cts.Token);
            }
            catch
            {
                owner.Dispose();
                throw;
            }

            // 队列等待及请求线上发送不计为设备应答超时。
            await sent.Task.WaitAsync(_cts.Token);
            TimeSpan responseTimeout = _serialOptions?.GetResponseTimeout(
                (DeviceStatus.ConnectRes.MaxCtoLen == 0 ? byte.MaxValue : DeviceStatus.ConnectRes.MaxCtoLen) + 7)
                ?? TimeSpan.FromMilliseconds(_timeoutMs);
            try
            {
                byte[] raw = await tcs.Task.WaitAsync(responseTimeout, _cts.Token);
                return TCommand.Decode(raw, DeviceStatus.ConnectRes.IsLittleEndian);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"XCP {typeof(TCommand).Name} (CTR={ctr}) 响应超时（{responseTimeout.TotalMilliseconds:F0} ms）。", ex);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _ctoTcs, null, tcs);
            _ctoSemaphore.Release();
        }
    }
    public int CalculateByteLen(byte sizeOfSymbolInAg)
    {
        return DeviceStatus.ConnectRes.Granularity switch
        {
            XcpProtocol.Std.AddressGranularity.Byte => sizeOfSymbolInAg,
            XcpProtocol.Std.AddressGranularity.Word => sizeOfSymbolInAg * 2,
            _ => sizeOfSymbolInAg * 4,
        };
    }
    public byte CalculateAgLen(byte sizeOfSymbolInBytes)
    {
        return CalculateAgLenOfValue(DeviceStatus.ConnectRes.Granularity, sizeOfSymbolInBytes);
    }
    public ushort ReadUInt16(ReadOnlySpan<byte> buffer)
    {
        if (DeviceStatus.ConnectRes.IsLittleEndian)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        }
        else
        {
            return BinaryPrimitives.ReadUInt16BigEndian(buffer);
        }
    }
    /// <summary>
    /// 使用SHORT_UPLOAD指令将指定的变量值上传至上位机。适用于低频传输，如数据标定。
    /// </summary>
    /// <param name="symbolRuntime"></param>
    /// <returns></returns>
    public async Task UploadSymbol(SymbolRuntime symbolRuntime)
    {
        Span<byte> buffer = stackalloc byte[8];

        byte agLen = CalculateAgLenOfValue(DeviceStatus.ConnectRes.Granularity, symbolRuntime.ValueSizeInBytes);
        buffer = await Std.ShortUploadAsync(agLen, symbolRuntime.Symbol.ActualAddress);
        symbolRuntime.UpdateValueFromBytes(buffer, DeviceStatus.ConnectRes.IsLittleEndian);
        symbolRuntime.ValueToString();
    }
    /// <summary>
    /// 使用SHORT_UPLOAD指令将指定的变量值上传至上位机。适用于低频传输。
    /// </summary>
    /// <param name="lenInByte">长度，单位字节</param>
    /// <param name="addr">32bit地址</param>
    /// <returns></returns>
    public async Task<byte[]> UploadSymbol(byte lenInByte, uint addr)
    {
        byte agLen = CalculateAgLenOfValue(DeviceStatus.ConnectRes.Granularity, lenInByte);
        return await Std.ShortUploadAsync(agLen, addr);
    }
    public Task<byte[]> UploadAgAsync(byte agNumber, uint agAddress)
    {
        return Std.ShortUploadAsync(agNumber, agAddress);
    }
    /// <summary>
    /// 将标定值下载至设备。同时根据UI字符串更新Value值和ValueHistory。
    /// </summary>
    /// <param name="symbolRuntime"></param>
    /// <returns></returns>
    public async Task<bool> DownloadSymbol(SymbolRuntime symbolRuntime)
    {
        string NewValue = symbolRuntime.ValueString;
        string OldValue = symbolRuntime.ValueToStringWithoutUpdate();
        //相同时不处理
        if (OldValue == NewValue)
        {
            return false;
        }
        await Cal.ShortDownloadAsync(symbolRuntime.Symbol.ActualAddress, symbolRuntime.StringToValue());
        Serilog.Log.Information($"CAL {symbolRuntime.Symbol.Name}({symbolRuntime.Symbol.Alias}) : {OldValue} -> {NewValue}");
        return true;
    }

    public async Task StartDaq(IReadOnlyList<SymbolRuntime> symbolRuntimes)
    {
        if (_serialOptions != null)
        {
            // 当前布局每次事件仅有一个 ODT；LEN 含 PID 及现有的 7 字节时间戳/对齐头。
            int payloadLength = 8;
            foreach (var symbol in symbolRuntimes)
                payloadLength += CalculateByteLenOfValue(DeviceStatus.ConnectRes.Granularity, symbol.ValueSizeInBytes);
            if (payloadLength > DeviceStatus.ConnectRes.MaxDtoLen)
                throw new InvalidOperationException($"串口 DAQ 布局长度 {payloadLength} 超过设备 MAX_DTO {DeviceStatus.ConnectRes.MaxDtoLen}");
            int wireLength = SerialTransportProtocol.GetMaxWireLength(payloadLength + 7);
            double byteRate = _serialOptions.BaudRate / _serialOptions.BitsPerCharacter;
            Serilog.Log.Information("串口 DAQ 带宽预算：每事件线上上限={WireLength} 字节，接收理论上限={ByteRate:F0} 字节/秒，预留 20% 带宽时建议采样不超过 {SampleRate:F1} 次/秒；采样率由设备配置",
                wireLength, byteRate, byteRate * 0.8 / wireLength);
        }
        Volatile.Write(ref _acceptDaqFrames, false);
        OdtLayout layout = new OdtLayout();
        layout.DaqList = 0;
        layout.Odt = 0;
        await Daq.FreeDaqAsync();
        await Daq.AllocDaqAsync();
        await Daq.AllocOdtAsync();
        if (symbolRuntimes.Count > byte.MaxValue)
        {
            NotificationService.Show("监控停止", $"监控变量过多，当前监控数量：{symbolRuntimes.Count}，最大监控数量：255", NotificationType.Warning);
            return;
        }
        await Daq.AllocOdtEntryAsync((byte)symbolRuntimes.Count);

        await Daq.SetDaqPtrAsync(0, 0, 0);
        foreach (var item in symbolRuntimes)
        {
            byte sizeInAg = CalculateAgLenOfValue(DeviceStatus.ConnectRes.Granularity, item.ValueSizeInBytes);
            await Daq.WriteDaqAsync(sizeInAg, item.Symbol.ActualAddress);
            layout.Entries.Add(new DaqEntry()
            {
                Size = CalculateByteLenOfValue(DeviceStatus.ConnectRes.Granularity, item.ValueSizeInBytes),
                Symbol = item
            });
        }

        await Daq.SetDaqListModeAsync();

        byte firstPid = await Daq.SelectDaqListAsync();
        layout.Pid = (byte)(firstPid + layout.Odt);
        _pidOdtMap = new Dictionary<byte, OdtLayout> { [layout.Pid] = layout };
        Volatile.Write(ref _acceptDaqFrames, true);
        try
        {
            await Daq.StartSynchAsync();
        }
        catch
        {
            Volatile.Write(ref _acceptDaqFrames, false);
            throw;
        }
        GlobalStatus.Instance.IsMonitoring = true;
    }

    public async Task StopDaq()
    {
        await Daq.StopDaqListAsync(0);
        Volatile.Write(ref _acceptDaqFrames, false);
        GlobalStatus.Instance.IsMonitoring = false;
    }

    private async Task SenderLoop()
    {
        await foreach (var item in _highQueue.Reader.ReadAllAsync(_cts.Token))
            await SendInternal(item);
    }

    private async Task SendInternal(SendItem item)
    {
        try
        {
            await _comm.SendAsync(item.Owner.Memory[..item.Length], _cts.Token);
            item.Sent.TrySetResult();
        }
        catch (Exception ex)
        {
            item.Sent.TrySetException(ex);
            throw;
        }
        finally
        {
            //不管是否发送成功都要归还内存
            item.Owner.Dispose();
        }
        // response 在 ReceiverLoop 处理
    }
    /// <summary>
    /// 记录上一次收到有效数据包的时间，用于间隔1s发送心跳。
    /// </summary>
    private long _lastActivityTimestamp = Stopwatch.GetTimestamp();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetHeartbeatTimer()
    {
        Interlocked.Exchange(ref _lastActivityTimestamp, Stopwatch.GetTimestamp());
    }
    private async Task ReceiverLoop()
    {
        using IMemoryOwner<byte> owner = _pool.Rent(ushort.MaxValue + 4);
        while (!_cts.IsCancellationRequested)
        {
            int len = await _comm.ReceiveAsync(owner.Memory, _cts.Token);
            if (len <= 0)
                continue;
            ReceiveStatistics.RecordDatagram();

            // 下位机会将多个带 LEN/CTR 的 XCP 帧合并在同一个 UDP 数据报中。
            int offset = 0;
            while (offset < len)
            {
                if (len - offset < 5)
                {
                    ReceiveStatistics.RecordMalformedDatagram();
                    Serilog.Log.Warning("XCP 数据报帧头不完整：长度 {Length}，偏移 {Offset}", len, offset);
                    break;
                }

                int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(owner.Memory.Span.Slice(offset, 2));
                if (payloadLength == 0 || payloadLength > len - offset - 4)
                {
                    ReceiveStatistics.RecordMalformedDatagram();
                    Serilog.Log.Warning("XCP 数据报帧长度无效：LEN={PayloadLength}，长度 {Length}，偏移 {Offset}", payloadLength, len, offset);
                    break;
                }

                int frameLength = payloadLength + 4;
                ReadOnlyMemory<byte> frame = owner.Memory.Slice(offset, frameLength);
                // One counter per XCP message, including CRM/EV/SERV and every
                // message inside an accumulated UDP datagram. ushort wraps normally.
                ReceiveStatistics.RecordCounter(BinaryPrimitives.ReadUInt16LittleEndian(frame.Span[2..]));
                byte pid = frame.Span[4];
                ResetHeartbeatTimer();

                if (pid == 0xFF || pid == 0xFE)
                    HandleCto(frame[4..]);
                else if (pid < 0xFC)
                    HandleDaq(frame);
                // EV (0xFD) 和 SERV (0xFC) 是异步消息，不能完成正在等待的命令。

                // 此下位机的对齐填充已包含在 LEN 中，不再额外补齐。
                offset += frameLength;
            }
        }
    }
    private void HandleCto(ReadOnlyMemory<byte> data)
    {
        var tcs = Interlocked.Exchange(ref _ctoTcs, null);
        tcs?.TrySetResult(data.ToArray());
    }
    private void HandleDaq(ReadOnlyMemory<byte> data)
    {
        // CONNECT 期间或 DAQ 停止后到达的 DTO 不属于本轮监控。
        if (!Volatile.Read(ref _acceptDaqFrames))
        {
            ReceiveStatistics.RecordInactiveDaq();
            return;
        }

        ReceiveStatistics.RecordDaqReceived();
        // 每帧持有独立内存，接收缓冲区可立即复用。
        IMemoryOwner<byte> owner = _pool.Rent(data.Length);
        data.CopyTo(owner.Memory);
        var frame = new DaqFrame(data.Span[4], Stopwatch.GetTimestamp(), owner, data.Length);

        if (!_daqChannel.Writer.TryWrite(frame))
        {
            ReceiveStatistics.RecordQueueDrop();
            frame.Dispose();
        }
    }
    private async Task DaqLoop()
    {
        await foreach (var frame in _daqChannel.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                if (!_pidOdtMap.TryGetValue(frame.Pid, out var layout))
                {
                    ReceiveStatistics.RecordUnknownPid();
                    continue;
                }

                var payload = frame.Data.Span[12..];

                int offset = 0;

                foreach (var entry in layout.Entries)
                {
                    int size = entry.Symbol.ValueSizeInBytes;

                    entry.Symbol.UpdateValueFromBytes(payload.Slice(offset, size), DeviceStatus.ConnectRes.IsLittleEndian);

                    offset += entry.Size;
                }
                ReceiveStatistics.RecordDaqDecoded();
            }
            catch (Exception ex)
            {
                ReceiveStatistics.RecordParseError();
                NotificationService.Show("DAQ解析错误", ex.Message, NotificationType.Warning);
                Debug.WriteLine(ex);
            }
            finally
            {
                frame.Dispose(); // 安全释放内存所有权归还 MemoryPool
            }
        }
    }

    private async Task HeartbeatLoop()
    {
        long intervalTicks = Stopwatch.Frequency; // 1 秒对应的 ticks
        long lastDiagnosticsTimestamp = Stopwatch.GetTimestamp();
        long lastErrorCount = 0;
        long lastInactiveDaqCount = 0;

        while (!_cts.IsCancellationRequested && Volatile.Read(ref _disconnecting) == 0)
        {
            try
            {
                await Task.Delay(50, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (Volatile.Read(ref _disconnecting) != 0)
                break;

            long now = Stopwatch.GetTimestamp();
            if (now - lastDiagnosticsTimestamp >= intervalTicks)
            {
                lastDiagnosticsTimestamp = now;
                var stats = ReceiveStatistics.Snapshot();
                if (stats.InactiveDaqFrames != lastInactiveDaqCount)
                {
                    lastInactiveDaqCount = stats.InactiveDaqFrames;
                    Serilog.Log.Verbose("XCP 监控未运行时忽略 DTO：累计={Ignored}", stats.InactiveDaqFrames);
                }
                long errorCount = stats.CounterGapFrames + stats.DuplicateOrReorderedFrames +
                    stats.QueueDrops + stats.UnknownPidFrames + stats.ParseErrors + stats.MalformedDatagrams;
                if (errorCount != lastErrorCount)
                {
                    lastErrorCount = errorCount;
                    Serilog.Log.Warning("XCP 接收累计诊断：CTR前向缺口={CounterGaps}，重复/乱序={Reordered}，DAQ队列丢帧={QueueDrops}，未知PID={UnknownPid}，解析失败={ParseErrors}，无效数据报文={Malformed}，DAQ接收/解析={Received}/{Decoded}",
                        stats.CounterGapFrames, stats.DuplicateOrReorderedFrames, stats.QueueDrops,
                        stats.UnknownPidFrames, stats.ParseErrors, stats.MalformedDatagrams,
                        stats.DaqReceived, stats.DaqDecoded);
                }
            }
            long last = Interlocked.Read(ref _lastActivityTimestamp);

            // 1 秒内有过通信，跳过
            if ((now - last) < intervalTicks)
                continue;

            // 发送前再次确认，消除 50ms 轮询窗口的竞态
            if ((Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastActivityTimestamp)) < intervalTicks)
                continue;

            try
            {
                DeviceStatus.GetStatusRes = await Std.GetStatusAsync();
                _cts.Token.ThrowIfCancellationRequested();
                GlobalStatus.Instance.IsConnected = true;

                // 心跳本身也是一次有效通信，更新时间戳避免连续发送
                Interlocked.Exchange(ref _lastActivityTimestamp, Stopwatch.GetTimestamp());
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException)
            {
                Debug.WriteLine($"[心跳警告] GET_STATUS 响应超时，正在尝试通过 SYNCH 命令复位协议状态机... 异常详情: {ex.Message}");

                try
                {
                    await Std.SyncAsync();
                    _cts.Token.ThrowIfCancellationRequested();
                    Debug.WriteLine("[心跳成功] 协议重新同步成功，链路恢复健康。");
                    GlobalStatus.Instance.IsConnected = true;

                    Interlocked.Exchange(ref _lastActivityTimestamp, Stopwatch.GetTimestamp());
                }
                catch (Exception syncEx)
                {
                    NotificationService.Show("设备断开", $"设备连接超时, {syncEx.Message}", NotificationType.Error);
                    Debug.WriteLine($"[心跳致命错误] SYNCH 命令复位超时。下位机失联。{syncEx.Message}");
                    Serilog.Log.Debug($"[心跳致命错误] SYNCH 命令复位超时。下位机失联。{syncEx.Message}");
                    GlobalStatus.Instance.IsConnected = false;
                    break;
                }
            }
            catch (Exception unkownEx)
            {
                NotificationService.Show("设备断开", $"[心跳未知错误] 通信组件发生异常: {unkownEx.Message}", NotificationType.Error);
                Debug.WriteLine($"[心跳未知错误] 通信组件发生异常: {unkownEx.Message}");
                Serilog.Log.Debug($"[心跳未知错误] 通信组件发生异常: {unkownEx.Message}");
                GlobalStatus.Instance.IsConnected = false;
                break;
            }
        }
    }

    private async Task RunLoopAsync(Func<Task> loop)
    {
        try { await loop(); }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "XCP 收发任务异常，终止设备会话");
            _ctoTcs?.TrySetException(ex);
            _cts.Cancel();
            GlobalStatus.Instance.IsConnected = false;
            GlobalStatus.Instance.IsMonitoring = false;
            // 不在当前任务内等待自己的结束；所有入口都共享同一份清理任务。
            _ = Task.Run(async () =>
            {
                try { await DisposeAsync(); }
                catch (Exception cleanupEx) { Serilog.Log.Error(cleanupEx, "XCP 会话清理失败"); }
            });
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _cts.Cancel();
        Volatile.Write(ref _acceptDaqFrames, false);
        _ctoTcs?.TrySetCanceled(_cts.Token);
        _highQueue.Writer.TryComplete();
        _daqChannel.Writer.TryComplete();
        try
        {
            // 先关闭底层，确保取消/拔出串口时读写任务能够结束。
            await _comm.DisposeAsync();
        }
        finally
        {
            if (_rxTask != null) await _rxTask;
            if (_txTask != null) await _txTask;
            if (_daqTask != null) await _daqTask;
            if (_heartbeatTask != null) await _heartbeatTask;
            while (_highQueue.Reader.TryRead(out var item))
            {
                item.Sent.TrySetCanceled(_cts.Token);
                item.Owner.Dispose();
            }
            while (_daqChannel.Reader.TryRead(out var frame))
                frame.Dispose();
            // 等待正在返回的业务命令释放信号量，再结束串口占用。
            await _ctoSemaphore.WaitAsync();
            _ctoSemaphore.Release();
            _ctoSemaphore.Dispose();
            _cts.Dispose();
            GlobalStatus.Instance.IsConnected = false;
            GlobalStatus.Instance.IsMonitoring = false;
            _session.Dispose();
        }
    }

    private class XcpStd
    {
        private readonly XcpClient _client;

        public XcpStd(XcpClient client)
        {
            _client = client;
        }
        public Task<Std.ConnectResponse> ConnectAsync(Std.ConnectMode mode = XcpProtocol.Std.ConnectMode.Normal)
        {
            return _client.SendCtoAsync<Std.ConnectCommand, Std.ConnectMode, Std.ConnectResponse>(mode);
        }
        public Task<Std.GetStatusResponse> GetStatusAsync()
        {
            return _client.SendCtoAsync<Std.GetStatusCommand, EmptyParam, Std.GetStatusResponse>(new EmptyParam());
        }
        public Task<bool> DisonnectAsync()
        {
            return _client.SendCtoAsync<Std.DisconnectCommand, EmptyParam, bool>(new EmptyParam());
        }
        public Task<bool> SyncAsync()
        {
            return _client.SendCtoAsync<Std.SyncCommand, EmptyParam, bool>(new EmptyParam());
        }
        public Task<bool> SetMtaAsync(uint addr)
        {
            Std.SetMtaParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                Extension = 0,
                Address = addr,
            };
            return _client.SendCtoAsync<Std.SetMtaCommand, Std.SetMtaParams, bool>(p);
        }
        /// <summary>
        /// 上传SetMta指定地址的数据
        /// </summary>
        /// <param name="agNumber">数据长度in AG</param>
        /// <returns>数据，长度为AG * agNumber</returns>
        public Task<byte[]> UploadAsync(byte agNumber)
        {
            return _client.SendCtoAsync<Std.UploadCommand, byte, byte[]>(agNumber);
        }
        /// <summary>
        /// 上传指定地址的数据
        /// </summary>
        /// <param name="agNumber">数据长度in AG</param>
        /// <param name="addr">地址</param>
        /// <returns>数据，长度为AG * agNumber</returns>
        public Task<byte[]> ShortUploadAsync(byte agNumber, uint addr)
        {

            Std.ShortUploadParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                Number = agNumber,
                Extension = 0,
                Address = addr,
            };
            return _client.SendCtoAsync<Std.ShortUploadCommand, Std.ShortUploadParams, byte[]>(p);
        }
        public Task<bool> UserCmdAsync(byte subCmd, byte[] parameters)
        {
            Std.UserCmdParams p = new()
            {
                SubCommand = subCmd,
                Parameters = parameters,
            };
            return _client.SendCtoAsync<Std.UserCmdCommand, Std.UserCmdParams, bool>(p);
        }
    }
    private class XcpCal
    {
        private readonly XcpClient _client;
        public XcpCal(XcpClient client) { _client = client; }
        /// <summary>
        /// 将数据下载至SET_MTA指定的地址。
        /// </summary>
        /// <param name="data">变量Value直接转换的字节数组，小端序</param>
        /// <returns></returns>
        public Task<bool> DownloadAsync(ReadOnlyMemory<byte> data)
        {
            Cal.DownloadParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                Ag = _client.DeviceStatus.ConnectRes.Granularity,
                Data = data,
            };
            return _client.SendCtoAsync<Cal.DownloadCommand, Cal.DownloadParams, bool>(p);
        }
        public Task<bool> ShortDownloadAsync(uint address, ReadOnlyMemory<byte> data)
        {
            Cal.ShortDownloadParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                Ag = _client.DeviceStatus.ConnectRes.Granularity,
                Extension = 0,
                Address = address,
                Data = data,
            };
            return _client.SendCtoAsync<Cal.ShortDownloadCommand, Cal.ShortDownloadParams, bool>(p);
        }

    }
    private class XcpDaq
    {
        private readonly XcpClient _client;

        public XcpDaq(XcpClient client)
        {
            _client = client;
        }

        public Task<bool> ClearDaqListAsync(ushort daqList = 0)
        {
            Daq.ClearDaqListParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                DaqList = daqList,
            };
            return _client.SendCtoAsync<Daq.ClearDaqListCommand, Daq.ClearDaqListParams, bool>(p);
        }
        public Task<bool> FreeDaqAsync()
        {
            return _client.SendCtoAsync<Daq.FreeDaqCommand, EmptyParam, bool>(new EmptyParam());
        }
        /// <summary>
        /// 分配DAQ列表
        /// </summary>
        /// <param name="daqListCount">要分配的DAQ list数量</param>
        /// <returns></returns>
        public Task<bool> AllocDaqAsync(ushort daqListCount = 1)
        {
            Daq.AllocDaqParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                DaqCount = daqListCount,
            };
            return _client.SendCtoAsync<Daq.AllocDaqCommand, Daq.AllocDaqParams, bool>(p);
        }
        /// <summary>
        /// 分配ODT表数量
        /// </summary>
        /// <param name="daqList">要分配的DAQ list</param>
        /// <param name="odtCount">该DAQ list中要分配的odt表数量</param>
        /// <returns></returns>
        public Task<bool> AllocOdtAsync(ushort daqList = 0, byte odtCount = 1)
        {
            Daq.AllocOdtParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                DaqList = daqList,
                OdtCount = odtCount,
            };
            return _client.SendCtoAsync<Daq.AllocOdtCommand, Daq.AllocOdtParams, bool>(p);
        }
        /// <summary>
        /// 在指定的DAQ列表的ODT表分配odt entry
        /// </summary>
        /// <param name="odtEntriesCount">要分配的odt entry数量</param>
        /// <param name="daqList">DAQ 编号</param>
        /// <param name="odtNumber">ODT 编号</param>
        /// <returns></returns>
        public Task<bool> AllocOdtEntryAsync(byte odtEntriesCount, ushort daqList = 0, byte odtNumber = 0)
        {
            Daq.AllocOdtEntryParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                DaqList = daqList,
                OdtNumber = odtNumber,
                OdtEntriesCount = odtEntriesCount
            };
            return _client.SendCtoAsync<Daq.AllocOdtEntryCommand, Daq.AllocOdtEntryParams, bool>(p);
        }
        public Task<bool> SetDaqPtrAsync(ushort daqList, byte odtNum, byte odtEntryNum)
        {
            Daq.SetDaqPtrParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                DaqListNumber = daqList,
                OdtNumber = odtNum,
                OdtEntryNumber = odtEntryNum,
            };
            return _client.SendCtoAsync<Daq.SetDaqPtrCommand, Daq.SetDaqPtrParams, bool>(p);
        }
        /// <summary>
        /// 写入DAQ ODT信息
        /// </summary>
        /// <param name="sizeOfDaqInAg">DAQ 大小 in AG</param>
        /// <param name="addr">Address</param>
        /// <returns></returns>
        public Task<bool> WriteDaqAsync(byte sizeOfDaqInAg, uint addr)
        {
            Daq.WriteDaqParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                BitOffset = 0,
                SizeOfDaq = sizeOfDaqInAg,
                Extension = 0,
                Address = addr,
            };
            return _client.SendCtoAsync<Daq.WriteDaqCommand, Daq.WriteDaqParams, bool>(p);
        }

        public Task<bool> SetDaqListModeAsync(ushort daqList = 0, ushort eventChannel = 0, byte prescaler = 1, byte priority = 0)
        {
            Daq.SetDaqListModeParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                Direction = false,
                Timestamp = true,
                PidOff = false,
                DaqList = daqList,
                EventChannel = eventChannel,
                RatePrescaler = prescaler,
                Priority = priority,
            };
            return _client.SendCtoAsync<Daq.SetDaqListModeCommand, Daq.SetDaqListModeParams, bool>(p);
        }

        /// <summary>
        /// 开始指定的DAQ list
        /// </summary>
        /// <param name="list"></param>
        /// <returns>第一PID</returns>
        public Task<byte> StartDaqListAsync(ushort list = 0)
        {
            Daq.StartStopDaqListParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                Mode = XcpProtocol.Daq.DaqListMode.Start,
                DaqList = list,
            };
            return _client.SendCtoAsync<Daq.StartStopDaqListCommand, Daq.StartStopDaqListParams, byte>(p);
        }
        public Task<byte> SelectDaqListAsync(ushort list = 0)
        {
            Daq.StartStopDaqListParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                Mode = XcpProtocol.Daq.DaqListMode.Select,
                DaqList = list,
            };
            return _client.SendCtoAsync<Daq.StartStopDaqListCommand, Daq.StartStopDaqListParams, byte>(p);
        }
        public Task<byte> StopDaqListAsync(ushort list = 0)
        {
            Daq.StartStopDaqListParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                Mode = XcpProtocol.Daq.DaqListMode.Stop,
                DaqList = list,
            };
            return _client.SendCtoAsync<Daq.StartStopDaqListCommand, Daq.StartStopDaqListParams, byte>(p);
        }

        public Task<Daq.GetDaqListInfoResponse> GetDaqListInfoAsync(ushort list = 0)
        {
            Daq.GetDaqListInfoParams p = new()
            {
                IsLittleEndian = _client.DeviceStatus.ConnectRes.IsLittleEndian,
                DaqList = list,
            };
            return _client.SendCtoAsync<Daq.GetDaqListInfoCommand, Daq.GetDaqListInfoParams, Daq.GetDaqListInfoResponse>(p);
        }

        public Task<bool> StartSynchAsync()
        {
            return _client.SendCtoAsync<Daq.StartStopSynchCommand, Daq.DaqListMode, bool>(XcpProtocol.Daq.DaqListMode.Start);
        }
        public Task<bool> StopSynchAsync()
        {
            return _client.SendCtoAsync<Daq.StartStopSynchCommand, Daq.DaqListMode, bool>(XcpProtocol.Daq.DaqListMode.Stop);
        }
    }
}
