using ReactiveUI.Reactive;
using ReactiveUI.Avalonia.Reactive;
using System.Reactive.Disposables.Fluent;
using UniversalHost.Services;
using UniversalHost.ViewModels;
using UniversalHost.ViewModels.Windows;
using UniversalHost.Views.Windows;
#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
#endif

namespace UniversalHost.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        public MainWindow()
        {
            InitializeComponent();

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

                vm.ShowRenameDialog.RegisterHandler(async interaction =>
                    {
                        string oldName = interaction.Input;

                        var renameVm = new RenameWindowViewModel(oldName);
                        var renameWindow = new RenameWindow { DataContext = renameVm };
                        //renameWindow.Activate();
                        string? result = await renameWindow.ShowDialog<string?>(this);

                        interaction.SetOutput(result);
                    })
                   .DisposeWith(disposables);
            });
        }
    }
}
