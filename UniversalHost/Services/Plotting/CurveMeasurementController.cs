using ScottPlot;
using System;
using System.Collections.Generic;
using System.Globalization;
using UniversalHost.Models;

namespace UniversalHost.Services.Plotting;

internal sealed record CurveMeasurementTarget(CurvePlotBuffer History, IYAxis YAxis, string Label, bool IsVisible);

/// <summary>管理临时测量位置和读数，鼠标捕获与控件输入路由由视图处理。</summary>
internal sealed class CurveMeasurementController : IDisposable
{
    private const double NearestPointRadius = 12;
    private const float DragRadius = 6;
    private readonly Plot _plot;
    private readonly Func<CurveMeasurementTarget?> _getSelectedTarget;
    private readonly Func<CurveMeasurementTarget?> _getReferenceTarget;
    private readonly Action<CurveMeasurementReadout> _publishReadout;
    private readonly CurveMeasurementOverlay _overlay = new();
    private CurveMeasurementReadout _readout = CurveMeasurementReadout.Empty;
    private CurveMeasurementMode _mode;
    private Pixel? _mouse;
    private double _cursorA;
    private double _cursorB;
    private bool _initializeCursors;
    private int _dragIndex = -1;
    private double _dragOffset;

    public bool IsDragging => _dragIndex >= 0;
    public Cursor MouseCursor => _mouse is { } mouse ? GetMouseCursor(mouse) : Cursor.Arrow;
    private bool IsVertical => _mode == CurveMeasurementMode.VerticalCursors;
    private bool IsCursorMode => IsVertical || _mode == CurveMeasurementMode.HorizontalCursors;

    public CurveMeasurementController(Plot plot, Func<CurveMeasurementTarget?> getSelectedTarget,
        Func<CurveMeasurementTarget?> getReferenceTarget, Action<CurveMeasurementReadout> publishReadout)
    {
        _plot = plot;
        _getSelectedTarget = getSelectedTarget;
        _getReferenceTarget = getReferenceTarget;
        _publishReadout = publishReadout;
        _overlay.Axes.XAxis = plot.Axes.Bottom;
        _overlay.Axes.YAxis = plot.Axes.Left;
        plot.Add.Plottable(_overlay);
    }

    public void SetMode(CurveMeasurementMode mode)
    {
        if (_mode == mode) return;
        EndDrag();
        _mode = mode;
        _initializeCursors = IsCursorMode;
        Update();
    }

    public bool PointerMoved(Pixel mouse)
    {
        _mouse = mouse;
        if (IsDragging) MoveDraggedCursor(mouse);
        return Update();
    }

    public bool PointerExited()
    {
        _mouse = null;
        return Update();
    }

    public bool TryBeginDrag(Pixel mouse)
    {
        Update();
        int index = HitTest(mouse);
        if (index < 0) return false;

        _dragIndex = index;
        _mouse = mouse;
        PixelRect rect = _plot.LastRender.DataRect;
        float cursorPixel = _overlay.GetCursorPixel(index, rect);
        bool visible = IsVertical ? rect.ContainsX(cursorPixel) : rect.ContainsY(cursorPixel);
        Coordinates coordinates = _plot.GetCoordinates(mouse, _overlay.Axes.XAxis, _overlay.Axes.YAxis);
        double position = index == 0 ? _cursorA : _cursorB;
        // 在线上拖动保留点击偏移；边缘手柄则将视野外光标带回鼠标位置。
        _dragOffset = visible ? position - (IsVertical ? coordinates.X : coordinates.Y) : 0;
        MoveDraggedCursor(mouse);
        Update();
        return true;
    }

    public void EndDrag() => _dragIndex = -1;

    public Cursor GetMouseCursor(Pixel mouse) => IsDragging || HitTest(mouse) >= 0
        ? (IsVertical ? Cursor.SizeWestEast : Cursor.SizeNorthSouth)
        : Cursor.Arrow;

