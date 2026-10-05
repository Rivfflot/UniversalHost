using System;
using System.Diagnostics;
using UniversalHost.Models;

namespace UniversalHost.Services.Communication.Serial;

/// <summary>有界字节流分帧器；普通接收空闲不会结束候选。</summary>
public sealed class SerialFrameDecoder
{
    private readonly SerialProtocol _protocol;
    private readonly byte[] _encoded;
    private readonly byte[] _raw;
    private readonly TimeSpan _incompleteFrameTimeout;
    private int _length;
    private long _candidateStart;
    private bool _discardUntilDelimiter;
    public long RejectedFrames { get; private set; }

    public SerialFrameDecoder(SerialProtocol protocol, TimeSpan incompleteFrameTimeout)
    {
        if (incompleteFrameTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(incompleteFrameTimeout));
        _protocol = protocol;
        _raw = new byte[SerialTransportProtocol.GetMaxRawLength(protocol)];
        _encoded = new byte[Cobs.GetMaxEncodedLength(_raw.Length)];
        _incompleteFrameTimeout = incompleteFrameTimeout;
    }

    public void ExpireCandidate(long now)
    {
        if (_length > 0 && Stopwatch.GetElapsedTime(_candidateStart, now) >= _incompleteFrameTimeout)
            DiscardUntilDelimiter();
    }

    public void DiscardUntilDelimiter()
    {
        _length = 0;
        _discardUntilDelimiter = true;
        RejectedFrames++;
    }

    // 返回内存仅在下一次调用前有效；消费方立即复制到自己的接收缓存。
    public bool TryReadByte(byte value, long now, out ReadOnlyMemory<byte> packet)
    {
        packet = default;
        ExpireCandidate(now);
        if (value == 0)
        {
            int length = _length;
            _length = 0;
            if (_discardUntilDelimiter)
            {
                _discardUntilDelimiter = false;
                return false;
            }
            if (length == 0)
                return false;
            if (!Cobs.TryDecode(_encoded.AsSpan(0, length), _raw, out int rawLength) ||
                !SerialTransportProtocol.TryValidate(_raw.AsSpan(0, rawLength), _protocol, out int offset, out int packetLength))
            {
                RejectedFrames++;
                return false;
            }
            packet = _raw.AsMemory(offset, packetLength);
            return true;
        }
        if (_discardUntilDelimiter)
            return false;
        if (_length == _encoded.Length)
        {
            DiscardUntilDelimiter();
            return false;
        }
        if (_length == 0)
            _candidateStart = now;
        _encoded[_length++] = value;
        return false;
    }
}
