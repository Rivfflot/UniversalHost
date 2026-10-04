using ScottPlot;
using System;
using System.Collections.Generic;
using UniversalHost.Models;

namespace UniversalHost.Services.Plotting;

internal readonly record struct CurveMeasurementOverlayState(
    CurveMeasurementMode Mode,
    Coordinates? Point = null,
    double CursorA = 0,
    double CursorB = 0,
    Coordinates? IntersectionA = null,
    Coordinates? IntersectionB = null);

/// <summary>仅绘制测量层；光标、标记和屏幕边缘手柄均不参与坐标轴自动缩放。</summary>
internal sealed class CurveMeasurementOverlay : IPlottable
{
    private static readonly Color FirstColor = new("#D99000");
    private static readonly Color SecondColor = new("#00A9E8");
    private readonly LineStyle _mouseLine = new() { Color = new("#795BC8"), Width = 1, Pattern = LinePattern.Dashed };
    private readonly LineStyle[] _cursorLines =
    [
        new() { Color = FirstColor, Width = 1.5f, Pattern = LinePattern.Dashed },
        new() { Color = SecondColor, Width = 1.5f, Pattern = LinePattern.Dashed },
    ];
    private readonly LabelStyle[] _handleLabels =
    [
        new() { Text = "A", ForeColor = Colors.White, BackgroundColor = FirstColor, FontSize = 11, Bold = true, Padding = 2, Alignment = Alignment.MiddleCenter },
        new() { Text = "B", ForeColor = Colors.White, BackgroundColor = SecondColor, FontSize = 11, Bold = true, Padding = 2, Alignment = Alignment.MiddleCenter },
    ];
    private readonly MarkerStyle _marker = new() { Shape = MarkerShape.OpenCircle, Size = 12, LineWidth = 2 };

    public CurveMeasurementOverlayState State { get; set; }
    public bool IsVisible { get; set; } = true;
    public IAxes Axes { get; set; } = new Axes();
    public IEnumerable<LegendItem> LegendItems => LegendItem.None;
    public AxisLimits GetAxisLimits() => AxisLimits.NoLimits;

    public float GetCursorPixel(int index, PixelRect rect)
    {
        double position = index == 0 ? State.CursorA : State.CursorB;
        return State.Mode == CurveMeasurementMode.VerticalCursors
            ? Axes.XAxis.GetPixel(position, rect)
            : Axes.YAxis.GetPixel(position, rect);
    }

    public Pixel GetHandlePixel(int index, PixelRect rect)
    {
        float position = GetCursorPixel(index, rect);
        if (State.Mode == CurveMeasurementMode.VerticalCursors)
        {
            float inset = Math.Min(16, rect.Width / 2);
            return new(Math.Clamp(position, rect.Left + inset, rect.Right - inset),
                Math.Min(rect.Top + 12 + index * 24, rect.Bottom - 8));
        }
        else
        {
            float inset = Math.Min(16, rect.Height / 2);
            return new(Math.Min(rect.Left + 12 + index * 24, rect.Right - 8),
                Math.Clamp(position, rect.Top + inset, rect.Bottom - inset));
        }
    }

    public void Render(RenderPack rp)
    {
        switch (State.Mode)
        {
            case CurveMeasurementMode.MouseCoordinates when State.Point is { } mouse:
                Pixel pixel = Axes.GetPixel(mouse);
                DrawLine(rp, _mouseLine, new(pixel.X, rp.DataRect.Top), new(pixel.X, rp.DataRect.Bottom));
                DrawLine(rp, _mouseLine, new(rp.DataRect.Left, pixel.Y), new(rp.DataRect.Right, pixel.Y));
                break;
            case CurveMeasurementMode.NearestPoint when State.Point is { } point:
                DrawPoint(rp, point, SecondColor);
                break;
            case CurveMeasurementMode.VerticalCursors:
            case CurveMeasurementMode.HorizontalCursors:
                DrawCursor(rp, 0);
                DrawCursor(rp, 1);
                if (State.Mode == CurveMeasurementMode.VerticalCursors)
                {
                    if (State.IntersectionA is { } a) DrawPoint(rp, a, FirstColor);
                    if (State.IntersectionB is { } b) DrawPoint(rp, b, SecondColor);
                }
                break;
        }
    }

    private void DrawCursor(RenderPack rp, int index)
    {
        PixelRect rect = rp.DataRect;
        float position = GetCursorPixel(index, rect);
        bool vertical = State.Mode == CurveMeasurementMode.VerticalCursors;
        float minimum = vertical ? rect.Left : rect.Top;
        float maximum = vertical ? rect.Right : rect.Bottom;
        if (position >= minimum && position <= maximum)
        {
            DrawLine(rp, _cursorLines[index],
                vertical ? new(position, rect.Top) : new(rect.Left, position),
                vertical ? new(position, rect.Bottom) : new(rect.Right, position));
        }
        else
        {
            // 保留真实测量位置，仅在对应屏幕边缘绘制向外的三角标记。
            Pixel handle = GetHandlePixel(index, rect);
            float direction = position < minimum ? -1 : 1;
            float edge = direction < 0 ? minimum + 5 : maximum - 5;
            using SkiaSharp.SKPath triangle = new();
            if (vertical)
            {
                triangle.MoveTo(edge + direction * 4, handle.Y);
                triangle.LineTo(edge - direction * 3, handle.Y - 4);
                triangle.LineTo(edge - direction * 3, handle.Y + 4);
            }
            else
            {
                triangle.MoveTo(handle.X, edge + direction * 4);
                triangle.LineTo(handle.X - 4, edge - direction * 3);
                triangle.LineTo(handle.X + 4, edge - direction * 3);
            }
            triangle.Close();
            _cursorLines[index].ApplyToPaint(rp.Paint);
            rp.Paint.IsStroke = false;
            rp.Paint.SKPaint.PathEffect = null;
            rp.Canvas.DrawPath(triangle, rp.Paint.SKPaint);
        }
        _handleLabels[index].Render(rp.Canvas, GetHandlePixel(index, rect), rp.Paint);
    }

    private void DrawPoint(RenderPack rp, Coordinates point, Color color)
    {
        Pixel pixel = Axes.GetPixel(point);
        if (!rp.DataRect.Contains(pixel)) return;
        _marker.LineColor = color;
        _marker.OutlineColor = color;
        Drawing.DrawMarker(rp.Canvas, rp.Paint, pixel, _marker);
    }

    private static void DrawLine(RenderPack rp, LineStyle style, Pixel start, Pixel end) =>
        style.Render(rp.Canvas, new PixelLine(start, end), rp.Paint);
}
