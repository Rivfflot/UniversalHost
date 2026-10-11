using System.Text.Json.Serialization;

namespace UniversalHost.Models;

public sealed class ProjectMetadata
{
    // 仅在工程文件结构或字段含义发生不兼容变化时递增。
    public const int CurrentFormatVersion = 1;

    [JsonRequired]
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string AppVersion { get; init; } = "未知";
}
