using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using UniversalHost.Services;
using UniversalHost.ViewModels;
using UniversalHost.ViewModels.Windows;
using UniversalHost.Views.Windows;
#if DEBUG
using Avalonia.Data;
using Avalonia.Media;
#endif

namespace UniversalHost.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        public MainWindow()
        {
            InitializeComponent();
            AddHandler(PointerReleasedEvent, OpenMdiContextMenu, RoutingStrategies.Tunnel);

#if DEBUG
            // 调试菜单在代码中创建，避免 Release XAML 引用已被条件编译移除的命令。
            var randomDataMenuItem = new MenuItem
            {
                Header = "🔢",
                Cursor = new Cursor(StandardCursorType.Hand),
                FontFamily = new FontFamily("Apple Color Emoji, Noto Color Emoji"),
                FontSize = 18,
                Padding = new Thickness(0)
            };
            DockPanel.SetDock(randomDataMenuItem, Avalonia.Controls.Dock.Right);
            ToolTip.SetTip(randomDataMenuItem, "测试");
            ToolTip.SetShowOnDisabled(randomDataMenuItem, true);
            randomDataMenuItem.Bind(MenuItem.CommandProperty,
                new Binding(nameof(MainWindowViewModel.ToggleRandomDataCommand)));
            MainMenu.Items.Add(randomDataMenuItem);
#endif

            this.WhenActivated(disposables =>
            {
                // 服务会自动管理这个窗口的通知生命周期
                NotificationService.Register(this);

                if (DataContext is not MainWindowViewModel vm) return;

                disposables.Add(vm.ShowDocumentationWindow.RegisterHandler(interaction =>
                {
                    var window = new DocumentationWindow(interaction.Input);
                    WindowScreenSizing.FitAndCenter(window, this);
                    window.Show(this);
                    interaction.SetOutput(RxVoid.Default);
                }));

                disposables.Add(vm.ShowRenameDialog.RegisterHandler(async interaction =>
                {
                    string oldName = interaction.Input;

                    var renameVm = new RenameWindowViewModel(oldName);
                    var renameWindow = new RenameWindow { DataContext = renameVm };
                    string? result = await renameWindow.ShowDialog<string?>(this);

                    interaction.SetOutput(result);
                }));
            });
        }

        private void OpenMdiContextMenu(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Right ||
                e.Source is not Visual source || source.FindAncestorOfType<MdiDocumentWindow>(includeSelf: true) != null ||
                source.FindAncestorOfType<MdiDocumentControl>(includeSelf: true) is not { } workspace) return;

            // 空标签页显示占位内容，布局面板要等第一个窗口加入后才会生成。
            Control surface = source.FindAncestorOfType<MdiLayoutPanel>(includeSelf: true) ?? (Control)workspace;
            var position = e.GetPosition(surface);
            if (!new Rect(surface.Bounds.Size).Contains(position)) return;

            var menu = (ContextMenu)Resources["MdiWorkspaceContextMenu"]!;
            menu.Tag = position;
            menu.Open(surface);
            e.Handled = true;
        }
    }
}
