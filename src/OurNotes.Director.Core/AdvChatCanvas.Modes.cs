using System.Numerics;

namespace OurNotes.Director.Core;

public sealed partial class AdvChatCanvas
{
    readonly Vector2 defaultPosition;
    Vector3 baseScale;
    public int ScreenMode{get;private set;}
    Vector2 HalfExtents()
    {
        Layout();var m=root!.Matrix;float det=m[0]*m[5]-m[1]*m[4];
        float a=m[5]/det,b=-m[4]/det,c=-m[1]/det,d=m[0]/det;
        Vector2 min=new(float.PositiveInfinity),max=new(float.NegativeInfinity);
        void Visit(AdvCanvasNode n){
            float l=-n.Pivot.X*n.Width,t=-n.Pivot.Y*n.Height;
            foreach(var p in new[]{new Vector2(l,t),new(l+n.Width,t),new(l,t+n.Height),new(l+n.Width,t+n.Height)}){
                var world=AdvMatrix.Transform(n.Matrix,new(p,0,1));float x=world.X-m[12],y=world.Y-m[13];var q=new Vector2(a*x+b*y,c*x+d*y);min=Vector2.Min(min,q);max=Vector2.Max(max,q);
            }
            foreach(var child in children[n])if(child.Active)Visit(child);
        }
        Visit(root);var size=(max-min)*new Vector2(baseScale.X,baseScale.Y);
        float angle=Math.Abs(Actor.N(windowConfig?["_windowRotation"]?["z"]))*.017453292f,s=Math.Abs(MathF.Sin(angle)),co=Math.Abs(MathF.Cos(angle));
        return new((size.X*co+size.Y*s)*.5f,(size.Y*co+size.X*s)*.5f);
    }
    public (Vector2 Position,Vector3 Scale) ModeTransform()
    {
        var off=ScreenMode==1?ViewConfig["_incomingCallPositionOffset"]:null;var window=windowConfig?["_windowPosition"];
        Vector2 pos=new((Actor.N(off?["x"])+defaultPosition.X)+Actor.N(window?["x"]),Actor.N(off?["y"])+Actor.N(window?["y"]));
        float multiplier=1;
        if(ScreenMode==1){var half=HalfExtents();float fx=Width*.5f-Math.Abs(pos.X),fy=Height*.5f-Math.Abs(pos.Y);
            multiplier=fx<=0||fy<=0?.01f:half.X<=0||half.Y<=0?1:Math.Clamp(Math.Min(fy/half.Y,fx/half.X),.01f,1);
            var limit=Vector2.Max(Vector2.Zero,new Vector2(Width,Height)*.5f-half*multiplier);pos=Vector2.Clamp(pos,-limit,limit);
        }
        return(pos,baseScale*multiplier);
    }
    public void SetModeName(string text,int members)
    {
        if(ScreenMode==1)SetText("_incomingCallNameText",text);else if(ScreenMode==2)SetText("_lockScreenNameText",text);else SetWindowName(text,members);
    }
}
