using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using UniversalHost.Models;

namespace UniversalHost.Services;

internal static class ProjectMetadataService
{
    private const string EntryName = "metadata.json";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task SaveAsync(ZipArchive archive)
    {
        if (archive.Mode == ZipArchiveMode.Update)
            archive.GetEntry(EntryName)?.Delete();

        var entry = archive.CreateEntry(EntryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, new ProjectMetadata { AppVersion = AppInfo.Version }, _jsonOptions);
    }

    public static ProjectMetadata Read(ZipArchive archive)
    {
        var entry = archive.GetEntry(EntryName)
            ?? throw new InvalidDataException("工程缺少元信息。");

        ProjectMetadata metadata;
        try
        {
            using var stream = entry.Open();
            metadata = JsonSerializer.Deserialize<ProjectMetadata>(stream)
                ?? throw new InvalidDataException("工程元信息为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("工程元信息格式错误。", ex);
        }

        if (metadata.FormatVersion <= 0)
            throw new InvalidDataException("工程文件格式版本无效。");
        if (metadata.FormatVersion > ProjectMetadata.CurrentFormatVersion)
        {
            var savedAppVersion = string.IsNullOrWhiteSpace(metadata.AppVersion) ? "未知" : metadata.AppVersion;
            LogService.ForModule(LogModule.Project).Warning(
                "工程文件格式版本 {FormatVersion} 高于当前支持的版本 {SupportedFormatVersion}；保存程序版本 {SavedAppVersion}，当前程序版本 {AppVersion}",
                metadata.FormatVersion, ProjectMetadata.CurrentFormatVersion, savedAppVersion, AppInfo.Version);
            throw new InvalidDataException(
                $"当前程序无法打开此工程。此工程由程序版本 {savedAppVersion} 保存，当前程序版本为 {AppInfo.Version}，请更新程序后重试。");
        }

        return metadata;
    }
}
