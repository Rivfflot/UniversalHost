using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ScottPlot;
using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using UniversalHost.Services;
using UniversalHost.ViewModels.Documents;

namespace UniversalHost.Views.Documents;

public partial class CurveMonitorView : ReactiveUserControl<CurveMonitorViewModel>
{
    private ListBoxItem? _currentHoveredItem;
    private static readonly DataFormat<CurveMonitorLayout.CurveItem> RowFormat =
    DataFormat<CurveMonitorLayout.CurveItem>.CreateInProcessFormat<CurveMonitorLayout.CurveItem>("CurveItemRow");
    public CurveMonitorView()
    {
        InitializeComponent();
        SymbolList.AddHandler(PointerPressedEvent, FirstRow_PointerPressed, handledEventsToo: true);
        SymbolList.AddHandler(DragDrop.DragOverEvent, ListBox_DragOver);
        SymbolList.AddHandler(DragDrop.DragLeaveEvent, ListBox_DragLeave);



        this.WhenActivated(disposables =>
        {
            ViewModel!.RemoveCurve += (c) =>
            {
                RemoveCurve(c);
            };
            ViewModel!.AddCurve += (c) =>
            {
                AddCurve(c);
            };
            ViewModel.RefreshCurve += () =>
            {
                CurvePlot.Refresh();
            };
            var scanLine = CurvePlot.Plot.Add.VerticalLine(0, 0.95f, Colors.Red);
            foreach (var item in ViewModel!.DisplayCurves)
            {
                AddCurve(item);
            }
            //右边留一个小间隔以显示最后一个横坐标
            CurvePlot.Plot.Axes.Margins(0, 1e-4, 0.05, 0.05);
            CurvePlot.Plot.Axes.Left.IsVisible = false;
            if (ViewModel!.DisplayCurves.Any())
            {
                ViewModel!.DisplayCurves[0].YAxis?.IsVisible = true;
            }
            Observable.Interval(TimeSpan.FromMilliseconds(50))
                .ObserveOn(AvaloniaScheduler.Instance)
                .Where(_ => GlobalStatus.Instance.IsMonitoring && this.IsVisible)
                .Subscribe(_ =>
                {
                    if (ViewModel!.DisplayCurves.Count > 0)
                    {
                        scanLine.IsVisible = true;
                        scanLine.X = ViewModel!.DisplayCurves[0].Runtime.PlotHistory.WriteIndex - 1;
                    }
                    else
                    {
                        scanLine.IsVisible = false;
                    }
                    CurvePlot.Plot.MoveToTop(ViewModel!.SelectedCurveItem?.Signal!);
                    CurvePlot.Refresh();
                }).DisposeWith(disposables);

            Observable.Interval(TimeSpan.FromMilliseconds(200))
               .Select(_ => GlobalStatus.Instance.IsMonitoring)
               .DistinctUntilChanged()
               .Publish()
               .RefCount().ObserveOn(AvaloniaScheduler.Instance).Subscribe(state =>
                {
                    if (!state)
                    {
                        if (ViewModel!.DisplayCurves.Count > 0)
                        {
                            scanLine.IsVisible = true;
                            scanLine.X = ViewModel!.DisplayCurves[0].Runtime.PlotHistory.WriteIndex - 1;
                        }
                        else
                        {
                            scanLine.IsVisible = false;
                        }
                        CurvePlot.Refresh();
                    }
                }).DisposeWith(disposables);

            ApplyPlotTheme();
            var themeChangedHandler = new EventHandler((s, e) => ApplyPlotTheme());
            this.ActualThemeVariantChanged += themeChangedHandler;
            Disposable.Create(() =>
            {
                this.ActualThemeVariantChanged -= themeChangedHandler;
            }).DisposeWith(disposables);
        });
    }
    private void ApplyPlotTheme()
    {
        if (this.ActualThemeVariant == ThemeVariant.Light)
        {
            CurvePlot.Plot.FigureBackground.Color = Colors.White;
            CurvePlot.Plot.Axes.Color(Colors.Black);

            CurvePlot.Plot.Grid.XAxisStyle.FillColor1 = new Color(1, 1, 1, 0);
            CurvePlot.Plot.Grid.YAxisStyle.FillColor1 = new Color(1, 1, 1, 0);

            CurvePlot.Plot.Grid.XAxisStyle.MajorLineStyle.Color = new Color(1, 1, 1, 25);
            CurvePlot.Plot.Grid.YAxisStyle.MajorLineStyle.Color = new Color(1, 1, 1, 25);
        }
        else
        {
            CurvePlot.Plot.FigureBackground.Color = new("#010101");
            CurvePlot.Plot.Axes.Color(new("#D8D8D8"));

            CurvePlot.Plot.Grid.XAxisStyle.FillColor1 = new Color("#888888").WithAlpha(20);
            CurvePlot.Plot.Grid.YAxisStyle.FillColor1 = new Color("#888888").WithAlpha(20);

            CurvePlot.Plot.Grid.XAxisStyle.MajorLineStyle.Color = Colors.White.WithAlpha(40);
            CurvePlot.Plot.Grid.YAxisStyle.MajorLineStyle.Color = Colors.White.WithAlpha(40);
        }
        CurvePlot.Refresh();
    }
    private void AddCurve(CurveMonitorLayout.CurveItem item)
    {
        bool isFirst = false;
        if (CurvePlot.Plot.PlottableList.Count == 1)
        {
            isFirst = true;
        }
        item.Signal = CurvePlot.Plot.Add.Signal(item.Runtime.PlotHistory.Buffer);
        item.Signal.IsVisible = item.IsVisible;
        item.Color = Avalonia.Media.Color.FromUInt32(item.Signal.Color.ARGB);
        //item.ScanLine = CurvePlot.Plot.Add.VerticalLine(0);
        //item.ScanLine.LineWidth = 0.5f;
        //item.ScanLine.IsVisible = item.IsVisible;
        item.YAxis = CurvePlot.Plot.Axes.AddLeftAxis();
        item.YAxis.IsVisible = isFirst;
        item.Signal.Axes.XAxis = CurvePlot.Plot.Axes.Bottom;
        item.Signal.Axes.YAxis = item.YAxis;
        ApplyPlotTheme();
    }
    private void RemoveCurve(CurveMonitorLayout.CurveItem item)
    {
        CurvePlot.Plot.Remove(item.Signal!);
        //CurvePlot.Plot.Remove(item.ScanLine!);
        CurvePlot.Plot.Remove(item.YAxis!);
    }
    private void ListBox_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(RowFormat))
            return;

        e.DragEffects = DragDropEffects.Move;

        if (sender is not ListBox listBox)
            return;

        var point = e.GetPosition(listBox);

        var hit = listBox.InputHitTest(point) as Control;

        var item = hit?.FindAncestorOfType<ListBoxItem>();

        if (item != _currentHoveredItem)
        {
            _currentHoveredItem?.Classes.Remove("drag-hover");

            _currentHoveredItem = item;

            _currentHoveredItem?.Classes.Add("drag-hover");
        }
    }

    private void ListBox_DragLeave(object? sender, DragEventArgs e)
    {
        _currentHoveredItem?.Classes.Remove("drag-hover");
        _currentHoveredItem = null;
    }
    private void ListBox_Drop(object? sender, DragEventArgs e)
    {
        _currentHoveredItem?.Classes.Remove("drag-hover");
        _currentHoveredItem = null;

        if (sender is not ListBox listBox)
            return;

        if (e.DataTransfer.TryGetValue(RowFormat)
            is not CurveMonitorLayout.CurveItem dragged)
            return;

        CurveMonitorLayout.CurveItem? target = GetTargetItem(e, listBox);

        if (target == null)
            return;

        if (DataContext is not CurveMonitorViewModel vm)
            return;

        var keys = vm.CurvesLayout.CurvesSource.Items;

        int oldIndex = -1;
        int newIndex = -1;
        for (int i = 0; i < keys.Count; i++)
        {
            if (dragged.Runtime.Symbol.Id == keys[i].Id)
            {
                oldIndex = i;
            }
            if (target.Runtime.Symbol.Id == keys[i].Id)
            {
                newIndex = i;
            }
        }

        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
            return;

        vm.CurvesLayout.CurvesSource.Edit(innerList =>
        {
            innerList.Move(oldIndex, newIndex);
            var temp = innerList.ToArray();
            innerList.Clear();
            innerList.AddRange(temp);
        });

        vm.SelectedCurveItem = dragged;
    }

    private CurveMonitorLayout.CurveItem? GetTargetItem(DragEventArgs e, ListBox listBox)
    {
        var point = e.GetPosition(listBox);

        var hit = listBox.InputHitTest(point) as Control;

        var item = hit?.FindAncestorOfType<ListBoxItem>();

        return item?.DataContext as CurveMonitorLayout.CurveItem;
    }
    /// <summary>
    /// 只在ListBox的第一行点击有效
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private async void FirstRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Properties.IsRightButtonPressed)
        {
            base.OnPointerPressed(e);
        }
        else
        {
            if (DataContext is not CurveMonitorViewModel vm)
                return;

            if (sender is not Control control)
                return;

            var row = control.FindAncestorOfType<ListBoxItem>();
            if (row?.DataContext is not CurveMonitorLayout.CurveItem item)
                return;

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(RowFormat, item));

            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
    }
}