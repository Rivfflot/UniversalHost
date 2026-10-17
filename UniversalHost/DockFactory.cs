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
    /// 寻找激活的或第一个TAB layout
    /// </summary>
    /// <param name="layout">rootdock</param>
    /// <returns>输入错误或不存在为null。激活的或第一个TAB layout。</returns>
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
        var tool = new Tool
        {
            Id = DockableRegistry.IapToolId,
            Title = "在线升级",
            CanClose = true,
            CanFloat = false,
            CanPin = true,
            MdiBounds = default,
            Context = DockableRegistry.IapToolViewModel
        };
        ApplyToolSize(tool);
        return tool;
    }

    public Tool CreateFaultRecordTool()
    {
        var tool = new Tool
        {
            Id = DockableRegistry.FaultRecordToolId,
            Title = "故障录波",
            CanClose = true,
            CanFloat = false,
            CanPin = true,
            MdiBounds = default,
            Context = DockableRegistry.FaultRecordViewModel
        };
        ApplyToolSize(tool);
        return tool;
    }

    private static void ApplyToolSize(IDockable dockable)
    {
        if (dockable is not Tool tool) return;

        double minWidth;
        double height;
        switch (tool.Id)
        {
            case DockableRegistry.IapToolId:
                minWidth = 360;
                height = 135;
                break;
            case DockableRegistry.FaultRecordToolId:
                minWidth = 260;
                height = 100;
                break;
            default:
                return;
        }

        // 固定工具的尺寸由当前界面决定，工程只保留位置和可用的宽度。
        var bounds = tool.MdiBounds;
        tool.MinWidth = minWidth;
        tool.MinHeight = height;
        tool.MaxHeight = height;
        tool.MdiBounds = new DockRect(bounds.X, bounds.Y, Math.Max(bounds.Width, minWidth), height);
    }

    public override void InitDockable(IDockable dockable, IDockable? owner)
    {
        ApplyToolSize(dockable);
        base.InitDockable(dockable, owner);
    }

    public override void InitLayout(IDockable layout)
    {
        DockableLocator = new Dictionary<string, Func<IDockable?>>
        {
            [DockableRegistry.IapToolId] = () => CreateIapTool(),
            [DockableRegistry.FaultRecordToolId] = () => CreateFaultRecordTool(),
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

    public void ShowOrCreateTool(IRootDock layout, string id)
    {
        var tool = base.FindDockable(layout, x => x.Id == id);

        if (tool == null)
        {
            if (layout.HiddenDockables != null)
            {
                foreach (var item in layout.HiddenDockables)
                {
                    if (item.Id == id)
                    {
                        tool = item;
                        break;
                    }
                }
            }
        }
        var activeTab = FirstOrDefaultActiveTab(layout);
        if (activeTab == null) return;

        if (tool == null)
        {
            switch (id)
            {
                case DockableRegistry.IapToolId:
                    tool = CreateIapTool();
                    break;
                case DockableRegistry.FaultRecordToolId:
                    tool = CreateFaultRecordTool();
                    break;
                default:
                    return;
            }
            AddDockable(activeTab, tool);
            SetActiveDockable(tool);
        }
        else
        {
            ApplyToolSize(tool);
            if (tool.Owner is IDock parentDock)
            {
                var visibleList = parentDock.VisibleDockables;

                if (visibleList!.Contains(tool))
                {
                    HideDockable(tool);

                }
                else
                {
                    RestoreDockable(tool);
                    if (tool.Owner == null)
                    {
                        AddDockable(activeTab, tool);
                    }
                    else
                    {
                        MoveDockable((IDock)tool.Owner, activeTab, tool, null);
                    }
                    SetActiveDockable(tool);
                }
            }
        }
    }
}
