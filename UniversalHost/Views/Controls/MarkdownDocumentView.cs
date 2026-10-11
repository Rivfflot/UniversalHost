using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using System.Collections.Generic;
using UniversalHost.Models;

namespace UniversalHost.Views.Controls;

/// <summary>
/// 用原生控件显示编译期生成的文档，保留可选择的文本与表格自动换行。
/// </summary>
public sealed class MarkdownDocumentView : StackPanel
{
    private readonly Dictionary<int, Control> _headings = [];
    public IReadOnlyDictionary<int, Control> Headings => _headings;

    public void SetDocument(Documentation document)
    {
        Children.Clear();
        _headings.Clear();
        for (int index = 0; index < document.Blocks.Count; index++)
        {
            var block = document.Blocks[index];
            Control control;
            if (block.TableRows is { } rows)
            {
                control = CreateTable(rows);
            }
            else if (block.HeadingLevel > 0)
            {
                var heading = CreateText(block.Inlines);
                heading.FontSize = block.HeadingLevel switch { 1 => 28, 2 => 23, 3 => 19, 4 => 17, _ => 16 };
                heading.LineHeight = double.NaN;
                heading.FontWeight = FontWeight.SemiBold;
                heading.Margin = new Thickness(0, index == 0 ? 0 : 20, 0, 10);
                control = heading;
                _headings.Add(index, heading);
            }
            else if (block.ListMarker is { } marker)
            {
                var listItem = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    Margin = new Thickness(16, 0, 0, 6)
                };
                listItem.Children.Add(new TextBlock
                {
                    Text = marker,
                    FontSize = 14,
                    LineHeight = 24,
                    MinWidth = 20,
                    Margin = new Thickness(0, 0, 8, 0)
                });
                var text = CreateText(block.Inlines);
                Grid.SetColumn(text, 1);
                listItem.Children.Add(text);
                control = listItem;
            }
            else
            {
                control = CreateText(block.Inlines);
                control.Margin = new Thickness(0, 0, 0, 12);
            }
            Children.Add(control);
        }
    }

    private static SelectableTextBlock CreateText(IReadOnlyList<DocumentationInline> inlines)
    {
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 24 };
        foreach (var inline in inlines)
        {
            var run = new Run(inline.Text);
            if (inline.IsCode)
                run.FontFamily = new FontFamily("Consolas, Microsoft YaHei");
            text.Inlines!.Add(run);
        }
        return text;
    }

    private static Grid CreateTable(IReadOnlyList<IReadOnlyList<DocumentationCell>> rows)
    {
        var table = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        for (int column = 0; column < rows[0].Count; column++)
            table.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int row = 0; row < rows.Count; row++)
        {
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int column = 0; column < rows[row].Count; column++)
            {
                var text = CreateText(rows[row][column].Inlines);
                if (row == 0) text.FontWeight = FontWeight.SemiBold;
                var cell = new Border
                {
                    Child = text,
                    Padding = new Thickness(10, 7),
                    BorderThickness = new Thickness(1, row == 0 ? 1 : 0, column == rows[row].Count - 1 ? 1 : 0, 1)
                };
                cell.Classes.Add("documentation-table-cell");
                if (row == 0) cell.Classes.Add("documentation-table-header");
                else if (row % 2 == 0) cell.Classes.Add("documentation-table-alternate");
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                table.Children.Add(cell);
            }
        }
        return table;
    }
}
