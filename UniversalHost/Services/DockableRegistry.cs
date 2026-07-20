using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.ReactiveUI.Controls;
using System.Collections.Generic;
using UniversalHost.ViewModels.Documents;
using UniversalHost.ViewModels.Tools;

namespace UniversalHost.Services;

/// <summary>
/// Dock布局及其上下文的创建和恢复
/// </summary>
public static class DockableRegistry
{
    //保存和读取时使用，用于恢复Context的类
    public class ContextSaveClass
    {
        public Dictionary<string, GridMonitorLayoutSave> GridMonitorIdLayoutPairs { get; set; } = [];
        public Dictionary<string, CurveMonitorLayout> CurveMonitorIdLayoutPairs { get; set; } = [];
        public Dictionary<string, BitsMonitorLayout> BitsMonitorIdLayoutPairs { get; set; } = [];
        public Dictionary<string, GridCalibrateLayoutSave> GridClibrateIdLayoutPairs { get; set; } = [];
        public Dictionary<string, UserCommandLayoutSave> UserCommandIdLayoutPairs { get; set; } = [];
        public void ClearAll()
        {
            GridMonitorIdLayoutPairs.Clear();
            CurveMonitorIdLayoutPairs.Clear();
            BitsMonitorIdLayoutPairs.Clear();
            GridClibrateIdLayoutPairs.Clear();
            UserCommandIdLayoutPairs.Clear();
        }
    }
    public static ContextSaveClass ContextSave { get; } = new ContextSaveClass();
    // 存储正在运行的 GridMonitorViewModel 映射：Key = Guid, ValueString = GridMonitorViewModel
    public static Dictionary<string, GridMonitorViewModel> GridMonitorDocuments { get; } = [];
    public static Dictionary<string, CurveMonitorViewModel> CurveMonitorDocuments { get; } = [];
    public static Dictionary<string, BitsMonitorViewModel> BitsMonitorDocuments { get; } = [];
    public static Dictionary<string, GridCalibrateViewModel> GridCalibrateDocuments { get; } = [];
    public static Dictionary<string, UserCommandViewModel> UserCommandDocuments { get; } = [];
    //IAP tool
    public const string IapToolId = "IapTool";
    public static IapViewModel IapToolViewModel { get; } = new IapViewModel();
    public static void ClearAllDocuments()
    {
        GridMonitorDocuments.Clear();
        CurveMonitorDocuments.Clear();
        BitsMonitorDocuments.Clear();
        GridCalibrateDocuments.Clear();
        UserCommandDocuments.Clear();
    }
    public static GridMonitorViewModel GetOrCreateGridMonitorViewModel(string guid)
    {
        if (!GridMonitorDocuments.TryGetValue(guid, out var vm))
        {
            vm = new GridMonitorViewModel(guid);
            GridMonitorDocuments.Add(guid, vm);
        }
        return vm;
    }
    public static CurveMonitorViewModel GetOrCreateCurveMonitorViewModel(string guid)
    {
        if (!CurveMonitorDocuments.TryGetValue(guid, out var vm))
        {
            vm = new CurveMonitorViewModel(guid);
            CurveMonitorDocuments.Add(guid, vm);
        }
        return vm;
    }
    public static BitsMonitorViewModel GetOrCreateBitsMonitorViewModel(string guid)
    {
        if (!BitsMonitorDocuments.TryGetValue(guid, out var vm))
        {
            vm = new BitsMonitorViewModel(guid);
            BitsMonitorDocuments.Add(guid, vm);
        }
        return vm;
    }
    public static GridCalibrateViewModel GetOrCreateGridCalibrateViewModel(string guid)
    {
        if (!GridCalibrateDocuments.TryGetValue(guid, out var vm))
        {
            vm = new GridCalibrateViewModel(guid);
            GridCalibrateDocuments.Add(guid, vm);
        }
        return vm;
    }
    public static UserCommandViewModel GetOrCreateUserCommandViewModel(string guid)
    {
        if (!UserCommandDocuments.TryGetValue(guid, out var vm))
        {
            vm = new UserCommandViewModel(guid);
            UserCommandDocuments.Add(guid, vm);
        }
        return vm;
    }
    /// <summary>
    /// 保存时使用，更新要保存的数据
    /// </summary>
    public static void UpdateContextSave()
    {
        ContextSave.ClearAll();
        //更新表格监控窗口
        foreach (var item in GridMonitorDocuments)
        {
            ContextSave.GridMonitorIdLayoutPairs.Add(item.Key, item.Value.Save);
        }
        //更新曲线监控窗口
        foreach (var item in CurveMonitorDocuments)
        {
            ContextSave.CurveMonitorIdLayoutPairs.Add(item.Key, item.Value.CurvesLayout);
        }
        //更新表格监控窗口
        foreach (var item in BitsMonitorDocuments)
        {
            ContextSave.BitsMonitorIdLayoutPairs.Add(item.Key, item.Value.Layout);
        }
        //更新表格标定窗口
        foreach (var item in GridCalibrateDocuments)
        {
            ContextSave.GridClibrateIdLayoutPairs.Add(item.Key, item.Value.Save);
        }
        //更新指令窗口
        foreach (var item in UserCommandDocuments)
        {
            ContextSave.UserCommandIdLayoutPairs.Add(item.Key, item.Value.Save);
        }
    }
    /// <summary>
    /// 重建VM。读取时使用。
    /// </summary>
    /// <param name="context">VM数据</param>
    public static void RebuildViewModels(ContextSaveClass context)
    {

        //重建表格监控窗口
        //先清理旧的
        ContextSave.GridMonitorIdLayoutPairs = context.GridMonitorIdLayoutPairs;
        foreach (var item in GridMonitorDocuments.Values)
        {
            item.Dispose();
        }
        GridMonitorDocuments.Clear();
        //添加新的
        foreach (var item in ContextSave.GridMonitorIdLayoutPairs)
        {
            var vm = new GridMonitorViewModel(item.Key, item.Value);
            GridMonitorDocuments.Add(item.Key, vm);
        }
        //重建曲线监控窗口
        ContextSave.CurveMonitorIdLayoutPairs = context.CurveMonitorIdLayoutPairs;
        foreach (var item in CurveMonitorDocuments.Values)
        {
            item.Dispose();
        }
        CurveMonitorDocuments.Clear();
        foreach (var item in ContextSave.CurveMonitorIdLayoutPairs)
        {
            var vm = new CurveMonitorViewModel(item.Key, item.Value);
            CurveMonitorDocuments.Add(item.Key, vm);
        }
        //重建位监控窗口
        ContextSave.BitsMonitorIdLayoutPairs = context.BitsMonitorIdLayoutPairs;
        foreach (var item in BitsMonitorDocuments.Values)
        {
            item.Dispose();
        }
        BitsMonitorDocuments.Clear();
        foreach (var item in ContextSave.BitsMonitorIdLayoutPairs)
        {
            var vm = new BitsMonitorViewModel(item.Key, item.Value);
            BitsMonitorDocuments.Add(item.Key, vm);
        }
        //重建表格标定窗口
        ContextSave.GridClibrateIdLayoutPairs = context.GridClibrateIdLayoutPairs;
        foreach (var item in GridCalibrateDocuments.Values)
        {
            item.Dispose();
        }
        GridCalibrateDocuments.Clear();
        foreach (var item in ContextSave.GridClibrateIdLayoutPairs)
        {
            var vm = new GridCalibrateViewModel(item.Key, item.Value);
            GridCalibrateDocuments.Add(item.Key, vm);
        }
        //重建用户指令窗口
        ContextSave.UserCommandIdLayoutPairs = context.UserCommandIdLayoutPairs;
        foreach (var item in UserCommandDocuments.Values)
        {
            item.Dispose();
        }
        UserCommandDocuments.Clear();
        foreach (var item in ContextSave.UserCommandIdLayoutPairs)
        {
            var vm = new UserCommandViewModel(item.Key, item.Value);
            UserCommandDocuments.Add(item.Key, vm);
        }
    }

