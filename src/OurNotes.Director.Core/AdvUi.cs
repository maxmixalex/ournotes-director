// Native presentation of exported ADV UI assets and Talk/Voice timing.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OurNotes.Director.Core;

public sealed partial class AdvUi : IDisposable
{
    readonly Archive archive;readonly string site,language;readonly JsonNode manifest,episode,settings,doc,fonts;
    readonly OffscreenGl gl;readonly AdvLoop loop;readonly AdvAudio audio;
    readonly Dictionary<string,JsonNode> nodes;
    readonly Dictionary<string,(uint Id,int Width,int Height)> textures=[];
    readonly List<int> voices=[];
    readonly float canvasWidth,canvasHeight;readonly int renderScale;
    string window="UIDefaultTalkWindow",text="",speaker="";bool talkVisible,typing;
    int visible,voiceScope,transitionToken;JsonNode? transition;float transitionValue=1;float[] transitionColor=[0,0,0,1];bool transitionVisible;
    public string Text=>text;public string Speaker=>speaker;public bool Typing=>typing;
    public Func<string,Actor?>? ResolveCharacter{get;set;}
    public Func<IEnumerable<Actor>>? ShowingCharacters{get;set;}
    public System.Numerics.Vector3 ShakeOffset{get;set;}
    string locationText="";float locationStart,locationSpeed=1;int locationGeneration;bool locationVisible;
    System.Numerics.Vector3 drawOffset;float drawAlpha=1;
    public bool HasLetterbox{get;set;}
    float letterboxAlpha;
    public AdvUi(Archive archive,string site,JsonNode manifest,JsonNode definition,JsonNode episode,JsonNode settings,string language,OffscreenGl gl,AdvLoop loop,AdvAudio audio,int renderScale=1)
    {
        this.archive=archive;this.site=site;this.manifest=manifest;this.episode=episode;this.settings=settings;this.language=language;this.gl=gl;this.loop=loop;this.audio=audio;
        criData=new(()=>CriLipsData.FromArchive(archive,site,manifest,definition));
        uiTweens=new(loop);this.renderScale=renderScale;
        doc=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(definition,"ui"),language))!;fonts=JsonNode.Parse(archive.Logical(site,manifest,"ui/fonts.json",language))!;
        nodes=doc["nodes"]!.AsArray().OfType<JsonNode>().GroupBy(n=>Archive.S(n,"path")).ToDictionary(g=>g.Key,g=>g.Last());
        float scale=Math.Min(gl.Width/1920f,gl.Height/1080f);canvasWidth=gl.Width/scale;canvasHeight=gl.Height/scale;
        if(definition["chat"] is not null)Chat=new(archive,site,manifest,definition,language,gl);
    }
    public IEnumerable<AdvWait> Prepare(){talkVisible=false;if(HasLetterbox){float elapsed=0;while(elapsed<.2f){yield return loop.YieldUpdate();elapsed+=Math.Min((float)loop.DeltaTime,1f/30);letterboxAlpha=Math.Clamp(elapsed/.2f,0,1);}}else yield return loop.Ready();}
    public void Bind(AdvPlayback player)
    {
        player.Register("TalkWindow",(r,p)=>Window(r));player.Register("Talk",Talk);player.Register("Voice",(r,p)=>Voice(r,p));
        player.Register("FadeIn",(r,p)=>FadeIn(r,p));player.Register("FadeOut",(r,p)=>Fade(r,p,false));
        player.Register("Location",Location);
        player.Register("Flash",Flash);
        player.Register("Subtitles",Subtitles);
        if(Chat is not null){player.Register("ChatWindow",(r,p)=>ChatWindow(r,p));player.Register("ChatTalk",ChatTalk);player.Register("ChatStamp",ChatStamp);player.Register("ChatRead",ChatRead);player.Register("ChatTyping",ChatTyping);}
    }
    IEnumerable<AdvWait> Location(AdvRow r,AdvPlayback p)
    {
        yield return loop.DelayFrames(1);if(p.Cancelled)yield break;yield return p.NoWait(r,ShowLocation(r,p));
    }
    IEnumerable<AdvWait> ShowLocation(AdvRow r,AdvPlayback p)
    {
        int generation=++locationGeneration;string id=r.Text("AdvTextID");if(id is "" or "0")id=r.Data["TargetTextIDs"]?[0]?.ToString()??"";
        locationText=Localize(id);locationStart=loop.Time;locationSpeed=p.Speed;locationVisible=true;
        var node=nodes.Values.Single(n=>Archive.S(n,"name")=="LocationVIew");float duration=0;
        foreach(var command in node["tweenSequence"]!["list"]!.AsArray()){if((int)Actor.N(command!["_commandType"])!=1)throw new NotSupportedException("Unsupported location sequence command");duration+=Math.Max(0,Actor.N(command["_duration"]));}
        do{yield return loop.YieldUpdate();}while(generation==locationGeneration&&(loop.Time-locationStart)*locationSpeed<duration&&!p.Cancelled);
    }
    IEnumerable<AdvWait> Window(AdvRow r){string requested=r.Index=="-1"?r.Text("TargetName"):r.Text("TargetAssetName");if(!nodes.Values.Any(n=>Archive.S(n,"name")==requested&&n["talkWindow"] is not null))throw new NotSupportedException("Talk window absent from UI export: "+requested);window=requested;UpdateBackdrop();yield break;}
    public string Localize(string id)
    {
        if(id=="")return "";string key=language switch{"zh-Hans"=>"zh-Hans","zh-Hant"=>"zh-Hant",_=>language};
        // nnnotes' source text table uses its language field names.
        string field=key switch{"zh-Hans"=>"simplifiedChinese","zh-Hant"=>"traditionalChinese","ja"=>"japanese","en"=>"english","ko"=>"korean",_=>throw new ArgumentException("Unsupported language: "+key)};
        var value=episode["text"]?[id];return value?[field]?.ToString()??value?[key]?.ToString()??throw new InvalidDataException("Missing localized text: "+id+" / "+language);
    }
    static string Plain(string value)
    {
        value=Regex.Replace(value,"<ruby\\s*=\\s*([^\"'>]+)>([^\\n]*?)</ruby>|<r\\s*=\\s*([^\"'>]+)>([^\\n]*?)</r>",m=>m.Groups[1].Success?m.Groups[1].Value+m.Groups[2].Value:m.Groups[3].Value+m.Groups[4].Value);
        return Regex.Replace(value.Replace("<br>","\n",StringComparison.OrdinalIgnoreCase),"<[^>]*>","");
    }
    public static int TextLength(string text)=>Plain(text).EnumerateRunes().Count();
    IEnumerable<AdvWait> Type(string line,float speed)
    {
        typing=true;visible=0;float delay=(language is "en" or "ko"?.015f:Actor.N(nodes.Values.Single(n=>Archive.S(n,"name")==window)["talkWindow"]?["_typingDelay"],.05f))/speed;
        foreach(var rune in Plain(line).EnumerateRunes()){
            visible++;yield return loop.YieldUpdate();if(rune.Value is >=65 and <=90 or >=97 and <=122)continue;
            yield return loop.Delay(MathF.Round(delay*1000)/1000);
        }
        typing=false;
    }
    void StopVoices(){foreach(int id in voices)audio.Stop(id);voices.Clear();}
    int[] PlayVoices(AdvRow r,bool lipSync=false){r=ResolveExternalVoice(r);StopVoices();string split=Archive.S(settings,"_targetNameSplitKey");var names=r.Text("TargetName").Split(split==""?",":split,StringSplitOptions.RemoveEmptyEntries);int index=0;foreach(var id in r.Data["VoiceIDs"]?.AsArray()??[]){var sound=audio.Play(id!.GetValue<long>(),crossFade:.3f);voices.Add(sound.Id);if(lipSync&&!r.Flag("IgnoreLipSync")&&index<names.Length&&ResolveCharacter?.Invoke(names[index]) is Actor actor)AttachVoiceLipSync(actor,sound);index++;}return voices.ToArray();}
    public static string SpeakerName(AdvRow row,JsonNode? masterIds,Func<string,string> localize)
    {
        if(row.Text("TargetName")==""||row.Number("TargetStatus")==2)return "";
        if(row.Number("TargetStatus")==1)return localize(Archive.S(masterIds??new JsonObject(),"_unknownCharacterNameTextId"));
        var ids=row.Data["TargetTextIDs"]?.AsArray()??new JsonArray();var colors=row.Data["TargetTextColors"] as JsonArray;
        string separator=ids.Count<2?"":localize(Archive.S(masterIds??new JsonObject(),"_splitCharacterNameTextId"));
        return string.Join(separator,ids.Select((id,i)=>{string name=localize(id!.ToString());return colors is not null&&i<colors.Count?$"<color={colors[i]}>{name}</color>":name;}));
    }
    IEnumerable<AdvWait> Talk(AdvRow r,AdvPlayback p)
    {
        voiceScope++;string id=r.Text("AdvTextID");if(id is "" or "0"){talkVisible=false;UpdateBackdrop();yield break;}
        if((r.Data["VoiceIDs"]?.AsArray().Count??0)>0&&audio.LastBgmOrSeFrame==loop.Frame)yield return loop.DelayFrames(2);
        text=Localize(id);speaker=SpeakerName(r,MasterIds,Localize);talkVisible=true;
        UpdateBackdrop();
        var type=new AdvJob(loop,Type(text,p.Speed));var started=PlayTalkVoices(r,p);
        do{yield return loop.YieldUpdate();}while(typing&&!p.Cancelled);type.ThrowIfFailed();float start=loop.Time;
        if(started.Length>0){foreach(int sound in started){do{yield return loop.YieldUpdate();}while(audio.IsPlaying(sound)&&!p.Cancelled);}foreach(var w in p.DelayAdjusted(p.CalcDuration(Actor.N(settings["_waitAfterVoiceTime"]))))yield return w;}
        else foreach(var w in p.DelayAdjusted(p.CalcDuration(Actor.N(settings["_waitTalkTextUnitTime"])*TextLength(text))))yield return w;
        float remaining=p.CalcDuration(Actor.N(settings["_minTalkDisplayTime"]))-(loop.Time-start);foreach(var w in p.DelayAdjusted(remaining))yield return w;
        yield return loop.YieldUpdate();yield return loop.YieldUpdate();
    }
    IEnumerable<AdvWait> Voice(AdvRow r,AdvPlayback p){yield return p.NoWait(r,VoiceInner(r,p));}
    IEnumerable<AdvWait> VoiceInner(AdvRow r,AdvPlayback p)
    {
        int scope=++voiceScope;if(audio.LastBgmOrSeFrame==loop.Frame)yield return loop.DelayFrames(2);if(scope!=voiceScope)yield break;
        var started=PlayVoices(r,true);if(started.Length==0)yield break;yield return loop.YieldUpdate();
        foreach(int id in started){do{yield return loop.YieldUpdate();}while(audio.IsPlaying(id)&&!p.Cancelled&&scope==voiceScope);}
        if(scope!=voiceScope)yield break;foreach(var w in p.DelayAdjusted(p.CalcDuration(Actor.N(settings["_waitAfterVoiceTime"]))))yield return w;if(scope==voiceScope)StopVoices();
    }
    IEnumerable<AdvWait> FadeIn(AdvRow r,AdvPlayback p){yield return p.NoWait(r,Fade(r,p,true));}
    IEnumerable<AdvWait> Fade(AdvRow r,AdvPlayback p,bool incoming)
    {
        string address=r.Text("TargetAssetName");if(address=="")address=Archive.S(settings,"_defaultTransitionAssetAddress");transition=doc["transitions"]?[address]??throw new InvalidDataException("Missing rule transition: "+address);
        transitionColor=Color(r.Text("Parameter1"));transitionVisible=true;int token=++transitionToken;float duration=p.CalcDuration(r.Number("Duration"));float t=0;
        while(t<duration){transitionValue=(incoming?-1:1)+(incoming?2:-2)*Curve(transition["_easingCurve"]!["m_Curve"]!.AsArray(),t/duration);t+=(float)(loop.DeltaTime>0?loop.DeltaTime:1d/30);yield return loop.YieldUpdate();if(token!=transitionToken)yield break;}
        transitionValue=incoming?1:-1;if(incoming)transitionVisible=false;
    }
    static float Curve(JsonArray keys,float t)
    {
        if(t<=Actor.N(keys[0]!["time"]))return Actor.N(keys[0]!["value"]);for(int i=1;i<keys.Count;i++){var a=keys[i-1]!;var b=keys[i]!;if(t>Actor.N(b["time"]))continue;float d=Actor.N(b["time"])-Actor.N(a["time"]),u=(t-Actor.N(a["time"]))/d,u2=u*u,u3=u2*u;return (2*u3-3*u2+1)*Actor.N(a["value"])+(u3-2*u2+u)*d*Actor.N(a["outSlope"])+(-2*u3+3*u2)*Actor.N(b["value"])+(u3-u2)*d*Actor.N(b["inSlope"]);}return Actor.N(keys[^1]!["value"]);
    }
    static float[] Color(string s)
    {
        s=s.Trim();if(s=="")return [0,0,0,1];if(s.Equals("white",StringComparison.OrdinalIgnoreCase))return [1,1,1,1];if(s.Equals("black",StringComparison.OrdinalIgnoreCase))return [0,0,0,1];s=s.TrimStart('#');if(s.Length is not (6 or 8))throw new ArgumentException("Invalid HTML color");var bytes=Convert.FromHexString(s);return [bytes[0]/255f,bytes[1]/255f,bytes[2]/255f,bytes.Length==4?bytes[3]/255f:1];
    }
    (uint Id,int Width,int Height) Texture(string key,bool font=false)
    {
        string cache=(font?"font:":"ui:")+key;if(textures.TryGetValue(cache,out var value))return value;
        var info=(font?fonts:doc)["textures"]![key]!;var pixels=Png.Decode(archive.Logical(site,manifest,"ui/"+Archive.S(info,"texture"),language));value=(gl.Upload(pixels),pixels.Width,pixels.Height);textures[cache]=value;return value;
    }
    readonly record struct Rect(float X,float Y,float W,float H);
    Rect Bounds(string path)
    {
        var n=nodes[path];if(n["canvas"] is not null||!path.Contains('/'))return new(0,0,canvasWidth,canvasHeight);
        var parent=Bounds(path[..path.LastIndexOf('/')]);var r=n["rect"];if(r is null)return parent;
        float x0=Actor.N(r["m_AnchorMin"]?["x"]),y0=Actor.N(r["m_AnchorMin"]?["y"]),x1=Actor.N(r["m_AnchorMax"]?["x"]),y1=Actor.N(r["m_AnchorMax"]?["y"]),px=Actor.N(r["m_Pivot"]?["x"],.5f),py=Actor.N(r["m_Pivot"]?["y"],.5f);
        float w=parent.W*(x1-x0)+Actor.N(r["m_SizeDelta"]?["x"]),h=parent.H*(y1-y0)+Actor.N(r["m_SizeDelta"]?["y"]);
        return new(parent.X+parent.W*(x0+(x1-x0)*px)+Actor.N(r["m_AnchoredPosition"]?["x"])-w*px,parent.Y+parent.H*(y0+(y1-y0)*py)+Actor.N(r["m_AnchoredPosition"]?["y"])-h*py,w,h);
    }
    float[] Quad(Rect r){r=r with{X=r.X+drawOffset.X,Y=r.Y+drawOffset.Y};return[2*r.X/canvasWidth-1,2*r.Y/canvasHeight-1,2*(r.X+r.W)/canvasWidth-1,2*r.Y/canvasHeight-1,2*(r.X+r.W)/canvasWidth-1,2*(r.Y+r.H)/canvasHeight-1,2*r.X/canvasWidth-1,2*(r.Y+r.H)/canvasHeight-1];}
    void Image(JsonNode n)
    {
        var image=n["image"]!;if(!Actor.B(image["m_Enabled"]))return;string name=Archive.S(image,"sprite");if(name=="")return;var sprite=doc["sprites"]?[name]??throw new InvalidDataException("Missing UI sprite: "+name);var tex=Texture(Archive.S(sprite,"texture"));var r=sprite["textureRect"]!;
        float x=Actor.N(r["x"])/tex.Width,y=Actor.N(r["y"])/tex.Height,w=Actor.N(r["width"])/tex.Width,h=Actor.N(r["height"])/tex.Height;var c=image["m_Color"]!;
        float[] color=[Actor.N(c["r"],1),Actor.N(c["g"],1),Actor.N(c["b"],1),Actor.N(c["a"],1)*drawAlpha];var bounds=Bounds(Archive.S(n,"path"));
        if(n["gradient"] is JsonNode gradient&&Actor.B(gradient["enabled"])&&Actor.N(gradient["angleDeg"])==0){
            var data=gradient["gradient"]!;var stops=new SortedSet<float>{0,1};foreach(string type in new[]{"ctime","atime"})for(int i=0;i<(int)Actor.N(data[type=="ctime"?"m_NumColorKeys":"m_NumAlphaKeys"]);i++)stops.Add(Actor.N(data[type+i])/65535);
            var values=stops.ToArray();for(int i=1;i<values.Length;i++){float a=values[i-1],b=values[i];var ca=AdvAnimation.Gradient(data,a);var cb=AdvAnimation.Gradient(data,b);gl.Draw(tex.Id,Quad(new(bounds.X+bounds.W*a,bounds.Y,bounds.W*(b-a),bounds.H)),[x+w*a,1-y,x+w*b,1-y,x+w*b,1-y-h,x+w*a,1-y-h],[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],color,0,false,false,colors:[..ca,..cb,..cb,..ca]);}
        }else gl.Draw(tex.Id,Quad(bounds),[x,1-y,x+w,1-y,x+w,1-y-h,x,1-y-h],[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],color,0,false,false);
    }
    public void Draw(bool clean)
    {
        drawOffset=System.Numerics.Vector3.Zero;drawAlpha=1;if(!clean)DrawBackdrop();DrawFlash();
        if(transitionVisible&&transition is not null){var texture=transition["_texture"]?["texture"]?.ToString();if(texture is null)throw new NotSupportedException("Transition without rule texture");gl.Rule(Texture(texture).Id,transitionValue,Actor.B(transition["_gradient"]),transitionColor);}
        DrawLocation(clean);
        drawOffset=ShakeOffset;drawAlpha=1;
        if(!clean&&talkVisible){string prefix=nodes.Values.Single(n=>Archive.S(n,"name")==window&&n["talkWindow"] is not null)["path"]!.ToString()+"/";
            foreach(var pair in nodes.Where(p=>p.Key.StartsWith(prefix,StringComparison.Ordinal))){var n=pair.Value;if(n["image"] is not null&&Actor.B(n["active"])&&(pair.Key.EndsWith("TalkBackground",StringComparison.Ordinal)||speaker!=""&&pair.Key.Contains("/Speaker/Line",StringComparison.Ordinal)))Image(n);
                if(n["textStyle"] is not null){if(Archive.S(n,"name")=="TalkText")DrawText(n,text,visible);else if(Archive.S(n,"name")=="SpeakerText")DrawText(n,speaker,int.MaxValue);}}
        }
        drawOffset=System.Numerics.Vector3.Zero;drawAlpha=1;
        if(!clean&&subtitles.Length>0)DrawText(nodes.Values.Single(n=>Archive.S(n,"name")=="SubtitlesText"),subtitles,int.MaxValue);
    }
    void DrawLocation(bool clean)
    {
        drawOffset=System.Numerics.Vector3.Zero;
        if(!clean&&locationVisible){var clip=doc["clips"]!["AdvLocation/Play"]!;var curves=clip["curves"]!;float t=Math.Min((loop.Time-locationStart)*locationSpeed,Actor.N(clip["stopTime"]));drawAlpha=AdvAnimation.Evaluate(curves["CanvasGroup.m_Alpha"],t,1);drawOffset=new(AdvAnimation.Evaluate(curves["RectTransform.m_AnchoredPosition.x"],t),AdvAnimation.Evaluate(curves["RectTransform.m_AnchoredPosition.y"],t),0);
            if(drawAlpha>0)foreach(var n in nodes.Values.Where(n=>Archive.S(n,"path").Contains("/LocationVIew/",StringComparison.Ordinal))){if(n["image"] is not null)Image(n);if(n["textStyle"] is not null)DrawText(n,locationText,int.MaxValue);}}
        drawOffset=System.Numerics.Vector3.Zero;drawAlpha=1;
    }
    public void DrawLetterbox(int width,int height,int viewportHeight)
    {
        float band=(height-viewportHeight)*.5f;if(band<=1)return;
        var sprite=doc["sprites"]![Archive.S(doc,"letterBoxSprite")]!;var tex=Texture(Archive.S(sprite,"texture"));var r=sprite["textureRect"]!;
        float drawHeight=Math.Max(band,Actor.N(sprite["rect"]!["height"])/Actor.N(sprite["rect"]!["width"])*width);
        float x=Actor.N(r["x"])/tex.Width,y=Actor.N(r["y"])/tex.Height,w=Actor.N(r["width"])/tex.Width,h=Actor.N(r["height"])/tex.Height;
        foreach(float bottom in new[]{height-band,band-drawHeight}){
            float low=2*bottom/height-1,high=2*(bottom+drawHeight)/height-1;
            gl.Draw(tex.Id,[-1,low,1,low,1,high,-1,high],[x,1-y,x+w,1-y,x+w,1-y-h,x,1-y-h],[0,1,2,0,2,3],letterboxAlpha,[1,1,1,1],[0,0,0,0],[1,1,1,1],0,false,false);
        }
    }
}
