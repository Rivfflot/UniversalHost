using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using System;

namespace UniversalHost.Views.Controls;

// 日期仍由原生 DatePicker 选择；使用适合紧凑数字日期的占位文字和显示比例。
public sealed class LogDatePicker : DatePicker
{
    public LogDatePicker()
    {
        Resources["StringDatePickerYearText"] = "年";
        Resources["StringDatePickerMonthText"] = "月";
        Resources["StringDatePickerDayText"] = "日";
    }

    protected override Type StyleKeyOverride => typeof(DatePicker);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Grid>("PART_ButtonContentGrid") is not { } grid) return;

        // Avalonia 默认给月份 132、年/日各 78 的星号权重，适用于完整月份名称。
        // 数字月份改为与日期等宽，保留文化设置决定的年/月/日顺序及原生弹出层。
        foreach (var name in new[] { "PART_YearTextBlock", "PART_MonthTextBlock", "PART_DayTextBlock" })
        {
            if (e.NameScope.Find<TextBlock>(name) is not { IsVisible: true } text) continue;
            // 默认月份额外保留 9 像素左边距；数字月份使用与年、日相同的居中布局。
            text.Padding = new Thickness(0, 3, 0, 6);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.TextAlignment = TextAlignment.Center;
            int column = Grid.GetColumn(text);
            if (column < grid.ColumnDefinitions.Count)
                grid.ColumnDefinitions[column].Width = new GridLength(name == "PART_YearTextBlock" ? 64 : 44, GridUnitType.Star);
        }
    }
}
