using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI.Avalonia.Reactive;
using ReactiveUI.Primitives.Reactive.Concurrency;
using ReactiveUI.Reactive;
using ScottPlot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using UniversalHost.Models.Plotting;
using UniversalHost.Services;
using UniversalHost.Services.Plotting;
using UniversalHost.ViewModels.Documents;

namespace UniversalHost.Views.Documents;

public partial class CurveMonitorView : ReactiveUserControl<CurveMonitorViewModel>
{
    private const double XAxisRightMargin = 1e-4;
    private ListBoxItem? _currentHoveredItem;
    private CurveMeasurementController? _measurements;
    private IPointer? _measurementDragPointer;
    private readonly CurveYAxisDragController _yAxisDrag;
    private IPointer? _yAxisDragPointer;
    private bool _isYAxisKeyPressed;
    private bool _plotInputWasEnabled;
    private bool _sharedYAxisDirty;
    private sealed class CurveRenderState(ScottPlot.Plottables.Signal signal, ScottPlot.AxisPanels.LeftAxis yAxis)
    {
        public ScottPlot.Plottables.Signal Signal { get; } = signal;
        public ScottPlot.AxisPanels.LeftAxis YAxis { get; } = yAxis;
        public long LastVersion { get; set; } = -1;
        public long LastAutoScaleVersion { get; set; } = -1;
        public bool LastAutoScaleVisible { get; set; }
        public bool LastVisible { get; set; }
    }
    private readonly Dictionary<CurveMonitorLayout.CurveItem, CurveRenderState> _curves = [];
    private static readonly DataFormat<CurveMonitorLayout.CurveItem> RowFormat =
    DataFormat<CurveMonitorLayout.CurveItem>.CreateInProcessFormat<CurveMonitorLayout.CurveItem>("CurveItemRow");
    public CurveMonitorView()
    {
        InitializeComponent();
        _yAxisDrag = new(CurvePlot.Plot);
        // 默认 Y 轴隐藏时仍生成刻度，供横向网格线使用。
        // Y 是按住生效的窗口级手势，需同时监听按下和松开，不能使用切换式 KeyBinding。
        AddHandler(KeyDownEvent, CurveMonitorView_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, CurveMonitorView_KeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (CurvePlot.Menu != null)
        {
            // 仅翻译默认菜单文字，保留 ScottPlot 原有的点击回调。
            for (int i = 0; i < CurvePlot.Menu.ContextMenuItems.Count; i++)
            {
                var item = CurvePlot.Menu.ContextMenuItems[i];
                item.Label = item.Label switch
                {
                    "Save Image" => "保存图像",
                    "Copy to Clipboard" => "复制图像",
                    "Autoscale" => "坐标轴自适应",
                    _ => item.Label,
                };
                CurvePlot.Menu.ContextMenuItems[i] = item;
            }
        }
        SymbolList.AddHandler(PointerPressedEvent, FirstRow_PointerPressed, handledEventsToo: true);
        SymbolList.AddHandler(DragDrop.DragOverEvent, ListBox_DragOver);
        SymbolList.AddHandler(DragDrop.DragLeaveEvent, ListBox_DragLeave);
        // 在 AvaPlot 处理鼠标按下之前拦截选中 Y 轴和光标拖动，避免同时启动图表平移。
        CurvePlot.AddHandler(PointerPressedEvent, CurvePlot_PointerPressed, RoutingStrategies.Tunnel);
        CurvePlot.AddHandler(PointerMovedEvent, CurvePlot_PointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        CurvePlot.AddHandler(PointerReleasedEvent, CurvePlot_YAxisPointerReleased, RoutingStrategies.Tunnel);
        CurvePlot.AddHandler(PointerReleasedEvent, CurvePlot_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        CurvePlot.AddHandler(PointerWheelChangedEvent, CurvePlot_PointerWheelChanged, RoutingStrategies.Tunnel);
        CurvePlot.PointerExited += (_, _) =>
        {
            bool changed = _measurements?.PointerExited() == true;
            if (_measurementDragPointer == null && _yAxisDragPointer == null) CurvePlot.SetCursor(ScottPlot.Cursor.Arrow);
            if (changed) CurvePlot.Refresh();
        };
        CurvePlot.PointerCaptureLost += (_, _) =>
        {
            EndYAxisDrag();
            EndMeasurementDrag();
        };
        this.WhenActivated(disposables =>
        {
            var viewModel = ViewModel;
            if (viewModel == null) return;

            var subscriptions = new CompositeDisposable();
            this.GetObservable(IsKeyboardFocusWithinProperty)
                .Where(hasFocus => !hasFocus)
                .Subscribe(_ => ResetYAxisInput()).DisposeWith(subscriptions);
            if (TopLevel.GetTopLevel(this) is WindowBase window)
            {
                EventHandler deactivatedHandler = (_, _) => ResetYAxisInput();
                window.Deactivated += deactivatedHandler;
                Disposable.Create(() => window.Deactivated -= deactivatedHandler).DisposeWith(subscriptions);
            }
            viewModel.WhenAnyValue(x => x.SelectedCurveItem)
                .Skip(1)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(_ => EndYAxisDrag()).DisposeWith(subscriptions);
            var measurements = new CurveMeasurementController(CurvePlot.Plot,
                () => GetMeasurementTarget(viewModel.SelectedCurveItem),
                () => GetMeasurementTarget(viewModel.SelectedCurveItem) ??
                    GetMeasurementTarget(viewModel.DisplayCurves.FirstOrDefault(x => x.IsVisible)),
                readout => viewModel.MeasurementReadout = readout);
            _measurements = measurements;
            var monitorConfig = ProjectSaveService.Instance.Settings.MonitorConfig;
            void SetXAxisLimits()
            {
                // 固定显示完整记录长度，右边保留小间隔以显示最后一个横坐标。
                CurvePlot.Plot.Axes.SetLimitsX(0, monitorConfig.MaxSaveLen * (1 + XAxisRightMargin));
            }
            CurvePlot.Plot.Axes.Margins(0, XAxisRightMargin, 0.05, 0.05);
            var sharedYAxis = CurvePlot.Plot.Axes.Left;
            bool sharedYAxisInitialized = viewModel.CurvesLayout.SharedYAxisRange is { IsValid: true };
            if (viewModel.CurvesLayout.SharedYAxisRange is { IsValid: true } sharedRange)
                sharedYAxis.Range.Set(sharedRange.Min, sharedRange.Max);
            if (viewModel.CurvesLayout.XAxisRange is { IsValid: true } xRange)
                CurvePlot.Plot.Axes.SetLimitsX(xRange.Min, xRange.Max);
            else
                SetXAxisLimits();
            var scanLine = CurvePlot.Plot.Add.VerticalLine(0, 0.95f, Colors.Red);
            scanLine.IsVisible = false;
            void RefreshPlot(bool force = true, bool resetAutoScale = false)
            {
                UpdateYAxisAssignments();
                bool changed = UpdateCurveRenderRanges() || _sharedYAxisDirty;
                if (!force && !changed)
                {
                    if (measurements.Update()) CurvePlot.Refresh();
                    return;
                }
                if (viewModel.DisplayCurves.FirstOrDefault(x => x.IsVisible && x.Runtime.PlotHistory.Count > 0) is { } first)
                {
                    var history = first.Runtime.PlotHistory;
                    var state = history.GetState();
                    scanLine.IsVisible = state.Count > 0;
                    scanLine.X = (state.WriteIndex + history.Capacity - 1) % history.Capacity;
                }
                else
                {
                    scanLine.IsVisible = false;
                }
                if (viewModel.SelectedCurveItem is { } selected && _curves.TryGetValue(selected, out var curve))
                {
                    CurvePlot.Plot.MoveToTop(curve.Signal);
                }
                if (viewModel.CurvesLayout.IsYAxisAutoScaleEnabled)
                {
                    bool autoScaled = resetAutoScale;
                    if (viewModel.CurvesLayout.IsSharedYAxisEnabled)
                    {
                        if (resetAutoScale || _sharedYAxisDirty || _curves.Values.Any(state =>
                            state.LastAutoScaleVisible != state.Signal.IsVisible ||
                            (state.Signal.IsVisible && state.LastAutoScaleVersion != state.LastVersion)))
                        {
                            AutoScaleSharedYAxis();
                            foreach (var state in _curves.Values)
                            {
                                state.LastAutoScaleVersion = state.LastVersion;
                                state.LastAutoScaleVisible = state.Signal.IsVisible;
                            }
                            autoScaled = true;
                            _sharedYAxisDirty = false;
                        }
                    }
                    else foreach (var curveState in _curves.Values)
                    {
                        // 切换标签、选择曲线等无数据变化的重绘不能覆盖暂存的范围。
                        if (curveState.Signal.IsVisible &&
                            (resetAutoScale || curveState.LastAutoScaleVersion != curveState.LastVersion))
                        {
                            if (!CurveAxisAutoScaler.TryAutoScaleY(curveState.Signal, CurvePlot.Plot.Axes.AutoScaler))
                                CurvePlot.Plot.Axes.AutoScaleY(curveState.YAxis);
                            curveState.LastAutoScaleVersion = curveState.LastVersion;
                            autoScaled = true;
                        }
                    }
                    if (autoScaled) SetXAxisLimits();
                }
                measurements.Update();
                measurements.BringToFront();
                _sharedYAxisDirty = false;
                CurvePlot.Refresh();
            }
            Action<CurveMonitorLayout.CurveItem> removeCurveHandler = RemoveCurve;
            Action<CurveMonitorLayout.CurveItem> addCurveHandler = AddCurve;
            Action refreshCurveHandler = () => RefreshPlot();
            Action capturePlotStateHandler = () =>
            {
                var xAxis = CurvePlot.Plot.Axes.Bottom;
                viewModel.CurvesLayout.XAxisRange = CurveAxisRange.Create(xAxis.Min, xAxis.Max);
                viewModel.CurvesLayout.SharedYAxisRange = CurveAxisRange.Create(sharedYAxis.Min, sharedYAxis.Max);
                foreach (var (item, curve) in _curves)
                    item.YAxisRange = CurveAxisRange.Create(curve.YAxis.Min, curve.YAxis.Max);
            };
            var themeChangedHandler = new EventHandler((s, e) => ApplyPlotTheme());
            viewModel.RemoveCurve += removeCurveHandler;
            viewModel.AddCurve += addCurveHandler;
            viewModel.RefreshCurve += refreshCurveHandler;
            viewModel.CapturePlotState += capturePlotStateHandler;
            ActualThemeVariantChanged += themeChangedHandler;

            Disposable.Create(() =>
            {
                capturePlotStateHandler();
                viewModel.RemoveCurve -= removeCurveHandler;
                viewModel.AddCurve -= addCurveHandler;
                viewModel.RefreshCurve -= refreshCurveHandler;
                viewModel.CapturePlotState -= capturePlotStateHandler;
                ActualThemeVariantChanged -= themeChangedHandler;
                subscriptions.Dispose();
                ResetYAxisInput();
                EndMeasurementDrag();
                measurements.Dispose();
                _measurements = null;

                // 清理本视图创建的对象，也覆盖停用前已从显示集合移除的项。
                foreach (var item in _curves.Keys.ToArray())
                {
                    RemoveCurve(item);
                }
                CurvePlot.Plot.Remove(scanLine);
                _currentHoveredItem?.Classes.Remove("drag-hover");
                _currentHoveredItem = null;
            }).DisposeWith(disposables);

            foreach (var item in viewModel.DisplayCurves)
            {
                AddCurve(item);
            }
            UpdateYAxisAssignments();
            if (viewModel.CurvesLayout.IsSharedYAxisEnabled)
            {
                if (!sharedYAxisInitialized) AutoScaleSharedYAxis();
                foreach (var state in _curves.Values)
                {
                    state.LastAutoScaleVersion = state.LastVersion;
                    state.LastAutoScaleVisible = state.Signal.IsVisible;
                }
                sharedYAxisInitialized = true;
                _sharedYAxisDirty = false;
            }
            viewModel.CurvesLayout.WhenAnyValue(x => x.IsSharedYAxisEnabled)
                .Skip(1)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(_ =>
                {
                    EndYAxisDrag();
                    EndMeasurementDrag();
                    if (viewModel.CurvesLayout.IsSharedYAxisEnabled && !sharedYAxisInitialized)
                    {
                        UpdateYAxisAssignments();
                        AutoScaleSharedYAxis();
                        sharedYAxisInitialized = true;
                    }
                    RefreshPlot(resetAutoScale: true);
                }).DisposeWith(subscriptions);
            viewModel.CurvesLayout.WhenAnyValue(x => x.IsYAxisAutoScaleEnabled)
                .Skip(1)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(_ => RefreshPlot(resetAutoScale: true)).DisposeWith(subscriptions);
            viewModel.WhenAnyValue(x => x.MeasurementMode)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(mode =>
                {
                    EndYAxisDrag();
                    EndMeasurementDrag();
                    measurements.SetMode(mode);
                    CurvePlot.SetCursor(measurements.MouseCursor);
                    measurements.BringToFront();
                    CurvePlot.Refresh();
                }).DisposeWith(subscriptions);
            monitorConfig.WhenAnyValue(x => x.MaxSaveLen)
                .Skip(1)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(_ =>
                {
                    SetXAxisLimits();
                    RefreshPlot(resetAutoScale: true);
                }).DisposeWith(subscriptions);

            // UI 定时器不会积压后台 Interval 排入的刷新事件；空闲或隐藏窗口不重绘。
            // 持续检查版本，也覆盖停止监控后的最后一帧、清空历史和重新显示窗口。
            var refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(33),
            };
            refreshTimer.Tick += (_, _) =>
            {
                if (IsEffectivelyVisible) RefreshPlot(force: false);
            };
            refreshTimer.Start();
            Disposable.Create(refreshTimer.Stop).DisposeWith(subscriptions);

            ApplyPlotTheme();
            RefreshPlot();
        });
    }
    private CurveMeasurementTarget? GetMeasurementTarget(CurveMonitorLayout.CurveItem? item)
    {
        if (item == null || !_curves.TryGetValue(item, out var curve)) return null;
        string label = item.Runtime.Symbol.Name;
        if (!string.IsNullOrWhiteSpace(item.Runtime.Symbol.Unit)) label += $" [{item.Runtime.Symbol.Unit}]";
        return new(item.Runtime.PlotHistory, curve.Signal.Axes.YAxis, label, item.IsVisible);
    }

    private void CurvePlot_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerUpdateKind kind = e.GetCurrentPoint(CurvePlot).Properties.PointerUpdateKind;
        if (_isYAxisKeyPressed && kind == PointerUpdateKind.LeftButtonPressed)
        {
            // 没有可操作的选中轴时也不将 Y 手势退化为所有坐标轴的拖动。
            e.Handled = true;
            if (_measurementDragPointer != null || _yAxisDragPointer != null ||
                ViewModel?.SelectedCurveItem is not { } selected || !_curves.TryGetValue(selected, out var curve))
                return;

            var mouse = e.GetPosition(CurvePlot);
            if (!_yAxisDrag.TryBeginDrag(curve.Signal.Axes.YAxis, new((float)mouse.X, (float)mouse.Y))) return;

            _plotInputWasEnabled = CurvePlot.UserInputProcessor.IsEnabled;
            CurvePlot.UserInputProcessor.Disable();
            _yAxisDragPointer = e.Pointer;
            e.Pointer.Capture(CurvePlot);
            CurvePlot.Focus();
            CurvePlot.SetCursor(ScottPlot.Cursor.SizeNorthSouth);
            return;
        }

        if (_measurements == null || kind != PointerUpdateKind.LeftButtonPressed) return;
        var point = e.GetPosition(CurvePlot);
        if (!_measurements.TryBeginDrag(new((float)point.X, (float)point.Y))) return;

        _plotInputWasEnabled = CurvePlot.UserInputProcessor.IsEnabled;
        CurvePlot.UserInputProcessor.Disable();
        _measurementDragPointer = e.Pointer;
        e.Pointer.Capture(CurvePlot);
        CurvePlot.Focus();
        e.Handled = true;
        CurvePlot.Refresh();
    }

    private void CurvePlot_PointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(CurvePlot);
        Pixel pixel = new((float)point.X, (float)point.Y);
        if (_yAxisDragPointer != null)
        {
            if (!ReferenceEquals(e.Pointer, _yAxisDragPointer)) return;
            bool axisChanged = _yAxisDrag.Move(pixel);
            bool measurementsChanged = _measurements?.PointerMoved(pixel) == true;
            CurvePlot.SetCursor(ScottPlot.Cursor.SizeNorthSouth);
            e.Handled = true;
            if (axisChanged || measurementsChanged) CurvePlot.Refresh();
            return;
        }

        if (_measurements == null) return;
        bool changed = _measurements.PointerMoved(pixel);
        CurvePlot.SetCursor(_measurements.GetMouseCursor(pixel));
        if (_measurementDragPointer != null) e.Handled = true;
        if (changed) CurvePlot.Refresh();
    }

