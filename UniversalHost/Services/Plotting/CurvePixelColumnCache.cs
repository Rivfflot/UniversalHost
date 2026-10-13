using ScottPlot;
using System;
using UniversalHost.Models.Plotting;

namespace UniversalHost.Services.Plotting;

internal readonly record struct CurvePixelColumn(CurvePlotRange Range, double Enter, double Exit)
{
    public static CurvePixelColumn Empty => new(CurvePlotRange.Empty, double.NaN, double.NaN);
}

/// <summary>
/// 缓存当前 X 视野内每个像素列的原始极值。环形写入只查询受影响的列，
/// Y 轴缩放复用极值；X 范围、分辨率或历史重置变化时重新查询。
/// 每条绘图对象独立保存视野，多个窗口的缩放互不影响。
/// </summary>
internal sealed class CurvePixelColumnCache(CurvePlotBuffer history)
{
    private CurvePixelColumn[] _columns = [];
    private double _left, _right, _period, _offset;
    private float _width;
    private int _first, _last, _count;
    private long _version = -1;

    public int ColumnCount { get; private set; }
    public long Revision { get; private set; }
    public ReadOnlySpan<CurvePixelColumn> Columns => _columns.AsSpan(0, ColumnCount);

    public void Update(IAxes axes, CurvePlotSignalSource source)
    {
        // 记录查询前的版本。并发采样若在查询中继续写入，下次仍会检查这些写入。
        bool stable = history.TryGetStableState(out var state);
        double left = axes.GetCoordinateX(axes.DataRect.Left);
        double right = axes.GetCoordinateX(axes.DataRect.Right);
        int first = source.MinRenderIndex;
        int last = source.MaxRenderIndex;
        int count = (int)axes.DataRect.Width;
        bool appended = last >= _last && _last == _count - 1 && last == state.Count - 1;
        long writes = state.Version - _version;
        bool rebuild = !stable || _version < 0 || _left != left || _right != right || _width != axes.DataRect.Width ||
            _period != source.Period || _offset != source.XOffset || _first != first ||
            (_last != last && !appended) || history.LastClearVersion > _version ||
            writes < 0 || writes >= history.Capacity;

        ColumnCount = count;
        if (_columns.Length < count) _columns = new CurvePixelColumn[count];
        bool changed = rebuild;
        if (rebuild)
        {
            for (int x = 0; x < count; x++) _columns[x] = source.GetColumn(axes, x);
        }
        else if (writes > 0)
        {
            int start = state.WriteIndex - (int)writes;
            if (start < 0) start += history.Capacity;
            int end = start + (int)writes - 1;
            if (end < history.Capacity)
                changed = UpdateRange(start, end, axes, source);
            else
                changed = UpdateRange(start, history.Capacity - 1, axes, source) |
                    UpdateRange(0, end - history.Capacity, axes, source);
        }

        _left = left;
        _right = right;
        _width = axes.DataRect.Width;
        _period = source.Period;
        _offset = source.XOffset;
        _first = first;
        _last = last;
        _count = state.Count;
        _version = stable ? state.Version : -1;
        if (changed) Revision++;
    }

    private bool UpdateRange(int first, int last, IAxes axes, CurvePlotSignalSource source)
    {
        if (ColumnCount == 0 || last < source.MinRenderIndex || first > source.MaxRenderIndex) return false;
        first = Math.Max(first, source.MinRenderIndex);
        last = Math.Min(last, source.MaxRenderIndex);
        // 查询列包含两端的采样点，额外覆盖相邻列和不足一像素的浮点边界。
        // 从点 i 到 i+1 的连线也受点 i 的变化影响。
        double p1 = axes.GetPixelX(source.GetX(first - 1)) - axes.DataRect.Left;
        double p2 = axes.GetPixelX(source.GetX(last + 1)) - axes.DataRect.Left;
        double left = Math.Floor(Math.Min(p1, p2)) - 2;
        double right = Math.Ceiling(Math.Max(p1, p2)) + 2;
        if (right < 0 || left >= ColumnCount) return false;
        int start = (int)Math.Clamp(left, 0, ColumnCount - 1);
        int end = (int)Math.Clamp(right, 0, ColumnCount - 1);
        bool changed = false;
        for (int x = start; x <= end; x++)
        {
            CurvePixelColumn column = source.GetColumn(axes, x);
            changed |= _columns[x] != column;
            _columns[x] = column;
        }
        return changed;
    }
}
