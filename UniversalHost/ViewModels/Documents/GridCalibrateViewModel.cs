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
using UniversalHost.Services.Communication;
using UniversalHost.ViewModels.Views;

namespace UniversalHost.ViewModels.Documents;

public record struct GridCalibrateLayoutSave(
    Guid[] DisplaySymbols,
    bool IsNameVisiable,
    bool IsAliasVisiable,
    bool IsValueVisiable,
    bool IsUnitVisiable);
public partial class GridCalibrateViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = [];

    private readonly SourceList<Guid> _keysSourceList = new();
    public ISourceList<Guid> KeysSource => _keysSourceList;
    public GridCalibrateLayoutSave Save => new(_keysSourceList.Items.ToArray(),
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

    public GridCalibrateViewModel(string id) : this(id, new GridCalibrateLayoutSave([], true, true, true, true)) { }

    public GridCalibrateViewModel(string id, GridCalibrateLayoutSave save)
    {
        Id = id;

        _isNameColumnVisible = save.IsNameVisiable;
        _isAliasColumnVisible = save.IsAliasVisiable;
        _isValueColumnVisible = save.IsValueVisiable;
        _isUnitColumnVisible = save.IsUnitVisiable;

        //只添加列表中有的变量
        _keysSourceList.AddRange(
             save.DisplaySymbols.Where(id =>
                 SymbolRuntimeService.CalibrateSymbolRuntimesSource.Lookup(id).HasValue));
        //源移除变量时移除此窗口的相应变量
        SymbolRuntimeService.CalibrateSymbolRuntimesSource.Connect()
                .OnItemRemoved(removedRuntime =>
                {
                    _keysSourceList.Remove(removedRuntime.Symbol.Id);
                })
                .Subscribe()
                .DisposeWith(_disposables);

        _keysSourceList.Connect()
                .Transform(key =>
                    SymbolRuntimeService.CalibrateSymbolRuntimesSource
                        .Lookup(key)
                        .Value)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Bind(out _displaySymbolRuntimes)
                .Subscribe()
                .DisposeWith(_disposables);
    }
    public bool Contains(Guid id)
    {
        return KeysSource.Items.Contains(id);
    }
    public void AddOrRemoveSymbol(Guid symbolId)
    {
        if (KeysSource.Items.Contains(symbolId))
        {
            KeysSource.Remove(symbolId);
        }
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
    [ReactiveCommand]
    private async Task DownloadValues()
    {
        var unknownItems = DisplaySymbolRuntimes.Where(item => item.Symbol.DataType == SymbolDataType.Unknown).ToList();

        if (unknownItems.Count > 0)
        {
            var errorDetails = string.Join(", ", unknownItems.Select(i => $"{i.Symbol.Name}({i.Symbol.Alias})"));
            NotificationService.Show("标定错误", $"变量 {errorDetails} 类型为 Unkown", NotificationType.Warning);
            return;
        }
        try
        {
            int cnt = 0;
            foreach (var item in DisplaySymbolRuntimes)
            {
                if (await XcpService.Client!.DownloadSymbol(item))
                {
                    cnt++;
                }
            }
            NotificationService.Show("标定成功", $"共下载 {cnt} 个变量\n{DisplaySymbolRuntimes.Count - cnt} 个变量值未改变", NotificationType.Success);
        }
        catch (Exception ex)
        {
            NotificationService.Show("标定错误", ex.Message, NotificationType.Error);
        }
    }
    [ReactiveCommand]
    private async Task UploadValues()
    {
        var unknownItems = DisplaySymbolRuntimes.Where(item => item.Symbol.DataType == SymbolDataType.Unknown).ToList();

        if (unknownItems.Count > 0)
        {
            var errorDetails = string.Join(", ", unknownItems.Select(i => $"{i.Symbol.Name}({i.Symbol.Alias})"));
            NotificationService.Show("上传标定变量错误", $"变量 {errorDetails} 类型为 Unkown", NotificationType.Warning);
            return;
        }
        try
        {
            foreach (var item in DisplaySymbolRuntimes)
            {
                await XcpService.Client!.UploadSymbol(item);
                NotificationService.Show("此窗口变量已上传", $"共上传 {DisplaySymbolRuntimes.Count} 个变量", NotificationType.Success);
            }
        }
        catch (Exception ex)
        {
            NotificationService.Show("上传标定变量错误", ex.Message, NotificationType.Error);
        }
    }
    [ReactiveCommand]
    public async Task DownloadSelectedSymbolValue()
    {
        if (SelectedSymbolRuntime == null || GlobalStatus.Instance.IsConnected == false)
        {
            return;
        }
        if (SelectedSymbolRuntime.Symbol.DataType == SymbolDataType.Unknown)
        {
            NotificationService.Show("标定错误", $"{SelectedSymbolRuntime.Symbol.Name}({SelectedSymbolRuntime.Symbol.Alias}) 类型为 Unknown", NotificationType.Warning);
            return;
        }
        try
        {
            if (await XcpService.Client!.DownloadSymbol(SelectedSymbolRuntime))
            {
                NotificationService.Show("标定成功", $"{SelectedSymbolRuntime.Symbol.Name} ({SelectedSymbolRuntime.Symbol.Alias}) : {SelectedSymbolRuntime.ValueString}\n已成功下载至设备", NotificationType.Success);
            }
            else
            {
                NotificationService.Show("未标定", $"{SelectedSymbolRuntime.Symbol.Name} ({SelectedSymbolRuntime.Symbol.Alias})\n变量值未改变", NotificationType.Success);
            }
        }
        catch (Exception ex)
        {
            NotificationService.Show("标定错误", ex.Message, NotificationType.Error);
        }
    }
    [ReactiveCommand]
    private async Task UploadSelectedSymbolValue()
    {
        if (SelectedSymbolRuntime == null || GlobalStatus.Instance.IsConnected == false)
        {
            return;
        }
        if (SelectedSymbolRuntime.Symbol.DataType == SymbolDataType.Unknown)
        {
            NotificationService.Show("上传标定变量错误", $"{SelectedSymbolRuntime.Symbol.Name}({SelectedSymbolRuntime.Symbol.Alias}) 类型为 Unknown", NotificationType.Warning);
            return;
        }
        try
        {
            await XcpService.Client!.UploadSymbol(SelectedSymbolRuntime);
            NotificationService.Show("变量已上传", $"{SelectedSymbolRuntime.Symbol.Name}({SelectedSymbolRuntime.Symbol.Alias})", NotificationType.Success);
        }
        catch (Exception ex)
        {
            NotificationService.Show("上传标定变量错误", ex.Message, NotificationType.Error);
        }
    }
    public void Dispose()
    {
        _keysSourceList.Dispose();
        _disposables.Dispose();
    }
}

