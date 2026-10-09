using System.Numerics;
namespace OurNotes.Director.Core;

// AdvSlanderCommentFrameFitter.OnEnable/layout and StretchPosition.Update.
public sealed class AdvFrameLayout
{
    sealed class Fit(AdvCanvasNode node){public AdvCanvasNode Node=node;public bool Dirty;public float? Width,Height;}
    readonly List<Fit> fitters=[];readonly AdvCanvasNode[] stretch;
    bool active;
    public AdvFrameLayout(IEnumerable<AdvCanvasNode> nodes)
    {
        var all=nodes.ToArray();stretch=all.Where(n=>n.Component("StretchPosition") is not null).ToArray();
        foreach(var node in all)if(Actor.B(node.Component("AdvSlanderCommentFrameFitter")?["m_Enabled"])){
            if(node.BeforeLayout is not null||Actor.B(node.Component("AspectRatioFitter")?["m_Enabled"]))throw new NotSupportedException("Comment frame fitter conflicts with an existing size fitter");
            var state=new Fit(node);fitters.Add(state);node.BeforeLayout=n=>{
                if(!n.ActiveInHierarchy||n.Parent is not { } parent)return;
                bool resized=state.Width!=parent.Width||state.Height!=parent.Height;state.Width=parent.Width;state.Height=parent.Height;
                if(!state.Dirty&&!resized)return;state.Dirty=false;
                if(parent.Width<=0||parent.Height<=0||parent.Scale.X<=0||parent.Record["rect"] is null)return;
                float s=Math.Min(Math.Min(parent.Width/1920,parent.Height/886),1);n.Scale=new(s);n.AnchoredPosition=(s-1)*parent.AnchoredPosition/parent.Scale.X;
            };
        }
    }
    public void SetActive(bool value){if(value&&!active)foreach(var f in fitters)if(f.Node.ActiveInHierarchy)f.Dirty=true;active=value;}
    public void Update()
    {
        if(!active)return;
        foreach(var node in stretch){var c=node.Component("StretchPosition")!;if(!Actor.B(c["m_Enabled"])||!node.ActiveInHierarchy||node.Parent is not {} parent||!parent.HasLayout)continue;
            float target=-Actor.N(c["positionPercent"])/100*parent.Width,min=node.AnchoredPosition.X-node.SizeDelta.X*node.Pivot.X,max=node.AnchoredPosition.X+node.SizeDelta.X*(1-node.Pivot.X);
            node.SizeDelta+=new Vector2(target-max,0);node.AnchoredPosition=node.AnchoredPosition with{X=min+node.SizeDelta.X*node.Pivot.X};
        }
    }
}
