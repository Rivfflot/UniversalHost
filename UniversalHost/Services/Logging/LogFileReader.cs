using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models.Logging;

namespace UniversalHost.Services.Logging;

// 游标是一次读取的快照。调用方仅在结果仍有效时接受新游标，取消/暂停不会漏读。
public sealed record LogReadCursor(long Position, long LineNumber,
    byte[] Pending, bool DiscardingLongLine, byte[] Anchor)
{
    public static LogReadCursor Empty { get; } = new(0, 0, [], false, []);
}

public sealed record LogReadBatch(LogReadCursor Cursor, LogEntry[] Entries,
    long SkippedLines, long FileLength, bool Reset, bool Changed);

public static class LogFileReader
{
    public const int MaxLineBytes = 1024 * 1024;
    public const int MaxReadBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<LogReadBatch> ReadAsync(string path, LogReadCursor previous,
        long lastId, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = file.Length;
        bool reset = previous.Position > 0 &&
            (length < previous.Position ||
             !await AnchorMatchesAsync(file, previous, token).ConfigureAwait(false));
        var cursor = reset ? LogReadCursor.Empty : previous;
        file.Position = cursor.Position;
        using var line = new MemoryStream();
        line.Write(cursor.Pending);
        bool discarding = cursor.DiscardingLongLine;
        long lineNumber = cursor.LineNumber;
        long skipped = 0;
        long position = cursor.Position;
        long end = Math.Min(length, position + MaxReadBytes);
        List<LogEntry> entries = [];
        var buffer = new byte[65536];

        while (position < end)
        {
            token.ThrowIfCancellationRequested();
            int count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position)), token)
                .ConfigureAwait(false);
            if (count == 0) break;
            position += count;
            int start = 0;
            while (start < count)
            {
                token.ThrowIfCancellationRequested();
                int newline = Array.IndexOf(buffer, (byte)'\n', start, count - start);
                int stop = newline < 0 ? count : newline;
                if (!discarding)
                {
                    if (line.Length + stop - start > MaxLineBytes)
                    {
                        discarding = true;
                        line.SetLength(0);
                    }
                    else line.Write(buffer, start, stop - start);
                }
                if (newline >= 0)
                {
                    lineNumber++;
                    if (discarding) skipped++;
                    else
                    {
                        var entry = ParseLine(line.GetBuffer().AsSpan(0, (int)line.Length), lastId + 1, lineNumber);
                        if (entry == null) skipped++;
                        else
                        {
                            entries.Add(entry);
                            lastId++;
                        }
                    }
                    line.SetLength(0);
                    discarding = false;
                }
                start = stop + 1;
            }
        }

        // 保存游标之前的少量字节，可识别原地截断后迅速增长到旧长度以上的文件。
        int anchorLength = (int)Math.Min(128, position);
        var anchor = new byte[anchorLength];
        file.Position = position - anchorLength;
        await file.ReadExactlyAsync(anchor, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new(new(position, lineNumber, line.ToArray(), discarding, anchor),
            entries.ToArray(), skipped, length, reset, reset || position != previous.Position);
    }

    private static async Task<bool> AnchorMatchesAsync(FileStream file, LogReadCursor cursor, CancellationToken token)
    {
        file.Position = cursor.Position - cursor.Anchor.Length;
        var actual = new byte[cursor.Anchor.Length];
        int read = 0;
        while (read < actual.Length)
        {
            int count = await file.ReadAsync(actual.AsMemory(read), token).ConfigureAwait(false);
            if (count == 0) return false;
            read += count;
        }
        return actual.AsSpan().SequenceEqual(cursor.Anchor);
    }

    private static LogEntry? ParseLine(ReadOnlySpan<byte> bytes, long id, long lineNumber)
    {
        try
        {
            if (lineNumber == 1 && bytes.StartsWith("\uFEFF"u8)) bytes = bytes[3..];
            string json = Utf8.GetString(bytes).TrimEnd('\r');
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Timestamp", out var time) || time.ValueKind != JsonValueKind.String ||
                !LogFilterService.TryParseTime(time.GetString()!, out var timestamp) ||
                !root.TryGetProperty("Level", out var level) || level.ValueKind != JsonValueKind.String)
                return null;
            string message = GetString(root, "RenderedMessage") ?? GetString(root, "MessageTemplate") ?? "";
            string module = "(未指定)";
            if (root.TryGetProperty("Properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                module = GetString(properties, "SourceContext") ?? module;
            return new(id, lineNumber, timestamp, level.GetString()!, module, message, json);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or FormatException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
}
