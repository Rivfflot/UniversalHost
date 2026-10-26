using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UniversalHost.Services;
using UniversalHost.ViewModels;
using UniversalHost.Views;

namespace UniversalHost
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

#if DEBUG
            this.AttachDeveloperTools();
#endif
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var startup = StartupOptions.Parse(desktop.Args);
                var viewModel = new MainWindowViewModel();
                var mainWindow = new MainWindow
                {
                    DataContext = viewModel,
                };
                desktop.MainWindow = mainWindow;
                if (startup.ProjectPath != null || startup.Error != null)
                {
                    mainWindow.Opened += async (_, _) =>
                    {
                        NotificationService.Register(mainWindow);
                        if (startup.Error != null)
                            NotificationService.Show("启动参数错误", startup.Error, NotificationType.Error);
                        else
                            await viewModel.OpenProjectAsync(startup.ProjectPath!);
                    };
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

    }
}
