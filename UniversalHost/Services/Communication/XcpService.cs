using ReactiveUI;
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
using static UniversalHost.Models.XcpProtocol;

namespace UniversalHost.Services.Communication;

public static class XcpService
{
    public static XcpClient? Client = null;

    static XcpService()
    {
        GlobalStatus.Instance.WhenAnyValue(x => x.IsConnected)
            .Subscribe(async isConnected =>
            {
                if (!isConnected && Client != null)
                {
                    if (Client != null)
                    {
                        await Client.DisposeAsync();
                        Client = null;
                    }
                    GlobalStatus.Instance.IsMonitoring = false;
                    NotificationService.Show("设备已断开", "", NotificationType.Warning);
                }
            });
    }

    public static async Task CreateClientAsync()
    {
        if (Client != null)
        {
            await Client.DisposeAsync();
        }
        Client?.DisposeAsync().AsTask().Wait();
        Client = new XcpClient();
    }

    public static async Task DisposeClientAsync()
    {
        if (Client != null)
        {
            await Client.DisposeAsync();
            Client = null;
        }
        Debug.WriteLine("Client已清理");
        GlobalStatus.Instance.IsConnected = false;
        GlobalStatus.Instance.IsMonitoring = false;
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

    private readonly Dictionary<byte, OdtLayout> _pidOdtMap = [];

    private volatile TaskCompletionSource<byte[]>? _ctoTcs;

    private readonly struct SendItem
    {
        public readonly IMemoryOwner<byte> Owner;
        public readonly int Length;

        public SendItem(IMemoryOwner<byte> owner, int length)
        {
            Owner = owner;
            Length = length;
        }
    }

    private readonly Channel<SendItem> _highQueue = Channel.CreateUnbounded<SendItem>(
                                        new UnboundedChannelOptions
                                        {
                                            SingleReader = true,
                                            SingleWriter = false
                                        });

    private readonly Channel<SendItem> _lowQueue = Channel.CreateUnbounded<SendItem>(
                                        new UnboundedChannelOptions
                                        {
                                            SingleReader = true,
                                            SingleWriter = false
                                        });

    private readonly ICommService _comm;

    private readonly CancellationTokenSource _cts = new();

    private readonly SemaphoreSlim _ctoSemaphore = new(1, 1);

    private readonly MemoryPool<byte> _pool = MemoryPool<byte>.Shared;

    private readonly Channel<DaqFrame> _daqChannel = Channel.CreateBounded<DaqFrame>(
            new BoundedChannelOptions(128)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });

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

    public XcpClient()
    {
        _comm = ProjectSaveService.Instance.Settings.DeviceConfig.Mode switch
        {
            CommunicationMode.UDP => new UdpService(ProjectSaveService.Instance.Settings.UdpConfig.LocalAddress, ProjectSaveService.Instance.Settings.UdpConfig.XcpLocalPort, ProjectSaveService.Instance.Settings.UdpConfig.RemoteAddress, ProjectSaveService.Instance.Settings.UdpConfig.XcpRemotePort, ProjectSaveService.Instance.Settings.UdpConfig.TimeoutMilliseconds),
            //CommunicationMode.Serial => new SerialService(ProjectSaveService.Instance.Settings.SerialConfig),//TODO : 串口通信
            _ => throw new Exception("Unsupported communication Mode")
        };
        _timeoutMs = ProjectSaveService.Instance.Settings.DeviceConfig.Mode switch
        {
            CommunicationMode.UDP => ProjectSaveService.Instance.Settings.UdpConfig.TimeoutMilliseconds,
            //CommunicationMode.Serial => new SerialService(ProjectSaveService.Instance.Settings.SerialConfig),//TODO : 串口通信
            _ => throw new Exception("Unsupported communication Mode")
        };

        Std = new XcpStd(this);
        Cal = new XcpCal(this);
        Daq = new XcpDaq(this);
    }
    public async Task ConnectAsync()
    {
        _txTask = Task.Run(SenderLoop);
        _rxTask = Task.Run(ReceiverLoop);
        //protocol 解析失败会throw
        DeviceStatus.ConnectRes = await Std.ConnectAsync();
        DeviceStatus.GetStatusRes = await Std.GetStatusAsync();
        GlobalStatus.Instance.IsConnected = true;
        _heartbeatTask = Task.Run(HeartbeatLoop);
        _daqTask = Task.Run(DaqLoop);
    }
    public async Task DisconnectAsync()
    {
        try
        {
            await Std.DisonnectAsync();
        }
        finally
        {
            GlobalStatus.Instance.IsConnected = false;
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

        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            IMemoryOwner<byte> owner = _pool.Rent(280);
            Memory<byte> mem = owner.Memory;

            int xcpLen = TCommand.Encode(mem.Span[4..], args);

            ushort ctr = (ushort)Interlocked.Increment(ref _ctr);

            BinaryPrimitives.WriteUInt16LittleEndian(mem.Span, (ushort)xcpLen);
            BinaryPrimitives.WriteUInt16LittleEndian(mem.Span[2..], ctr);

            _ctoTcs = tcs;

            // 入队
            await _highQueue.Writer.WriteAsync(new SendItem(owner, xcpLen + 4), _cts.Token);

            using var timeout = new CancellationTokenSource(_timeoutMs);

            byte[] raw = await tcs.Task.WaitAsync(timeout.Token);

            return TCommand.Decode(raw, DeviceStatus.ConnectRes.IsLittleEndian);
        }
        finally
        {
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
        buffer = await Std.ShortUploadAsync(agLen, symbolRuntime.Symbol.Address);
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
        await Cal.ShortDownloadAsync(symbolRuntime.Symbol.Address, symbolRuntime.StringToValue());
        Serilog.Log.Information($"CAL {symbolRuntime.Symbol.Name}({symbolRuntime.Symbol.Alias}) : {OldValue} -> {NewValue}");
        return true;
    }

    public async Task StartDaq(IReadOnlyList<SymbolRuntime> symbolRuntimes)
    {
        _pidOdtMap.Clear();
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
            await Daq.WriteDaqAsync(sizeInAg, item.Symbol.Address);
            layout.Entries.Add(new DaqEntry()
            {
                Size = CalculateByteLenOfValue(DeviceStatus.ConnectRes.Granularity, item.ValueSizeInBytes),
                Symbol = item
            });
        }

        await Daq.SetDaqListModeAsync();

        byte firstPid = await Daq.SelectDaqListAsync();
        await Daq.StartSynchAsync();
        layout.Pid = (byte)(firstPid + layout.Odt);
        _pidOdtMap.Add(layout.Pid, layout);
        GlobalStatus.Instance.IsMonitoring = true;
    }

