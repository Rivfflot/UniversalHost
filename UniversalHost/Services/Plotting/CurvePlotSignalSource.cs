using ScottPlot;
using ScottPlot.DataSources;
using System;
using System.Collections;
using System.Collections.Generic;
using UniversalHost.Models;

namespace UniversalHost.Services.Plotting;

/// <summary>把环形缓冲区的极值索引接入 Signal 的逐像素绘制和自动缩放。</summary>
internal sealed class CurvePlotSignalSource(CurvePlotBuffer history) :
    SignalSourceBase, ISignalSource, IDataSource, IReadOnlyList<double>
{
    public override int Length => history.Count;
    public int Count => Length;
    public double this[int index] => GetY(index);
    bool IDataSource.PreferCoordinates => false;

    public IReadOnlyList<double> GetYs() => this;

    public IEnumerable<double> GetYs(int firstIndex, int lastIndex)
    {
        firstIndex = Math.Max(firstIndex, MinRenderIndex);
        lastIndex = Math.Min(lastIndex, MaxRenderIndex);
        for (int i = firstIndex; i <= lastIndex; i++) yield return GetY(i);
    }

    public double GetY(int index)
    {
        lock (history.SyncRoot)
        {
            return index >= 0 && index < history.CountUnsafe
                ? history.GetPhysicalValueUnsafe(index) : double.NaN;
        }
    }

    public override SignalRangeY GetLimitsY(int firstIndex, int lastIndex)
    {
        CurvePlotRange range = history.GetRange(firstIndex, lastIndex);
        return range.HasValues ? new(range.Minimum, range.Maximum) : new(double.NaN, double.NaN);
    }

    public new AxisLimits GetLimits()
    {
        lock (history.SyncRoot)
        {
            int first = MinRenderIndex;
            int last = MaxRenderIndex;
            if (first > last) return AxisLimits.NoLimits;
            CurvePlotRange range = history.GetRangeUnsafe(first, last);
            if (!range.HasValues) return AxisLimits.HorizontalOnly(GetX(first), GetX(last));
            double y1 = range.Minimum * YScale + YOffset;
            double y2 = range.Maximum * YScale + YOffset;
            return new(GetX(first), GetX(last), Math.Min(y1, y2), Math.Max(y1, y2));
        }
    }

    public new CoordinateRange GetLimitsX() => GetLimits().XRange;
    public new CoordinateRange GetLimitsY() => GetLimits().YRange;

    public PixelColumn GetPixelColumn(IAxes axes, int xPixelIndex)
    {
        float x = axes.DataRect.Left + xPixelIndex;
        double x1 = axes.GetCoordinateX(x);
        double x2 = axes.GetCoordinateX(x + 1);
        double left = Math.Min(x1, x2);
        double right = Math.Max(x1, x2);
        lock (history.SyncRoot)
        {
            int first = MinRenderIndex;
            int last = MaxRenderIndex;
            if (first > last || !RangeContainsSignal(left, right))
                return PixelColumn.WithoutData(x);
            int i1 = GetIndex(left, true);
            int i2 = GetIndex(right, true);
            CurvePlotRange range = history.GetRangeUnsafe(i1, i2);
            if (!range.HasValues) return PixelColumn.WithoutData(x);

            double enter = history.GetPhysicalValueUnsafe(i1);
            double exit = history.GetPhysicalValueUnsafe(i2);
            // 非有限设备值不参与缩放，也不让它遮掉同一像素内的有效尖峰。
            if (!double.IsFinite(enter)) enter = range.Minimum;
            if (!double.IsFinite(exit)) exit = range.Maximum;
            return new PixelColumn(x,
                axes.GetPixelY(enter * YScale + YOffset),
                axes.GetPixelY(exit * YScale + YOffset),
                axes.GetPixelY(range.Minimum * YScale + YOffset),
                axes.GetPixelY(range.Maximum * YScale + YOffset));
        }
    }

    int IDataSource.GetXClosestIndex(Coordinates location) => GetIndex(location.X, true);
    Coordinates IDataSource.GetCoordinate(int index) => new(index * Period, GetY(index));
    Coordinates IDataSource.GetCoordinateScaled(int index) => new(GetX(index), GetY(index) * YScale + YOffset);
    double IDataSource.GetX(int index) => index * Period;
    double IDataSource.GetXScaled(int index) => GetX(index);
    double IDataSource.GetYScaled(int index) => GetY(index) * YScale + YOffset;
    bool IDataSource.IsSorted() => true;

    public IEnumerator<double> GetEnumerator()
    {
        int length = Length;
        for (int i = 0; i < length; i++) yield return GetY(i);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>读取屏幕数据时锁住历史，耗时的 Skia 绘制在释放锁后进行。</summary>
internal sealed class CurvePlotSignal : ScottPlot.Plottables.Signal
{
    private readonly CurvePlotBuffer _history;
    private PixelColumn[] _columns = [];

    public CurvePlotSignal(CurvePlotBuffer history) : base(new CurvePlotSignalSource(history) { Period = 1 })
    {
        _history = history;
    }

    public override void Render(RenderPack rp)
    {
        using SkiaSharp.SKPath path = new();
        List<Pixel>? markers = null;
        double pointsPerPixel;
        int columnCount = 0;
        lock (_history.SyncRoot)
        {
            int first = Math.Max(0, MinRenderIndex);
            int last = Math.Min(_history.CountUnsafe - 1, MaxRenderIndex);
            if (first > last || Axes.DataRect.Width <= 0) return;

            double left = Axes.GetCoordinateX(Axes.DataRect.Left);
            double right = Axes.GetCoordinateX(Axes.DataRect.Right);
            double xMin = Math.Min(left, right);
            double xMax = Math.Max(left, right);
            pointsPerPixel = (xMax - xMin) / Axes.DataRect.Width / Data.Period;
            bool connected = false;
            if (pointsPerPixel < 1 || AlwaysUseLowDensityMode)
            {
                int i1 = Data.GetIndex(xMin, true);
                int i2 = Data.GetIndex(xMax + Data.Period, true);
                markers = [];
                for (int i = i1; i <= i2; i++)
                {
                    double y = Data.GetY(i) * Data.YScale + Data.YOffset;
                    if (!double.IsFinite(y))
                    {
                        connected = false;
                        continue;
                    }
                    Pixel pixel = new(Axes.GetPixelX(Data.GetX(i)), Axes.GetPixelY(y));
                    if (connected) path.LineTo(pixel.X, pixel.Y);
                    else path.MoveTo(pixel.X, pixel.Y);
                    connected = true;
                    markers.Add(pixel);
                }
            }
            else
            {
                columnCount = (int)Axes.DataRect.Width;
                if (_columns.Length < columnCount) _columns = new PixelColumn[columnCount];
                for (int x = 0; x < columnCount; x++)
                    _columns[x] = Data.GetPixelColumn(Axes, x);
            }
        }

        // 普通细实线直接绘制像素列矩形，避免中间密度下复杂路径的填充/描边低谷。
        if (markers == null)
        {
            bool useEnvelopeRendering = LineWidth == 1 && !LineStyle.Hairline &&
                !LineStyle.HandDrawn && LinePattern.Equals(ScottPlot.LinePattern.Solid) &&
                LineStyle.StrokeCap == SkiaSharp.SKStrokeCap.Butt;
            if (useEnvelopeRendering)
            {
                if (HasSmoothWideEnvelope(columnCount)) RenderSmoothEnvelope(rp, path, columnCount);
                else RenderEnvelopeRectangles(rp, columnCount);
                return;
            }
            BuildColumnPath(path, columnCount);
        }
        // 特殊线型和放大后的原始点保留折线绘制，均不占用采集线程的锁。
        Drawing.DrawPath(rp.Canvas, rp.Paint, path, LineStyle);
        if (markers != null && pointsPerPixel < 1)
        {
            float radius = (float)Math.Min(Math.Sqrt(.2 / pointsPerPixel), MaximumMarkerSize);
            MarkerSize = radius * MaximumMarkerSize * .2f;
            Drawing.DrawMarkers(rp.Canvas, rp.Paint, markers, MarkerStyle);
        }
    }

    private bool HasSmoothWideEnvelope(int count)
    {
        double height = 0, boundaryTravel = 0;
        int validColumns = 0, adjacentColumns = 0;
        bool connected = false;
        float previousTop = 0, previousBottom = 0;
        for (int i = 0; i < count; i++)
        {
            if (!TryGetVisibleEdges(_columns[i], out float top, out float bottom))
            {
                connected = false;
                continue;
            }
            height += bottom - top;
            validColumns++;
            if (connected)
            {
                boundaryTravel += Math.Abs(top - previousTop) + Math.Abs(bottom - previousBottom);
                adjacentColumns++;
            }
            previousTop = top;
            previousBottom = bottom;
            connected = true;
        }
        // 仅宽且边界平滑的包络使用整段填充；边界跳变会放大 Skia 的路径处理成本。
        return validColumns > 0 && height >= validColumns * Axes.DataRect.Height * .25 &&
            boundaryTravel <= adjacentColumns * Axes.DataRect.Height * .05;
    }

    private bool TryGetVisibleEdges(PixelColumn column, out float top, out float bottom)
    {
        var edges = column.HasData ? GetEnvelopeEdges(column) : (Top: 0f, Bottom: 0f);
        top = Math.Max(edges.Top, Axes.DataRect.Top);
        bottom = Math.Min(edges.Bottom, Axes.DataRect.Bottom);
        return column.HasData && top < bottom;
    }

    private void RenderSmoothEnvelope(RenderPack rp, SkiaSharp.SKPath path, int count)
    {
        if (!LineStyle.CanBeRendered) return;
        int first = 0;
        while (first < count)
        {
            while (first < count && !TryGetVisibleEdges(_columns[first], out _, out _)) first++;
            if (first == count) break;
            int end = first + 1;
            while (end < count && TryGetVisibleEdges(_columns[end], out _, out _)) end++;
            TryGetVisibleEdges(_columns[first], out float firstTop, out _);
            path.MoveTo(_columns[first].X - .5f, firstTop);
            for (int i = first; i < end; i++)
            {
                TryGetVisibleEdges(_columns[i], out float top, out _);
                path.LineTo(_columns[i].X - .5f, top);
                path.LineTo(_columns[i].X + .5f, top);
            }
            for (int i = end - 1; i >= first; i--)
            {
                TryGetVisibleEdges(_columns[i], out _, out float bottom);
                path.LineTo(_columns[i].X + .5f, bottom);
                path.LineTo(_columns[i].X - .5f, bottom);
            }
            path.Close();
            first = end;
        }
        LineStyle.ApplyToPaint(rp.Paint);
        rp.Paint.IsStroke = false;
        rp.Canvas.DrawPath(path, rp.Paint.SKPaint);
    }

    private void RenderEnvelopeRectangles(RenderPack rp, int count)
    {
        if (!LineStyle.CanBeRendered) return;
        LineStyle.ApplyToPaint(rp.Paint);
        rp.Paint.IsStroke = false;

        const float mergeTolerance = .25f;
        bool active = false;
        float left = 0, right = 0;
        float topMin = 0, topMax = 0, bottomMin = 0, bottomMax = 0;
        for (int i = 0; i < count; i++)
        {
            PixelColumn column = _columns[i];
            if (!TryGetVisibleEdges(column, out float top, out float bottom))
            {
                if (active) rp.Canvas.DrawRect(new SkiaSharp.SKRect(left, topMin, right, bottomMax), rp.Paint.SKPaint);
                active = false;
                continue;
            }

            // X 边界对齐整数像素，避免相邻抗锯齿矩形出现半透明接缝。
            float columnLeft = MathF.Round(column.X - .5f);
            float columnRight = columnLeft + 1;
            if (active && columnLeft == right &&
                Math.Max(topMax, top) - Math.Min(topMin, top) <= mergeTolerance &&
                Math.Max(bottomMax, bottom) - Math.Min(bottomMin, bottom) <= mergeTolerance)
            {
                right = columnRight;
                topMin = Math.Min(topMin, top);
                topMax = Math.Max(topMax, top);
                bottomMin = Math.Min(bottomMin, bottom);
                bottomMax = Math.Max(bottomMax, bottom);
                continue;
            }

            if (active) rp.Canvas.DrawRect(new SkiaSharp.SKRect(left, topMin, right, bottomMax), rp.Paint.SKPaint);
            active = true;
            left = columnLeft;
            right = columnRight;
            topMin = topMax = top;
            bottomMin = bottomMax = bottom;
        }
        if (active) rp.Canvas.DrawRect(new SkiaSharp.SKRect(left, topMin, right, bottomMax), rp.Paint.SKPaint);
    }

    private void BuildColumnPath(SkiaSharp.SKPath path, int count)
    {
        bool connected = false;
        for (int i = 0; i < count; i++)
        {
            PixelColumn column = _columns[i];
            if (!column.HasData)
            {
                connected = false;
                continue;
            }
            if (connected) path.LineTo(column.X, column.Enter);
            else path.MoveTo(column.X, column.Enter);
            path.MoveTo(column.X, column.Bottom);
            path.LineTo(column.X, column.Top);
            path.MoveTo(column.X, column.Exit);
            connected = true;
        }
    }

    private static (float Top, float Bottom) GetEnvelopeEdges(PixelColumn column)
    {
        float top = Math.Min(column.Bottom, column.Top);
        float bottom = Math.Max(column.Bottom, column.Top);
        // 常量和小于一个像素的波动仍绘制为一像素细线。
        if (bottom - top < 1)
        {
            float center = (top + bottom) * .5f;
            return (center - .5f, center + .5f);
        }
        return (top, bottom);
    }
}
