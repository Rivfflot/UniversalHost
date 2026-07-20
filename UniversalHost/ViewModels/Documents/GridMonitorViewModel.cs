using Avalonia.Input.Platform;
using DynamicData;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.ViewModels.Views;

namespace UniversalHost.ViewModels.Documents;

public record struct GridMonitorLayoutSave(
    Guid[] DisplaySymbols,
    bool IsNameVisiable,
    bool IsAliasVisiable,
    bool IsValueVisiable,
    bool IsUnitVisiable);

public partial class GridMonitorViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = [];

    private readonly SourceList<Guid> _keysSourceList = new();
    public ISourceList<Guid> KeysSource => _keysSourceList;
    //用于布局保存与恢复
    public GridMonitorLayoutSave Save => new(_keysSourceList.Items.ToArray(),
                     IsNameColumnVisible, IsAliasColumnVisible, IsValueColumnVisible, IsUnitColumnVisible);
    private readonly ReadOnlyObservableCollection<SymbolRuntime> _displaySymbolRuntimes;
    // 绑定到 DataGrid 的数据源
    public ReadOnlyObservableCollection<SymbolRuntime> DisplaySymbolRuntimes => _displaySymbolRuntimes;

    [Reactive] private SymbolRuntime? _selectedSymbolRuntime; // DataGrid 选中的行
    [Reactive] private bool _isNameColumnVisible;
    [Reactive] private bool _isAliasColumnVisible;
    [Reactive] private bool _isValueColumnVisible;
    [Reactive] private bool _isUnitColumnVisible;

    // 保存和恢复布局使用 Document Id
    public readonly string Id;

    // 默认构造函数（无参数新建网格时使用）

    public GridMonitorViewModel(string id) : this(id, new GridMonitorLayoutSave([], true, true, true, true)) { }

    // 恢复布局时调用的构造函数
    public GridMonitorViewModel(string id, GridMonitorLayoutSave save)
    {
        Id = id;


        _isNameColumnVisible = save.IsNameVisiable;
        _isAliasColumnVisible = save.IsAliasVisiable;
        _isValueColumnVisible = save.IsValueVisiable;
        _isUnitColumnVisible = save.IsUnitVisiable;

        //只添加列表中有的变量
        _keysSourceList.AddRange(
             save.DisplaySymbols.Where(id =>
                 SymbolRuntimeService.MonitorSymbolRuntimesSource.Lookup(id).HasValue));
        //源移除变量时移除此窗口的相应变量
        SymbolRuntimeService.MonitorSymbolRuntimesSource.Connect()
                .OnItemRemoved(removedRuntime =>
                {
                    _keysSourceList.Remove(removedRuntime.Symbol.Id);
                })
                .Subscribe()
                .DisposeWith(_disposables);

        _keysSourceList.Connect()
                .Transform(key =>
                    SymbolRuntimeService.MonitorSymbolRuntimesSource
                        .Lookup(key)
                        .Value)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Bind(out _displaySymbolRuntimes)
                .Subscribe()
                .DisposeWith(_disposables);
    }
    public bool Contains(Guid symbolId)
    {
        return KeysSource.Items.Contains(symbolId);
    }
    public void AddOrRemoveSymbol(Guid symbolId)
    {
        // 1. 如果已经存在，说明用户是在“取消勾选”，应当将其移除
        if (KeysSource.Items.Contains(symbolId))
        {
            KeysSource.Remove(symbolId);
        }
        // 2. 如果不存在，说明用户是在“勾选”，应当将其添加
        else
        {
            KeysSource.Add(symbolId);
        }
    }
    /// <summary>
    /// 在DataType改变时调用
    /// </summary>
    public void RefreshDisplay()
    {
        _keysSourceList.Edit(list =>
        {
            var ids = list.ToArray();
            list.Clear();
            list.AddRange(ids);
        });
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
        if (_selectedSymbolRuntime == null) return;

        _keysSourceList.Remove(_selectedSymbolRuntime.Symbol.Id);
        _selectedSymbolRuntime = null;
    }

    [ReactiveCommand]
    void ClearSelectedSymbols()
    {
        _keysSourceList.Clear();
    }
    [ReactiveCommand]
    private async Task CopySymbolNameAsync(Avalonia.Controls.TopLevel topLevel)
    {
        if (topLevel?.Clipboard is { } clipboard && _selectedSymbolRuntime != null)
        {
            await clipboard.SetTextAsync(_selectedSymbolRuntime.Symbol.Name);
        }
    }
    [ReactiveCommand]
    private async Task CopySymbolValueAsync(Avalonia.Controls.TopLevel topLevel)
    {
        if (topLevel?.Clipboard is { } clipboard && _selectedSymbolRuntime != null)
        {
            await clipboard.SetTextAsync(_selectedSymbolRuntime.ValueString);
        }
    }

    public void Dispose()
    {
        _keysSourceList.Dispose();
        _disposables.Dispose();
    }
}