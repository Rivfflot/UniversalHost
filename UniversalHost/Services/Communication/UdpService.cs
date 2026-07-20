using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalHost.Services.Communication;

public class UdpService : ICommService
{
    private readonly UdpClient _udpClient;
    private readonly IPEndPoint _remoteEndpoint;
    private readonly int _timeoutMilliseconds;
    private CancellationTokenSource? _timeoutCts;
    private CancellationTokenRegistration? _linkedRegistration;

    public UdpService() : this("127.0.0.1", 0, "127.0.0.1", 0, 0) { }

    public UdpService(string localAddr, int localPort, string remoteAddr, int remotePort, int timeoutMilliseconds)
    {
        _timeoutMilliseconds = timeoutMilliseconds;

        var localEndPoint = new IPEndPoint(IPAddress.Parse(localAddr), localPort);
        _udpClient = new UdpClient(localEndPoint);
        const int SIO_UDP_CONNRESET = -1744830452;
        _udpClient.Client.IOControl(
            (IOControlCode)SIO_UDP_CONNRESET,
            new byte[] { 0, 0, 0, 0 },
            null);
        _remoteEndpoint = new IPEndPoint(IPAddress.Parse(remoteAddr), remotePort);
        _udpClient.Client.ReceiveTimeout = timeoutMilliseconds;
        _udpClient.Connect(_remoteEndpoint);
    }
    public void Send(ReadOnlySpan<byte> data)
    {
        _udpClient.Client.Send(data, SocketFlags.None);
    }
    public async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        return await _udpClient.SendAsync(data, ct);
    }
    public byte[]? Receive()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            return _udpClient.Receive(ref ep);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            return null;
        }
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        InitializeOrResetCts(ct);
        try
        {
            var result = await _udpClient.Client.ReceiveAsync(buffer, SocketFlags.None, _timeoutCts!.Token);

            return result;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                return -2;//取消操作
            }
            else
            {
                //throw new TimeoutException("UDP接收超时");
                return -1;//超时
            }
        }
    }
    private void InitializeOrResetCts(CancellationToken ct)
    {
        if (_timeoutCts == null || !_timeoutCts.TryReset())
        {
            _timeoutCts?.Dispose(); // 释放旧的
            _timeoutCts = new CancellationTokenSource();
        }

        _linkedRegistration?.Dispose();

        if (ct.CanBeCanceled)
        {
            _linkedRegistration = ct.Register(() => _timeoutCts.Cancel());
        }

        // 重新开启倒计时
        _timeoutCts.CancelAfter(_timeoutMilliseconds);
    }


    public ValueTask DisposeAsync()
    {
        _linkedRegistration?.Dispose();
        _timeoutCts?.Dispose();
        _udpClient.Close();
        _udpClient.Dispose();
        return ValueTask.CompletedTask;
    }


}
