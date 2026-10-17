using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    bool nativePlacement = true;
    sealed record PlacementGesture(CreativeScene Document, string Actor, StageDragPlane Plane, long Session, int Frame);
    PlacementGesture? stageGesture;

    void AddPlacementModeMenu(MenuFlyout menu)
    {
        var native = new ToggleMenuFlyoutItem { Text = "拖动原作空间位置", IsChecked = nativePlacement };
        var overlay = new ToggleMenuFlyoutItem { Text = "拖动画面偏移（扩展）", IsChecked = !nativePlacement };
        native.Click += (_, _) => { nativePlacement = true; stageGesture = null; BuildInspector(); };
        overlay.Click += (_, _) => { nativePlacement = false; stageGesture = null; BuildInspector(); };
        menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(native); menu.Items.Add(overlay);
    }

    PlacementGesture StartPlacement(string actor)
    {
        if (!viewport.HasCurrentSource || viewport.LastFrame is not { } frame ||
            frame.Targets.FirstOrDefault(t => t.Id == actor)?.StagePlacement is not { } plane)
            throw new InvalidOperationException("此人物没有可拖动的原作空间位置。已有画面偏移的项目，请在视图中选择“拖动画面偏移（扩展）”。");
        return new(document, actor, plane, frame.SessionId, frame.Frame);
    }

    async Task DragStageAsync(PreviewDrag drag)
    {
        var gesture = stageGesture;
        if (gesture is null || gesture.Actor != drag.TargetId)
        {
            var owner = document;
            await viewport.PauseForEditingAsync();
            if (!ReferenceEquals(owner, document)) return;
            gesture = stageGesture?.Actor == drag.TargetId ? stageGesture : StartPlacement(drag.TargetId);
            stageGesture = gesture;
        }
        if (!ReferenceEquals(gesture.Document, document)) { stageGesture = null; return; }
        Vector3 delta = gesture.Plane.Displacement(drag.DeltaX, drag.DeltaY);
        await viewport.PreviewStagePositionAsync(drag.TargetId, gesture.Plane.Slot,
            gesture.Plane.Position + delta, gesture.Session, gesture.Frame);
        if (!drag.IsCompleted) return;
        stageGesture = null;
        if (!ReferenceEquals(gesture.Document, document)) return;
        await CommitPlacementAsync(gesture, delta);
    }

    async Task CommitPlacementAsync(PlacementGesture gesture, Vector3 delta)
    {
        if (delta.LengthSquared() < 1e-12f) return;
        try
        {
            await EditAsync(() => selectedClip = CreativeStagePlacement.MoveHere(document,
                gesture.Actor, gesture.Frame / 30d, delta), false);
            await CommitLiveAsync(); timeline.RevealSelection();
        }
        catch
        {
            // An invalid document edit must not leave a transient pose masquerading as saved.
            if (ReferenceEquals(gesture.Document, document)) await ReloadAsync();
            throw;
        }
    }

    async Task MoveStageDepthAsync(string actor, float delta)
    {
        await viewport.PauseForEditingAsync(); var gesture = StartPlacement(actor);
        var move = new Vector3(0, 0, delta);
        await viewport.PreviewStagePositionAsync(actor, gesture.Plane.Slot,
            gesture.Plane.Position + move, gesture.Session, gesture.Frame);
        await CommitPlacementAsync(gesture, move);
    }
}
