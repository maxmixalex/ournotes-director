using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

// The game's AdvSlanderCommentFrame receiver and TMP bindings of a Frame prefab.
public sealed class AdvFrameTexts
{
    readonly IReadOnlyDictionary<string,AdvCanvasNode> nodes;
    readonly JsonNode? receiver;
    public Dictionary<AdvCanvasNode,AdvTmpText> Texts{get;}=[];
    public AdvFrameTexts(IReadOnlyDictionary<string,AdvCanvasNode> nodes,JsonNode fonts,JsonNode? bindings,string language)
    {
        this.nodes=nodes;receiver=nodes.Values.First().Component("AdvSlanderCommentFrame");
        foreach(var node in nodes.Values){
            var component=node.Component("TextMeshProUGUI")??node.Component("RubyTextMeshProUGUI")??node.Component("RubyEmojiTextMeshProUGUI");if(component is null)continue;
            var binding=bindings?[node.Path]?.DeepClone()??throw new InvalidDataException("Frame text binding missing: "+node.Path);
            string cls=Archive.S(component,"class",Archive.S(component,"type"));if(binding["class"] is JsonNode bindingClass&&bindingClass.ToString()!=cls)throw new InvalidDataException("Frame text binding class differs: "+node.Path);
            binding["enabled"]=Actor.B(component["m_Enabled"]);binding["m_text"]=Archive.S(component,"m_text");
            var localize=node.Component("LocalizeText");bool localized=Actor.B(localize?["m_Enabled"])&&Actor.B(localize?["_localizeEnabled"]);
            if(localized&&Archive.S(localize!,"_masterTextID") is not ("" or "0"))throw new NotSupportedException("Localized serialized frame text");
            if(Actor.B(binding["localizeKoreanAdjust"]?["m_Enabled"]))throw new NotSupportedException("Frame LocalizeKoreanAdjust");
            var text=new AdvTmpText(fonts,binding);if(localized&&language=="en"&&!Archive.S(node.Record,"name",node.Path.Split('/')[^1]).Contains("nowrap",StringComparison.OrdinalIgnoreCase))text.Wrapping=1;Texts.Add(node,text);
        }
        Clear();
    }
    static string Normalize(string text)=>text.Normalize(NormalizationForm.FormC);
    public static string UserName(string text)=>Normalize(text);
    public static string UserId(string name,string id){if(id.Length==0)return "";int count=19-StringInfo.ParseCombiningCharacters(Normalize(name)).Length;if(count<1)return "";string value=Normalize(id);var offsets=StringInfo.ParseCombiningCharacters(value);return "@"+(offsets.Length>count?value[..offsets[count]]:value);}
    public static string Body(string text){string value=Normalize(text).Replace("\r\n","\n").Replace('\r','\n');var offsets=StringInfo.ParseCombiningCharacters(value);return !value.Contains('\n')&&offsets.Length>=21?value.Insert(offsets[20],"\n"):value;}
    IEnumerable<JsonNode> Patterns=>receiver?["_patterns"]?.AsArray().OfType<JsonNode>()??[];
    AdvCanvasNode? TextNode(JsonNode? reference,string field){if(reference is null)return null;var comment=nodes[Archive.S(reference,"gameObject")].Component("AdvSlanderCommentNode")!;var ui=nodes[Archive.S(comment[field]!,"gameObject")].Component("UIText")!;return nodes[Archive.S(ui["_targetText"]!,"gameObject")];}
    void Set(AdvCanvasNode? node,string text){if(node is not null&&Texts.TryGetValue(node,out var target))target.Text=text;}
    public void Clear(){foreach(var pattern in Patterns)foreach(var reference in pattern["_nodes"]!.AsArray())foreach(string field in new[]{"_userName","_userId","_body"})Set(TextNode(reference,field),"");}
    public void SetTexts(string state,IReadOnlyList<string> texts)
    {
        if(receiver is null)return;Clear();var pattern=Patterns.FirstOrDefault(p=>Archive.S(p,"_animatorStateName")==state);if(pattern is null)return;var references=pattern["_nodes"]!.AsArray();if(texts.Count!=references.Count*3)return;
        for(int i=0;i<references.Count;i++){var r=references[i];Set(TextNode(r,"_userName"),UserName(texts[i*3]));Set(TextNode(r,"_userId"),UserId(texts[i*3],texts[i*3+1]));Set(TextNode(r,"_body"),Body(texts[i*3+2]));}
    }
}
