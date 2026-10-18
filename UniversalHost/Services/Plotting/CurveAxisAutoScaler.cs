using ScottPlot;
using ScottPlot.AutoScalers;

namespace UniversalHost.Services.Plotting;

/// <summary>独立曲线轴直接使用该曲线的极值，避免对每个轴重新遍历整张图。</summary>
internal static class CurveAxisAutoScaler
{
    public static bool TryAutoScaleY(ScottPlot.Plottables.Signal signal, IAutoScaler autoScaler)
    {
        if (autoScaler is not FractionalAutoScaler scaler) return false;
        CoordinateRange range = signal.GetAxisLimits().YRange;
        double min = range.Min;
        double max = range.Max;
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            min = -10;
            max = 10;
        }
        if (min == max)
        {
            min -= 1;
            max += 1;
            if (min == max)
            {
                min = NumericConversion.DecrementLargeDouble(min);
                max = NumericConversion.IncrementLargeDouble(max);
            }
        }
        double span = max - min;
        double bottom = min - span * scaler.BottomFraction;
        double top = max + span * scaler.TopFraction;
        if (!double.IsFinite(bottom) || !double.IsFinite(top) || bottom == top) return false;
        signal.Axes.YAxis.Range.Set(scaler.InvertedY ? top : bottom, scaler.InvertedY ? bottom : top);
        return true;
    }
}
