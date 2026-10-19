using System.Diagnostics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

static class EditorPerformanceTests
{
    public static void Run(Action<bool, string> check)
    {
        var native = new JsonObject
        {
            ["format"] = DirectorProject.Format, ["version"] = 1,
            ["source"] = new JsonObject { ["adv"] = "10462", ["site"] = "intl", ["language"] = "zh-Hans" },
            ["output"] = new JsonObject { ["width"] = 1920, ["height"] = 1080 },
            ["initialize"] = new JsonArray(), ["commands"] = new JsonArray(), ["finalize"] = new JsonArray(), ["text"] = new JsonObject()
        };
        var rows = native["commands"]!.AsArray();
        rows.Add(new JsonObject { ["cmd"] = "Character", ["TargetName"] = "actor", ["Model"] = "fixture" });
        var entries = new List<NativeCommandTiming> { new("commands", 0, "Character", 0, 1, 0, "0") };
        for (int i = 1; i <= 600; i++)
        {
            string command = i % 3 == 0 ? "Expression" : "Motion";
            var row = new JsonObject { ["cmd"] = command, ["TargetName"] = "actor", ["MotionName"] = "motion",
                ["ExpressionName"] = "expression", ["AtFrame"] = i, ["ExtraOriginal"] = JsonNode.Parse("1.2300e-7") };
            rows.Add(row); entries.Add(new("commands", i, command, i, i + 1, i, i.ToString()));
        }
        // Repeated source visits and equal ordinals are intentionally not deduped.
        entries.Add(new("commands", 1, "Motion", 603, 604, 601, "repeat"));
        entries.Add(new("commands", 2, "Motion", 603, 604, 601, "equal"));
        entries.Add(new("commands", 3, "Expression", 609, 610, 602, "last"));
        var timing = new NativePlaybackTiming("fixture", true, 620, 620d / 30, entries);
        string untouched = native.ToJsonString();
        var watch = Stopwatch.StartNew(); var legacy = LegacyImport(native, timing); double before = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); var current = CreativeScene.FromPerformance(native, timing); double after = watch.Elapsed.TotalMilliseconds;
        static string WithoutIds(CreativeScene scene)
        {
            var root = scene.Root.DeepClone(); foreach (var clip in root["clips"]!.AsArray().OfType<JsonObject>()) clip.Remove("id");
            return root.ToJsonString();
        }
        check(WithoutIds(current) == WithoutIds(legacy), "Batched story import equals legacy conversion including repeated/equal-ordinal commands, durations and source tokens");
        check(native.ToJsonString() == untouched, "Import never mutates the input project or its numeric tokens");
        check(after < 5000, "600-row conversion finishes below the generous 5-second regression ceiling");
        Console.WriteLine($"STORY_IMPORT_BENCHMARK legacyMs={before:F3} batchedMs={after:F3} clips={current.Clips.Count}");
        var calculated = CreativeClipSpans.Calculate(current);
        check(current.Clips.OfType<JsonObject>().All(clip => calculated[CreativeScene.Text(clip, "id")] == CreativeClipEditing.DisplayEnd(current, clip)),
            "Batched imported spans equal the reference motion/expression end rules");
        var authored = CreativeScene.Blank("intl", "zh-Hans"); string actor = authored.AddActor("model", "Actor");
        var random = new Random(37);
        for (int i = 0; i < 55; i++)
        {
            string kind = i % 3 == 0 ? "expression" : "motion";
            string id = CreativeMaterialEditing.Insert(authored, actor, kind, "asset", "clip", random.Next(15) / 30d, 1);
            authored.Clip(id)["start"] = random.Next(15) / 30d + (i % 2 == 0 ? .01 : .017);
            if (i % 7 == 0) authored.Clip(id)["enabled"] = false;
            if (kind == "motion" && i % 4 == 0) CreativeMaterialEditing.SetKeepsExpression(authored, id, false);
        }
        foreach (bool nativeActor in new[] { false, true })
        {
            authored.Actor(actor)["fromSource"] = nativeActor;
            var spans = CreativeClipSpans.Calculate(authored);
            check(authored.Clips.OfType<JsonObject>().All(clip => spans[CreativeScene.Text(clip, "id")] == CreativeClipEditing.DisplayEnd(authored, clip)),
                "Batched spans preserve fractional frame ties, insertion order, disabled clips and authored/native motion rules");
        }
        check(GpuPreferencePolicy.Merge("AutoHDREnable=1;GpuPreference=1;Foo=x;", 2) == "AutoHDREnable=1;Foo=x;GpuPreference=2;",
            "GPU preference changes only its own token, preserving unrelated graphics options");
        check(GpuPreferencePolicy.Merge("GpuPreference=2;", 0) is null && GpuPreferencePolicy.Merge("foo=a;gpupreference=1;", 0) == "foo=a;",
            "Automatic GPU removes only the per-app override");
        var settings = new DesktopSettings(); GpuPreferencePolicy.Store(settings, 2);
        check(GpuPreferencePolicy.Read(settings) == 2, "GPU preference roundtrips through persisted settings forms");
        check(GpuPreferencePolicy.Executables("dotnet.exe", "advkit.exe").Length == 1,
            "GPU selection never changes shared dotnet.exe host policy");
    }
    static CreativeScene LegacyImport(JsonObject native,NativePlaybackTiming timing)
    {
        if(!timing.IsComplete||timing.EndFrame is null)throw new InvalidOperationException("原剧情尚未解析完成，不能猜测语音时间。");
        var scene=CreativeScene.Blank(native["source"]?["site"]?.ToString()??"intl",native["source"]?["language"]?.ToString()??"zh-Hans");
        scene.Root["source"]=native["source"]!.DeepClone();scene.Root["nativeOutput"]=native["output"]?.DeepClone();scene.Root["imported"]=true;scene.Canvas["content"]=SceneContent.Read(native["output"]?["content"]);
        scene.Root["originalProject"]=native.DeepClone();scene.Root["text"]=native["text"]?.DeepClone();
        scene.Canvas["width"]=native["output"]?["width"]?.DeepClone()??JsonValue.Create(1920);scene.Canvas["height"]=native["output"]?["height"]?.DeepClone()??JsonValue.Create(1080);
        var entries=timing.Commands.OrderBy(t=>t.Ordinal).ToArray();
        foreach(var t in entries)
        {
            if(native[t.Section] is not JsonArray section||t.Index>=section.Count||section[t.Index] is not JsonObject data)continue;
            if(t.Command is "Delay" or "Wait" or "GoTo" or "ForceAuto" or "CancelDelay")continue;
            var row=(JsonObject)data.DeepClone();row["cmd"]=t.Command;row.Remove("AtFrame");
            string actor=CreativeScene.Text(row,"TargetName"),kind=CreativeScene.Kind(t.Command);
            if(t.Command=="Character"&&actor.Length>0&&CreativeScene.Text(row,"Model").Length>0&&!scene.Actors.OfType<JsonObject>().Any(a=>CreativeScene.Text(a,"id")==actor))
                scene.Actors.Add((JsonNode)new JsonObject{["id"]=actor,["name"]=actor,["model"]=CreativeScene.Text(row,"Model"),["fromSource"]=true});
            int end=t.EndFrame??t.StartFrame+1;
            if(kind is "motion" or "expression")
            {
                var next=entries.FirstOrDefault(n=>n.Ordinal>t.Ordinal&&CreativeScene.Kind(n.Command)==kind&&native[n.Section]?[n.Index] is JsonObject r&&CreativeScene.Text(r,"TargetName")==actor);
                end=next?.StartFrame??timing.EndFrame.Value;
            }
            string label=t.Command is "Talk" or "Voice"?DialogueLabel(native,row,scene.Language):CreativeScene.Text(row,"MotionName",CreativeScene.Text(row,"ExpressionName",t.Command));
            string id=scene.AddClip(actor,kind,t.StartFrame/30d,Math.Max(1,end-t.StartFrame)/30d,row,label);
            scene.Clip(id)["sourcePerformance"]=true;
        }
        double tail=(timing.EndFrame.Value+1)/30d, contentEnd=scene.Clips.OfType<JsonObject>().Select(c=>CreativeScene.Number(c,"start")+CreativeScene.Number(c,"duration")).DefaultIfEmpty(0).Max();
        if(tail>contentEnd+0.0001)scene.AddClip("","hold",contentEnd,tail-contentEnd,new JsonObject{["cmd"]="Delay",["Frames"]=0},"结束停留");
        scene.Validate();return scene;
    }
    static string DialogueLabel(JsonObject root,JsonObject row,string language)
    {
        string field=language switch{"ja"=>"japanese","en"=>"english","zh-Hant"=>"traditionalChinese",_=>"simplifiedChinese"};
        return root["text"]?[CreativeScene.Text(row,"AdvTextID")]?[field]?.ToString()??"语音";
    }
}
