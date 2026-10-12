using System.Globalization;
using System.Text.Json.Nodes;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

public static class CommandCatalogTests
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ournotes-command-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { RunIn(root, check); }
        finally { Directory.Delete(root, true); }
    }

    private static void RunIn(string root, Action<bool, string> check)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "archive", "help", "probe.archive", "probe.runtime", "probe.model", "probe.native", "probe.sound", "probe.camera",
            "render.validate", "render.state", "render.frame", "render.contact", "render.clip", "render.sequence", "shot.init", "shot.validate",
            "story.list", "story.search", "story.show", "story.commands", "story.frame", "story.contact", "story.clip", "story.state", "story.sequence",
            "story.native-program", "story.native-check", "story.audit", "story.dialogue-plan", "story.export-dialogue", "commands.list",
            "dialogue.search", "voice.search", "voice.extract", "home.list", "home.show", "home.animations", "home.frame", "home.contact", "home.clip", "home.sequence", "home.export",
            "model.list", "model.show", "model.parameters", "model.gallery", "motion.list", "expression.list", "expression.show",
            "stage.list", "stage.show", "postfx.list", "postfx.show", "still.list", "still.show", "frame.list", "frame.show", "effect.list", "effect.show", "media.list", "media.show",
            "asset.search", "asset.show", "asset.verify", "asset.extract", "asset.gallery", "media.probe", "media.clip", "media.mux", "image.probe", "image.compare"
        };
        check(expected.SetEquals(CommandCatalog.All.Select(command => command.Id)), "Desktop: command catalog covers every CLI operation");
        check(CommandCatalog.All.Select(command => command.Id).Distinct(StringComparer.Ordinal).Count() == CommandCatalog.All.Count,
            "Desktop: command identifiers are unique");
        check(Enum.GetValues<CommandGroup>().All(group => CommandCatalog.ForGroup(group).Count > 0), "Desktop: all navigation groups have operations");
        check(CommandCatalog.All.All(command => command.Label.Any(character => character > 127) && command.Description.Any(character => character > 127)
            && command.Fields.All(field => field.Label.Any(character => character > 127) && field.Description.Any(character => character > 127))),
            "Desktop: operation and form labels are localized");
        check(CommandCatalog.All.All(command => command.Fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() == command.Fields.Count),
            "Desktop: a form never declares duplicate keys");
        check(CommandCatalog.All.All(command => command.Fields.All(field => (field.Position is null) != (field.Option is null))),
            "Desktop: every field maps to one positional argument or named option");
        check(CommandCatalog.All.SelectMany(command => command.Fields).Where(field => field.LookupCommandId is not null)
            .All(field => expected.Contains(field.LookupCommandId!)), "Desktop: resource selectors link to real operations");
        check(CommandCatalog.Get("home.export").Fields.Single(field => field.Key == "out").Kind == CommandFieldKind.NewDirectory
            && CommandCatalog.Get("render.sequence").Fields.Single(field => field.Key == "out").Kind == CommandFieldKind.OutputDirectory
            && CommandCatalog.Get("home.clip").Fields.Single(field => field.Key == "out").Kind == CommandFieldKind.OutputFile,
            "Desktop: new directories, resumable directories and output files remain distinct");
        check(new[] { "media.probe", "media.clip", "media.mux", "image.probe", "image.compare", "probe.runtime", "help" }
            .All(id => !CommandCatalog.Get(id).RequiresArchive && CommandCatalog.Get(id).Fields.All(field => field.Key != "archive")),
            "Desktop: local media and runtime commands require no archive");

        string archive = Path.Combine(root, "素材归档");
        string output = Path.Combine(root, "输出目录");
        Directory.CreateDirectory(archive);
        Directory.CreateDirectory(output);
        string project = Path.Combine(root, "透明项目 & sample.json");
        string opaqueProject = Path.Combine(root, "opaque.json");
        string legacy = Path.Combine(root, "legacy.json");
        string commandArray = Path.Combine(root, "commands-array.json");
        string transparentArray = Path.Combine(root, "transparent-array.json");
        string ignoredArray = Path.Combine(root, "ignored-array.json");
        string input = Path.Combine(root, "source file $sample.mov");
        string audio = Path.Combine(root, "audio.wav");
        string image = Path.Combine(root, "image.png");
        string library = Path.Combine(root, "library.dll");
        string captions = Path.Combine(root, "subtitle.srt");
        File.WriteAllText(project, Project(640, 360, "cineform", true).ToJsonString());
        File.WriteAllText(opaqueProject, Project(640, 360, "h264", false).ToJsonString());
        File.WriteAllText(legacy, "{\"actors\":[],\"duration\":3,\"background\":[0,0,0,0],\"output\":{\"width\":640,\"height\":360,\"renderScale\":1}}");
        File.WriteAllText(commandArray, Project(640, 360, "h264", false)["commands"]!.ToJsonString());
        File.WriteAllText(transparentArray, Project(640, 360, "cineform", true)["commands"]!.ToJsonString());
        var ignoredCommands = Project(640, 360, "cineform", true)["commands"]!.DeepClone();
        ignoredCommands[0]!["IgnoreData"] = 1;
        File.WriteAllText(ignoredArray, ignoredCommands.ToJsonString());
        foreach (string file in new[] { input, audio, image, library, captions }) File.WriteAllText(file, "fixture");
        var context = new CommandContext { ArchivePath = archive, OutputDirectory = output, WorkingDirectory = root };
        Dictionary<string, string?> Values(params (string Key, string? Value)[] entries) => entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        CommandBuildResult Build(string id, params (string Key, string? Value)[] entries) => CommandLineBuilder.Build(id, Values(entries), context);
        bool Error(CommandBuildResult result, string key) => !result.IsValid && result.Arguments.Count == 0 && result.Errors.Any(error => error.FieldKey == key);
        string? Arg(CommandBuildResult result, string key)
        {
            int index = result.Arguments.ToList().IndexOf("--" + key);
            return index >= 0 && index + 1 < result.Arguments.Count ? result.Arguments[index + 1] : null;
        }

        // Exercise all public forms, not just the most prominent export operation.
        foreach (var command in CommandCatalog.All)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var field in command.Fields.Where(field => field.Required && field.Key != "out"))
                values[field.Key] = field.Key switch
                {
                    "project" => command.Id == "shot.validate" ? legacy : project,
                    "script" => project,
                    "input" => command.Id.StartsWith("image.", StringComparison.Ordinal) ? image : input,
                    "first" or "second" => image,
                    "audio" => audio,
                    "advId" or "adv" => "10060", "spotId" => "40005", "modelId" => "hotaru_model",
                    "voiceId" or "soundId" => "3101040101002", "logicalPath" => "episode.json", "expressionId" => "smile", "query" => "测试",
                    _ => "resource"
                };
            if (command.Id is "asset.show" or "asset.verify" or "asset.extract") values["adv"] = "10060";
            var result = CommandLineBuilder.Build(command.Id, values, context);
            check(result.IsValid, "Desktop: valid form builds " + command.Id + " (" + string.Join("; ", result.Errors.Select(error => error.Message)) + ")");
            if (result.IsValid) check(result.Arguments.Take(command.CommandTokens.Count).SequenceEqual(command.CommandTokens), "Desktop: exact command prefix " + command.Id);
        }

        const string query = "标题 \"含空格\" ; $(不执行) & 文本";
        var safeQuery = Build("story.search", ("query", query));
        check(safeQuery.IsValid && safeQuery.Arguments[2] == query && safeQuery.Arguments.Count(argument => argument == query) == 1,
            "Desktop: user text stays one literal ArgumentList element");
        var filePath = Build("media.probe", ("input", input));
        check(filePath.IsValid && filePath.Arguments[2] == input && !filePath.Arguments.Contains("--archive"), "Desktop: paths with spaces are unquoted argument values");
        check(Error(Build("story.search", ("query", "-invalid")), "query"), "Desktop: parser-incompatible leading dash is rejected");
        check(Error(Build("home.list", ("unsupported", "")), "unsupported"), "Desktop: even an empty unknown setting is rejected");
        check(!Build("missing.operation").IsValid, "Desktop: unknown operations never produce executable arguments");
        check(Error(Build("story.show"), "advId"), "Desktop: required resource selectors are enforced");
        check(Error(Build("media.probe", ("input", "missing.mov")), "input"), "Desktop: missing input files are reported before launch");

        var inherited = CommandLineBuilder.Build("story.list", Values(), context with { Site = "jp", Language = "ja" });
        check(inherited.IsValid && Arg(inherited, "site") == "jp" && Arg(inherited, "archive") == archive,
            "Desktop: relevant global context is inherited");
        var overridden = CommandLineBuilder.Build("dialogue.search", Values(("query", "台词"), ("site", "intl"), ("lang", "zh-Hans")), context with { Site = "jp", Language = "ja" });
        check(overridden.IsValid && Arg(overridden, "site") == "intl" && Arg(overridden, "lang") == "zh-Hans", "Desktop: explicit operation values override context");
        var cleared = CommandLineBuilder.Build("home.list", Values(("site", "")), context with { Site = "jp" });
        check(cleared.IsValid && !cleared.Arguments.Contains("--site"), "Desktop: blank explicit value can clear an inherited filter");
        var archiveAlias = Build("archive", ("archive", Path.Combine(root, "new-archive")), ("site", "jp"));
        check(archiveAlias.IsValid && Arg(archiveAlias, "sites") == "jp" && !archiveAlias.Arguments.Contains("--archive"), "Desktop: archive aliases normalize to one destination and site option");
        var archiveDefaults = CommandLineBuilder.Build("archive", Values(), context with { Site = "intl" });
        check(archiveDefaults.IsValid && !archiveDefaults.Arguments.Contains("--sites")
            && CommandCatalog.Get("archive").Fields.Single(field => field.Key == "sites").DefaultValue == "intl,jp",
            "Desktop: untouched archive form keeps both CLI sites instead of inheriting the browser's international site");
        var explicitArchiveSite = CommandLineBuilder.Build("archive", Values(("sites", "jp")), context with { Site = "intl" });
        check(explicitArchiveSite.IsValid && Arg(explicitArchiveSite, "sites") == "jp", "Desktop: archive download sites remain explicitly selectable");
        var bands = CommandCatalog.Get("archive").Fields.Single(field => field.Key == "band");
        check(bands.AllowCustomValue && new[] { "millsage", "mygo", "mujica", "yumemita", "kadan" }.All(token => bands.Choices.Any(choice => choice.Value == token)),
            "Desktop: documented band tokens have clickable presets while upstream IDs remain supported");
        check(Error(Build("archive", ("out", "a"), ("archive", "b")), "out"), "Desktop: archive destination aliases cannot conflict");
        check(Error(Build("archive", ("sites", "intl"), ("site", "jp")), "sites"), "Desktop: archive site aliases cannot conflict");
        check(Error(Build("archive", ("concurrency", "65")), "concurrency") && Error(Build("archive", ("retries", "0")), "retries"), "Desktop: archive retry and concurrency limits match CLI");
        check(Error(Build("archive", ("sites", "unknown")), "sites"), "Desktop: archive site choices are validated");
        var speakerAlias = Build("voice.search", ("query", "台词"), ("actor", "hotaru"));
        check(speakerAlias.IsValid && Arg(speakerAlias, "speaker") == "hotaru", "Desktop: actor search alias remains available");
        check(Error(Build("asset.extract", ("logicalPath", "episode.json")), "adv"), "Desktop: extraction requires a source");
        check(Error(Build("asset.show", ("logicalPath", "episode.json"), ("adv", "10060"), ("model", "hotaru")), "model"), "Desktop: story and model sources are mutually exclusive");
        check(Build("asset.extract", ("logicalPath", "voice:123"), ("out", "voice.m4a")).IsValid, "Desktop: original voice extraction does not require a model source");
        check(Error(Build("asset.extract", ("logicalPath", "voice:123"), ("model", "hotaru")), "model"), "Desktop: voice extraction cannot silently ignore a model source");

        var home = Build("home.clip", ("spotId", "40005"), ("character", "hotaru"));
        check(home.IsValid && home.OutputPath!.EndsWith(".mov", StringComparison.Ordinal) && !home.Arguments.Contains("--framing"), "Desktop: homepage defaults preserve CineForm and automatic framing");
        check(Error(Build("home.clip", ("spotId", "all")), "spotId"), "Desktop: all-home selection only works for export");
        check(Error(Build("home.clip", ("spotId", "40005"), ("character", "all")), "character"), "Desktop: all-character selection only works for export");
        check(Build("home.export", ("spotId", "all"), ("character", "all")).IsValid, "Desktop: every-home character batch is expressible");
        check(Error(Build("home.export", ("spotId", "all"), ("adv", "10868")), "adv"), "Desktop: all-home export rejects a single source override");
        check(Error(Build("home.show", ("spotId", "0")), "spotId"), "Desktop: homepage IDs are validated");
        check(Error(Build("home.clip", ("spotId", "40005"), ("codec", "h264"), ("character", "hotaru")), "codec"), "Desktop: transparent character H.264 is rejected");
        check(Error(Build("home.clip", ("spotId", "40005"), ("codec", "h264"), ("no-background", "true")), "codec"), "Desktop: transparent group H.264 is rejected");
        check(Build("home.clip", ("spotId", "40005"), ("codec", "h264")).IsValid, "Desktop: opaque homepage H.264 remains available");
        check(Error(Build("home.clip", ("spotId", "40005"), ("codec", "cineform"), ("width", "1080"), ("height", "1920")), "width"), "Desktop: CineForm portrait alignment is validated");
        check(Build("home.clip", ("spotId", "40005"), ("width", "1088"), ("height", "1920")).IsValid, "Desktop: aligned CineForm portrait is accepted");
        check(Error(Build("home.clip", ("spotId", "40005"), ("height", "16")), "width"), "Desktop: CineForm requires height of at least 32");
        check(Error(Build("home.clip", ("spotId", "40005"), ("fps", "121")), "fps"), "Desktop: home fps upper bound matches runtime");
        check(Error(Build("home.clip", ("spotId", "40005"), ("fps", "29.97")), "fps"), "Desktop: home fixed fps must be an integer");
        check(Error(Build("home.clip", ("spotId", "40005"), ("duration", "0")), "duration"), "Desktop: nonpositive durations are rejected");
        check(Error(Build("home.clip", ("spotId", "40005"), ("duration", "NaN")), "duration"), "Desktop: nonfinite numeric inputs are rejected");
        check(Error(Build("home.clip", ("spotId", "40005"), ("time", "35999"), ("duration", "2")), "duration"), "Desktop: summed timeline duration is bounded");
        check(Error(Build("home.clip", ("spotId", "40005"), ("out", "wrong.mp4")), "out"), "Desktop: codec and output container extension agree");
        check(Error(Build("home.clip", ("spotId", "40005"), ("cineform-quality", "crf18")), "cineform-quality"), "Desktop: arbitrary quality settings are not silently accepted");
        check(Build("home.clip", ("spotId", "40005"), ("codec", "cfhd"), ("cineform-quality", "FILM1")).IsValid, "Desktop: documented CineForm alias and case normalize");
        check(Error(Build("home.contact", ("spotId", "40005"), ("times", "0,,2")), "times"), "Desktop: invalid contact samples are rejected");
        check(Error(Build("home.contact", ("spotId", "40005"), ("width", "7680"), ("height", "7680"), ("supersample", "4")), "supersample"), "Desktop: oversized home supersampling is blocked");
        check(Error(Build("story.contact", ("advId", "10060"), ("columns", "17")), "columns"), "Desktop: story contact column bounds match CLI");
        check(Error(Build("home.contact", ("spotId", "40005"), ("columns", "100"), ("times", string.Join(',', Enumerable.Repeat("1", 100))), ("width", "1920"), ("height", "1080")), "columns"), "Desktop: contact sheet megapixel limit is checked");

        var projectBuild = Build("render.clip", ("project", project));
        check(projectBuild.IsValid && projectBuild.Arguments[2] == project && !projectBuild.Arguments.Contains("--codec")
            && !projectBuild.Arguments.Contains("--width") && projectBuild.OutputPath!.EndsWith(".mov", StringComparison.Ordinal),
            "Desktop: unified output defaults are validated without overwriting project settings");
        check(Error(Build("render.clip", ("project", project), ("codec", "h264")), "codec"), "Desktop: project transparency blocks H.264 before native rendering");
        check(Build("render.clip", ("project", opaqueProject)).IsValid, "Desktop: opaque project H.264 is supported");
        check(Error(Build("render.clip", ("project", opaqueProject), ("width", "641")), "width"), "Desktop: H.264 dimensions must be even");
        check(Build("render.clip", ("project", legacy)).IsValid, "Desktop: legacy adapter defaults use ProRes and preserve transparency");
        check(Error(Build("render.frame", ("project", project), ("script", project)), "script"), "Desktop: unified project and alternate script cannot be combined");
        check(Error(Build("story.frame", ("advId", "10061"), ("script", project)), "script"), "Desktop: alternate project cannot target the wrong story");
        foreach (string id in new[] { "story.frame", "story.contact", "story.state", "story.native-check", "story.clip" })
            check(Build(id, ("advId", "10060"), ("script", commandArray)).IsValid, "Desktop: raw top-level command array remains usable by " + id);
        check(Error(Build("story.clip", ("advId", "10060"), ("script", transparentArray)), "codec"),
            "Desktop: raw command array transparency blocks default H.264");
        check(Build("story.clip", ("advId", "10060"), ("script", transparentArray), ("codec", "cineform")).IsValid,
            "Desktop: raw command array transparency permits alpha-capable output");
        check(Build("story.clip", ("advId", "10060"), ("script", ignoredArray)).IsValid,
            "Desktop: ignored raw command does not mark opaque output transparent");
        check(Error(Build("render.frame", ("project", commandArray)), "project"), "Desktop: unified render requires a project object rather than a raw array");
        check(Error(Build("story.sequence", ("advId", "10060"), ("script", commandArray)), "script"),
            "Desktop: sequence reports its existing object-script requirement before launch");
        var scriptSource = CommandLineBuilder.Build("story.frame", Values(("advId", "10060"), ("script", project)), context with { Site = "intl", Language = "zh-Hans" });
        check(scriptSource.IsValid && Arg(scriptSource, "site") == "jp" && Arg(scriptSource, "lang") == "ja",
            "Desktop: script source site and language take precedence over untouched global defaults");
        check(Error(Build("story.frame", ("advId", "10060"), ("script", project), ("site", "intl")), "site"),
            "Desktop: explicitly conflicting script site is reported in the form");
        var localizedScript = Build("story.frame", ("advId", "10060"), ("script", project), ("site", " jp "), ("lang", "zh-Hans"));
        check(localizedScript.IsValid && Arg(localizedScript, "site") == "jp" && Arg(localizedScript, "lang") == "zh-Hans",
            "Desktop: matching site normalizes whitespace and project language remains overridable");
        var rawSource = CommandLineBuilder.Build("story.frame", Values(("advId", "10060"), ("script", commandArray)), context with { Site = "jp", Language = "ja" });
        check(rawSource.IsValid && Arg(rawSource, "site") == "jp" && Arg(rawSource, "lang") == "ja",
            "Desktop: source-free raw commands retain selected global story context");
        check(Error(Build("story.clip", ("advId", "10060"), ("speed", "0.24")), "speed"), "Desktop: story output speed range is validated");
        check(Error(Build("render.sequence", ("project", project), ("overwrite", "true")), "overwrite"), "Desktop: sequence never accepts overwrite");
        check(Error(Build("render.sequence", ("project", project), ("speed", "2")), "speed"), "Desktop: sequence retains source simulation speed");
        check(Error(Build("render.clip", ("project", project), ("resume", "true")), "resume"), "Desktop: resume only exists on compatible sequence forms");
        check(Error(Build("home.sequence", ("spotId", "40005"), ("resume", "true")), "resume"), "Desktop: home sequence never claims resume support");
        check(Error(Build("home.export", ("spotId", "40005"), ("overwrite", "true")), "overwrite"), "Desktop: homepage batch cannot overwrite a directory");

        string existing = Path.Combine(root, "existing");
        Directory.CreateDirectory(existing);
        check(Error(Build("home.export", ("spotId", "40005"), ("out", existing)), "out"), "Desktop: existing batch output directory is refused");
        check(Error(Build("render.sequence", ("project", project), ("out", existing)), "out"), "Desktop: existing sequence needs resume");
        File.WriteAllText(Path.Combine(existing, "000000.png"), "frame");
        check(Error(Build("render.sequence", ("project", project), ("out", existing), ("resume", "true")), "resume"), "Desktop: resume cannot adopt an unrelated directory");
        File.WriteAllText(Path.Combine(existing, "render.json"), "{}");
        check(Build("render.sequence", ("project", project), ("out", existing), ("resume", "true")).IsValid, "Desktop: existing render manifest allows authoritative CLI resume validation");
        string existingFile = Path.Combine(root, "existing.png");
        File.WriteAllText(existingFile, "do not replace");
        check(Error(Build("home.frame", ("spotId", "40005"), ("out", existingFile)), "out"), "Desktop: existing file needs explicit overwrite");
        check(Build("home.frame", ("spotId", "40005"), ("out", existingFile), ("overwrite", "true")).IsValid, "Desktop: explicit single-file replacement can be requested");
        check(File.ReadAllText(existingFile) == "do not replace", "Desktop: builder validation never mutates existing output");
        check(Error(Build("media.clip", ("input", input), ("out", input), ("overwrite", "true")), "out"), "Desktop: input video cannot be overwritten by its own conversion");

        var mediaContext = context with { ArchivePath = Path.Combine(root, "missing-archive"), SourceWidth = 1080, SourceHeight = 1920 };
        var badMedia = CommandLineBuilder.Build("media.clip", Values(("input", input), ("codec", "cineform")), mediaContext);
        check(Error(badMedia, "width") && badMedia.Errors.All(error => error.FieldKey != "archive"), "Desktop: local media validates probed size without depending on an archive");
        check(CommandLineBuilder.Build("media.clip", Values(("input", input), ("codec", "cineform")), mediaContext with { SourceWidth = 1088 }).IsValid,
            "Desktop: media probe dimensions can validate a compatible conversion");
        check(Error(CommandLineBuilder.Build("media.mux", Values(("input", input), ("audio", audio), ("out", "mux.mp4")), context with { SourceCodec = "cfhd" }), "out"),
            "Desktop: CineForm cannot be silently copied into MP4");
        check(CommandLineBuilder.Build("media.mux", Values(("input", input), ("audio", audio), ("subtitles", captions)), context with { SourceCodec = "cfhd" }).IsValid,
            "Desktop: CineForm MOV plus audio and SRT is expressible");
        check(Error(CommandLineBuilder.Build("media.mux", Values(("input", input), ("audio", audio)), context with { SourceCodec = "ffv1" }), "out"),
            "Desktop: unsupported MOV copy codec is rejected");
        var toolOverride = CommandLineBuilder.Build("probe.runtime", Values(("dll", library), ("ffmpeg", "ffmpeg")), context with { CubismSdkPath = archive, FFmpegPath = "unused-tool" });
        check(toolOverride.IsValid && Arg(toolOverride, "dll") == library && Arg(toolOverride, "ffmpeg") == "ffmpeg" && !toolOverride.Arguments.Contains("--cubism-sdk"),
            "Desktop: explicit native file overrides contextual SDK and literal tool names remain usable");

        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var invariant = Build("home.clip", ("spotId", "40005"), ("time", "0.5"));
            check(invariant.IsValid && Arg(invariant, "time") == "0.5", "Desktop: numeric arguments are culture independent");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static JsonObject Project(int width, int height, string codec, bool transparent) => new()
    {
        ["format"] = "ournotes-director/project", ["version"] = 1,
        ["source"] = new JsonObject { ["adv"] = "10060", ["site"] = "jp", ["language"] = "ja" },
        ["output"] = new JsonObject { ["width"] = width, ["height"] = height, ["supersample"] = 1, ["codec"] = codec, ["cineformQuality"] = "high", ["letterbox"] = "none" },
        ["commands"] = new JsonArray(new JsonObject
        {
            ["cmd"] = "Director.Scene", ["Values"] = new JsonObject { ["background"] = new JsonArray(0, 0, 0, transparent ? 0 : 1), ["stageVisible"] = false }
        })
    };
}
