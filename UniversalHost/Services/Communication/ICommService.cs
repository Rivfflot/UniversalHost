using System;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalHost.Services.Communication;

public interface ICommService : IAsyncDisposable
{
    void Send(ReadOnlySpan<byte> data);
    Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    byte[]? Receive();
    Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
}
