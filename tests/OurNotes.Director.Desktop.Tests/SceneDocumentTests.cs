using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

public static class SceneDocumentTests
{
    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ournotes-scene-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Parsing(check);
            Validation(check);
            Editing(check);
            Files(folder, check);
            Templates(folder, check);
        }
        finally { Directory.Delete(folder, true); }
    }

    static void Parsing(Action<bool, string> check)
    {
        const string raw = " { \"format\" : \"ournotes-director/project\", \"version\":1, \"source\":{\"adv\":\"42\"}, \"text\": { \"large\":123456789012345678901234567890, \"tiny\":1.234567890123456789e-80, \"中文\":\"原文\" } } ";
        var document = SceneDocument.Parse(raw);
        check(document.Text == raw && document.IsModified && document.Kind == SceneDocumentKind.UnifiedProject,
            "scene paste retains original text and marks a new document unsaved");
        var detached = document.Root;
        detached["text"]!["中文"] = "被修改";
        check(document.Root["text"]!["中文"]!.GetValue<string>() == "原文", "scene Root is a detached copy");
        document.SetOutput(width: 1280);
        check(document.Root["text"]!["large"]!.ToJsonString() == "123456789012345678901234567890" &&
            document.Root["text"]!["tiny"]!.ToJsonString() == "1.234567890123456789e-80",
            "scene structured edits retain large integer and decimal source tokens");

        var fenced = SceneDocument.Parse("\ufeff \n```JSON\r\n" + raw + "\r\n```\n");
        check(fenced.Root["source"]!["adv"]!.GetValue<string>() == "42" && !fenced.Text.Contains("```", StringComparison.Ordinal),
            "scene accepts a BOM and a JSON Markdown fence");
        check(SceneDocument.TryParse("~~~\n{}\n~~~", out var tilde, out _) && tilde!.Kind == SceneDocumentKind.Unknown,
            "scene accepts an unlabelled tilde fence without pretending an empty object is renderable");

        const string broken = "```json\r\n{\r\n  \"名字\": 1,\r\n  \"x\": }\r\n```";
        int expectedOffset = broken.IndexOf('}');
        bool accepted = SceneDocument.TryParse(broken, out _, out var error);
        check(!accepted && error is { Line: 4, Column: 8 } && error.Offset == expectedOffset,
            "scene syntax error maps UTF-8 parser bytes to original fenced UTF-16 editor line and column");
        const string sameLine = "{\"中文\":1,\"x\":?}";
        SceneDocument.TryParse(sameLine, out _, out var unicodeError);
        check(unicodeError!.Offset == sameLine.IndexOf('?') && unicodeError.Column == sameLine.IndexOf('?') + 1,
            "scene same-line non-ASCII syntax positions count editor characters rather than UTF-8 bytes");
        check(!SceneDocument.TryParse("{\"a\":1,\"\\u0061\":2}", out _, out var duplicate) && duplicate!.Message.Contains("重复", StringComparison.Ordinal),
            "scene rejects duplicate escaped property names instead of losing one value");
        check(!SceneDocument.TryParse("[]", out _, out _) && !SceneDocument.TryParse("null", out _, out _) &&
            !SceneDocument.TryParse("", out _, out _) && !SceneDocument.TryParse("```json\n{}", out _, out _),
            "scene rejects arrays, null, empty input and incomplete fences");
        string before = document.Text;
        check(Throws<SceneDocumentParseException>(() => document.ReplaceText("{ bad")) && document.Text == before,
            "scene failed editor replacement preserves the prior document transaction");
        document.Format();
        check(document.Text == document.ToJson() && document.ToJson(false).Contains("123456789012345678901234567890", StringComparison.Ordinal),
            "scene explicit formatting does not narrow original numbers");
    }

    static void Validation(Action<bool, string> check)
    {
        var unknown = Project("""
            "futureRoot":{"mode":2},
            "source":{"adv":"42","futureSource":"kept"},
            "output":{"futureOutput":true},
            "commands":[]
            """);
        var result = unknown.Validate();
        check(result.HasErrors && result.Issues.Count(issue => issue.Code == "field.unknown") == 3 &&
            unknown.Root["futureRoot"]!["mode"]!.GetValue<int>() == 2,
            "scene unknown envelope fields are retained and all reported as native-incompatible");

        var original = Project("""
            "source":{"adv":"unavailable-source"},
            "commands":[{"cmd":"Character","TargetName":"actor","Model":"unavailable-model","UnconsumedGameField":123456789012345678901234567890}]
            """);
        result = original.Validate();
        check(!result.HasErrors && result.RequiresNativeValidation && result.Issues.Any(issue => issue.Code == "native.validation-required") &&
            result.Summary.Contains("尚未验证", StringComparison.Ordinal),
            "scene local validation neither loads fake archive assets nor presents a local pass as render success");
        check(original.Root["commands"]![0]!["UnconsumedGameField"]!.ToJsonString() == "123456789012345678901234567890",
            "scene preserves original game fields outside the Director extension schema");

        var bad = Project("""
            "source":{"adv":"42"},
            "output":{"width":15,"height":1080,"supersample":5,"clean":"true","codec":"bad","cineformQuality":"tiny","letterbox":"crop"},
            "commands":[
              {"cmd":"Director.Actor","TargetName":"actor","Duration":-1,"Values":{"opacity":1.1}},
              {"cmd":"Director.Camera","Values":{"fieldOfView":180}},
              {"cmd":"Director.Actor","TargetName":"actor","Values":{"height":1e999}},
              {"cmd":"Director.Camera","Values":{"framing":{"zoom":0}}},
              {"cmd":"Director.Actor","TargetName":"actor","Values":{"future":1}},
              {"cmd":"Delay","raw":0}
            ]
            """);
        result = bad.Validate();
        check(result.HasErrors && result.Issues.Count(issue => issue.Code == "row.invalid") == 6 &&
            result.Issues.Count(issue => issue.Path.StartsWith("$.output", StringComparison.Ordinal)) == 6,
            "scene validates Director ranges, finite values, extension keys, command IDs and typed output fields");

        var dimensions = Project("""
            "source":{"adv":"42"},"output":{"codec":"cineform","width":1919,"height":1080}
            """);
        check(!dimensions.Validate().HasErrors,
            "scene canvas is codec-independent; CineForm alignment is enforced when exporting video, not PNG or editing");
        dimensions.SetOutput(width: 1920, codec: "CFHD", cineformQuality: "HIGH+");
        check(!dimensions.Validate().HasErrors && dimensions.Output.Codec == "cineform" && dimensions.Output.CineformQuality == "high+",
            "scene uses the common encoder's codec and quality normalization");

        var legacy = SceneTemplates.Create("legacy-shot", "42", "model");
        var legacyTiming = legacy.Root;
        legacyTiming["fps"] = 31;
        legacy.ReplaceText(legacyTiming.ToJsonString());
        check(legacy.Validate().HasErrors, "scene legacy validation enforces native Shot fixed frame timing");
        var legacyUnknown = SceneDocument.Parse("""{"actors":[{"name":"a","model":"m","future":1}]}""");
        check(legacyUnknown.Validate().HasErrors && legacyUnknown.Root["actors"]![0]!["future"]!.GetValue<int>() == 1,
            "scene legacy imports retain and report unmapped actor fields");
        check(SceneDocument.Parse("""{"actors":[null]}""").Validate().HasErrors &&
            SceneDocument.Parse("""{"actors":[],"output":null}""").Validate().HasErrors,
            "scene malformed legacy nulls become validation issues rather than crashing validation");
        check(SceneDocument.Parse("""{"format":"future/project","anything":17}""").Validate().HasErrors,
            "scene unrecognized document versions remain editable but cannot pass local validation");
    }

    static void Editing(Action<bool, string> check)
    {
        var document = Project("""
            "source":{"adv":"42","site":"intl","language":"zh-Hans","extra":"keep"},
            "initialize":[],"finalize":[],
            "commands":[
              {"cmd":"Character","TargetName":"actor","Model":"model-a","rawGameData":{"x":7}},
              {"cmd":"Director.Actor","TargetName":"actor","Space":"screen","Duration":2,"Ease":"InOutSine","IsNoWait":1,"Values":{"position":[0.2,0.5],"height":0.9,"tint":[1,0.9,0.8,1]}},
              {"cmd":"Talk","Text":"keep original dialogue"},
              {"cmd":"Director.Actor","TargetName":"actor","Values":{"opacity":0.3}},
              {"cmd":"Director.Camera","Values":{"position":[1,2,3],"framing":{"x":0.1,"y":0.2,"zoom":1.5,"roll":8}}},
              {"cmd":"Director.Parameter","TargetName":"actor","Parameter":"a-real-name-is-not-assumed","Value":0},
              {"cmd":"Delay","Duration":3}
            ]
            """);
        var actors = document.GetActors();
        check(actors.Count == 2 && actors[0].Location == new SceneRowLocation("commands", 1) && actors[0].Model == "model-a" &&
            actors[1].Location.Index == 3 && actors[1].Space == "" && !actors[1].Values.ContainsKey("position"),
            "scene actor helpers identify exact timeline rows and retain inherited Space/position rather than inventing defaults");
        actors[0].Values["height"] = 100;
        check(document.Root["commands"]![1]!["Values"]!["height"]!.GetValue<double>() == .9,
            "scene actor helper values are detached from the live document");
        document.PatchActor(actors[1].Location, new() { ["opacity"] = .8 });
        var commands = document.Root["commands"]!.AsArray();
        check(commands.Count == 7 && commands[1]!["Duration"]!.GetValue<int>() == 2 &&
            commands[1]!["Ease"]!.GetValue<string>() == "InOutSine" && commands[1]!["IsNoWait"]!.GetValue<int>() == 1 &&
            commands[2]!["Text"]!.GetValue<string>() == "keep original dialogue" && commands[3]!["Values"]!["opacity"]!.GetValue<double>() == .8 &&
            commands[5]!["Parameter"]!.GetValue<string>() == "a-real-name-is-not-assumed",
            "scene actor patch leaves other actors, dialogue, timing and parameter script intact");
        document.PatchCamera(document.GetCameras()[0].Location,
            new() { ["framing"] = new JsonObject { ["zoom"] = 2 } }, duration: 1, isNoWait: true);
        var camera = document.Root["commands"]![4]!;
        check(camera["Values"]!["framing"]!["x"]!.GetValue<double>() == .1 && camera["Values"]!["framing"]!["roll"]!.GetValue<int>() == 8 &&
            camera["Values"]!["position"]!.AsArray().Count == 3 && camera["Duration"]!.GetValue<double>() == 1 && camera["IsNoWait"]!.GetValue<int>() == 1,
            "scene camera deep merge preserves independent world transform and framing fields");
        string before = document.Text;
        check(Throws<ArgumentException>(() => document.PatchActor(actors[0].Location, new() { ["height"] = -1 })) &&
            document.Text == before, "scene invalid visual patch is atomic");
        check(Throws<ArgumentException>(() => document.PatchActor(actors[0].Location, new() { ["position"] = new JsonArray(1, 2, 3) })) &&
            document.Text == before, "scene actor patch enforces the existing explicit coordinate space");
        document.SetSource(adv: "99", site: "jp", language: "ja");
        check(document.Source == new SceneSource("99", "jp", "ja") && document.Root["source"]!["extra"]!.GetValue<string>() == "keep",
            "scene source edit preserves unknown fields for explicit review");

        var inherited = SceneTemplates.Create("story-patch", "42");
        before = inherited.Text;
        check(Throws<InvalidOperationException>(() => inherited.AppendActor("a", new() { ["height"] = 1 })) && inherited.Text == before &&
            !inherited.Root.ContainsKey("commands"), "scene append cannot silently replace inherited story commands");
        inherited.ReplaceSection("commands", new JsonArray());
        var appended = inherited.AppendActor("a", new() { ["height"] = 1 });
        check(appended == new SceneRowLocation("commands", 0) && inherited.Root["commands"]!.AsArray().Count == 1,
            "scene explicit empty section allows intentional appends");
        inherited.ReplaceSection("commands", null);
        check(!inherited.Root.ContainsKey("commands"), "scene replacing a section with null restores source inheritance");

        var legacy = SceneDocument.Parse("""
            {"version":1,"duration":3,"actors":[{"name":"a","model":"m","motion":"idle","expression":"smile","x":0.5,"y":0.4}],
             "camera":{"x":0.2,"y":-0.1,"zoom":1.2,"roll":5},
             "tracks":[{"target":"a","property":"x","keys":[{"t":0,"value":0.5},{"t":3,"value":0.7}]}],
             "events":[{"t":1,"actor":"a","expression":"another"}]}
            """);
        var tracks = legacy.Root["tracks"]!.ToJsonString();
        var events = legacy.Root["events"]!.ToJsonString();
        legacy.PatchActor(legacy.GetActors()[0].Location, new() { ["position"] = new JsonArray(.25, .75), ["height"] = .8 });
        legacy.PatchCamera(legacy.GetCameras()[0].Location, new() { ["framing"] = new JsonObject { ["zoom"] = 2 } });
        legacy.SetOutput(width: 1280, height: 720, supersample: 3);
        check(legacy.Root["actors"]![0]!["x"]!.GetValue<double>() == .25 && legacy.Root["actors"]![0]!["motion"]!.GetValue<string>() == "idle" &&
            legacy.Root["camera"]!["x"]!.GetValue<double>() == .2 && legacy.Root["camera"]!["roll"]!.GetValue<int>() == 5 &&
            legacy.Root["tracks"]!.ToJsonString() == tracks && legacy.Root["events"]!.ToJsonString() == events &&
            legacy.Root["output"]!["renderScale"]!.GetValue<int>() == 3 && !legacy.Root["output"]!.AsObject().ContainsKey("supersample") &&
            !legacy.Validate().HasErrors, "scene legacy visual edits map fields while preserving full motion, expression, tracks and events");
        before = legacy.Text;
        check(Throws<NotSupportedException>(() => legacy.SetOutput(width: 1920, codec: "cineform")) && legacy.Text == before &&
            Throws<NotSupportedException>(() => legacy.PatchActor(legacy.GetActors()[0].Location, new() { ["height"] = .5 }, duration: 1)),
            "scene legacy helpers reject unrepresentable codec/timeline changes without partial writes");
    }

    static void Files(string folder, Action<bool, string> check)
    {
        string original = Path.Combine(folder, "original folder"), relocated = Path.Combine(folder, "new folder"), snapshots = Path.Combine(folder, "scratch");
        Directory.CreateDirectory(Path.Combine(original, "assets"));
        string image = Path.Combine(original, "assets", "背景 plate.png");
        File.WriteAllBytes(image, [1, 2, 3]); // No image decoder is needed for document/path validation.
        var document = Project("""
            "source":{"adv":"42"},
            "initialize":[{"cmd":"Director.Scene","Values":{"backgroundImage":{"path":"assets/背景 plate.png"}}}],
            "commands":[{"cmd":"Director.Scene","IgnoreData":1,"Values":{"backgroundImage":{"path":"assets/背景 plate.png"}}},
                        {"cmd":"Director.Scene","Values":{"backgroundImage":{"adv":"42","logicalPath":"assets/do-not-rebase.png"}}},
                        {"cmd":"Talk","Unconsumed":{"path":"do-not-rebase.png"}}],
            "finalize":[]
            """, original);
        string file = Path.Combine(original, "scene.json");
        document.Save(file);
        check(!document.IsModified && document.FilePath == file && document.BaseDirectory == original,
            "scene save establishes document identity and clean state");
        var opened = SceneDocument.Open(file);
        check(!opened.IsModified && opened.Text == File.ReadAllText(file) && opened.ResolveAssetPath("assets/背景 plate.png") == image,
            "scene open binds relative references to document directory");
        opened.SetSource(language: "ja");
        string unsaved = opened.Text, disk = File.ReadAllText(file);
        using (var snapshot = opened.CreateSnapshot(snapshots))
        {
            var staged = SceneDocument.Open(snapshot.FilePath);
            check(staged.Root["initialize"]![0]!["Values"]!["backgroundImage"]!["path"]!.GetValue<string>() == image &&
                staged.Root["commands"]![0]!["Values"]!["backgroundImage"]!["path"]!.GetValue<string>() == image,
                "scene snapshot makes local references absolute in active and ignored timeline sections");
            check(staged.Root["commands"]![1]!["Values"]!["backgroundImage"]!["logicalPath"]!.GetValue<string>() == "assets/do-not-rebase.png" &&
                staged.Root["commands"]![2]!["Unconsumed"]!["path"]!.GetValue<string>() == "do-not-rebase.png",
                "scene snapshot never rewrites archive logical paths or unknown game data");
            check(opened.Text == unsaved && opened.IsModified && opened.FilePath == file && File.ReadAllText(file) == disk &&
                snapshot.SourceFilePath == file && snapshot.BaseDirectory == original,
                "scene snapshot preserves unsaved editor changes and the original saved file");
            string snapshotFile = snapshot.FilePath;
            snapshot.Dispose();
            check(!File.Exists(snapshotFile) && File.Exists(file) && File.Exists(image), "scene snapshot disposal removes only its owned temporary input");
        }
        opened.Save(Path.Combine(relocated, "copy.json"));
        string relative = opened.Root["initialize"]![0]!["Values"]!["backgroundImage"]!["path"]!.GetValue<string>();
        check(!Path.IsPathRooted(relative) && opened.ResolveAssetPath(relative) == image && !opened.IsModified && File.ReadAllText(file) == disk,
            "scene Save As rebases known local references while leaving the source document and source image intact");
        opened.SetOutput(width: 1280);
        string before = opened.Text, identity = opened.FilePath!;
        check(ThrowsWriteFailure(() => opened.Save(original)) && opened.Text == before && opened.FilePath == identity && opened.IsModified &&
            Directory.Exists(original) && File.ReadAllBytes(image).SequenceEqual(new byte[] { 1, 2, 3 }) && !Directory.EnumerateFiles(folder, ".scene-save-*.tmp").Any(),
            "scene failed save preserves editor state and original files while cleaning its temporary file");

        var legacy = SceneTemplates.Create("legacy-shot", "42", "model");
        var legacyRoot = legacy.Root;
        legacyRoot["backgroundImage"] = new JsonObject { ["path"] = "assets/背景 plate.png" };
        legacy = SceneDocument.Parse(legacyRoot.ToJsonString(), baseDirectory: original);
        using var legacySnapshot = legacy.CreateSnapshot(snapshots);
        check(SceneDocument.Open(legacySnapshot.FilePath).Root["backgroundImage"]!["path"]!.GetValue<string>() == image &&
            legacy.Root["backgroundImage"]!["path"]!.GetValue<string>() == "assets/背景 plate.png",
            "scene legacy snapshot resolves local images without converting or mutating Shot");
    }

    static void Templates(string folder, Action<bool, string> check)
    {
        check(SceneTemplates.All.Select(template => template.Key).Distinct().Count() == 4 &&
            SceneTemplates.All.All(template => template.Label.Length > 0 && template.Description.Length > 0),
            "scene exposes four described template choices");
        foreach (var template in SceneTemplates.All)
        {
            var document = SceneTemplates.Create(template.Key, new SceneSource("selected-source", "jp", "ja"), "selected-model", "selected-second-model");
            string path = Path.Combine(folder, template.Key + ".json");
            document.Save(path);
            var native = DirectorProject.Read(path); // Pure managed native envelope/legacy adapter, no archive or GPU.
            check(!document.Validate().HasErrors && native.Adv == "selected-source" && native.Site == "jp" && native.Language == "ja",
                "scene template agrees with native project reader: " + template.Key);
        }
        foreach (var template in SceneTemplates.All.Where(template => template.Key != "story-patch"))
        {
            var document = SceneTemplates.Create(template.Key, new SceneSource("", "jp", "ja"), "selected-model", "selected-second-model");
            string path = Path.Combine(folder, "library-" + template.Key + ".json");
            document.Save(path);
            var native = DirectorProject.Read(path);
            check(!document.Validate().HasErrors && document.Source.IsLibrary && native.Adv == DirectorProject.LibrarySource
                && native.Site == "jp" && native.Language == "ja",
                "new material template uses the shared library through the native adapter: " + template.Key);
            if (document.Kind == SceneDocumentKind.UnifiedProject)
                check(document.Root["source"]?["adv"] is null && new[] { "initialize", "commands", "finalize" }
                    .All(section => document.Root[section] is JsonArray),
                    "library template has explicit performance sections without a source story: " + template.Key);
        }
        var original = SceneTemplates.Create("story-patch", "42");
        check(!original.Root.ContainsKey("initialize") && !original.Root.ContainsKey("commands") && !original.Root.ContainsKey("finalize"),
            "scene story patch template inherits all original plot sections");
        var duet = SceneTemplates.Create("duet-background", "42", "first-model", "second-model");
        check(duet.GetActors().Select(actor => actor.Model).SequenceEqual(["first-model", "second-model"]) &&
            !duet.ToJson().Contains("backgroundImage", StringComparison.Ordinal) && !duet.ToJson().Contains("TargetAssetName", StringComparison.Ordinal),
            "scene duet uses selected model IDs and generated colour without invented asset references");
        check(Throws<ArgumentException>(() => SceneTemplates.Create("duet-background", "42", "m")) &&
            Throws<ArgumentException>(() => SceneTemplates.Create("transparent-actor", "42")),
            "scene model-dependent templates require the actual UI model selection");
        check(new[] { "", " ", DirectorProject.LibrarySource }.All(source =>
                Throws<ArgumentException>(() => SceneTemplates.Create("story-patch", new SceneSource(source, "jp", "ja")))),
            "only the original-story template requires a real source to inherit");
    }

    static SceneDocument Project(string properties, string? baseDirectory = null) => SceneDocument.Parse(
        "{\"format\":\"ournotes-director/project\",\"version\":1," + properties + "}", baseDirectory: baseDirectory);

    static bool ThrowsWriteFailure(Action action)
    {
        try { action(); return false; }
        // Replacing a directory is IOException on Unix and UnauthorizedAccessException on Windows.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
