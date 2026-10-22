using ReactiveUI.Reactive;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using UniversalHost.Models;
using UniversalHost.Services;

namespace UniversalHost.ViewModels.Windows;

public sealed class SymbolNumberInputViewModel : ReactiveObject
{
    private long? _value;
    private readonly Action _changed;
    public long Minimum { get; }
    public long Maximum { get; }
    public string Label { get; }
    public bool HasLabel => Label.Length > 0;
    public string Hint { get; }
    public double Width { get; }
    public long? Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            this.RaiseAndSetIfChanged(ref _value, value);
            _changed();
        }
    }
    public SymbolNumberInputViewModel(long value, long minimum, long maximum, string label, string hint, double width, Action changed)
    {
        _value = value;
        Minimum = minimum;
        Maximum = maximum;
        Label = label;
        Hint = $"{hint}：{minimum} ～ {maximum}";
        Width = width;
        _changed = changed;
    }
}

public sealed class SymbolInputViewModel : ReactiveObject, IDisposable
{
    private readonly UserSymbolInfo _symbol;
    private readonly ProjectSettings _settings;
    private string _kind = "偏移", _error = "";
    private bool _applying;
    private IReadOnlyList<SymbolNumberInputViewModel> _inputs = [];
    public static string[] Choices { get; } = ["偏移", "下标"];
    public IReadOnlyList<SymbolNumberInputViewModel> Inputs
    {
        get => _inputs;
        private set => this.RaiseAndSetIfChanged(ref _inputs, value);
    }
    public string Error
    {
        get => _error;
        private set { this.RaiseAndSetIfChanged(ref _error, value); this.RaisePropertyChanged(nameof(HasError)); }
    }
    public bool HasError => Error.Length > 0;
    public bool CanEditInput => SymbolSelectionService.CanEditInput(_settings.DeviceConfig, _symbol);
    public string Kind
    {
        get => _kind;
        set
        {
            if (!CanEditInput || value == _kind || value is not ("偏移" or "下标")) return;
            this.RaiseAndSetIfChanged(ref _kind, value);
            RefreshInputs();
            TryApply();
        }
    }
    private SymbolInputKind InputKind => Kind == "下标" ? SymbolInputKind.ArrayIndex : SymbolInputKind.Offset;
    public SymbolInputViewModel(UserSymbolInfo symbol)
    {
        _symbol = symbol;
        _settings = ProjectSaveService.Instance.Settings;
        Refresh();
        symbol.PropertyChanged += SymbolChanged;
    }
    private void SymbolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_applying && e.PropertyName is nameof(UserSymbolInfo.InputKind) or nameof(UserSymbolInfo.MemberPath)
            or nameof(UserSymbolInfo.Offset) or nameof(UserSymbolInfo.Access) or nameof(UserSymbolInfo.Definition)
            or nameof(UserSymbolInfo.Address) or nameof(UserSymbolInfo.Size)) Refresh();
    }
    private void Refresh()
    {
        _kind = _symbol.InputKind == SymbolInputKind.ArrayIndex ? "下标" : "偏移";
        this.RaisePropertyChanged(nameof(Kind));
        this.RaisePropertyChanged(nameof(CanEditInput));
        RefreshInputs();
    }
    private void RefreshInputs()
    {
        try
        {
            var values = SymbolSelectionService.GetInputValues(_settings.DeviceConfig, _symbol, InputKind);
            bool indices = InputKind == SymbolInputKind.ArrayIndex;
            Inputs = values.Select((value, index) => new SymbolNumberInputViewModel(
                value.Value, value.Minimum, value.Maximum, values.Count > 1 ? $"{index + 1}:" : "",
                indices ? $"第 {index + 1} 维下标" : "相对根变量的偏移量（目标地址单位）",
                indices ? 60 : 76, TryApply)).ToArray();
            Error = "";
        }
        catch (Exception ex) { Inputs = []; Error = ex.Message; }
    }
    private void TryApply()
    {
        if (_applying || !CanEditInput || Inputs.Count == 0 || Inputs.Any(input => !input.Value.HasValue)) return;
        _applying = true;
        try
        {
            if (!ReferenceEquals(_settings, ProjectSaveService.Instance.Settings)) throw new InvalidOperationException("工程已切换");
            if (FirmwareImportCoordinator.Instance.IsBusy) throw new InvalidOperationException("请等待固件解析完成");
            SymbolSelectionService.ApplyInput(_settings.DeviceConfig, _symbol, InputKind, Inputs.Select(input => input.Value!.Value).ToArray());
            Error = "";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { _applying = false; }
    }
    public void Dispose() => _symbol.PropertyChanged -= SymbolChanged;
}
