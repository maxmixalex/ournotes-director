using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed class Shot
{
    public int Version {get;set;}=1;
    public double Duration {get;set;}=3;
    public int Fps {get;set;}=30;
    public uint Seed {get;set;}=722;
    public int WarmupFrames {get;set;}=30;
    public OutputSpec Output {get;set;}=new();
    public float[] Background {get;set;}=[0,0,0,0];
    public ImageSpec? BackgroundImage {get;set;}
    public ShotSource? Source {get;set;}
    public string? Stage {get;set;}
    public GradeSpec Grade {get;set;}=new();
    public CameraSpec Camera {get;set;}=new();
    public List<ActorSpec> Actors {get;set;}=[];
    public List<TrackSpec> Tracks {get;set;}=[];
    public List<EventSpec> Events {get;set;}=[];
    public static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    static readonly DirectorJsonContext JsonContext = new(new JsonSerializerOptions(JsonOptions));
    public static Shot Read(string path)=>JsonSerializer.Deserialize(File.ReadAllText(path),JsonContext.Shot)??throw new InvalidDataException("Empty shot");
    public static Shot CreateTemplate(Archive archive,string site,string template="fullbody",string? model=null,string? language=null)
    {
        if(template is not ("fullbody" or "portrait" or "duet"))throw new ArgumentException("--template: fullbody, portrait, duet");
        // The published index is the current archive selection. Leftover model files from
        // an earlier band are reusable storage, but must not become a new template's defaults.
        var modelIndex=archive.Index("models");
        var models=modelIndex["models"]!.AsArray().OfType<JsonObject>()
            .Where(row=>Archive.S(row,"site")==site&&File.Exists(archive.SitePath(site,Archive.S(row,"manifest"))))
            .DistinctBy(row=>Archive.S(row,"id")).OrderBy(row=>Archive.S(row,"id"),StringComparer.Ordinal).ToArray();
        if(models.Length==0)throw new ArgumentException($"No downloaded models are selected for site '{site}'. Run archive for this site before shot init.");
        var band=modelIndex["band"];
        var discoveryPath=Path.Combine(archive.Root,"discovery.json");
        if(File.Exists(discoveryPath))
        {
            var discovery=Archive.ReadJson(discoveryPath);
            band=discovery["regions"]?[site]?["band"]??discovery["band"]??band;
        }
        var members=band?["characters"]?.AsArray().OfType<JsonObject>().Select(row=>Archive.S(row,"id")).ToHashSet(StringComparer.Ordinal)??[];
        JsonObject[] PreferMembers(JsonObject[] candidates)
        {
            var owned=candidates.Where(row=>members.Contains(Archive.S(row,"character"))).ToArray();
            return owned.Length>0?owned:candidates;
        }
        var defaults=PreferMembers(models);
        JsonObject First(string preferred)=>defaults.FirstOrDefault(row=>Archive.S(row,"id")==preferred)??defaults[0];
        var first=string.IsNullOrWhiteSpace(model)?First("adv_live2d_hotaru_016_casual_spring_01"):
            models.FirstOrDefault(row=>Archive.S(row,"id")==model)??throw new ArgumentException($"Model '{model}' is not in the selected archive for site '{site}'. Use model list to choose a downloaded model.");
        var selected=new List<JsonObject>{first};
        if(template=="duet")
        {
            var remaining=models.Where(row=>Archive.S(row,"id")!=Archive.S(first,"id")).ToArray();
            if(remaining.Length==0)throw new ArgumentException($"The duet template requires two different downloaded models in site '{site}'; only one is selected.");
            string Character(JsonObject row)=>Archive.S(row,"character",ActorName(Archive.S(row,"id")));
            var preferred=PreferMembers(remaining);
            var otherCharacter=preferred.Where(row=>Character(row)!=Character(first)).ToArray();
            var candidates=otherCharacter.Length>0?otherCharacter:preferred;
            selected.Add(candidates.FirstOrDefault(row=>Archive.S(row,"id")=="adv_live2d_natsume_017_casual_spring_01")??candidates[0]);
        }

        var sources=new List<(string Id,JsonObject Manifest,string[] Languages)>();
        foreach(var row in archive.Index("catalog")["stories"]!.AsArray().OfType<JsonObject>().Where(row=>Archive.S(row,"site")==site))
        {
            string path=archive.SitePath(site,Archive.S(row,"manifest"));if(!File.Exists(path))continue;
            var manifest=Archive.ReadJson(path).AsObject();
            if(Archive.S(manifest["story"]??new JsonObject(),"playbackMode","0") is not ("0" or "false" or ""))continue;
            if(manifest["files"] is not JsonObject files||new[]{"story.json","episode.json","scene.json"}.Any(key=>files[key] is null))continue;
            var languages=row["languages"]?.AsArray().OfType<JsonValue>().Select(value=>value.ToString()).ToArray()??[];
            // A language-filtered archive still stores common geometry, but cannot
            // render a source whose language-specific UI/fonts were all excluded.
            if(languages.Length==0&&manifest["languages"] is JsonObject {Count:>0})continue;
            if(!string.IsNullOrWhiteSpace(language)&&languages.Length>0&&!languages.Contains(language,StringComparer.Ordinal))continue;
            sources.Add((Archive.S(row,"advId"),manifest,languages));
        }
        if(sources.Count==0)throw new ArgumentException($"No downloaded normal ADV scene is available for site '{site}'"+
            (string.IsNullOrWhiteSpace(language)?".":$" in language '{language}'.")+" A shot template requires a normal ADV source.");
        string preferredSource="10060";
        var registryPath=archive.SitePath(site,"director/resources.json");
        if(File.Exists(registryPath)&&Archive.ReadJson(registryPath)["baseAdvId"] is JsonNode baseId)preferredSource=baseId.ToString();
        var source=sources.OrderBy(row=>row.Id==preferredSource?0:1)
            .ThenBy(row=>long.TryParse(row.Id,out long id)?id:long.MaxValue).ThenBy(row=>row.Id,StringComparer.Ordinal).First();
        var sourceLanguage=language;
        if(string.IsNullOrWhiteSpace(sourceLanguage))
            sourceLanguage=source.Languages.Contains("zh-Hans",StringComparer.Ordinal)?"zh-Hans":
                source.Languages.Contains(Archive.S(source.Manifest,"language"),StringComparer.Ordinal)?Archive.S(source.Manifest,"language"):
                source.Languages.FirstOrDefault()??Archive.S(source.Manifest,"language","zh-Hans");
        var shot=new Shot{Source=new(){Adv=source.Id,Site=site,Language=sourceLanguage}};
        foreach(var row in selected)
        {
            string id=Archive.S(row,"id"),name=ActorName(id),unique=name;int suffix=2;
            while(shot.Actors.Any(actor=>actor.Name==unique))unique=name+suffix++;
            shot.Actors.Add(new(){Name=unique,Model=id});
        }
        if(template=="portrait")shot.Camera=new(){Zoom=2.5f,Y=-.24f};
        if(template=="duet"){shot.Actors[0].X=.35f;shot.Actors[1].X=.65f;}
        shot.Validate();return shot;
    }
    static string ActorName(string model)
    {
        const string prefix="adv_live2d_";
        string name=model.StartsWith(prefix,StringComparison.Ordinal)?model[prefix.Length..].Split('_')[0]:"actor";
        return string.IsNullOrWhiteSpace(name)||name=="camera"?"actor":name;
    }
    public void Validate()
    {
        if(Source is not null&&(string.IsNullOrWhiteSpace(Source.Adv)||string.IsNullOrWhiteSpace(Source.Site)||string.IsNullOrWhiteSpace(Source.Language)))throw new ArgumentException("Shot source requires adv, site and language");
        if(Version!=1||Fps!=30||!double.IsFinite(Duration)||Duration<=0||Duration>600||Math.Abs(Duration*Fps-Math.Round(Duration*Fps))>1e-5)throw new ArgumentException("Shot requires version 1, fps 30, duration 0..600 with an integral frame count");
        if(Output.Width<16||Output.Height<16||Output.Width>7680||Output.Height>4320||Output.RenderScale is <1 or >4||WarmupFrames is <0 or >300)throw new ArgumentException("Invalid resolution/renderScale/warmupFrames");
        if(Background.Length!=4||Background.Any(v=>!float.IsFinite(v)||v<0||v>1))throw new ArgumentException("background must be RGBA 0..1");
        if(Stage is not null&&BackgroundImage is not null)throw new ArgumentException("Use stage or backgroundImage, not both");
        if(!float.IsFinite(Grade.Exposure)||Grade.Exposure is < -8 or > 8||!float.IsFinite(Grade.Contrast)||Grade.Contrast is <0 or >4||!float.IsFinite(Grade.Saturation)||Grade.Saturation is <0 or >4||!float.IsFinite(Grade.Vignette)||Grade.Vignette is <0 or >1)throw new ArgumentException("Invalid grade");
        if(Actors.Count==0||Actors.Count>16||Actors.Select(a=>a.Name).Distinct().Count()!=Actors.Count||Actors.Any(a=>string.IsNullOrWhiteSpace(a.Name)||string.IsNullOrWhiteSpace(a.Model)))throw new ArgumentException("Provide 1..16 uniquely named actors");
        if(!float.IsFinite(Camera.Zoom)||Camera.Zoom<=0||!float.IsFinite(Camera.X)||!float.IsFinite(Camera.Y)||!float.IsFinite(Camera.Roll))throw new ArgumentException("Invalid camera");
        foreach(var a in Actors){if(a.Name=="camera"||!float.IsFinite(a.Opacity)||a.Opacity is <0 or >1||!float.IsFinite(a.Brightness)||a.Brightness is <0 or >8||!float.IsFinite(a.Height)||a.Height<=0||a.Height>20||!float.IsFinite(a.X)||!float.IsFinite(a.Y)||!float.IsFinite(a.Rotation)||!float.IsFinite(a.Speed)||a.Speed<0||a.Tint.Length!=4||a.Tint.Any(v=>!float.IsFinite(v)||v<0)||a.Parameters.Values.Any(v=>!float.IsFinite(v)))throw new ArgumentException($"Invalid actor: {a.Name}");}
        if(Tracks.Select(t=>(t.Target,t.Property)).Distinct().Count()!=Tracks.Count)throw new ArgumentException("Duplicate tracks for the same target/property");
        foreach(var t in Tracks)foreach(var k in t.Keys){if(t.Property is "height" or "zoom"&&k.Value<=0||t.Property=="opacity"&&k.Value is <0 or >1||t.Property=="brightness"&&k.Value is <0 or >8)throw new ArgumentException("Track value out of range");}
        foreach(var t in Tracks){if(t.Interpolation is not ("linear" or "smoothstep" or "step")||t.Keys.Count==0)throw new ArgumentException("Invalid track interpolation/keys");double prev=-1;foreach(var k in t.Keys){if(!double.IsFinite(k.T)||k.T<0||k.T>Duration||k.T<=prev||!float.IsFinite(k.Value))throw new ArgumentException("Track keys must be finite and strictly increasing within duration");prev=k.T;}ValidatePath(t.Target,t.Property);}
        double time=-1;foreach(var e in Events){if(!double.IsFinite(e.T)||e.T<0||e.T>=Duration||e.T<time||!Actors.Any(a=>a.Name==e.Actor)||e.Motion is null&&e.Expression is null||e.Fade<0||!float.IsFinite(e.Fade))throw new ArgumentException("Invalid event; events must be sorted inside the shot");time=e.T;}
    }
    void ValidatePath(string target,string property){if(target=="camera"){if(property is not ("x" or "y" or "zoom" or "roll"))throw new ArgumentException($"Unknown camera property {property}");}else{if(!Actors.Any(a=>a.Name==target))throw new ArgumentException($"Unknown actor {target}");if(property is not ("x" or "y" or "height" or "rotation" or "opacity" or "brightness")&&!property.StartsWith("param:"))throw new ArgumentException($"Unknown actor property {property}");}}
    public float Value(string target,string property,double time,float fallback)
    {
        var track=Tracks.LastOrDefault(t=>t.Target==target&&t.Property==property);if(track is null)return fallback;
        var keys=track.Keys;if(time<=keys[0].T)return keys[0].Value;for(int i=1;i<keys.Count;i++)if(time<keys[i].T){var a=keys[i-1];var b=keys[i];float t=(float)((time-a.T)/(b.T-a.T));if(track.Interpolation=="step")t=0;else if(track.Interpolation=="smoothstep")t=t*t*(3-2*t);return a.Value+(b.Value-a.Value)*t;}return keys[^1].Value;
    }
}
public sealed class ShotSource {public string Adv{get;set;}="10060";public string Site{get;set;}="intl";public string Language{get;set;}="zh-Hans";}
public sealed class OutputSpec {public int Width{get;set;}=1920;public int Height{get;set;}=1080;public int RenderScale{get;set;}=2;}
public sealed class CameraSpec {public float X{get;set;}public float Y{get;set;}public float Zoom{get;set;}=1;public float Roll{get;set;}}
public sealed class GradeSpec {public float Exposure{get;set;}public float Contrast{get;set;}=1;public float Saturation{get;set;}=1;public float Vignette{get;set;}}
public sealed class ImageSpec {public string? Path{get;set;}public string? Adv{get;set;}public string? LogicalPath{get;set;}}
public sealed class ActorSpec
{
    public string Name{get;set;}="actor";public string Model{get;set;}="";public string? Motion{get;set;}public string? Expression{get;set;}
    public float X{get;set;}=.5f;public float Y{get;set;}=.5f;public float Height{get;set;}=.95f;public float Rotation{get;set;}public float Opacity{get;set;}=1;public float Brightness{get;set;}=1;public float Speed{get;set;}=1;
    public float[] Tint{get;set;}=[1,1,1,1];public bool Loop{get;set;}=true;public bool Blink{get;set;}=true;public bool Breath{get;set;}=true;public bool Physics{get;set;}=true;public Dictionary<string,float> Parameters{get;set;}=[];
}
public sealed class TrackSpec {public string Target{get;set;}="";public string Property{get;set;}="";public string Interpolation{get;set;}="linear";public List<KeySpec> Keys{get;set;}=[];}
public sealed class KeySpec {public double T{get;set;}public float Value{get;set;}}
public sealed class EventSpec {public double T{get;set;}public string Actor{get;set;}="";public string? Motion{get;set;}public string? Expression{get;set;}public float Fade{get;set;}=.3f;}
