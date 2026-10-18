using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

static class ViewportNavigationTests
{
    public static void Run(Action<bool, string> check)
    {
        var view = new ViewportNavigation(); view.Resize(900, 600, 1920, 1080);
        var fit = view.Bounds;
        check(fit.Width == 900 && Math.Abs(fit.Height - 506.25) < 1e-8 && Math.Abs(fit.Y - 46.875) < 1e-8,
            "Fit centres the complete canvas inside the viewport without changing its aspect ratio");
        view.PanTo(74, -32);
        var before = view.Bounds;
        const double anchorX = 137, anchorY = 214;
        double x = (anchorX - before.X) / before.Width, y = (anchorY - before.Y) / before.Height;
        view.ZoomAt(anchorX, anchorY, 120);
        var after = view.Bounds;
        check(Math.Abs(after.X + x * after.Width - anchorX) < 1e-8 && Math.Abs(after.Y + y * after.Height - anchorY) < 1e-8,
            "Wheel zoom keeps the canvas point under the pointer fixed after panning");
        view.ZoomAt(anchorX, anchorY, -120); after = view.Bounds;
        check(Math.Abs(after.X - before.X) < 1e-8 && Math.Abs(after.Y - before.Y) < 1e-8 && Math.Abs(after.Width - before.Width) < 1e-8,
            "An opposite wheel step restores the previous view without anchor drift");
        double centreX = (450 - after.X) / after.Width, centreY = (300 - after.Y) / after.Height;
        view.Resize(600, 700, 1920, 1080); after = view.Bounds;
        check(Math.Abs((300 - after.X) / after.Width - centreX) < 1e-8 && Math.Abs((350 - after.Y) / after.Height - centreY) < 1e-8,
            "Resizing the preview panel retains the canvas point at the viewport centre");
        view.Fit(); after = view.Bounds;
        check(view.Zoom == 1 && view.PanX == 0 && view.PanY == 0 && Math.Abs(after.X) < 1e-8,
            "Fit clears navigation zoom and pan together");
        check(ViewportNavigation.WheelAction(false, true, true) == ViewportWheelAction.SelectedObject
            && ViewportNavigation.WheelAction(true, true, true) == ViewportWheelAction.Canvas
            && ViewportNavigation.WheelAction(false, false, false) == ViewportWheelAction.Canvas,
            "Plain wheel edits the selected actor, Ctrl wheel navigates, and an unselected canvas remains navigable");
        check(ViewportNavigation.WheelAction(false, true, false) == ViewportWheelAction.None
            && ViewportNavigation.WheelAction(true, true, false) == ViewportWheelAction.Canvas,
            "An actor absent at the current frame cannot silently turn an object gesture into canvas zoom");
    }
}
