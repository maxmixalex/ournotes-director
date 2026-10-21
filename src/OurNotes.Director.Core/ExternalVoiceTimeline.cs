using System.Globalization;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Audio-only execution for explicitly timed, locally voiced shots. The same
/// player, PCM mixer, external resource resolver and speaker ownership are used by the
/// graphical renderer. Unsupported movie/game-audio semantics use the full interpreter.</summary>
public sealed class ExternalVoiceTimeline
{
    static readonly HashSet<string> VisualOnly = new(StringComparer.Ordinal)
    {
        "Character", "Motion", "MotionLoop", "Expression", "Delay", "Pause", "Resume", "EyeBlink",
        "In", "Out", "Costume", "Director.Actor", "Director.Camera", "Director.Parameter", "Director.Scene"
    };
    public static bool Supports(JsonNode document)
    {
        if(document["initialize"] is not JsonArray {Count:0} || document["finalize"] is not JsonArray {Count:0}
            || document["commands"] is not JsonArray rows) return false;
        foreach(var item in rows)
        {
            if(item is not JsonObject row || row["AtFrame"] is not JsonValue value || !value.TryGetValue<int>(out _))return false;
            var command=AdvProgram.Row(row,"audio eligibility");if(command.Ignored)continue;
            if(VisualOnly.Contains(command.Command))continue;
            if(command.Command!="Voice" || string.IsNullOrWhiteSpace(command.Text("AudioPath")))return false;
        }
        // The scene player's letterbox preparation would add a source-dependent wait.
        return document["output"]?["letterbox"]?.ToString()=="none" && document["output"]?["playbackViewport"] is null;
    }
    public AdvLoop Loop {get;} = new();
    public AdvAudio Audio {get;}
    public AdvPlayback Player {get;}
    readonly ExternalVoiceBank files;
    readonly VoiceTrackPlayback voices;
    public ExternalVoiceTimeline(JsonNode document,string baseDirectory,string ffmpeg)
    {
        if(!Supports(document))throw new ArgumentException("This scene requires the complete audio interpreter.");
        var rows=document["commands"]!.AsArray().Select((row,index)=>AdvProgram.Row(row!,index.ToString(CultureInfo.InvariantCulture))).ToArray();
        Audio=new(Loop,[]);files=new(Audio,baseDirectory,ffmpeg);voices=new(Audio);
        Player=new(Loop,new AdvProgram{Rows=rows}){Prepare=_=>[]};Audio.Bind(Player);
        Player.Register("TalkWindow",(_,_)=>[]);
        foreach(string command in rows.Where(row=>!row.Ignored).Select(row=>row.Command).Distinct())
        {
            if(command=="Voice")Player.Register(command,PlayVoice);
            else if(command!="Delay")Player.Register(command,(_,_)=>[]);
        }
        Player.Start();
    }
    IEnumerable<AdvWait> PlayVoice(AdvRow row,AdvPlayback player)
    {
        row=files.Resolve(row);
        var names=row.Text("TargetName").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        var ids=row.Data["VoiceIDs"]!.AsArray().Select(id=>long.Parse(id!.ToString(),CultureInfo.InvariantCulture)).ToArray();
        var sounds=voices.Start(names,ids,row.Index);
        while(!player.Cancelled&&sounds.Any(s=>Audio.IsPlaying(s.Id)))yield return Loop.YieldUpdate();
    }
    public void MixFrame(Span<float> output) {Loop.Drain();Player.ThrowIfFailed();Audio.MixFrame(output);}
    public void Step()=>Player.Step();
}
