using System.Globalization;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed class AdvFrames : IDisposable
{
    sealed class Frame
    {
        public readonly Dictionary<string,AdvCanvasNode> Nodes=[];
        public readonly List<AdvCanvasAnimator> Animators=[];
        public AdvCanvasNode Root=null!,Group=null!;public AdvAnimator? Main;
        public AdvTween? Fade;public bool Active;
        public AdvCanvasParticles? Particles;
        public AdvFrameTexts? Text;public AdvCanvasLayout? Layout;public AdvFrameLayout Modifiers=null!;public int LastLayoutFrame=-1;
    }
    readonly Archive archive;readonly string site;readonly JsonNode manifest,document,ui;
    readonly AdvLoop loop;readonly AdvTweens tweens;readonly OffscreenGl gl;readonly UnityShaderLibrary? shaders;readonly AdvAnimRecords records;readonly bool timingOnly;
    readonly Dictionary<string,Frame> frames=[];readonly List<Frame> ordered=[];readonly Dictionary<string,uint> textures=[];
    readonly AdvCanvasImages? images;readonly float width,height;
    AdvCanvasTexts? textRenderer;readonly string language;
    public Func<string,string>? Localize{get;set;}
    readonly AdvRandom particleRandom=new(0x55495053);
    public int VisibleCount=>ordered.Count(f=>f.Active);
    public object Snapshot()=>JsonOutput.Object(("canvas", JsonOutput.Object(("width", width), ("height", height))), ("items", frames.Select(p=>JsonOutput.Object(("name", p.Key), ("active", p.Value.Active), ("alpha", p.Value.Group.Alpha), ("animationTime", p.Value.Main?.NormalizedTime), ("particles", p.Value.Particles?.Snapshot()), ("texts", p.Value.Text?.Texts.ToDictionary(t=>t.Key.Path,t=>t.Value.Text))))));
    public AdvFrames(Archive archive,string site,JsonNode manifest,JsonNode definition,string language,AdvLoop loop,AdvTweens tweens,OffscreenGl gl,int renderScale=1,bool timingOnly=false)
    {
        this.archive=archive;this.site=site;this.manifest=manifest;this.loop=loop;this.tweens=tweens;this.gl=gl;this.language=language;this.timingOnly=timingOnly;
        // Timing analysis needs the native animator state and waits, not fonts,
        // particle shaders or GPU image upload infrastructure. The real preview
        // builds those resources once after timing has completed.
        shaders=timingOnly?null:new(archive,site,manifest);images=timingOnly?null:new(archive,site,manifest,gl);
        string file=Archive.S(definition,"frames");document=file==""?new JsonObject{["frames"]=new JsonObject()}:JsonNode.Parse(archive.Logical(site,manifest,file))!;records=new(document);
        ui=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(definition,"ui"),language))!;
        var scaler=ui["nodes"]!.AsArray().First(n=>Archive.S(n!,"path")=="UIAdvWidget/FrameCanvas")!["canvasScaler"]!;
        float rw=Actor.N(scaler["m_ReferenceResolution"]?["x"],1920),rh=Actor.N(scaler["m_ReferenceResolution"]?["y"],1080),match=Actor.N(scaler["m_MatchWidthOrHeight"]),threshold=Actor.N(scaler["_maxAspectThreshold"],float.PositiveInfinity);
        float scale=(int)Actor.N(scaler["m_ScreenMatchMode"]) switch{1=>Math.Min(gl.Width/rw,gl.Height/rh),2=>Math.Max(gl.Width/rw,gl.Height/rh),_=>MathF.Pow(2,MathF.Log2(gl.Width/rw)*(1-match)+MathF.Log2(gl.Height/rh)*match)};
        if(gl.Width/(float)gl.Height>threshold)scale=renderScale*threshold/(gl.Width/(float)gl.Height);width=gl.Width/scale;height=gl.Height/scale;
        loop.On(AdvPhase.Animation,()=>{foreach(var f in frames.Values.Where(f=>f.Active))foreach(var a in f.Animators)a.Step((float)loop.DeltaTime);});
    }
    public void Preload(IEnumerable<AdvRow> rows)
    {
        foreach(string name in rows.Where(r=>!r.Ignored&&r.Command=="Frame").Select(r=>r.Text("TargetAssetName")).Where(n=>!string.IsNullOrWhiteSpace(n)).Distinct()){
            var doc=document["frames"]?[name]??throw new InvalidDataException("Frame not present in episode: "+name);var frame=new Frame();
            foreach(var record in doc["nodes"]!.AsArray().OfType<JsonNode>()){string path=Archive.S(record,"path");int cut=path.LastIndexOf('/');frame.Nodes[path]=new(record,cut<0?null:frame.Nodes[path[..cut]]);}
            frame.Root=frame.Nodes.Values.First();var adv=frame.Root.Component("AdvFrame")??throw new InvalidDataException("Frame has no AdvFrame component");frame.Group=frame.Nodes[Archive.S(adv["_canvasGroup"]!,"gameObject")];
            frame.Modifiers=new(frame.Nodes.Values);
            if(!timingOnly&&frame.Nodes.Values.Any(n=>n.Component("TextMeshProUGUI") is not null||n.Component("RubyTextMeshProUGUI") is not null||n.Component("RubyEmojiTextMeshProUGUI") is not null)){
                textRenderer??=new(archive,site,manifest,language,gl);frame.Text=new(frame.Nodes,textRenderer.Fonts,textRenderer.Fonts["frameTexts"]?[name],language);
                frame.Layout=new(frame.Nodes.Values,n=>frame.Text.Texts.TryGetValue(n,out var text)&&text.Enabled?text.PreferredSize(n.Width):null);
            }
            if(!timingOnly&&frame.Nodes.Values.Any(n=>n.Component("ParticleSystem") is not null))
                frame.Particles=new(frame.Nodes,particleRandom,width,height,Actor.N(ui["uiCamera"]?["camera"]?["orthographic size"],5),gl,shaders!,Texture,cacheStepCurves:true);
            foreach(var node in frame.Nodes.Values){var a=node.Component("Animator");if(a?["m_Controller"] is null)continue;
                var animator=new AdvCanvasAnimator(node,a,records,b=>frame.Nodes.TryGetValue(node.Path+(b.Path==""?"":"/"+b.Path),out var target)?target.Bind(b):null);frame.Animators.Add(animator);
                if(adv["_animator"]?["gameObject"]?.ToString()==node.Path){frame.Main=animator.Animator;animator.KeepStateOnDisable=true;}
            }
            SetActive(frame,false);frames[name]=frame;ordered.Add(frame);
        }
    }
    public void Bind(AdvPlayback player)=>player.Register("Frame",(r,p)=>Execute(r,p));
    static void SetActive(Frame frame,bool active){bool changed=frame.Active!=active;frame.Active=active;frame.Root.Active=active;if(changed&&active)foreach(var animator in frame.Animators)if(animator.Node.ActiveInHierarchy)animator.OnEnable();frame.Modifiers.SetActive(active);}
    IEnumerable<AdvWait> Execute(AdvRow r,AdvPlayback p){yield return p.NoWait(r,ExecuteInner(r,p));}
    IEnumerable<AdvWait> WaitAnimation(Frame frame,AdvPlayback p)
    {
        if(frame.Main is null)yield break;yield return loop.YieldUpdate();while(!p.Cancelled&&frame.Main.NormalizedTime<1)yield return loop.YieldUpdate();
    }
    IEnumerable<AdvWait> ExecuteInner(AdvRow r,AdvPlayback p)
    {
        if(!frames.TryGetValue(r.Text("TargetAssetName"),out var frame))throw new InvalidDataException("Frame not preloaded: "+r.Text("TargetAssetName"));
        ordered.Remove(frame);ordered.Add(frame);frame.Root.AnchoredPosition=System.Numerics.Vector2.Zero;
        if(r.Number("DelaySeconds")>0){var delay=p.CommonDelay(p.CalcDuration(r.Number("DelaySeconds")));yield return delay;if(!delay.GetResult())yield break;}
        float.TryParse(r.Text("Parameter1"),NumberStyles.Float,CultureInfo.InvariantCulture,out float fade);fade=p.CalcDuration(fade);if(fade<=0)fade=p.CalcDuration(r.Number("Duration"));
        if(frame.Main is not null)frame.Main.Speed=p.Speed;
        string state=r.Text("Parameter2");
        if(state!=""){
            frame.Text?.SetTexts(state,r.Data["TargetTextIDs"]?.AsArray().Select(n=>n?.ToString() is null or "" or "0"?"":Localize?.Invoke(n!.ToString())??throw new InvalidOperationException("Frame text localization not connected")).ToArray()??[]);
            if(frame.Main?.HasState(state)!=true)yield break;if(!frame.Active){frame.Group.Alpha=1;frame.Fade?.Kill();SetActive(frame,true);}frame.Main.Play(state);foreach(var w in WaitAnimation(frame,p))yield return w;yield break;
        }
        bool show=!frame.Active;frame.Group.Alpha=show?0:1;frame.Fade?.Kill();if(show)SetActive(frame,true);
        if(frame.Main?.HasState(show?"Show":"Hide")==true){
            if(show&&fade>0){yield return loop.Delay(MathF.Round(fade*1000)/1000);if(p.Cancelled)yield break;frame.Group.Alpha=1;}
            else if(show)frame.Group.Alpha=1;
            if(frame.Main.StateName!=(show?"Show":"Hide"))frame.Main.Play(show?"Show":"Hide");foreach(var w in WaitAnimation(frame,p))yield return w;
        }else{
            if(show&&frame.Main is not null)frame.Main.Play(frame.Main.StateName);
            if(fade>0){frame.Fade=tweens.To(()=>frame.Group.Alpha,show?1:0,fade,AdvEase.OutQuad,v=>frame.Group.Alpha=v);yield return frame.Fade.Completed;}else frame.Group.Alpha=show?1:0;
        }
        if(!show&&!p.Cancelled){frame.Group.Alpha=0;SetActive(frame,false);frame.Text?.Clear();}
    }
    uint Texture(JsonNode desc)
    {
        string path=Archive.S(desc,"texture");if(!textures.TryGetValue(path,out uint value))textures[path]=value=gl.Upload(Png.Decode(archive.Logical(site,manifest,path)));return value;
    }
    void Image(AdvCanvasNode node)=>images!.Image(node,width,height);
    public void Draw()
    {
        AdvanceRenderState();var globals=textRenderer is null?null:AdvCanvasTexts.OrthographicGlobals(width,height,gl.Width,gl.Height);foreach(var frame in ordered){if(!frame.Active)continue;
            foreach(var node in frame.Nodes.Values){if(!node.ActiveInHierarchy)continue;if(node.Image is not null)Image(node);if(frame.Text?.Texts.TryGetValue(node,out var text)==true)textRenderer!.Draw(node,text,globals!,1);if(node.Component("UIParticle") is not null)frame.Particles?.Draw(node);}}
    }
    public void AdvanceRenderState(){if(timingOnly)return;foreach(var frame in ordered){frame.Modifiers.SetActive(frame.Active);if(frame.LastLayoutFrame!=loop.Frame){frame.Modifiers.Update();frame.LastLayoutFrame=loop.Frame;}if(frame.Layout is not null)frame.Layout.Layout(width,height);else foreach(var node in frame.Nodes.Values)node.Layout(width,height);frame.Particles?.Update(frame.Active,loop.Frame,(float)loop.DeltaTime);}}
    public void Dispose(){images?.Dispose();shaders?.Dispose();textRenderer?.Dispose();}
}
