using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Reactive;
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
        public ReactiveCommand<Window, Unit> SelectIapFileCommand { get; }
        public ReactiveCommand<Unit, Unit> StartIapCommand { get; }
        public IapViewModel()
        {

            IapProgressBar = 0;

            StartIapCommand = ReactiveCommand.CreateFromTask(StartIapAsync);
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
                ProjectSaveService.Instance.Settings.IapConfig.IapFilePath = files[0].Path.LocalPath;
            }
        }
        private async Task StartIapAsync()
        {
            _iapCancellation = new CancellationTokenSource();

            //IapService
            try
            {
                var iapProgress = new Progress<double>(value => IapProgressBar = value);
                var stageProgress = new Progress<string>(value => CurrentStage = value);
                Serilog.Log.Debug("在线升级开始");
                var iapCommService = new IapService(iapProgress, stageProgress);
                await Task.Run(() => iapCommService.RunIapSequenceAsync(_iapCancellation.Token));
                CurrentStage = "升级完成";
                Serilog.Log.Information("在线升级完成");
            }
            catch (OperationCanceledException ex)
            {
                CurrentStage = "升级取消";
                NotificationService.Show("在线升级升级任务已取消", ex.Message, NotificationType.Info);
                Serilog.Log.Information($"在线升级升级任务已取消,{ex.Message}");
            }
            catch (Exception ex)
            {
                CurrentStage = "升级停止";
                NotificationService.Show("在线升级停止", ex.Message, NotificationType.Error);
                Serilog.Log.Error($"在线升级停止，{ex.Message}");
            }
            finally
            {
                IapProgressBar = 0;
                _iapCancellation.Dispose();
                _iapCancellation = null;
            }
        }
    }
}
