using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models;

namespace UniversalHost.Services.Communication.Serial;

/// <summary>串口传输适配层：业务只收发完整 XCP 包或原 IAP 帧。</summary>
public sealed class SerialTransportService : ICommService
{
    private readonly ICommService _port;
    private readonly SerialProtocol _protocol;
    private readonly SerialFrameDecoder _decoder;
    private readonly bool _hasLocalEcho;
    private readonly Action? _frameRejected;
    private readonly byte[] _receiveBuffer = new byte[4096];
    private readonly byte[] _rawSendBuffer;
    private readonly byte[] _wireSendBuffer;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _echoGate = new();
    private readonly object _disposeGate = new();
    // 有界保存未消费的回显，重试不能覆盖前一次发送的回显期望。
    private const int MaxPendingEchoFrames = 32;
    private readonly List<byte[]> _echoPackets = [];
    private Task? _disposeTask;
    private int _receiveOffset, _receiveLength;
    private bool _sentSync;
    private long _reportedRejectedFrames;

    public SerialTransportService(SerialConnectionOptions options, SerialProtocol protocol, Action? frameRejected = null)
        : this(OpenPort(options, protocol), options, protocol, frameRejected) { }

    private static SerialPortService OpenPort(SerialConnectionOptions options, SerialProtocol protocol)
    {
        if (!Enum.IsDefined(protocol))
            throw new ArgumentOutOfRangeException(nameof(protocol));
        if (protocol == SerialProtocol.Xcp && options.DuplexMode != SerialDuplexMode.FullDuplex)
            throw new InvalidOperationException("半双工串口禁止 XCP");
        return new SerialPortService(options);
    }

    internal SerialTransportService(ICommService port, SerialConnectionOptions options,
        SerialProtocol protocol, Action? frameRejected = null)
    {
        if (protocol == SerialProtocol.Xcp && options.DuplexMode != SerialDuplexMode.FullDuplex)
            throw new InvalidOperationException("半双工串口禁止 XCP");
        _port = port;
        _protocol = protocol;
        _hasLocalEcho = options.HasLocalEcho;
        _frameRejected = frameRejected;
        _decoder = new SerialFrameDecoder(protocol, options.GetIncompleteFrameTimeout(protocol));
        _rawSendBuffer = new byte[SerialTransportProtocol.GetMaxRawLength(protocol)];
        // 额外 1 字节用于新会话第一次发送前的同步分隔符。
        _wireSendBuffer = new byte[SerialTransportProtocol.GetMaxWireLength(_rawSendBuffer.Length) + 1];
    }

    public void Send(ReadOnlySpan<byte> data) => SendAsync(data.ToArray()).GetAwaiter().GetResult();

    public async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            int prefix = _sentSync ? 0 : 1;
            _wireSendBuffer[0] = 0;
            int length = SerialTransportProtocol.Encode(_protocol, data.Span,
                _rawSendBuffer, _wireSendBuffer.AsSpan(prefix));
            if (_hasLocalEcho)
            {
                lock (_echoGate)
                {
                    if (_echoPackets.Count == MaxPendingEchoFrames)
                        throw new IOException("串口本机回显长期未返回，请检查适配器回显设置");
                    _echoPackets.Add(data.ToArray());
                }
            }
            try
            {
                int sent = await _port.SendAsync(_wireSendBuffer.AsMemory(0, prefix + length), linked.Token).ConfigureAwait(false);
                if (sent != prefix + length)
                    throw new IOException("串口线上帧未完整发送");
                _sentSync = true;
                return data.Length;
            }
            catch
            {
                _shutdown.Cancel();
                throw;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public byte[]? Receive()
    {
        byte[] buffer = new byte[SerialTransportProtocol.GetMaxRawLength(_protocol)];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            int length = ReceiveAsync(buffer, timeout.Token).GetAwaiter().GetResult();
            return buffer.AsSpan(0, length).ToArray();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return null; }
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _receiveGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                _decoder.ExpireCandidate(Stopwatch.GetTimestamp());
                ReportRejectedFrames();
                while (_receiveOffset < _receiveLength)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    bool valid = _decoder.TryReadByte(_receiveBuffer[_receiveOffset++],
                        Stopwatch.GetTimestamp(), out var packet);
                    ReportRejectedFrames();
                    if (!valid || IsLocalEcho(packet.Span))
                        continue;
                    if (packet.Length > buffer.Length)
                        throw new IOException("串口协议帧超出业务接收缓存");
                    packet.CopyTo(buffer);
                    return packet.Length;
                }
                _receiveOffset = _receiveLength = 0;
                try
                {
                    int length = await _port.ReceiveAsync(_receiveBuffer, linked.Token).ConfigureAwait(false);
                    if (length > 0)
                        _receiveLength = length;
                }
                catch (SerialReceiveException)
                {
                    _decoder.DiscardUntilDelimiter();
                    ReportRejectedFrames();
                }
            }
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    private bool IsLocalEcho(ReadOnlySpan<byte> packet)
    {
        if (!_hasLocalEcho)
            return false;
        lock (_echoGate)
        {
            for (int index = 0; index < _echoPackets.Count; index++)
            {
                if (!packet.SequenceEqual(_echoPackets[index]))
                    continue;
                // 每次发送消费一份回显；单字节 0x00 数据帧的请求可能与成功 ACK 完全相同。
                _echoPackets.RemoveAt(index);
                return true;
            }
            return false;
        }
    }

    private void ReportRejectedFrames()
    {
        while (_reportedRejectedFrames < _decoder.RejectedFrames)
        {
            _reportedRejectedFrames++;
            _frameRejected?.Invoke();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _shutdown.Cancel();
        try
        {
            await _port.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _sendGate.WaitAsync().ConfigureAwait(false);
            await _receiveGate.WaitAsync().ConfigureAwait(false);
            _receiveOffset = _receiveLength = 0;
            _echoPackets.Clear();
            _receiveGate.Release();
            _sendGate.Release();
            _shutdown.Dispose();
        }
    }
}
