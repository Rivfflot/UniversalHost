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
        var entries = previous.Entries.Concat(batch.Entries).ToArray();
        long size = entries.Sum(entry => entry.TextBytes);
        int first = 0;
        while (entries.Length - first > MaxEntries || size > MaxTextBytes)
        {
            token.ThrowIfCancellationRequested();
            size -= entries[first++].TextBytes;
        }
        if (first > 0) entries = entries[first..];
        token.ThrowIfCancellationRequested();
        return new(batch.Cursor, entries, entries.Select(entry => entry.Module).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray(), previous.LoadedCount + batch.Entries.Length,
            previous.SkippedLines + batch.SkippedLines, batch.Entries.LastOrDefault()?.Id ?? LastId,
            batch.FileLength, batch.Reset ? "文件已截断、替换或改写，已从头重新读取。" : Notice);
    }

    public static LogFileItem[] GetFiles(string folder, CancellationToken token) =>
        new DirectoryInfo(folder).EnumerateFiles("*.jsonl")
            .Select(file => { token.ThrowIfCancellationRequested(); return file; })
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Select(file => new LogFileItem(file.FullName, file.Name)).ToArray();
}
