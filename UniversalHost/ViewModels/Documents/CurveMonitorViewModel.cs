using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using DynamicData;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.SourceGenerators;
using ScottPlot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.ViewModels.Views;

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
        //public ScottPlot.Plottables.VerticalLine? ScanLine;
        public ScottPlot.AxisPanels.LeftAxis? YAxis;
        #region Curve Sytle
        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                this.RaiseAndSetIfChanged(ref _isVisible, value);
                Signal?.IsVisible = value;
                //ScanLine?.IsVisible = value;
            }
        }
        private Avalonia.Media.Color _color;
        public Avalonia.Media.Color Color
        {
            get => _color;
            set
            {
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

    [JsonPropertyName("BitsMonitorSymbols")]
    public List<CurveItemStorage> CurvesSourceStorage
    {
        get => CurvesSource.Items.Select(
                            x => new CurveItemStorage
                            {
                                Id = x.Id,
                                IsVisible = x.IsVisible,
                            }).ToList(); // 保存时：从 SourceList 转换到 List
        set
        {
            _curvesSource.Edit(list =>
            {
                list.Clear();
                list.AddRange(value
                            .Select(storage =>
                            {
                                var lookup = SymbolRuntimeService
                                    .MonitorSymbolRuntimesSource
                                    .Lookup(storage.Id);

                                if (!lookup.HasValue)
                                    return null;

                                var item = new CurveItem(lookup.Value);

                                item.ApplyStorage(storage);

                                return item;
                            })
                            .Where(x => x != null)!);
            });
        }
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
    // 数据源
    private readonly ReadOnlyObservableCollection<CurveMonitorLayout.CurveItem> _displayCurves;
    public ReadOnlyObservableCollection<CurveMonitorLayout.CurveItem> DisplayCurves => _displayCurves;
    public CurveMonitorViewModel(string id) : this(id, new CurveMonitorLayout()) { }
    public CurveMonitorViewModel(string id, CurveMonitorLayout layout)
    {
        Id = id;
        CurvesLayout = layout;

        //源移除变量时移除此窗口的相应变量
        SymbolRuntimeService.MonitorSymbolRuntimesSource.Connect()
                    .OnItemRemoved(removed =>
                    {
                        var toRemove = CurvesLayout.CurvesSource.Items
                            .FirstOrDefault(x => x.Id == removed.Symbol.Id);

                        if (toRemove != null)
                        {
                            RemoveCurve?.Invoke(toRemove);
                            CurvesLayout.CurvesSource.Remove(toRemove);
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
                            var newItem = new CurveMonitorLayout.CurveItem(current)
                            {
                                IsVisible = oldItem.IsVisible,
                            };
                            CurvesLayout.CurvesSource.Replace(oldItem, newItem);
                            RemoveCurve?.Invoke(oldItem);
                            AddCurve?.Invoke(newItem);
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
            RemoveCurve?.Invoke(item);
            CurvesLayout.CurvesSource.Remove(item);
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
        UniversalHost.Views.Views.SelectSymbolWindow.Window.Show();
        UniversalHost.Views.Views.SelectSymbolWindow.Window.Activate();
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

        _selectedCurveItem = null;
    }
    [ReactiveCommand]
    void ClearSelectedSymbols()
    {
        foreach (var item in DisplayCurves)
        {
            RemoveCurve?.Invoke(item);
        }
        CurvesLayout.CurvesSource.Clear();
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
