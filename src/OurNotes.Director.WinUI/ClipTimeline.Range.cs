using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using OurNotes.Director.Desktop.Core;
using Windows.System;

namespace OurNotes.Director.WinUI;

internal sealed partial class ClipTimeline
{
    readonly DropDownButton rangeButton = new() { Content = "全部区间", Padding = new(8, 5, 8, 5) };
    TimelineRange? workRange;
    public TimelineRange? WorkRange => workRange;
    public double PlaybackStart => workRange?.Start ?? 0;
    public double PlaybackLastTime => workRange?.LastFrameTime ?? length;
    public bool FollowSelection { get; set; } = true;

    void InitializeRange()
    {
        // Canvas/Border do not take keyboard focus themselves. After a pointer
        // selection, I/O and frame stepping must go to the timeline, not the
        // inspector field which happened to be focused before the click.
        PointerEventHandler focusTimeline = (_, args) =>
        {
            if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                Focus(FocusState.Pointer);
        };
        canvas.AddHandler(PointerPressedEvent, focusTimeline, true);
        ruler.AddHandler(PointerPressedEvent, focusTimeline, true);
        ToolTipService.SetToolTip(rangeButton, "设置预览 / 导出区间 · I 入点，O 出点；不会裁掉原剧情或丢失前面的状态");
        rangeButton.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            menu.Items.Add(StudioMenus.Item("在播放头设入点", MarkIn, shortcut: "I"));
            menu.Items.Add(StudioMenus.Item("在播放头设出点（含当前帧）", MarkOut, shortcut: "O"));
            menu.Items.Add(StudioMenus.Item("设为所选片段", RangeFromSelection, enabled: items.Any(c => c.Id == selected)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(StudioMenus.Item("清除入点 / 出点", ClearRange, enabled: workRange is not null));
            menu.ShowAt(rangeButton);
        };
    }

    public void MarkIn() => SetRange((workRange ?? TimelineRange.Full(length, framesPerSecond))
        .MarkIn(Math.Min(position, length)));
    public void MarkOut() => SetRange((workRange ?? TimelineRange.Full(length, framesPerSecond))
        .MarkOut(Math.Min(position, length)));
    public void ClearRange() { workRange = null; UpdateRangeLabel(); DrawRuler(); }
    public void ResetProjectView()
    {
        // Two separate projects may use the same archive and language. The host knows
        // when a document is replaced; source metadata alone cannot identify that change.
        sourceKey = null; filteredActor = null; selected = null; fitPending = true;
        IsPlaying = false; Position = 0; ClearRange();
        scroll.ChangeView(0, 0, null, true);
    }
    public void SetRange(TimelineRange range)
    {
        workRange = range.ClampTo(TimelineRange.Full(length, framesPerSecond));
        UpdateRangeLabel(); DrawRuler();
    }
    public TimelineRange? SelectedRange()
    {
        var clip = items.FirstOrDefault(c => c.Id == selected);
        return clip is null ? null : TimelineRange.ForClip(clip.Start, Math.Max(1d / framesPerSecond, clip.End - clip.Start), framesPerSecond, length);
    }
    void RangeFromSelection() { if (SelectedRange() is { } range) SetRange(range); }
    void ValidateRange(bool changedProject)
    {
        if (changedProject || workRange?.FramesPerSecond != framesPerSecond) workRange = null;
        if (workRange is not null) workRange = workRange.ClampTo(TimelineRange.Full(length, framesPerSecond));
        UpdateRangeLabel();
    }
    void UpdateRangeLabel()
    {
        rangeButton.Content = workRange is null ? "全部区间" : $"{workRange.Start:0.##}–{workRange.End:0.##}s";
        ToolTipService.SetToolTip(rangeButton, workRange is null ? "I 设入点 · O 设出点 · 所选片段设为区间"
            : $"预览 / 导出区间 · {workRange.FrameCount} 帧，{workRange.Duration:0.###} 秒\n原剧情完整保留；图片仍取当前播放头");
    }
    bool HandleRangeKey(VirtualKey key)
    {
        bool control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (control) return false;
        if (key == VirtualKey.I) { MarkIn(); return true; }
        if (key == VirtualKey.O) { MarkOut(); return true; }
        return false;
    }
    void DrawRangeOnRuler()
    {
        if (workRange is null || !IsLoaded) return;
        double first = workRange.Start * scale - scroll.HorizontalOffset;
        double last = workRange.End * scale - scroll.HorizontalOffset;
        double left = Math.Clamp(first, 0, scroll.ActualWidth), right = Math.Clamp(last, 0, scroll.ActualWidth);
        if (right > left)
        {
            var band = new Rectangle { Width = right - left, Height = 26, Fill = Brush(65, 89, 169, 233), IsHitTestVisible = false };
            Canvas.SetLeft(band, left); ruler.Children.Add(band);
        }
        foreach (double x in new[] { first, last })
            if (x >= 0 && x <= scroll.ActualWidth)
                ruler.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 26, Stroke = Brush(255, 81, 155, 220), StrokeThickness = 2, IsHitTestVisible = false });
    }
}
