using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Editable actors and time-positioned clips. Native rows are an output of this
/// document, not the user's primary editing model. Original performances retain their rows.</summary>
public sealed class CreativeScene
{
    public const string Format = "ournotes-director/scene";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public JsonObject Root { get; private set; }
    public JsonArray Actors => Root["actors"]!.AsArray();
    public JsonArray Clips => Root["clips"]!.AsArray();
    public JsonObject Source => Root["source"]!.AsObject();
    public JsonObject Canvas => Root["canvas"]!.AsObject();
    public string Site => Text(Source, "site", "intl");
    public string Language => Text(Source, "language", "zh-Hans");
    public double Duration => Math.Max(1d / 30, Clips.OfType<JsonObject>().Where(Enabled)
        .Select(c => Number(c, "start") + Number(c, "duration", 1d / 30)).DefaultIfEmpty(3).Max());
    public CreativeScene(JsonObject root)
    {
        Root = (JsonObject)root.DeepClone();
        // Migrate the unreleased draft's hidden floor to an ordinary, removable hold clip.
        if (Root.Remove("minimumDuration", out var old) && old is not null && Root["clips"] is JsonArray clips)
        {
            double end = double.Parse(old.ToString(), CultureInfo.InvariantCulture);
            double contentEnd = clips.OfType<JsonObject>().Select(c => Number(c,"start") + Number(c,"duration",1d/30)).DefaultIfEmpty(0).Max();
            if (double.IsFinite(end) && end > contentEnd)
                clips.Add((JsonNode)NewClip("", "hold", contentEnd, end-contentEnd, new JsonObject { ["cmd"]="Delay", ["Frames"]=0 }, "结束停留"));
        }
        Validate();
    }
    public static CreativeScene Blank(string site, string language) => new(new JsonObject
    {
        ["format"] = Format, ["version"] = 1,
        ["source"] = new JsonObject { ["site"] = site, ["language"] = language },
        ["canvas"] = new JsonObject { ["width"] = 1920, ["height"] = 1080, ["transparent"] = true },
        ["actors"] = new JsonArray(), ["clips"] = new JsonArray()
    });
    public static string UnwrapJson(string text)
    {
        text = text.Trim();
        if (!text.StartsWith("```",StringComparison.Ordinal)) return text;
        int first=text.IndexOf('\n'), last=text.LastIndexOf("```",StringComparison.Ordinal);
        if(first<0 || last<=first || text[(last+3)..].Trim().Length!=0) throw new ArgumentException("请粘贴一个完整的 JSON 代码块。");
        return text[(first+1)..last].Trim();
    }
    public static CreativeScene Parse(string text) => new(JsonNode.Parse(UnwrapJson(text)) as JsonObject ?? throw new ArgumentException("场景必须是 JSON 对象。"));
    public string ToJson() => Root.ToJsonString(Json);
    public static string Id() => Guid.NewGuid().ToString("N");
    public static string Text(JsonObject value,string key,string fallback="") => value[key]?.ToString() ?? fallback;
    public static double Number(JsonObject value,string key,double fallback=0) => value[key] is null ? fallback :
        double.TryParse(value[key]!.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out double n)&&double.IsFinite(n) ? n : throw new ArgumentException(key+" 必须是有限数值。");
    static bool Enabled(JsonObject clip) => clip["enabled"]?.GetValue<bool>() != false;
    public JsonObject Actor(string id) => Actors.OfType<JsonObject>().FirstOrDefault(a=>Text(a,"id")==id) ?? throw new ArgumentException("角色不存在。");
    public JsonObject Clip(string id) => Clips.OfType<JsonObject>().FirstOrDefault(c=>Text(c,"id")==id) ?? throw new ArgumentException("片段不存在。");
    public string AddActor(string model,string name)
    {
        if(string.IsNullOrWhiteSpace(model))throw new ArgumentException("请选择角色模型。");
        string id="actor_"+Id()[..8];
        Actors.Add((JsonNode)new JsonObject { ["id"]=id,["model"]=model,["name"]=name,["x"]=.5,["y"]=.5,["height"]=.95,["opacity"]=1 });
        if(Root["staging"] is not null)
        {
            var used=Actors.OfType<JsonObject>().Where(a=>a["stageSlot"] is not null).Select(a=>(int)Number(a,"stageSlot")).ToHashSet();
            int slot=new[]{3,7,5,1,9}.FirstOrDefault(p=>!used.Contains(p));
            if(slot==0){Actors.Remove(Actor(id));throw new InvalidOperationException("原作舞台的五个站位已满。");}
            Actor(id)["stageSlot"]=slot;
        }
        return id;
    }
    static JsonObject NewClip(string actor,string kind,double start,double duration,JsonObject data,string label) => new()
    {
        ["id"]=Id(),["actor"]=actor,["kind"]=kind,["start"]=Math.Round(start*30)/30,
        ["duration"]=Math.Max(1d/30,Math.Round(duration*30)/30),["label"]=label,["data"]=data.DeepClone()
    };
    public string AddClip(string actor,string kind,double start,double duration,JsonObject data,string label)
    {
        if(!double.IsFinite(start+duration) || start<0 || duration<=0)throw new ArgumentException("片段时间无效。");
        var clip=NewClip(actor,kind,start,duration,data,label); Clips.Add((JsonNode)clip);
        try { Validate(); } catch { Clips.Remove(clip); throw; }
        return Text(clip,"id");
    }
    public void MoveClip(string id,double seconds)
    {
        if(!double.IsFinite(seconds)||seconds<0)throw new ArgumentException("开始时间无效。");
        var clip=Clip(id); var old=clip["start"]?.DeepClone(); clip["start"]=Math.Round(seconds*30)/30;
        try { Validate(); } catch { clip["start"]=old; throw; }
    }
    public bool CanResize(JsonObject clip) => Text(clip,"kind") is "hold" or "focus" or "depth" ||
        Text(clip["data"]!.AsObject(),"cmd").StartsWith("Director.",StringComparison.Ordinal) || clip["data"]?["AudioPath"] is not null ||
        Text(clip,"kind")=="motion" && clip["sourcePerformance"]?.GetValue<bool>()!=true && !CreativeStaging.IsNativeActor(this,Text(clip,"actor"));
    public void ResizeClip(string id,double seconds)
    {
        if(!double.IsFinite(seconds)||seconds<=0)throw new ArgumentException("持续时间无效。");
        var clip=Clip(id); if(!CanResize(clip))throw new InvalidOperationException("这段原有演出的时长由原始指令决定；请替换素材或编辑高级参数。");
        var before=(JsonObject)clip.DeepClone(); var data=clip["data"]!.AsObject(); seconds=Math.Max(1d/30,Math.Round(seconds*30)/30);
        if(data["AudioPath"] is not null)data["AudioDuration"]=seconds;
        else if(Text(data,"cmd").StartsWith("Director.",StringComparison.Ordinal)||Text(clip,"kind") is "focus" or "depth")data["Duration"]=seconds;
        clip["duration"]=seconds;
        try { Validate(); } catch { int index=Clips.IndexOf(clip); Clips[index]=before; throw; }
    }
    public void RemoveClip(string id)=>Clips.Remove(Clip(id));
    public string DuplicateClip(string id)
    {
        var copy=(JsonObject)Clip(id).DeepClone(); copy["id"]=Id(); copy["start"]=Number(copy,"start")+Number(copy,"duration");
        Clips.Add((JsonNode)copy); try{Validate();}catch{Clips.Remove(copy);throw;} return Text(copy,"id");
    }
    public void Validate()
    {
        if(Text(Root,"format")!=Format||Number(Root,"version")!=1)throw new ArgumentException("需要 ournotes-director/scene version 1。");
        if(Root["source"] is not JsonObject||Root["canvas"] is not JsonObject||Root["actors"] is not JsonArray||Root["clips"] is not JsonArray)
            throw new ArgumentException("场景需要 source、canvas、actors 和 clips。");
        foreach(string key in new[]{"width","height"})if(Number(Canvas,key)%1!=0)throw new ArgumentException("画布宽高必须是整数。");
        RenderDimensions.Validate(checked((int)Number(Canvas,"width",1920)),checked((int)Number(Canvas,"height",1080)),1);
        _=SceneContent.Read(Canvas["content"]);
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var item in Actors)
        {
            if(item is not JsonObject a||string.IsNullOrWhiteSpace(Text(a,"id"))||!ids.Add(Text(a,"id"))||string.IsNullOrWhiteSpace(Text(a,"model")))throw new ArgumentException("角色需要唯一 id 和实际 model。");
            _=Number(a,"x",.5); _=Number(a,"y",.5);
            if(Number(a,"height",.95)<=0||Number(a,"opacity",1) is <0 or >1)throw new ArgumentException("角色尺寸或透明度无效。");
        }
        ids.Clear();
        foreach(var item in Clips)
        {
            if(item is not JsonObject c||string.IsNullOrWhiteSpace(Text(c,"id"))||!ids.Add(Text(c,"id"))||c["data"] is not JsonObject data)throw new ArgumentException("片段需要唯一 id 和 data。");
            double start=Number(c,"start"),duration=Number(c,"duration",1d/30);
            if(start<0||duration<=0||start+duration>36000)throw new ArgumentException("片段时间必须在十小时范围内。");
            string command=Text(data,"cmd");
            if(!AdvProgram.KnownCommand(command)||command is "GoTo" or "Wait" or "ForceAuto")throw new ArgumentException("交互跳转或等待点击不能直接作为时间片段。");
            if(data["AudioPath"] is not null)
            {
                double input=Number(data,"AudioIn"),length=Number(data,"AudioDuration",duration);
                if(input<0||length<=0||c["sourceDuration"] is not null&&input+length>Number(c,"sourceDuration")+1d/30)throw new ArgumentException("语音裁剪范围超出文件长度。");
            }
            DirectorValidation.Check(AdvProgram.Row(data,"clip "+Text(c,"id")));
        }
        CreativeStaging.Validate(this);
        CreativeMaterialEditing.Validate(this);
    }
    public JsonObject Compile()
    {
        Validate();
        var output=Root["nativeOutput"]?.DeepClone() as JsonObject ?? new JsonObject { ["clean"]=true,["letterbox"]="none",["codec"]="cineform",["cineformQuality"]="film1",["supersample"]=1 };
        output["width"]=(int)Number(Canvas,"width",1920);output["height"]=(int)Number(Canvas,"height",1080);
        if(Canvas["content"] is not null)output["content"]=SceneContent.Read(Canvas["content"]);
        var rows=new List<(int At,int Priority,int Order,JsonObject Row)>();int order=0;
        var expressionInherit=new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        void Add(JsonObject row,int at,int priority=0){row["AtFrame"]=at;rows.Add((at,priority,order++,row));}
        if(Root["imported"]?.GetValue<bool>()!=true&&Root["staging"] is null)
        {
            Add(new(){["cmd"]="Director.Scene",["Values"]=new JsonObject{["stageVisible"]=false,["background"]=Canvas["transparent"]?.GetValue<bool>()!=false?new JsonArray(0,0,0,0):new JsonArray(0,0,0,1)}},0);
            Add(new(){["cmd"]="Director.Camera",["Values"]=new JsonObject{["framing"]=new JsonObject{["x"]=0,["y"]=0,["zoom"]=1,["roll"]=0}}},0);
        }
        if(Root["staging"] is JsonObject staging)
            Add(new(){["cmd"]="Stage",["TargetAssetName"]=staging["stage"]!.ToString(),["Duration"]=0},0);
        foreach(var a in Actors.OfType<JsonObject>().Where(a=>a["fromSource"]?.GetValue<bool>()!=true))
        {
            string id=Text(a,"id");Add(new(){["cmd"]="Character",["TargetName"]=id,["Model"]=Text(a,"model")},0);
            if(a["stageSlot"] is not null)
            {
                int slot=(int)Number(a,"stageSlot");
                Add(new(){["cmd"]="In",["TargetName"]=id,["PositionType"]=slot,["Duration"]=0},0);
                if(Number(a,"depth")!=0)Add(new(){["cmd"]="MoveToDirection",["TargetName"]=id,["PositionType"]=slot,["Duration"]=0,["Parameter1"]=0,["Parameter2"]=0,["Parameter3"]=Number(a,"depth")},0);
                if(Text(a,"layer") is "Forward" or "Back")Add(new(){["cmd"]=Text(a,"layer"),["TargetName"]=id,["PositionType"]=slot},0);
                continue;
            }
            Add(new(){["cmd"]="Director.Actor",["TargetName"]=id,["Space"]="screen",["Values"]=new JsonObject{
                ["position"]=new JsonArray(Number(a,"x",.5),Number(a,"y",.5)),["height"]=Number(a,"height",.95),["opacity"]=Number(a,"opacity",1),
                ["visible"]=true,["animation"]="director",["physics"]=true,["blink"]=true,["breath"]=true,["loop"]=true}},0);
        }
        foreach(var c in Clips.OfType<JsonObject>().Where(Enabled))
        {
            var row=(JsonObject)c["data"]!.DeepClone();string actor=Text(c,"actor"),kind=Text(c,"kind");
            if(actor.Length>0)row["TargetName"]=actor;
            int start=(int)Math.Round(Number(c,"start")*30),end=(int)Math.Round((Number(c,"start")+Number(c,"duration"))*30);
            bool authoredMotion=kind=="motion"&&c["sourcePerformance"]?.GetValue<bool>()!=true&&!CreativeStaging.IsNativeActor(this,actor);
            if(authoredMotion)Add(new(){["cmd"]="Director.Actor",["TargetName"]=actor,["Values"]=new JsonObject{["speed"]=1}},start);
            foreach(var expanded in CreativeStaging.Expand(this,c))
            {
                Add(expanded,start);
                if(CreativeMaterialEditing.KeepsExpression(c)&&expanded["cmd"]?.ToString()=="Motion")expressionInherit.Add(expanded);
            }
            if(authoredMotion)
            {
                bool replaced=Clips.OfType<JsonObject>().Any(other=>!ReferenceEquals(other,c)&&Enabled(other)&&Text(other,"kind")=="motion"&&Text(other,"actor")==actor&&Number(other,"start")>=Number(c,"start")&&Number(other,"start")<end/30d);
                if(!replaced)Add(new(){["cmd"]="Director.Actor",["TargetName"]=actor,["Values"]=new JsonObject{["speed"]=0}},end,-1);
            }
        }
        Add(new(){["cmd"]="Delay",["Frames"]=0},Math.Max(1,Story.Frames(Duration)),1);
        var ordered=rows.OrderBy(r=>r.At).ThenBy(r=>r.Priority).ThenBy(r=>r.Order).Select(r=>r.Row).ToArray();
        CreativeMaterialEditing.ApplyExpressionPolicy(ordered,expressionInherit);
        return new JsonObject { ["format"]=DirectorProject.Format,["version"]=1,["source"]=Source.DeepClone(),["output"]=output,
            ["initialize"]=new JsonArray(),["finalize"]=new JsonArray(),["commands"]=new JsonArray(ordered.Select(r=>(JsonNode)r).ToArray()),
            ["text"]=Root["text"]?.DeepClone()??new JsonObject() };
    }
    public static JsonObject Materialize(Archive archive,JsonObject project)
    {
        var result=(JsonObject)project.DeepClone();var source=result["source"]!;
        var manifest=archive.Manifest(source["site"]?.ToString()??"intl","stories",DirectorProject.SourceId(source));
        string site=source["site"]?.ToString()??"intl";
        var definition=JsonNode.Parse(archive.Logical(site,manifest,"story.json"))!;
        var episode=JsonNode.Parse(archive.Logical(site,manifest,definition["episode"]!.ToString()))!;
        var settings=JsonNode.Parse(archive.Logical(site,manifest,definition["scene"]!.ToString()))!["settings"]!["playerSettings"]!;
        result["initialize"]??=settings["_initializeEpisodes"]?.DeepClone()??new JsonArray();result["commands"]??=episode["commands"]!.DeepClone();result["finalize"]??=settings["_finalizeEpisodes"]?.DeepClone()??new JsonArray();
        var text=episode["text"]?.DeepClone() as JsonObject??new JsonObject();
        if(result["text"] is JsonObject extra)foreach(var(key,value)in extra)text[key]=value?.DeepClone();result["text"]=text;return result;
    }
    public static CreativeScene FromPerformance(JsonObject native,NativePlaybackTiming timing,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(!timing.IsComplete||timing.EndFrame is null)throw new InvalidOperationException("原剧情尚未解析完成，不能猜测语音时间。");
        var scene=Blank(native["source"]?["site"]?.ToString()??"intl",native["source"]?["language"]?.ToString()??"zh-Hans");
        scene.Root["source"]=native["source"]!.DeepClone();scene.Root["nativeOutput"]=native["output"]?.DeepClone();scene.Root["imported"]=true;scene.Canvas["content"]=SceneContent.Read(native["output"]?["content"]);
        scene.Root["originalProject"]=native.DeepClone();scene.Root["text"]=native["text"]?.DeepClone();
        scene.Canvas["width"]=native["output"]?["width"]?.DeepClone()??JsonValue.Create(1920);scene.Canvas["height"]=native["output"]?["height"]?.DeepClone()??JsonValue.Create(1080);
        var entries=timing.Commands.OrderBy(t=>t.Ordinal).ToArray();
        // Compute successors once. Import must not scan the remaining performance,
        // validate every growing prefix, or look up every newly appended clip.
        var ends = new int[entries.Length];
        var nextByTarget = new Dictionary<(string Actor, string Kind), int>();
        for (int index = entries.Length - 1; index >= 0;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int first = index;
            while (first > 0 && entries[first - 1].Ordinal == entries[index].Ordinal) first--;
            for (int i = first; i <= index; i++)
            {
                var entry = entries[i]; ends[i] = entry.EndFrame ?? entry.StartFrame + 1;
                if (Kind(entry.Command) is not ("motion" or "expression")
                    || native[entry.Section] is not JsonArray rows || entry.Index < 0 || entry.Index >= rows.Count
                    || rows[entry.Index] is not JsonObject row) continue;
                ends[i] = nextByTarget.GetValueOrDefault((Text(row, "TargetName"), Kind(entry.Command)), timing.EndFrame.Value);
            }
            // Equal ordinals cannot be each other's successor. Reverse assignment
            // retains the first stable entry for a later ordinal, as before.
            for (int i = index; i >= first; i--)
            {
                var entry = entries[i];
                if (Kind(entry.Command) is ("motion" or "expression")
                    && native[entry.Section] is JsonArray rows && entry.Index >= 0 && entry.Index < rows.Count
                    && rows[entry.Index] is JsonObject row)
                    nextByTarget[(Text(row, "TargetName"), Kind(entry.Command))] = entry.StartFrame;
            }
            index = first - 1;
        }
        var actors = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = entries[index];
            if(native[t.Section] is not JsonArray section||t.Index<0||t.Index>=section.Count||section[t.Index] is not JsonObject data)continue;
            if(t.Command is "Delay" or "Wait" or "GoTo" or "ForceAuto" or "CancelDelay")continue;
            var row=(JsonObject)data.DeepClone();row["cmd"]=t.Command;row.Remove("AtFrame");
            string actor=Text(row,"TargetName"),kind=Kind(t.Command);
            if(t.Command=="Character"&&actor.Length>0&&Text(row,"Model").Length>0&&actors.Add(actor))
                scene.Actors.Add((JsonNode)new JsonObject{["id"]=actor,["name"]=actor,["model"]=Text(row,"Model"),["fromSource"]=true});
            int end=ends[index];
            string label=t.Command is "Talk" or "Voice"?DialogueLabel(native,row,scene.Language):Text(row,"MotionName",Text(row,"ExpressionName",t.Command));
            var clip=NewClip(actor,kind,t.StartFrame/30d,Math.Max(1,end-t.StartFrame)/30d,row,label);
            clip["sourcePerformance"]=true; scene.Clips.Add((JsonNode)clip);
        }
        double tail=(timing.EndFrame.Value+1)/30d, contentEnd=scene.Clips.OfType<JsonObject>().Select(c=>Number(c,"start")+Number(c,"duration")).DefaultIfEmpty(0).Max();
        if(tail>contentEnd+0.0001)scene.Clips.Add((JsonNode)NewClip("","hold",contentEnd,tail-contentEnd,new JsonObject{["cmd"]="Delay",["Frames"]=0},"结束停留"));
        cancellationToken.ThrowIfCancellationRequested();
        scene.Validate();return scene;
    }
    static string DialogueLabel(JsonObject root,JsonObject row,string language)
    {
        string field=language switch{"ja"=>"japanese","en"=>"english","zh-Hant"=>"traditionalChinese",_=>"simplifiedChinese"};
        return root["text"]?[Text(row,"AdvTextID")]?[field]?.ToString()??"语音";
    }
    public static string Kind(string command)=>command switch {"Talk" or "Voice"=>"voice","Motion" or "MotionLoop"=>"motion","Expression"=>"expression", "Director.Actor" or "In" or "Out"=>"transform","Stage"=>"stage","DoF" or "Focus"=>"focus","MoveToDirection" or "MoveToForward" or "MoveToBack"=>"depth","Director.Camera" or "Pan" or "Zoom"=>"camera","Director.Scene" or "PostEffect"=>"grade","Character"=>"setup",_=>"source"};
}
