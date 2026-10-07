using ScottPlot;
using System;

namespace UniversalHost.Services.Plotting;

/// <summary>只调整选中的 Y 轴，鼠标拖动固定开始时的目标，不修改其他坐标轴。</summary>
internal sealed class CurveYAxisDragController(Plot plot)
{
    private const double WheelZoomFactor = 1.15;
    private Pixel _startPixel;
    private double _initialMin;
    private double _initialMax;

    public IYAxis? Axis { get; private set; }

    public bool TryBeginDrag(IYAxis axis, Pixel mouse)
    {
        PixelRect rect = plot.LastRender.DataRect;
        if (rect.Height <= 0 || !double.IsFinite(axis.Min) || !double.IsFinite(axis.Max) || axis.Max <= axis.Min)
            return false;

        Axis = axis;
        _startPixel = mouse.Divide((float)plot.ScaleFactor);
        _initialMin = axis.Min;
        _initialMax = axis.Max;
        return true;
    }

    public bool Move(Pixel mouse)
    {
        if (Axis is not { } axis) return false;
        PixelRect rect = plot.LastRender.DataRect;
        if (rect.Height <= 0) return false;

        float deltaY = mouse.Divide((float)plot.ScaleFactor).Y - _startPixel.Y;
        if (!float.IsFinite(deltaY)) return false;

        double previousMin = axis.Min;
        double previousMax = axis.Max;
        // 每次从按下时的范围计算，避免移动事件和重绘频率影响拖动结果。
        axis.Range.Set(_initialMin, _initialMax);
        axis.Range.PanMouse(deltaY, rect.Height);

        if (!double.IsFinite(axis.Min) || !double.IsFinite(axis.Max) || axis.Max <= axis.Min)
        {
            axis.Range.Set(previousMin, previousMax);
            return false;
        }

        return axis.Min != previousMin || axis.Max != previousMax;
    }

    public bool ZoomWheel(IYAxis axis, Pixel mouse, double delta)
    {
        if (Axis != null || delta == 0 || !double.IsFinite(delta)) return false;
        return ZoomAtMouse(axis, mouse, delta > 0 ? WheelZoomFactor : 1 / WheelZoomFactor);
    }

    private bool ZoomAtMouse(IYAxis axis, Pixel mouse, double factor)
    {
        PixelRect rect = plot.LastRender.DataRect;
        if (rect.Height <= 0 || factor <= 0 || factor == 1 || !double.IsFinite(factor) ||
            !double.IsFinite(axis.Min) || !double.IsFinite(axis.Max) || axis.Max <= axis.Min)
            return false;

        double zoomTo = axis.GetCoordinate(mouse.Divide((float)plot.ScaleFactor).Y, rect);
        if (!double.IsFinite(zoomTo)) return false;

        double previousMin = axis.Min;
        double previousMax = axis.Max;
        axis.Range.ZoomFrac(factor, zoomTo);
        if (!double.IsFinite(axis.Min) || !double.IsFinite(axis.Max) || axis.Max <= axis.Min)
        {
            axis.Range.Set(previousMin, previousMax);
            return false;
        }

        return axis.Min != previousMin || axis.Max != previousMax;
    }

    public void EndDrag() => Axis = null;
}
