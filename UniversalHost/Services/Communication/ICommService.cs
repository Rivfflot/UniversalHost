using System;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalHost.Services.Communication;

public interface ICommService : IAsyncDisposable
{
    /// <summary>异步发送数据。发送完成前，调用方须保留缓冲区并保持内容有效且不变。</summary>
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
}
