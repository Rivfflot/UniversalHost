using DynamicData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using UniversalHost.Models;

namespace UniversalHost.Services;

public static class SymbolRuntimeService
{
    private static readonly CompositeDisposable _disposables = [];

    private static readonly SourceCache<SymbolRuntime, Guid> _monitorSymbolRuntimesSource =
                                                    new SourceCache<SymbolRuntime, Guid>(x => x.Symbol.Id);
    public static SourceCache<SymbolRuntime, Guid> MonitorSymbolRuntimesSource => _monitorSymbolRuntimesSource;

    private static readonly SourceCache<SymbolRuntime, Guid> _calibrateSymbolRuntimesSource =
                                                   new SourceCache<SymbolRuntime, Guid>(x => x.Symbol.Id);
    public static SourceCache<SymbolRuntime, Guid> CalibrateSymbolRuntimesSource => _calibrateSymbolRuntimesSource;

    static SymbolRuntimeService() { }
    private static void RebuildMonitorSymbolRuntime(Guid id)
    {
        var symbolRuntime = _monitorSymbolRuntimesSource.Lookup(id);
        if (symbolRuntime.HasValue)
        {
            var newRuntime = SymbolRuntime.CreateSymbolRuntime(symbolRuntime.Value.Symbol,
                                                ProjectSaveService.Instance.Settings.MonitorConfig.MaxSaveLen);
            _monitorSymbolRuntimesSource.AddOrUpdate(newRuntime);

            //更新后刷新UI。只刷新表格监控窗口。曲线监控窗口会监听Update事件并更新。
            var gridMonitors = DockableRegistry.GridMonitorDocuments.Values
                    .Where(x => x.KeysSource.Items.Contains(id));

            foreach (var monitor in gridMonitors)
            {
                monitor.RefreshDisplay();
            }
        }
        else
        {
            return;
        }
    }
    /// <summary>
    /// 读取工程文件后重建符号表运行时
    /// </summary>
    public static void RebuildSymbolRuntimes()
    {
        //先清除
        _disposables.Clear();
        _monitorSymbolRuntimesSource.Clear();
        _calibrateSymbolRuntimesSource.Clear();
        //重建SourceCache
        ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols
                    .Connect()
                    .AutoRefresh(symbol => symbol.IsMonitored)
                    .Filter(symbol => symbol.IsMonitored)
                    .Transform(symbol => SymbolRuntime.CreateSymbolRuntime(symbol,
                                                ProjectSaveService.Instance.Settings.MonitorConfig.MaxSaveLen))
                    .PopulateInto(_monitorSymbolRuntimesSource)
                    .DisposeWith(_disposables);

        ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols
                    .Connect()
                    .Transform(symbol => SymbolRuntime.CreateSymbolRuntime(symbol, 3))
                    .PopulateInto(_calibrateSymbolRuntimesSource)
                    .DisposeWith(_disposables);
        //DataType改变时重建改变的符号
        ProjectSaveService.Instance.Settings.MonitorConfig.MonitoredSymbols
                    .Connect()
                    .AutoRefresh(x => x.DataType)
                    .WhereReasonsAre(ChangeReason.Refresh)
                    .Subscribe(changes =>
                    {
                        foreach (var change in changes)
                        {
                            RebuildMonitorSymbolRuntime(change.Current.Id);
                        }
                    })
                    .DisposeWith(_disposables);

        ProjectSaveService.Instance.Settings.CalibrateConfig.CalibratedSymbols
                   .Connect()
                   .AutoRefresh(x => x.DataType)
                   .WhereReasonsAre(ChangeReason.Refresh)
                   .Subscribe(changes =>
                   {
                       foreach (var change in changes)
                       {
                           var symbolRuntime = _calibrateSymbolRuntimesSource.Lookup(change.Current.Id);
                           if (symbolRuntime.HasValue)
                           {
                               var newRuntime = SymbolRuntime.CreateSymbolRuntime(symbolRuntime.Value.Symbol,
                                                                   ProjectSaveService.Instance.Settings.MonitorConfig.MaxSaveLen);
                               _calibrateSymbolRuntimesSource.AddOrUpdate(newRuntime);
                               //更新后刷新UI。
                               var vms = DockableRegistry.GridCalibrateDocuments.Values
                                       .Where(x => x.KeysSource.Items.Contains(change.Current.Id));

                               foreach (var view in vms)
                               {
                                   view.RefreshDisplay();
                               }
                           }
                           else
                           {
                               return;
                           }
                       }
                   })
                   .DisposeWith(_disposables);

        //ValueString 5Hz更新
        Observable.Interval(TimeSpan.FromMilliseconds(200), System.Reactive.Concurrency.TaskPoolScheduler.Default)
                   .Subscribe(_ =>
                   {
                       UpdateMonitoredSymbolsValueString();
                   })
                   .DisposeWith(_disposables);
    }

    private static void UpdateMonitoredSymbolsValueString()
    {
        if (_monitorSymbolRuntimesSource.Items is IList<SymbolRuntime> monitorSymbolRumtimesList)
        {
            for (int i = 0; i < monitorSymbolRumtimesList.Count; i++)
            {
                monitorSymbolRumtimesList[i].ValueToString();
            }
        }
    }
}
