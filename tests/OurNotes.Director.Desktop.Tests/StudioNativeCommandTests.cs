using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

public static class StudioNativeCommandTests
{
    public static void Run(Action<bool, string> check)
    {
        Catalog(check);
        Commands(check);
        Dialogue(check);
        Timing(check);
        SparseAndNative(check);
        FullCanvas(check);
        string folder = Path.Combine(Path.GetTempPath(), "ournotes-native-command-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { SourceArchive(folder, check); SnapshotTiming(folder, check); }
        finally { Directory.Delete(folder, true); }
    }

    static void Catalog(Action<bool, string> check)
    {
        string[] names = AdvProgram.Names.OfType<string>().Concat(AdvProgram.DirectorCommands).ToArray();
        check(StudioCommandCatalog.All.Select(item => item.Command).SequenceEqual(names),
            "native instruction catalog covers the actual registry in enum order without inventing commands");
        foreach (var item in StudioCommandCatalog.All)
        {
            var row = StudioCommandCatalog.Create(item.Command, "actor");
            var native = AdvProgram.Row(row, "catalog");
            DirectorValidation.Check(native);
            if (!native.Ignored) _ = AdvPlayback.FixedDelayFrames(native);
            int index = Array.IndexOf(AdvProgram.Names, item.Command);
            check(native.Command == item.Command && item.CommandId == (index < 0 ? null : index),
                "native instruction template parses with its real command ID: " + item.Command);
        }
        var fallback = StudioCommandCatalog.Get("Future.Unrecognized");
        check(fallback.Command == "Future.Unrecognized" && fallback.Label == "Future.Unrecognized" && fallback.CommandId is null
            && Throws<ArgumentException>(() => StudioCommandCatalog.Create(fallback.Command)),
            "unknown instruction descriptors remain visible but have no fabricated executable defaults");
        check(StudioCommandCatalog.Create("Character")["Model"]!.ToString() == ""
            && AdvProgram.Row(StudioCommandCatalog.Create("Character"), "character").Ignored
            && AdvProgram.Row(StudioCommandCatalog.Create("Director.Parameter", "a"), "parameter").Ignored,
            "resource-dependent command templates are disabled until a real model or parameter is selected");
        check(StudioCommandCatalog.Create("Talk")["VoiceIDs"] is JsonArray
            && !StudioCommandCatalog.Create("Talk").ContainsKey("Text"),
            "Talk template uses native AdvTextID and VoiceIDs fields instead of an unconsumed inline text field");
    }

    static void Commands(Action<bool, string> check)
    {
        var scene = Raw("""
            [{"cmd":"Talk","AdvTextID":"shared","VoiceIDs":[9223372036854775807]},
             {"cmd":"Wait","future":{"large":1e999,"array":[1,2]}},
             {"cmd":"Future.Command","rawData":123456789012345678901234567890},null,7]
            """, "[{\"cmd\":\"Character\",\"TargetName\":\"a\",\"Model\":\"real-model\"}]",
            "[{\"cmd\":\"Expression\",\"TargetName\":\"a\",\"ExpressionName\":\"end\",\"IgnoreData\":true}]");
        string original = scene.Document.Text;
        var initialIds = scene.Commands.Select(row => row.Id).ToArray();
        check(scene.CanEditCommands && !scene.CanEditTimeline && scene.Commands.Count == 7
            && scene.Commands.Count(row => !row.IsObject) == 2 && scene.Commands.Any(row => !row.IsKnownCommand && row.Command == "Future.Command"),
            "Talk, Wait, unknown commands and malformed rows remain in the editable complete instruction list");
        var sourceActor = scene.Actors.Single();
        check(sourceActor.Id == "a" && sourceActor.Model == "real-model", "raw Character rows expose their targets and explicit models without studio keys");
        int changed = 0; scene.Changed += () => changed++;
        scene.SelectedCommandId = initialIds[2]; scene.Time = 40;
        check(changed == 0 && !scene.CanUndo && scene.Time == 40 && scene.Document.Text == original,
            "selection and automatic playhead navigation do not alter raw JSON or cap it at a guessed three seconds");
        var first = scene.Commands.Single(row => row.Section == "commands" && row.Index == 0);
        scene.MoveCommand(first.Id, "commands", 1);
        check(scene.Commands.Single(row => row.Id == first.Id).Index == 1 && scene.SelectedCommandId == first.Id,
            "moving down uses the requested final index after removal and preserves command identity");
        check(scene.Undo() && scene.Document.Text == original && scene.Commands.Select(row => row.Id).SequenceEqual(initialIds),
            "undo restores exact original numeric tokens, row order and stable selection identities");
        check(scene.Redo() && scene.Commands.Single(row => row.Id == first.Id).Index == 1,
            "redo restores the same command ID instead of allocating a replacement");
        scene.Undo();
        string copy = scene.DuplicateCommand(first.Id);
        check(copy != first.Id && scene.Commands.Single(row => row.Id == copy).Index == 1
            && JsonNode.DeepEquals(scene.Commands.Single(row => row.Id == copy).Data, first.Data),
            "copying a row gives it independent editor identity while preserving all native values");
        scene.MoveCommand(copy, "finalize", 1);
        check(scene.Commands.Single(row => row.Id == copy) is { Section: "finalize", Index: 1 }
            && scene.Commands.Single(row => row.Id == first.Id).Data["VoiceIDs"]![0]!.ToJsonString() == "9223372036854775807",
            "cross-section moves retain integer resource IDs and leave the original row intact");
        var finish = scene.Commands.First(row => row.Section == "finalize");
        scene.SetCommandEnabled(finish.Id, true);
        check(scene.Commands.Single(row => row.Id == finish.Id).Data["IgnoreData"]!.GetValue<bool>() == false,
            "enabling an original boolean IgnoreData flag preserves its native representation");
        var initialize = scene.Commands.First(row => row.Section == "initialize");
        scene.SetCommandEnabled(initialize.Id, false);
        check(!scene.Commands.Single(row => row.Id == initialize.Id).Enabled && scene.Actors.Count == 0,
            "disabling initialize Character affects both native row data and the visible target list");
        string unchanged = scene.Document.Text; int events = changed;
        scene.SetCommandEnabled(initialize.Id, false);
        check(scene.Document.Text == unchanged && changed == events, "setting the existing enabled state does not create an edit");
        var wait = scene.Commands.Single(row => row.Command == "Wait");
        scene.PatchCommand(wait.Id, JsonNode.Parse("{\"future\":{\"new\":true}}")!.AsObject());
        var patched = scene.Commands.Single(row => row.Id == wait.Id).Data;
        check(patched["future"]!["large"]!.ToJsonString() == "1e999" && patched["future"]!["array"]!.AsArray().Count == 2
            && patched["future"]!["new"]!.GetValue<bool>(), "recursive command field editing preserves unrelated unknown fields and numeric lexemes");
        var malformed = scene.Commands.First(row => !row.IsObject);
        scene.UpdateCommand(malformed.Id, new() { ["cmd"] = "Delay", ["Frames"] = 2 });
        check(scene.Commands.Single(row => row.Id == malformed.Id).Command == "Delay",
            "a malformed native row can be replaced in place without changing its editor identity");
        var otherMalformed = scene.Commands.Single(row => !row.IsObject); scene.RemoveCommand(otherMalformed.Id);
        check(scene.Commands.All(row => row.IsObject), "malformed native rows can be removed without discarding the rest of the script");
        unchanged = scene.Document.Text; bool undo = scene.CanUndo; events = changed;
        check(Throws<ArgumentOutOfRangeException>(() => scene.MoveCommand(first.Id, "commands", 999))
            && scene.Document.Text == unchanged && scene.CanUndo == undo && changed == events,
            "an invalid move rolls back the document and produces no history or change notification");
        scene.BeginEdit("combined native edit");
        scene.PatchCommand(wait.Id, new() { ["Parameter1"] = "one" }); scene.SetCommandEnabled(wait.Id, false); scene.CommitEdit();
        check(scene.Undo() && scene.Document.Text == unchanged, "a compound native edit undoes as one transaction");
        scene.BeginEdit(); scene.RemoveCommand(first.Id); scene.CancelEdit();
        check(scene.Document.Text == unchanged && scene.Commands.Any(row => row.Id == first.Id),
            "canceling an instruction transaction restores the actual source row and its stable identity");
        string id = scene.AddActor("new-model");
        check(scene.Commands.Any(row => row.Command == "Character" && row.Target == id && row.Data["Model"]!.ToString() == "new-model")
            && scene.Commands.Any(row => row.Command == "Director.Actor" && row.Target == id),
            "adding a real actor is available in an original story with unknown Talk/Wait timing");
    }

    static void Dialogue(Action<bool, string> check)
    {
        var scene = Raw("[{\"cmd\":\"Talk\",\"AdvTextID\":\"shared\"},{\"cmd\":\"Talk\",\"AdvTextID\":\"shared\"}]");
        scene.EditDocument(doc =>
        {
            var root = doc.Root;
            root["text"] = JsonNode.Parse("""
                {"shared":{"simplifiedChinese":"原对白","english":"original","ja":"別名","future":1e999},"unrelated":{"english":"keep"}}
                """); doc.ReplaceText(root.ToJsonString());
        });
        // Reopen so one undo below specifically measures the text-edit transaction.
        scene = StudioSceneDocument.Parse(scene.Document.Text);
        var rows = scene.Commands.ToArray(); string before = scene.Document.Text;
        check(scene.GetCommandText(rows[0].Id) == "原对白" && rows[0].Summary.Contains("原对白", StringComparison.Ordinal),
            "imported Talk rows display their current-language dialogue rather than only an opaque text ID");
        scene.SetCommandText(rows[0].Id, "修改后的一句");
        string privateId = scene.Commands.Single(row => row.Id == rows[0].Id).Data["AdvTextID"]!.ToString();
        check(privateId.StartsWith("studio-text-", StringComparison.Ordinal) && scene.GetCommandText(rows[0].Id) == "修改后的一句"
            && scene.GetCommandText(rows[1].Id) == "原对白", "editing shared original dialogue forks only the selected instruction's text reference");
        check(scene.Document.Root["text"]![privateId]!["english"]!.ToString() == "original"
            && scene.Document.Root["text"]![privateId]!["future"]!.ToJsonString() == "1e999"
            && scene.Document.Root["text"]!["unrelated"]!["english"]!.ToString() == "keep",
            "text edits retain other translations, exact unknown values and unrelated text entries");
        check(scene.Undo() && scene.Document.Text == before && !scene.CanUndo,
            "one undo restores both the original AdvTextID and complete text table");
        scene.Redo(); scene.SetCommandText(rows[0].Id, "再次修改");
        check(scene.Commands.Single(row => row.Id == rows[0].Id).Data["AdvTextID"]!.ToString() == privateId,
            "repeated edits reuse the same private dialogue ID instead of accumulating a new entry per keystroke");
        string duplicate = scene.DuplicateCommand(rows[0].Id); scene.SetCommandText(duplicate, "duplicate English", "en");
        string duplicateId = scene.Commands.Single(row => row.Id == duplicate).Data["AdvTextID"]!.ToString();
        check(duplicateId != privateId && scene.GetCommandText(duplicate, "en") == "duplicate English"
            && scene.GetCommandText(rows[0].Id, "en") == "original" && scene.GetCommandText(duplicate) == "再次修改",
            "a copied dialogue forks again on edit while preserving its other inherited translations");
        scene.SetCommandText(duplicate, "日本語", "ja");
        check(scene.Document.Root["text"]![duplicateId]!["japanese"]!.ToString() == "日本語"
            && scene.Document.Root["text"]![duplicateId]!["ja"]!.ToString() == "日本語",
            "supported language aliases are synchronized with native language fields");
        before = scene.Document.Text;
        check(Throws<ArgumentException>(() => scene.SetCommandText(duplicate, "bad", "xx")) && scene.Document.Text == before,
            "unsupported dialogue languages fail before changing text or history");
    }

    static void Timing(Action<bool, string> check)
    {
        var scene = Raw("[{\"cmd\":\"Talk\",\"AdvTextID\":\"a\"},{\"cmd\":\"Wait\"}]");
        string source = scene.Document.Text; int changes = 0, notifications = 0;
        scene.Changed += () => changes++; scene.TimingChanged += () => notifications++;
        check(scene.DurationMode == StudioDurationMode.Automatic && !scene.IsDurationResolved && scene.ResolvedDuration is null
            && scene.PreviewLimitDuration == 36000, "original-story duration starts automatic and unresolved rather than claiming a guessed duration");
        var partial = new NativePlaybackTiming(scene.NativeProjectHash, false, null, null,
            [new("commands", 0, "Talk", 0, null, 0, "")], AnalyzedFrame: 45);
        check(scene.ApplyNativeTiming(partial) && scene.ResolvedDuration is null && !scene.Commands[0].TimingKnown,
            "partial native execution exposes an unfinished command without treating analyzed progress as the end");
        check(!scene.ApplyNativeTiming(partial with { AnalyzedFrame = 30 }) && !scene.ApplyNativeTiming(partial with { ProjectHash = "stale" }),
            "stale and regressing timing callbacks cannot overwrite current observations");
        var finished = new NativePlaybackTiming(scene.NativeProjectHash, true, 90, 91d / 30,
            [new("commands", 0, "Talk", 0, 30, 0, ""), new("commands", 1, "Wait", 30, 60, 1, ""),
             new("commands", 0, "Talk", 60, 90, 2, "")], AnalyzedFrame: 90);
        check(scene.ApplyNativeTiming(finished) && scene.IsDurationResolved && Near(scene.Duration, 91d / 30)
            && Near(scene.ResolvedDuration!.Value, 91d / 30) && scene.PlaybackEndTime == 3,
            "automatic duration includes the real native end frame while seeking stops at its frame index");
        scene.Time = scene.Duration;
        check(scene.Time == 3 && changes == 0 && notifications == 2 && !scene.CanUndo && scene.Document.Text == source,
            "native timing and endpoint clamping do not write JSON or create document edits");
        var repeated = scene.TimelineCommands.Where(row => row.Command == "Talk").ToArray();
        check(repeated.Length == 2 && repeated[0].Id != repeated[1].Id && repeated[0].CommandId == repeated[1].CommandId
            && repeated.All(row => row.IsNativeTiming) && repeated[0].Start == 0 && repeated[1].Start == 2,
            "native timeline represents repeated execution of a source row as distinct observed occurrences");
        check(!scene.ApplyNativeTiming(partial) && scene.IsDurationResolved,
            "an older incomplete callback cannot replace a completed native duration");
        check(Throws<ArgumentException>(() => scene.ApplyNativeTiming(finished with { Duration = 3 }))
            && Throws<ArgumentException>(() => scene.ApplyNativeTiming(partial with { Duration = 3 })),
            "model rejects missing-final-frame duration and fabricated totals on incomplete runs");
        scene.PatchCommand(scene.Commands[0].Id, new() { ["AdvTextID"] = "different" });
        check(!scene.IsDurationResolved && !scene.ApplyNativeTiming(finished),
            "editing a native instruction invalidates its previous observed duration and execution trace");
        var raw = scene.Document.Root["commands"]!.DeepClone(); scene.SetDurationMode(StudioDurationMode.Fixed, 5);
        var fixedScene = StudioSceneDocument.Parse(scene.ToJson());
        check(fixedScene.DurationMode == StudioDurationMode.Fixed && fixedScene.Duration == 5
            && JsonNode.DeepEquals(new JsonArray(fixedScene.Document.Root["commands"]!.AsArray().Skip(1).Select(row => row?.DeepClone()).ToArray()), raw),
            "fixed preview/export range round-trips as inert metadata without truncating or retiming original instructions");
        scene.SetDurationMode(StudioDurationMode.Automatic);
        check(StudioSceneDocument.Parse(scene.ToJson()).DurationMode == StudioDurationMode.Automatic && !scene.IsDurationResolved,
            "switching back to automatic is persistent and still requires actual native completion");
        scene.Undo(); check(scene.DurationMode == StudioDurationMode.Fixed && scene.Duration == 5, "undo restores the explicit fixed range and mode together");

        var extended = StudioSceneDocument.Create(new("42", "intl", "zh-Hans"), 5);
        string initial = extended.Document.Text;
        extended.InsertCommand("commands", 0, new() { ["cmd"] = "Delay", ["Frames"] = 60 });
        int endFrame = CompletionFrame(extended.Document.Root);
        check(extended.DurationMode == StudioDurationMode.Automatic && extended.PreviewLimitDuration > 7 && !extended.IsDurationResolved
            && endFrame == 210 && extended.Document.Root["commands"]![0]!["Frames"]!.GetValue<int>() == 60,
            "native scheduling changes extend an implicit five-second preset through its complete seven-second execution without an old fixed cap");
        extended.ApplyNativeTiming(new(extended.NativeProjectHash, true, endFrame, (endFrame + 1d) / 30, [], AnalyzedFrame: endFrame));
        check(Near(extended.ResolvedDuration!.Value, 211d / 30)
            && StudioSceneDocument.Parse(extended.ToJson()).DurationMode == StudioDurationMode.Automatic,
            "the extended native program resolves from its actual completion frame and saves automatic duration mode");
        check(extended.Undo() && extended.Document.Text == initial && extended.DurationMode == StudioDurationMode.Fixed && extended.Duration == 5,
            "undo of the first native edit restores both the original preset and its implicit authoring range");
        var explicitRange = StudioSceneDocument.Create(new("42", "intl", "zh-Hans"), 5);
        explicitRange.SetDurationMode(StudioDurationMode.Fixed, 5);
        explicitRange.InsertCommand("commands", 0, new() { ["cmd"] = "Delay", ["Frames"] = 60 });
        check(explicitRange.DurationMode == StudioDurationMode.Fixed && explicitRange.Duration == 5 && explicitRange.PreviewLimitDuration == 5
            && CompletionFrame(explicitRange.Document.Root) == 210,
            "an explicitly chosen fixed range remains fixed after native edits while the complete underlying program is preserved");
    }

    static void SparseAndNative(Action<bool, string> check)
    {
        var scene = StudioSceneDocument.Create(new("42", "intl", "zh-Hans"), 1);
        string actor = scene.AddActor("fixture-model");
        scene.SetActorTransform(actor, .2, .5, 1, 0, 0, false); scene.SetActorTransform(actor, .8, .5, 1, 0, 1, true);
        string original = scene.Document.Text;
        var key = scene.Commands.Single(row => row.IsKeyframe && row.Data["Values"]?["position"] is not null);
        var updated = (JsonObject)key.Data.DeepClone(); updated["Values"]!["position"]![0] = .4; scene.UpdateCommand(key.Id, updated);
        check(scene.CanEditTimeline && Near(scene.Sample(.5).Actors[0].Transform.X, .3)
            && scene.Commands.Any(row => row.Id == key.Id), "editing only a sparse key's native values synchronizes its derived frames and retains the selected key ID");
        check(Near(PlaybackX(scene.Document.Root, 15), .3), "command value editing changes actual native row playback at the selected intermediate frame");
        scene.Undo(); check(scene.Document.Text == original && scene.CanEditTimeline, "undo of key-field editing restores the exact original sparse keys and native cache");
        var rawCommands = scene.Document.Root["commands"]!.AsArray();
        string newId = scene.InsertCommand("commands", 0, new() { ["cmd"] = "Delay", ["Frames"] = 3 });
        var after = scene.Document.Root["commands"]!.AsArray();
        check(!scene.CanEditTimeline && scene.CanEditCommands && after[0]!["Frames"]!.GetValue<int>() == 3
            && rawCommands.Select(row => row?.ToJsonString()).SequenceEqual(after.Skip(1).Take(rawCommands.Count).Select(row => row?.ToJsonString())),
            "raw scheduling edits retain every existing sparse/cache row and disable only automatic sparse recompilation");
        check(StudioSceneDocument.Parse(scene.ToJson()).Document.Root["commands"]![0]!["Frames"]!.GetValue<int>() == 3,
            "reopening raw scheduling edits cannot silently rebuild over the inserted native instruction");
        scene.Undo(); check(scene.Document.Text == original && scene.CanEditTimeline && !scene.Commands.Any(row => row.Id == newId),
            "undo restores sparse editing after a raw scheduling change");
        var bake = scene.Commands.First(row => row.Data["Key"]?.ToString().StartsWith("ournotes-studio/1/bake/", StringComparison.Ordinal) == true);
        string bakeKey = bake.Data["Key"]!.ToString(); scene.RemoveCommand(bake.Id);
        check(!scene.CanEditTimeline && !scene.Commands.Any(row => row.Data["Key"]?.ToString() == bakeKey)
            && !StudioSceneDocument.Parse(scene.ToJson()).Commands.Any(row => row.Data["Key"]?.ToString() == bakeKey),
            "explicit deletion of a native cache row remains deleted instead of being regenerated on reload");
        scene.Undo(); var section = scene.Document.GetSection("commands")!;
        section.Insert(0, (JsonNode)new JsonObject { ["cmd"] = "Delay", ["Frames"] = 4 }); scene.ReplaceCommandSection("commands", section);
        check(!scene.CanEditTimeline && scene.Document.Root["commands"]![0]!["Frames"]!.GetValue<int>() == 4,
            "whole-section native replacement receives the same protection from stale sparse recompilation");
    }

    static void FullCanvas(Action<bool, string> check)
    {
        var metadata = JsonNode.Parse("""
            {"canvas":{"width":6000.0,"height":9000.0,"originX":3000.0,"originY":4500.0,"pixelsPerUnit":6000.0,"mocVersion":5},
             "defaultMotion":"actual-idle","defaultExpression":"actual-default"}
            """)!.AsObject();
        string original = metadata.ToJsonString();
        var scene = StudioSceneDocument.CreateSingleActor(new("42", "intl", "zh-Hans"), "real-model", metadata);
        var output = scene.Document.Output; var actor = scene.Actors.Single();
        check(output is { Width: 6000, Height: 9000, Supersample: 1, Codec: "cineform", Clean: true, Letterbox: "none" }
            && scene.Sample(0).Background[3]!.GetValue<double>() == 0,
            "single-actor transparent preset keeps the real 6000x9000 canvas at one sample per pixel");
        check(actor.Transform.X == .5 && actor.Transform.Y == .5 && actor.Transform.Height == 1
            && actor.Motion == "actual-idle" && actor.Expression == "actual-default" && !scene.CanUndo,
            "full-size preset centers the native canvas and retains actual model default motion and expression");
        check(!scene.Document.Validate().HasErrors && scene.Document.Validate().RequiresNativeValidation && metadata.ToJsonString() == original,
            "large-canvas syntax validation passes without changing source metadata or claiming the model was rendered");
        string before = scene.Document.Text;
        check(Throws<ArgumentException>(() => scene.SetOutput(supersample: 2)) && scene.Document.Text == before && !scene.CanUndo,
            "supersampling that exceeds the shared pixel budget fails atomically instead of silently reducing native dimensions");
        var offset = JsonNode.Parse("{\"canvas\":{\"width\":1001,\"height\":1501,\"originX\":400,\"originY\":600}}")!.AsObject();
        var padded = StudioSceneDocument.CreateSingleActor(new("42", "intl", "zh-Hans"), "real-model", offset, motion: "explicit-motion");
        var transform = padded.Actors.Single().Transform;
        check(padded.Document.Output.Width == 1008 && padded.Document.Output.Height == 1504
            && Near(transform.Height * padded.Document.Output.Height, 1501),
            "codec alignment adds only transparent padding while preserving one output pixel per native canvas pixel");
        double canvasLeft = transform.X * 1008 - 400, canvasTop = transform.Y * 1504 - (1501 - 600);
        check(Near(canvasLeft, 3.5) && Near(canvasTop, 1.5) && padded.Actors.Single().Motion == "explicit-motion",
            "noncentral model origins position the complete padded canvas symmetrically without cropping");
        offset["canvas"]!["height"] = RenderDimensions.MaxSide + 1;
        check(Throws<ArgumentException>(() => StudioSceneDocument.CreateSingleActor(new("42", "intl", "zh-Hans"), "real-model", offset)),
            "canvas dimensions above the native limit require an explicit user size instead of a hidden downscale");
    }

    static void SourceArchive(string folder, Action<bool, string> check)
    {
        var episode = JsonNode.Parse("""
            {"commands":[{"Command":2,"AdvTextID":"line","VoiceIDs":[9223372036854775807]},
              {"cmd":"Future.Command","untouched":1e999}],
             "text":{"line":{"simplifiedChinese":"源对白","english":"source"},"source-only":{"english":"kept"}}}
            """)!.AsObject();
        var settings = JsonNode.Parse("""
            {"_initializeEpisodes":[{"Command":23,"TargetName":"source-target","TargetAssetName":"source-address","large":123456789012345678901234567890}],
             "_finalizeEpisodes":[{"cmd":"Expression","TargetName":"source-target","ExpressionName":"end"}],"_waitCommandLingeringTimeOnAutoPlay":0.5}
            """)!.AsObject();
        var manifest = new JsonObject { ["files"] = new JsonObject() };
        Directory.CreateDirectory(Path.Combine(folder, "index")); File.WriteAllText(Path.Combine(folder, "index", "stats.json"), "{}");
        Add("story.json", new JsonObject { ["episode"] = "episode.json", ["scene"] = "scene.json",
            ["models"] = new JsonObject { ["Character/Live2D/source-address"] = "verified-model-id" } });
        Add("episode.json", episode); Add("scene.json", new JsonObject { ["settings"] = new JsonObject { ["playerSettings"] = settings.DeepClone() } });
        string manifests = Path.Combine(folder, "sites", "intl", "stories"); Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, "42.json"), manifest.ToJsonString());
        var archive = new Archive(folder);
        const string inherited = """
            {"format":"ournotes-director/project","version":1,"source":{"adv":"42","site":"intl","language":"zh-Hans"},
             "initialize":null,"finalize":[],"text":{"line":{"simplifiedChinese":"项目覆盖"}}}
            """;
        var scene = StudioSceneDocument.Parse(inherited); int changes = 0; scene.Changed += () => changes++;
        check(scene.CommandSections.Count(row => row.IsInherited) == 2
            && Throws<InvalidOperationException>(() => scene.InsertCommand("commands", 0, StudioCommandCatalog.Create("Talk")))
            && scene.Document.Text == inherited, "inherited sections are explicit placeholders and cannot be silently replaced by inserting one row");
        var result = scene.ExpandInheritedSections(archive);
        check(result is { Initialize: 1, Commands: 2, Finalize: 0, TextEntries: 1 } && changes == 1
            && scene.CommandSections.All(row => !row.IsInherited) && scene.Commands.Count == 3,
            "source expansion reads verified archive assets and materializes only inherited initialize/commands arrays in one transaction");
        check(scene.Document.Root["finalize"]!.AsArray().Count == 0
            && scene.Document.Root["text"]!["line"]!["english"] is null
            && scene.Document.Root["text"]!["source-only"]!["english"]!.ToString() == "kept",
            "explicit empty sections and whole-entry project text overrides retain the same precedence as native source merging");
        check(scene.Commands.Any(row => row.Command == "Talk" && row.CommandId == 2 && row.Summary.Contains("项目覆盖", StringComparison.Ordinal))
            && scene.Commands.Single(row => row.Command == "Future.Command").Data["untouched"]!.ToJsonString() == "1e999",
            "expanded original commands resolve numeric enum names and visible dialogue while retaining unsupported source content");
        string characterId = scene.Commands.Single(row => row.Command == "Character").Id;
        check(scene.Actors.Single().Model == "verified-model-id" && scene.ResolveCommandModel(characterId) == "verified-model-id"
            && !scene.Commands.Single(row => row.Id == characterId).Data.ContainsKey("Model"),
            "source model associations power actor and action selection without rewriting original TargetAssetName rows");
        string expanded = scene.Document.Text;
        check(scene.Undo() && scene.Document.Text == inherited && scene.Redo() && scene.Document.Text == expanded
            && scene.Commands.Single(row => row.Command == "Character").Id == characterId,
            "archive expansion can be undone to exact inheritance and redone with the same command identities");
        string path = Path.Combine(folder, "saved-original.json"); scene.Save(path);
        var reopened = StudioSceneDocument.Open(path); string saved = reopened.Document.Text; int metadataChanges = 0;
        reopened.Changed += () => metadataChanges++;
        check(reopened.Actors.Single().Model == "" && reopened.ResolveSourceModels(archive) == 1
            && reopened.Actors.Single().Model == "verified-model-id" && reopened.Document.Text == saved
            && !reopened.CanUndo && metadataChanges == 0,
            "reopening an already-expanded original loads its model mappings separately without editing saved JSON");
        var foreground = StudioSceneDocument.Parse(reopened.ToJson()); foreground.ImportSourceMetadata(reopened);
        check(foreground.Actors.Single().Model == "verified-model-id" && !foreground.CanUndo,
            "a background-loaded clone can transfer source associations to the foreground editor after ReplaceJson");
        foreground.ImportSourceMetadata(foreground);
        check(foreground.Actors.Single().Model == "verified-model-id", "copying source metadata to the same editor leaves associations intact");
        foreground.SetSource(adv: "other");
        check(foreground.Actors.Single().Model == "" && Throws<ArgumentException>(() => foreground.ImportSourceMetadata(reopened)),
            "source changes cannot reuse stale model associations from a different story");
        var direct = StudioSceneDocument.Parse(inherited); string episodeText = episode.ToJsonString(), settingsText = settings.ToJsonString();
        direct.ExpandInheritedSections(episode, settings);
        check(JsonNode.DeepEquals(direct.Document.Root, scene.Document.Root) && episode.ToJsonString() == episodeText && settings.ToJsonString() == settingsText,
            "JSON source expansion follows the archive path's same lossless contract without mutating caller data");

