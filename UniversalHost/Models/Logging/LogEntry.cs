using Serilog.Events;
using System;
using System.Globalization;

namespace UniversalHost.Models.Logging;

public sealed record LogEntry(long Id, long LineNumber, DateTimeOffset Timestamp,
    string Level, string Module, string Message, string RawJson)
{
    public bool IsWarning => Level == "Warning";
    public bool IsError => Level is "Error" or "Fatal";
    public string TimeText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
    public string MessagePreview => Message.Length > 500
        ? Message[..500].Replace('\r', ' ').Replace('\n', ' ') + "…"
        : Message.Replace('\r', ' ').Replace('\n', ' ');

    // UTF-16 文本预算；不包括对象、数组、虚拟化控件及暂时的读取/筛选结果。
    public long TextBytes => 2L * (Level.Length + Module.Length + Message.Length + RawJson.Length);
}

public sealed record LogFileItem(string FullPath, string Name);

public sealed record LogLevelOption(string Name, LogEventLevel? Level);
