using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

if(args.Contains("--director")){try{return DirectorIntegrationTests.Run();}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Contains("--native-scene"))return NativeSceneTests.Run();
if(args.Contains("--tmp-missing")){try{return TmpTextTests.MissingGlyphs();}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Contains("--canvas-material")){try{return CanvasMaterialTests.Run();}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Contains("--background-plane")){try{return EffectSpriteTests.BackgroundPlane();}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Length==2&&args[0]=="--animator-sprites"){try{return AnimatorSpriteTests.Run(args[1]);}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Contains("--alpha"))return AlphaTests.Run();
if(args.Length==2&&args[0]=="--canvas-perspective")return CanvasProjectionTests.Run(args[1]);
if(args.Length==2&&args[0]=="--canvas-tiles")return CanvasProjectionTests.Tiles(args[1]);
if(args.Length==2&&args[0]=="--still-sequences")return StillSequenceTests.Run(args[1]);
if(args.Contains("--effect-scene"))return EffectParticleTests.Scene();
if(args.Contains("--canvas-particles"))return EffectParticleTests.Canvas();
if(args.Length==2&&args[0]=="--frame-text"){try{return FrameTextTests.Run(args[1]);}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Length==2&&args[0]=="--frame-layout"){try{return FrameTextTests.Layout(args[1]);}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Length==2&&args[0]=="--canvas-animator-lifetime"){try{return FrameTextTests.AnimatorLifetime(args[1]);}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Contains("--material-state")){try{return MaterialStateTests.Run();}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Length==2&&args[0]=="--effect-sub-emitters"){try{return EffectParticleTests.SubEmitters(args[1]);}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Contains("--effect-mesh-materials")){try{return EffectSpriteTests.MeshMaterials();}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Contains("--effect-sorting")){try{return EffectSpriteTests.Sorting();}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
if(args.Length==2&&args[0]=="--crilips")return CriLipsTests.Run(args[1]);
if(args.Length==2&&args[0]=="--tmp")return TmpTextTests.Run(args[1]);
if(args.Length==2&&args[0]=="--effect-particles")return EffectParticleTests.Run(args[1]);
if(args.Length==2&&args[0]=="--effect-world")return EffectParticleTests.World(args[1]);
if(args.Length==2&&args[0]=="--effect-particle-meshes")return EffectParticleTests.Meshes(args[1]);
if(args.Length==2&&args[0]=="--effect-instance")return EffectParticleTests.Instance(args[1]);
if(args.Length==2&&args[0]=="--effect-groups")return EffectParticleTests.Groups(args[1]);
if(args.Length==2&&args[0]=="--effect-group-scene")return EffectParticleTests.GroupScene(args[1]);
if(args.Length==2&&args[0]=="--effect-sprites")return EffectSpriteTests.Run(args[1]);
if(args.Length==2&&args[0]=="--effect-animation")return EffectSpriteTests.Animation(args[1]);
if(args.Length==2&&args[0]=="--effect-sprite-preview")return EffectSpriteTests.Preview(args[1]);
if(args.Length==2&&args[0]=="--effect-gpu")return EffectSpriteTests.Compare(args[1]);
if(args.Length==2&&args[0]=="--effect-character-gpu")return EffectSpriteTests.CompareStory(args[1],false);
if(args.Length==2&&args[0]=="--effect-dim-gpu")return EffectSpriteTests.CompareStory(args[1],true);
if(args.Length==2&&args[0]=="--chat-preview")return ChatCanvasTests.Run(args[1]);
if(args.Length==2&&args[0]=="--chat-commands")return ChatCommandTests.Run(args[1]);
if(args.Length==2&&args[0]=="--ui-gradient")return UiGradientTests.Run(args[1]);

var root = Path.Combine(Path.GetTempPath(), "ournotes-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "index"));
Directory.CreateDirectory(Path.Combine(root, "sites", "intl", "assets"));
File.WriteAllText(Path.Combine(root, "index", "stats.json"), "{}");
var archive = new Archive(root); var passed = 0;
void Assert(bool ok, string name) { if (!ok) throw new Exception(name); passed++; }
void Reject(Action action, string name)
{
    try { action(); } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException) { passed++; return; }
    throw new Exception("Expected rejection: " + name);
}
JsonObject Put(string text, string encoding)
{
    var bytes = Encoding.UTF8.GetBytes(text); var path = "assets/" + Archive.Hash(bytes) + ".json" + (encoding == "" ? "" : "." + encoding);
    using var output = new MemoryStream();
    if (encoding == "") output.Write(bytes);
    else { using Stream zip = encoding == "gz" ? new GZipStream(output, CompressionLevel.Optimal, true) : new BrotliStream(output, CompressionLevel.Optimal, true); zip.Write(bytes); }
    File.WriteAllBytes(archive.SitePath("intl", path), output.ToArray());
    return new JsonObject { ["asset"] = path, ["size"] = bytes.Length, ["stored"] = output.Length };
}
try
{
    JsonOutputTests.Run(Assert);
    await ArchiveIndexTests.RunAsync(Assert);
    await ArchiveDiscoveryTests.RunAsync(Assert);
    ArchiveTransactionTests.Run(Assert);
    {
        var loop=new AdvLoop();var tweens=new AdvTweens(loop);
        var owner=new AdvCanvasNode(JsonNode.Parse("""{"path":"owner","active":true}""")!,null);
        var target=new AdvCanvasNode(JsonNode.Parse("""{"path":"target","active":true,"components":[{"class":"CanvasGroup","m_Alpha":1},{"class":"Image","m_Color":{"r":1,"g":1,"b":1,"a":1}}]}""")!,null);
        var nodes=new Dictionary<string,AdvCanvasNode>{{owner.Path,owner},{target.Path,target}};
        void Start(string json)=>AdvStillProperties.Start(nodes,owner,JsonNode.Parse(json)!,tweens,1,AdvEase.Linear);
        Start("""{"animationType":1,"targetType":5,"target":{"transform":"target"},"endValueV3":{"x":60,"y":-30,"z":9}}""");
        Start("""{"animationType":21,"targetType":5,"target":{"gameObject":"target"},"endValueV2":{"x":300,"y":180}}""");
        Start("""{"animationType":6,"targetType":3,"target":{"gameObject":"target"},"endValueColor":{"r":0,"g":0.5,"b":1,"a":0.2}}""");
        Start("""{"animationType":7,"targetType":2,"target":{"gameObject":"target"},"endValueFloat":0}""");
        for(int i=0;i<15;i++)loop.Step();
        Assert(Math.Abs(target.AnchoredPosition.X-30)<.001f&&Math.Abs(target.AnchoredPosition.Y+15)<.001f&&Math.Abs(target.LocalZ-4.5f)<.001f,"still anchored XYZ midpoint");
        Assert(Math.Abs(target.SizeDelta.X-150)<.001f&&Math.Abs(target.SizeDelta.Y-90)<.001f,"still size midpoint");
        Assert(Math.Abs(target.Image!["m_Color"]!["r"]!.GetValue<float>()-.5f)<.001f&&Math.Abs(target.Image["m_Color"]!["a"]!.GetValue<float>()-.6f)<.001f,"still RGBA midpoint");
        Assert(Math.Abs(target.Alpha-.5f)<.001f&&owner.AnchoredPosition==System.Numerics.Vector2.Zero,"still explicit target and group fade");
        for(int i=0;i<16;i++)loop.Step();
        Start("""{"animationType":7,"targetType":3,"target":{"gameObject":"target"},"endValueFloat":1}""");
        for(int i=0;i<31;i++)loop.Step();
        Assert(target.Image!["m_Color"]!["a"]!.GetValue<float>()==1&&target.Image["m_Color"]!["r"]!.GetValue<float>()==0,"still graphic fade preserves RGB");
        target.Reparent(owner);
        owner.Rotation=System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ,MathF.PI/2);
        target.Rotation=System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ,170*MathF.PI/180);
        Start("""{"animationType":3,"targetType":11,"target":{"transform":"target"},"endValueV3":{"x":0,"y":0,"z":10}}""");
        for(int i=0;i<15;i++)loop.Step();
        var direction=System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX,owner.Rotation*target.Rotation);
        Assert(Math.Abs(direction.X-MathF.Sqrt(.5f))<.0001f&&Math.Abs(direction.Y+MathF.Sqrt(.5f))<.0001f,"still world rotation takes shortest arc under rotated parent");
        for(int i=0;i<16;i++)loop.Step();
        direction=System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX,owner.Rotation*target.Rotation);
        Assert(Math.Abs(direction.X-MathF.Cos(10*MathF.PI/180))<.0001f&&Math.Abs(direction.Y-MathF.Sin(10*MathF.PI/180))<.0001f,"still rotation reaches world destination");
    }
    foreach (var encoding in new[] { "", "gz", "br" })
    {
        var entry = Put("{\"value\":1e999,\"text\":\"幸福\\n萤\"}", encoding);
        Assert(Encoding.UTF8.GetString(archive.Materialize("intl", entry)).Contains("1e999"), encoding + " raw numeric preservation");
        var badSize = entry.DeepClone(); badSize["size"] = 1;
        Reject(() => archive.Materialize("intl", badSize), "decoded size");
        var badStored = entry.DeepClone(); badStored["stored"] = 1;
        Reject(() => archive.Materialize("intl", badStored), "stored size");
    }
    var first = Put("1e999", "gz"); var second = Put("[1,2,3]", "br");
    JsonArray Part(string key, JsonNode e) => new(key, e["asset"]!.DeepClone(), e["size"]!.DeepClone(), e["stored"]!.DeepClone());
    var split = new JsonObject { ["parts"] = new JsonArray(Part("infinity", first), Part("array", second)) };
    Assert(Encoding.UTF8.GetString(archive.Materialize("intl", split)) == "{\"infinity\":1e999,\"array\":[1,2,3]}", "split ordered raw values");
    split["parts"]!.AsArray().Add(Part("array", second));
    Reject(() => archive.Materialize("intl", split), "duplicate split keys");
    Reject(() => archive.SitePath("intl", "../outside"), "path traversal");
    Reject(() => archive.SitePath("../intl", "anything"), "site traversal");
    var corrupt = Put("original", "");
    File.WriteAllText(archive.SitePath("intl", Archive.S(corrupt, "asset")), "tampered");
    Reject(() => archive.Decode("intl", Archive.S(corrupt, "asset")), "hash mismatch");
    var output = Path.Combine(root, "export.bin");
    archive.Export("intl", [1, 2], output, new { test = true }, false);
    Reject(() => archive.Export("intl", [3], output, new { test = true }, false), "overwrite guard");
    Assert(File.ReadAllBytes(output).SequenceEqual(new byte[] { 1, 2 }), "failed export preserves previous output");
    var pixels=new Pixels(2,2,[255,0,0,255,0,255,0,128,0,0,255,0,1,2,3,255]);
    var decoded=Png.Decode(Png.Encode(pixels));
    Assert(decoded.Width==2&&decoded.Height==2&&decoded.Rgba.SequenceEqual(pixels.Rgba),"PNG RGBA/alpha round trip");
    var shot=new Shot{Actors=[new(){Model="test"}],Tracks=[new(){Target="camera",Property="zoom",Interpolation="linear",Keys=[new(){T=0,Value=1},new(){T=2,Value=3}]}]};
    shot.Validate();Assert(shot.Value("camera","zoom",1,0)==2,"Linear camera interpolation");
    shot.Tracks[0].Interpolation="step";Assert(shot.Value("camera","zoom",1,0)==1,"Step camera interpolation");
    shot.Tracks[0].Interpolation="smoothstep";Assert(shot.Value("camera","zoom",1,0)==2,"Smooth camera interpolation");
    shot.Fps=60;Reject(shot.Validate,"Unsupported simulation fps");shot.Fps=30;
    shot.Tracks[0].Keys[1].T=0;Reject(shot.Validate,"Duplicate key time");
    Directory.CreateDirectory(archive.SitePath("intl", "stories"));
    var episode = JsonNode.Parse("""
    {"commands":[
      {"i":0,"cmd":"Talk","AdvTextID":"ignored","IgnoreData":1},
      {"i":1,"cmd":"Talk","AdvTextID":"a","TargetName":"hotaru","VoiceIDs":[123],"lines":{"simplifiedChinese":"<color=red>你好</color>"}},
      {"i":2,"cmd":"Talk","AdvTextID":"0"},
      {"i":3,"cmd":"Talk","AdvTextID":"b","lines":{"simplifiedChinese":"无声\n\n第二行"}}
    ]}
    """)!;
    void SaveEpisode() => File.WriteAllText(archive.SitePath("intl","stories/1.json"),new JsonObject { ["files"] = new JsonObject { ["episode.json"] = Put(episode.ToJsonString(),"gz"), ["audio/voice.m4a"] = new JsonObject{["asset"]="assets/"+new string('a',64)+".m4a"} } }.ToJsonString());
    File.WriteAllText(Path.Combine(root,"index","dialogue.jsonl"),"""
    {"site":"intl","advId":1,"commandIndex":1,"voices":[{"soundId":123,"logicalPath":"audio/voice.m4a","asset":"assets/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.m4a","samples":48001,"sampleRate":48000}]}
    """);
    SaveEpisode();
    var story = new Story(archive,"intl","1");
    var plan = story.Plan("zh-Hans",0,50,.4,2);
    Assert(plan.Lines.Count==2&&plan.Lines[0].CommandIndex==1,"Disabled rows and hide-talk commands excluded");
    Assert(plan.Lines[0].ContentFrames==31&&plan.Lines[1].StartFrame==43&&plan.FrameCount==115,"Voice duration rounds up and frame offsets accumulate without drift");
    Assert(plan.Srt().Contains("00:00:01,433")&&!plan.Srt().Contains("<color")&&!plan.Srt().Contains("无声\n\n第二行"),"SRT preserves multiline text without tags or premature cue breaks");
    var clipped=Media.ClipSrt(plan.Srt(),1);
    Assert(clipped.Contains("00:00:01,000")&&!clipped.Contains("第二行"),"Mux clips last subtitle cue and excludes later cues");
    Reject(()=>Media.ClipSrt("bad",1),"Invalid SRT rejected before mux");
    Assert(story.Plan("zh-Hans",1,1,0,2).Lines[0].StartFrame==0,"Selected dialogue range rebases to zero");
    Reject(()=>story.Plan("en",0,1,0,2),"Missing translation fails instead of substituting another language");
    Reject(()=>story.Plan("zh-Hans",100,1,0,2),"Empty dialogue selection rejected");
    Reject(()=>story.Plan("zh-Hans",0,1,double.NaN,2),"Nonfinite timing rejected");
    episode["commands"]!.AsArray().Add(new JsonObject{["i"]=4,["cmd"]="GoTo"});SaveEpisode();
    try { new Story(archive,"intl","1").Plan("zh-Hans",0,1,0,2);throw new Exception("Expected branch rejection"); } catch(NotSupportedException) { passed++; }
    AdvEngineTests.Run(Assert);
    CanvasLayoutTests.Run(Assert);
    Console.WriteLine($"Passed {passed} resolver/export/shot/PNG/story/native-engine checks.");
    return 0;
}
finally { Directory.Delete(root, true); }
