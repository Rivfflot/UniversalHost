using DynamicData;
using ELFSharp.ELF;
using ELFSharp.ELF.Sections;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using UniversalHost.Services;

namespace UniversalHost.Models;

public partial class DeviceConfig : ReactiveObject
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
    [JsonPropertyName("Symbols")]
    public List<SymbolInfo> SymbolsStorage
    {
        get => Symbols.Items.ToList(); // 保存时
        set // 读取时
        {
            Symbols.Edit(list =>
            {
                list.Clear();
                list.AddRange(value);
            });
        }
    }
    public uint ReadElfFile(string path)
    {
        var elf = ELFReader.Load(path);
        var sourceFileName = System.IO.Path.GetFileName(path);
        uint addSymbolCount = 0;
        foreach (var section in elf.Sections)
        {
            if (section.Type != SectionType.SymbolTable)
                continue;
            // 针对 32 位 ELF
            if (elf.Class == Class.Bit32)
            {
                var symbol_table = (SymbolTable<uint>)section;
                foreach (var symbol in symbol_table.Entries)
                {
                    if (string.IsNullOrEmpty(symbol.Name) || symbol.Name.Contains('.') || symbol.Name.StartsWith("$"))
                    {
                        continue;
                    }
                    //仅加入全局变量
                    //if (symbol.Binding != SymbolBinding.Global)
                    //{
                    //    continue;
                    //}

                    if (symbol.Type == SymbolType.Object || symbol.Type == SymbolType.NotSpecified)
                    {
                        this.Symbols.Add(new SymbolInfo
                        {
                            SourceFileName = sourceFileName,
                            Name = symbol.Name,
                            Address = symbol.Value,
                            Size = symbol.Size
                        });
                        addSymbolCount++;
                    }
                }
            }
            // 针对 64 位 ELF
            else if (elf.Class == Class.Bit64)
            {
                //var symbol_table = (SymbolTable<ulong>)section;
                //foreach (var symbol in symbol_table.Entries)
                //{
                //    if (symbol.Name.Contains('.'))
                //    {
                //        continue;
                //    }
                //    if (symbol.Type == SymbolType.Object || symbol.Type == SymbolType.NotSpecified)
                //    {
                //        if (symbol.Size == 0)
                //        {
                //            continue;
                //        }
                //        this.Symbols.Add(new SymbolInfo
                //        {
                //            Name = symbol.Name,
                //            Address = symbol.ValueString,
                //            Size = symbol.Size
                //        });
                //        addSymbolCount++;
                //    }
                //}
            }
            else
            {
                throw new InvalidDataException("ELF错误：类型未知");
            }
        }
        return addSymbolCount;
    }
    public void ReloadSymbolTable(string projectFilePath)
    {
        this.Symbols.Clear();
        foreach (var path in this.SymbolFilePaths.Items)
        {
            ReadElfFile(ProjectFilePathService.ResolvePath(path, projectFilePath));
        }
    }
}
