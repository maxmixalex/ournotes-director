using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class FrameTextTests
{
    public static int AnimatorLifetime(string fixture)
    {
        int checks=0;
        foreach(var test in Archive.ReadJson(fixture).AsArray()){
            var node=new AdvCanvasNode(test!["node"]!,null);var animator=new AdvCanvasAnimator(node,test["component"]!,new(test),node.Bind);int frame=0;
            foreach(var state in test["states"]!.AsArray()){
                if(frame==20)node.Active=false;if(frame==35){node.Active=true;animator.OnEnable();}if(frame==50)animator.Enabled=true;if(frame==60)animator.Enabled=false;if(frame==70)animator.Enabled=true;
                animator.Step(1f/30);if(Math.Abs(node.AnchoredPosition.X-Actor.N(state!["x"]))>.0001f||Math.Abs(animator.Animator.NormalizedTime-Actor.N(state["time"]))>.00001f)throw new Exception($"Canvas Animator lifetime mismatch at {checks}");frame++;checks++;
            }
        }
        Console.WriteLine($"Passed {checks} original canvas Animator enabled/disabled, reactivation and state-retention frames.");return 0;
    }
    public static int Layout(string fixture)
    {
        var doc=Archive.ReadJson(fixture);var nodes=new Dictionary<string,AdvCanvasNode>();
        foreach(var record in doc["nodes"]!.AsArray()){string path=Archive.S(record!,"path");nodes[path]=new(record!,path.Contains('/')?nodes[path[..path.LastIndexOf('/')]]:null);}
        var modifiers=new AdvFrameLayout(nodes.Values);int checks=0;float maximum=0;
        foreach(var step in doc["steps"]!.AsArray()){
            var root=nodes["root"];root.Scale=new(Actor.N(step!["scale"]));root.Active=Actor.B(step["on"]);modifiers.SetActive(root.Active);
            nodes["root/stretch"].Bind(new("","StretchPosition","positionPercent"))!.Set(Actor.N(step["percent"]));modifiers.Update();foreach(var node in nodes.Values)node.Layout(Actor.N(step["w"]),Actor.N(step["h"]));
            foreach(var expected in step["expected"]!.AsArray()){
                var node=nodes[Archive.S(expected!,"path")];var actual=new Dictionary<string,float[]>{{"scale",[node.Scale.X,node.Scale.Y,node.Scale.Z]},{"position",[node.AnchoredPosition.X,node.AnchoredPosition.Y]},{"size",[node.SizeDelta.X,node.SizeDelta.Y]}};
                foreach(var (key,values) in actual)for(int i=0;i<values.Length;i++){float error=Math.Abs(values[i]-Actor.N(expected![key]![i]));maximum=Math.Max(maximum,error);if(error>.0002f)throw new Exception($"Frame layout differs at state {checks}, {node.Path}/{key}/{i}: {error}");}
            }
            checks++;
        }
        Console.WriteLine($"Passed {checks} source frame fitter/stretch states and Animator binding; maximum error {maximum:G9}.");return 0;
    }
    public static int Run(string fixture)
    {
        foreach(var c in Archive.ReadJson(fixture).AsArray()){
            string[] actual=[AdvFrameTexts.UserName(c!["name"]!.ToString()),AdvFrameTexts.UserId(c["name"]!.ToString(),c["id"]!.ToString()),AdvFrameTexts.Body(c["body"]!.ToString())];
            if(!actual.SequenceEqual(c["expected"]!.AsArray().Select(n=>n!.ToString())))throw new Exception("Frame comment grapheme formatting differs");
        }
        var archive=new Archive(Path.GetFullPath("legacy/millsage-story-archive/archive"));var manifest=archive.Manifest("intl","stories","10060");using var gl=new OffscreenGl(320,180);using var drawing=new AdvCanvasTexts(archive,"intl",manifest,"zh-Hans",gl);
        var rootRecord=JsonNode.Parse("""{"path":"root","active":true,"components":[{"class":"AdvSlanderCommentFrame","_patterns":[{"_animatorStateName":"Show","_nodes":[{"gameObject":"root/comment"}]}]}]}""")!;
        var root=new AdvCanvasNode(rootRecord,null);var nodes=new Dictionary<string,AdvCanvasNode>{{root.Path,root}};
        var comment=new AdvCanvasNode(JsonNode.Parse("""{"path":"root/comment","active":true,"components":[{"class":"AdvSlanderCommentNode","_userName":{"gameObject":"root/name"},"_userId":{"gameObject":"root/id"},"_body":{"gameObject":"root/body"}}]}""")!,root);nodes.Add(comment.Path,comment);
        var bindings=new JsonObject();int row=0;
        foreach(string key in new[]{"name","id","body"}){
            string path="root/"+key;var rec=JsonNode.Parse("""{"active":true,"rect":{"m_AnchorMin":{"x":0.5,"y":0.5},"m_AnchorMax":{"x":0.5,"y":0.5},"m_Pivot":{"x":0.5,"y":0.5},"m_SizeDelta":{"x":1600,"y":160}},"components":[{"class":"TextMeshProUGUI","m_Enabled":1,"m_text":"initial"}]}""")!;rec["path"]=path;rec["rect"]!["m_AnchoredPosition"]=new JsonObject{["x"]=0,["y"]=180-row++*180};
            rec["components"]!.AsArray().Add(new JsonObject{["class"]="UIText",["_targetText"]=new JsonObject{["gameObject"]=path}});nodes[path]=new(rec,root);
            var binding=drawing.Fonts["texts"]!.AsObject().First().Value!.DeepClone();binding["class"]="TextMeshProUGUI";binding.AsObject().Remove("localizeKoreanAdjust");bindings[path]=binding;
        }
        var texts=new AdvFrameTexts(nodes,drawing.Fonts,bindings,"zh-Hans");var layout=new AdvCanvasLayout(nodes.Values,n=>texts.Texts.TryGetValue(n,out var t)?t.PreferredSize(n.Width):null);
        texts.SetTexts("Show",["星","millsage","123456789012345678901"]);
        if(texts.Texts[nodes["root/id"]].Text!="@millsage"||texts.Texts[nodes["root/body"]].Text!="12345678901234567890\n1")throw new Exception("Frame receiver did not bind the three texts");
        // This cached episode's subset atlas contains these characters; formatting above also covers absent glyphs.
        texts.SetTexts("Show",["春","",new string('一',21)]);
        Pixels Render(){layout.Layout(1920,1080);gl.Begin([0,0,0,1]);var globals=AdvCanvasTexts.OrthographicGlobals(1920,1080,320,180);foreach(var (node,text) in texts.Texts)drawing.Draw(node,text,globals,1);return gl.Read(320,180);}
        var shown=Render();if(!shown.Rgba.Where((v,i)=>i%4!=3).Any(v=>v!=0))throw new Exception("Frame text was not rendered");
        texts.SetTexts("Show",["wrong count"]);if(Render().Rgba.Where((v,i)=>i%4!=3).Any(v=>v!=0))throw new Exception("Frame receiver retained stale text after a count mismatch");
        texts.SetTexts("Show",["春","",new string('一',21)]);if(!Render().Rgba.SequenceEqual(shown.Rgba))throw new Exception("Frame text replay changed pixels");
        texts.Clear();if(texts.Texts.Values.Any(t=>t.Text.Length!=0))throw new Exception("Frame hide did not clear comments");
        Console.WriteLine("Passed 7 source grapheme formatting cases and native frame text binding, GPU drawing, clearing and replay.");return 0;
    }
}
