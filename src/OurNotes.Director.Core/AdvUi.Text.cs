using System.Numerics;
using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

public sealed partial class AdvUi
{
    AdvCanvasTexts? textRenderer;
    UnityShaderLibrary? textShaders;
    readonly Dictionary<string,(AdvTmpText Text,AdvCanvasNode Node)> textLayouts=[];
    readonly Dictionary<string,JsonNode> fontLines=[];
    Dictionary<string,float[]>? textGlobals;
    void DrawText(JsonNode node,string value,int maximum)
    {
        string path=Archive.S(node,"path");
        if(!textLayouts.TryGetValue(path,out var entry)){
            var binding=fonts["texts"]![path]!;var style=node["textStyle"]!;
            var role=doc["textStyle"]?["roles"]?[Archive.S(style,"fontRole")];
            if(role is not null)fontLines.TryAdd(Archive.S(binding["localized"]??binding,"fontAsset"),role);
            var record=new JsonObject{["path"]=path,["active"]=true};
            entry=(new AdvTmpText(fonts,binding,fontLines),new AdvCanvasNode(record,null){AnchorMin=Vector2.Zero,AnchorMax=Vector2.Zero,Pivot=Vector2.Zero});textLayouts.Add(path,entry);
            if(language=="en"&&!Archive.S(node,"name").Contains("nowrap",StringComparison.OrdinalIgnoreCase))entry.Text.Wrapping=1;
        }
        var text=entry.Text;var canvas=entry.Node;text.SetStoryText(value);text.MaxVisible=maximum;
        var bounds=Bounds(path);var parent=nodes.GetValueOrDefault(path[..path.LastIndexOf('/')]);
        if(parent?["layoutGroup"] is JsonNode group&&Actor.B(group["m_Enabled"])){
            string parentPath=Archive.S(parent,"path");var children=nodes.Values.Where(n=>Archive.S(n,"path").StartsWith(parentPath+"/",StringComparison.Ordinal)&&!Archive.S(n,"path")[(parentPath.Length+1)..].Contains('/')&&Actor.B(n["active"])).ToArray();
            if(Archive.S(group,"class")!="HorizontalLayoutGroup"||children.Length!=1)throw new NotSupportedException("Text layout group not connected: "+parentPath);
            var area=Bounds(parentPath);var pad=group["m_Padding"]!;float left=Actor.N(pad["m_Left"]),right=Actor.N(pad["m_Right"]),top=Actor.N(pad["m_Top"]),bottom=Actor.N(pad["m_Bottom"]);
            var preferred=text.PreferredSize(area.W-left-right);
            if(Actor.N(parent["contentSizeFitter"]?["m_HorizontalFit"])==2){float fitted=preferred.X+left+right;area=area with{X=area.X+(area.W-fitted)*Actor.N(parent["rect"]?["m_Pivot"]?["x"],.5f),W=fitted};}
            float childW=Actor.B(group["m_ChildControlWidth"])?Actor.B(group["m_ChildForceExpandWidth"])?area.W-left-right:Math.Min(preferred.X,area.W-left-right):Actor.N(node["rect"]?["m_SizeDelta"]?["x"]);
            preferred=text.PreferredSize(childW);
            if(Actor.N(parent["contentSizeFitter"]?["m_VerticalFit"])==2){float fitted=preferred.Y+top+bottom;area=area with{Y=area.Y+(area.H-fitted)*Actor.N(parent["rect"]?["m_Pivot"]?["y"],.5f),H=fitted};}
            float childH=Actor.B(group["m_ChildControlHeight"])?Actor.B(group["m_ChildForceExpandHeight"])?area.H-top-bottom:Math.Min(preferred.Y,area.H-top-bottom):Actor.N(node["rect"]?["m_SizeDelta"]?["y"]);
            int align=(int)Actor.N(group["m_ChildAlignment"]);bounds=new(area.X+left+(area.W-left-right-childW)*(align%3)*.5f,area.Y+bottom+(area.H-top-bottom-childH)*(1-align/3*.5f),childW,childH);
        }
        canvas.AnchoredPosition=new(bounds.X+drawOffset.X,bounds.Y+drawOffset.Y);canvas.SizeDelta=new(bounds.W,bounds.H);canvas.Alpha=drawAlpha;canvas.Layout(canvasWidth,canvasHeight);
        textRenderer??=new(archive,site,manifest,language,gl);textGlobals??=AdvCanvasTexts.OrthographicGlobals(canvasWidth,canvasHeight,gl.Width,gl.Height);
        bool backdrop=Archive.S(node,"name")=="TalkText"&&Actor.B(TalkWindow["_useBackdropFilter"]);
        static Vector4 Color(JsonNode c)=>new(Actor.N(c["r"]),Actor.N(c["g"]),Actor.N(c["b"]),Actor.N(c["a"]));
        textRenderer.Draw(canvas,text,textGlobals,1,backdrop?Color(TalkWindow["_talkTextColor"]!):null,backdrop?Color(TalkWindow["_talkTextOutlineColor"]!):null);
    }
    public void Dispose(){Chat?.Dispose();textRenderer?.Dispose();textShaders?.Dispose();}
}
