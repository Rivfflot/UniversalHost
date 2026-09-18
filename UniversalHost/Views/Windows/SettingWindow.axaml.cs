using Avalonia.Controls;
using ReactiveUI.Reactive;
using ReactiveUI.Avalonia.Reactive;
using ReactiveUI.Primitives.Reactive.Disposables;
using System;
using UniversalHost.Services;
using UniversalHost.ViewModels.Windows;


namespace UniversalHost.Views.Windows;

public partial class SettingWindow : ReactiveWindow<SettingWindowViewModel>
{
    public SettingWindow()
    {

        InitializeComponent();

        this.WhenActivated((ContainerDisposable disposables) =>
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
