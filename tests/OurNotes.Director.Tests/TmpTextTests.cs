using System.Numerics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class TmpTextTests
{
    public static int MissingGlyphs()
    {
        var names=Archive.ReadJson("ournotes-director/tests/OurNotes.Director.Tests/Fixtures/speaker-names.json");
        foreach(var test in names["cases"]!.AsArray())if(AdvUi.SpeakerName(new("test","Talk",test!["row"]!),names["masterIds"],id=>names["texts"]![id]!.ToString())!=Archive.S(test,"expected"))throw new Exception("Speaker name differs from source");
        var archive=new Archive(Path.GetFullPath("legacy/millsage-story-archive/archive"));var manifest=archive.Manifest("intl","stories","10079");
        var fonts=JsonNode.Parse(archive.Logical("intl",manifest,"ui/fonts.json","zh-Hans"))!;int checks=0;
        foreach(var pair in fonts["fonts"]!.AsObject()){
            string name=pair.Key;var missing=pair.Value?["missingGlyph"];if(missing is null)continue;
            var binding=fonts["texts"]!.AsObject().First().Value!.DeepClone();binding["localized"]!["fontAsset"]=name;
            binding["localized"]!["material"]=fonts["materials"]!.AsObject().First(p=>p.Key.StartsWith(name,StringComparison.Ordinal)).Key;
            int substitute=(int)Actor.N(missing["unicode"]);
            foreach(var code in missing["characters"]!.AsArray()){
                var actual=new AdvTmpText(fonts,binding){Text=char.ConvertFromUtf32((int)Actor.N(code))};
                var expected=new AdvTmpText(fonts,binding){Text=char.ConvertFromUtf32(substitute)};
                if(actual.PreferredSize(800)!=expected.PreferredSize(800)||!actual.Generate(800,200,new(0,1)).SequenceEqual(expected.Generate(800,200,new(0,1))))throw new Exception("Declared missing glyph differs from its original font substitute");checks++;
            }
        }
        if(checks==0)throw new Exception("No missing glyph metadata tested");
        var sample=fonts["texts"]!.AsObject().First().Value!;var unknown=new AdvTmpText(fonts,sample){Text=char.ConvertFromUtf32(0x10FFFF)};
        try{unknown.PreferredSize(800);throw new Exception("Unlisted missing character was silently substituted");}catch(NotSupportedException){}
        var definition=JsonNode.Parse(archive.Logical("intl",manifest,"story.json"))!;
        var scene=JsonNode.Parse(archive.Logical("intl",manifest,Archive.S(definition,"scene")))!;
        var episode=JsonNode.Parse(archive.Logical("intl",manifest,Archive.S(definition,"episode")))!;
        var settings=scene["settings"]!["playerSettings"]!;var program=AdvProgram.From(episode,settings);var loop=new AdvLoop();
        using(var gl=new OffscreenGl(64,64))using(var ui=new AdvUi(archive,"intl",manifest,definition,episode,settings,"zh-Hans",gl,loop,new AdvAudio(loop,[])){MasterIds=scene["settings"]!["masterIdSettings"]}){
            ui.CheckTexts(program.Initialize.Concat(program.Rows).Concat(program.Finalize));
            episode["text"]!["preflight-test"]=new JsonObject{["simplifiedChinese"]=char.ConvertFromUtf32(0x10FFFF)};
            var row=new AdvRow("bad42","Talk",new JsonObject{["AdvTextID"]="preflight-test",["TargetName"]="hotaru"});
            try{ui.CheckTexts([row]);throw new Exception("Preflight accepted unknown glyph");}catch(InvalidDataException e){if(!e.Message.Contains("bad42",StringComparison.Ordinal)||!e.Message.Contains("10FFFF",StringComparison.Ordinal))throw;}
            row.Data["IgnoreData"]=true;ui.CheckTexts([row]);
        }
        Console.WriteLine($"Passed {checks} original declared missing-glyph substitutions and unknown-character rejection.");return 0;
    }
    public static int Run(string fixture)
    {
        var archive=new Archive(Path.GetFullPath("legacy/millsage-story-archive/archive"));
        var sources=new Dictionary<string,JsonNode>();int cases=0,errors=0;float maximum=0;
        foreach(var test in Archive.ReadJson(fixture).AsArray()){
            string id=Archive.S(test!,"id"),language=Archive.S(test!,"language","zh-Hans"),key=id+":"+language;var manifest=archive.Manifest("intl","stories",id);
            if(!sources.TryGetValue(key,out var fonts))sources[key]=fonts=JsonNode.Parse(archive.Logical("intl",manifest,"ui/fonts.json",language))!;
            var binding=test!["window"] is null?fonts["texts"]![Archive.S(test,"path")]!:fonts["chatTexts"]![Archive.S(test,"window")]![Archive.S(test,"path")]!;
            var lines=new Dictionary<string,JsonNode>();
            if(test["window"] is null){var doc=JsonNode.Parse(archive.Logical("intl",manifest,"ui/ui.json",language))!;var node=doc["nodes"]!.AsArray().First(n=>Archive.S(n!,"path")==Archive.S(test,"path"))!;var role=doc["textStyle"]?["roles"]?[Archive.S(node["textStyle"]!,"fontRole")];if(role is not null)lines[Archive.S(binding["localized"]??binding,"fontAsset")]=role;}
            var text=new AdvTmpText(fonts,binding,lines){Text=Archive.S(test,"text")};if(test["wrapping"] is not null)text.Wrapping=(int)Actor.N(test["wrapping"]);float w=Actor.N(test["width"]),h=Actor.N(test["height"]);
            if(test["storyText"] is not null){text.SetStoryText(Archive.S(test,"storyText"));if(text.Text!=Archive.S(test,"text"))throw new Exception($"Case {cases}: ruby rewrite differs: {text.Text}");}
            if(test["typingLength"] is not null&&AdvUi.TextLength(Archive.S(test,"storyText"))!=(int)Actor.N(test["typingLength"]))throw new Exception($"Case {cases}: ruby typing length differs");
            var pivot=test["pivot"] is JsonArray v?new Vector2(Actor.N(v[0]),Actor.N(v[1])):new Vector2(0,1);
            var preferred=text.PreferredSize(w);var glyphs=text.Generate(w,h,pivot);var expected=test["glyphs"]!.AsArray();
            void Compare(float a,float b,string label){float error=Math.Abs(a-b);maximum=Math.Max(maximum,error);if(error>.002f){if(errors++<12)Console.Error.WriteLine($"Case {cases} {label}: native {a:R}, reference {b:R}");}}
            Compare(preferred.X,Actor.N(test["preferred"]![0]),"preferred width");Compare(preferred.Y,Actor.N(test["preferred"]![1]),"preferred height");
            Compare(text.RenderedFontSize,Actor.N(test["size"]),"font size");
            if(glyphs.Count!=expected.Count)throw new Exception($"Case {cases}: glyph count {glyphs.Count} != {expected.Count}");
            for(int i=0;i<glyphs.Count;i++){
                var g=glyphs[i];var e=expected[i]!;if(g.Character!=(int)Actor.N(e["character"])||g.Line!=(int)Actor.N(e["line"]))throw new Exception($"Case {cases} glyph {i}: character/line differs");
                for(int axis=0;axis<4;axis++){Compare(g.Rect[axis],Actor.N(e["rect"]![axis]),$"glyph {i} rect {axis}");Compare(g.Uv[axis],Actor.N(e["uv"]![axis]),$"glyph {i} UV {axis}");}Compare(g.Scale,Actor.N(e["scale"]),$"glyph {i} SDF scale");
                if(e["color"] is JsonArray rgba)for(int axis=0;axis<4;axis++)Compare(g.Color[axis],Actor.N(rgba[axis]),$"glyph {i} color {axis}");
            }
            cases++;
        }
        if(errors>0)throw new Exception($"TMP comparison: {errors} mismatches; maximum error {maximum:R}");
        Console.WriteLine($"Passed {cases} original TMP text layouts; maximum metric error {maximum:R}.");return 0;
    }
}
