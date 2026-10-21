namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    internal async Task WithFullscreenPlaybackSmokeAsync(Func<Task> action)
    {
        var current = document ?? throw new InvalidOperationException("全屏播放检查需要 Live2D 项目。");
        double duration = durationBox.Value;
        // The preceding canvas export deliberately uses a 0.4-second clip. Give
        // the real fullscreen redraw enough playback range on software GPUs.
        // The test still requires native time to advance and playback to continue.
        try
        {
            durationBox.Value = Math.Max(duration, 15);
            await FlushEditsAsync();
            await action();
        }
        finally
        {
            if (ReferenceEquals(document, current))
            { durationBox.Value = duration; await FlushEditsAsync(); }
        }
    }
}
