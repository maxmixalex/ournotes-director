using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    readonly WindowsPreviewAudio previewAudio = new();
    readonly TextBlock audioStatus = new() { FontSize = 11, MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    void InitializeAudioPresentation()
    {
        session.PlaybackClock = previewAudio;
        previewAudio.StatusChanged += message => DispatcherQueue.TryEnqueue(() =>
        { if (!disposed) { audioStatus.Text = message; ToolTipService.SetToolTip(audioStatus, message); } });
        previewAudio.Failed += error => DispatcherQueue.TryEnqueue(() =>
        { if (!disposed) { audioStatus.Text = "无法播放语音"; ToolTipService.SetToolTip(audioStatus, error.Message); } });
        if (Content is Grid root && root.Children.OfType<Border>().LastOrDefault() is { } bar)
        {
            bar.Child = null;
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(status); row.Children.Add(audioStatus); Grid.SetColumn(audioStatus, 1);
            var sound = new Slider { Minimum = 0, Maximum = 100, Value = 100, Width = 80, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(sound, "预览音量；设为 0 静音，不影响导出");
            sound.ValueChanged += (_, _) => previewAudio.Volume = sound.Value / 100;
            row.Children.Add(sound); Grid.SetColumn(sound, 2); bar.Child = row;
        }
    }
    void ConfigureAudio(ScenePreviewSource source)
    {
        try { previewAudio.Configure(source); }
        catch (Exception error) { previewAudio.Clear(); audioStatus.Text = "语音准备失败"; ToolTipService.SetToolTip(audioStatus, error.Message); }
    }
}
