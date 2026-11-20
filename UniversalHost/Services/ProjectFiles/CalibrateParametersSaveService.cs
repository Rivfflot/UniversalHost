using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Symbols;

namespace UniversalHost.Services;

public sealed record CalibrateParametersLoadResult(int LoadedCount, int SkippedCount, IReadOnlyList<string> Messages);

public static class CalibrateParametersSaveService
{
    private sealed record CalibrateParameter
    {
        public required string FirmwareName { get; init; }
        public required string RootSymbol { get; init; }
        public required AccessMode Mode { get; init; }
        public required string MemberPath { get; init; }
        public required uint Offset { get; init; }
        public required ScalarType DataType { get; init; }
        public required int StorageBits { get; init; }
        public string Name { get; init; } = "";
        public string Alias { get; init; } = "";
        public required string ValueString { get; init; }

        [JsonIgnore]
        public ParameterKey Key => new(FirmwareName.ToUpperInvariant(), RootSymbol,
            Mode, MemberPath, Offset, DataType, StorageBits);
    }

    // 文件名忽略大小写；根符号和成员名保持大小写敏感。地址和变量 GUID 不参与匹配。
    private readonly record struct ParameterKey(string FirmwareName, string RootSymbol, AccessMode Mode,
        string MemberPath, uint Offset, ScalarType DataType, int StorageBits);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter<AccessMode>(allowIntegerValues: false),
            new JsonStringEnumConverter<ScalarType>(allowIntegerValues: false) }
    };

    private static string FileName(string path) => Path.GetFileName(path.Replace('\\', '/'));

    private static CalibrateParameter CreateParameter(SymbolRuntime runtime)
    {
        var symbol = runtime.Symbol;
        return new()
        {
            FirmwareName = FileName(symbol.SourceKey.Length > 0 ? symbol.SourceKey : symbol.SourceFileName),
            RootSymbol = !string.IsNullOrWhiteSpace(symbol.Definition?.LinkageName)
                ? symbol.Definition.LinkageName : symbol.RootName,
            Mode = symbol.Mode,
            MemberPath = symbol.MemberPath,
            Offset = symbol.Mode == AccessMode.Manual ? symbol.Offset : 0,
            DataType = symbol.DataType,
            StorageBits = symbol.StorageBits,
            Name = symbol.Name,
            Alias = symbol.Alias,
            ValueString = runtime.ValueString ?? ""
        };
    }

    private static void Validate(CalibrateParameter? parameter)
    {
        if (parameter == null || string.IsNullOrWhiteSpace(parameter.FirmwareName)
            || parameter.FirmwareName != FileName(parameter.FirmwareName)
            || string.IsNullOrWhiteSpace(parameter.RootSymbol) || parameter.MemberPath == null
            || !Enum.IsDefined(parameter.Mode) || !Enum.IsDefined(parameter.DataType)
            || parameter.DataType == ScalarType.Unknown || parameter.StorageBits is not (8 or 16 or 32 or 64)
            || (parameter.Mode == AccessMode.Automatic && parameter.Offset != 0)
            || string.IsNullOrWhiteSpace(parameter.ValueString))
            throw new InvalidDataException("参数文件包含缺失或无效的变量信息。");
    }

    public static Task<string> SaveAsync()
    {
        // 在发起操作时固定输出目录和变量集合，后台任务不再读取当前工程路径。
        string projectPath = ProjectSaveService.Instance.ProjectFilePath;
        var runtimes = SymbolRuntimeService.CalibrateSymbolRuntimesSource.Items.ToArray();
        return Task.Run(() => SaveCoreAsync(projectPath, runtimes));
    }

    private static async Task<string> SaveCoreAsync(string projectPath, SymbolRuntime[] runtimes)
    {
        var parameters = new List<CalibrateParameter>();
        var encodedValues = new Dictionary<ParameterKey, byte[]>();
        foreach (var runtime in runtimes.Where(item => !string.IsNullOrWhiteSpace(item.ValueString)))
        {
            var parameter = CreateParameter(runtime);
            Validate(parameter);
            var encoded = runtime.EncodeValue(parameter.ValueString);
            if (encodedValues.TryGetValue(parameter.Key, out var previous) && !previous.AsSpan().SequenceEqual(encoded))
                throw new InvalidOperationException("同一变量存在不同的参数值，请统一重复变量的值后再保存。");
            encodedValues[parameter.Key] = encoded;
            parameters.Add(parameter);
        }
        if (parameters.Count == 0) throw new InvalidOperationException("无可保存参数");
        var groups = parameters.GroupBy(parameter => parameter.Key).ToArray();

        string savePath = Path.Combine(Path.GetDirectoryName(projectPath)!,
            $"parameters_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        await using var stream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await JsonSerializer.SerializeAsync(stream, groups.Select(group => group.First()).ToArray(), _jsonOptions)
            .ConfigureAwait(false);
        return savePath;
    }

    public static Task<CalibrateParametersLoadResult> LoadAsync(string path)
    {
        return Task.Run(() => LoadCoreAsync(path));
    }

    private static async Task<CalibrateParametersLoadResult> LoadCoreAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var parameters = await JsonSerializer.DeserializeAsync<CalibrateParameter[]>(stream, _jsonOptions)
            .ConfigureAwait(false);
        if (parameters is not { Length: > 0 }) throw new InvalidDataException("参数文件格式错误或没有参数。");
        foreach (var parameter in parameters) Validate(parameter);

        var runtimes = SymbolRuntimeService.CalibrateSymbolRuntimesSource.Items.ToArray();
        var lookup = runtimes.ToLookup(runtime => CreateParameter(runtime).Key);
        int loaded = 0, skipped = 0;
        List<string> messages = [];
        foreach (var group in parameters.GroupBy(parameter => parameter.Key))
        {
            var parameter = group.First();
            string label = $"{parameter.FirmwareName} : {parameter.RootSymbol}{parameter.MemberPath}";
            string? reason = null;
            var matches = lookup[group.Key].ToArray();
            if (matches.Length == 0) reason = "未找到来源、根符号、访问位置和类型完全匹配的标定变量";
            else
            {
                try
                {
                    var encoded = matches[0].EncodeValue(parameter.ValueString);
                    if (group.Any(item => !encoded.AsSpan().SequenceEqual(matches[0].EncodeValue(item.ValueString))))
                        reason = "文件中同一变量的参数值冲突";
                    // 先验证所有重复配置，避免部分填值；仅编码校验，不向设备写入。
                    foreach (var runtime in matches) runtime.EncodeValue(parameter.ValueString);
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or InvalidOperationException)
                {
                    reason = $"参数值无效：{ex.Message}";
                }
            }
            if (reason != null)
            {
                skipped += group.Count();
                messages.Add($"{label}：{reason}");
                continue;
            }
            // 仅修改普通模型的标量属性；Avalonia 绑定负责将控件刷新调度到 UI 线程。
            // 不在后台修改窗口、控件或绑定集合。
            foreach (var runtime in matches) runtime.ValueString = parameter.ValueString;
            loaded += matches.Length;
        }
        return new(loaded, skipped, messages);
    }
}
