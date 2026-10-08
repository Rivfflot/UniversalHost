using System;
using System.IO;

namespace UniversalHost.Services;

/// <summary>工程资源路径以 .uhprj 所在目录为基准，与进程工作目录无关。</summary>
public static class ProjectFilePathService
{
    public static string ResolvePath(string filePath, string projectFilePath)
    {
        if (Path.IsPathFullyQualified(filePath))
            return Path.GetFullPath(filePath);

        // 拒绝依赖当前盘符或工作目录的路径，例如 D:BOOT.bin 和 \BOOT.bin。
        if (Path.IsPathRooted(filePath))
            throw new ArgumentException("请使用完整绝对路径或相对工程目录的路径", nameof(filePath));

        return Path.GetFullPath(filePath, GetProjectFolder(projectFilePath));
    }

    public static string ToStoredPath(string filePath, string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return filePath;

        var absolutePath = ResolvePath(filePath, projectFilePath);
        if (string.IsNullOrWhiteSpace(projectFilePath))
            return absolutePath;

        var relativePath = Path.GetRelativePath(GetProjectFolder(projectFilePath), absolutePath);
        if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            return absolutePath;

        return relativePath == "." ? "." : Path.Combine(".", relativePath);
    }

    public static string RebasePath(string filePath, string sourceProjectFilePath, string targetProjectFilePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return filePath;

        return ToStoredPath(ResolvePath(filePath, sourceProjectFilePath), targetProjectFilePath);
    }

    private static string GetProjectFolder(string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath))
            throw new InvalidOperationException("请先新建或打开工程，再使用相对路径");

        return Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
    }
}
