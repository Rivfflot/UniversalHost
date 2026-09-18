using Avalonia.Controls;
using UniversalHost.ViewModels.Windows;

namespace UniversalHost.Views.Windows;

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