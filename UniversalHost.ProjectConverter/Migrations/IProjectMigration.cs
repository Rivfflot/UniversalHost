using System.IO.Compression;
using UniversalHost.Models;

namespace UniversalHost.ProjectConverter.Migrations;

// 每一步只负责相邻版本；注册表在开始写入前检查完整升级链。
internal interface IProjectMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    Task<IReadOnlyList<ProjectConversionRemoval>> ApplyAsync(ZipArchive archive, string projectPath, CancellationToken token);
}
