using System;

namespace UniversalHost.Models;

/// <summary>
/// 曲线专用环形缓冲区。索引和导出仍按最新到最旧排列；绘图查询使用物理索引，
/// 保持扫描线从左到右、填满后环形覆盖的显示方式。
/// 无锁读取允许跨越相邻采样更新，不保证数据与极值索引的整段快照一致。
/// </summary>
public sealed class CurvePlotBuffer
{
    private const int BlockSize = 32;
    private readonly double[] _buffer;
    private readonly CurvePlotRange[] _ranges;
    private readonly int _leafCount;
    private int _writeIndex;
    private int _count;
    private long _version;

    internal double GetPhysicalValue(int index) => index >= 0 && index < _count
        ? _buffer[index] : double.NaN;

    public int Capacity => _buffer.Length;
    public int Count => _count;
    public int WriteIndex => _writeIndex;
    public long Version => _version;

    public CurvePlotBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _buffer = new double[capacity];
        int blockCount = (capacity - 1) / BlockSize + 1;
        _leafCount = 1;
        while (_leafCount < blockCount) _leafCount <<= 1;
        // 叶节点汇总 32 点，父节点逐级汇总，不为每个原始点重复保存 min/max。
        _ranges = new CurvePlotRange[_leafCount * 2];
        Array.Fill(_ranges, CurvePlotRange.Empty);
    }

    public (int Count, int WriteIndex, long Version) GetState() => (_count, _writeIndex, _version);

    public void Clear()
    {
        Array.Clear(_buffer);
        Array.Fill(_ranges, CurvePlotRange.Empty);
        _writeIndex = 0;
        _count = 0;
        _version++;
    }

    public void Add(double value)
    {
        int index = _writeIndex;
        double previousValue = _buffer[index];
        bool overwriting = _count == Capacity;
        _buffer[index] = value;
        if (_count < Capacity) _count++;
        _writeIndex = index + 1 == Capacity ? 0 : index + 1;
        if (overwriting && value.Equals(previousValue))
        {
            _version++;
            return;
        }

        int node = _leafCount + index / BlockSize;
        CurvePlotRange previousRange = _ranges[node];
        CurvePlotRange range;
        if (overwriting && double.IsFinite(previousValue) &&
            (previousValue == previousRange.Minimum || previousValue == previousRange.Maximum))
        {
            // 被覆盖的点可能是旧极值，只重算所在小块，正确移除已过期的尖峰。
            int first = index / BlockSize * BlockSize;
            range = ScanRange(first, Math.Min(first + BlockSize, _count));
        }
        else
        {
            range = previousRange.Include(value);
        }

        while (_ranges[node] != range)
        {
            _ranges[node] = range;
            node >>= 1;
            if (node == 0) break;
            range = _ranges[node * 2].Combine(_ranges[node * 2 + 1]);
        }
        _version++;
    }

    /// <summary>最新值为 0，保留原缓冲区的历史访问语义。</summary>
    public double this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            int physicalIndex = _writeIndex - 1 - index;
            if (physicalIndex < 0) physicalIndex += Capacity;
            return _buffer[physicalIndex];
        }
    }

    public void CopyToSpan(Span<double> destination)
    {
        var state = GetState();
        if (destination.Length < state.Count)
            throw new ArgumentException("destination too small", nameof(destination));
        CopyToSpan(destination, state.Count, state.WriteIndex);
    }

    private void CopyToSpan(Span<double> destination, int count, int writeIndex)
    {
        // 固定本次导出的长度，避免采集线程继续填充时写出目标 Span 的边界。
        int index = writeIndex;
        for (int i = 0; i < count; i++)
        {
            if (--index < 0) index = Capacity - 1;
            destination[i] = _buffer[index];
        }
    }

    public double[] ToArray()
    {
        var state = GetState();
        double[] result = new double[state.Count];
        CopyToSpan(result, state.Count, state.WriteIndex);
        return result;
    }

    /// <summary>查询物理索引闭区间的精确极值，自动裁剪到已写入区域。</summary>
    public CurvePlotRange GetRange(int firstIndex, int lastIndex)
    {
        int count = _count;
        firstIndex = Math.Max(0, firstIndex);
        lastIndex = Math.Min(count - 1, lastIndex);
        if (firstIndex > lastIndex) return CurvePlotRange.Empty;
        // 自动缩放通常查询全部数据，直接使用根节点，复杂度 O(1)。
        if (firstIndex == 0 && lastIndex == count - 1) return _ranges[1];

        int end = lastIndex + 1;
        CurvePlotRange result = CurvePlotRange.Empty;
        while (firstIndex < end && firstIndex % BlockSize != 0)
            result = result.Include(_buffer[firstIndex++]);
        while (firstIndex < end && end % BlockSize != 0)
            result = result.Include(_buffer[--end]);

        // 区间内部只读取已经维护好的多级 LOD，两端最多扫描 62 个原始点。
        int left = _leafCount + firstIndex / BlockSize;
        int right = _leafCount + end / BlockSize;
        while (left < right)
        {
            if ((left & 1) != 0) result = result.Combine(_ranges[left++]);
            if ((right & 1) != 0) result = result.Combine(_ranges[--right]);
            left >>= 1;
            right >>= 1;
        }
        return result;
    }

    private CurvePlotRange ScanRange(int first, int end)
    {
        CurvePlotRange range = CurvePlotRange.Empty;
        for (int i = first; i < end; i++) range = range.Include(_buffer[i]);
        return range;
    }
}

public readonly record struct CurvePlotRange(double Minimum, double Maximum)
{
    public static CurvePlotRange Empty => new(double.PositiveInfinity, double.NegativeInfinity);
    public bool HasValues => Minimum <= Maximum;

    internal CurvePlotRange Include(double value) => double.IsFinite(value)
        ? new(Math.Min(Minimum, value), Math.Max(Maximum, value)) : this;

    internal CurvePlotRange Combine(CurvePlotRange other) =>
        new(Math.Min(Minimum, other.Minimum), Math.Max(Maximum, other.Maximum));
}
