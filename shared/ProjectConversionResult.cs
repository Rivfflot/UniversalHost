using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UniversalHost.Models;

public sealed record ProjectConversionRemoval(Guid Id, string Group, string Name, string Source, string Reason);

// 独立进程的结果契约；历史格式与迁移实现仍只属于转换器。
public sealed record ProjectConversionResult(int FromVersion, int ToVersion, string? BackupPath)
{
    [JsonRequired]
    public IReadOnlyList<ProjectConversionRemoval> RemovedSymbols { get; init; } = [];
}