    public static void RestoreContexts(IDockable dockable)
    {
        if (dockable is Tool tool)
        {
            switch (tool.Id)
            {
                case IapToolId:

                    tool.Context = IapToolViewModel;
                    break;
            }
        }
        else if (dockable is IDocument document)
        {
            if (GridMonitorDocuments.TryGetValue(document.Id, out var vm))
            {
                document.Context = vm;
            }
            else if (CurveMonitorDocuments.TryGetValue(document.Id, out var vm2))
            {
                document.Context = vm2;
            }
            else if (GridCalibrateDocuments.TryGetValue(document.Id, out var vm3))
            {
                document.Context = vm3;
            }
            else if (BitsMonitorDocuments.TryGetValue(document.Id, out var vm4))
            {
                document.Context = vm4;
            }
            else if (UserCommandDocuments.TryGetValue(dockable.Id, out var vm5))
            {
                document.Context = vm5;
            }
        }

        if (dockable is IRootDock rootDock)
        {
            if (rootDock.DefaultDockable is IDock defaultDock)
            {
                foreach (var item in defaultDock.VisibleDockables ?? [])
                {
                    RestoreContexts(item);
                }
            }
            if (rootDock.HiddenDockables != null)
            {
                foreach (var item in rootDock.HiddenDockables)
                {
                    RestoreContexts(item);
                }
            }
        }

        if (dockable is IDock dock)
        {
            // Visible
            if (dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    RestoreContexts(child);
                }
            }
        }
    }
}

