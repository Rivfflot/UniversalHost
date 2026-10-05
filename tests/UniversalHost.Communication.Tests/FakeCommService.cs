using System.Threading.Channels;
using UniversalHost.Services.Communication;

namespace UniversalHost.Communication.Tests;

/// <summary>按任意块交付的模拟串口；保留延迟、溢出、取消和清理时序。</summary>
internal sealed class FakeCommService : ICommService
{
    private readonly Channel<object> _input = Channel.CreateUnbounded<object>();
    private readonly CancellationTokenSource _closed = new();
    private byte[]? _chunk;
    private int _offset;
    private int _writing;
    public List<byte[]> Writes { get; } = [];
    public Func<byte[], CancellationToken, Task>? OnSend { get; set; }
    public TimeSpan SendDelay { get; set; }
    public TaskCompletionSource? FinishDisposal { get; set; }
    public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsDisposed { get; private set; }
    public bool WritesOverlapped { get; private set; }

    public void Enqueue(byte[] bytes) => _input.Writer.TryWrite(bytes);
    public void FailRead(Exception error) => _input.Writer.TryWrite(error);
    public void Send(ReadOnlySpan<byte> data) => SendAsync(data.ToArray()).GetAwaiter().GetResult();

    public async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _writing) > 1)
            WritesOverlapped = true;
        try
        {
            byte[] bytes = data.ToArray();
            Writes.Add(bytes);
            if (SendDelay > TimeSpan.Zero)
                await Task.Delay(SendDelay, ct);
            if (OnSend != null)
                await OnSend(bytes, ct);
            return bytes.Length;
        }
        finally { Interlocked.Decrement(ref _writing); }
    }

    public byte[]? Receive() => throw new NotSupportedException();

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        if (_chunk == null)
        {
            object item = await _input.Reader.ReadAsync(linked.Token);
            if (item is Exception exception)
                throw exception;
            _chunk = (byte[])item;
            _offset = 0;
        }
        int count = Math.Min(buffer.Length, _chunk.Length - _offset);
        _chunk.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        if (_offset == _chunk.Length)
            _chunk = null;
        return count;
    }

    public async ValueTask DisposeAsync()
    {
        _closed.Cancel();
        DisposalStarted.TrySetResult();
        if (FinishDisposal != null)
            await FinishDisposal.Task;
        IsDisposed = true;
    }
}
