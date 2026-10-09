using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvUi
{
    AdvTweens uiTweens=null!;
    AdvTween? flashTween,backdropTween;
    float flashAlpha,backdropAlpha;
    bool backdrop;
    readonly List<OffscreenGl.GlSurface> blurLevels=[];
    JsonNode TalkWindow=>nodes.Values.Single(n=>Archive.S(n,"name")==window)["talkWindow"]!;
    AdvEase UiEase=>(AdvEase)(int)Actor.N(doc["dotween"]?["defaultEaseType"],6);
    void UpdateBackdrop()
    {
        backdrop=talkVisible&&Actor.B(TalkWindow["_useBackdropFilter"]);backdropTween?.Kill();
        if(!backdrop){backdropAlpha=0;return;}
        backdropTween=uiTweens.To(()=>backdropAlpha,1,.2f,UiEase,v=>backdropAlpha=v);
    }
    IEnumerable<AdvWait> Flash(AdvRow row,AdvPlayback player){yield return player.NoWait(row,FlashInner(player.CalcDuration(row.Number("Duration"),.3f)));}
    IEnumerable<AdvWait> FlashInner(float duration)
    {
        flashTween?.Kill();flashAlpha=1;
        var tween=flashTween=uiTweens.To(()=>flashAlpha,0,duration,UiEase,v=>flashAlpha=v,true);
        yield return tween.Completed;
        if(flashTween==tween){flashAlpha=0;flashTween=null;}
    }
    void DrawBackdrop()
    {
        if(!backdrop)return;
        var p=doc["blur"]??throw new InvalidDataException("Center talk requires UI blur settings");
        int count=Math.Clamp((int)Actor.N(p["iterations"]),1,8),downsample=Math.Clamp((int)Actor.N(p["downsample"]),0,30);
        textShaders??=new(archive,site,manifest,"ui/shaders/",language);
        if(blurLevels.Count==0)for(int i=0;i<count;i++)blurLevels.Add(gl.FloatSurface(Math.Max(1,(gl.Width/renderScale)>>Math.Min(30,downsample+i)),Math.Max(1,(gl.Height/renderScale)>>Math.Min(30,downsample+i))));
        var source=gl.CurrentSurface;
        void Pass(int pass,OffscreenGl.GlSurface target){gl.Blit(textShaders.Program("Hidden/UI/DualKawaseBlur",pass),target,new Dictionary<string,float[]>{{"_Offset",[Math.Max(0,Actor.N(p["offset"]))]},{"_BlitTexture_TexelSize",[1f/source.Width,1f/source.Height,source.Width,source.Height]}},new Dictionary<string,uint>{{"_BlitTexture",source.Texture}});source=target;}
        for(int i=0;i<count;i++)Pass(0,blurLevels[i]);for(int i=count-2;i>=0;i--)Pass(1,blurLevels[i]);gl.CopySurface(source,gl.CurrentSurface,true);
        var node=nodes.Values.Single(n=>Archive.S(n,"name")=="CenterTalkBackdrop");
        node["image"]!["m_Color"]=TalkWindow["_backdropFilterColor"]!.DeepClone();drawAlpha=backdropAlpha;Image(node);drawAlpha=1;
    }
    void DrawFlash(){if(flashAlpha<=0)return;drawAlpha=flashAlpha;Image(nodes.Values.Single(n=>Archive.S(n,"name")=="FlashView"));drawAlpha=1;}
}