    private void CurvePlot_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_measurementDragPointer == null || e.InitialPressMouseButton != MouseButton.Left) return;
        EndMeasurementDrag();
        e.Handled = true;
    }

    private void CurvePlot_YAxisPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _yAxisDragPointer) || e.InitialPressMouseButton != MouseButton.Left) return;
        var point = e.GetPosition(CurvePlot);
        _yAxisDrag.Move(new((float)point.X, (float)point.Y));
        EndYAxisDrag();
        e.Handled = true;
        _measurements?.Update();
        CurvePlot.Refresh();
    }

    private void CurvePlot_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_isYAxisKeyPressed) return;

        e.Handled = true;
        if (_yAxisDragPointer != null || _measurementDragPointer != null ||
            ViewModel?.SelectedCurveItem is not { } selected || !_curves.TryGetValue(selected, out var curve))
            return;

        var point = e.GetPosition(CurvePlot);
        if (!_yAxisDrag.ZoomWheel(curve.Signal.Axes.YAxis, new((float)point.X, (float)point.Y), e.Delta.Y)) return;
        _measurements?.Update();
        CurvePlot.Refresh();
    }

    private void CurveMonitorView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Y || e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        _isYAxisKeyPressed = true;
        e.Handled = true;
    }

    private void CurveMonitorView_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Y) return;
        ResetYAxisInput();
        e.Handled = true;
    }

    private void ResetYAxisInput()
    {
        _isYAxisKeyPressed = false;
        EndYAxisDrag();
    }

    private void EndYAxisDrag()
    {
        _yAxisDrag.EndDrag();
        if (_yAxisDragPointer is not { } pointer) return;

        _yAxisDragPointer = null;
        if (ReferenceEquals(pointer.Captured, CurvePlot)) pointer.Capture(null);
        CurvePlot.UserInputProcessor.IsEnabled = _plotInputWasEnabled;
        CurvePlot.SetCursor(_measurements?.MouseCursor ?? ScottPlot.Cursor.Arrow);
    }

    private void EndMeasurementDrag()
    {
        _measurements?.EndDrag();
        if (_measurementDragPointer is { } pointer)
        {
            _measurementDragPointer = null;
            if (ReferenceEquals(pointer.Captured, CurvePlot)) pointer.Capture(null);
            CurvePlot.UserInputProcessor.IsEnabled = _plotInputWasEnabled;
        }
        CurvePlot.SetCursor(_measurements?.MouseCursor ?? ScottPlot.Cursor.Arrow);
    }

    private void UpdateYAxisAssignments()
    {
        if (ViewModel is not { } viewModel) return;
        bool shared = viewModel.CurvesLayout.IsSharedYAxisEnabled;
        CurvePlot.Plot.Axes.Left.IsVisible = shared;
        var selected = viewModel.SelectedCurveItem ?? viewModel.DisplayCurves.FirstOrDefault();
        foreach (var (item, state) in _curves)
        {
            state.Signal.Axes.YAxis = shared ? CurvePlot.Plot.Axes.Left : state.YAxis;
            state.YAxis.IsVisible = !shared && ReferenceEquals(item, selected);
        }
    }

    private void AutoScaleSharedYAxis()
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (var state in _curves.Values)
        {
            if (!state.Signal.IsVisible) continue;
            var range = state.Signal.GetAxisLimits().YRange;
            if (!double.IsFinite(range.Min) || !double.IsFinite(range.Max)) continue;
            min = Math.Min(min, range.Min);
            max = Math.Max(max, range.Max);
        }
        // 空窗口保留当前范围；测量辅助图元不参与曲线极值计算。
        if (min > max) return;
        if (!CurveAxisAutoScaler.TryAutoScaleY(CurvePlot.Plot.Axes.Left,
            new CoordinateRange(min, max), CurvePlot.Plot.Axes.AutoScaler))
            CurvePlot.Plot.Axes.AutoScaleY(CurvePlot.Plot.Axes.Left);
    }

    private bool UpdateCurveRenderRanges()
    {
        bool changed = false;
        foreach (var (item, curve) in _curves)
        {
            var state = item.Runtime.PlotHistory.GetState();
            bool visible = item.IsVisible && state.Count > 0;
            changed |= curve.LastVisible != visible || (visible && curve.LastVersion != state.Version);
            curve.LastVisible = visible;
            curve.LastVersion = state.Version;
            curve.Signal.MaxRenderIndex = Math.Max(0, state.Count - 1);
            curve.Signal.IsVisible = visible;
        }
        return changed;
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
        if (_curves.ContainsKey(item)) return;

        bool isFirst = _curves.Count == 0;
        // 首次添加时自动配色，重新激活或重建视图时复用曲线项的颜色。
        ScottPlot.Color? color = item.HasColor
            ? new ScottPlot.Color(item.Color.R, item.Color.G, item.Color.B, item.Color.A)
            : null;
        item.Signal = new CurvePlotSignal(item.Runtime.PlotHistory)
        {
            Color = color ?? CurvePlot.Plot.Add.GetNextColor(),
        };
        CurvePlot.Plot.Add.Plottable(item.Signal);
        if (!item.HasColor)
        {
            item.Color = Avalonia.Media.Color.FromUInt32(item.Signal.Color.ARGB);
        }
        item.YAxis = CurvePlot.Plot.Axes.AddLeftAxis();
        item.YAxis.TickGenerator = new CurveAxisTicks(item.YAxis, item.YAxis.TickGenerator);
        item.YAxis.IsVisible = isFirst;
        item.Signal.Axes.XAxis = CurvePlot.Plot.Axes.Bottom;
        item.Signal.Axes.YAxis = item.YAxis;
        var renderState = new CurveRenderState(item.Signal, item.YAxis);
        if (item.YAxisRange is { IsValid: true } range)
        {
            item.YAxis.Range.Set(range.Min, range.Max);
            renderState.LastAutoScaleVersion = item.Runtime.PlotHistory.Version;
        }
        _curves.Add(item, renderState);
        _sharedYAxisDirty = true;
        UpdateYAxisAssignments();
        UpdateCurveRenderRanges();
        ApplyPlotTheme();
    }
    private void RemoveCurve(CurveMonitorLayout.CurveItem item)
    {
        if (!_curves.Remove(item, out var curve)) return;
        _sharedYAxisDirty = true;

        item.YAxisRange = CurveAxisRange.Create(curve.YAxis.Min, curve.YAxis.Max);
        if (ReferenceEquals(_yAxisDrag.Axis, curve.YAxis)) EndYAxisDrag();
        // 与 ScottPlot 的渲染锁同步，确保原生路径不会在绘图中途被释放。
        lock (CurvePlot.Plot.Sync)
        {
            CurvePlot.Plot.Remove(curve.Signal);
            if (curve.Signal is IDisposable disposable) disposable.Dispose();
            CurvePlot.Plot.Remove(curve.YAxis);
        }
        if (ReferenceEquals(item.Signal, curve.Signal)) item.Signal = null;
        if (ReferenceEquals(item.YAxis, curve.YAxis)) item.YAxis = null;
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