        void Add(string logical, JsonObject value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value.ToJsonString()); string asset = "assets/" + Archive.Hash(bytes) + ".json";
            string path = Path.Combine(folder, "sites", "intl", asset); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
            manifest["files"]![logical] = new JsonObject { ["asset"] = asset, ["size"] = bytes.Length, ["stored"] = bytes.Length };
        }
    }

    static void SnapshotTiming(string folder, Action<bool, string> check)
    {
        var root = Raw("[{\"cmd\":\"Talk\"}]").Document.Root;
        root["initialize"]!.AsArray().Add((JsonNode)new JsonObject { ["cmd"] = "Director.Scene", ["Values"] = new JsonObject
            { ["backgroundImage"] = new JsonObject { ["path"] = "background.png" } } });
        var scene = StudioSceneDocument.Parse(root.ToJsonString(), baseDirectory: folder);
        using var snapshot = scene.Document.CreateSnapshot(Path.Combine(folder, "snapshots"));
        string snapshotHash = Archive.Hash(Encoding.UTF8.GetBytes(DirectorProject.Document(snapshot.FilePath).ToJsonString()));
        string rawHash = Archive.Hash(Encoding.UTF8.GetBytes(scene.Document.Root.ToJsonString()));
        check(snapshotHash != rawHash && snapshotHash == scene.NativeProjectHash
            && scene.ApplyNativeTiming(new(snapshotHash, false, null, null, [], AnalyzedFrame: 5)),
            "native timing accepts the real render snapshot hash after local images are anchored to the project directory");
        check(scene.ApplyNativeTiming(new(rawHash, true, 9, 10d / 30, [], AnalyzedFrame: 9)) && scene.IsDurationResolved,
            "direct project playback and temporary render snapshots share the same validated duration contract");
    }

    static StudioSceneDocument Raw(string commands, string initialize = "[]", string finalize = "[]")
        => StudioSceneDocument.Parse("{\"format\":\"ournotes-director/project\",\"version\":1,\"source\":{\"adv\":\"42\",\"site\":\"intl\",\"language\":\"zh-Hans\"},"
            + "\"initialize\":" + initialize + ",\"commands\":" + commands + ",\"finalize\":" + finalize + "}");
    static bool Near(double left, double right) => Math.Abs(left - right) < 1e-6;
    static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
    static double PlaybackX(JsonObject root, int frame)
    {
        AdvRow[] Rows(string section) => root[section]!.AsArray().Select((row, index) => AdvProgram.Row(row!, index.ToString())).ToArray();
        var player = new AdvPlayback(new AdvLoop(), new AdvProgram { Initialize = Rows("initialize"), Rows = Rows("commands"), Finalize = Rows("finalize") }) { Prepare = _ => [] };
        foreach (string name in new[] { "TalkWindow", "Character", "Director.Scene", "Director.Camera" }) player.Register(name, (_, _) => []);
        double x = .5; player.Register("Director.Actor", (row, _) =>
        {
            DirectorValidation.Check(row);
            if (row.Number("Duration") != 0) throw new InvalidOperationException("Expected baked native row");
            if (row.Data["Values"]?["position"] is JsonArray position) x = position[0]!.GetValue<double>();
            return [];
        });
        player.Start(); for (int index = 0; index < frame; index++) player.Step(); return x;
    }
    static int CompletionFrame(JsonObject root)
    {
        AdvRow[] Rows(string section) => root[section]!.AsArray().Select((row, index) => AdvProgram.Row(row!, index.ToString())).ToArray();
        var player = new AdvPlayback(new AdvLoop(), new AdvProgram { Initialize = Rows("initialize"), Rows = Rows("commands"), Finalize = Rows("finalize") }) { Prepare = _ => [] };
        foreach (string name in new[] { "TalkWindow", "Director.Scene", "Director.Camera" }) player.Register(name, (_, _) => []);
        player.Start(); for (int index = 0; index < 1000 && !player.Finished; index++) player.Step();
        return player.CompletionFrame ?? throw new InvalidOperationException("Fixture never finished");
    }
}
