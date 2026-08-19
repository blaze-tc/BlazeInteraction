using Microsoft.Win32;

namespace Yuexin.Radar.Bridge.Wpf.Services;

public interface IFileDialogService
{
    string? SelectRecordingPath();
    string? SelectReplayPath();
}

public sealed class WpfFileDialogService : IFileDialogService
{
    public string? SelectRecordingPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存雷达原始数据录制",
            Filter = "Radar recording (*.radarrec)|*.radarrec",
            DefaultExt = ".radarrec",
            AddExtension = true,
            FileName = $"radar-{DateTime.Now:yyyyMMdd-HHmmss}.radarrec"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectReplayPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开雷达原始数据录制",
            Filter = "Radar recording (*.radarrec)|*.radarrec",
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
