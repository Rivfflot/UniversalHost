using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using DynamicData;
using ReactiveUI.Reactive;
using ReactiveUI.Primitives.Reactive.Concurrency;
using ReactiveUI.SourceGenerators;
using ScottPlot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.ViewModels.Windows;

namespace UniversalHost.ViewModels.Documents;

public partial class CurveMonitorLayout : ReactiveObject
{
    //曲线样式保存
    public class CurveItemStorage
    {
        public Guid Id { get; set; }
        public bool IsVisible { get; set; }
    }
    //曲线绑定项
    public partial class CurveItem : ReactiveObject
    {
        public Guid Id => Runtime.Symbol.Id;
        public SymbolRuntime Runtime { get; }
        public ScottPlot.Plottables.Signal? Signal;
        public ScottPlot.AxisPanels.LeftAxis? YAxis;
        #region Curve Sytle
        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                this.RaiseAndSetIfChanged(ref _isVisible, value);
                Signal?.IsVisible = value && Runtime.PlotHistory.Count > 0;
            }
        }
        private Avalonia.Media.Color _color;
        // 颜色属于曲线项，不能随视图停用时的绘图对象一起丢失。
        internal bool HasColor { get; private set; }
        public Avalonia.Media.Color Color
        {
            get => _color;
            set
            {
                HasColor = true;
                this.RaiseAndSetIfChanged(ref _color, value);
                Brush = Avalonia.Media.Brush.Parse(Color.ToString());
                this.RaisePropertyChanged(nameof(Brush));
            }
        }
        [Reactive] private Avalonia.Media.IBrush _brush = Avalonia.Media.Brushes.Transparent;
        #endregion
        public CurveItem(SymbolRuntime runtime)
        {
            Runtime = runtime;
        }
        public void ApplyStorage(CurveItemStorage storage)
        {
            IsVisible = storage.IsVisible;
        }
    }
    [JsonIgnore] private readonly SourceList<CurveItem> _curvesSource = new();
    [JsonIgnore] public ISourceList<CurveItem> CurvesSource => _curvesSource;
    private List<CurveItemStorage>? _pendingCurves;

    // 保留已有工程文件使用的字段名。
    [JsonPropertyName("BitsMonitorSymbols")]
    public List<CurveItemStorage> CurvesSourceStorage
    {
        get => _pendingCurves ?? CurvesSource.Items.Select(
                            x => new CurveItemStorage
                            {
                                Id = x.Id,
                                IsVisible = x.IsVisible,
                            }).ToList(); // 保存时：从 SourceList 转换到 List
        // 反序列化只保存 Id 和样式，不能查询上一工程或尚未建立的运行时。
        set => _pendingCurves = value ?? [];
    }

    internal void RestoreRuntimes(IObservableCache<SymbolRuntime, Guid> runtimes)
    {
        if (_pendingCurves == null) return;

        _curvesSource.Edit(list =>
        {
            list.Clear();
            foreach (var storage in _pendingCurves)
            {
                var lookup = runtimes.Lookup(storage.Id);
                if (!lookup.HasValue) continue;

                var item = new CurveItem(lookup.Value);
                item.ApplyStorage(storage);
                list.Add(item);
            }
        });
        _pendingCurves = null;
    }
    //页面布局保存项
    [JsonIgnore][Reactive] private GridLength _leftPanelLength = new(150);
    [JsonPropertyName("LeftPanelLength")]
    public double LeftPanelLengthStorage
    {
        get => _leftPanelLength.Value;
        set
        {
            _leftPanelLength = new(value);
        }
    }
    [Reactive] private bool _isNameVisible = true;
    [Reactive] private bool _isAliasVisible = true;
    [Reactive] private bool _isValueVisible = true;
    [Reactive] private bool _isUnitVisible = true;
    [Reactive] private bool _isYAxisAutoScaleEnabled = true;
};
public partial class CurveMonitorViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = [];
    // 保存和恢复布局使用 Document Id
    public readonly string Id;
    public CurveMonitorLayout CurvesLayout { get; init; }
    public Action<CurveMonitorLayout.CurveItem>? RemoveCurve;
    public Action<CurveMonitorLayout.CurveItem>? AddCurve;
    public Action? RefreshCurve;
    [Reactive] private CurveMonitorLayout.CurveItem? _selectedCurveItem;
    // 测量模式和读数属于窗口运行时状态，不写入工程布局。
    private CurveMeasurementMode _measurementMode;
    public CurveMeasurementMode MeasurementMode
    {
        get => _measurementMode;
        private set
        {
            if (_measurementMode == value) return;
            this.RaiseAndSetIfChanged(ref _measurementMode, value);
            this.RaisePropertyChanged(nameof(IsMouseCoordinatesEnabled));
            this.RaisePropertyChanged(nameof(IsNearestPointEnabled));
            this.RaisePropertyChanged(nameof(IsVerticalCursorsEnabled));
            this.RaisePropertyChanged(nameof(IsHorizontalCursorsEnabled));
            this.RaisePropertyChanged(nameof(IsMeasurementReadoutVisible));
        }
    }
    public bool IsMouseCoordinatesEnabled
    {
        get => MeasurementMode == CurveMeasurementMode.MouseCoordinates;
        set => SetMeasurementMode(CurveMeasurementMode.MouseCoordinates, value);
    }
    public bool IsNearestPointEnabled
    {
        get => MeasurementMode == CurveMeasurementMode.NearestPoint;
        set => SetMeasurementMode(CurveMeasurementMode.NearestPoint, value);
    }
    public bool IsVerticalCursorsEnabled
    {
        get => MeasurementMode == CurveMeasurementMode.VerticalCursors;
        set => SetMeasurementMode(CurveMeasurementMode.VerticalCursors, value);
    }
    public bool IsHorizontalCursorsEnabled
    {
        get => MeasurementMode == CurveMeasurementMode.HorizontalCursors;
        set => SetMeasurementMode(CurveMeasurementMode.HorizontalCursors, value);
    }
    public bool IsMeasurementReadoutVisible => MeasurementMode != CurveMeasurementMode.None;
    [Reactive] private CurveMeasurementReadout _measurementReadout = CurveMeasurementReadout.Empty;

    private void SetMeasurementMode(CurveMeasurementMode mode, bool enabled)
    {
        if (enabled) MeasurementMode = mode;
        else if (MeasurementMode == mode) MeasurementMode = CurveMeasurementMode.None;
    }
    // 数据源
    private readonly ReadOnlyObservableCollection<CurveMonitorLayout.CurveItem> _displayCurves;
    public ReadOnlyObservableCollection<CurveMonitorLayout.CurveItem> DisplayCurves => _displayCurves;
    public CurveMonitorViewModel(string id) : this(id, new CurveMonitorLayout()) { }
    public CurveMonitorViewModel(string id, CurveMonitorLayout layout)
    {
        Id = id;
        CurvesLayout = layout;
        CurvesLayout.RestoreRuntimes(SymbolRuntimeService.MonitorSymbolRuntimesSource);

        //源移除变量时移除此窗口的相应变量
        SymbolRuntimeService.MonitorSymbolRuntimesSource.Connect()
                    .OnItemRemoved(removed =>
                    {
                        var toRemove = CurvesLayout.CurvesSource.Items
                            .FirstOrDefault(x => x.Id == removed.Symbol.Id);

                        if (toRemove != null)
                        {
                            bool wasSelected = ReferenceEquals(SelectedCurveItem, toRemove);
                            RemoveCurve?.Invoke(toRemove);
                            CurvesLayout.CurvesSource.Remove(toRemove);
                            if (wasSelected) SelectedCurveItem = null;
                        }
                    })
                    .Subscribe()
                    .DisposeWith(_disposables);

        SymbolRuntimeService.MonitorSymbolRuntimesSource.Connect()
                    .OnItemUpdated((current, _) =>
                    {
                        var oldItem = CurvesLayout.CurvesSource.Items
                                    .FirstOrDefault(x => x.Id == current.Symbol.Id);

                        if (oldItem != null)
                        {
                            bool wasSelected = ReferenceEquals(SelectedCurveItem, oldItem);
                            var newItem = new CurveMonitorLayout.CurveItem(current)
                            {
                                IsVisible = oldItem.IsVisible,
                            };
                            if (oldItem.HasColor) newItem.Color = oldItem.Color;
                            CurvesLayout.CurvesSource.Replace(oldItem, newItem);
                            RemoveCurve?.Invoke(oldItem);
                            AddCurve?.Invoke(newItem);
                            if (wasSelected)
                            {
                                // 等待 DisplayCurves 绑定完成，避免 ListBox 因新项尚未出现而清空选择。
                                AvaloniaScheduler.Instance.Schedule(() =>
                                {
                                    if (SelectedCurveItem != null && !ReferenceEquals(SelectedCurveItem, oldItem)) return;
                                    SelectedCurveItem = CurvesLayout.CurvesSource.Items.FirstOrDefault(x => x.Id == oldItem.Id);
                                }).DisposeWith(_disposables);
                            }
                        }
                    })
                    .Subscribe()
                    .DisposeWith(_disposables);

        CurvesLayout.CurvesSource.Connect()
                .ObserveOn(AvaloniaScheduler.Instance)
                .Bind(out _displayCurves)
                .Subscribe()
                .DisposeWith(_disposables);

        // IsVisible 改变时刷新Plot
        CurvesLayout.CurvesSource.Connect()
             .ObserveOn(AvaloniaScheduler.Instance)
             .AutoRefresh(x => x.IsVisible)
             .Subscribe((_) => { RefreshCurve?.Invoke(); }).DisposeWith(_disposables);

        //只显示选中变量的Y轴
        this.WhenAnyValue(x => x.SelectedCurveItem).Subscribe(s =>
        {
            foreach (var item in DisplayCurves)
            {
                item.YAxis?.IsVisible = false;
            }
            s?.YAxis?.IsVisible = true;
            RefreshCurve?.Invoke();
        }).DisposeWith(_disposables);

    }
    public bool Contains(Guid symbolId)
    {
        return CurvesLayout.CurvesSource.Items.Any(x => x.Id == symbolId);
    }
    public void AddOrRemoveSymbol(Guid symbolId)
    {
        var item = CurvesLayout.CurvesSource.Items.FirstOrDefault(x => x.Id == symbolId);
        // 如果存在，其移除
        if (item != null)
        {
            bool wasSelected = ReferenceEquals(SelectedCurveItem, item);
            RemoveCurve?.Invoke(item);
            CurvesLayout.CurvesSource.Remove(item);
            if (wasSelected) SelectedCurveItem = null;
        }
        // 如果不存在，添加
        else
        {
            var newItem = new CurveMonitorLayout.CurveItem(SymbolRuntimeService.MonitorSymbolRuntimesSource.KeyValues[symbolId]);
            CurvesLayout.CurvesSource.Add(newItem);
            AddCurve?.Invoke(newItem);
        }
    }
    [ReactiveCommand]
    private void ToggleYAxisAutoScale()
    {
        CurvesLayout.IsYAxisAutoScaleEnabled = !CurvesLayout.IsYAxisAutoScaleEnabled;
    }
    [ReactiveCommand]
    private void ToggleCurveItemVisiable()
    {
        if (_selectedCurveItem == null)
        {
            return;
        }
        _selectedCurveItem.IsVisible = !_selectedCurveItem.IsVisible;
    }
    /// <summary>
    /// 手动添加变量的按钮命令
    /// </summary>
    [ReactiveCommand]
    private async Task OpenAddDisplaySymbolWindow()
    {
        SelectWindowViewModel.Instance.UpdateDocumentId(Id);
        UniversalHost.Views.Windows.SelectSymbolWindow.Window.Show();
        UniversalHost.Views.Windows.SelectSymbolWindow.Window.Activate();
    }
    /// <summary>
    /// 手动删除选中变量的按钮命令
    /// </summary>
    [ReactiveCommand]
    private void RemoveSelectedSymbol()
    {
        if (_selectedCurveItem == null) return;
        RemoveCurve?.Invoke(_selectedCurveItem);
        CurvesLayout.CurvesSource.Remove(_selectedCurveItem);

        SelectedCurveItem = null;
    }
    [ReactiveCommand]
    void ClearSelectedSymbols()
    {
        foreach (var item in DisplayCurves)
        {
            RemoveCurve?.Invoke(item);
        }
        CurvesLayout.CurvesSource.Clear();
        SelectedCurveItem = null;
    }
    [ReactiveCommand]
    private async Task CopySymbolNameAsync(Avalonia.Controls.TopLevel topLevel)
    {
        if (topLevel?.Clipboard is { } clipboard && _selectedCurveItem != null)
        {
            await clipboard.SetTextAsync(_selectedCurveItem.Runtime.Symbol.Name);
        }
    }
    [ReactiveCommand]
    private async Task CopySymbolValueAsync(Avalonia.Controls.TopLevel topLevel)
    {
        if (topLevel?.Clipboard is { } clipboard && _selectedCurveItem != null)
        {
            await clipboard.SetTextAsync(_selectedCurveItem.Runtime.ValueString);
        }
    }
    public void Dispose()
    {
        CurvesLayout.CurvesSource.Dispose();
        _disposables.Dispose();
    }
}
