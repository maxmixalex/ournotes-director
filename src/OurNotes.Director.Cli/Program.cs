using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using static OurNotes.Director.Core.Archive;

Console.OutputEncoding = new UTF8Encoding(false);
NativeLogging.ConfigureConsole();
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
try
{
    var options = new Dictionary<string, string>(); var positional = new List<string>();
    var flags = new HashSet<string> { "json", "with-voice", "overwrite", "help", "resume", "clean", "until-end", "debug", "no-deep", "loop", "no-background" };
    var allowed = new HashSet<string> { "archive", "band", "site", "lang", "speaker", "actor", "character", "kind", "adv", "model", "limit", "offset", "out", "cubism-sdk", "dll", "time", "times", "columns", "codec", "cineform-quality", "ffmpeg", "ffprobe", "start", "duration", "template", "gap", "silent-seconds", "audio", "subtitles", "script", "width", "height", "supersample", "speed", "sites", "languages", "concurrency", "retries", "animation", "framing", "fps" };
    for (var i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if (a.StartsWith('-'))
        {
            var key = a == "-o" ? "out" : a.TrimStart('-');
            if (options.ContainsKey(key)) throw new ArgumentException($"Duplicate option: {a}");
            if (flags.Contains(key)) options[key] = "true";
            else if (allowed.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith('-')) options[key] = args[++i];
            else throw new ArgumentException($"Unknown option or missing value: {a}");
        }
        else positional.Add(a);
    }
    string Opt(string name, string fallback = "") => options.GetValueOrDefault(name, fallback);
    string Arg(int n) => positional.Count > n ? positional[n] : "";
    string Required(int n) => Arg(n) is { Length: > 0 } s ? s : throw new ArgumentException("Missing required positional argument; use help");
    var command = Arg(0); var verb = Arg(1);
    if (command is "" or "help" || options.ContainsKey("help"))
    {
        Console.WriteLine(JsonOutput.Serialize(JsonOutput.Object(("schema", "ournotes-director/1"), ("commands", new[] {
            "archive [--band ID|NAME] [--out DIRECTORY] [--languages zh-Hans,ja] [--sites intl,jp] [--concurrency 8] [--no-deep] (one band; default millsage; accepts a MasterBand ID, name token or Band_Name_* text ID; discover, verify/download, index and build Director Pack; rerun to update incrementally)",
            "render validate|state|frame|contact|clip|sequence <project.json|legacy-shot.json> (one native ADV executor; game primitives and Director.Actor/Camera/Parameter/Scene; sequence supports --resume)",
            "probe archive", "probe runtime (load packaged Core, lip-sync engine and FFmpeg; no archive required)", "probe model <id>", "probe native <model-id> [--cubism-sdk PATH | --dll PATH]",
            "probe sound <sound-id> --adv ID (native PCM decode and loop metadata)",
            "probe camera <adv-id> (native main-camera definition and projection)",
            "story list|search [text]", "story show <adv-id>", "dialogue search <text>", "voice search <text>", "voice extract <voice-id> -o FILE",
            "home list | home show <spot-id> | home animations <spot-id> [--character KEY] (discover archived home scenes, grouped characters, source animations and durations)",
            "home export <spot-id|all> -o NEW_DIRECTORY [--character KEY|all --codec cineform --cineform-quality film1 --fps 30 --width 1920 --height 1080 --supersample 2] (complete scene plus each transparent character; default full animation including its end pose)",
            "home frame|contact|sequence|clip <spot-id> [--character KEY --animation NAME --loop --framing scene|fit --no-background --time 0 --duration SECONDS --fps 30] -o OUTPUT (native Spine; fitted characters use one stable crop over the animation; scene preserves camera coordinates)",
            "story commands <adv-id> [--offset 0 --limit 50] (lossless episode rows)",
            "story frame|clip <adv-id> --time 0 --duration 10 --width 1920 --height 1080 --supersample 2 [--script FILE --clean --lang zh-Hans --codec h264|cineform|prores|ffv1] -o FILE (native game primitives; supersample 1..4)",
            "story clip <adv-id> [--speed 1] --until-end [--duration 3600] -o FILE (duration is a safety cap; unfinished output is rejected)",
            "story contact <adv-id> --times 0,1,2,3 --columns 3 [--script FILE --width 640 --height 360 --clean] -o contact.png (one native session; JSON maps cells to exact frames)",
            "story state <adv-id> --time 0 [--script FILE --lang zh-Hans] (native timeline, actors, camera, dialogue and audio at a frame)",
            "story native-program <adv-id> [-o program.json] (compile native init/body/finalize, not render)",
            "story native-check <adv-id|all> [--script FILE -o report.json] (load real native resources and render frame zero; not a full-timeline guarantee)",
            "story audit <adv-id|all> [-o audit.json]", "story dialogue-plan <adv-id> [--lang zh-Hans --offset 0 --limit 50 --gap 0.4 --silent-seconds 2] [-o plan.json]",
            "story export-dialogue <adv-id> -o NEW_DIRECTORY [same dialogue options] (WAV, SRT, source voices, timeline, hashes; editorial timing)",
            "model list [--character hotaru]", "model show <id>", "motion list <model-id>", "expression list <model-id>", "expression show <model-id> <expression-id> (original parameter names, values and blend modes)",
            "stage|postfx|still|frame|effect|media list", "stage|postfx|still|frame|effect|media show <id>",
            "asset search <text> [--kind image]", "asset extract <logical-path> --adv ID|--model ID -o FILE",
            "asset verify|show <logical-path> --adv ID|--model ID", "commands list --adv ID"
            ,"shot init -o shot.json [--archive PATH] [--site intl] [--lang LANGUAGE] [--template fullbody|portrait|duet] [--model ID]", "shot validate <shot.json>", "model parameters <id>",
            "render frame <shot.json> --time 0 -o frame.png", "render contact <shot.json> -o contact.png",
            "render sequence <shot.json> -o frames [--resume]", "render clip <shot.json> -o shot.mov --codec cineform|prores|ffv1|h264 [--cineform-quality film1] (CineForm: RGB+alpha, width multiple of 16, height multiple of 8 and at least 32; film1 balances size/quality, high is smaller)"
            ,"model gallery <id> --kind expression|motion --limit 12 --time 1 -o gallery.png", "asset gallery [substring] --limit 12 -o assets.png",
            "media probe <local-file>", "media clip <local-file> --start 0 --duration 5 [--codec prores|cineform --cineform-quality film1] -o plate.mov (default ProRes HQ; CineForm preserves alpha)"
            ,"media mux <video> --audio FILE [--subtitles FILE.srt] -o preview.mp4|master.mkv|master.mov (copy video including CineForm alpha; audio padded/trimmed to video duration)"
            ,"image probe <png>", "image compare <first.png> <second.png>"
        }), ("options", "--archive PATH --site intl --lang zh-Hans --speaker hotaru --with-voice --limit 50 --offset 0 --json --overwrite"), ("output", "JSON envelope; exit 0 success, 2 invalid request, 3 IO/data failure, 4 unsupported feature. Extract never overwrites without --overwrite."), ("rendering", "Native Cubism and Spine 4.2 + offscreen OpenGL; 30Hz game-command simulation, home animations at configurable fps, original materials and postprocessing, PNG/CineForm/ProRes/FFV1/H264, 1..4x supersampling. Complete game parity remains in progress; unsupported features fail explicitly.")), json));
        return 0;
    }
    if (command == "archive") return await ArchiveCommand.RunAsync(options, positional, json);
    foreach (var key in new[] { "band", "sites", "languages", "concurrency", "retries", "no-deep" })
        if (options.ContainsKey(key)) throw new ArgumentException($"--{key} is only supported by archive");
    if (command == "probe" && verb == "runtime")
    {
        var runtime = NativeDependencies.Probe(Opt("dll"), Opt("cubism-sdk"), options.GetValueOrDefault("ffmpeg"), options.GetValueOrDefault("ffprobe"));
        Console.WriteLine(JsonOutput.Serialize(JsonOutput.Object(("schema", "ournotes-director/1"), ("ok", true), ("result", runtime)), json));
        return 0;
    }
    // Local media operations do not depend on a game archive. The desktop tools use
    // these before a user has downloaded any game resources.
    if ((command == "media" && verb is "probe" or "clip" or "mux") ||
        (command == "image" && verb is "probe" or "compare"))
    {
        string LocalOutput() => Opt("out") is { Length: > 0 } path ? path : throw new ArgumentException("--out is required");
        string LocalTool(string name) => NativeDependencies.MediaTool(name, options.GetValueOrDefault(name));
        object localResult = (command, verb) switch
        {
            ("image", "probe") => ImageProbe.Inspect(Required(2)),
            ("image", "compare") => ImageProbe.Compare(Required(2), Required(3)),
            ("media", "probe") => Media.Probe(Required(2), LocalTool("ffprobe")),
            ("media", "clip") => Media.Clip(Required(2), LocalOutput(), double.Parse(Opt("start", "0"), System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(Opt("duration", "5"), System.Globalization.CultureInfo.InvariantCulture), options.ContainsKey("overwrite"),
                LocalTool("ffmpeg"), LocalTool("ffprobe"), Opt("codec", "prores"), Opt("cineform-quality", VideoEncoding.DefaultCineFormQuality)),
            ("media", "mux") => Media.Mux(Required(2), Opt("audio") is { Length: > 0 } audio ? audio : throw new ArgumentException("--audio is required"),
                Opt("subtitles") is { Length: > 0 } subtitles ? subtitles : null, LocalOutput(), options.ContainsKey("overwrite"), LocalTool("ffmpeg"), LocalTool("ffprobe")),
            _ => throw new NotSupportedException("Unknown local media operation")
        };
        Console.WriteLine(JsonOutput.Serialize(JsonOutput.Object(("schema", "ournotes-director/1"), ("ok", true), ("result", localResult)), json));
        return 0;
    }
    // Both project files and adapted legacy Shot files use the native ADV executor.
    if(command=="render"){
        string projectPath=Required(2);
        var project=DirectorProject.Read(projectPath);
        if(verb is not ("validate" or "state" or "frame" or "contact" or "clip" or "sequence"))throw new NotSupportedException("Unified project operation not implemented: "+verb);
        if(options.ContainsKey("resume")&&verb!="sequence")throw new ArgumentException("--resume is supported only by render sequence");
        if(verb=="sequence"&&(options.ContainsKey("overwrite")||options.ContainsKey("speed")))throw new ArgumentException("sequence uses source 30 fps and --resume; --speed and --overwrite are not supported");
        if(Archive.ReadJson(projectPath)["format"] is null&&!options.ContainsKey("until-end"))options.TryAdd("duration",Shot.Read(projectPath).Duration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(options.ContainsKey("script"))throw new ArgumentException("A unified project already supplies the script; do not combine it with --script");
        foreach(string key in new[]{"width","height","supersample","codec"})if(project.Output[key] is {} value)options.TryAdd(key,value.ToString());
        if(project.Output["cineformQuality"] is {} quality)options.TryAdd("cineform-quality",quality.ToString());
        if(project.Output["clean"]?.GetValue<bool>()==true)options.TryAdd("clean","true");
        options.TryAdd("site",project.Site);options.TryAdd("lang",project.Language);options["script"]=Path.GetFullPath(projectPath);
        command="story";verb=verb=="validate"?"native-check":verb;positional[2]=project.Adv;
    }
    var archivePath = Opt("archive", Environment.GetEnvironmentVariable("OURNOTES_ARCHIVE") ?? "");
    if (archivePath == "")
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null && archivePath == ""; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "archive");
                if(!File.Exists(Path.Combine(candidate,"index","stats.json")))candidate=Path.Combine(dir.FullName,"legacy","millsage-story-archive","archive");
                if (File.Exists(Path.Combine(candidate,"index","stats.json"))) archivePath = candidate;
            }
    }
    var archive = new Archive(archivePath == "" ? throw new ArgumentException("No archive found. Run advkit archive, or provide --archive / OURNOTES_ARCHIVE.") : archivePath);
    var site = Opt("site", "intl"); var language = Opt("lang");
    var limit = int.Parse(Opt("limit", "50")); var offset = int.Parse(Opt("offset", "0"));
    if (limit < 1 || limit > 10000 || offset < 0) throw new ArgumentException("limit must be 1..10000; offset must be nonnegative");
    object? result;
    bool Contains(string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    object Page(IEnumerable<JsonNode> rows)
    {
        var all = rows.ToArray();
        return JsonOutput.Object(("total", all.Length), ("offset", offset), ("limit", limit), ("items", all.Skip(offset).Take(limit).ToArray()), ("nextOffset", offset + limit < all.Length ? (int?)(offset + limit) : null));
    }
    JsonNode[] SiteRows(string name) => archive.Rows(name).Where(r => S(r, "site") == site).ToArray();
    string Output() => Opt("out") is { Length: > 0 } s ? s : throw new ArgumentException("--out is required");
    string NativeDll() => NativeDependencies.CoreLibrary(Opt("dll"), Opt("cubism-sdk"));
    string MediaTool(string name) => NativeDependencies.MediaTool(name, options.GetValueOrDefault(name));
    if(command=="home") result=HomeCommand.Run(archive,options.GetValueOrDefault("site"),verb,Arg(2),options);
    else if(command=="story"&&verb=="sequence")
        result=DirectorSequence.Run(archive,site,NativeDll(),Required(2),Opt("lang","zh-Hans"),Opt("script") is {Length:>0} script?script:throw new ArgumentException("sequence requires a project"),Output(),int.Parse(Opt("width","1920")),int.Parse(Opt("height","1080")),int.Parse(Opt("supersample","2")),double.Parse(Opt("time","0"),System.Globalization.CultureInfo.InvariantCulture),double.Parse(Opt("duration",options.ContainsKey("until-end")?"3600":"10"),System.Globalization.CultureInfo.InvariantCulture),options.ContainsKey("clean"),options.ContainsKey("resume"),options.ContainsKey("until-end"),MediaTool("ffmpeg"));
    else if(command=="story"&&verb is "frame" or "clip")
        result=AdvStoryRenderer.Run(archive,site,NativeDll(),Required(2),Opt("lang","zh-Hans"),options.GetValueOrDefault("script"),verb,Output(),int.Parse(Opt("width","1920")),int.Parse(Opt("height","1080")),double.Parse(Opt("time","0"),System.Globalization.CultureInfo.InvariantCulture),double.Parse(Opt("duration",options.ContainsKey("until-end")?"3600":"10"),System.Globalization.CultureInfo.InvariantCulture),Opt("codec","h264"),options.ContainsKey("clean"),options.ContainsKey("overwrite"),MediaTool("ffmpeg"),int.Parse(Opt("supersample","2")),options.ContainsKey("until-end"),double.Parse(Opt("speed","1"),System.Globalization.CultureInfo.InvariantCulture),Opt("cineform-quality",VideoEncoding.DefaultCineFormQuality));
    else if(command=="story"&&verb=="contact")
        result=AdvStoryRenderer.Contact(archive,site,NativeDll(),Required(2),Opt("lang","zh-Hans"),options.GetValueOrDefault("script"),Output(),int.Parse(Opt("width","640")),int.Parse(Opt("height","360")),Opt("times","0,1,2").Split(',').Select(t=>double.Parse(t,System.Globalization.CultureInfo.InvariantCulture)).ToArray(),int.Parse(Opt("columns","3")),options.ContainsKey("clean"),options.ContainsKey("overwrite"),MediaTool("ffmpeg"),int.Parse(Opt("supersample","2")));
    else if(command=="story"&&verb=="state")
    {
        double time=double.Parse(Opt("time","0"),System.Globalization.CultureInfo.InvariantCulture);
        if(!double.IsFinite(time)||time<0||time>36000)throw new ArgumentException("Invalid time; maximum ten hours");
        using var renderer=new AdvStoryRenderer(archive,site,NativeDll(),Required(2),Opt("lang","zh-Hans"),320,180,options.GetValueOrDefault("script"),MediaTool("ffmpeg"),1);
        renderer.Frame((int)Math.Floor(time*30+1e-8));
        float[] V(System.Numerics.Vector3 v)=>[v.X,v.Y,v.Z];
        result=JsonOutput.Object(("frame", renderer.Loop.Frame), ("time", renderer.Loop.Frame/30d), ("row", renderer.Player.ListIndex), ("finished", renderer.Player.Finished), ("stage", renderer.Scene.StageName), ("camera", JsonOutput.Object(("position", V(renderer.Scene.Camera.Position)), ("euler", V(renderer.Scene.Camera.Euler)), ("Fov", renderer.Scene.Camera.Fov), ("ZoomRatio", renderer.Scene.Camera.ZoomRatio))), ("actors", renderer.Scene.Shown.Select(p=>JsonOutput.Object(("slot", p.Key), ("name", p.Value.Spec.Name), ("model", p.Value.Spec.Model), ("position", V(p.Value.Prefab.Root.WorldPosition)), ("EyeBlinkStopped", p.Value.EyeBlinkStopped), ("VoiceLipSyncActive", p.Value.VoiceLipSyncActive), ("PseudoLipSyncActive", p.Value.PseudoLipSyncActive), ("MouthOpen", p.Value.MouthOpen), ("LipSyncMissing", p.Value.LipSyncMissing)))), ("directorActors", renderer.Scene.DirectorState()), ("dialogue", JsonOutput.Object(("Text", renderer.Ui.Text), ("Speaker", renderer.Ui.Speaker), ("Typing", renderer.Ui.Typing))), ("frames", renderer.Frames.Snapshot()), ("chat", renderer.Ui.Chat?.Snapshot()), ("effects", renderer.Scene.Effects.ToDictionary(p=>p.Key,p=>p.Value.Snapshot())), ("stageEffects", renderer.Scene.StageEffectGroups.ToDictionary(p=>p.Key,p=>JsonOutput.Object(("group", p.Value.Current), ("effects", p.Value.Instances.Select(e=>e.Snapshot()))))), ("audio", renderer.Audio.Playing.Select(a=>JsonOutput.Object(("Id", a.Id), ("SoundId", a.Cue.SoundId), ("StartFrame", a.StartFrame), ("Repeat", a.Repeat), ("Gain", a.Gain)))));
    }
    else if (command == "probe" && verb == "archive")
        result = JsonOutput.Object(("root", archive.Root), ("stats", archive.Index("stats")), ("indexFingerprint", Hash(File.ReadAllBytes(Path.Combine(archive.Root, "index", "stats.json")))), ("capabilities", JsonOutput.Object(("query", true), ("verifiedExtraction", true), ("nativeCoreProbe", true), ("nativeRendering", true), ("gameAdvFidelity", false))), ("fingerprintScope", "stats.json only; not a full archive integrity check"));
    else if(command=="shot"&&verb=="init")
    {
        var template=Opt("template","fullbody");
        var shot=Shot.CreateTemplate(archive,site,template,Opt("model"),language);
        result=archive.Export(site,JsonOutput.SerializeToUtf8Bytes(shot,Shot.JsonOptions),Output(),JsonOutput.Object(("template", "transparent-character")),options.ContainsKey("overwrite"));
    }
    else if(command=="probe"&&verb=="sound")
    {
        if(Opt("adv")=="")throw new ArgumentException("--adv is required");
        var story=new Story(archive,site,Opt("adv"));var cue=AdvAudio.Decode(archive,site,story.Manifest,story.Episode,long.Parse(Required(2),System.Globalization.CultureInfo.InvariantCulture),MediaTool("ffmpeg"));
        result=JsonOutput.Object(("SoundId", cue.SoundId), ("Category", cue.Category), ("Channels", cue.Channels), ("rate", AdvAudio.SampleRate), ("Samples", cue.Samples), ("duration", (double)cue.Samples/AdvAudio.SampleRate), ("peak", cue.Pcm.Max(v=>Math.Abs(v))), ("LoopStart", cue.LoopStart), ("LoopEnd", cue.LoopEnd));
    }
    else if(command=="probe"&&verb=="camera")
    {
        var manifest=archive.Manifest(site,"stories",Required(2));var story=JsonNode.Parse(archive.Logical(site,manifest,"story.json"))!;
        var scene=JsonNode.Parse(archive.Logical(site,manifest,S(story,"scene")))!;var loop=new AdvLoop();var camera=AdvCamera.FromScene(loop,new AdvTweens(loop),scene);
        result=JsonOutput.Object(("Near", camera.Near), ("Far", camera.Far), ("FieldOfView", camera.FieldOfView), ("ZoomRatio", camera.ZoomRatio), ("aspect", 13.0/6), ("matrixLayout", "column-major, Unity +Z forward, OpenGL clip depth [-1,1]"), ("view", camera.View()), ("projection", camera.ViewProjection(13f/6)), ("rendered", false));
    }
    else if(command=="shot"&&verb=="validate")
    {
        var shot=Shot.Read(Required(2));shot.Validate();
        using var renderer=new Renderer(archive,site,NativeDll(),shot,Required(2));
        result=JsonOutput.Object(("valid", true), ("frames", (int)Math.Round(shot.Duration*30)), ("Device", renderer.Device), ("actors", shot.Actors.Select(a=>JsonOutput.Object(("Name", a.Name), ("Model", a.Model)))), ("alpha", "straight"), ("simulationHz", 30));
    }
    else if(command=="model"&&verb=="parameters")
    {
        using var actor=new Actor(archive,site,NativeDll(),new ActorSpec{Model=Required(2)},null,722);result=actor.Description();
    }
    else if(command=="expression"&&verb=="show")
    {
        using var actor=new Actor(archive,site,NativeDll(),new ActorSpec{Model=Required(2)},null,722);result=actor.ExpressionDescription(Required(3));
    }
    else if(command=="model"&&verb=="gallery")result=Gallery.Models(archive,site,NativeDll(),Required(2),Opt("kind","expression"),offset,Math.Min(limit,48),double.Parse(Opt("time","1"),System.Globalization.CultureInfo.InvariantCulture),Output(),options.ContainsKey("overwrite"));
    else if(command=="asset"&&verb=="gallery")result=Gallery.Assets(archive,site,Arg(2),offset,Math.Min(limit,48),Output(),options.ContainsKey("overwrite"));
    else if ((command == "model" && verb == "show") || (command == "probe" && verb == "model") || (command is "motion" or "expression" && verb == "list"))
    {
        var model = archive.Model(site, Required(2));
        result = command is "motion" or "expression" ? Page(model[command == "motion" ? "motions" : "expressions"]!.AsArray().Select(v => (JsonNode)new JsonObject { ["id"] = v!.DeepClone(), ["model"] = Arg(2) })) : model;
    }
    else if (command == "probe" && verb == "native")
    {
        var id = Required(2); var manifest = archive.Manifest(site, "models", id);
        var moc = manifest["files"]!.AsObject().First(p => p.Key.EndsWith(".moc3", StringComparison.Ordinal)).Key;
        result = CubismProbe.Run(NativeDll(), archive.Logical(site, manifest, moc));
    }
    else if (command == "model" && verb == "list")
        result = Page(archive.Index("models")["models"]!.AsArray().OfType<JsonNode>().Where(r => S(r, "site") == site && Contains(S(r, "id"), Opt("character"))));
    else if(command=="story"&&verb is "native-program" or "native-check")
    {
        var id=Required(2);
        if(verb=="native-program")result=AdvProgram.Load(archive,site,id);
        else
        {
            var ids=id=="all"?archive.Index("catalog")["stories"]!.AsArray().OfType<JsonNode>().Where(r=>S(r,"site")==site).Select(r=>S(r,"advId")).Distinct().ToArray():[id];
            result=JsonOutput.Object(("backend", "C# native ADV"), ("browser", false), ("fullFidelityVerified", false), ("scope", "Native resource initialization and frame zero; use story clip --until-end to validate the complete timeline"), ("stories", ids.Select(i=>{
                try{var program=AdvProgram.Load(archive,site,i);
                    try{using var renderer=new AdvStoryRenderer(archive,site,NativeDll(),i,Opt("lang","zh-Hans"),160,90,options.GetValueOrDefault("script"),MediaTool("ffmpeg"),1);renderer.Frame(0);program=renderer.Player.Program;
                        return (object)JsonOutput.Object(("advId", i), ("compiled", true), ("initialized", true), ("PlaybackMode", program.PlaybackMode), ("initializeRows", program.Initialize.Length), ("bodyRows", program.Rows.Length), ("finalizeRows", program.Finalize.Length), ("required", program.RequiredCommands), ("implementedHandlers", renderer.Player.SupportedCommands), ("missing", renderer.Player.MissingCommands));}
                    catch(Exception ex) when(ex is NotSupportedException or InvalidDataException or ArgumentException){return JsonOutput.Object(("advId", i), ("compiled", true), ("initialized", false), ("required", program.RequiredCommands), ("reason", ex.Message));}
                }
                catch(NotSupportedException ex){return JsonOutput.Object(("advId", i), ("compiled", false), ("reason", ex.Message));}
            }).ToArray()));
        }
        if(options.ContainsKey("out"))result=archive.Export(site,JsonOutput.SerializeToUtf8Bytes(result,json),Output(),JsonOutput.Object(("command", "story "+verb), ("advId", id)),options.ContainsKey("overwrite"));
    }
    else if (command == "story" && verb is "audit" or "commands" or "dialogue-plan" or "export-dialogue")
    {
        var id = Required(2);
        if (verb == "audit")
        {
            var ids = id == "all" ? archive.Index("catalog")["stories"]!.AsArray().OfType<JsonNode>().Where(r => S(r,"site") == site).Select(r => S(r,"advId")).Distinct().ToArray() : [id];
            var audits = ids.Select(i => new Story(archive, site, i).Audit()).ToArray();
            result = JsonOutput.Object(("count", audits.Length), ("stories", audits));
        }
        else
        {
            var story = new Story(archive, site, id);
            if (verb == "commands") result = Page(story.Commands);
            else
            {
                if (verb == "export-dialogue" && options.ContainsKey("overwrite")) throw new ArgumentException("Dialogue packages require a new directory; --overwrite is not supported");
                var plan = story.Plan(language == "" ? "zh-Hans" : language, offset, limit,
                    double.Parse(Opt("gap","0.4"),System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(Opt("silent-seconds","2"),System.Globalization.CultureInfo.InvariantCulture));
                result = verb == "export-dialogue" ? story.ExportDialogue(plan,Output(),MediaTool("ffmpeg")) : plan;
            }
        }
        if (options.ContainsKey("out") && verb != "export-dialogue") result = archive.Export(site,JsonOutput.SerializeToUtf8Bytes(result,json),Output(),JsonOutput.Object(("command", "story " + verb), ("advId", id)),options.ContainsKey("overwrite"));
    }
    else if (command == "story" && verb == "show") result = archive.Manifest(site, "stories", Required(2))["story"];
    else if (command == "story" && verb is "list" or "search")
        result = Page(archive.Index("catalog")["stories"]!.AsArray().OfType<JsonNode>().Where(r => S(r, "site") == site && Contains(r.ToJsonString(json), Arg(2))));
    else if (command is "dialogue" or "voice" && verb == "search")
    {
        var query = Required(2); var speaker = Opt("speaker", Opt("actor"));
        var titles = archive.Index("catalog")["stories"]!.AsArray().OfType<JsonNode>().Where(r => S(r, "site") == site).ToDictionary(r => S(r, "advId"), r => r["titles"]);
        result = Page(SiteRows(command == "voice" ? "voices" : "dialogue").Where(r =>
            (speaker == "" || S(r, "speakerTarget").Equals(speaker, StringComparison.OrdinalIgnoreCase)) &&
            (Opt("adv") == "" || S(r, "advId") == Opt("adv")) &&
            (!options.ContainsKey("with-voice") || r["voices"]!.AsArray().Count > 0) &&
            Contains(language == "" ? r["text"]?.ToJsonString(json) ?? "" : r["text"]?[language]?.ToString() ?? "", query))
            .Select(r => { r["titles"] = titles.GetValueOrDefault(S(r, "advId"))?.DeepClone(); foreach (var v in r["voices"]!.AsArray().OfType<JsonNode>()) { v["resolvedPath"] = archive.SitePath(site, S(v, "asset")); if ((double?)v["sampleRate"] > 0) v["duration"] = (double)v["samples"]! / (double)v["sampleRate"]!; } return r; }));
    }
    else if ((command == "voice" && verb == "extract") || (command == "asset" && verb == "extract" && Arg(2).StartsWith("voice:")))
    {
        var id = Required(2).Replace("voice:", "", StringComparison.Ordinal);
        var candidates = SiteRows("voices").Where(r => Opt("adv") == "" || S(r, "advId") == Opt("adv")).SelectMany(r => r["voices"]!.AsArray().OfType<JsonNode>()).Where(v => S(v, "soundId") == id).DistinctBy(v => S(v, "asset")).ToArray();
        if (candidates.Length != 1) throw new ArgumentException($"Voice matched {candidates.Length} distinct assets; use --site/--adv to disambiguate");
        var v = candidates[0]; result = archive.Export(site, archive.Decode(site, S(v, "asset")), Output(), v, options.ContainsKey("overwrite"));
    }
    else if (command == "asset" && verb is "extract" or "verify" or "show")
    {
        if ((Opt("adv") == "") == (Opt("model") == "")) throw new ArgumentException("Specify exactly one of --adv or --model");
        var manifest = archive.Manifest(site, Opt("model") == "" ? "stories" : "models", Opt("model", Opt("adv")));
        var bytes = archive.Logical(site, manifest, Required(2), language == "" ? null : language);
        result = verb == "show" ? JsonNode.Parse(bytes) : verb == "extract" ? archive.Export(site, bytes, Output(), JsonOutput.Object(("adv", Opt("adv")), ("model", Opt("model")), ("logicalPath", Arg(2)), ("language", language)), options.ContainsKey("overwrite")) : JsonOutput.Object(("verified", true), ("size", bytes.Length), ("sha256", Hash(bytes)));
    }
    else if (command == "asset" && verb == "search")
        result = Page(archive.Rows("files").Where(r => S(r, "site") == site && Contains(S(r, "logicalPath"), Arg(2)) && (Opt("kind") == "" || S(r, "kind") == Opt("kind")) && (Opt("adv") == "" || S(r, "advId") == Opt("adv")) && (Opt("model") == "" || S(r, "modelId") == Opt("model"))).Select(r => { r["resolvedPath"] = archive.SitePath(site, S(r, "asset")); return r; }));
    else if (command == "commands" && verb == "list")
    {
        if (Opt("adv") == "") throw new ArgumentException("--adv is required");
        result = Page(SiteRows("commands").Where(r => S(r, "advId") == Opt("adv")));
    }
    else if (command is "stage" or "postfx" or "still" or "frame" or "effect" or "media" && verb is "list" or "show")
    {
        var key = command switch { "stage" => "stageDetails", "postfx" => "postEffects", "still" => "stills", "frame" => "frames", "effect" => "effects", _ => "videos" };
        var registry = ReadJson(archive.SitePath(site, "director/resources.json"));
        var rows = registry[key]!.AsArray().OfType<JsonNode>().Select(r => r is JsonValue ? (JsonNode)new JsonObject { ["id"] = r.DeepClone() } : r.DeepClone()).ToArray();
        foreach (var r in rows) { r["id"] ??= r["name"]?.DeepClone(); r["sourceRegistry"] = "director/resources.json"; }
        if (verb == "list") result = Page(rows);
        else
        {
            var resource = rows.FirstOrDefault(r => S(r, "id") == Required(2)) ?? throw new ArgumentException("Resource not found");
            var logicalPath = command switch { "stage" => "scene.json", "postfx" => "posteffects.json", "still" => "stills.json", "frame" => "frames.json", "effect" => "effects.json", _ => "videos/videos.json" };
            var group = command == "stage" ? "stages" : key;
            var manifest = ReadJson(archive.SitePath(site, "director/manifest.json"));
            var document = JsonNode.Parse(archive.Logical(site, manifest, logicalPath))!;
            result = JsonOutput.Object(("resource", resource), ("sourceLogicalFile", logicalPath), ("sourceManifest", "director/manifest.json"), ("definition", document[group]?[Arg(2)]));
        }
    }
    else throw new ArgumentException("Unknown command; use help");
    Console.WriteLine(JsonOutput.Serialize(JsonOutput.Object(("schema", "ournotes-director/1"), ("ok", true), ("provenance", JsonOutput.Object(("archive", archive.Root), ("site", site))), ("result", result)), json));
    return 0;
}
catch (Exception ex)
{
    if(args.Contains("--debug"))Console.Error.WriteLine(ex);
    var code = ex is OperationCanceledException ? 130 : ex is NotSupportedException ? 4 : ex is ArgumentException or FormatException or JsonException ? 2 : 3;
    Console.WriteLine(JsonOutput.Serialize(JsonOutput.Object(("schema", "ournotes-director/1"), ("ok", false), ("error", JsonOutput.Object(("type", ex.GetType().Name), ("message", ex.Message))), ("exitCode", code)), json));
    return code;
}
