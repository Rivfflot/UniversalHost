using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalHost.ProjectConverter;

internal static class ProjectJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task<JsonObject> ReadAsync(ZipArchive archive, string name, CancellationToken token)
    {
        var entries = archive.Entries.Where(e => e.FullName == name).ToArray();
        if (entries.Length != 1) throw new InvalidDataException($"工程必须包含唯一的 {name}。");
        await using var stream = entries[0].Open();
        return await JsonNode.ParseAsync(stream, cancellationToken: token) as JsonObject
            ?? throw new InvalidDataException($"{name} 不是有效的 JSON 对象。");
    }

    public static async Task WriteAsync(ZipArchive archive, string name, JsonObject value, CancellationToken token)
    {
        archive.GetEntry(name)?.Delete();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, Options, token);
    }
}
