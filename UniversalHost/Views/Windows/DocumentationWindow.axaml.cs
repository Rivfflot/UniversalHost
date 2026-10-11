using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Linq;
using UniversalHost.Models;
using UniversalHost.ViewModels.Windows;

namespace UniversalHost.Views.Windows;

public partial class DocumentationWindow : Window
{
    private readonly Border _trailingSpace = new();

    public DocumentationWindow()
    {
        InitializeComponent();
        DocumentScroller.LayoutUpdated += (_, _) => UpdateTrailingSpace();
        ContentsList.AddHandler(KeyDownEvent, ContentsListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public DocumentationWindow(Documentation document) : this()
    {
        DataContext = new DocumentationWindowViewModel(document);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not DocumentationWindowViewModel viewModel) return;
        DocumentPanel.SetDocument(viewModel.Document);
        _trailingSpace.Height = 0;
        DocumentPanel.Children.Add(_trailingSpace);
    }

    private void UpdateTrailingSpace()
    {
        if (DocumentPanel.Headings.Count == 0 || DocumentScroller.Viewport.Height <= 0) return;
        var lastHeading = DocumentPanel.Headings.Last().Value;
        var position = lastHeading.TranslatePoint(default, DocumentPanel);
        if (position is null) return;

        // 最后一节不足一屏时补齐滚动空间，保证其标题也能滚到视口最上端。
        double height = Math.Max(0, DocumentScroller.Viewport.Height - (_trailingSpace.Bounds.Top - position.Value.Y));
        if (Math.Abs(_trailingSpace.Height - height) > 0.5)
            _trailingSpace.Height = height;
    }

    private void ContentsListSelectionChanged(object? sender, SelectionChangedEventArgs e) => JumpToSelectedHeading();

    private void ContentsListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        var source = e.Source as Visual;
        var item = source as ListBoxItem ?? source?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        // 同一目录项再次点击时 SelectionChanged 不会触发，也需要重新定位。
        if (item?.DataContext is DocumentationHeadingViewModel) JumpToSelectedHeading();
    }

    private void ContentsListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            JumpToSelectedHeading();
            e.Handled = true;
        }
    }

    private void JumpToSelectedHeading()
    {
        if (ContentsList.SelectedItem is not DocumentationHeadingViewModel entry) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!DocumentPanel.Headings.TryGetValue(entry.BlockIndex, out var heading)) return;
            DocumentScroller.UpdateLayout();
            UpdateTrailingSpace();
            DocumentScroller.UpdateLayout();
            if (heading.TranslatePoint(default, DocumentScroller) is { } position)
                DocumentScroller.Offset = new Vector(0, DocumentScroller.Offset.Y + position.Y);
        }, DispatcherPriority.Loaded);
    }
}
