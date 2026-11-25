using System;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalHost.Services.Communication;

public interface ICommService : IAsyncDisposable
{
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
}
