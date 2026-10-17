using System.Numerics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class StagePlacementNativeTests
{
    public static void Run(Archive archive, string core, string ffmpeg, string file, string output, string actor, Action<bool, string> check)
    {
        var scene = CreativeScene.Parse(File.ReadAllText(file));
        Vector3 delta; Pixels moved; int frame = 45;
        using (var native = new AdvStoryRenderer(archive, "intl", core, DirectorProject.LibrarySource, "zh-Hans", 640, 360, file, ffmpeg, 1))
        {
            native.Frame(frame, true);
            var targets = native.PreviewTargets();
            var plane = targets.Single(t => t.Id == actor).StagePlacement ?? throw new InvalidOperationException("Missing real stage projection.");
            var start = plane.Position;
            delta = plane.Displacement(.05, -.02);
            native.Scene.PreviewStagePosition(actor, plane.Slot, start + delta);
            moved = native.Frame(frame, true);
            check(native.Scene.Slot(plane.Slot).Position.Z == start.Z && native.Loop.Frame == frame,
                "Direct manipulation retains source depth and does not advance speech, animation or physics");
            var depthOrder = native.Scene.StageActors.OrderByDescending(s => s.WorldZ).ThenBy(s => s.Priority).Select(s => s.Id);
            check(native.PreviewTargets().Select(t => t.Id).SequenceEqual(depthOrder), "Canvas hit testing follows rendered native depth/order rather than model insertion order");
        }
        CreativeStagePlacement.MoveHere(scene, actor, frame / 30d, delta);
        var project = scene.Compile();
        using (var reloaded = new AdvStoryRenderer(archive, "intl", core, DirectorProject.LibrarySource, "zh-Hans", 640, 360, null, ffmpeg, 1, project))
            check(reloaded.Frame(frame, true).Rgba.SequenceEqual(moved.Rgba), "Native canvas drag pixels equal saved-and-replayed MoveToDirection at the same source frame");
        File.WriteAllBytes(Path.Combine(output, "native-space-drag.png"), Png.Encode(moved));
    }
}
