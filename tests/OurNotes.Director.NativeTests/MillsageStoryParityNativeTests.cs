using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>The original 10060 command stream: underwater canvas transforms and
/// Kanata's anonymous entrance after Hotaru changes slots without an Out.</summary>
static class MillsageStoryParityNativeTests
{
    const string Underwater="adv_frame_underwater/adv_frame_underwater";
    const string Kirakira="adv_frame_kirakira_1/adv_frame_kirakira_1";
    const string Upstream="a18de5b5e0b3e1fb3fe185008154056f3e7cbce3";
    public static async Task<int> RunAsync(string fixture,string output)
    {
        fixture=Path.GetFullPath(fixture);output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var checks=new JsonArray();var samples=new JsonArray();var commands=new JsonArray();
        var report=new JsonObject{["ok"]=false,["story"]="10060",["upstreamCommit"]=Upstream,
            ["checks"]=checks,["samples"]=samples,["commands"]=commands};
        void Check(bool ok,string name){checks.Add((JsonNode)new JsonObject{["name"]=name,["ok"]=ok});if(!ok)throw new InvalidOperationException(name);Console.WriteLine(name);}
        try
        {
            NativeTestGraphics.Configure();
            await Task.Run(()=>
            {
                var archive=new Archive(fixture);string manifestPath=archive.SitePath("intl","stories/10060.json");
                string manifestBefore=Hash(File.ReadAllBytes(manifestPath));
                var manifest=archive.Manifest("intl","stories","10060");var definition=JsonNode.Parse(archive.Logical("intl",manifest,"story.json"))!;
                var episode=JsonNode.Parse(archive.Logical("intl",manifest,Archive.S(definition,"episode")))!;
                var frameDocument=JsonNode.Parse(archive.Logical("intl",manifest,Archive.S(definition,"frames")))!;
                var rows=episode["commands"]!.AsArray().OfType<JsonNode>().ToArray();
                JsonNode Row(int i)=>rows.Single(r=>Actor.N(r["i"])==i);
                Check(Archive.S(Row(2),"TargetName")=="kanata"&&Archive.S(Row(2),"TargetAssetName").EndsWith("/adv_live2d_sub_kanata_casual_spring_01",StringComparison.Ordinal),
                    "Original Character row 2 resolves Kanata's casual spring model");
                Check(new[]{370,386,398}.Select(i=>(int)Actor.N(Row(i)["PositionType"])).SequenceEqual(new[]{5,1,9})
                    &&rows.Where(r=>Actor.N(r["i"])>370&&Actor.N(r["i"])<435).All(r=>Archive.S(r,"cmd")!="Out"),
                    "Original Hotaru sequence moves 5 to 1 to 9 without Out before Kanata's entrance");
                Check(Archive.S(Row(435),"cmd")=="In"&&Archive.S(Row(435),"TargetName")=="kanata"&&Actor.N(Row(435)["PositionType"])==5
                    &&Archive.S(Row(437),"TargetName")=="kanata"&&Actor.N(Row(437)["TargetStatus"])==1,
                    "Original rows 435 and 437 enter Kanata at slot 5 and intentionally display an anonymous name");
                CheckUnderwaterNodes(frameDocument["frames"]![Underwater]!,Check,report);
                string core=NativeDependencies.CoreLibrary(baseDirectory:NativeTestGraphics.PackageDirectory);
                string ffmpeg=NativeDependencies.MediaTool("ffmpeg",baseDirectory:NativeTestGraphics.PackageDirectory);
                using var renderer=new AdvStoryRenderer(archive,"intl",core,"10060","zh-Hans",960,540,null,ffmpeg,1);
                int kanataIn=-1;AdvRow? pendingKanataIn=null;var kanataPlacements=new JsonArray();
                renderer.Player.OnCommand=(row,frame)=>
                {
                    if(pendingKanataIn is {} entrance)
                    {
                        int slot=(int)entrance.Number("PositionType");var placements=renderer.Scene.StageActors.Where(a=>a.Id=="kanata").ToArray();
                        Check(placements.Length==1&&placements[0].Slot==slot&&ReferenceEquals(renderer.Scene.Character("kanata")!.Prefab.Root.Parent,renderer.Scene.Slot(slot)),
                            $"Original Kanata In row {entrance.Index} leaves exactly one model in requested slot {slot}");
                        kanataPlacements.Add((JsonNode)new JsonObject{["row"]=entrance.Index,["slot"]=slot,["observedAtFrame"]=frame});pendingKanataIn=null;
                    }
                    if(row.Command is "In" or "Out" or "Frame")commands.Add((JsonNode)new JsonObject{["frame"]=frame,["row"]=row.Data.DeepClone()});
                    if(row.Index=="435")
                    {
                        kanataIn=frame;var actors=renderer.Scene.StageActors;
                        Check(actors.Count(a=>a.Id=="hotaru")==1&&actors.Single(a=>a.Id=="hotaru").Slot==9
                            &&actors.All(a=>a.Slot!=5&&a.Slot!=1),
                            "Before original Kanata In, moved Hotaru has one renderer entry at slot 9 and leaves slots 5 and 1 empty");
                        Check(actors.All(a=>ReferenceEquals(renderer.Scene.Character(a.Id)!.Prefab.Root.Parent,renderer.Scene.Slot(a.Slot))),
                            "Every registered stage actor agrees with its actual native transform parent");
                    }
                    if(row.Command=="In"&&row.Text("TargetName")=="kanata")pendingKanataIn=row;
                };
                Pixels Sample(int frame,string file)
                {
                    var pixels=renderer.Frame(frame);Check(renderer.Loop.Frame==frame&&!renderer.Player.Finished,$"Original story reaches frame {frame} without a substituted script");
                    File.WriteAllBytes(Path.Combine(output,file),Png.Encode(pixels));
                    samples.Add((JsonNode)new JsonObject{["frame"]=frame,["file"]=file,["rgbaSha256"]=Hash(pixels.Rgba),
                        ["commandIndex"]=renderer.Player.ListIndex,["speaker"]=renderer.Ui.Speaker,["frames"]=JsonOutput.SerializeToNode(renderer.Frames.Snapshot())});
                    return pixels;
                }
                JsonNode FrameState(string name)=>JsonOutput.SerializeToNode(renderer.Frames.Snapshot())!["items"]!.AsArray().Single(n=>Archive.S(n!,"name")==name)!;
                Sample(1000,"millsage-kirakira-frame-1000.png");
                var sparkles=FrameState(Kirakira);
                Check(Actor.B(sparkles["active"])&&sparkles["particles"]!["systems"]!.AsArray().Any(s=>Actor.N(s!["count"])>0),
                    "Original early kirakira frame retains live particle systems after the white fade opens");
                Sample(10000,"millsage-underwater-frame-10000.png");
                var underwater=FrameState(Underwater);var bubbles=underwater["particles"]!["systems"]!.AsArray();
                Check(Actor.B(underwater["active"])&&bubbles.Count==2&&bubbles.All(s=>Actor.N(s!["count"])>0),
                    "Original underwater sequence renders both full-size and small bubble systems");
                Check(bubbles.All(s=>Math.Abs(Actor.N(s!["canvasOrigin"]![0])-418.5233282199806)<.001
                    &&Math.Abs(Actor.N(s["canvasOrigin"]![1])+750.461424517208)<.001),
                    "Actual underwater particle hosts use the upstream canvas origins, not the old half-canvas offset");
                var kanataPixels=Sample(16058,"millsage-kanata-frame-16058.png");
                Check(kanataIn==16028,"Original anonymous Kanata entrance occurs at frame 16028 (08:54.267)");
                var kanata=renderer.Scene.Character("kanata")!;
                Check(kanata.Spec.Model=="adv_live2d_sub_kanata_casual_spring_01"&&renderer.Scene.StageActors.Count(a=>a.Id=="kanata")==1
                    &&renderer.Scene.StageActors.Single(a=>a.Id=="kanata").Slot==5&&ReferenceEquals(kanata.Prefab.Root.Parent,renderer.Scene.Slot(5)),
                    "Kanata's original native model is loaded, entered once and attached to slot 5");
                var target=renderer.PreviewTargets().SingleOrDefault(t=>t.Id=="kanata");
                Check(target is {Width:>0,Height:>0}&&target.X<1&&target.Y<1&&target.X+target.Width>0&&target.Y+target.Height>0,
                    "Kanata has evaluated native geometry inside the visible program monitor");
                Check(renderer.Ui.Speaker==renderer.Ui.Localize("11000"),"The original ??? speaker label remains anonymous while Kanata is visible");
                Check(renderer.Scene.StageActors.Single(a=>a.Id=="kanata").Motion=="mtn_angry01_C",
                    "Kanata executes the original entrance motion from row 436");
                Pixels withoutKanata;bool active=kanata.Prefab.Root.Active;
                try{kanata.Prefab.Root.Active=false;withoutKanata=renderer.Frame(16058);}finally{kanata.Prefab.Root.Active=active;}
                int changed=ChangedPixels(kanataPixels,withoutKanata);
                Check(changed>1000,"Kanata contributes visible native pixels at her anonymous entrance");
                Check(renderer.Frame(16058).Rgba.AsSpan().SequenceEqual(kanataPixels.Rgba),
                    "Restoring Kanata at the same native frame restores identical story pixels");
                report["kanataChangedPixels"]=changed;report["kanataInFrame"]=kanataIn;
                Sample(16508,"millsage-kanata-frame-16508.png");
                Check(renderer.Scene.StageActors.Count(a=>a.Id=="kanata")==1,"Kanata remains visible for the later original anonymous dialogue");
                // Continue the same public renderer to the original end. This
                // covers later 5 -> 1 -> 7 moves and the repeated frame toggles,
                // without exporting or reading every intermediate video frame.
                _=renderer.Frame(60000,stopAtEnd:true);
                Check(renderer.Player.Finished&&renderer.Loop.Frame<60000,"The complete unmodified original episode reaches its native end");
                Check(kanataPlacements.Count==rows.Count(r=>Archive.S(r,"cmd")=="In"&&Archive.S(r,"TargetName")=="kanata")
                    &&kanataPlacements.Count==7&&pendingKanataIn is null,
                    "All seven original Kanata entrances, moves and re-entries retain a visible native model");
                var finalActors=renderer.Scene.StageActors;
                Check(finalActors.Select(a=>a.Id).Distinct().Count()==finalActors.Count
                    &&finalActors.All(a=>ReferenceEquals(renderer.Scene.Character(a.Id)!.Prefab.Root.Parent,renderer.Scene.Slot(a.Slot))),
                    "The full episode ends with unique actors and consistent native slot parents");
                report["fullStory"]=new JsonObject{["completionFrame"]=renderer.Player.CompletionFrame,["nativeFrame"]=renderer.Loop.Frame,
                    ["kanataPlacements"]=kanataPlacements,["originalCommandCount"]=rows.Length};
                Check(Hash(File.ReadAllBytes(manifestPath))==manifestBefore,"Original story manifest remains byte-identical after parity tests");
                report["sourceManifestSha256"]=manifestBefore;
            });
            report["ok"]=true;Console.WriteLine($"Passed {checks.Count} original millsage model, frame-effect and upstream-layout checks.");return 0;
        }
        catch(Exception error){report["exception"]=error.ToString();throw;}
        finally{File.WriteAllText(Path.Combine(output,"millsage-story-parity-report.json"),report.ToJsonString(new(){WriteIndented=true}));}
    }

