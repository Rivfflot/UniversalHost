using DynamicData;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System;

namespace UniversalHost.Models;

public partial class MonitorConfig : ReactiveObject
{
    [Reactive] private int _maxSaveLen = 100000;//最大存储长度
    [JsonIgnore]
    public SourceCache<UserSymbolInfo, Guid> MonitoredSymbols { get; } =
                                                 new SourceCache<UserSymbolInfo, Guid>(x => x.Id);
    [JsonPropertyName("MonitoredSymbols")] // 确保 JSON 中的名称保持不变
    public List<UserSymbolInfo> MonitoredSymbolsStorage
    {
        get => MonitoredSymbols.Items.ToList(); // 保存时
        set//读取
        {
            MonitoredSymbols.Edit(updater =>
            {
                updater.Clear();

                if (value is null)
                    return;

                updater.AddOrUpdate(value);
            });
        }
    }
    public int RemoveDuplicateSymbols()
    {
        var duplicates = MonitoredSymbols.Items
            .GroupBy(x => (x.Name, x.SourceFileName, x.Offset))
            .SelectMany(g => g.Skip(1))
            .ToList();

        if (duplicates.Count == 0)
            return duplicates.Count;

        MonitoredSymbols.Edit(updater =>
        {
            foreach (var item in duplicates)
            {
                updater.Remove(item.Id);
            }
        });

        return duplicates.Count;
    }

}

public partial class CalibrateConfig : ReactiveObject
{
    [JsonIgnore]
    public SourceCache<UserSymbolInfo, Guid> CalibratedSymbols { get; } =
                                                 new SourceCache<UserSymbolInfo, Guid>(x => x.Id);
    [JsonPropertyName("CalibratedSymbols")] // 确保 JSON 中的名称保持不变
    public List<UserSymbolInfo> CalibratedSymbolsStorage
    {
        get => CalibratedSymbols.Items.ToList(); // 保存时
        set//读取
        {
            CalibratedSymbols.Edit(updater =>
            {
                updater.Clear();

                if (value is null)
                    return;

                updater.AddOrUpdate(value);
            });
        }
    }
    public int RemoveDuplicateSymbols()
    {
        var duplicates = CalibratedSymbols.Items
            .GroupBy(x => (x.Name, x.SourceFileName, x.Offset))
            .SelectMany(g => g.Skip(1))
            .ToList();

        if (duplicates.Count == 0)
            return duplicates.Count;

        CalibratedSymbols.Edit(updater =>
        {
            foreach (var item in duplicates)
            {
                updater.Remove(item.Id);
            }
        });

        return duplicates.Count;
    }

}
