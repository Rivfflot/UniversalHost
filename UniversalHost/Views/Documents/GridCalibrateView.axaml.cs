using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DynamicData;
using System;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.ViewModels.Documents;

namespace UniversalHost.Views.Documents;

public partial class GridCalibrateView : UserControl
{
    private DataGridRow? _currentHoveredRow;
    private static readonly DataFormat<SymbolRuntime> RowFormat =
    DataFormat<SymbolRuntime>.CreateInProcessFormat<SymbolRuntime>("SymbolRuntimeRow");
    public GridCalibrateView()
    {
        InitializeComponent();
        var valueColumn = Grid.Columns.OfType<DataGridTemplateColumn>().Single();
        var integerEditor = (IDataTemplate)Resources["IntegerValueEditor"]!;
        var otherEditor = (IDataTemplate)Resources["OtherValueEditor"]!;
        valueColumn.CellEditingTemplate = new FuncDataTemplate<SymbolRuntime>((runtime, _) =>
        {
            if (!runtime.Symbol.CanDisplayHex) return otherEditor.Build(runtime);
            var editor = (NumericUpDown)integerEditor.Build(runtime)!;
            var converter = new IntegerTextConverter(runtime.ValueString);
            editor.TextConverter = converter;
            editor.Bind(NumericUpDown.TextProperty,
                CompiledBinding.Create((SymbolRuntime item) => item.ValueString, source: runtime,
                    mode: BindingMode.TwoWay, updateSourceTrigger: UpdateSourceTrigger.Explicit));
            editor.Initialized += (_, _) =>
            {
                // Initialize the numeric value after the control has loaded its bound text.
                var text = editor.Text;
                editor.SetCurrentValue(NumericUpDown.TextProperty, null);
                editor.SetCurrentValue(NumericUpDown.TextProperty, text);
            };
            editor.PropertyChanged += (_, e) =>
            {
                if (e.Property == NumericUpDown.TextProperty && string.IsNullOrEmpty(editor.Text))
                    converter.Clear();
            };
            return editor;
        });
        Grid.CellEditEnding += (_, e) =>
        {
            if (e.Column == valueColumn && e.EditAction == DataGridEditAction.Commit)
                CommitValueEdit(e);
        };
        Grid.AddHandler(PointerPressedEvent, Grid_PointerPressed, handledEventsToo: true);
        Grid.AddHandler(DragDrop.DragOverEvent, DataGrid_DragOver);
        Grid.AddHandler(DragDrop.DragLeaveEvent, DataGrid_DragLeave);
        Grid.AddHandler(InputElement.KeyDownEvent, DataGrid_KeyDown, RoutingStrategies.Tunnel);
    }

    private sealed class IntegerTextConverter(string? input) : IValueConverter
    {
        private string? _input = input;
        public void Clear() => _input = null;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            _input = (string?)value;
            var text = _input?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? (decimal)ulong.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                : decimal.Parse(text, NumberStyles.Integer, culture);
        }

