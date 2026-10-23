using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using UniversalHost.Models;
using UniversalHost.ProjectConverter.Migrations;

namespace UniversalHost.ProjectConverter;

public static class ProjectConversion
{
    public static string AppVersion { get; } = typeof(ProjectConversion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "未知";

    private static readonly IProjectMigration[] Migrations = [new Version1To2Migration()];

    public static async Task<ProjectConversionResult> ConvertAsync(string path, CancellationToken token = default)
    {
        path = Path.GetFullPath(path);
        // 独占跨进程迁移锁；崩溃后只留下无锁文件，下次仍可重试。
        var lockPath = path + ".conversion.lock";
        using var conversionLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            int originalVersion;
            byte[] originalHash;
            var removedSymbols = new List<ProjectConversionRemoval>();
            // 原文件始终以只读方式打开，所有步骤均在同目录临时副本中执行。
            await using (var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            {
                using (var archive = new ZipArchive(original, ZipArchiveMode.Read, leaveOpen: true))
                {
                    var metadata = await ProjectJson.ReadAsync(archive, "metadata.json", token);
                    originalVersion = metadata.Deserialize<ProjectMetadata>()?.FormatVersion
                        ?? throw new InvalidDataException("工程元信息为空。");
                }
                var steps = GetMigrationChain(originalVersion);
                if (steps.Count == 0) return new(originalVersion, originalVersion, null);
                original.Position = 0;
                originalHash = await SHA256.HashDataAsync(original, token);
                original.Position = 0;
                await using var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true);
                await original.CopyToAsync(temporary, token);
                temporary.Position = 0;
                using (var archive = new ZipArchive(temporary, ZipArchiveMode.Update, leaveOpen: true))
                {
                    var metadata = await ProjectJson.ReadAsync(archive, "metadata.json", token);
                    foreach (var step in steps)
                    {
                        token.ThrowIfCancellationRequested();
                        removedSymbols.AddRange(await step.ApplyAsync(archive, path, token));
                        metadata["FormatVersion"] = step.ToVersion;
                    }
                    metadata["AppVersion"] = AppVersion;
                    await ProjectJson.WriteAsync(archive, "metadata.json", metadata, token);
                }
                await temporary.FlushAsync(token);
                temporary.Flush(flushToDisk: true);
            }

            // 防止迁移期间其他程序保存了工程；失败时不能覆盖用户的新内容。
            await using var checkedOriginal = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, true);
            var currentHash = await SHA256.HashDataAsync(checkedOriginal, token);
            if (!originalHash.AsSpan().SequenceEqual(currentHash))
                throw new IOException("转换期间工程被其他程序修改，请关闭其他程序后重试。");
            token.ThrowIfCancellationRequested();
            var backupPath = path + $"_v{originalVersion}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_backup";
            // 同卷原子替换并备份；不使用先删除原文件再移动的降级路径。
            File.Replace(temporaryPath, path, backupPath);
            return new(originalVersion, ProjectMetadata.CurrentFormatVersion, backupPath) { RemovedSymbols = removedSymbols };
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static List<IProjectMigration> GetMigrationChain(int version)
    {
        if (version < ProjectMetadata.MinimumFormatVersion)
            throw new InvalidDataException("仅兼容含元数据的格式 1 及以上工程。");
        if (version > ProjectMetadata.CurrentFormatVersion)
            throw new InvalidDataException($"工程格式 {version} 高于转换器支持的格式 {ProjectMetadata.CurrentFormatVersion}，请更新程序。");
        var steps = new List<IProjectMigration>();
        while (version < ProjectMetadata.CurrentFormatVersion)
        {
            var candidates = Migrations.Where(m => m.FromVersion == version).ToArray();
            if (candidates.Length != 1 || candidates[0].ToVersion != version + 1)
                throw new InvalidDataException($"缺少唯一的工程格式 {version} → {version + 1} 转换步骤。");
            steps.Add(candidates[0]);
            version = candidates[0].ToVersion;
        }
        return steps;
    }
}
