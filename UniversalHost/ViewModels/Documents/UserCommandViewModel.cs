using DynamicData;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.Services.Communication;
using UniversalHost.ViewModels.Views;

namespace UniversalHost.ViewModels.Documents;

public record struct UserCommandLayoutSave(
    byte[] DisplayUserCommands,
    bool IsIdVisiable,
    bool IsNameVisiable
    );

public partial class UserCommandViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = [];
    private readonly SourceList<byte> _keysSourceList = new();
    public ISourceList<byte> KeysSource => _keysSourceList;

    public UserCommandLayoutSave Save => new(_keysSourceList.Items.ToArray(), IsIdVisiable, IsNameVisiable);
    // 绑定到 DataGrid 的数据源
    private readonly ReadOnlyObservableCollection<UserCommand> _displayUserCommands;
    public ReadOnlyObservableCollection<UserCommand> DisplayUserCommands => _displayUserCommands;

    [Reactive] private UserCommand? _selectedUserCommand;
    [Reactive] private bool _isIdVisiable;
    [Reactive] private bool _isNameVisiable;

    // 保存和恢复布局使用 Document Id
    public readonly string Id;

    public UserCommandViewModel(string id) : this(id, new UserCommandLayoutSave([], true, true)) { }
    // 恢复布局时调用的构造函数
    public UserCommandViewModel(string id, UserCommandLayoutSave save)
    {
        Id = id;

        _isIdVisiable = save.IsIdVisiable;
        _isNameVisiable = save.IsNameVisiable;
        _keysSourceList.AddRange(save.DisplayUserCommands);
        _keysSourceList.Connect()
                .Transform(key =>
                    ProjectSaveService.Instance.Settings.UserCommandConfig.UserCommands
                        .Lookup(key)
                        .Value)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Bind(out _displayUserCommands)
                .Subscribe()
                .DisposeWith(_disposables);
    }
    public bool Contains(byte commandId)
    {
        return KeysSource.Items.Contains(commandId);
    }
    public void AddOrRemove(byte commandId)
    {
        // 1. 如果已经存在，说明用户是在“取消勾选”，应当将其移除
        if (KeysSource.Items.Contains(commandId))
        {
            KeysSource.Remove(commandId);
        }
        // 2. 如果不存在，说明用户是在“勾选”，应当将其添加
        else
        {
            KeysSource.Add(commandId);
        }
    }
    [ReactiveCommand]
    private async Task ExecuteUserCommand(UserCommand command)
    {
        try
        {
            await XcpService.Client!.ExecuteUserCmd(command.Id);
            NotificationService.Show("指令下发成功", $"{command.Id} : {command.Name} 已成功执行", NotificationType.Success);
        }
        catch (Exception ex)
        {
            NotificationService.Show("指令下发失败", ex.Message, NotificationType.Error);
        }
    }
    [ReactiveCommand]
    private async Task OpenAddDisplaySymbolWindow()
    {
        SelectWindowViewModel.Instance.UpdateDocumentId(Id);
        UniversalHost.Views.Views.SelectSymbolWindow.Window.Show();
        UniversalHost.Views.Views.SelectSymbolWindow.Window.Activate();
    }
    /// <summary>
    /// 手动删除选中指令的按钮命令
    /// </summary>
    [ReactiveCommand]
    private void RemoveSelectedCommand()
    {
        if (_selectedUserCommand == null) return;

        _keysSourceList.Remove(_selectedUserCommand.Id);
        _selectedUserCommand = null;
    }
    [ReactiveCommand]
    void ClearSelectedCommands()
    {
        _keysSourceList.Clear();
    }
    public void Dispose()
    {
        _keysSourceList.Dispose();
        _disposables.Dispose();
    }
}
