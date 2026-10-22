using System.Text.Json.Serialization;

namespace UniversalHost.Models;

// 主程序和独立转换器仅共享格式契约，不共享迁移实现或界面依赖。
public sealed class ProjectMetadata
{
    public const int CurrentFormatVersion = 2;
    public const int MinimumFormatVersion = 1;

    [JsonRequired]
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string AppVersion { get; init; } = "未知";
}
