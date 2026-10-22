using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using UniversalHost.Models.Logging;
using UniversalHost.Services;
using UniversalHost.ViewModels.Windows;

namespace UniversalHost.Views.Windows;

public partial class LogViewerWindow : Window
{
    private readonly CompositeDisposable _disposables = [];
    private readonly LogViewerWindowViewModel _viewModel;
    private ScrollViewer? _scroller;
    private LogEntry? _anchor;
    private double _anchorTop;
    private bool _scrollbarGesture, _updatingEntries, _closed;
    private int _entriesVersion;

    public LogViewerWindow() : this(new LogViewerWindowViewModel()) { }

    public LogViewerWindow(LogViewerWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _disposables.Add(viewModel.PickDirectory.RegisterHandler(async interaction =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "打开日志目录",
                AllowMultiple = false
            });
            interaction.SetOutput(folders.Count == 0 ? null : folders[0].TryGetLocalPath());
        }));
        _disposables.Add(viewModel.PickFile.RegisterHandler(async interaction =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "打开日志文件",
                AllowMultiple = false,
                FileTypeFilter = [new("JSONL 日志") { Patterns = ["*.jsonl"] }, new("所有文件") { Patterns = ["*"] }]
            });
            interaction.SetOutput(files.Count == 0 ? null : files[0].TryGetLocalPath());
        }));
        _disposables.Add(viewModel.CopyText.RegisterHandler(async interaction =>
        {
            if (Clipboard == null) throw new InvalidOperationException("当前窗口无法访问剪贴板。");
            await Clipboard.SetTextAsync(interaction.Input);
            interaction.SetOutput(Unit.Default);
        }));
        viewModel.EntriesChanging += CaptureAnchor;
        viewModel.EntriesChanged += EntriesChanged;
        viewModel.ScrollToLatestRequested += ScrollToLatest;
        _disposables.Add(Disposable.Create(() =>
        {
            viewModel.EntriesChanging -= CaptureAnchor;
            viewModel.EntriesChanged -= EntriesChanged;
            viewModel.ScrollToLatestRequested -= ScrollToLatest;
            viewModel.Dispose();
        }));
        LogList.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel, true);
        LogList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel, true);
        LogList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel, true);
        LogList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Bubble, true);
        LogList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged, RoutingStrategies.Bubble, true);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NotificationService.Register(this);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0) _viewModel.SuspendFollowing();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.PageUp or Key.Home) _viewModel.SuspendFollowing();
    }

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source)
            _scrollbarGesture = source is ScrollBar || source.GetVisualAncestors().Any(visual => visual is ScrollBar);
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e) => _scrollbarGesture = false;

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_updatingEntries && _scrollbarGesture && e.OffsetDelta.Y < 0) _viewModel.SuspendFollowing();
    }

    private void CaptureAnchor()
    {
        _entriesVersion++;
        _updatingEntries = true;
        _scroller ??= LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var container = LogList.GetRealizedContainers().FirstOrDefault(item =>
            item.TranslatePoint(default, LogList) is { } point && point.Y + item.Bounds.Height > 0);
        _anchor = container?.DataContext as LogEntry;
        _anchorTop = container?.TranslatePoint(default, LogList)?.Y ?? 0;
    }

    private void EntriesChanged(bool hasNewMatches)
    {
        int version = _entriesVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || version != _entriesVersion) return;
            if (hasNewMatches && _viewModel.FollowLatest) ScrollToLatest();
            else if (_anchor != null && _viewModel.Entries.Contains(_anchor))
            {
                // ItemsSource 批量替换后恢复正在看的记录；淘汰范围外的记录只能停留在剩余内容中。
                LogList.ScrollIntoView(_anchor);
                LogList.UpdateLayout();
                var container = LogList.GetRealizedContainers().FirstOrDefault(item => ReferenceEquals(item.DataContext, _anchor));
                if (_scroller != null && container?.TranslatePoint(default, LogList) is { } point)
                    _scroller.Offset = new Vector(_scroller.Offset.X, Math.Max(0, _scroller.Offset.Y + point.Y - _anchorTop));
            }
            _updatingEntries = false;
        }, DispatcherPriority.Loaded);
    }

    private void ScrollToLatest()
    {
        if (_closed || _viewModel.Entries.Count == 0) return;
        // 显式恢复跟随后，不让已排队的列表布局回调再恢复先前的历史位置。
        _anchor = null;
        LogList.ScrollIntoView(_viewModel.Entries[^1]);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _disposables.Dispose();
        base.OnClosed(e);
    }
}
