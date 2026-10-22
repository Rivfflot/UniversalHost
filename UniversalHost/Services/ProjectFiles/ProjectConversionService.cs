using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Models;

namespace UniversalHost.Services;

// 主程序只识别版本并调用独立工具；所有历史结构和转换步骤均属于转换器。
internal static class ProjectConversionService
{
    public static async Task<ProjectConversionResult?> EnsureCurrentAsync(string path, CancellationToken token = default)
    {
        ProjectMetadata metadata;
        using (var archive = ZipFile.OpenRead(path))
            metadata = ProjectMetadataService.Read(archive, allowOlderVersion: true);
        if (metadata.FormatVersion == ProjectMetadata.CurrentFormatVersion) return null;

        var executable = Path.Combine(AppContext.BaseDirectory, "dependencies",
            OperatingSystem.IsWindows() ? "UniversalHost.ProjectConverter.exe" : "UniversalHost.ProjectConverter");
        if (!File.Exists(executable))
            throw new FileNotFoundException("缺少工程转换器，请恢复完整发布包后重试。", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(Path.GetFullPath(path));
        start.ArgumentList.Add("--target-version");
        start.ArgumentList.Add(ProjectMetadata.CurrentFormatVersion.ToString(CultureInfo.InvariantCulture));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var process = new Process { StartInfo = start };
        LogService.ForModule(LogModule.Project).Information("开始转换工程格式 {FromVersion} → {ToVersion}: {ProjectPath}",
            metadata.FormatVersion, ProjectMetadata.CurrentFormatVersion, path);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            process.Start();
            using var registration = timeout.Token.Register(() => Kill(process));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0)
                throw new InvalidDataException($"工程转换失败，原文件已保留: {(await error).Trim()}");
            var result = JsonSerializer.Deserialize<ProjectConversionResult>(await output)
                ?? throw new InvalidDataException("转换器未返回结果。");
            if (result.ToVersion != ProjectMetadata.CurrentFormatVersion)
                throw new InvalidDataException("转换器返回的格式版本与主程序不一致。");
            using (var archive = ZipFile.OpenRead(path))
                ProjectMetadataService.Read(archive);
            LogService.ForModule(LogModule.Project).Information("工程转换完成，原文件备份: {BackupPath}", result.BackupPath);
            if (result.RemovedSymbols.Count > 0)
                LogService.ForModule(LogModule.Project).Warning("工程转换删除 {Count} 个无法匹配的变量: {RemovedSymbols}",
                    result.RemovedSymbols.Count, string.Join(Environment.NewLine,
                        result.RemovedSymbols.Select(item => $"{item.Group}: {item.Name} ({item.Source})，{item.Reason}")));
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("工程转换超时，请检查固件文件和转换器后重试。");
        }
        finally
        {
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
