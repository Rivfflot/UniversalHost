using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace UniversalHost.Symbols;

public sealed class FirmwareParser(ParseOptions? options = null)
{
    private readonly ParseOptions _options = options ?? new();
    public static string HostRid => $"{(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : "unsupported")}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

    public async Task<FirmwareImage> ParseAsync(string path, CancellationToken cancellationToken = default)
    {
        var worker = _options.WorkerPath ?? Path.Combine(AppContext.BaseDirectory, "symbol-tools", HostRid,
            OperatingSystem.IsWindows() ? "symbols-worker.exe" : "symbols-worker");
        if (!Path.IsPathFullyQualified(worker)) throw new ArgumentException("解析工具路径必须为绝对路径");
        if (!File.Exists(worker)) throw new FileNotFoundException($"缺少 {HostRid} 固件解析工具，请检查发布包", worker);
        if (_options.Timeout <= TimeSpan.Zero || _options.MaxOutputBytes < 1024) throw new ArgumentOutOfRangeException(nameof(options));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var monitorCancellation = new CancellationTokenSource();
        timeout.CancelAfter(_options.Timeout);
        var start = new ProcessStartInfo(worker)
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
        using var process = new Process { StartInfo = start };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
            // Kill the isolated parser on cancellation or resource-limit failure,
            // including when its pipe is full. Always reap it before returning.
            using var registration = timeout.Token.Register(() => Kill(process));
            async Task<string> ReadPipe(StreamReader reader, int limit)
            {
                try { return await ReadBoundedAsync(reader, limit, timeout.Token).ConfigureAwait(false); }
                catch { Kill(process); throw; }
            }
            async Task<ArrayBufferWriter<byte>> ReadOutputAsync()
            {
                try { return await ReadOutputBoundedAsync(process.StandardOutput.BaseStream, _options.MaxOutputBytes, timeout.Token).ConfigureAwait(false); }
                catch { Kill(process); throw; }
            }
            var outputTask = ReadOutputAsync();
            var errorTask = ReadPipe(process.StandardError, 64 * 1024);
            var memory = MonitorMemoryAsync(process, _options.MaxWorkerMemoryBytes, monitorCancellation.Token);
            async Task WaitForExitAsync()
            {
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                finally { monitorCancellation.Cancel(); }
            }
            var exit = WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask, exit, memory).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException($"解析工具退出 {process.ExitCode}: {await errorTask.ConfigureAwait(false)}");
            timeout.Token.ThrowIfCancellationRequested();
            var raw = JsonSerializer.Deserialize<RawImage>((await outputTask.ConfigureAwait(false)).WrittenSpan)
                ?? throw new InvalidDataException("解析工具未返回结果");
            timeout.Token.ThrowIfCancellationRequested();
            if (raw.Schema != 1) throw new InvalidDataException("解析工具版本不匹配");
            return new DwarfModelBuilder(raw, timeout.Token).Build(Path.GetFullPath(path));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"固件解析超过 {_options.Timeout.TotalSeconds} 秒"); }
        finally
        {
            monitorCancellation.Cancel();
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task MonitorMemoryAsync(Process process, long limit, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                process.Refresh();
                if (process.HasExited) return;
                if (process.PrivateMemorySize64 > limit)
                {
                    Kill(process);
                    throw new InvalidDataException("解析工具内存使用超过限制");
                }
                await Task.Delay(50, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            // The worker can exit between HasExited and PrivateMemorySize64.
            // Ignore only that confirmed exit; keep other monitor faults fatal.
            catch (InvalidOperationException) when (process.HasExited) { return; }
            catch { Kill(process); throw; }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder();
        char[] buffer = new char[8192];
        int bytes = 0;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return text.ToString();
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count)));
            if (bytes > limit) throw new InvalidDataException("解析工具输出超过资源限制");
            text.Append(buffer, 0, count);
        }
    }

    private static async Task<ArrayBufferWriter<byte>> ReadOutputBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Min(64 * 1024, limit));
        while (true)
        {
            // Probe EOF separately at the exact limit, without doubling the
            // backing buffer just to discover EOF or a single excess byte.
            if (buffer.WrittenCount == limit)
            {
                if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("解析工具输出超过资源限制");
                return buffer;
            }
            int request = Math.Min(64 * 1024, limit - buffer.WrittenCount);
            int count = await stream.ReadAsync(buffer.GetMemory(request)[..request], token).ConfigureAwait(false);
            if (count == 0) return buffer;
            if (count > limit - buffer.WrittenCount) throw new InvalidDataException("解析工具输出超过资源限制");
            buffer.Advance(count);
        }
    }
}

internal sealed class RawImage
{
    public int Schema { get; set; }
    public string Backend { get; set; } = "";
    public string Format { get; set; } = "";
    public string Platform { get; set; } = "";
    public string ABI { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int AddressUnitBits { get; set; }
    public int AddressBits { get; set; }
    public bool LittleEndian { get; set; }
    public bool Linked { get; set; }
    public bool SymbolsComplete { get; set; }
    public bool TypesComplete { get; set; }
    public List<RawSymbol> Symbols { get; set; } = [];
    public List<string>? ExcludedSymbolNames { get; set; } = [];
    public List<RawNode>? Nodes { get; set; }
    public Dictionary<string, string> Signatures { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
    public List<int>? DwarfVersions { get; set; }
}
internal sealed record RawSymbol(string Name, ulong Address, ulong Size);
internal sealed class RawNode
{
    public string ID { get; set; } = "";
    public uint Tag { get; set; }
    public int Version { get; set; }
    public bool HasMemberLocation { get; set; }
    public Dictionary<uint, RawAttribute> Attributes { get; set; } = [];
    public List<string> Children { get; set; } = [];
    public ulong? Location { get; set; }
    public bool HasLocation { get; set; }
    public ulong? MemberOffset { get; set; }
}
internal sealed class RawAttribute
{
    public string Text { get; set; } = "";
    public long? Number { get; set; }
    public string Ref { get; set; } = "";
}
