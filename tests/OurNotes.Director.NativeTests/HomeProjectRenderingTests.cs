using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

internal static class HomeProjectRenderingTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Home project: " + message); checks++; }
        string root = Path.Combine(Path.GetTempPath(), "ournotes-home-project-" + Guid.NewGuid().ToString("N"));
        HomeRenderingTests.WriteFixture(root);
        try
        {
            var scene = new HomeCatalog(new Archive(root)).Open(77);
            var resources = new HomeResources(scene); var timing = new HomeTiming(0, 61, 30);
            Pixels Source(int frame)
            { using var source = new HomeRenderer(new(resources), 128, 96, 1, timing); return source.Frame(frame); }
            var normal = Source(15); var frame7 = Source(7); var frame0 = Source(0); var frame14 = Source(14);
            using (var renderer = new HomeRenderer(new(resources), 128, 96, 1, timing, editState: new()))
            {
                Check(renderer.Frame(15).Rgba.SequenceEqual(normal.Rgba), "identity edits preserve original complete-scene pixels");
                var original = renderer.PreviewTargets().Single(t => t.Id == "alice");
                var edits = Track(new() { Transform = new(.125, .0625, 1) });
                renderer.SetEditState(edits); renderer.Frame(15);
                var moved = renderer.PreviewTargets().Single(t => t.Id == "alice");
                Check(Math.Abs(moved.X - original.X - .125) < 1e-6 && Math.Abs(moved.Y - original.Y - .0625) < 1e-6,
                    "native selection bounds follow normalized X/Y edits with Y down");
                renderer.SetEditState(Track(new() { Transform = new(Scale: 1.5) })); renderer.Frame(15);
                Check(Math.Abs(renderer.PreviewTargets().Single().Width / original.Width - 1.5) < 1e-5,
                    "native preview scales the complete character around a fixed pivot");
                renderer.SetEditState(Track(new() { Transform = new(Visible: false) })); var hidden = renderer.Frame(15);
                Check(renderer.PreviewTargets().Count == 0 && Pixel(hidden, 64, 48).SequenceEqual(new byte[] { 0, 255, 0, 255 }),
                    "hidden characters disappear without removing the room");
                renderer.SetEditState(Track(new() { Animations = [new(.5, "home_start", false)] }));
                Check(renderer.Frame(15).Rgba.SequenceEqual(frame0.Rgba) && renderer.Frame(22).Rgba.SequenceEqual(frame7.Rgba),
                    "animation cues restart on their exact timeline boundary");
                renderer.SetEditState(Track(new() { Animations = [new(0, "home_start", false, 2)] }));
                Check(renderer.Frame(7).Rgba.SequenceEqual(frame14.Rgba), "animation cue speed evaluates the actual source pose");
                renderer.SetEditState(Track(new() { Animations = [new(.5, "home_start", false, 1, .25)] }));
                Check(renderer.Frame(15).Rgba.SequenceEqual(frame7.Rgba), "animation cue source offset is preserved");
                var edited = Track(new()
                {
                    Keys = [new(0, new()), new(1, new(.2, .1, 1.25))],
                    Animations = [new(0, "home_start", true)]
                }) with { Background = false, Grade = new(-1, 1, 1, 0) };
                renderer.SetEditState(edited); var first = renderer.Frame(15);
                Check(renderer.Transparent && renderer.RoomMeshCount == 0 && first.Rgba[3] == 0,
                    "live background toggles expose genuine source alpha without rebuilding the viewport");
                Check(first.Rgba.Where((_, i) => i % 4 == 2).Max() is >= 125 and <= 130,
                    "native grade changes RGB while keeping transparent pixels");
                renderer.Frame(45); Check(first.Rgba.SequenceEqual(renderer.Frame(15).Rgba), "edited animation seeks backward deterministically");
                renderer.QueueFrame(15); renderer.QueueFrame(30);
                Check(first.Rgba.SequenceEqual(renderer.ReadQueuedFrame().Rgba), "edited queued output agrees with synchronous preview"); renderer.ReadQueuedFrame();
                var project = new HomeProject(new HomeProjectSource(77, "intl", 42)) { Duration = 2, EditState = edited, Output = new(128, 96, 30, 1) };
                string json = Path.Combine(root, "project.json"); File.WriteAllBytes(json, JsonOutput.SerializeToUtf8Bytes(project.ToJson()));
                var restored = HomeProject.Read(json);
                // Export uses a separate context after this live context is disposed below.
                File.WriteAllBytes(Path.Combine(root, "expected.png"), Png.Encode(first));
                Check(restored.EditState.Characters["alice"].Keys.Count == 2 && restored.EditState.Grade.Exposure == -1,
                    "saved composition retains render-affecting tracks and grade");
            }
            var saved = HomeProject.Read(Path.Combine(root, "project.json"));
            string exported = Path.Combine(root, "export.png");
            var report = HomeExport.Run(new(resources), "frame", exported, new(15, 1, 30), 128, 96, 1, editState: saved.EditState);
            Check(Png.Decode(File.ReadAllBytes(exported)).Rgba.SequenceEqual(Png.Decode(File.ReadAllBytes(Path.Combine(root, "expected.png"))).Rgba)
                && report["edited"]!.GetValue<bool>(), "saved-project offline export is byte-identical to the matching native preview");
            Shared(root, Check);
            Console.WriteLine($"Passed {checks} native home project, edits, timeline, slot-order and export-consistency checks.");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }

    static HomeEditState Track(HomeCharacterTrack track) => new() { Characters = new Dictionary<string, HomeCharacterTrack> { ["alice"] = track } };
    static byte[] Pixel(Pixels pixels, int x, int y) => pixels.Rgba[((y * pixels.Width + x) * 4)..((y * pixels.Width + x) * 4 + 4)];

    static void Shared(string root, Action<bool, string> check)
    {
        string manifestPath = Path.Combine(root, "sites", "intl", "stories", "42.json"); var manifest = Archive.ReadJson(manifestPath);
        var files = manifest["files"]!.AsObject();
        string PathOf(string logical) => Path.Combine(root, "sites", "intl", files[logical]!["asset"]!.ToString());
        void Put(string logical, byte[] bytes)
        {
            string asset = "assets/" + Archive.Hash(bytes) + Path.GetExtension(logical); string path = Path.Combine(root, "sites", "intl", asset);
            File.WriteAllBytes(path, bytes); files[logical] = new JsonObject { ["asset"] = asset, ["size"] = bytes.Length };
        }
        var spot = Archive.ReadJson(PathOf("host/spot/spot.json"));
        spot["characters"] = JsonNode.Parse("""[{"path":"home/Pair/Alice","_characterId":1},{"path":"home/Pair/Bob","_characterId":2}]""");
        spot["spineCharacters"]![0]!["path"] = "home/Pair";
        Put("host/spot/spot.json", JsonOutput.SerializeToUtf8Bytes(spot));
        Put("host/spot/spine/alice.json", Encoding.UTF8.GetBytes("""
            {"skeleton":{"hash":"shared-home-project","spine":"4.2.40","fps":30},
             "bones":[{"name":"root"},{"name":"Alice","parent":"root"},{"name":"Bob","parent":"root"}],
             "slots":[{"name":"Alice_back","bone":"Alice","attachment":"pixel","color":"ff000080"},
                      {"name":"Bob_front","bone":"Bob","attachment":"pixel","color":"00ff0080"},
                      {"name":"Alice_front","bone":"Alice","attachment":"pixel","color":"0000ff80"},
                      {"name":"bg_table","bone":"root","attachment":"pixel","color":"ffff00ff"}],
             "skins":[{"name":"default","attachments":{
                "Alice_back":{"pixel":{"path":"pixel","width":1.2,"height":1.2}},
                "Bob_front":{"pixel":{"path":"pixel","width":0.8,"height":0.8}},
                "Alice_front":{"pixel":{"path":"pixel","width":0.4,"height":0.4}},
                "bg_table":{"pixel":{"path":"pixel","x":1,"width":0.3,"height":0.3}}
             }}],"animations":{"home_start":{"bones":{"Alice":{"translate":[{}, {"time":1,"x":0.1}]},"Bob":{"translate":[{}, {"time":1,"x":-0.1}]}}}}}
            """));
        Put("host/spot/spine/page.png", Png.Encode(new(4, 4, Enumerable.Repeat((byte)255, 64).ToArray())));
        File.WriteAllBytes(manifestPath, JsonOutput.SerializeToUtf8Bytes(manifest));
        var scene = new HomeCatalog(new Archive(root)).Open(77); var resources = new HomeResources(scene); var timing = new HomeTiming(0, 31, 30);
        check(scene.Characters.Count == 2 && scene.Characters.All(c => c.SharedPartPaths.Count == 1), "fixture resolves two real character filters in one shared skeleton");
        Pixels original;
        using (var renderer = new HomeRenderer(new(resources), 128, 96, 1, timing, background: false)) original = renderer.Frame(15);
        using (var renderer = new HomeRenderer(new(resources), 128, 96, 1, timing, editState: new() { Background = false }))
        {
            var split = renderer.Frame(15);
            check(split.Rgba.SequenceEqual(original.Rgba), "shared character isolation retains interleaved translucent slot draw order and unassigned scenery");
            var bob = renderer.PreviewTargets().Single(t => t.Id == "bob");
            renderer.SetEditState(Track(new() { Transform = new(.15, 0, 1) }) with { Background = false }); renderer.Frame(15);
            var after = renderer.PreviewTargets().Single(t => t.Id == "bob");
            check(Math.Abs(bob.X - after.X) < 1e-8 && Math.Abs(bob.Width - after.Width) < 1e-8,
                "moving Alice leaves Bob's geometry unchanged inside their shared skeleton");
            var grouped = new HomeAnimation(resources); grouped.SetEditState(new()); grouped.Advance(15, 30);
            var alice = grouped.Parts.Single(p => p.CharacterKey == "alice");
            check(alice.Actor.Mesh.Batches.Select(b => b.DrawOrder).SequenceEqual(new[] { 0, 2 }),
                "excluded character slots divide shared render batches at their actual original draw positions");
        }
    }
}
