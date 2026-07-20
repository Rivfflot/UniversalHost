using Avalonia.Media;
using DynamicData;
using DynamicData.Kernel;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.ViewModels.Views;

namespace UniversalHost.ViewModels.Documents;

public partial class BitsMonitorLayout : ReactiveObject
{
    private static readonly string ZeroString = "0";
    private static readonly string OneString = "1";
    private static readonly IBrush DefaultBackground = Brushes.Transparent;
    private static readonly IBrush ActiveBackground = Brush.Parse("rgba(255,165,0,0.4)");
    public partial class BitStyle : ReactiveObject
    {
        [Reactive] private string _name = "";
        [Reactive] private string _alias = "";
        private string _valueString = ZeroString;
        [JsonIgnore]
        public string ValueString
        {
            get => _valueString;
            set
            {
                if (_valueString != value)
                {
                    this.RaiseAndSetIfChanged(ref _valueString, value);
                    this.RaisePropertyChanged(nameof(BackgroundBrush));
                }
            }
        }
        [JsonIgnore] public IBrush BackgroundBrush => _valueString == OneString ? ActiveBackground : DefaultBackground;
        public BitStyle() { }
        public BitStyle(string name) { _name = name; }
    }
    public partial class BitsMonitorSymbol : ReactiveObject
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        [JsonIgnore] public SymbolRuntime Runtime { get; } = new SymbolRuntime<byte>(new UserSymbolInfo(), 1);

