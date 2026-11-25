using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalHost.Services.Communication;

public class UdpService : ICommService
{
    private readonly UdpClient _udpClient;

    public UdpService() : this("127.0.0.1", 0, "127.0.0.1", 0) { }

    public UdpService(string localAddr, int localPort, string remoteAddr, int remotePort, int receiveBufferSize = 0)
    {
        var localEndPoint = new IPEndPoint(IPAddress.Parse(localAddr), localPort);
        _udpClient = new UdpClient(localEndPoint);
        if (receiveBufferSize > 0)
            _udpClient.Client.ReceiveBufferSize = receiveBufferSize;
        const int SIO_UDP_CONNRESET = -1744830452;
        _udpClient.Client.IOControl(
            (IOControlCode)SIO_UDP_CONNRESET,
            new byte[] { 0, 0, 0, 0 },
            null);
        _udpClient.Connect(new IPEndPoint(IPAddress.Parse(remoteAddr), remotePort));
    }

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        _udpClient.Client.SendAsync(data, SocketFlags.None, ct);

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) =>
        _udpClient.Client.ReceiveAsync(buffer, SocketFlags.None, ct);

    public ValueTask DisposeAsync()
    {
        _udpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
