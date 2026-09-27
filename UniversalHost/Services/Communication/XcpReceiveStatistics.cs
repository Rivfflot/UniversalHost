using System.Threading;

namespace UniversalHost.Services.Communication;

/// <summary>
/// 统计单个 XcpClient 连接的累计数据。CTR 缺口只是传输层观测结果，不能直接证明网络丢包；
/// 延迟到达的数据帧和设备重启也会影响统计值。16 位序号状态仅由 ReceiverLoop 更新。
/// </summary>
public sealed class XcpReceiveStatistics
{
    private bool _hasCounter;
    private ushort _nextCounter;
    private long _datagrams, _transportFrames, _counterGapFrames, _duplicateOrReorderedFrames;
    private long _daqReceived, _daqDecoded, _queueDrops, _unknownPidFrames, _parseErrors, _malformedDatagrams;

    internal void RecordCounter(ushort counter)
    {
        Interlocked.Increment(ref _transportFrames);
        if (_hasCounter)
        {
            ushort delta = unchecked((ushort)(counter - _nextCounter));
            if (delta >= 0x8000)
            {
                // 收到迟到帧或重复帧时，不回退当前序号位置。
                Interlocked.Increment(ref _duplicateOrReorderedFrames);
                return;
            }
            Interlocked.Add(ref _counterGapFrames, delta);
        }
        _hasCounter = true;
        _nextCounter = unchecked((ushort)(counter + 1));
    }

    internal void RecordDatagram() => Interlocked.Increment(ref _datagrams);
    internal void RecordMalformedDatagram() => Interlocked.Increment(ref _malformedDatagrams);
    internal void RecordDaqReceived() => Interlocked.Increment(ref _daqReceived);
    internal void RecordDaqDecoded() => Interlocked.Increment(ref _daqDecoded);
    internal void RecordQueueDrop() => Interlocked.Increment(ref _queueDrops);
    internal void RecordUnknownPid() => Interlocked.Increment(ref _unknownPidFrames);
    internal void RecordParseError() => Interlocked.Increment(ref _parseErrors);

    public XcpReceiveSnapshot Snapshot() => new(
        Interlocked.Read(ref _datagrams), Interlocked.Read(ref _transportFrames),
        Interlocked.Read(ref _counterGapFrames), Interlocked.Read(ref _duplicateOrReorderedFrames),
        Interlocked.Read(ref _daqReceived), Interlocked.Read(ref _daqDecoded),
        Interlocked.Read(ref _queueDrops), Interlocked.Read(ref _unknownPidFrames),
        Interlocked.Read(ref _parseErrors), Interlocked.Read(ref _malformedDatagrams));
}

public readonly record struct XcpReceiveSnapshot(
    long Datagrams, long TransportFrames, long CounterGapFrames, long DuplicateOrReorderedFrames,
    long DaqReceived, long DaqDecoded, long QueueDrops, long UnknownPidFrames,
    long ParseErrors, long MalformedDatagrams);
