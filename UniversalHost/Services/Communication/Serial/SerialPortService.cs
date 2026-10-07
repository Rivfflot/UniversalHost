using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models;

namespace UniversalHost.Services.Communication.Serial;

/// <summary>单个串口的原始字节收发；帧封装由 SerialTransportService 负责。</summary>
public sealed class SerialPortService : ICommService
{
    public const int ReceiveBufferCapacity = 256 * 1024;
    private readonly SerialPort _serialPort;
    private readonly SerialConnectionOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly byte[] _readBuffer = new byte[4096];
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _receiveError;

    public SerialPortService(SerialConnectionOptions options)
    {
        _options = options;
        _serialPort = new SerialPort(options.PortName, options.BaudRate, options.Parity, 8, options.StopBits)
        {
            Handshake = Handshake.None,
            ReadTimeout = 20,
            ReadBufferSize = ReceiveBufferCapacity,
            WriteBufferSize = 64 * 1024,
            ParityReplace = 0,
            RtsEnable = options.DirectionControl == SerialDirectionControl.RtsLowWhileSending
        };
        try
        {
            _serialPort.Open();
            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();
            _serialPort.ErrorReceived += OnErrorReceived;
        }
        catch
        {
            _serialPort.Dispose();
            throw;
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs args) =>
        Interlocked.Exchange(ref _receiveError, 1);

    public void Send(ReadOnlySpan<byte> data) => SendAsync(data.ToArray()).GetAwaiter().GetResult();

    public async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            byte[] bytes = data.ToArray();
            TimeSpan wireTime = _options.GetTransmissionTime(bytes.Length);
            TimeSpan writeBudget = wireTime + TimeSpan.FromMilliseconds(
                Math.Max(1000, _options.TimeoutMilliseconds * 2) + _options.TurnaroundDelayMilliseconds * 2);
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            writeCts.CancelAfter(writeBudget);
            _serialPort.WriteTimeout = Math.Max(1, (int)Math.Ceiling(writeBudget.TotalMilliseconds));
            using var abort = writeCts.Token.Register(AbortWrite);
            bool manualDirection = _options.DuplexMode == SerialDuplexMode.HalfDuplex &&
                _options.DirectionControl != SerialDirectionControl.Automatic;
            try
            {
                if (manualDirection)
                {
                    _serialPort.RtsEnable = _options.DirectionControl == SerialDirectionControl.RtsHighWhileSending;
                    await Task.Delay(_options.TurnaroundDelayMilliseconds, writeCts.Token).ConfigureAwait(false);
                }
                long start = Stopwatch.GetTimestamp();
                // Windows SerialStream 异步写入不保证取消，使用有限超时的写入并等待驱动排空。
                await Task.Run(() =>
                {
                    writeCts.Token.ThrowIfCancellationRequested();
                    _serialPort.Write(bytes, 0, bytes.Length);
                    _serialPort.BaseStream.Flush();
                }).ConfigureAwait(false);
                writeCts.Token.ThrowIfCancellationRequested();
                // 驱动写入完成不等于线上完成，覆盖字符时间及最后一个字符的移位时间。
                TimeSpan remaining = wireTime - Stopwatch.GetElapsedTime(start);
                TimeSpan guard = _options.GetTransmissionTime(1);
                if (remaining > guard)
                    guard = remaining;
                await Task.Delay(guard, writeCts.Token).ConfigureAwait(false);
                if (manualDirection)
                    _serialPort.RtsEnable = _options.DirectionControl == SerialDirectionControl.RtsLowWhileSending;
                if (_options.DuplexMode == SerialDuplexMode.HalfDuplex)
                    await Task.Delay(_options.TurnaroundDelayMilliseconds, writeCts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (writeCts.IsCancellationRequested)
            {
                linked.Token.ThrowIfCancellationRequested();
                throw new TimeoutException($"串口发送未能在 {writeBudget.TotalMilliseconds:F0} ms 内完成");
            }
            finally
            {
                if (manualDirection && _serialPort.IsOpen)
                    _serialPort.RtsEnable = _options.DirectionControl == SerialDirectionControl.RtsLowWhileSending;
            }
            return data.Length;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void AbortWrite()
    {
        try
        {
            if (_serialPort.IsOpen)
                _serialPort.DiscardOutBuffer();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
    }

    public byte[]? Receive()
    {
        byte[] buffer = new byte[4096];
        int length = ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
        return length > 0 ? buffer.AsSpan(0, length).ToArray() : null;
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _receiveGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (Interlocked.Exchange(ref _receiveError, 0) != 0)
                    throw new SerialReceiveException("串口接收溢出、校验或帧错误，需要重新同步");
                if (!_serialPort.IsOpen)
                    throw new IOException("串口已关闭");
                int count = Math.Min(_serialPort.BytesToRead, Math.Min(buffer.Length, _readBuffer.Length));
                if (count > 0)
                {
                    try
                    {
                        int read = _serialPort.Read(_readBuffer, 0, count);
                        _readBuffer.AsMemory(0, read).CopyTo(buffer);
                        return read;
                    }
                    catch (TimeoutException) { }
                }
                // 无数据时可取消地轮询，避免 Windows 上挂起的 ReadAsync 阻碍会话清理。
                if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromMilliseconds(50))
                    return 0;
                await Task.Delay(2, linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _receiveGate.Release();
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
        await _sendGate.WaitAsync().ConfigureAwait(false);
        await _receiveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                _serialPort.ErrorReceived -= OnErrorReceived;
                AbortWrite();
                try { if (_serialPort.IsOpen) _serialPort.DiscardInBuffer(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
                _serialPort.Dispose();
            }).ConfigureAwait(false);
        }
        finally
        {
            _receiveGate.Release();
            _sendGate.Release();
            _shutdown.Dispose();
        }
    }
}

internal sealed class SerialReceiveException(string message) : IOException(message);
