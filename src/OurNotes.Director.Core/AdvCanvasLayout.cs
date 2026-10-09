// uGUI's horizontal/vertical layout and ContentSizeFitter, from ournotes-player/ui-layout.js.
using System.Numerics;
using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

public sealed class AdvCanvasLayout
{
    readonly AdvCanvasNode[] nodes;
    readonly Func<AdvCanvasNode,Vector2?> textSize;
    readonly Dictionary<AdvCanvasNode,AdvCanvasNode[]> children;
    readonly Dictionary<(AdvCanvasNode,int),Size> totals=[];
    readonly record struct Size(float Min,float Preferred,float Flexible);
    public AdvCanvasLayout(IEnumerable<AdvCanvasNode> nodes,Func<AdvCanvasNode,Vector2?> textSize)
    {
        this.nodes=nodes.ToArray();this.textSize=textSize;
        children=this.nodes.ToDictionary(n=>n,n=>this.nodes.Where(c=>c.Parent==n).ToArray());
    }
    static JsonNode? Enabled(AdvCanvasNode n,string name){var c=n.Component(name);return Actor.B(c?["m_Enabled"])?c:null;}
    static JsonNode? Group(AdvCanvasNode n)=>Enabled(n,"HorizontalLayoutGroup")??Enabled(n,"VerticalLayoutGroup");
    static float Axis(Vector2 v,int axis)=>axis==0?v.X:v.Y;
    static Vector2 With(Vector2 v,int axis,float value)=>axis==0?v with{X=value}:v with{Y=value};
    static float Extent(AdvCanvasNode n,int axis)=>axis==0?n.Width:n.Height;
    AdvCanvasNode[] Children(AdvCanvasNode n)=>children[n].Where(c=>c.ActiveInHierarchy&&!Actor.B(c.Component("LayoutElement")?["m_IgnoreLayout"])).ToArray();
    Size Measure(AdvCanvasNode n,int axis)
    {
        var entries=new List<(float Priority,Size Value)>();string dimension=axis==0?"Width":"Height";
        if(textSize(n) is Vector2 text)entries.Add((0,new(0,Axis(text,axis),-1)));
        if(n.Image is JsonNode image&&Actor.B(image["m_Enabled"])){float p=0;if(image["m_Sprite"] is JsonNode sprite){var r=sprite["rect"];var b=sprite["border"];bool sliced=Actor.N(image["m_Type"]) is 1 or 2;
            float length=sliced?(axis==0?Actor.N(b?["x"])+Actor.N(b?["z"]):Actor.N(b?["y"])+Actor.N(b?["w"])):Actor.N(r?[axis==0?"width":"height"]);
            p=length/(Actor.N(sprite["pixelsToUnits"],100)/100*Actor.N(image["m_PixelsPerUnitMultiplier"],1));}entries.Add((0,new(0,p,-1)));}
        if(Enabled(n,"LayoutElement") is JsonNode e)entries.Add((Actor.N(e["m_LayoutPriority"]),new(Actor.N(e["m_Min"+dimension]),Actor.N(e["m_Preferred"+dimension]),Actor.N(e["m_Flexible"+dimension]))));
        if(Group(n) is not null&&totals.TryGetValue((n,axis),out var total))entries.Add((0,total));
        float Property(Func<Size,float> get){float priority=float.NegativeInfinity,value=0;foreach(var e in entries){float p=get(e.Value);if(e.Priority<priority||p<0)continue;if(e.Priority>priority){priority=e.Priority;value=p;}else value=Math.Max(value,p);}return value;}
        float min=Property(s=>s.Min);return new(min,Math.Max(min,Property(s=>s.Preferred)),Property(s=>s.Flexible));
    }
    public float PreferredSize(AdvCanvasNode n,int axis)=>Measure(n,axis).Preferred;
    readonly record struct Config(bool Other,float Pad,float Start,float Spacing,float Align,bool Control,bool Expand);
    static Config Configure(JsonNode group,int axis)
    {
        if(Actor.B(group["m_ChildScaleWidth"])||Actor.B(group["m_ChildScaleHeight"])||Actor.B(group["m_ReverseArrangement"]))throw new NotSupportedException("Canvas layout child scaling or reversal");
        var p=group["m_Padding"]!;float start=Actor.N(p[axis==0?"m_Left":"m_Top"]),end=Actor.N(p[axis==0?"m_Right":"m_Bottom"]);int align=(int)Actor.N(group["m_ChildAlignment"]);
        bool vertical=Archive.S(group,"class",Archive.S(group,"type"))=="VerticalLayoutGroup";string dimension=axis==0?"Width":"Height";
        return new(vertical!=(axis==1),start+end,start,Actor.N(group["m_Spacing"]),(axis==0?align%3:align/3)*.5f,Actor.B(group["m_ChildControl"+dimension]),Actor.B(group["m_ChildForceExpand"+dimension]));
    }
    Size ChildSize(AdvCanvasNode n,int axis,Config config){var s=config.Control?Measure(n,axis):new Size(Axis(n.SizeDelta,axis),Axis(n.SizeDelta,axis),0);return config.Expand?s with{Flexible=Math.Max(s.Flexible,1)}:s;}
    void Calculate(AdvCanvasNode n,int axis)
    {
        if(!n.ActiveInHierarchy)return;bool has=textSize(n) is not null||n.Image is not null||Enabled(n,"LayoutElement") is not null||Group(n) is not null;
        if(!has&&n.Component("HorizontalLayoutGroup") is null&&n.Component("VerticalLayoutGroup") is null)return;
        foreach(var child in children[n])Calculate(child,axis);if(Group(n) is not JsonNode g)return;
        var config=Configure(g,axis);var list=Children(n);float min=config.Pad,pref=config.Pad,flex=0;
        foreach(var c in list){var s=ChildSize(c,axis,config);if(config.Other){min=Math.Max(s.Min+config.Pad,min);pref=Math.Max(s.Preferred+config.Pad,pref);flex=Math.Max(s.Flexible,flex);}else{min+=s.Min+config.Spacing;pref+=s.Preferred+config.Spacing;flex+=s.Flexible;}}
        if(!config.Other&&list.Length>0){min-=config.Spacing;pref-=config.Spacing;}totals[(n,axis)]=new(min,Math.Max(min,pref),flex);
    }
    float width,height;
    void Relayout(){foreach(var node in nodes)node.Layout(width,height);}
    void Place(AdvCanvasNode child,int axis,float pos,float? size=null)
    {
        child.AnchorMin=child.AnchorMax=new(0,1);if(size is float s)child.SizeDelta=With(child.SizeDelta,axis,s);float length=Axis(child.SizeDelta,axis);
        child.AnchoredPosition=With(child.AnchoredPosition,axis,axis==0?pos+length*child.Pivot.X:-pos-length*(1-child.Pivot.Y));
    }
    void Control(AdvCanvasNode n,int axis)
    {
        var fitter=Enabled(n,"ContentSizeFitter");var group=Group(n);if(!n.ActiveInHierarchy||fitter is null&&group is null)return;
        if(fitter is not null){int mode=(int)Actor.N(fitter[axis==0?"m_HorizontalFit":"m_VerticalFit"]);if(mode!=0){if(mode is not (1 or 2))throw new NotSupportedException("Canvas fit mode "+mode);var size=Measure(n,axis);float parent=n.Parent is null?(axis==0?width:height):Extent(n.Parent,axis);n.SizeDelta=With(n.SizeDelta,axis,(mode==1?size.Min:size.Preferred)-parent*(Axis(n.AnchorMax,axis)-Axis(n.AnchorMin,axis)));Relayout();}}
        if(group is not null){var g=Configure(group,axis);var total=totals[(n,axis)];float size=Extent(n,axis);
            float Start(float required)=>g.Start+(size-(required+g.Pad))*g.Align;
            if(g.Other){float inner=size-g.Pad;foreach(var c in Children(n)){var s=ChildSize(c,axis,g);float required=Math.Min(Math.Max(inner,s.Min),s.Flexible>0?size:s.Preferred),start=Start(required);Place(c,axis,g.Control?start:start+(required-Axis(c.SizeDelta,axis))*g.Align,g.Control?required:null);}}
            else{float pos=g.Start,multiplier=0,surplus=size-total.Preferred;if(surplus>0){if(total.Flexible==0)pos=Start(total.Preferred-g.Pad);else multiplier=surplus/total.Flexible;}float lerp=total.Min!=total.Preferred?Math.Clamp((size-total.Min)/(total.Preferred-total.Min),0,1):0;
                foreach(var c in Children(n)){var s=ChildSize(c,axis,g);float childSize=s.Min+(s.Preferred-s.Min)*lerp+s.Flexible*multiplier;Place(c,axis,g.Control?pos:pos+(childSize-Axis(c.SizeDelta,axis))*g.Align,g.Control?childSize:null);pos+=childSize+g.Spacing;}}
            Relayout();}
        foreach(var c in children[n])Control(c,axis);
    }
    public void Layout(float width,float height)
    {
        this.width=width;this.height=height;Relayout();var roots=new List<AdvCanvasNode>();foreach(var n in nodes){if(!n.ActiveInHierarchy||Group(n) is null&&Enabled(n,"ContentSizeFitter") is null)continue;var root=n;while(root.Parent is AdvCanvasNode parent&&parent.ActiveInHierarchy&&Group(parent) is not null)root=parent;if(!roots.Contains(root))roots.Add(root);}
        int Depth(AdvCanvasNode n)=>n.Parent is null?0:1+Depth(n.Parent);
        foreach(var root in roots.OrderBy(Depth))for(int axis=0;axis<2;axis++){Calculate(root,axis);Control(root,axis);}
    }
}
