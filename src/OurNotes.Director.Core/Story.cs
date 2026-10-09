using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static OurNotes.Director.Core.Archive;

namespace OurNotes.Director.Core;

// Reads the lossless nnnotes episode, not the archive's abbreviated command index.
public sealed class Story
{
    private readonly Archive archive;
    private readonly string site;
    public string Id { get; }
    public JsonNode Manifest { get; }
    public JsonNode Episode { get; }
    public string EpisodeSha256 { get; }
    public JsonNode[] Commands { get; }
    public static bool Enabled(JsonNode row) => row["IgnoreData"]?.ToString() is not ("1" or "true");
    public Story(Archive archive, string site, string id)
    {
        this.archive = archive; this.site = site; Id = id;
        Manifest = archive.Manifest(site, "stories", id);
        var bytes = archive.Logical(site, Manifest, "episode.json");
        EpisodeSha256 = Hash(bytes); Episode = JsonNode.Parse(bytes)!;
        Commands = Episode["commands"]!.AsArray().OfType<JsonNode>().ToArray();
    }

    private static string Area(string command) => command switch
    {
        "Character" or "Costume" or "In" or "Out" or "Motion" or "MotionLoop" or "Expression" or "Pause" or "Resume" or "EyeBlink" => "character",
        "Talk" or "TalkWindow" or "Location" or "Subtitles" => "dialogue-ui",
        "Bgm" or "Se" or "Voice" or "SoundVolume" => "audio",
        "Stage" or "StageEnv" or "RimLight" => "stage",
        "DoF" or "PostEffect" or "Frame" or "Effect" or "Still" or "FadeIn" or "FadeOut" or "Flash" => "compositing",
        "Delay" or "Wait" or "CancelDelay" or "ForceAuto" or "ChoiceSet" or "ChoiceShow" or "GoTo" => "control-flow",
        "Movie" or "Clip" or "Timeline" => "video",
        _ when command.StartsWith("Chat", StringComparison.Ordinal) => "chat-ui",
        _ => "camera-and-placement"
    };

    public object Audit()
    {
        var active = Commands.Where(Enabled).ToArray();
        var files = Manifest["files"]!.AsObject();
        var missing = new List<string>(); var assets = new HashSet<string>();
        foreach (var entry in files)
        {
            if (entry.Value?["asset"] is JsonNode asset) assets.Add(asset.ToString());
            if (entry.Value?["parts"] is JsonArray parts)
                foreach (var part in parts) assets.Add(part![1]!.ToString());
        }
        foreach (var asset in assets) if (!File.Exists(archive.SitePath(site, asset))) missing.Add(asset);
        return JsonOutput.Object(("advId", Id), ("title", Manifest["story"]?["titles"]), ("episodeSha256", EpisodeSha256), ("commandCount", Commands.Length), ("activeCommandCount", active.Length), ("ignoredCommandCount", Commands.Length - active.Length), ("nativeOriginalPlaybackReady", false), ("blockers", new[] { "Archive audit does not execute the native player. Use story native-check for resource/handler initialization and story clip --until-end for complete timeline validation; full visual parity remains in progress." }), ("commands", active.GroupBy(c => S(c, "cmd")).Select(g => JsonOutput.Object(("command", g.Key), ("count", g.Count()), ("area", Area(g.Key)), ("nativePlayback", "not-tested-by-archive-audit"), ("firstRow", g.First()["i"]), ("rows", g.Select(c => c["i"]).ToArray()))).ToArray()), ("characters", active.Where(c => S(c, "cmd") == "Character").Select(c => JsonOutput.Object(("target", S(c, "TargetName")), ("asset", S(c, "TargetAssetName")), ("assetIndex", c["TargetAssetIndex"] ?? JsonValue.Create(0)))).ToArray()), ("assets", JsonOutput.Object(("logicalFiles", files.Count), ("storedAssets", assets.Count), ("missing", missing), ("verification", "Base episode manifest existence only, excluding language overrides and external model manifests; episode decoded and SHA-256 verified. Use asset verify for other file contents."))), ("source", "nnnotes episode.json; IgnoreData excluded per ournotes-player checkEpisodeCommands"));
    }

