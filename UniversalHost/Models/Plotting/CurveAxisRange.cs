using System;
using System.Text.Json.Serialization;

namespace UniversalHost.Models.Plotting;

/// <summary>坐标范围同时记录平移位置和缩放比例，不保存绘图库对象。</summary>
public sealed class CurveAxisRange
{
    public double Min { get; set; }
    public double Max { get; set; }

    [JsonIgnore]
    public bool IsValid => double.IsFinite(Min) && double.IsFinite(Max) &&
        Max > Min && double.IsFinite(Max - Min);

    public static CurveAxisRange? Create(double min, double max)
    {
        var range = new CurveAxisRange { Min = min, Max = max };
        return range.IsValid ? range : null;
    }
}
