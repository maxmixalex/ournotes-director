using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class IsolatedActorRealTests
{
    internal static JsonObject Run(Archive archive, string core, string ffmpeg, string sourcePath, JsonObject visibleVoice, string output, Action<bool, string> check)
    {
        string target = visibleVoice["target"]!.ToString(); int frame = visibleVoice["frame"]!.GetValue<int>();
        var project = DirectorProject.Document(sourcePath).DeepClone().AsObject();
        string sourceHash = Archive.Hash(File.ReadAllBytes(sourcePath));
        project["output"] = ActorIsolation.Output(project["output"] as JsonObject, target, 600, 900);
        string path = Path.Combine(output, "isolated-current-pose.project.json"); File.WriteAllText(path, project.ToJsonString());
        Pixels expected; IReadOnlyList<PreviewLipSync> lips;
        var laterStates = new Dictionary<int, IReadOnlyList<PreviewLipSync>>();
        using (var renderer = new AdvStoryRenderer(archive, "intl", core, "10462", "zh-Hans", 600, 900, path, ffmpeg, 1))
        {
            expected = renderer.Frame(frame, true); lips = renderer.Scene.PreviewLipSyncState();
            check(expected.Rgba.Where((_, i) => i % 4 == 3).Any(alpha => alpha > 0) && expected.Rgba.Where((_, i) => i % 4 == 3).Any(alpha => alpha == 0),
                "Isolated original-story actor renders visible native pose with a genuinely transparent background");
            var actor = renderer.Scene.Character(target)!;
            var position = actor.Prefab.Root.Position; var scale = actor.Prefab.Root.Scale;
            var repeated = renderer.Frame(frame, true);
            check(repeated.Rgba.SequenceEqual(expected.Rgba) && actor.Prefab.Root.Position == position && actor.Prefab.Root.Scale == scale,
                "Isolated drawing restores source transforms and repeats the same evaluated pose without restarting the action");
            float opacity = actor.Spec.Opacity;
            try
            {
                actor.Spec.Opacity = 0;
                check(renderer.Frame(frame, true).Rgba.All(value => value == 0), "Removing the isolated target leaves no background, other actor, dialogue or post-processing pixels");
            }
            finally { actor.Spec.Opacity = opacity; }
            var later = renderer.Frame(frame + 8, true);
            check(!later.Rgba.SequenceEqual(expected.Rgba), "Isolated frames retain the source performance and mouth movement across time");
            laterStates[frame + 8] = renderer.Scene.PreviewLipSyncState();
            _ = renderer.Frame(frame + 20, true);
            laterStates[frame + 20] = renderer.Scene.PreviewLipSyncState();
        }
        using (var normal = new AdvStoryRenderer(archive, "intl", core, "10462", "zh-Hans", 600, 900, sourcePath, ffmpeg, 1))
        {
            _ = normal.Frame(frame, true);
            var normalLips = normal.Scene.PreviewLipSyncState();
            check(normalLips.SequenceEqual(lips), "Isolated export and original playback evaluate exactly the same voice and lip-sync state");
            foreach (var (sample, isolatedLips) in laterStates)
            {
                _ = normal.Frame(sample, true);
                check(normal.Scene.PreviewLipSyncState().SequenceEqual(isolatedLips), "Isolated voice state stays identical at subsequent source frame " + sample);
            }
        }
        string png = Path.Combine(output, "isolated-current-pose.png");
        _ = AdvStoryRenderer.Run(archive, "intl", core, "10462", "zh-Hans", path, "frame", png, 600, 900, frame / 30d, 1, "ffv1", true, true, ffmpeg, 1);
        check(Png.Decode(File.ReadAllBytes(png)).Rgba.SequenceEqual(expected.Rgba), "The actual PNG export route preserves the current isolated native pixels");
        check(Archive.Hash(File.ReadAllBytes(sourcePath)) == sourceHash, "Isolated export leaves the original scene project unchanged");
        return new() { ["target"] = target, ["frame"] = frame, ["width"] = 600, ["height"] = 900, ["sourceUnchanged"] = true, ["pngSha256"] = Archive.Hash(File.ReadAllBytes(png)) };
    }
}
