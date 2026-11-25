using System;
using System.Threading;

namespace UniversalHost.Models;

/// <summary>
/// 采样历史环形缓冲区。曲线索引从最新到最旧排列，范围查询使用物理索引，
/// 保持扫描线从左到右、填满后环形覆盖的显示方式。
/// 无锁读取允许跨越相邻采样更新，不保证数据与极值索引的整段快照一致。
/// </summary>
public abstract class SampleHistoryBuffer
{
    private const int BlockSize = 32;
    protected abstract double ReadPhysical(int index);
    private readonly SampleHistoryRange[] _ranges;
    private readonly int _leafCount;
    private int _writeIndex;
    private int _count;
    private long _version;
    private long _lastClearVersion;
    private int _mutationVersion;

    internal double GetPhysicalValue(int index) => index >= 0 && index < _count
        ? ReadPhysical(index) : double.NaN;

    public int Capacity { get; }
    public int Count => _count;
    public int WriteIndex => _writeIndex;
    public long Version => _version;
    internal long LastClearVersion => _lastClearVersion;

    protected SampleHistoryBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        int blockCount = (capacity - 1) / BlockSize + 1;
        _leafCount = 1;
        while (_leafCount < blockCount) _leafCount <<= 1;
        // 叶节点汇总 32 点，父节点逐级汇总，不为每个原始点重复保存 min/max。
        _ranges = new SampleHistoryRange[_leafCount * 2];
        Array.Fill(_ranges, SampleHistoryRange.Empty);
    }

    public (int Count, int WriteIndex, long Version) GetState() => (_count, _writeIndex, _version);

    internal bool TryGetStableState(out (int Count, int WriteIndex, long Version) state)
    {
        int before = Volatile.Read(ref _mutationVersion);
        state = GetState();
        // 增量缓存只能接受同一次完整写入后的版本和游标，避免错算脏区间后长期遗漏尖峰。
        return (before & 1) == 0 && before == Volatile.Read(ref _mutationVersion);
    }

    public void Clear()
    {
        Interlocked.Increment(ref _mutationVersion);
        Array.Fill(_ranges, SampleHistoryRange.Empty);
        _writeIndex = 0;
        _count = 0;
        _version++;
        _lastClearVersion = _version;
        Interlocked.Increment(ref _mutationVersion);
    }

    protected void BeginWrite() => Interlocked.Increment(ref _mutationVersion);

    protected void CompleteWrite(int index, double previousValue, double value)
    {
        bool overwriting = _count == Capacity;
        if (_count < Capacity) _count++;
        _writeIndex = index + 1 == Capacity ? 0 : index + 1;
        if (overwriting && value.Equals(previousValue))
        {
            _version++;
            Interlocked.Increment(ref _mutationVersion);
            return;
        }

        int node = _leafCount + index / BlockSize;
        SampleHistoryRange previousRange = _ranges[node];
        SampleHistoryRange range;
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
        Interlocked.Increment(ref _mutationVersion);
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
            return ReadPhysical(physicalIndex);
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
            destination[i] = ReadPhysical(index);
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
    public SampleHistoryRange GetRange(int firstIndex, int lastIndex)
    {
        int count = _count;
        firstIndex = Math.Max(0, firstIndex);
        lastIndex = Math.Min(count - 1, lastIndex);
        if (firstIndex > lastIndex) return SampleHistoryRange.Empty;
        // 自动缩放通常查询全部数据，直接使用根节点，复杂度 O(1)。
        if (firstIndex == 0 && lastIndex == count - 1) return _ranges[1];

        int end = lastIndex + 1;
        SampleHistoryRange result = SampleHistoryRange.Empty;
        while (firstIndex < end && firstIndex % BlockSize != 0)
            result = result.Include(ReadPhysical(firstIndex++));
        while (firstIndex < end && end % BlockSize != 0)
            result = result.Include(ReadPhysical(--end));

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

    private SampleHistoryRange ScanRange(int first, int end)
    {
        SampleHistoryRange range = SampleHistoryRange.Empty;
        for (int i = first; i < end; i++) range = range.Include(ReadPhysical(i));
        return range;
    }
}

/// <summary>保存原始采样类型，并提供供绘图使用的 double 视图。</summary>
internal sealed class SampleHistoryBuffer<T>(int capacity) : SampleHistoryBuffer(capacity) where T : struct
{
    private readonly T[] _samples = new T[capacity];
    protected override double ReadPhysical(int index) => SymbolRuntime<T>.ConvertToDouble(_samples[index]);

    public void Add(T sample)
    {
        BeginWrite();
        int index = WriteIndex;
        double previous = ReadPhysical(index);
        _samples[index] = sample;
        CompleteWrite(index, previous, SymbolRuntime<T>.ConvertToDouble(sample));
    }

    public T GetChronologicalValue(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        int first = Count == Capacity ? WriteIndex : 0;
        int untilWrap = Capacity - first;
        return _samples[index < untilWrap ? first + index : index - untilWrap];
    }
}

public readonly record struct SampleHistoryRange(double Minimum, double Maximum)
{
    public static SampleHistoryRange Empty => new(double.PositiveInfinity, double.NegativeInfinity);
    public bool HasValues => Minimum <= Maximum;

    internal SampleHistoryRange Include(double value) => double.IsFinite(value)
        ? new(Math.Min(Minimum, value), Math.Max(Maximum, value)) : this;

    internal SampleHistoryRange Combine(SampleHistoryRange other) =>
        new(Math.Min(Minimum, other.Minimum), Math.Max(Maximum, other.Maximum));
}
