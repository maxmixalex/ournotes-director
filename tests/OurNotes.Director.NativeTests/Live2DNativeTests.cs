using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>Opt-in real Cubism model regression for paused workbench edits and exports.</summary>
static class Live2DNativeTests
{
    public static async Task<int> RunAsync(string fixture, string outputDirectory)
    {
        fixture = Path.GetFullPath(fixture); outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var checks = new JsonArray(); var report = new JsonObject { ["ok"] = false, ["checks"] = checks };
        void Check(bool value, string name)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["ok"] = value });
            if (!value) throw new InvalidOperationException(name);
            Console.WriteLine(name);
        }
        try
        {
            NativeTestGraphics.Configure(); var archive = new Archive(fixture);
            string core = NativeDependencies.CoreLibrary(baseDirectory: NativeTestGraphics.PackageDirectory);
            string ffmpeg = NativeDependencies.MediaTool("ffmpeg", baseDirectory: NativeTestGraphics.PackageDirectory);
            var manifest = archive.Manifest("intl", "stories", "10462");
            var definition = JsonNode.Parse(archive.Logical("intl", manifest, "story.json"))!;
            string modelId = definition["models"]!.AsObject().First().Value!.ToString();
            string actorId = "workbench";
            var source = new JsonObject
            {
                ["format"] = DirectorProject.Format, ["version"] = 1,
                ["source"] = new JsonObject { ["adv"] = "10462", ["site"] = "intl", ["language"] = "zh-Hans" },
                ["output"] = new JsonObject { ["width"] = 320, ["height"] = 480, ["clean"] = true,
                    ["letterbox"] = "none", ["isolateActor"] = actorId, ["supersample"] = 1 },
                ["initialize"] = new JsonArray(), ["finalize"] = new JsonArray(), ["text"] = new JsonObject(),
                ["commands"] = new JsonArray(
                    new JsonObject { ["cmd"] = "Character", ["TargetName"] = actorId, ["Model"] = modelId, ["AtFrame"] = 0 },
                    new JsonObject { ["cmd"] = "In", ["TargetName"] = actorId, ["PositionType"] = 3, ["Duration"] = 0, ["AtFrame"] = 0 },
                    new JsonObject { ["cmd"] = "Delay", ["Frames"] = 0, ["AtFrame"] = 300 })
            };
            string project = Path.Combine(outputDirectory, "live2d.source.project.json");
            File.WriteAllText(project, source.ToJsonString());
            string parameter;
            double pinned;
            int expectedCount;
            using (var nativeModel = new Actor(archive, "intl", core, new ActorSpec { Model = modelId }, null, 722))
            {
                var parameters = nativeModel.PreviewParameters(); expectedCount = nativeModel.Core.ParameterIds.Length;
                var angle = parameters.FirstOrDefault(p => p.Id == "ParamAngleX" && p.Maximum > p.Minimum)
                    ?? parameters.First(p => p.Maximum > p.Minimum);
                parameter = angle.Id; pinned = angle.Maximum;
                Check(parameters.Count == expectedCount && parameters.Select(p => p.Id).SequenceEqual(nativeModel.Core.ParameterIds)
                    && parameters.Select(p => p.Minimum).SequenceEqual(nativeModel.Core.Min.Select(v => (double)v))
                    && parameters.Select(p => p.Maximum).SequenceEqual(nativeModel.Core.Max.Select(v => (double)v))
                    && parameters.Select(p => p.Default).SequenceEqual(nativeModel.Core.Defaults.Select(v => (double)v)),
                    "Parameter definitions match the loaded Cubism Core model, including ranges and defaults");
            }
            report["model"] = modelId; report["parameter"] = parameter; report["parameterCount"] = expectedCount;
            await using var session = new RealtimePreviewSession();
            var input = new ScenePreviewSource(fixture, project, core, ffmpeg, 320, 480, 10);
            await session.LoadSceneAsync(input); await session.SeekAsync(1);
            var baseline = session.LastFrame!;
            var baselineParameters = await session.ReadActorParametersAsync(actorId);
            var automatic = baselineParameters.Single(p => p.Id == parameter);
            Check(baselineParameters.Count == expectedCount && baseline.Targets.Count == 1
                && baseline.Pixels.Rgba.Where((_, i) => i % 4 == 3).Any(alpha => alpha > 0),
                "The isolated source actor exposes actual model parameters and nontransparent native pixels");
            File.WriteAllBytes(Path.Combine(outputDirectory, "live2d-original.png"), Png.Encode(baseline.Pixels));
            var pin = new JsonObject { ["cmd"] = "Director.Parameter", ["TargetName"] = actorId,
                ["Parameter"] = parameter, ["Value"] = pinned, ["Duration"] = 0, ["AtFrame"] = 30 };
            await session.ApplySceneEditsAsync([pin]);
            var pinnedFrame = session.LastFrame!;
            var actualPin = (await session.ReadActorParametersAsync(actorId)).Single(p => p.Id == parameter);
            Check(actualPin.IsOverridden && Math.Abs(actualPin.Value - pinned) < 1e-5
                && pinnedFrame.Frame == baseline.Frame && pinnedFrame.SessionId == baseline.SessionId
                && pinnedFrame.ActorAnimations!.SequenceEqual(baseline.ActorAnimations!),
                "A paused parameter slider changes the actual Core value without advancing or recreating the performance");
            Check(!pinnedFrame.Pixels.Rgba.SequenceEqual(baseline.Pixels.Rgba), "The real parameter edit deforms the exported native pixels");
            File.WriteAllBytes(Path.Combine(outputDirectory, "live2d-parameter.png"), Png.Encode(pinnedFrame.Pixels));
            await session.ApplySceneEditsAsync([new JsonObject { ["cmd"] = "Director.Parameter", ["TargetName"] = actorId,
                ["Parameter"] = parameter, ["Operation"] = "release", ["Duration"] = 0 }]);
            var released = (await session.ReadActorParametersAsync(actorId)).Single(p => p.Id == parameter);
            Check(!released.IsOverridden && Math.Abs(released.Value - automatic.Value) < 1e-5
                && baseline.Pixels.Rgba.SequenceEqual(session.LastFrame!.Pixels.Rgba),
                "Paused release restores the same automatically evaluated source pose and pixels at the same frame");

            await session.ApplySceneEditsAsync([pin]);
            var exported = (JsonObject)source.DeepClone();
            exported["commands"]!.AsArray().Insert(2, pin.DeepClone());
            string editedProject = Path.Combine(outputDirectory, "live2d.edited.project.json");
            File.WriteAllText(editedProject, exported.ToJsonString());
            await session.CommitSceneProjectAsync(input with { ProjectPath = editedProject });
            Check(session.LastFrame!.SessionId == baseline.SessionId && session.LastFrame.Frame == 30,
                "Committing the saved native parameter command retains the current session and playhead");
            await session.SeekAsync(1);
            Check(pinnedFrame.Pixels.Rgba.SequenceEqual(session.LastFrame!.Pixels.Rgba),
                "Replaying the saved export project reproduces the live parameter edit exactly");
            var beforeTransform = session.LastFrame!; var bounds = beforeTransform.Targets.Single();
            var transform = new IsolatedActorTransform(.6, .55, .8, 0, .6);
            await session.SetIsolatedTransformAsync(transform);
            var transformed = session.LastFrame!; var moved = transformed.Targets.Single();
            Check(transformed.SessionId == beforeTransform.SessionId && transformed.Frame == beforeTransform.Frame
                && transformed.ActorAnimations!.SequenceEqual(beforeTransform.ActorAnimations!),
                "Model position/scale/opacity redraw without reloading or advancing native animation");
            Check(Math.Abs(moved.Width / bounds.Width - .8) < .001 && Math.Abs(moved.Height / bounds.Height - .8) < .001
                && !transformed.Pixels.Rgba.SequenceEqual(beforeTransform.Pixels.Rgba),
                "Isolation scale changes the actual drawable bounds and pixels");
            File.WriteAllBytes(Path.Combine(outputDirectory, "live2d-transformed.png"), Png.Encode(transformed.Pixels));
            exported["output"]!["isolateTransform"] = ActorIsolation.TransformJson(transform);
            File.WriteAllText(editedProject, exported.ToJsonString());
            await session.CommitSceneProjectAsync(input with { ProjectPath = editedProject });
            await session.SeekAsync(1);
            Check(transformed.Pixels.Rgba.SequenceEqual(session.LastFrame!.Pixels.Rgba),
                "Saving and replaying the isolation transform reproduces the live native image");
            await session.SetIsolatedTransformAsync(transform with { Opacity = 0 });
            Check(session.LastFrame!.Targets.Count == 0 && session.LastFrame.Pixels.Rgba.Where((_, i) => i % 4 == 3).All(alpha => alpha == 0),
                "Hiding the model produces true transparent pixels and no hit target");

            await Task.Run(() =>
            {
                using var renderer = new AdvStoryRenderer(archive, "intl", core, "10462", "zh-Hans", 320, 480, project, ffmpeg, 1);
                _ = renderer.Frame(30, true); var actor = renderer.Scene.Character(actorId)!;
                var animation = actor.PreviewAnimation(); var nativeSlot = renderer.Scene.StageActors.Single();
                _ = renderer.Frame(30, true, [new JsonObject { ["cmd"] = "Director.Actor", ["TargetName"] = actorId,
                    ["Values"] = new JsonObject { ["controls"] = new JsonObject { ["physics"] = false, ["breath"] = false, ["blink"] = false, ["loop"] = false } }, ["Duration"] = 0 }]);
                Check(ReferenceEquals(actor, renderer.Scene.Character(actorId)) && renderer.Scene.StageActors.Single().Slot == nativeSlot.Slot
                    && actor.PreviewAnimation() == animation && !actor.Spec.Physics && !actor.Spec.Breath && !actor.Spec.Blink && !actor.Spec.Loop,
                    "Live controller edits reach the actual native actor while preserving slot ownership and animation history");
            });
            await ActorCanvasNativeTests.RunAsync(input, parameter, pinned, Check, report, outputDirectory);
            await PreviewQualityNativeTests.RunAsync(input, actorId, outputDirectory, Check);
            report["ok"] = true;
            Console.WriteLine($"Passed {checks.Count} real native Live2D model, parameter, release, transform and replay checks."); return 0;
        }
        catch (Exception error) { report["exception"] = error.ToString(); throw; }
        finally { File.WriteAllText(Path.Combine(outputDirectory, "live2d-report.json"), report.ToJsonString(new() { WriteIndented = true })); }
    }
}
