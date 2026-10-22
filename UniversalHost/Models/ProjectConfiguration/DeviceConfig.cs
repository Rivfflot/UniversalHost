using DynamicData;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using UniversalHost.Services;
using UniversalHost.Symbols;

namespace UniversalHost.Models;

public partial class DeviceConfig : ReactiveObject, IJsonOnDeserialized
{
    [Reactive] private CommunicationMode _mode = CommunicationMode.UDP;
    [Reactive] private byte _deviceID = 0x00;
    [JsonIgnore] public SourceList<string> SymbolFilePaths { get; } = new();
    [JsonPropertyName("SymbolFilePaths")] // 确保 JSON 中的名称保持不变
    public List<string> SymbolFilePathsStorage
    {
        get => SymbolFilePaths.Items.ToList(); // 保存时：从 SourceList 转换到 List
        set // 读取时：用 List 的数据填充 SourceList
        {
            SymbolFilePaths.Edit(list =>
            {
                list.Clear();
                list.AddRange(value);
            });
        }
    }
    [JsonIgnore] public SourceList<SymbolInfo> Symbols { get; } = new();
    [Reactive] private List<FirmwareImage> _firmwareSnapshots = [];

    void IJsonOnDeserialized.OnDeserialized()
    {
        // 根符号只在固件快照中持久化；恢复内存列表不读取或重新解析固件。
        Symbols.Edit(list =>
        {
            list.Clear();
            list.AddRange(FirmwareSnapshots.SelectMany(image => image.Symbols
                .Where(symbol => symbol.Address <= uint.MaxValue)
                .Select(symbol => FirmwareSymbolService.ToSymbolInfo(image, symbol))));
        });
    }
}
