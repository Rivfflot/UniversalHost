using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalHost.Services;
using UniversalHost.Services.Communication;

namespace UniversalHost.ViewModels.Tools
{
    public partial class IapViewModel : ReactiveObject
    {
        //窗口关闭触发，需要释放资源
        private CancellationTokenSource? _iapCancellation;

        [Reactive] private double _iapProgressBar;
        [Reactive] private string _currentStage = "准备就绪";
        [Reactive] private string _stageTimestamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
        [Reactive] private bool _isRunning;
        public ReactiveCommand<Window, Unit> SelectIapFileCommand { get; }
        public ReactiveCommand<Unit, Unit> StartIapCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelIapCommand { get; }
        public IapViewModel()
        {

            IapProgressBar = 0;

            StartIapCommand = ReactiveCommand.CreateFromTask(StartIapAsync,
                GlobalStatus.Instance.WhenAnyValue(x => x.IsIapRunning).Select(running => !running));
            CancelIapCommand = ReactiveCommand.Create(() => _iapCancellation?.Cancel(),
                this.WhenAnyValue(x => x.IsRunning));
            SelectIapFileCommand = ReactiveCommand.CreateFromTask<Window>(SelectIapFileAsync);
        }
        private async Task SelectIapFileAsync(Window window)
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 IAP 文件",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                new("BIN 文件") { Patterns = ["*.bin"] },
                new("所有文件") { Patterns = ["*"] }
                }
            });

            if (files is { Count: > 0 })
            {
                ProjectSaveService.Instance.Settings.IapConfig.IapFilePath =
                    ProjectFilePathService.ToStoredPath(files[0].Path.LocalPath, ProjectSaveService.Instance.ProjectFilePath);
            }
        }
        private async Task StartIapAsync()
        {
            _iapCancellation = new CancellationTokenSource();
            IsRunning = true;

            //IapService
            try
            {
                var iapProgress = new Progress<double>(value => IapProgressBar = value);
                var stageProgress = new Progress<string>(UpdateStage);
                LogService.ForModule(LogModule.Iap).Debug("在线升级开始");
                var iapCommService = new IapService(iapProgress, stageProgress);
                await iapCommService.RunIapSequenceAsync(_iapCancellation.Token);
                UpdateStage("升级完成");
                IapProgressBar = 100;
                LogService.ForModule(LogModule.Iap).Information("在线升级完成");
            }
            catch (OperationCanceledException ex)
            {
                UpdateStage("升级取消");
                IapProgressBar = 0;
                NotificationService.Show("在线升级任务已取消", ex.Message, NotificationType.Info);
                LogService.ForModule(LogModule.Iap).Information("在线升级任务已取消,{Reason}", ex.Message);
            }
            catch (Exception ex)
            {
                UpdateStage("升级停止");
                IapProgressBar = 0;
                NotificationService.Show("在线升级停止", ex.Message, NotificationType.Error);
                LogService.ForModule(LogModule.Iap).Error(ex, "在线升级停止，{Reason}", ex.Message);
            }
            finally
            {
                _iapCancellation.Dispose();
                _iapCancellation = null;
                IsRunning = false;
            }
        }

        private void UpdateStage(string stage)
        {
            StageTimestamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
            CurrentStage = stage;
        }
    }
}