        // Preserve the entered spelling when NumericUpDown formats on focus loss.
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => _input;
    }

    private static void CommitValueEdit(DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.DataContext is not SymbolRuntime runtime) return;
        var (text, property) = e.EditingElement switch
        {
            NumericUpDown number => (number.Text, NumericUpDown.TextProperty),
            TextBox box => (box.Text, TextBox.TextProperty),
            _ => (null, null)
        };
        if (property == null) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(text)) runtime.EncodeValue(text);
            BindingOperations.GetBindingExpressionBase(e.EditingElement, property)?.UpdateSource();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            e.Cancel = true;
            NotificationService.Show("标定输入错误", $"{runtime.Symbol.Name}：{ex.Message}", NotificationType.Warning);
        }
    }

    private async Task DataGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || DataContext is not GridCalibrateViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (Grid.CommitEdit()) await vm.DownloadSelectedSymbolValue();
        }
        else if (e.Source is not TextBox)
        {
            var command = e.Key switch
            {
                Key.Delete => (ICommand)vm.RemoveSelectedSymbolCommand,
                Key.D => vm.DownloadSelectedSymbolValueCommand,
                Key.U => vm.UploadSelectedSymbolValueCommand,
                _ => null
            };
            if (command?.CanExecute(null) != true) return;
            e.Handled = true;
            command.Execute(null);
        }
    }
    private void DataGrid_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(RowFormat)) return;
        // 获取鼠标当前悬停的行控件
        if (sender is not DataGrid grid)
        {
            return;
        }
        var point = e.GetPosition(grid);
        var hit = grid?.InputHitTest(point) as Control;
        var row = hit?.FindAncestorOfType<DataGridRow>();
        // 如果滑到了新的一行上
        if (row != _currentHoveredRow)
        {
            // 抹除旧行上的视觉效果伪类
            if (_currentHoveredRow != null)
            {
                _currentHoveredRow.Classes.Remove("drag-hover");
            }
            // 为新行注入视觉效果伪类
            _currentHoveredRow = row;
            if (_currentHoveredRow != null)
            {
                _currentHoveredRow.Classes.Add("drag-hover");
            }
        }
    }

    private void DataGrid_DragLeave(object? sender, DragEventArgs e)
    {
        // 离开 DataGrid 区域时，清除视觉效果标记
        if (_currentHoveredRow != null)
        {
            _currentHoveredRow.Classes.Remove("drag-hover");
            _currentHoveredRow = null;
        }
    }
    private async void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Properties.IsRightButtonPressed)
        {
            base.OnPointerPressed(e);
        }
        else
        {
            if (sender is not DataGrid grid) return;

            // 获取当前鼠标点击处的物理坐标
            var point = e.GetPosition(grid);

            // 利用 InputHitTest 拿到点击到的具体前端可视控件（如 TextBlock）
            var hit = grid.InputHitTest(point) as Control;
            if (hit == null) return;

            // 从 hit 控件向上寻找它所属的 DataGridCell
            var cell = hit as DataGridCell ?? hit.FindAncestorOfType<DataGridCell>();
            if (cell == null) return;

            // 找到该单元格所处的单元格横向容器（DataGridCellsPresenter）
            var presenter = cell.FindAncestorOfType<Avalonia.Controls.Primitives.DataGridCellsPresenter>();
            if (presenter == null) return;

            // 计算当前单元格是当前行的第几列 (0, 1, 2, 3...)
            int columnIndex = presenter.Children.IndexOf(cell);

            // 安全边界拦截
            if (columnIndex < 0 || columnIndex >= grid.Columns.Count) return;

            // 拿到对应的列对象，并对其 Header 进行强判定
            var column = grid.Columns[columnIndex];
            string? headerText = column.Header?.ToString();

            // 仅允许在 "变量名" 和 "别名" 列上触发拖拽，其余列（值、单位）直接拦截拒绝
            if (headerText != "变量名" && headerText != "别名")
            {
                return;
            }

            // 验证通过，寻找当前行的 DataContext
            var row = hit.FindAncestorOfType<DataGridRow>();
            if (row?.DataContext is not SymbolRuntime item)
                return;

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(RowFormat, item));

            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
    }

    private void Grid_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(RowFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    private void Grid_Drop(object? sender, DragEventArgs e)
    {
        if (_currentHoveredRow != null)
        {
            _currentHoveredRow.Classes.Remove("drag-hover");
            _currentHoveredRow = null;
        }

        if (e.DataTransfer.TryGetValue(RowFormat) is not SymbolRuntime dragged) return;

        if (DataContext is not GridCalibrateViewModel vm) return;

        var target = GetRowUnderPointer(e, sender as DataGrid);
        if (target == null) return;

        var keys = vm.KeysSource.Items;

        int oldIndex = keys.IndexOf(dragged.Symbol.Id);
        int newIndex = keys.IndexOf(target.Symbol.Id);

        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
            return;

        vm.KeysSource.Edit(innerList =>
        {
            innerList.Move(oldIndex, newIndex);
            var temp = innerList.ToArray();
            innerList.Clear();
            innerList.AddRange(temp);
        });

        vm.SelectedSymbolRuntime = dragged;
    }

    private SymbolRuntime? GetRowUnderPointer(DragEventArgs e, DataGrid? grid)
    {
        if (grid == null)
            return null;

        var point = e.GetPosition(grid);

        var hit = grid.InputHitTest(point) as Control;
        if (hit == null)
            return null;

        var row = hit.FindAncestorOfType<DataGridRow>();
        return row?.DataContext as SymbolRuntime;
    }
}
