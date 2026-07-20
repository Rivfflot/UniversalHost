using ReactiveUI;
using ReactiveUI.SourceGenerators;
using System.Reactive;


namespace UniversalHost.ViewModels.Views;

public partial class RenameWindowViewModel : ReactiveObject
{
    [Reactive] private string _newName;

    public ReactiveCommand<Unit, string?> ConfirmCommand { get; }
    public ReactiveCommand<Unit, string?> CancelCommand { get; }

    public RenameWindowViewModel(string currentName)
    {
        _newName = currentName;

        var canConfirm = this.WhenAnyValue(
           x => x.NewName,
           name => !string.IsNullOrWhiteSpace(name)
            );

        // 确认时返回新名称
        ConfirmCommand = ReactiveCommand.Create(() => (string?)NewName, canConfirm);

        // 取消时返回 null
        CancelCommand = ReactiveCommand.Create(() => (string?)null);
    }


}
