using System;
using System.IO;
using System.Linq;
using System.Threading;
using UniversalHost.Models.Logging;

namespace UniversalHost.Services.Logging;

public sealed record LogSnapshot(LogReadCursor Cursor, LogEntry[] Entries, string[] Modules,
    long LoadedCount, long SkippedLines, long LastId, long FileLength, string Notice)
{
    public const int MaxEntries = 20000;
    public const long MaxTextBytes = 32 * 1024 * 1024;
    public static LogSnapshot Empty { get; } = new(LogReadCursor.Empty, [], [], 0, 0, 0, 0, "");

    public LogSnapshot Append(LogReadBatch batch, CancellationToken token)
    {
        var previous = batch.Reset ? Empty : this;
        int totalCount = previous.Entries.Length + batch.Entries.Length;
        int first = totalCount;
        long size = 0;
        // 从尾部确定可保留范围，避免先合并全部记录、超限后再复制一次。
        while (first > 0 && totalCount - first < MaxEntries)
        {
            token.ThrowIfCancellationRequested();
            int index = first - 1;
            var entry = index < previous.Entries.Length
                ? previous.Entries[index] : batch.Entries[index - previous.Entries.Length];
            if (entry.TextBytes > MaxTextBytes - size) break;
            size += entry.TextBytes;
            first--;
        }
        var entries = new LogEntry[totalCount - first];
        int previousCount = Math.Max(0, previous.Entries.Length - first);
        if (previousCount > 0)
            previous.Entries.AsSpan(first, previousCount).CopyTo(entries);
        batch.Entries.AsSpan(Math.Max(0, first - previous.Entries.Length)).CopyTo(entries.AsSpan(previousCount));
        token.ThrowIfCancellationRequested();
        return new(batch.Cursor, entries, entries.Select(entry => entry.Module).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray(), previous.LoadedCount + batch.Entries.Length,
            previous.SkippedLines + batch.SkippedLines, batch.Entries.LastOrDefault()?.Id ?? LastId,
            batch.FileLength, batch.Reset ? "文件已截断或改写，已从头重新读取。" : Notice);
    }

    public static LogFileItem[] GetFiles(string folder, CancellationToken token) =>
        new DirectoryInfo(folder).EnumerateFiles("*.jsonl")
            .Select(file => { token.ThrowIfCancellationRequested(); return file; })
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Select(file => new LogFileItem(file.FullName, file.Name)).ToArray();
}