    private int HitTest(Pixel mouse)
    {
        if (!IsCursorMode || _initializeCursors) return -1;
        Pixel pixel = mouse.Divide((float)_plot.ScaleFactor);
        PixelRect rect = _plot.LastRender.DataRect;
        if (!rect.Contains(pixel)) return -1;
        float handleRadius = 12 / (float)_plot.ScaleFactor;
        for (int i = 0; i < 2; i++)
        {
            Pixel handle = _overlay.GetHandlePixel(i, rect);
            float dx = handle.X - pixel.X;
            float dy = handle.Y - pixel.Y;
            if (dx * dx + dy * dy <= handleRadius * handleRadius) return i;
        }

        int nearest = -1;
        float distance = DragRadius / (float)_plot.ScaleFactor;
        for (int i = 0; i < 2; i++)
        {
            float cursorPixel = _overlay.GetCursorPixel(i, rect);
            bool visible = IsVertical ? rect.ContainsX(cursorPixel) : rect.ContainsY(cursorPixel);
            float candidate = Math.Abs(cursorPixel - (IsVertical ? pixel.X : pixel.Y));
            if (visible && candidate <= distance)
            {
                nearest = i;
                distance = candidate;
            }
        }
        return nearest;
    }

    private void MoveDraggedCursor(Pixel mouse)
    {
        PixelRect rect = _plot.LastRender.DataRect;
        float scale = (float)_plot.ScaleFactor;
        Pixel clamped = new(Math.Clamp(mouse.X / scale, rect.Left, rect.Right),
            Math.Clamp(mouse.Y / scale, rect.Top, rect.Bottom));
        Coordinates coordinates = _plot.GetCoordinates(clamped.Multiply(scale), _overlay.Axes.XAxis, _overlay.Axes.YAxis);
        double position = (IsVertical ? coordinates.X : coordinates.Y) + _dragOffset;
        if (!double.IsFinite(position)) return;
        if (_dragIndex == 0) _cursorA = position;
        else _cursorB = position;
    }

    public void BringToFront() => _plot.MoveToTop(_overlay);

    /// <summary>采样、选择或坐标轴变化时更新；返回测量层或读数是否发生变化。</summary>
    public bool Update()
    {
        CurveMeasurementTarget? selected = _getSelectedTarget();
        CurveMeasurementTarget? reference = _getReferenceTarget();
        IYAxis yAxis = reference?.YAxis ?? _plot.Axes.Left;
        bool changed = !ReferenceEquals(_overlay.Axes.YAxis, yAxis);
        _overlay.Axes.YAxis = yAxis;

        PixelRect rect = _plot.LastRender.DataRect;
        bool hasLayout = rect.Width > 0 && rect.Height > 0;
        if (_initializeCursors && hasLayout)
        {
            double start = IsVertical ? _plot.Axes.Bottom.Min : yAxis.Min;
            double end = IsVertical ? _plot.Axes.Bottom.Max : yAxis.Max;
            if (double.IsFinite(start) && double.IsFinite(end) && start != end)
            {
                _cursorA = start + (end - start) / 3;
                _cursorB = start + (end - start) * 2 / 3;
                _initializeCursors = false;
            }
        }

        var state = new CurveMeasurementOverlayState(_initializeCursors ? CurveMeasurementMode.None : _mode,
            CursorA: _cursorA, CursorB: _cursorB);
        string target = reference == null ? "" : "曲线：" + reference.Label;
        CurveMeasurementReadout readout;
        bool hasMouse = _mouse is { } mouse && hasLayout && rect.Contains(mouse.Divide((float)_plot.ScaleFactor));
        Coordinates coordinates = hasMouse
            ? _plot.GetCoordinates(_mouse!.Value, _overlay.Axes.XAxis, yAxis)
            : default;

        switch (_mode)
        {
            case CurveMeasurementMode.MouseCoordinates:
                if (hasMouse) state = state with { Point = coordinates };
                readout = new("鼠标坐标", target, FormatXY("", hasMouse ? coordinates : null), "", "", "",
                    hasMouse ? "" : "移入图表查看坐标");
                break;
            case CurveMeasurementMode.NearestPoint:
                string nearestStatus = GetSelectedTargetStatus(selected);
                Coordinates? nearest = null;
                if (nearestStatus.Length == 0 && hasMouse)
                {
                    double xUnits = Math.Abs(_plot.Axes.Bottom.GetCoordinateDistance(1 / (float)_plot.ScaleFactor, rect));
                    double yUnits = Math.Abs(selected!.YAxis.GetCoordinateDistance(1 / (float)_plot.ScaleFactor, rect));
                    if (CurveMeasurementQuery.TryGetNearestPoint(selected.History, coordinates.X, coordinates.Y,
                        xUnits, yUnits, NearestPointRadius, out int index, out double value))
                    {
                        nearest = new(index, value);
                        state = state with { Point = nearest };
                    }
                    else nearestStatus = "鼠标附近没有采样点";
                }
                else if (nearestStatus.Length == 0) nearestStatus = "靠近所选曲线查看最近采样点";
                readout = new("曲线最近点", selected == null ? "" : "曲线：" + selected.Label,
                    FormatXY("", nearest), "", "", "", nearestStatus);
                break;
            case CurveMeasurementMode.VerticalCursors:
                Coordinates? first = null;
                Coordinates? second = null;
                string verticalStatus = GetSelectedTargetStatus(selected);
                if (!_initializeCursors && verticalStatus.Length == 0)
                {
                    if (CurveMeasurementQuery.TryGetIntersection(selected!.History, _cursorA, out double firstY)) first = new(_cursorA, firstY);
                    if (CurveMeasurementQuery.TryGetIntersection(selected.History, _cursorB, out double secondY)) second = new(_cursorB, secondY);
                    if (first == null || second == null) verticalStatus = "部分光标处无有效曲线交点";
                }
                state = state with { IntersectionA = first, IntersectionB = second };
                readout = new("垂直光标测量", selected == null ? "" : "曲线：" + selected.Label,
                    FormatCursorXY("A", _cursorA, first), FormatCursorXY("B", _cursorB, second),
                    "ΔX (B − A) = " + Format(_initializeCursors ? null : _cursorB - _cursorA),
                    "ΔY (B − A) = " + Format(first is { } a && second is { } b ? b.Y - a.Y : null),
                    AppendViewportStatus(verticalStatus, rect));
                break;
            case CurveMeasurementMode.HorizontalCursors:
                readout = new("水平光标测量", target,
                    "A  Y = " + Format(_initializeCursors ? null : _cursorA),
                    "B  Y = " + Format(_initializeCursors ? null : _cursorB), "",
                    "ΔY (B − A) = " + Format(_initializeCursors ? null : _cursorB - _cursorA),
                    AppendViewportStatus("", rect));
                break;
            default:
                readout = CurveMeasurementReadout.Empty;
                break;
        }

        changed |= _overlay.State != state;
        _overlay.State = state;
        if (_readout != readout)
        {
            _readout = readout;
            _publishReadout(readout);
            changed = true;
        }
        return changed;
    }

