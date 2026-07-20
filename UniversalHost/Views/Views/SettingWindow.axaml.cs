using Avalonia.Controls;
using ReactiveUI;
using ReactiveUI.Avalonia;
using System;
using UniversalHost.Services;
using UniversalHost.ViewModels.Views;


namespace UniversalHost.Views.Views;

public partial class SettingWindow : ReactiveWindow<SettingWindowViewModel>
{
    public SettingWindow()
    {

        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            // 只需要这一行，服务会自动管理这个窗口的通知生命周期
            NotificationService.Register(this);
        });
    }
    private void ElfTableMenuItemCopyPathClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is SettingWindowViewModel vm)
        {
            var path = (sender as MenuItem)?.DataContext as string;
            // 当前窗口
            var window = this;
            if (path != null)
            {
                vm.CopySymbolFilePathCommand.Execute((window, path)).Subscribe();
            }
        }
    }
}
