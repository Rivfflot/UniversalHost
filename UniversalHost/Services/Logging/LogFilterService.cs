using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using UniversalHost.Models.Logging;

namespace UniversalHost.Services.Logging;

public sealed record LogFilter(LogEventLevel? MinimumLevel, HashSet<string> Modules,
    DateTimeOffset? Start, DateTimeOffset? End);

public static partial class LogFilterService
{
    // JSONL 时间必须包含偏移量，不能依赖读取机器的本地时区。
    [GeneratedRegex(@"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OffsetSuffix();

    public static bool TryParseTime(string text, out DateTimeOffset time)
    {
        time = default;
        return OffsetSuffix().IsMatch(text.Trim()) && DateTimeOffset.TryParse(text,
            CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out time);
    }

    // 日期控件自身可能带本机偏移量；只取日历日期，再使用日志偏移量组合。
    public static DateTimeOffset? FromSelection(DateTimeOffset? date, TimeSpan? clock, TimeSpan offset, bool endOfSecond = false)
    {
        if (date == null || clock == null || clock < TimeSpan.Zero || clock >= TimeSpan.FromDays(1)) return null;
        var seconds = TimeSpan.FromTicks(clock.Value.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
        var value = DateTime.SpecifyKind(date.Value.Date + seconds, DateTimeKind.Unspecified);
        if (endOfSecond) value = value.AddTicks(TimeSpan.TicksPerSecond - 1);
        try { return new DateTimeOffset(value, offset); }
        catch (ArgumentException) { return null; }
    }

    public static LogEntry[] Apply(LogEntry[] entries, LogFilter filter, CancellationToken token)
    {
        var result = new List<LogEntry>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (filter.MinimumLevel is { } minimum &&
                (!Enum.TryParse<LogEventLevel>(entry.Level, out var level) || level < minimum)) continue;
            if (filter.Modules.Count != 0 && !filter.Modules.Contains(entry.Module)) continue;
            if (filter.Start.HasValue && entry.Timestamp < filter.Start.Value) continue;
            if (filter.End.HasValue && entry.Timestamp > filter.End.Value) continue;
            result.Add(entry);
        }
        return result.ToArray();
    }

    public static string FormatDetails(LogEntry entry)
    {
        using var document = JsonDocument.Parse(entry.RawJson);
        var root = document.RootElement;
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string exception = root.TryGetProperty("Exception", out var error)
            ? error.ValueKind == JsonValueKind.String ? error.GetString() ?? "" : error.GetRawText() : "(无)";
        string properties = root.TryGetProperty("Properties", out var values) ? JsonSerializer.Serialize(values, options) : "{}";
        return $"时间：{entry.TimeText}\n等级：{entry.Level}\n来源模块：{entry.Module}\n文件行号：{entry.LineNumber}\n\n消息：\n{entry.Message}\n\n异常：\n{exception}\n\n结构化属性：\n{properties}\n\n原始 JSON：\n{entry.RawJson}";
    }
}
