using Avalonia;
using Avalonia.Controls;
using UniversalHost.Models;
using UniversalHost.ViewModels.Windows;

namespace UniversalHost.Views.Controls;

public partial class SymbolInputCell : UserControl
{
    public static readonly StyledProperty<UserSymbolInfo?> SymbolProperty =
        AvaloniaProperty.Register<SymbolInputCell, UserSymbolInfo?>(nameof(Symbol));
    public UserSymbolInfo? Symbol { get => GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    private SymbolInputViewModel? _editor;
    public SymbolInputCell() => InitializeComponent();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SymbolProperty) BindEditor();
    }
    private void BindEditor()
    {
        _editor?.Dispose();
        _editor = Symbol == null ? null : new SymbolInputViewModel(Symbol);
        EditorRoot.DataContext = _editor;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_editor == null) BindEditor();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _editor?.Dispose(); _editor = null; EditorRoot.DataContext = null;
        base.OnDetachedFromVisualTree(e);
    }
}
