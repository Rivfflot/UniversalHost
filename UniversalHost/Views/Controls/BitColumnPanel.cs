using Avalonia;
using Avalonia.Controls;
using System;

namespace UniversalHost.Views.Controls;

/// <summary>
/// 按固定的位数从上到下排列，再向右分列；行高由内容测量结果决定。
/// </summary>
public sealed class BitColumnPanel : Panel
{
    public static readonly StyledProperty<int> RowsPerColumnProperty =
        AvaloniaProperty.Register<BitColumnPanel, int>(nameof(RowsPerColumn), 16,
            validate: value => value > 0);

    private double[] _columnWidths = [];
    private double _rowHeight;

    static BitColumnPanel()
    {
        AffectsMeasure<BitColumnPanel>(RowsPerColumnProperty);
    }

    public int RowsPerColumn
    {
        get => GetValue(RowsPerColumnProperty);
        set => SetValue(RowsPerColumnProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int rows = RowsPerColumn;
        int columns = (Children.Count + rows - 1) / rows;
        _columnWidths = new double[columns];
        _rowHeight = 0;

        // 测量自然尺寸，窗口高度和 DPI 舍入只影响尺寸，不决定分列位置。
        var childSize = new Size(double.PositiveInfinity, double.PositiveInfinity);
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(childSize);
            int column = i / rows;
            _columnWidths[column] = Math.Max(_columnWidths[column], child.DesiredSize.Width);
            _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
        }

        double width = 0;
        foreach (double columnWidth in _columnWidths)
        {
            width += columnWidth;
        }

        return new Size(width, _rowHeight * Math.Min(rows, Children.Count));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int rows = RowsPerColumn;
        double x = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            int row = i % rows;
            int column = i / rows;
            if (row == 0 && column > 0)
            {
                x += _columnWidths[column - 1];
            }

            Children[i].Arrange(new Rect(x, row * _rowHeight, _columnWidths[column], _rowHeight));
        }

        return finalSize;
    }
}
