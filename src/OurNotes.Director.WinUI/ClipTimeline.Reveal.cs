using Microsoft.UI.Xaml;

namespace OurNotes.Director.WinUI;

internal sealed partial class ClipTimeline
{
    /// <summary>Bring a newly authored operation into view without collapsing tracks or
    /// changing the work area. Adding focus must not leave its new track off screen.</summary>
    public void RevealSelection()
    {
        string? id = selected, project = sourceKey;
        void Reveal()
        {
            if (selected != id || sourceKey != project || id is null) return;
            var clip = items.FirstOrDefault(item => item.Id == id);
            if (clip is null) return;
            if (filteredActor is not null && clip.Actor != filteredActor)
            { filteredActor = null; UpdateLanes(); Draw(); }
            int index = lanes.ToList().FindIndex(lane => lane.Items.Any(item => item.Id == id));
            if (index < 0) return;
            scroll.UpdateLayout();
            double top = index * RowHeight, bottom = top + RowHeight;
            double y = top < scroll.VerticalOffset ? top
                : bottom > scroll.VerticalOffset + scroll.ViewportHeight ? Math.Max(0, bottom - scroll.ViewportHeight) : scroll.VerticalOffset;
            double left = clip.Start * scale, right = clip.End * scale;
            double x = left < scroll.HorizontalOffset ? Math.Max(0, left - 8)
                : right > scroll.HorizontalOffset + scroll.ViewportWidth ? Math.Max(0, right - scroll.ViewportWidth + 8) : scroll.HorizontalOffset;
            if (right - left > scroll.ViewportWidth) x = Math.Max(0, left - 8);
            scroll.ChangeView(x, y, null, true); Draw();
        }
        Reveal(); DispatcherQueue.TryEnqueue(Reveal);
    }

    internal bool IsSelectedLaneVisible
    {
        get
        {
            int index = lanes.ToList().FindIndex(lane => lane.Items.Any(item => item.Id == selected));
            return index >= 0 && index * RowHeight >= scroll.VerticalOffset - 1
                && (index + 1) * RowHeight <= scroll.VerticalOffset + scroll.ViewportHeight + 1;
        }
    }
}
