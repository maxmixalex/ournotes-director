using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// AdvStill prefabs retain their original game commands and serialized tween sequences.
public sealed class AdvStills : IDisposable
{
    sealed class Still
    {
        public readonly Dictionary<string,AdvCanvasNode> Nodes=[];
        public AdvCanvasNode Root=null!,Group=null!;
        public JsonArray Sequences=null!;
        public AdvTween? Fade;
        public AdvStillAnimations Animation=null!;
    }
    readonly AdvLoop loop;readonly AdvTweens tweens;readonly AdvCanvasImages images;
    readonly AdvRandom random=new(722);
    readonly Dictionary<string,Still> loaded=[];readonly List<Still> ordered=[];
    readonly float width,height;readonly OffscreenGl gl;readonly uint white;
    readonly float planeDistance,worldPerCanvasUnit;
    public Vector3 ShakeOffset{get;set;}
    float overlay;AdvTween? overlayFade;
    public bool Visible=>ordered.Any(s=>s.Root.Active)||overlay>0;
    public bool PostEffectSuppressed{get;private set;}
    public AdvStills(Archive archive,string site,JsonNode manifest,JsonNode definition,AdvLoop loop,AdvTweens tweens,OffscreenGl gl,IEnumerable<AdvRow> rows)
    {
        this.loop=loop;this.tweens=tweens;this.gl=gl;images=new(archive,site,manifest,gl);white=gl.Upload(new(1,1,[255,255,255,255]));
        loop.On(AdvPhase.Tweens,()=>{foreach(var s in ordered)s.Animation.Step((float)loop.DeltaTime);});
        width=1920;height=width*gl.Height/gl.Width;
        var ui=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(definition,"ui"),"zh-Hans"))!;
        var canvas=ui["nodes"]!.AsArray().First(n=>Archive.S(n!,"path")=="UIAdvWidget/StillCanvas")!;
        planeDistance=Actor.N(canvas["canvas"]?["m_PlaneDistance"]);
        float fov=Actor.N(ui["videoAndStillCamera"]?["camera"]?["field of view"]);
        worldPerCanvasUnit=2*planeDistance*(float)Math.Tan((fov*.5f)*Math.PI/180)/height;
        if(planeDistance<=0||worldPerCanvasUnit<=0)throw new InvalidDataException("Still camera plane is missing");
        var names=rows.Where(r=>r.Command=="Still").Select(r=>r.Text("TargetAssetName")).Distinct().ToArray();if(names.Length==0)return;
        var doc=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(definition,"stills")))!;
        foreach(string name in names){
            var data=doc["stills"]?[name]??throw new InvalidDataException("Still not present in episode: "+name);var s=new Still();
            foreach(var record in data["nodes"]!.AsArray().OfType<JsonNode>()){string path=Archive.S(record,"path");int cut=path.LastIndexOf('/');s.Nodes[path]=new(record,cut<0?null:s.Nodes[path[..cut]]);}
            s.Root=s.Nodes.Values.First();var adv=s.Root.Component("AdvStill")??throw new InvalidDataException("AdvStill component missing");s.Group=s.Nodes[Archive.S(adv["_canvasGroup"]!,"gameObject")];s.Sequences=adv["_doTweenSequences"]!.AsArray();
            s.Animation=new(s.Nodes,tweens,random);
            s.Root.Active=false;s.Animation.SyncActivity();s.Group.Alpha=0;
            // ApplyContainedScale is on the parent target in the source. The zero-size prefab root is equivalent here.
            s.Root.Scale*=Math.Min(width/1920,height/1080);loaded.Add(name,s);ordered.Add(s);
        }
    }
    public void Bind(AdvPlayback player)=>player.Register("Still",(r,p)=>Execute(r,p));
    public AdvWait SetAlpha(float value,float duration)
    {
        var waits=new List<AdvWait>();
        foreach(var s in ordered.Where(s=>s.Root.Active)){
            s.Fade?.Kill();
            if(duration<=0){s.Group.Alpha=value;continue;}
            s.Fade=tweens.To(()=>s.Group.Alpha,value,duration,AdvEase.OutQuad,v=>s.Group.Alpha=v);waits.Add(s.Fade.Completed);
        }
        return loop.All(waits.ToArray());
    }
    IEnumerable<AdvWait> Execute(AdvRow row,AdvPlayback player){yield return player.NoWait(row,Toggle(row,player));}
    IEnumerable<AdvWait> Toggle(AdvRow row,AdvPlayback player)
    {
        if(!loaded.TryGetValue(row.Text("TargetAssetName"),out var s))throw new InvalidDataException("Still not loaded");
        ordered.Remove(s);ordered.Add(s);bool show=!s.Root.Active;float duration=player.CalcDuration(row.Number("Parameter1"));
        PostEffectSuppressed=true;
        s.Fade?.Kill();overlayFade?.Kill();
        if(show){s.Root.Active=true;s.Animation.SyncActivity();StopAnimation(s);int index=(int)row.Number("Parameter3");if(index>=0&&index<s.Sequences.Count)s.Animation.Play(Archive.S(s.Sequences[index]!,"gameObject"),player.Speed);}
        float alpha=show?row.Number("Parameter2"):0;if(show&&alpha<=0)alpha=1;alpha=Math.Min(alpha,1);
        s.Fade=tweens.To(()=>s.Group.Alpha,alpha,duration,AdvEase.OutQuad,v=>s.Group.Alpha=v);
        overlayFade=tweens.To(()=>overlay,show?Math.Clamp(row.Number("Parameter4"),0,1):0,duration,AdvEase.OutQuad,v=>overlay=v);
        yield return s.Fade.Completed;yield return overlayFade.Completed;
        if(!show&&!player.Cancelled){StopAnimation(s);s.Root.Active=false;s.Animation.SyncActivity();PostEffectSuppressed=false;}
    }
    static void StopAnimation(Still s)=>s.Animation.Stop();
    public void Draw()
    {
        // The overlay precedes the still instances in the original canvas hierarchy.
        if(overlay>0)gl.Draw(white,[-1,-1,1,-1,1,1,-1,1],[0,0,1,0,1,1,0,1],[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],[0,0,0,overlay],0,false,false);
        var projection=new Vector3(2*ShakeOffset.X/(worldPerCanvasUnit*width),2*ShakeOffset.Y/(worldPerCanvasUnit*height),planeDistance/(planeDistance+ShakeOffset.Z));
        foreach(var s in ordered){if(!s.Root.Active)continue;foreach(var n in s.Nodes.Values){n.Layout(width,height);if(n.ActiveInHierarchy&&n.Image is not null)images.Image(n,width,height,clipTransform:projection,perspectiveDistance:planeDistance/worldPerCanvasUnit);}}
    }
    public void Dispose(){foreach(var s in loaded.Values){s.Animation.Dispose();s.Fade?.Kill();}overlayFade?.Kill();images.Dispose();}
}