    private string FormatCursorXY(string label, double x, Coordinates? point) =>
        label + "  X = " + Format(_initializeCursors ? null : x) + "\n    Y = " + Format(point?.Y);

    private string AppendViewportStatus(string status, PixelRect rect)
    {
        if (_initializeCursors) return "等待图表显示后初始化光标";
        List<string> messages = [];
        if (status.Length > 0) messages.Add(status);
        for (int i = 0; i < 2; i++)
        {
            double position = i == 0 ? _cursorA : _cursorB;
            double minimum = IsVertical ? _plot.Axes.Bottom.Min : _overlay.Axes.YAxis.Min;
            double maximum = IsVertical ? _plot.Axes.Bottom.Max : _overlay.Axes.YAxis.Max;
            if (position >= Math.Min(minimum, maximum) && position <= Math.Max(minimum, maximum)) continue;
            float pixel = IsVertical ? _plot.Axes.Bottom.GetPixel(position, rect) : _overlay.Axes.YAxis.GetPixel(position, rect);
            string edge = IsVertical ? (pixel < rect.Left ? "左" : "右") : (pixel < rect.Top ? "上" : "下");
            messages.Add($"{(i == 0 ? "A" : "B")} 在视野{edge}侧，可拖动边缘标记找回");
        }
        return string.Join("；", messages);
    }

    private static string GetSelectedTargetStatus(CurveMeasurementTarget? target) => target switch
    {
        null => "请在左侧选择一条曲线",
        { IsVisible: false } => "所选曲线已隐藏",
        { History.Count: 0 } => "所选曲线暂无采样数据",
        _ => "",
    };

    private static string FormatXY(string label, Coordinates? point) =>
        label + "X = " + Format(point?.X) + "\nY = " + Format(point?.Y);

    private static string Format(double? value) => value is { } number && double.IsFinite(number)
        ? number.ToString("G7", CultureInfo.CurrentCulture) : "—";

    public void Dispose()
    {
        EndDrag();
        _plot.Remove(_overlay);
        _publishReadout(CurveMeasurementReadout.Empty);
    }
}
