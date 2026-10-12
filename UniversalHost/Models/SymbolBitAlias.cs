using ReactiveUI.Reactive;
using System;

namespace UniversalHost.Models;

/// <summary>
/// 由变量持有，各位监控窗口共享的位别名。
/// </summary>
public sealed class SymbolBitAlias : ReactiveObject
{
    private readonly Action _onAliasChanged;
    private string _alias = string.Empty;

    public int Index { get; }
    public string Alias
    {
        get => _alias;
        set
        {
            value ??= string.Empty;
            if (_alias == value) return;

            this.RaiseAndSetIfChanged(ref _alias, value);
            _onAliasChanged();
        }
    }

    internal SymbolBitAlias(int index, Action onAliasChanged)
    {
        Index = index;
        _onAliasChanged = onAliasChanged;
    }
}
