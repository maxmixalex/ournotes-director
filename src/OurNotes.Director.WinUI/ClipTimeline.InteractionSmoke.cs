using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class ClipTimeline
{
    internal async Task VerifyGroupAndHeightSmokeAsync(Action<bool, string> check)
    {
        FilterActor(null); SetAllGroupsCollapsed(false); scroll.ChangeView(null, 0, null, true); Draw(); UpdateLayout();
        int expandedCount = lanes.Count, itemCount = items.Length;
        string? selection = selected; TimelineRange? range = WorkRange;
        double oldHeight = RowHeight, oldScale = scale;
        var group = lanes.First(lane => lane.IsGroup && lane.Actor.Length > 0);
        Button Header() => labels.Children.OfType<Button>().First(button => button.Tag?.ToString() == group.GroupKey);
        new ButtonAutomationPeer(Header()).Invoke(); await Task.Delay(60); UpdateLayout();
        check(collapsedGroups.Contains(group.GroupKey!) && lanes.Count < expandedCount && items.Length == itemCount
            && selected == selection && WorkRange == range,
            "实际分组标题收起同一角色的子轨道，片段、选择和入出点保持不变");
        await App.CaptureCheckpointAsync("timeline-collapsed");
        new ButtonAutomationPeer(Header()).Invoke(); await Task.Delay(60); UpdateLayout();
        check(!collapsedGroups.Contains(group.GroupKey!) && lanes.Count == expandedCount,
            "再次点击实际标题展开相同的角色子轨道");
        heightSlider.Value = 72; UpdateLayout();
        check(RowHeight == 72 && scale == oldScale && labels.Children.OfType<Button>().All(button => Math.Abs(button.ActualHeight - 71) < 1)
            && WorkRange == range && items.Length == itemCount,
            "真实轨道高度滑块调整纵向高度，不改变横向比例或项目数据");
        await App.CaptureCheckpointAsync("timeline-tall-tracks");
        heightSlider.Value = oldHeight; UpdateLayout();
    }
    internal (FrameworkElement Element, double StartX, double EndX, double EndTime) ScrubCoordinates(double start, double end)
    {
        Fit(); UpdateLayout(); scroll.ChangeView(0, null, null, true); Draw();
        double maximum = Math.Max(24, ruler.ActualWidth - 20);
        double a = Math.Clamp(start * scale, 2, maximum - 2), b = Math.Clamp(end * scale, a + 1, maximum);
        return (ruler, a, b, At(b));
    }
}
