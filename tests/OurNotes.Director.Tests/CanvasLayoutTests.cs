using System.Numerics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class CanvasLayoutTests
{
    public static void Run(Action<bool,string> check)
    {
        AdvCanvasNode Node(string name,AdvCanvasNode? parent,params JsonNode[] components)=>new(new JsonObject{
            ["path"]=name,["active"]=true,["components"]=new JsonArray(components)
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
    }
}
