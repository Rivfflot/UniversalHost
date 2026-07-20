using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.ReactiveUI;
using Dock.Model.ReactiveUI.Controls;
using System;
using System.Collections.Generic;
using UniversalHost.Services;
using UniversalHost.ViewModels.Documents;
namespace UniversalHost;

public class DockFactory : Factory
{
    public DockFactory()
    {
        HideDocumentsOnClose = true;
    }

    public override IRootDock CreateLayout()
    {
        var defaultTab = CreateTab();
        var tabDock = new DocumentDock
        {
            Id = "RootTabDock",
            VisibleDockables = CreateList<IDockable>(defaultTab),
            ActiveDockable = defaultTab,
            CanClose = false,
            CanFloat = false,
            CanCreateDocument = false,
            CanDrag = false,
            CanPin = false,
            CanDrop = false,
            CanDockAsDocument = false,
            CanCloseLastDockable = false,
            EmptyContent = null,
        };

        return new RootDock
        {
            VisibleDockables = CreateList<IDockable>
            (
                tabDock
            ),
            DefaultDockable = tabDock,
            ActiveDockable = defaultTab
        };
    }
    private DocumentDock CreateTab()
    {
        return new DocumentDock
        {
            AllowedDropOperations = DockOperationMask.Fill,
            Id = Guid.NewGuid().ToString(),
            Title = "标签页",
            VisibleDockables = CreateList<IDockable>(new DockDock
            {
                Title = "__placeholder__",
            }),
            ActiveDockable = null,
            LayoutMode = DocumentLayoutMode.Mdi,
            CanFloat = false,
            EmptyContent = null,
        };
    }
    public void CreateNewTab(IRootDock layout)
    {
        var newTab = CreateTab();
        if (layout.DefaultDockable is not DocumentDock tabDock) return;
        tabDock.VisibleDockables!.Add(newTab);
    }
    /// <summary>
    /// 寻找激活的或第一个TAB dock
    /// </summary>
    /// <param name="layout">rootdock</param>
    /// <returns>输入错误或不存在为null。激活的或第一个TAB dock。</returns>
    public DocumentDock? FirstOrDefaultActiveTab(IRootDock layout)
    {
        if (layout.DefaultDockable is not DocumentDock tabDock) return null;
        if (tabDock.ActiveDockable == null)
        {
            if (tabDock.VisibleDockables == null || tabDock.VisibleDockables.Count == 0)
                return null;

            return tabDock.VisibleDockables[0] as DocumentDock;
        }

        return tabDock.ActiveDockable as DocumentDock;
    }

    public Document GetOrCreateGridMonitorDocument(string id)
    {
        return new Document
        {
            Id = id,
            Title = "表格监控窗口",
            CanClose = true,
            CanFloat = false,
            CanPin = true,
            MdiBounds = new DockRect(0, 0, 360, 240),
            Context = DockableRegistry.GetOrCreateGridMonitorViewModel(id)
        };
    }
    public Document GetOrCreateCurveMonitorDocument(string id)
    {
        return new Document
        {
            Id = id,
            Title = "曲线监控",
            CanClose = true,
            CanFloat = false,
            CanPin = true,
            MdiBounds = new DockRect(0, 0, 500, 400),
            Context = DockableRegistry.GetOrCreateCurveMonitorViewModel(id)
        };
    }
    public Document GetOrCreateBitsMonitorDocument(string id)
    {
        return new Document
        {
            Id = id,
            Title = "位监控",
            CanClose = true,
            CanFloat = false,
            CanPin = true,
            MdiBounds = new DockRect(0, 0, 160, 360),
            MinWidth = 160,
            Context = DockableRegistry.GetOrCreateBitsMonitorViewModel(id)
        };
    }
    public Document GetOrCreateGridCalibrateDocument(string id)
    {
        return new Document
        {
            Id = id,
            Title = "表格标定",
            CanClose = true,
            CanFloat = false,
            CanPin = true,
            MdiBounds = new DockRect(0, 0, 360, 240),
            Context = DockableRegistry.GetOrCreateGridCalibrateViewModel(id)
        };
    }
    public Document GetOrCreateUserCommandDocument(string id)
    {
        return new Document
        {
            Id = id,
            Title = "指令",
            CanClose = true,
            CanFloat = false,
            CanPin = true,
            MdiBounds = new DockRect(0, 0, 200, 200),
            Context = DockableRegistry.GetOrCreateUserCommandViewModel(id)
        };
    }
    public Tool CreateIapTool()
    {
        return new Tool
        {
            Id = DockableRegistry.IapToolId,
            Title = "在线升级",
            CanClose = true,
            CanFloat = false,
            CanPin = true,
            MdiBounds = new DockRect(0, 0, 360, 120),
            MinWidth = 360,
            MaxHeight = 120,
            Context = DockableRegistry.IapToolViewModel
        };
    }
    public override void InitLayout(IDockable layout)
    {
        DockableLocator = new Dictionary<string, Func<IDockable?>>
        {
            ["IapTool"] = () => CreateIapTool(),
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        };

        if (layout is IRootDock l && l.DefaultDockable is DocumentDock d)
        {

            d.EmptyContent = string.Empty;
            if (d.VisibleDockables != null)
            {
                foreach (var item in d.VisibleDockables)
                {
                    if (item is DocumentDock tab)
                    {
                        tab.EmptyContent = null;
                    }
                }
            }
        }

        base.InitLayout(layout);

        DockableRegistry.RestoreContexts(layout);
    }
    public override void OnDockableClosed(IDockable? dockable)
    {
        if (dockable is Document doc)
        {
            if (doc.Context is GridMonitorViewModel vm)
            {
                vm.Dispose();
                DockableRegistry.GridMonitorDocuments.Remove(vm.Id);
            }
            else if (doc.Context is CurveMonitorViewModel vm2)
            {
                vm2.Dispose();
                DockableRegistry.CurveMonitorDocuments.Remove(vm2.Id);
            }
            else if (doc.Context is GridCalibrateViewModel vm3)
            {
                vm3.Dispose();
                DockableRegistry.GridCalibrateDocuments.Remove(vm3.Id);
            }
            else if (doc.Context is UserCommandViewModel vm4)
            {
                vm4.Dispose();
                DockableRegistry.UserCommandDocuments.Remove(vm4.Id);
            }
            else if (doc.Context is BitsMonitorViewModel vm5)
            {
                vm5.Dispose();
                DockableRegistry.BitsMonitorDocuments.Remove(vm5.Id);
            }
        }
        base.OnDockableClosed(dockable);
    }
}