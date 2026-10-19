using Microsoft.UI.Xaml;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class HomeStudioPage
{
    async Task RunTimelineHandoffSmokeAsync(Action<bool, string> check)
    {
        var original = document ?? throw new InvalidOperationException("主页检查需要打开的项目。");
        var originalMetadata = metadata;
        string originalJson = original.ToJson();
        var replacement = HomeStudioDocument.Parse(originalJson);
        replacement.Playhead = replacement.Snap(.2);
        try
        {
            // Keep the exact same source identity; changing the homepage would
            // already reset the timeline and hide this project-switch bug.
            scrubber.Request(1.6);
            timeline.RunProjectReplacementSmoke(() => SetDocument(replacement, originalMetadata), check);
            await scrubber.FlushAsync();
            check(ReferenceEquals(document, replacement) && replacement.Playhead == replacement.Snap(.2)
                && timeline.Position == replacement.Playhead && replacement.ToJson() == originalJson && !scrubber.IsBusy,
                "同一主页来源的新项目取消旧定位，保留新项目播放头与演出数据");

            scrubber.Request(1.2);
            bool positioned = await SeekAfterScrubAsync(.4);
            await scrubber.FlushAsync();
            check(positioned && replacement.Playhead == replacement.Snap(.4) && timeline.Position == replacement.Playhead,
                "主页离散定位等待拖动队列结束，播放此处不会被更早的待处理位置覆盖");

            scrubber.Request(1.4);
            var oldAction = SeekAfterScrubAsync(.8);
            var next = HomeStudioDocument.Parse(originalJson); next.Playhead = next.Snap(.6);
            SetDocument(next, originalMetadata);
            check(!await oldAction && next.Playhead == next.Snap(.6) && timeline.Position == next.Playhead,
                "等待拖动完成期间切换主页项目会取消旧播放入口，旧目标时间不进入新项目");
        }
        finally { SetDocument(original, originalMetadata); }
    }
}

internal sealed partial class ClipTimeline
{
    internal void RunProjectReplacementSmoke(Action replaceProject, Action<bool, string> check)
    {
        string? previousSource = sourceKey;
        var clip = items.First(item => item.Actor.Length > 0);
        SetRange(TimelineRange.ForClip(length / 4, length / 4, framesPerSecond, length));
        FilterActor(clip.Actor);
        if (!collapsedGroups.Contains(GroupOf(clip))) ToggleGroup(GroupOf(clip));
        Select(clip.Id);
        zoom.Value = Math.Clamp(scale * 2, zoom.Minimum, zoom.Maximum);
        // Arm the state that would otherwise turn capture loss into a final seek.
        // Project replacement must suppress completion before releasing captures.
        scrubbing = true;
        int seeks = 0, completions = 0;
        void Seek(double _) => seeks++;
        void Complete() => completions++;
        SeekRequested += Seek; ScrubCompleted += Complete;
        try
        {
            replaceProject(); UpdateLayout();
            check(sourceKey == previousSource && WorkRange is null && filteredActor is null
                && collapsedGroups.Count == 0 && selected is null && !IsScrubbing
                && seeks == 0 && completions == 0,
                "相同主页来源的项目替换清除旧入出点、筛选、折叠和选择，取消手势不发出旧位置请求");
            check(scroll.HorizontalOffset < 1 && length * scale <= scroll.ViewportWidth + 1,
                "主页项目替换后重新显示完整时间范围，旧局部缩放不进入新项目");
        }
        finally { SeekRequested -= Seek; ScrubCompleted -= Complete; }
    }
}
