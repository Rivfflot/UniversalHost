using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models.Logging;
using UniversalHost.Services;
using UniversalHost.Services.Logging;

namespace UniversalHost.ViewModels.Windows;

public sealed class LogFilterOption : ReactiveObject
{
    private bool _isSelected;
    private readonly Action _changed;
    public string Name { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value == _isSelected) return;
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            _changed();
        }
    }

    public LogFilterOption(string name, Action changed) { Name = name; _changed = changed; }
}

public partial class LogViewerWindowViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _subscriptions = [];
    private readonly CompositeDisposable _cleanupDisposables;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(1));
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly HashSet<Task> _work = [];
    private CancellationTokenSource _source = new();
    private CancellationTokenSource? _readCancellation, _filterCancellation, _detailCancellation;
    private LogSnapshot _snapshot = LogSnapshot.Empty;
    private LogSnapshot _visibleSnapshot = LogSnapshot.Empty;
    private string? _folder, _file;
    private DateTimeOffset? _validStart, _validEnd;
    private TimeSpan _logOffset;
    private bool _initializeLogTime = true;
    private long _displayedThroughId;
    private bool _updatingFiles, _updatingFilters, _disposed;

    public Interaction<Unit, string?> PickDirectory { get; } = new();
    public Interaction<Unit, string?> PickFile { get; } = new();
    public Interaction<string, Unit> CopyText { get; } = new();
    public event Action? EntriesChanging;
    public event Action<bool>? EntriesChanged;
    public event Action? ScrollToLatestRequested;

    [Reactive] private IReadOnlyList<LogFileItem> _files = Array.Empty<LogFileItem>();
    [Reactive] private LogFileItem? _selectedFile;
    [Reactive] private IReadOnlyList<LogEntry> _entries = Array.Empty<LogEntry>();
    [Reactive] private LogEntry? _selectedEntry;
    [Reactive] private IReadOnlyList<LogFilterOption> _modules = Array.Empty<LogFilterOption>();
    [Reactive] private string _location = "尚未打开目录或文件";
    [Reactive] private string _statusText = "请打开目录或 JSONL 日志文件。";
    [Reactive] private string _countsText = "已加载 0 条 · 保留 0 条 · 筛选后 0 条 · 跳过 0 行";
    [Reactive] private string _retentionText = "最多保留最近 20,000 条且文本不超过 32 MiB；单行上限 1 MiB。";
    [Reactive] private bool _useStartTime;
    [Reactive] private bool _useEndTime;
    [Reactive] private DateTimeOffset? _startDate;
    [Reactive] private DateTimeOffset? _endDate;
    [Reactive] private TimeSpan? _startClock;
    [Reactive] private TimeSpan? _endClock;
    [Reactive] private bool _hasLogTime;
    [Reactive] private string _timeZoneText = "读取日志后可选择日志时间。";
    [Reactive] private LogLevelOption _minimumLevel = new("全部", null);
    [Reactive] private string _timeError = "";
    [Reactive] private string _details = "选择一条日志查看完整消息、异常和结构化属性。";
    [Reactive] private bool _autoRefresh = true;
    [Reactive] private bool _followLatest = true;
    [Reactive] private bool _isReading;
    public IReadOnlyList<LogLevelOption> Levels { get; }
    public string ReadModeText => AutoRefresh ? "自动刷新：约 1 秒" : "自动刷新已关闭，可手动刷新";
    public string FollowText => !FollowLatest ? "跟随已关闭，勾选“跟随最新”可恢复" : "正在跟随最新匹配日志";

    public LogViewerWindowViewModel()
    {
        Levels = [MinimumLevel, .. Enum.GetValues<LogEventLevel>().Select(level => new LogLevelOption(level.ToString(), level))];
        // 后台任务结束后再统一释放，避免读取任务仍在使用取消源或刷新锁。
        _cleanupDisposables =
        [
            // 切换文件时会替换 _source，因此释放时取当前实例。
            Disposable.Create(() => _source.Dispose()),
            _lifetime,
            _refreshGate,
            OpenDirectoryCommand,
            OpenFileCommand,
            ManualRefreshCommand,
            ClearFiltersCommand,
            CopyDetailsCommand,
            CopyJsonCommand
        ];
        _subscriptions.Add(this.WhenAnyValue(x => x.UseStartTime, x => x.UseEndTime,
                x => x.StartDate, x => x.EndDate, x => x.StartClock, x => x.EndClock, x => x.MinimumLevel)
            .Skip(1).Subscribe(_ => FilterInputChanged()));
        _subscriptions.Add(this.WhenAnyValue(x => x.SelectedFile).Skip(1).Subscribe(file =>
        {
            if (!_updatingFiles && file != null && file.FullPath != _file)
            {
                // 手动选文件即进入历史浏览，避免下次轮询又跳回目录中的最新文件。
                FollowLatest = false;
                ChangeFile(file.FullPath);
            }
        }));
        _subscriptions.Add(this.WhenAnyValue(x => x.SelectedEntry).Skip(1).Subscribe(_ => QueueDetails()));
        _subscriptions.Add(this.WhenAnyValue(x => x.AutoRefresh).Skip(1).Subscribe(enabled =>
        {
            this.RaisePropertyChanged(nameof(ReadModeText));
            if (enabled) StartWork(() => RefreshAsync(true, automatic: true));
            else
            {
                FollowLatest = false;
                _readCancellation?.Cancel();
                // 已读完但尚未显示的结果也要取消；恢复时从最后显示的游标补读。
                _snapshot = _visibleSnapshot;
                UpdateModules();
                QueueFilter(false);
            }
        }));
        _subscriptions.Add(this.WhenAnyValue(x => x.FollowLatest).Skip(1).Subscribe(follow =>
        {
            this.RaisePropertyChanged(nameof(FollowText));
            if (!follow) return;
            bool alreadyRefreshing = AutoRefresh;
            _readCancellation?.Cancel();
            AutoRefresh = true;
            ScrollToLatestRequested?.Invoke();
            // 联动开启自动刷新已触发读取，避免同一次操作再排入一个重复刷新。
            if (alreadyRefreshing) StartWork(() => RefreshAsync(true, automatic: true));
        }));
        StartWork(TimerLoopAsync);
    }

    public void Initialize(string? projectDirectory)
    {
        if (projectDirectory != null) OpenDirectory(projectDirectory);
    }

    [ReactiveCommand]
    private async Task OpenDirectoryAsync()
    {
        try
        {
            var path = await PickDirectory.Handle(Unit.Default);
            if (!_disposed && path != null) OpenDirectory(path);
        }
        catch (Exception ex) { if (!_disposed) StatusText = $"打开目录失败：{ex.Message}"; }
    }

    [ReactiveCommand]
    private async Task OpenFileAsync()
    {
        try
        {
            var path = await PickFile.Handle(Unit.Default);
            if (!_disposed && path != null) OpenFile(path);
        }
        catch (Exception ex) { if (!_disposed) StatusText = $"打开文件失败：{ex.Message}"; }
    }

    public void OpenDirectory(string folder)
    {
        if (_disposed) return;
        _folder = folder;
        _file = null;
        Location = folder;
        SetFiles([], null);
        ResetSource();
        StartWork(() => RefreshAsync(true));
    }

    public void OpenFile(string file)
    {
        if (_disposed) return;
        _folder = null;
        // 即使与目录模式中选中的路径相同，也要取消旧目录扫描并建立独立文件会话。
        _file = null;
        SetFiles([new(file, Path.GetFileName(file))], file);
        ChangeFile(file);
    }

    private void ChangeFile(string file, bool automatic = false, bool notifySuccess = false)
    {
        if (_disposed || _file == file) return;
        _file = file;
        Location = file;
        ResetSource();
        StartWork(() => RefreshAsync(true, automatic, notifySuccess));
    }

    private void ResetSource()
    {
        _source.Cancel();
        _source.Dispose();
        _source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _filterCancellation?.Cancel();
        _detailCancellation?.Cancel();
        _snapshot = LogSnapshot.Empty;
        _visibleSnapshot = LogSnapshot.Empty;
        _displayedThroughId = 0;
        _initializeLogTime = true;
        HasLogTime = false;
        TimeZoneText = "读取日志后可选择日志时间。";
        SelectedEntry = null;
        EntriesChanging?.Invoke();
        Entries = Array.Empty<LogEntry>();
        EntriesChanged?.Invoke(false);
        UpdateModules();
        UpdateCounts();
        StatusText = "正在打开…";
    }

    [ReactiveCommand]
    private void ManualRefresh()
    {
        StartWork(() => RefreshAsync(false, notifySuccess: true));
    }

    [ReactiveCommand]
    private void ClearFilters()
    {
        _updatingFilters = true;
        foreach (var option in Modules) option.IsSelected = false;
        MinimumLevel = Levels[0];
        UseStartTime = UseEndTime = false;
        _updatingFilters = false;
        UpdateModules();
        QueueFilter(false);
    }

    public void SuspendFollowing()
    {
        FollowLatest = false;
    }

    [ReactiveCommand]
    private async Task CopyDetailsAsync()
    {
        if (SelectedEntry == null) return;
        try
        {
            var entry = SelectedEntry;
            var details = await Task.Run(() => LogFilterService.FormatDetails(entry));
            if (!_disposed) await CopyText.Handle(details);
        }
        catch (Exception ex) { if (!_disposed) StatusText = $"复制失败：{ex.Message}"; }
    }

    [ReactiveCommand]
    private async Task CopyJsonAsync()
    {
        if (SelectedEntry == null) return;
        try { await CopyText.Handle(SelectedEntry.RawJson); }
        catch (Exception ex) { if (!_disposed) StatusText = $"复制失败：{ex.Message}"; }
    }

    private async Task TimerLoopAsync()
    {
        while (await _timer.WaitForNextTickAsync(_lifetime.Token))
        {
            try { if (AutoRefresh) await RefreshAsync(false, automatic: true); }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { }
        }
    }

    private async Task RefreshAsync(bool wait, bool automatic = false, bool notifySuccess = false)
    {
        if (_disposed || (automatic && !AutoRefresh) || (_folder == null && _file == null)) return;
        var sourceToken = _source.Token;
        bool entered = wait ? await WaitForRefreshAsync(sourceToken) : await _refreshGate.WaitAsync(0, sourceToken);
        if (!entered) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sourceToken);
        _readCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            // 自动刷新可能仍在等待读取锁；关闭开关后不能由排队任务重新开始读取。
            if (_disposed || (automatic && !AutoRefresh)) return;
            IsReading = true;
            if (_folder != null)
            {
                string folder = _folder;
                try
                {
                    var files = await Task.Run(() => LogSnapshot.GetFiles(folder, token), token);
                    token.ThrowIfCancellationRequested();
                    if (files.Length != 0 && (_file == null || FollowLatest) && files[0].FullPath != _file)
                    {
                        SetFiles(files, files[0].FullPath);
                        // 切换分片使用与手动切文件相同的取消/清理路径。新读取等待本轮释放锁，
                        // 不在持锁期间 await，也不允许旧文件的筛选结果覆盖新文件。
                        ChangeFile(files[0].FullPath, automatic, notifySuccess);
                        return;
                    }
                    SetFiles(files, _file);
                    if (_file == null) StatusText = "此目录暂无 .jsonl 日志文件；刷新时会重新检查。";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    StatusText = $"无法列出目录：{ex.Message}；请检查路径和权限后刷新。";
                    return;
                }
            }
            if (_file == null)
            {
                if (notifySuccess) NotificationService.Show("日志刷新成功", StatusText, NotificationType.Success);
                return;
            }
            string file = _file;
            var snapshot = _snapshot;
            var result = await Task.Run(async () =>
            {
                var batch = await LogFileReader.ReadAsync(file, snapshot.Cursor, snapshot.LastId, token);
                return (Snapshot: batch.Changed ? snapshot.Append(batch, token) : snapshot, batch.Changed,
                    batch.Reset, FirstTime: batch.Entries.FirstOrDefault()?.Timestamp, LastTime: batch.Entries.LastOrDefault()?.Timestamp);
            }, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || (automatic && !AutoRefresh)) return;
            _snapshot = result.Snapshot;
            if (result.Reset) _initializeLogTime = true;
            if (_initializeLogTime && result.FirstTime is { } firstTime)
                InitializeLogTime(firstTime, result.LastTime!.Value);
            long remaining = Math.Max(0, _snapshot.FileLength - _snapshot.Cursor.Position);
            string pending = _snapshot.Cursor.Pending.Length > 0 || _snapshot.Cursor.DiscardingLongLine
                ? "末尾有未结束的行，等待换行后解析。" : "";
            StatusText = $"{_snapshot.Notice} {(_snapshot.FileLength == 0 ? "文件为空，等待日志写入。" : "读取正常。")}" +
                $"{pending}{(remaining > 0 ? $"尚有约 {remaining / 1024.0:N0} KiB 待补读，每次最多 4 MiB。" : "")}";
            if (result.Changed)
            {
                UpdateModules();
                QueueFilter(false);
            }
            UpdateCounts();
            if (notifySuccess) NotificationService.Show("日志刷新成功", StatusText, NotificationType.Success);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!sourceToken.IsCancellationRequested)
                StatusText = ex is FileNotFoundException or DirectoryNotFoundException
                    ? "日志文件已被删除或移动；保留当前内容，文件恢复后刷新会重试。"
                    : $"无法读取日志：{ex.Message}；保留当前内容，请检查权限或文件占用后刷新。";
        }
        finally
        {
            if (ReferenceEquals(_readCancellation, cancellation)) _readCancellation = null;
            if (!_disposed) IsReading = false;
            _refreshGate.Release();
        }
    }

    private async Task<bool> WaitForRefreshAsync(CancellationToken token)
    {
        await _refreshGate.WaitAsync(token);
        return true;
    }

    private void SetFiles(LogFileItem[] files, string? selectedPath)
    {
        // 关闭跟随时保留缺失文件的选择，等待同路径恢复。
        if (selectedPath != null && files.All(item => item.FullPath != selectedPath))
            files = [new(selectedPath, Path.GetFileName(selectedPath) + "（等待恢复）"), .. files];
        _updatingFiles = true;
        if (!Files.SequenceEqual(files)) Files = files;
        SelectedFile = Files.FirstOrDefault(item => item.FullPath == selectedPath);
        _updatingFiles = false;
    }

    private void UpdateModules()
    {
        var existing = Modules.ToDictionary(option => option.Name, StringComparer.Ordinal);
        var names = _snapshot.Modules.Concat(Modules.Where(option => option.IsSelected).Select(option => option.Name))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (Modules.Select(option => option.Name).SequenceEqual(names)) return;
        Modules = names.Select(name => existing.TryGetValue(name, out var option)
            ? option : new LogFilterOption(name, FilterInputChanged)).ToArray();
    }

    private void FilterInputChanged()
    {
        if (!_updatingFilters) QueueFilter(true);
    }

    private void InitializeLogTime(DateTimeOffset first, DateTimeOffset last)
    {
        _updatingFilters = true;
        // 文件时区改变时保留已选边界对应的时刻；未启用的边界以新文件首批日志预填。
        var start = UseStartTime ? LogFilterService.FromSelection(StartDate, StartClock, _logOffset) : null;
        var end = UseEndTime ? LogFilterService.FromSelection(EndDate, EndClock, _logOffset) : null;
        _logOffset = first.Offset;
        var startDisplay = (start ?? first).ToOffset(_logOffset);
        var endDisplay = (end ?? last).ToOffset(_logOffset);
        StartDate = startDisplay;
        EndDate = endDisplay;
        StartClock = TimeSpan.FromTicks(startDisplay.TimeOfDay.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
        EndClock = TimeSpan.FromTicks(endDisplay.TimeOfDay.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
        HasLogTime = true;
        TimeZoneText = $"日志时间（UTC{first.ToString("zzz", CultureInfo.InvariantCulture)}，取自文件首条日志）；未勾选表示不限，结束时间包含所选整秒。";
        _initializeLogTime = false;
        _updatingFilters = false;
    }

    private void QueueFilter(bool debounce)
    {
        if (_disposed) return;
        _filterCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_source.Token);
        _filterCancellation = cancellation;
        StartWork(async () =>
        {
            try
            {
                var token = cancellation.Token;
                if (debounce) await Task.Delay(250, token);
                var start = UseStartTime ? LogFilterService.FromSelection(StartDate, StartClock, _logOffset) : null;
                var end = UseEndTime ? LogFilterService.FromSelection(EndDate, EndClock, _logOffset, true) : null;
                string error = (UseStartTime && start == null) || (UseEndTime && end == null)
                    ? "请选择完整的日期和时间。" : start > end ? "开始时间不能晚于结束时间。" : "";
                if (error.Length == 0)
                {
                    _validStart = start;
                    _validEnd = end;
                    TimeError = "";
                }
                else TimeError = error + " 沿用上次有效时间范围。";
                var filter = new LogFilter(MinimumLevel.Level,
                    Modules.Where(x => x.IsSelected).Select(x => x.Name).ToHashSet(StringComparer.Ordinal), _validStart, _validEnd);
                var snapshot = _snapshot;
                var displayed = Entries;
                long throughId = _displayedThroughId;
                var result = await Task.Run(() =>
                {
                    var entries = LogFilterService.Apply(snapshot.Entries, filter, token);
                    return (Entries: entries, Changed: !entries.SequenceEqual(displayed),
                        NewMatches: entries.Any(entry => entry.Id > throughId));
                }, token);
                token.ThrowIfCancellationRequested();
                if (_disposed) return;
                if (result.Changed)
                {
                    var selection = SelectedEntry;
                    EntriesChanging?.Invoke();
                    Entries = result.Entries;
                    SelectedEntry = selection == null ? null : result.Entries.FirstOrDefault(entry => entry.Id == selection.Id);
                    EntriesChanged?.Invoke(result.NewMatches);
                }
                _displayedThroughId = snapshot.LastId;
                _visibleSnapshot = snapshot;
                UpdateCounts();
            }
            finally
            {
                if (ReferenceEquals(_filterCancellation, cancellation)) _filterCancellation = null;
                cancellation.Dispose();
            }
        });
    }

    private void QueueDetails()
    {
        _detailCancellation?.Cancel();
        Details = SelectedEntry == null ? "选择一条日志查看完整消息、异常和结构化属性。" : "正在读取详情…";
        if (SelectedEntry is not { } entry || _disposed) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_source.Token);
        _detailCancellation = cancellation;
        StartWork(async () =>
        {
            try
            {
                var token = cancellation.Token;
                var details = await Task.Run(() => LogFilterService.FormatDetails(entry), token);
                token.ThrowIfCancellationRequested();
                if (!_disposed && SelectedEntry == entry) Details = details;
            }
            finally
            {
                if (ReferenceEquals(_detailCancellation, cancellation)) _detailCancellation = null;
                cancellation.Dispose();
            }
        });
    }

    private void UpdateCounts()
    {
        CountsText = $"已加载 {_snapshot.LoadedCount:N0} 条 · 保留 {_snapshot.Entries.Length:N0} 条 · 筛选后 {Entries.Count:N0} 条 · 跳过 {_snapshot.SkippedLines:N0} 行（格式错误或超过 1 MiB）";
        RetentionText = "最多保留最近 20,000 条且文本不超过 32 MiB；单行上限 1 MiB。" +
            (_snapshot.Entries.Length == 0 ? "" : $" 当前保留文件行 {_snapshot.Entries[0].LineNumber:N0}–{_snapshot.Entries[^1].LineNumber:N0}，已淘汰 {_snapshot.LoadedCount - _snapshot.Entries.Length:N0} 条。");
    }

    private void StartWork(Func<Task> action)
    {
        if (_disposed) return;
        var task = action();
        _work.Add(task);
        _ = ObserveWorkAsync(task);
    }

    private async Task ObserveWorkAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) StatusText = $"日志查看器操作失败：{ex.Message}"; }
        finally { _work.Remove(task); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _source.Cancel();
        _timer.Dispose();
        _subscriptions.Dispose();
        EntriesChanging = null;
        EntriesChanged = null;
        ScrollToLatestRequested = null;
        _ = FinishCleanupAsync(_work.ToArray());
    }

    private async Task FinishCleanupAsync(Task[] work)
    {
        try { await Task.WhenAll(work); }
        catch (Exception) { /* 各任务错误已由 ObserveWorkAsync 处理。 */ }
        _cleanupDisposables.Dispose();
        _snapshot = LogSnapshot.Empty;
        _visibleSnapshot = LogSnapshot.Empty;
        Entries = Array.Empty<LogEntry>();
        SelectedEntry = null;
        Details = "";
    }
}
