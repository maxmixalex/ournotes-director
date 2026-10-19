using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class SceneInteractionNativeTests
{
    public static async Task<int> RunAsync(string fixture, string output)
    {
        string project = Path.Combine(Path.GetFullPath(output), "authored.scene.json");
        string saved = File.ReadAllText(project);
        var checks = new JsonArray();
        var report = new JsonObject { ["ok"] = false, ["checks"] = checks };
        void Check(bool result, string label)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = label, ["ok"] = result });
            if (!result) throw new InvalidOperationException(label);
        }
        try
        {
            NativeTestGraphics.Configure();
            string core = NativeDependencies.CoreLibrary(baseDirectory: NativeTestGraphics.PackageDirectory);
            string ffmpeg = NativeDependencies.MediaTool("ffmpeg", baseDirectory: NativeTestGraphics.PackageDirectory);
            await using var session = new RealtimePreviewSession();
            await session.LoadSceneAsync(new(fixture, project, core, ffmpeg, 640, 360, 4, SegmentedCpuFrames: true));
            await session.SeekAsync(.5);
            var original = session.LastFrame!;
            long identity = original.SessionId;
            byte[] baseline = original.Pixels.Rgba.ToArray();
            await session.SetScenePreviewScaleAsync(.25);
            Check(ReferenceEquals(original, session.LastFrame), "Selecting an interaction budget does not publish an extra stale frame");
            await session.SeekAsync(.5);
            var draft = session.LastFrame!;
            Check(draft.ScenePreviewScale == .25 && draft.SessionId == identity && draft.Frame == original.Frame
                && draft.Pixels.Width == original.Pixels.Width && draft.Pixels.Height == original.Pixels.Height,
                "Scene-only reduced drawing preserves the full presentation size, native session and paused frame");
            Check(!draft.Pixels.Rgba.SequenceEqual(baseline), "Interaction quality changes real scene pixels rather than only its diagnostic label");
            Check(draft.Targets.SequenceEqual(original.Targets), "Interaction drawing keeps the reference hit geometry and original stage coordinates");
            await session.SetScenePreviewScaleAsync(1); await session.SeekAsync(.5);
            Check(session.LastFrame!.ScenePreviewScale == 1 && session.LastFrame.SessionId == identity
                && session.LastFrame.Pixels.Rgba.SequenceEqual(baseline),
                "Settling restores byte-identical full-resolution pixels at the same pose without reloading models");
            Check(original.Pixels.Rgba.SequenceEqual(baseline), "Retained full-resolution frames remain immutable across interaction FBO changes");
            await session.SetScenePreviewScaleAsync(.25); await session.SeekAsync(35d / 30);
            Check(session.LastFrame!.Frame == 35 && session.LastFrame.ScenePreviewScale == .25,
                "Forward interaction seeks evaluate the real motion and expression history");
            await session.SetScenePreviewScaleAsync(1); await session.SeekAsync(35d / 30);
            var full = session.LastFrame!;
            var reference = await Task.Run(() =>
            {
                using var renderer = new AdvStoryRenderer(new Archive(fixture), "intl", core,
                    DirectorProject.LibrarySource, "zh-Hans", 640, 360, project, ffmpeg, 1);
                return renderer.Frame(35, DirectorProject.Read(project).Output["clean"]?.GetValue<bool>() ?? false);
            });
            Check(full.Pixels.Rgba.SequenceEqual(reference.Rgba),
                "A settled gesture matches independently replayed native output, including focus and expression changes");
            await session.SetScenePreviewScaleAsync(.25); await session.SeekAsync(.2);
            Check(session.LastFrame!.Frame == 6 && session.LastFrame.ScenePreviewScale == .25 && !session.IsPlaying,
                "Backward native reconstruction retains the held-gesture drawing budget and stays paused");
            await session.SetScenePreviewScaleAsync(1); await session.SeekAsync(.2);
            var final = session.LastFrame!;
            bool rejected = false;
            try { await session.SetScenePreviewScaleAsync(double.NaN); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && ReferenceEquals(final, session.LastFrame), "Invalid drawing budgets fail before altering a live native session");
            Check(File.ReadAllText(project) == saved, "No interaction rendering choice is written into the project or export settings");
            report["ok"] = true;
            report["referenceWidth"] = 640; report["referenceHeight"] = 360;
            Console.WriteLine($"Passed {checks.Count} real scene interaction-quality and native-state checks.");
            return 0;
        }
        finally { File.WriteAllText(Path.Combine(output, "interaction-quality.json"), report.ToJsonString()); }
    }
}
