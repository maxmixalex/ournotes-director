using System.Globalization;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

internal static class HomeCommand
{
    public static object Run(Archive archive, string? site, string verb, string id, IReadOnlyDictionary<string, string> options)
    {
        string Opt(string key, string fallback = "") => options.GetValueOrDefault(key, fallback);
        int Int(string key, int fallback) => int.Parse(Opt(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        double Double(string key, double fallback) => double.Parse(Opt(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        HomeProject? project = options.TryGetValue("project", out var projectPath) ? HomeProject.Read(projectPath) : null;
        if (project is not null)
        {
            if (verb is not ("frame" or "contact" or "clip" or "sequence" or "export")) throw new ArgumentException("--project is supported by home frame/contact/clip/sequence/export.");
            if (id.Length == 0) id = project.Source.SpotId.ToString(CultureInfo.InvariantCulture);
            if (id != project.Source.SpotId.ToString(CultureInfo.InvariantCulture)) throw new ArgumentException("Home project spotId does not match the requested spot.");
            if (site is not null && site != project.Source.Site) throw new ArgumentException("Home project site does not match --site.");
            site = project.Source.Site;
        }
        var catalog = new HomeCatalog(archive);
        if (verb == "list")
        {
            var all = catalog.List(site); int offset = Int("offset", 0), limit = Int("limit", 50);
            return JsonOutput.Object(("total", all.Count), ("offset", offset), ("limit", limit), ("items", all.Skip(offset).Take(limit).ToArray()), ("nextOffset", offset + limit < all.Count ? (int?)(offset + limit) : null));
        }
        if (verb is not ("show" or "animations" or "frame" or "contact" or "clip" or "sequence" or "export")) throw new ArgumentException("Home operation must be list, show, animations, frame, contact, clip, sequence or export.");
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A home spot ID is required; see home list.");
        if (id == "all" && verb != "export") throw new ArgumentException("Use home export all to export every archived home.");
        long? adv = options.ContainsKey("adv") ? long.Parse(Opt("adv"), CultureInfo.InvariantCulture) : project?.Source.AdvId;
        if (project?.Source.AdvId is { } sourceAdv && adv != sourceAdv) throw new ArgumentException("Home project advId does not match --adv.");
        if (id == "all" && adv is not null) throw new ArgumentException("--adv selects a source story for one home spot.");
        HomeScene? scene = id == "all" ? null : catalog.Open(long.Parse(id, CultureInfo.InvariantCulture), site, adv);
        if (verb == "show") return scene!.ToJson(includeSlotFilters: true);
        string selector = Opt("character");
        if (selector == "all" && verb != "export") throw new ArgumentException("Use home export <spot-id> --character all for separate character files, or omit --character for the complete scene.");
        string? animationName = options.GetValueOrDefault("animation");
        bool? loop = options.ContainsKey("loop") ? true : null;
        HomeCharacter? Character(HomeScene selected) => selector is "" or "all" ? null : selected.SelectCharacter(selector);
        if (verb == "animations") return new HomeAnimation(new(scene!), Character(scene!), animationName, loop).Describe();
        int width = Int("width", project?.Output.Width ?? (verb == "contact" ? 640 : 1920)), height = Int("height", project?.Output.Height ?? (verb == "contact" ? 360 : 1080)),
            supersample = Int("supersample", project?.Output.Supersample ?? 2), fps = Int("fps", project?.Output.Fps ?? 30);
        double time = Double("time", 0); double? duration = options.ContainsKey("duration") ? Double("duration", 0) : null;
        if (duration is null && project is not null && verb is "clip" or "sequence" or "export")
            duration = project.Duration - time > 0 ? project.Duration - time : throw new ArgumentException("The export start time must be before the project end.");
        _ = HomeTiming.Plan(0, time, duration, fps);
        HomeRenderer.ValidateDimensions(width, height, supersample);
        string codec = VideoEncoding.NormalizeCodec(Opt("codec", project?.Output.Codec ?? "cineform")), quality = Opt("cineform-quality", project?.Output.CineformQuality ?? VideoEncoding.DefaultCineFormQuality);
        bool background = !options.ContainsKey("no-background") && (project?.EditState.Background ?? true), overwrite = options.ContainsKey("overwrite");
        string output = Path.GetFullPath(Opt("out") is { Length: > 0 } path ? path : throw new ArgumentException("--out is required."));
        string? ffmpeg = verb is "clip" or "export" ? NativeDependencies.MediaTool("ffmpeg", options.GetValueOrDefault("ffmpeg")) : null;
        string Framing(HomeCharacter? character) => Opt("framing", project?.EditState.Framing ?? (character is null ? "scene" : "fit"));
        HomeEditState? Edits(HomeCharacter? character) => project is null ? null : project.EditState with { Background = background, Framing = Framing(character) };
        if (Opt("framing") is not ("" or "scene" or "fit")) throw new ArgumentException("--framing must be scene or fit.");
        if (verb != "export")
        {
            var animation = new HomeAnimation(new(scene!), Character(scene!), animationName, loop);
            var timing = HomeTiming.Plan(animation.Duration, time, duration, fps, animation.AllLooping);
            double[]? times = verb == "contact" ? Opt("times", "0,1,2,3").Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray() : null;
            var report = HomeExport.Run(animation, verb, output, timing, width, height, supersample, Framing(animation.Character), background, codec, quality, overwrite, ffmpeg, times, Int("columns", 3), Edits(animation.Character));
            if (project is not null) { report["projectFormat"] = HomeProject.Format; report["projectDuration"] = project.Duration; }
            return report;
        }
        if (overwrite) throw new ArgumentException("Home export publishes a new directory; --overwrite is not supported.");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Home export requires a new output directory.");
        VideoEncoding.Validate(codec, width, height, quality);
        if (codec == "h264") throw new ArgumentException("Home export includes transparent characters; use cineform, prores or ffv1.");
        string extension = VideoEncoding.Format(codec) == "matroska" ? ".mkv" : ".mov";
        var scenes = scene is not null ? new[] { scene } : catalog.List(site).OfType<JsonObject>()
            .Select(item => catalog.Open(item["spotId"]!.GetValue<long>(), item["site"]!.GetValue<string>())).ToArray();
        if (scenes.Length == 0) throw new InvalidDataException("The archive contains no home spots.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(temp);
            var exports = new JsonArray();
            foreach (var selected in scenes)
            {
                var resources = new HomeResources(selected);
                var complete = new HomeAnimation(resources, animation: animationName, loop: loop);
                // All files in a batch share the full scene's timeline, so shorter parts hold their last pose.
                var timing = HomeTiming.Plan(complete.Duration, time, duration, fps, complete.AllLooping);
                string relative = scene is null ? selected.Site + "-" + selected.SpotId.ToString(CultureInfo.InvariantCulture) : "";
                var choices = new List<HomeCharacter?>();
                if (selector.Length == 0) choices.Add(null);
                if (selector is "" or "all") choices.AddRange(selected.Characters);
                else choices.Add(Character(selected));
                foreach (var character in choices)
                {
                    var current = character is null ? complete : new HomeAnimation(resources, character, animationName, loop);
                    string file = Path.Combine(relative, character is null ? "scene" + extension : Path.Combine("characters", character.Key + extension));
                    var report = HomeExport.Run(current, "clip", Path.Combine(temp, file), timing, width, height, supersample, Framing(character), background, codec, quality, false, ffmpeg, editState: Edits(character));
                    report["output"] = file.Replace('\\', '/'); exports.Add((JsonNode)report);
                }
            }
            var result = new JsonObject { ["format"] = "ournotes-home-export/1", ["output"] = output, ["spotCount"] = scenes.Length, ["fileCount"] = exports.Count,
                ["codec"] = codec, ["cineformQuality"] = codec == "cineform" ? VideoEncoding.NormalizeCineFormQuality(quality) : null,
                ["width"] = width, ["height"] = height, ["fps"] = fps, ["exports"] = exports };
            File.WriteAllBytes(Path.Combine(temp, "export.json"), JsonOutput.SerializeToUtf8Bytes(result));
            Directory.Move(temp, output);
            return result;
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }
}
