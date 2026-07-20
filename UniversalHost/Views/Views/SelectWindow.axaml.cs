using Avalonia.Controls;
using UniversalHost.ViewModels.Views;

namespace UniversalHost.Views.Views;

public partial class SelectSymbolWindow : Window
{
    public static SelectSymbolWindow Window { get; } = new SelectSymbolWindow() { DataContext = SelectWindowViewModel.Instance };
    public SelectSymbolWindow()
    {
        InitializeComponent();
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        e.Cancel = true;
        this.Hide();
        base.OnClosing(e);
    }
}