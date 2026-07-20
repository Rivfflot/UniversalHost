using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace UniversalHost.Views.Documents;

public partial class BitsMonitorView : UserControl
{
    public BitsMonitorView()
    {
        InitializeComponent();
    }

    private async void CopySymbolNameClickAsync(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = ((sender as MenuItem)?.DataContext as ViewModels.Documents.BitsMonitorLayout.BitsMonitorSymbol)?.Runtime.Symbol.Name;
        if (name != null)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(name);
            }
        }
    }
    private async void CopySymbolValueClickAsync(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var value = ((sender as MenuItem)?.DataContext as ViewModels.Documents.BitsMonitorLayout.BitsMonitorSymbol)?.Runtime.ValueString;
        if (value != null)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(value);
            }
        }
    }

}