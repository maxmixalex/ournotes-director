using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

static class CreativeMaterialTests
{
    public static void Run(Action<bool, string> check)
    {
        var scene = CreativeScene.Blank("intl", "zh-Hans");
        string actor = scene.AddActor("model-a", "A"), other = scene.AddActor("model-b", "B");
        string face = CreativeMaterialEditing.Insert(scene, actor, "expression", "face-happy", "微笑", .2, 3);
        CreativeMaterialEditing.Insert(scene, other, "expression", "face-sad", "难过", .3, 3);
        string motion = CreativeMaterialEditing.Insert(scene, actor, "motion", "motion-a", "动作A", 1, 1);
        JsonObject Motion(CreativeScene source, int at) => source.Compile()["commands"]!.AsArray().OfType<JsonObject>()
            .Single(row => row["cmd"]?.ToString() == "Motion" && row["TargetName"]?.ToString() == actor && row["AtFrame"]!.GetValue<int>() == at);
        check(Motion(scene,30)["ExpressionName"]?.ToString() == "face-happy", "Authored motion passes the preceding expression to the existing native Motion command");
        check(scene.Clip(motion)["data"]?["ExpressionName"] is null, "Compilation never writes a derived expression into the authored source row");
        check(CreativeClipEditing.DisplayEnd(scene,scene.Clip(face)) == scene.Duration, "Kept expression remains visible across an authored motion change");
        string later = CreativeMaterialEditing.Insert(scene,actor,"expression","face-angry","生气",1.5,2);
        check(CreativeClipEditing.DisplayEnd(scene,scene.Clip(face)) == 1.5, "Expression span ends at the next actual expression transition");
        scene.MoveClip(later,.8);
        check(Motion(scene,30)["ExpressionName"]?.ToString() == "face-angry", "Moving an expression before a motion recompiles the correct same-actor facial state");
        scene.Clip(later)["enabled"] = false;
        check(Motion(scene,30)["ExpressionName"]?.ToString() == "face-happy", "Disabled expression does not affect motion inheritance");
        CreativeMaterialEditing.SetKeepsExpression(scene,motion,false);
        check(Motion(scene,30)["ExpressionName"] is null && CreativeClipEditing.DisplayEnd(scene,scene.Clip(face)) == 1,
            "Native mode leaves an omitted ExpressionName omitted and shows the resulting expression reset on the timeline");
        string next = scene.AddClip(actor,"motion",1.2,1,new JsonObject{["cmd"]="Motion",["MotionName"]="original",["ExpressionName"]="face-original",["MotionFadeIn"]=.42,["MotionWait"]=.75,["customExtension"]="untouched"},"Original");
        var original = scene.Clip(next);original["sourcePerformance"] = true;
        double start = CreativeScene.Number(original,"start"),duration = CreativeScene.Number(original,"duration");
        CreativeMaterialEditing.Replace(scene,next,"motion","replacement","New asset");
        check(original["data"]?["MotionName"]?.ToString()=="replacement" && original["data"]?["ExpressionName"]?.ToString()=="face-original"
            && original["data"]?["MotionWait"]!.GetValue<double>()==.75 && original["data"]?["MotionFadeIn"]!.GetValue<double>()==.42
            && original["data"]?["customExtension"]?.ToString()=="untouched" && original["sourcePerformance"]!.GetValue<bool>(),
            "Replacing an imported motion changes the asset without losing its face, wait, fade, unknown fields or original-performance semantics");
        check(CreativeScene.Number(original,"start")==start && CreativeScene.Number(original,"duration")==duration,
            "Replacement keeps the authored timing instead of silently resetting it to a guessed material length");
        string before=scene.ToJson();bool rejected=false;
        try{CreativeMaterialEditing.SetKeepsExpression(scene,next,true);}catch(InvalidOperationException){rejected=true;}
        check(rejected && scene.ToJson()==before,"Queued native motions reject compile-time face inference without modifying the project");
        int count=scene.Clips.Count; rejected=false;
        try{CreativeMaterialEditing.Insert(scene,actor,"motion","another","Another",2,1);}catch(InvalidOperationException){rejected=true;}
        check(rejected&&scene.Clips.Count==count&&scene.ToJson()==before,"Ambiguous delayed native state cannot leave an invalid newly inserted clip in the document");
        var importedCopy=CreativeScene.Parse(before);
        check(importedCopy.Compile().ToJsonString()==scene.Compile().ToJsonString(),"Native expression policy and all preserved replacement fields survive JSON roundtrip");
        rejected=false;
        try{CreativeMaterialEditing.Replace(scene,next,"expression","face-x","wrong kind");}catch(InvalidOperationException){rejected=true;}
        check(rejected&&scene.ToJson()==before,"Wrong-kind replacement cannot destroy a motion row");
        var same=CreativeScene.Blank("intl","zh-Hans");string a=same.AddActor("model","A");
        var m=CreativeMaterialEditing.Insert(same,a,"motion","motion","Motion",0,1);
        var e=CreativeMaterialEditing.Insert(same,a,"expression","smile","Smile",0,1);
        check(same.Compile()["commands"]!.AsArray().OfType<JsonObject>().Where(r=>r["cmd"]?.ToString()=="Motion").Single()["ExpressionName"]?.ToString()=="",
            "A same-frame expression after Motion keeps the existing row order, not a future facial state");
        string again=CreativeMaterialEditing.Insert(same,a,"motion","next","Next",0,1);
        check(same.Compile()["commands"]!.AsArray().OfType<JsonObject>().Where(r=>r["cmd"]?.ToString()=="Motion").Last()["ExpressionName"]?.ToString()=="smile",
            "Same-frame stable ordering lets the next authored Motion preserve the face already set before it");
        scene.Clip(motion)["expressionMode"]="not-supported";rejected=false;
        try{scene.Validate();}catch(ArgumentException){rejected=true;}
        check(rejected,"Unknown expression policies are rejected instead of silently falling back to a different native behavior");
    }
}