    public DialoguePlan Plan(string language, int offset, int limit, double gap, double silentSeconds)
    {
        if (offset < 0 || limit < 1) throw new ArgumentException("Invalid dialogue range");
        if (!double.IsFinite(gap) || gap < 0 || gap > 30 || !double.IsFinite(silentSeconds) || silentSeconds <= 0 || silentSeconds > 60)
            throw new ArgumentException("gap must be 0..30 and silent-seconds must be >0..60");
        var fields = new Dictionary<string, string> { ["ja"] = "japanese", ["en"] = "english", ["zh-Hans"] = "simplifiedChinese", ["zh-Hant"] = "traditionalChinese", ["ko"] = "korean" };
        if (!fields.TryGetValue(language, out var field)) throw new ArgumentException("Unsupported dialogue language");
        var active = Commands.Where(Enabled).ToArray();
        if (active.Any(c => S(c, "cmd") is "GoTo" or "ChoiceShow"))
            throw new NotSupportedException("Branching episode needs an explicit route before a linear dialogue plan can be exported.");
        var indexed = archive.Rows("dialogue").Where(r => S(r, "site") == site && S(r, "advId") == Id)
            .ToDictionary(r => int.Parse(S(r, "commandIndex"), CultureInfo.InvariantCulture));
        var talks = active.Where(c => S(c, "cmd") == "Talk" && S(c, "AdvTextID") is not ("" or "0")).ToArray();
        var selected = talks.Skip(offset).Take(limit).ToArray();
        if (selected.Length == 0) throw new ArgumentException("No dialogue in requested range");
        var plan = new DialoguePlan { AdvId = Id, Site = site, Language = language, EpisodeSha256 = EpisodeSha256, Offset = offset, TotalDialogue = talks.Length, GapFrames = Frames(gap), SilentFrames = Frames(silentSeconds) };
        var position = 0;
        foreach (var row in selected)
        {
            int index = row["i"]!.GetValue<int>();
            indexed.TryGetValue(index, out var info);
            var text = row["lines"]?[field]?.ToString() ?? Episode["text"]?[S(row, "AdvTextID")]?[field]?.ToString();
            if (text is null) throw new InvalidDataException($"Missing {language} text at row {index}");
            var voices = new List<DialogueVoice>();
            foreach (var voiceId in row["VoiceIDs"]?.AsArray() ?? [])
            {
                var v = info?["voices"]?.AsArray().OfType<JsonNode>().SingleOrDefault(v => S(v, "soundId") == voiceId!.ToString())
                    ?? throw new InvalidDataException($"Missing voice {voiceId} metadata at row {index}");
                var samples = v["samples"]!.GetValue<long>(); var rate = v["sampleRate"]!.GetValue<int>();
                if (samples <= 0 || rate <= 0) throw new InvalidDataException($"Invalid voice duration at row {index}");
                var logical = S(v, "logicalPath");
                if (Manifest["files"]?[logical] is null) throw new InvalidDataException($"Voice not in episode manifest: {logical}");
                voices.Add(new() { SoundId = voiceId!.ToString(), LogicalPath = logical, Asset = S(v, "asset"), Samples = samples, SampleRate = rate });
            }
            int content = voices.Count == 0 ? plan.SilentFrames : voices.Max(v => Frames((double)v.Samples / v.SampleRate));
            var speaker = S(row, "TargetName");
            var labels = row["TargetTextIDs"]?.AsArray().Select(id => Episode["text"]?[id!.ToString()]?[field]?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            plan.Lines.Add(new() { CommandIndex = index, TextId = S(row, "AdvTextID"), SpeakerTarget = speaker, Speaker = labels?.Length > 0 ? string.Join(" / ", labels) : speaker, Text = text, StartFrame = position, ContentFrames = content, DurationFrames = content + plan.GapFrames, Voices = voices });
            position = checked(position + content + plan.GapFrames);
        }
        plan.FrameCount = position;
        return plan;
    }

    public static int Frames(double seconds) => checked((int)Math.Ceiling(seconds * 30 - 1e-9));

    public object ExportDialogue(DialoguePlan plan, string output, string ffmpeg)
    {
        var target = Path.GetFullPath(output);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Dialogue package output must be a new directory");
        var parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent);
        var scratch = Path.Combine(parent, ".dialogue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(scratch, "voices"));
        try
        {
            var voices = plan.Lines.SelectMany(l => l.Voices).DistinctBy(v => v.Asset).ToArray();
            foreach (var voice in voices)
            {
                var bytes = archive.Logical(site, Manifest, voice.LogicalPath);
                File.WriteAllBytes(Path.Combine(scratch, voice.PackagePath), bytes);
            }
            // 48 kHz stereo PCM is exactly 1600 samples per 30 Hz frame.
            long bytesExpected = (long)plan.FrameCount * 1600 * 4;
            if (bytesExpected > uint.MaxValue - 36) throw new NotSupportedException("Dialogue WAV exceeds RIFF size limit; export a smaller range");
            var wav = Path.Combine(scratch, "dialogue.wav");
            using (var stream = File.Create(wav))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write((uint)(bytesExpected + 36)); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write((uint)bytesExpected);
                var linePcm = Path.Combine(scratch, "line.pcm");
                foreach (var line in plan.Lines)
                {
                    var samples = (long)line.DurationFrames * 1600;
                    if (line.Voices.Count == 0) WriteSilence(stream, samples * 4);
                    else
                    {
                        var args = new List<string> { "-v", "error", "-nostdin", "-y" };
                        foreach (var voice in line.Voices) args.AddRange(["-i", Path.Combine(scratch, voice.PackagePath)]);
                        var filters = string.Concat(Enumerable.Range(0, line.Voices.Count).Select(i => $"[{i}:a:0]")) +
                            FormattableString.Invariant($"amix=inputs={line.Voices.Count}:duration=longest:normalize=0,volume={1.0 / line.Voices.Count:R},aresample=48000,apad=whole_len={samples},atrim=end_sample={samples}[a]");
                        args.AddRange(["-filter_complex", filters, "-map", "[a]", "-ac", "2", "-ar", "48000", "-f", "s16le", linePcm]);
                        Media.Run(ffmpeg, args);
                        using (var pcm = File.OpenRead(linePcm)) { if (pcm.Length != samples * 4) throw new InvalidDataException("Unexpected decoded PCM length"); pcm.CopyTo(stream); }
                        File.Delete(linePcm);
                    }
                    Console.Error.WriteLine($"dialogue row {line.CommandIndex}");
                }
            }
            File.WriteAllText(Path.Combine(scratch, "timeline.json"), JsonOutput.Serialize(plan, Shot.JsonOptions), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(scratch, "dialogue.srt"), plan.Srt(), new UTF8Encoding(false));
            var inventory = Directory.GetFiles(scratch, "*", SearchOption.AllDirectories).Order().Select(path => { using var source = File.OpenRead(path); return JsonOutput.Object(("path", Path.GetRelativePath(scratch, path).Replace('\\', '/')), ("size", source.Length), ("sha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(source)))); }).ToArray();
            File.WriteAllText(Path.Combine(scratch, "manifest.json"), JsonOutput.Serialize(JsonOutput.Object(("schema", "ournotes-dialogue-package/1"), ("AdvId", plan.AdvId), ("EpisodeSha256", plan.EpisodeSha256), ("files", inventory)), Shot.JsonOptions));
            Directory.Move(scratch, target);
            return JsonOutput.Object(("output", target), ("lines", plan.Lines.Count), ("FrameCount", plan.FrameCount), ("duration", plan.FrameCount / 30.0), ("originalGameTiming", false), ("files", inventory));
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
    private static void WriteSilence(Stream stream, long bytes)
    {
        var zero = new byte[65536];
        while (bytes > 0) { int n = (int)Math.Min(bytes, zero.Length); stream.Write(zero, 0, n); bytes -= n; }
    }
}

public sealed class DialogueVoice
{
    public string SoundId { get; set; } = "";
    public string LogicalPath { get; set; } = "";
    public string Asset { get; set; } = "";
    public long Samples { get; set; }
    public int SampleRate { get; set; }
    public string PackagePath => "voices/" + Path.GetFileName(Asset).Split('.')[0] + Path.GetExtension(LogicalPath);
}
public sealed class DialogueLine
{
    public int CommandIndex { get; set; }
    public string TextId { get; set; } = "";
    public string SpeakerTarget { get; set; } = "";
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
    public int StartFrame { get; set; }
    public int ContentFrames { get; set; }
    public int DurationFrames { get; set; }
    public List<DialogueVoice> Voices { get; set; } = [];
}
public sealed class DialoguePlan
{
    public string Schema => "ournotes-dialogue-plan/1";
    public string TimingPolicy => "editorial-voice-duration";
    public bool OriginalGameTiming => false;
    public string[] Limitations => ["Talk rows only, in source order; no game delays, transitions, BGM, SE, chat or standalone Voice commands.", "Voiced line: longest voice rounded up to 30 Hz plus gap. Silent line: configured fixed duration plus gap.", "Simultaneous voices start together at gain 1/count. Subtitles retain dialogue through its trailing gap."];
    public string AdvId { get; set; } = "";
    public string Site { get; set; } = "";
    public string Language { get; set; } = "";
    public string EpisodeSha256 { get; set; } = "";
    public int Fps => 30;
    public int Offset { get; set; }
    public int TotalDialogue { get; set; }
    public int GapFrames { get; set; }
    public int SilentFrames { get; set; }
    public int FrameCount { get; set; }
    public List<DialogueLine> Lines { get; set; } = [];
    public string Srt()
    {
        static string Stamp(int frame) { long ms = (long)Math.Round(frame * 1000.0 / 30, MidpointRounding.AwayFromZero); return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}"; }
        var text = new StringBuilder();
        for (int i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var body = Regex.Replace(line.Text.Replace("\r", ""), "<[^>]+>", "");
            body = Regex.Replace(body, "\n{2,}", "\n");
            text.Append(i + 1).Append('\n').Append(Stamp(line.StartFrame)).Append(" --> ").Append(Stamp(line.StartFrame + line.DurationFrames)).Append('\n');
            if (line.Speaker.Length > 0) text.Append(line.Speaker.Replace("\n", " ")).Append(": ");
            text.Append(body).Append("\n\n");
        }
        return text.ToString();
    }
}
