using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ReactiveUI.Avalonia.Reactive;
using ReactiveUI.Primitives.Reactive.Disposables;
using ReactiveUI.Reactive;
using System;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.ViewModels.Windows;


namespace UniversalHost.Views.Windows;

public partial class SettingWindow : ReactiveWindow<SettingWindowViewModel>
{
    public static readonly IValueConverter IsHexAddress = new FuncValueConverter<string, bool>(
        value => value?.StartsWith("0x", StringComparison.Ordinal) == true);

    public SettingWindow()
    {

        InitializeComponent();

        this.WhenActivated((ContainerDisposable disposables) =>
        {
            // 只需要这一行，服务会自动管理这个窗口的通知生命周期
            NotificationService.Register(this);
        });
    }

    private void ShowSymbolCopyMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual source ||
            source.FindAncestorOfType<DataGridRow>(includeSelf: true) is not { } row)
            return;

        var menuKey = row.DataContext switch
        {
            SymbolTableEntry => "SymbolTableCopyMenu",
            UserSymbolInfo => "UserSymbolCopyMenu",
            _ => null
        };
        if (menuKey is null || Resources[menuKey] is not ContextMenu menu) return;

        menu.DataContext = row.DataContext;
        menu.Placement = e.TryGetPosition(row, out _) ? PlacementMode.Pointer : PlacementMode.Bottom;
        menu.Open(row);
        e.Handled = true;
    }

    private async void CopySymbolTextClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: string text } || string.IsNullOrEmpty(text)) return;

        try
        {
            var clipboard = Clipboard ?? throw new InvalidOperationException("当前窗口无法访问剪贴板。");
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            NotificationService.Show("复制失败", ex.Message, NotificationType.Warning);
        }
    }

    private void SymbolFileMenuItemCopyPathClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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

    private async void SymbolFileMenuItemUpdatePathClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is SettingWindowViewModel vm && sender is MenuItem { DataContext: string path })
            await vm.UpdateSymbolFilePathAsync(this, path);
    }
}
