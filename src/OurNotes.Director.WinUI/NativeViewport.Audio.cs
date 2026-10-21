using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    readonly WindowsPreviewAudio previewAudio = new();
    readonly TextBlock audioStatus = new() { FontSize = 11, MaxWidth = 180, Foreground = Brush(205, 215, 230),
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    string? soundtrackKey;
    internal System.Text.Json.Nodes.JsonObject AudioDiagnosticsForSmoke() => previewAudio.Diagnostics();
    void AudioMessage(string message, bool exceptional = false)
    {
        audioStatus.Text = message; ToolTipService.SetToolTip(audioStatus, message);
        audioStatus.Visibility = exceptional || message.Contains("缓冲", StringComparison.Ordinal)
            || message.Contains("准备中", StringComparison.Ordinal) || message.Contains("不可用", StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;
    }
    void InitializeAudioPresentation()
    {
        session.PlaybackClock = previewAudio;
        previewAudio.StatusChanged += message => DispatcherQueue.TryEnqueue(() => { if (!disposed) AudioMessage(message); });
        previewAudio.Failed += error => DispatcherQueue.TryEnqueue(() => { if (!disposed) AudioMessage("无法播放声音：" + error.Message, true); });
        if (Content is Grid root && root.Children.OfType<Border>().LastOrDefault() is { } bar)
        {
            bar.Child = null; bar.RequestedTheme = ElementTheme.Dark;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(status); row.Children.Add(audioStatus); Grid.SetColumn(audioStatus, 1);
            var icon = new SymbolIcon(Symbol.Volume) { Foreground = Brush(205, 215, 230) };
            var mute = new ToggleButton { Content = icon, Padding = new(5), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(mute, "预览静音"); ToolTipService.SetToolTip(mute, "预览静音；不影响导出和口型");
            var sound = new Slider { Minimum = 0, Maximum = 100, Value = 100, Width = 70, StepFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(sound, "预览音量"); ToolTipService.SetToolTip(sound, "预览音量");
            double previous = 100; bool updating = false;
            sound.ValueChanged += (_, _) =>
            {
                previewAudio.Volume = sound.Value / 100;
                if (updating) return;
                updating = true; mute.IsChecked = sound.Value == 0; icon.Symbol = sound.Value == 0 ? Symbol.Mute : Symbol.Volume; updating = false;
            };
            mute.Click += (_, _) =>
            {
                if (updating) return;
                if (mute.IsChecked == true) { previous = Math.Max(1, sound.Value); sound.Value = 0; }
                else sound.Value = previous;
            };
            row.Children.Add(mute); Grid.SetColumn(mute, 2);
            row.Children.Add(sound); Grid.SetColumn(sound, 3); bar.Child = row;
        }
    }
    void ConfigureAudio(ScenePreviewSource source, bool prepareOnly = false)
    {
        try
        {
            if (!PreviewAudioIdentity.HasAudio(source))
            { previewAudio.Clear(); soundtrackKey = null; AudioMessage("无声音片段"); return; }
            string key = PreviewAudioIdentity.Key(source);
            if (key == soundtrackKey && previewAudio.PositionSeconds is not null) return;
            previewAudio.Configure(source, prepareOnly); soundtrackKey = key;
        }
        catch (Exception error)
        { previewAudio.Clear(); soundtrackKey = null; AudioMessage("声音准备失败：" + error.Message, true); }
    }
}
