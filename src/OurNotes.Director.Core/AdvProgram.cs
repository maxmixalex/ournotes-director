using System.Globalization;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed record AdvRow(string Index, string Command, JsonNode Data)
{
    public bool Ignored => Flag("IgnoreData");
    public string Text(string field) => Archive.S(Data, field);
    public bool Flag(string field) => Text(field) is "1" or "true";
    public float Number(string field, float fallback = 0) => Data[field] is null ? fallback : float.Parse(Text(field), CultureInfo.InvariantCulture);
}
public sealed class AdvProgram
{
    public static readonly string[] DirectorCommands=["Director.Actor","Director.Camera","Director.Parameter","Director.Scene"];
    public static bool KnownCommand(string name)=>Names.Contains(name)||DirectorCommands.Contains(name);
    // nnnotes advcommand.py and ournotes-player interfaces.js; holes 8 and 22 are unused.
    public static readonly string?[] Names = ["In","Out","Talk","Delay","Shake","FadeOut","FadeIn","Focus",null,"Forward","Back","Flash","Brightness","MoveToRight","MoveToLeft","Bgm","SoundVolume","Expression","Pause","Resume","Location","Motion",null,"Character","Costume","Stage","Movie","Clip","Subtitles","Wait","Still","Se","Angle","Pan","Tilt","TalkWindow","ChatWindow","ChatTalk","ChatStamp","ChatRead","ChoiceSet","ChoiceShow","GoTo","PostEffect","Frame","Timeline","Look","Pedestal","Track","DoF","Role","CameraShake","Voice","Zoom","Effect","Alpha","ForceAuto","StageEnv","RimLight","CancelDelay","MoveToUp","MoveToDown","MoveToForward","MoveToBack","MoveToDirection","ChatTyping","LookTarget","PanV2","MotionLoop","EyeBlink"];
    public AdvRow[] Initialize { get; init; } = [];
    public AdvRow[] Rows { get; init; } = [];
    public AdvRow[] Finalize { get; init; } = [];
    public float AutoWaitSeconds { get; init; }
    public int PlaybackMode { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public JsonNode? Scene { get; private set; }
    public string[] RequiredCommands => new[] { "TalkWindow" }.Concat(Initialize.Concat(Rows).Concat(Finalize).Where(r=>!r.Ignored).Select(r => r.Command)).Distinct().ToArray();
    public static AdvRow Row(JsonNode node, string index)
    {
        var name = Archive.S(node,"cmd");
        var number = node["Command"] ?? node["raw"];
        if (number is not null)
        {
            int n = number.GetValue<int>();
            if (n < 0 || n >= Names.Length || Names[n] is null) throw new NotSupportedException($"Unknown AdvCommand {n} at {index}");
            if (name.Length > 0 && name != Names[n]) throw new InvalidDataException($"Command number/name mismatch at {index}");
            name = Names[n]!;
        }
        if (!KnownCommand(name)) throw new NotSupportedException($"Unknown command {name} at {index}");
        return new(index, name, node.DeepClone());
    }
    public static AdvProgram From(JsonNode episode, JsonNode settings)
    {
        static AdvRow[] SettingsRows(JsonNode settings,string key,string prefix) => settings[key]!.AsArray().Select((row,i)=>Row(row!,prefix+i)).ToArray();
        return new() {
            Initialize = SettingsRows(settings,"_initializeEpisodes","init"),
            Finalize = SettingsRows(settings,"_finalizeEpisodes","fin"),
            Rows = episode["commands"]!.AsArray().Select((row,i)=>Row(row!,Archive.S(row!,"i",i.ToString(CultureInfo.InvariantCulture)))).ToArray(),
            AutoWaitSeconds = settings["_waitCommandLingeringTimeOnAutoPlay"]?.GetValue<float>() ?? throw new InvalidDataException("Missing auto wait setting"),
            PlaybackMode = episode["master"]?["_playbackMode"]?.GetValue<int>() ?? 0
        };
    }
    public static AdvProgram Load(Archive archive,string site,string id)
    {
        var manifest=archive.Manifest(site,"stories",id);
        var story=JsonNode.Parse(archive.Logical(site,manifest,"story.json"))!;
        var episode=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(story,"episode")))!;
        var scene=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(story,"scene")))!;
        var program=From(episode,scene["settings"]!["playerSettings"]!);program.Scene=scene;return program;
    }
}
