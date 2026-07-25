using ReactiveUI;
using ReactiveUI.SourceGenerators;
using System;
using System.Threading.Tasks;
using System.Linq;
using UniversalHost.Services;
using UniversalHost.Services.Communication;

namespace UniversalHost.ViewModels.Tools;

public partial class FaultRecordViewModel : ReactiveObject
{

    [Reactive] private double _progressVal = 0;
    [Reactive] private string _stage = "准备就绪";

    public FaultRecordViewModel() { }

    [ReactiveCommand]
    private async Task UploadFaultData()
    {
        var recordProgress = new Progress<double>(v => this.ProgressVal = v);
        var recordStage = new Progress<string>(v => this.Stage = v);
        try
        {
            var recordStatus = ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Items.First(s => s.Name == "fault_recorder_state");
            var recordData = ProjectSaveService.Instance.Settings.DeviceConfig.Symbols.Items.First(s => s.Name == "recorder_data");
            FaultRecordService faultRecordService = new FaultRecordService(recordStatus, recordData, recordProgress, recordStage);
            var path = await Task.Run(() => faultRecordService.RunFaultRecordSequence());
            NotificationService.Show("故障数据上传成功", $"保存至 {path}", NotificationType.Success);
            Serilog.Log.Information($"故障录波数据上传成功，保存至 {path}");
        }
        catch (Exception ex)
        {
            NotificationService.Show("故障数据上传失败", ex.Message, NotificationType.Warning);
            Serilog.Log.Warning($"故障数据上传失败 : {ex.Message}");
        }
    }
}
