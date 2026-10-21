using System.Numerics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class CanvasLayoutTests
{
    public static void Run(Action<bool,string> check)
    {
        AdvCanvasNode Node(string name,AdvCanvasNode? parent,params JsonNode[] components)=>new(new JsonObject{
            ["path"]=name,["active"]=true,["rect"]=new JsonObject(),["components"]=new JsonArray(components)
        },parent){AnchorMin=new(0,1),AnchorMax=new(0,1),Pivot=new(0,1)};
        JsonNode Group(string name)=>JsonNode.Parse("""
            {"class":"VerticalLayoutGroup","m_Enabled":1,"m_Padding":{"m_Left":10,"m_Right":10,"m_Top":5,"m_Bottom":5},
             "m_Spacing":3,"m_ChildAlignment":0,"m_ChildControlWidth":1,"m_ChildControlHeight":1,
             "m_ChildForceExpandWidth":0,"m_ChildForceExpandHeight":0}
            """)!;
        JsonNode Fit()=>JsonNode.Parse("""{"class":"ContentSizeFitter","m_Enabled":1,"m_HorizontalFit":2,"m_VerticalFit":2}""")!;
        var root=Node("root",null,Group("VerticalLayoutGroup"),Fit());root.SizeDelta=new(500,500);
        var first=Node("first",root);var second=Node("second",root);
        var ignored=Node("ignored",root,JsonNode.Parse("""{"class":"LayoutElement","m_Enabled":0,"m_IgnoreLayout":1}""")!);ignored.SizeDelta=new(999);
        var inactive=Node("inactive",root);inactive.Active=false;
        Vector2? Text(AdvCanvasNode n)=>n==first?new(100,20):n==second?new(150,30):n==ignored||n==inactive?new(999):null;
        var layout=new AdvCanvasLayout([root,first,second,ignored,inactive],Text);layout.Layout(1920,1080);
        check(root.Width==170&&root.Height==63,"Chat content fitter includes padding and spacing, excludes hidden/ignored nodes");
        check(first.AnchoredPosition==new Vector2(10,-5)&&second.AnchoredPosition==new Vector2(10,-28),"Chat bubbles stack from the top with the original spacing");
        check(first.Width==100&&first.Height==20&&second.Width==150&&second.Height==30,"Layout preserves each bubble's preferred dimensions");
        var before=(root.SizeDelta,first.SizeDelta,first.AnchoredPosition,second.SizeDelta,second.AnchoredPosition);
        layout.Layout(1920,1080);
        check(before==(root.SizeDelta,first.SizeDelta,first.AnchoredPosition,second.SizeDelta,second.AnchoredPosition),"Repeated layout does not drift");
        var horizontal=Group("HorizontalLayoutGroup");horizontal["class"]="HorizontalLayoutGroup";horizontal["m_ChildForceExpandWidth"]=1;
        var row=Node("row",null,horizontal);row.SizeDelta=new(323,50);
        var a=Node("a",row);var b=Node("b",row);
        new AdvCanvasLayout([row,a,b],n=>n==row?null:new Vector2(100,20)).Layout(1920,1080);
        check(a.Width==150&&b.Width==150&&b.AnchoredPosition.X==163,"Horizontal surplus is divided between flexible children");
        var priority=Node("priority",null,Fit(),JsonNode.Parse("""{"class":"LayoutElement","m_Enabled":1,"m_LayoutPriority":2,"m_MinWidth":50,"m_PreferredWidth":80,"m_MinHeight":0,"m_PreferredHeight":-1}""")!);
        new AdvCanvasLayout([priority],_=>new Vector2(100,35)).Layout(1920,1080);
        check(priority.Width==80&&priority.Height==35,"Layout priority overrides valid properties and skips negative values independently");

        // UI particle prefabs can contain a plain Transform between two rects.
        // It remains at its own local origin, including under rotated/scaled UI.
        var parent=Node("canvas",null);parent.AnchorMin=parent.AnchorMax=Vector2.Zero;parent.Pivot=new(.5f);
        parent.AnchoredPosition=new(200,100);parent.SizeDelta=new(300,200);parent.Scale=new(2,3,1);
        parent.Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI/2);parent.Layout(960,540);
        var transform=new AdvCanvasNode(JsonNode.Parse("""{"path":"canvas/transform","active":true,"rect":null,"localPosition":{"x":11,"y":7,"z":5},"localScale":{"x":4,"y":2,"z":1}}""")!,parent);
        transform.Layout(960,540);
        check(!transform.IsRectTransform&&transform.Width==0&&transform.Height==0&&transform.LayoutPosition==new Vector3(11,7,5),
            "Plain canvas Transform keeps its local position and exposes a zero-size parent rect");
        var expected=AdvMatrix.Transform(parent.Matrix,new Vector4(11,7,5,1));
        check(Vector3.Distance(new(transform.Matrix[12],transform.Matrix[13],transform.Matrix[14]),new(expected.X,expected.Y,expected.Z))<.0001f,
            "Plain Transform position follows its parent's rotation and scale without a half-canvas offset");
        var child=Node("canvas/transform/rect",transform);child.AnchorMin=child.AnchorMax=new(.5f);child.Pivot=new(.5f);
        child.AnchoredPosition=new(8,-4);child.SizeDelta=new(100,60);child.Layout(960,540);
        check(child.Width==100&&child.Height==60&&child.LayoutPosition==new Vector3(8,-4,0),
            "RectTransform below a plain Transform anchors at its origin and keeps sizeDelta");
        var beforeTransform=(float[])child.Matrix.Clone();transform.Layout(1920,1080);child.Layout(1920,1080);
        check(child.Matrix.AsSpan().SequenceEqual(beforeTransform),"Plain Transform ancestry does not drift when the external canvas size changes");
    }
}
