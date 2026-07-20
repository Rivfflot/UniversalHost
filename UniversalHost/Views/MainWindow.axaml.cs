using ReactiveUI;
using ReactiveUI.Avalonia;
using System.Reactive.Disposables.Fluent;
using UniversalHost.Services;
using UniversalHost.ViewModels;
using UniversalHost.ViewModels.Views;
using UniversalHost.Views.Views;

namespace UniversalHost.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        public MainWindow()
        {
            InitializeComponent();

            this.WhenActivated(disposables =>
            {
                // 服务会自动管理这个窗口的通知生命周期
                NotificationService.Register(this);

                if (DataContext is not MainWindowViewModel vm) return;

                vm.ShowRenameDialog.RegisterHandler(async interaction =>
                    {
                        string oldName = interaction.Input;

                        var renameVm = new RenameWindowViewModel(oldName);
                        var renameWindow = new RenameWindow { DataContext = renameVm };
                        //renameWindow.Activate();
                        string? result = await renameWindow.ShowDialog<string?>(this);

                        interaction.SetOutput(result);
                    })
                   .DisposeWith(disposables);
            });
        }
    }
}