    public async Task StopDaq()
    {
        await Daq.StopDaqListAsync(0);
        GlobalStatus.Instance.IsMonitoring = false;
    }

    private async Task SenderLoop()
    {
        var high = _highQueue.Reader;
        var low = _lowQueue.Reader;

        while (!_cts.IsCancellationRequested)
        {
            // === 只要高优先级（CTO命令）有包，优先发 ===
            while (high.TryRead(out var highItem))
            {
                await SendInternal(highItem);
            }

            // 吞吐模式：高优先级发完了，再低优先级（DTO数据） ===
            if (low.TryRead(out var lowItem))
            {
                await SendInternal(lowItem);
                continue; // 发完立刻重回头部，检查有没有突发的高优先级
            }

            // 只要任何一个队列重新进包，就会唤醒线程
            try
            {
                // 利用 Task.WhenAny 异步监测谁先有数据
                await Task.WhenAny(
                    high.WaitToReadAsync(_cts.Token).AsTask(),
                    low.WaitToReadAsync(_cts.Token).AsTask()
                );
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendInternal(SendItem item)
    {
        try
        {
            await _comm.SendAsync(item.Owner.Memory[..item.Length], _cts.Token);
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
        while (!_cts.IsCancellationRequested)
        {
            IMemoryOwner<byte> owner = _pool.Rent(1600);


            int len = await _comm.ReceiveAsync(owner.Memory, _cts.Token);
            if (len <= 0)
            {
                owner.Dispose();
                continue;
            }

            // 收到有效数据后
            ResetHeartbeatTimer();

            ushort xcp_payload_len = BinaryPrimitives.ReadUInt16LittleEndian(owner.Memory.Span.Slice(0, 2));

            byte pid = owner.Memory.Span[4];

            if (pid >= 0xFC)
            {
                HandleCto(owner.Memory.Slice(4, len - 4));
                owner.Dispose();
            }
            else
            {
                await HandleDaq(owner, len);
            }
        }
    }
    private void HandleCto(ReadOnlyMemory<byte> data)
    {
        var tcs = Interlocked.Exchange(ref _ctoTcs, null);
        tcs?.TrySetResult(data.ToArray());
    }
    private ValueTask HandleDaq(IMemoryOwner<byte> owner, int length)
    {
        byte pid = owner.Memory.Span[4];

        var frame = new DaqFrame(pid, Stopwatch.GetTimestamp(), owner, length);

        return _daqChannel.Writer.WriteAsync(frame, _cts.Token);
    }
    private async Task DaqLoop()
    {
        await foreach (var frame in _daqChannel.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                if (!_pidOdtMap.TryGetValue(frame.Pid, out var layout))
                    continue;

                var payload = frame.Data.Span[12..];

                int offset = 0;

                foreach (var entry in layout.Entries)
                {
                    int size = entry.Symbol.ValueSizeInBytes;

                    entry.Symbol.UpdateValueFromBytes(payload.Slice(offset, size), DeviceStatus.ConnectRes.IsLittleEndian);

                    offset += entry.Size;
                }
            }
            catch (Exception ex)
            {
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

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            long now = Stopwatch.GetTimestamp();
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
                GlobalStatus.Instance.IsConnected = true;

                // 心跳本身也是一次有效通信，更新时间戳避免连续发送
                Interlocked.Exchange(ref _lastActivityTimestamp, Stopwatch.GetTimestamp());
            }
            catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException)
            {
                Debug.WriteLine($"[心跳警告] GET_STATUS 响应超时，正在尝试通过 SYNCH 命令复位协议状态机... 异常详情: {ex.Message}");

                try
                {
                    await Std.SyncAsync();
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

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try { if (_rxTask != null) await _rxTask; } catch (OperationCanceledException) { }
        try { if (_txTask != null) await _txTask; } catch (OperationCanceledException) { }
        try { if (_daqTask != null) await _daqTask; } catch (OperationCanceledException) { }
        try { if (_heartbeatTask != null) await _heartbeatTask; } catch (OperationCanceledException) { }

        _cts.Dispose();
        await _comm.DisposeAsync();
        _ctoSemaphore.Dispose();
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

        public Task<bool> SetDaqListModeAsync(ushort daqList = 0, ushort eventChannel = 0, byte prescaler = 0, byte priority = 0xff)
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
