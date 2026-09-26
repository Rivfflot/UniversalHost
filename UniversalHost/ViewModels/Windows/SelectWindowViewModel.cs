using Avalonia.Data.Converters;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI.Reactive;
using ReactiveUI.Primitives.Reactive.Concurrency;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using UniversalHost.Models;
using UniversalHost.Services;

namespace UniversalHost.ViewModels.Windows;

public class SelectedConverter : IMultiValueConverter
{
    public object? Convert(
        IList<object?> values,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        if (values[0] is UserSymbolInfo symbol)
        {
            return SelectWindowViewModel.Instance.IsSymbolSelectedInCurrentDocument(symbol);
        }
        else if (values[0] is UserCommand command)
        {
            return SelectWindowViewModel.Instance.IsUserCommandSelectedInCurrentDocument(command);
        }

        return false;
    }
}
public partial class SelectWindowViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = [];
    private enum DocumentType
    {
        GridMonitor,
        CurveMonitor,
        BitsMonitor,
        GridCalibrate,
        UserCommand,
    }
    public static SelectWindowViewModel Instance { get; private set; } = new SelectWindowViewModel();

    [Reactive] private int _selectionRefreshVersion = 0;
    [Reactive] private bool _isMonitorMode = true;
    [Reactive] private bool _isCalibrateMode = false;
    [Reactive] private bool _isUserCmdMode = false;
    private string _documentId = "";
    private DocumentType _documentType = DocumentType.GridMonitor;
    // 监控变量表
    [Reactive] private string _monitorSymbolSearchText = string.Empty;
    private readonly ReadOnlyObservableCollection<UserSymbolInfo> _filteredMonitorSymbols;
    public ReadOnlyObservableCollection<UserSymbolInfo> FilteredMonitorSymbols => _filteredMonitorSymbols;
    // 标定变量表
    [Reactive] private string _calibrateSymbolSearchText = string.Empty;
    private readonly ReadOnlyObservableCollection<UserSymbolInfo> _filteredCalibrateSymbols;
    public ReadOnlyObservableCollection<UserSymbolInfo> FilteredCalibrateSymbols => _filteredCalibrateSymbols;
    //用户指令列表
    [Reactive] private string _userCommandSearchText = string.Empty;
    private readonly ReadOnlyObservableCollection<UserCommand> _filteredUserCommands;
    public ReadOnlyObservableCollection<UserCommand> FilteredUserCommands => _filteredUserCommands;
    public SelectWindowViewModel()
    {
        // 工程切换后必须重新选择目标文档，即使两份工程包含相同的文档 Id。
        ProjectSaveService.Instance.WhenAnyValue(x => x.Settings)
            .Subscribe(_ =>
            {
                _documentId = string.Empty;
                SelectionRefreshVersion++;
            })
            .DisposeWith(_disposables);

        var monitorSymbolFilter = this.WhenValueChanged(x => x.MonitorSymbolSearchText)
                                        .Throttle(TimeSpan.FromMilliseconds(100))
                                        .Select(UserSymbolInfo.CreateUserSymbolInfoSearchFilter);

        ProjectSaveService.Instance.WhenAnyValue(x => x.Settings.MonitorConfig.MonitoredSymbols)
                                .Select(source => source.Connect())
                                .Switch()
                                .AutoRefresh(symbol => symbol.IsMonitored, changeSetBuffer: TimeSpan.FromMilliseconds(100))
                                .AutoRefresh(symbol => symbol.Alias, changeSetBuffer: TimeSpan.FromSeconds(1))
                                .AutoRefresh(symbol => symbol.Description, changeSetBuffer: TimeSpan.FromSeconds(1))
                                .Filter(symbol => symbol.IsMonitored)
                                .Filter(monitorSymbolFilter)
                                .ObserveOn(AvaloniaScheduler.Instance)
                                .Bind(out _filteredMonitorSymbols)
                                .Subscribe(_ => { SelectionRefreshVersion++; })
                                .DisposeWith(_disposables);

        var calibrateSymbolFilter = this.WhenValueChanged(x => x.CalibrateSymbolSearchText)
                                        .Throttle(TimeSpan.FromMilliseconds(100))
                                        .Select(UserSymbolInfo.CreateUserSymbolInfoSearchFilter);

        ProjectSaveService.Instance.WhenAnyValue(x => x.Settings.CalibrateConfig.CalibratedSymbols)
                               .Select(source => source.Connect())
                               .Switch()
                               .AutoRefresh(symbol => symbol.Alias, changeSetBuffer: TimeSpan.FromSeconds(1))
                               .AutoRefresh(symbol => symbol.Description, changeSetBuffer: TimeSpan.FromSeconds(1))
                               .Filter(calibrateSymbolFilter)
                               .ObserveOn(AvaloniaScheduler.Instance)
                               .Bind(out _filteredCalibrateSymbols)
                               .Subscribe()
                               .DisposeWith(_disposables);

        var userCommandFilter = this.WhenValueChanged(x => x.UserCommandSearchText)
                                        .Throttle(TimeSpan.FromMilliseconds(100))
                                        .Select(UserCommand.CreateUserSymbolInfoSearchFilter);
        _filteredUserCommands = new ReadOnlyObservableCollection<UserCommand>([]);
        ProjectSaveService.Instance.WhenAnyValue(x => x.Settings.UserCommandConfig.UserCommands)
                           .Select(source => source.Connect())
                           .Switch()
                           .AutoRefresh(c => c.Name, changeSetBuffer: TimeSpan.FromSeconds(1))
                           .AutoRefresh(c => c.Description, changeSetBuffer: TimeSpan.FromSeconds(1))
                           .Filter(userCommandFilter)
                           .ObserveOn(AvaloniaScheduler.Instance)
                           .Bind(out _filteredUserCommands)
                           .Subscribe()
                           .DisposeWith(_disposables);
    }

    public void UpdateDocumentId(string id)
    {
        _documentId = id;

        if (DockableRegistry.GridMonitorDocuments.ContainsKey(id))
        {
            IsMonitorMode = true;
            IsCalibrateMode = false;
            IsUserCmdMode = false;
            _documentType = DocumentType.GridMonitor;
        }
        else if (DockableRegistry.CurveMonitorDocuments.ContainsKey(id))
        {
            IsMonitorMode = true;
            IsCalibrateMode = false;
            IsUserCmdMode = false;
            _documentType = DocumentType.CurveMonitor;
        }
        else if (DockableRegistry.BitsMonitorDocuments.ContainsKey(id))
        {
            IsMonitorMode = true;
            IsCalibrateMode = false;
            IsUserCmdMode = false;
            _documentType = DocumentType.BitsMonitor;
        }
        else if (DockableRegistry.GridCalibrateDocuments.ContainsKey(id))
        {
            IsMonitorMode = false;
            IsCalibrateMode = true;
            IsUserCmdMode = false;
            _documentType = DocumentType.GridCalibrate;
        }
        else if (DockableRegistry.UserCommandDocuments.ContainsKey(id))
        {
            IsMonitorMode = false;
            IsCalibrateMode = false;
            IsUserCmdMode = true;
            _documentType = DocumentType.UserCommand;
        }
        else
        {
            Debug.WriteLine($"ERROR: 找不到Dock {id}");
        }
        SelectionRefreshVersion++;
    }
    public bool IsUserCommandSelectedInCurrentDocument(UserCommand command)
    {
        var key = command.Id;
        if (DockableRegistry.UserCommandDocuments.TryGetValue(_documentId, out var vm))
        {
            return vm.Contains(key);
        }
        return false;
    }
    /// <summary>
    /// 核心：检查某个变量是否已被当前的目标网格添加（用于多窗口状态隔离）
    /// </summary>
    public bool IsSymbolSelectedInCurrentDocument(UserSymbolInfo userSymbolInfo)
    {
        if (userSymbolInfo == null) return false;
        var key = userSymbolInfo.Id;
        switch (_documentType)
        {
            case DocumentType.GridMonitor:
                if (DockableRegistry.GridMonitorDocuments.TryGetValue(_documentId, out var vm))
                    return vm.Contains(key);
                break;
            case DocumentType.CurveMonitor:
                if (DockableRegistry.CurveMonitorDocuments.TryGetValue(_documentId, out var vm2))
                    return vm2.Contains(key);
                break;
            case DocumentType.GridCalibrate:
                if (DockableRegistry.GridCalibrateDocuments.TryGetValue(_documentId, out var vm3))
                    return vm3.Contains(key);
                break;
            case DocumentType.BitsMonitor:
                if (DockableRegistry.BitsMonitorDocuments.TryGetValue(_documentId, out var vm4))
                    return vm4.Contains(key);
                break;
            default:
                break;
        }
        return false;
    }
    /// <summary>
    /// 处理 CheckBox 的勾选与取消勾选状态切换
    /// </summary>
    [ReactiveCommand]
    private void ToggleSymbol(UserSymbolInfo userSymbolInfo)
    {
        if (userSymbolInfo == null) return;

        var key = userSymbolInfo.Id;

        switch (_documentType)
        {
            case DocumentType.GridMonitor:
                if (DockableRegistry.GridMonitorDocuments.TryGetValue(_documentId, out var vm0))
                    vm0.AddOrRemoveSymbol(key);
                break;
            case DocumentType.CurveMonitor:
                if (DockableRegistry.CurveMonitorDocuments.TryGetValue(_documentId, out var vm1))
                    vm1.AddOrRemoveSymbol(key);
                break;
            case DocumentType.GridCalibrate:
                if (DockableRegistry.GridCalibrateDocuments.TryGetValue(_documentId, out var vm2))
                    vm2.AddOrRemoveSymbol(key);
                break;
            case DocumentType.BitsMonitor:
                if (DockableRegistry.BitsMonitorDocuments.TryGetValue(_documentId, out var vm3))
                    vm3.AddOrRemoveSymbol(key);
                break;
            default:
                break;
        }

        SelectionRefreshVersion++;
    }
    [ReactiveCommand]
    private void ToggleUserCommand(UserCommand command)
    {
        var key = command.Id;
        if (DockableRegistry.UserCommandDocuments.TryGetValue(_documentId, out var vm))
            vm.AddOrRemove(key);
        SelectionRefreshVersion++;
    }

    public void Dispose() => _disposables.Dispose();
}
