using ScottPlot;

namespace UniversalHost.Services.Plotting;

/// <summary>独立 Y 轴隐藏时跳过刻度生成，显示时沿用原刻度生成器。</summary>
internal sealed class CurveAxisTicks(IAxis axis, ITickGenerator generator) : ITickGenerator
{
    public Tick[] Ticks => axis.IsVisible ? generator.Ticks : [];
    public int MaxTickCount { get => generator.MaxTickCount; set => generator.MaxTickCount = value; }

    public void Regenerate(CoordinateRange range, Edge edge, PixelLength size, Paint paint, LabelStyle labelStyle)
    {
        if (axis.IsVisible) generator.Regenerate(range, edge, size, paint, labelStyle);
    }
}