    static void CheckUnderwaterNodes(JsonNode prefab,Action<bool,string> check,JsonObject report)
    {
        // Values measured from upstream Frame(row 249), PlayerLoop(30), the same
        // original UI/prefab, a 960x443 viewport and no GL. These are layout/mesh
        // reference values, not a claim of browser-vs-native pixel identity.
        const float width=960.1666603881731f,height=443.07690682495905f;
        var nodes=new Dictionary<string,AdvCanvasNode>();
        foreach(var rec in prefab["nodes"]!.AsArray().OfType<JsonNode>())
        {
            string path=Archive.S(rec,"path");int split=path.LastIndexOf('/');
            var node=new AdvCanvasNode(rec,split<0?null:nodes[path[..split]]);nodes.Add(path,node);node.Layout(width,height);
        }
        var parent=nodes["adv_frame_underwater/EfSplash"];
        check(!parent.IsRectTransform&&parent.Width==0&&parent.Height==0,
            "Original underwater EfSplash stays a plain Transform with a zero-size child rect");
        check(Vector3.Distance(parent.LayoutPosition,AdvPrefab.Vector(parent.Record["localPosition"]))<.000001f,
            "Original underwater EfSplash retains its serialized local XYZ");
        check(Math.Abs(parent.Matrix[12]-480.08331874999476)<.0002&&Math.Abs(parent.Matrix[13]-221.53843815369046)<.0002,
            "Original underwater Transform origin matches upstream a18de5b layout within 0.0002 canvas units");
        report["upstreamLayoutReference"]=new JsonObject{["commit"]=Upstream,["source"]="src/story/features/canvas.js TransformNode.layoutIn",
            ["scope"]="Original Frame 249 subsystem layout at 960x443; no upstream GL or full-story pixel comparison",
            ["canvasWidth"]=width,["canvasHeight"]=height,["expectedOrigin"]=new JsonArray(480.08331874999476,221.53843815369046),
            ["actualOrigin"]=new JsonArray(parent.Matrix[12],parent.Matrix[13])};
    }
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    static int ChangedPixels(Pixels first,Pixels second)
    {
        int result=0;for(int i=0;i<first.Rgba.Length;i+=4)if(Math.Abs(first.Rgba[i]-second.Rgba[i])>2||Math.Abs(first.Rgba[i+1]-second.Rgba[i+1])>2||Math.Abs(first.Rgba[i+2]-second.Rgba[i+2])>2)result++;return result;
    }
}