        public ObservableCollection<BitStyle> Bits { get; init; } = new ObservableCollection<BitStyle>();
        [Reactive] private bool _isExpanded = true;
        [JsonIgnore]
        public double PanelHeight =>
            Bits.Count switch
            {
                8 => 308.7 * 0.5,
                _ => 308.7
            };
        public BitsMonitorSymbol() { }
        public BitsMonitorSymbol(SymbolRuntime runtime)
        {
            Runtime = runtime;
            Id = runtime.Symbol.Id;
            Bits = [];
            for (int i = 0; i < (runtime.ValueSizeInBytes * 8); i++)
            {
                Bits.Add(new BitStyle($"Bit {i}"));
            }
        }
        public void RefreshBits(Span<byte> buffer)
        {
            Runtime.GetBitsValue(buffer.Slice(0, Bits.Count));
            for (int i = 0; i < Bits.Count; i++)
            {
                Bits[i].ValueString = buffer[i] == 0 ? ZeroString : OneString;
            }
        }
    }
    [JsonIgnore] private readonly ObservableCollection<BitsMonitorSymbol> _bitsMonitorSymbols = new([]);
    [JsonIgnore] public ObservableCollection<BitsMonitorSymbol> BitsMonitorSymbols => _bitsMonitorSymbols;

    [JsonPropertyName("BitsMonitorSymbols")]
    public List<BitsMonitorSymbol> BitsSourceStorage
    {
        get => BitsMonitorSymbols.ToList(); // 保存时：从 SourceList 转换到 List
        set
        {
            _bitsMonitorSymbols!.AddRange(value
                        .Select(storage =>
                        {
                            var lookup = SymbolRuntimeService
                                .MonitorSymbolRuntimesSource
                                .Lookup(storage.Id);

                            if (!lookup.HasValue)
                                return null;

                            var item = new BitsMonitorSymbol(lookup.Value)
                            {
                                Bits = storage.Bits,
                                IsExpanded = storage.IsExpanded,
                            };

                            int oldBitNum = item.Bits.Count;
                            int newBitNum = lookup.Value.ValueSizeInBytes * 8;
                            if (oldBitNum > newBitNum)
                            {
                                for (int i = newBitNum; i < oldBitNum; i++)
                                {
                                    item.Bits.RemoveAt(newBitNum);
                                }
                            }
                            else if (newBitNum > oldBitNum)
                            {
                                for (int i = oldBitNum; i < newBitNum; i++)
                                {
                                    item.Bits.Add(new BitsMonitorLayout.BitStyle($"Bit {i}"));
                                }
                            }
                            return item;
                        })
                        .Where(x => x != null)!);
        }
    }
    [Reactive] private bool _isNameVisible = true;
    [Reactive] private bool _isAliasVisible = true;
    [Reactive] private bool _isValueVisible = true;
};
public partial class BitsMonitorViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = [];

    // 保存和恢复布局使用 Document Id
    public readonly string Id;
    public BitsMonitorLayout Layout { get; init; }
    [Reactive] private BitsMonitorLayout.BitsMonitorSymbol? _selectedBitsMonitorSymbol;
    private static readonly byte[] _buffer = new byte[64];

    public BitsMonitorViewModel(string id) : this(id, new BitsMonitorLayout()) { }
    public BitsMonitorViewModel(string id, BitsMonitorLayout layout)
    {
        Id = id;
        Layout = layout;

        //源移除变量时移除此窗口的相应变量
        SymbolRuntimeService.MonitorSymbolRuntimesSource.Connect()
                    .OnItemRemoved(removed =>
                    {
                        var toRemove = Layout.BitsMonitorSymbols
                            .FirstOrDefault(x => x.Id == removed.Symbol.Id);

                        if (toRemove != null)
                            Layout.BitsMonitorSymbols.Remove(toRemove);
                    })
                    .Subscribe()
                    .DisposeWith(_disposables);

        SymbolRuntimeService.MonitorSymbolRuntimesSource.Connect()
                  .OnItemUpdated((current, _) =>
                  {
                      var oldSymbol = Layout.BitsMonitorSymbols.FirstOrDefault(x => x.Id == current.Symbol.Id);

                      if (oldSymbol != null)
                      {
                          int oldBitNum = oldSymbol.Bits.Count;
                          int newBitNum = current.ValueSizeInBytes * 8;
                          if (oldBitNum > newBitNum)
                          {
                              for (int i = newBitNum; i < oldBitNum; i++)
                              {
                                  oldSymbol.Bits.RemoveAt(newBitNum);
                              }
                          }
                          else if (newBitNum > oldBitNum)
                          {
                              for (int i = oldBitNum; i < newBitNum; i++)
                              {
                                  oldSymbol.Bits.Add(new BitsMonitorLayout.BitStyle($"Bit {i}"));
                              }
                          }

                          var newSymbol = new BitsMonitorLayout.BitsMonitorSymbol(current)
                          {
                              Bits = oldSymbol.Bits,
                          };
                          Layout.BitsMonitorSymbols.Replace(oldSymbol, newSymbol);
                      }
                  })
                  .Subscribe()
                  .DisposeWith(_disposables);

        Observable.Interval(TimeSpan.FromMilliseconds(200))
                    .ObserveOn(AvaloniaScheduler.Instance)
                    .Subscribe(_ => RefreshAllBits())
                    .DisposeWith(_disposables);
    }
    public bool Contains(Guid symbolId)
    {
        return Layout.BitsMonitorSymbols.Any(x => x.Id == symbolId);
    }
    public void AddOrRemoveSymbol(Guid symbolId)
    {
        SymbolRuntimeService.MonitorSymbolRuntimesSource.Lookup(symbolId).IfHasValue(runtime =>
        {
            var exist = Layout.BitsMonitorSymbols.FirstOrDefault(s => s.Id == symbolId);
            // Remove
            if (exist != null)
            {
                Layout.BitsMonitorSymbols.Remove(exist);
            }
            // Add
            else
            {
                var newMonitorBits = new BitsMonitorLayout.BitsMonitorSymbol(runtime);
                Layout.BitsMonitorSymbols.Add(newMonitorBits);
            }

        });
    }
    private void RefreshAllBits()
    {
        foreach (var item in Layout.BitsMonitorSymbols)
        {
            item.RefreshBits(_buffer);
        }
    }
    /// <summary>
    /// 手动添加变量的按钮命令
    /// </summary>
    [ReactiveCommand]
    private async Task OpenAddDisplaySymbolWindow()
    {
        SelectWindowViewModel.Instance.UpdateDocumentId(Id);
        UniversalHost.Views.Views.SelectSymbolWindow.Window.Show();
        UniversalHost.Views.Views.SelectSymbolWindow.Window.Activate();
    }
    /// <summary>
    /// 手动删除选中变量的按钮命令
    /// </summary>
    [ReactiveCommand]
    private void RemoveSelectedSymbol(BitsMonitorLayout.BitsMonitorSymbol symbol)
    {
        Layout.BitsMonitorSymbols.Remove(symbol);
    }

    [ReactiveCommand]
    void ClearSelectedSymbols()
    {
        Layout.BitsMonitorSymbols.Clear();
    }
    public void Dispose()
    {
        _disposables.Dispose();
    }
}
