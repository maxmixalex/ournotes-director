using System.Diagnostics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>Actual OpenGL coverage/pixel checks and opt-in real Cubism work measurements.
/// Timings describe the measured device; no software-GPU result predicts user hardware.</summary>
static class PreviewQualityNativeTests
{
    static double Error(Pixels image,Pixels reference)
    {
        if(image.Width!=reference.Width||image.Height!=reference.Height)throw new ArgumentException("Matching pixel grids are required.");
        double sum=0;
        for(int i=0;i<image.Rgba.Length;i+=4)
        {
            double a=image.Rgba[i+3]/255d,b=reference.Rgba[i+3]/255d;
            for(int c=0;c<3;c++){double difference=image.Rgba[i+c]*a-reference.Rgba[i+c]*b;sum+=difference*difference;}
            double alpha=image.Rgba[i+3]-reference.Rgba[i+3];sum+=alpha*alpha;
        }
        return sum/image.Rgba.Length;
    }

    static int PartialEdges(Pixels image)
    {
        int count=0;
        for(int i=3;i<image.Rgba.Length;i+=4)if(image.Rgba[i] is >0 and <255)count++;
        return count;
    }

    public static int RunGraphics(string directory)
    {
        Directory.CreateDirectory(directory);NativeTestGraphics.Configure();
        var checks=new JsonArray();var report=new JsonObject{["ok"]=false,["checks"]=checks};
        void Check(bool value,string name)
        {
            checks.Add((JsonNode)new JsonObject{["name"]=name,["ok"]=value});
            if(!value)throw new InvalidOperationException(name);
            Console.WriteLine(name);
        }
        try
        {
            const int width=80,height=96;
            var images=new Dictionary<int,Pixels>();
            foreach(int sampling in new[]{1,2,4})
            {
                using var gl=new OffscreenGl(width*sampling,height*sampling);report["device"]=gl.Device;
                uint texture=gl.Upload(new(1,1,[255,255,255,255]));
                gl.Begin([0,0,0,0]);
                gl.Draw(texture,[-.873f,-.764f,.718f,-.613f,-.097f,.846f],[0,0,1,0,.5f,1],[0,1,2],
                    1,[1,1,1,1],[0,0,0,0],[.17f,.48f,.93f,1],0,false,false);
                var direct=gl.Present(width,height,sampling,0,(_,_,_)=>{},true,true);
                Check(gl.PresentationCompositePasses==0,$"{sampling}x complete transparent canvas avoids an intermediate full-frame copy");
                var previous=gl.Present(width,height,sampling,0,(_,_,_)=>{},true);
                Check(direct.Rgba.SequenceEqual(previous.Rgba),$"{sampling}x direct resolve preserves existing exported RGBA bytes");
                images[sampling]=direct;
                File.WriteAllBytes(Path.Combine(directory,$"edge-{sampling}x.png"),Png.Encode(direct));
                // Exercise translucent overlaps and HDR color clamping, not only a
                // white silhouette where an alpha-conversion error could be invisible.
                foreach(float opacity in new[]{.031f,.37f,.5f,.91f})
                {
                    gl.Begin([0,0,0,0]);
                    gl.Draw(texture,[-1,-1,1,-1,1,1,-1,1],[0,0,1,0,1,1,0,1],[0,1,2,0,2,3],opacity,
                        [1,1,1,1],[0,0,0,0],[1.31f,.197f,.683f,1],0,false,false);
                    var optimized=gl.Present(width,height,sampling,0,(_,_,_)=>{},true,true);
                    var reference=gl.Present(width,height,sampling,0,(_,_,_)=>{},true);
                    Check(optimized.Rgba.SequenceEqual(reference.Rgba),$"{sampling}x transparent/HDR resolve matches previous bytes at alpha {opacity}");
                }
            }
            double single=Error(images[1],images[4]),doubleSample=Error(images[2],images[4]);
            report["singleSampleErrorTo4x"]=single;report["doubleSampleErrorTo4x"]=doubleSample;
            report["singleSamplePartialEdges"]=PartialEdges(images[1]);report["doubleSamplePartialEdges"]=PartialEdges(images[2]);
            Check(PartialEdges(images[1])==0&&PartialEdges(images[2])>40,
                "2x sampling produces actual fractional geometric edge coverage");
            Check(doubleSample<single,"2x native edge image is closer to the 4x reference than the old 1x image");
            report["ok"]=true;return 0;
        }
        catch(Exception error){report["exception"]=error.ToString();throw;}
        finally{File.WriteAllText(Path.Combine(directory,"preview-quality-graphics.json"),report.ToJsonString(new(){WriteIndented=true}));}
    }

    public static async Task RunAsync(ScenePreviewSource source,string actorId,string directory,Action<bool,string> check)
    {
        RunGraphics(directory);
        var report=new JsonObject{["ok"]=false,["timingScope"]="Measured native device only; software results are not hardware performance claims."};
        try
        {
            await Task.Run(()=>
            {
                var project=DirectorProject.Read(source.ProjectPath);
                using var renderer=new AdvStoryRenderer(new Archive(source.ArchivePath),project.Site,source.CubismPath,
                    project.Adv,project.Language,160,240,source.ProjectPath,source.FfmpegPath,1);
                renderer.SetIsolatedTransform(new(.5,.5,.9,7.3,1));
                var baseline=renderer.Frame(30,true);var actor=renderer.Scene.Character(actorId)!;
                var parameters=actor.PreviewParameters();var animation=actor.PreviewAnimation();var nativeCore=actor.Core;
                var before=actor.DrawingWork;var samples=new JsonArray();
                for(int i=0;i<16;i++)
                {
                    renderer.SetIsolatedTransform(new(.5+i*.002,.5,.9,7.3+i*.2,1));
                    long started=Stopwatch.GetTimestamp();_ = renderer.Frame(30,true);
                    samples.Add((JsonNode)JsonValue.Create(Stopwatch.GetElapsedTime(started).TotalMilliseconds)!);
                }
                var after=actor.DrawingWork;
                check(after.PoseRefreshes==before.PoseRefreshes&&after.MaskPasses==before.MaskPasses
                    &&after.ModelDraws-before.ModelDraws==16,
                    "Sixteen paused transforms reuse evaluated Cubism vertices and the model mask atlas while drawing every new position");
                report["device"]=renderer.Device;report["pausedTransformMilliseconds"]=samples;
                report["pausedTransformPoseRefreshes"]=after.PoseRefreshes-before.PoseRefreshes;
                report["pausedTransformMaskPasses"]=after.MaskPasses-before.MaskPasses;
                report["pausedTransformModelDraws"]=after.ModelDraws-before.ModelDraws;
                renderer.SetIsolatedTransform(new(.5,.5,.9,7.3,1));
                renderer.ResizePreview(160,240,2);var antialiased=renderer.Frame(30,true);
                renderer.ResizePreview(160,240,4);var reference=renderer.Frame(30,true);
                renderer.ResizePreview(160,240,1);var restored=renderer.Frame(30,true);
                check(ReferenceEquals(nativeCore,renderer.Scene.Character(actorId)!.Core)&&renderer.Loop.Frame==30
                    &&actor.PreviewAnimation()==animation&&actor.PreviewParameters().SequenceEqual(parameters)
                    &&restored.Rgba.SequenceEqual(baseline.Rgba),
                    "Native preview sampling can resize 1x to 2x to 4x and back without changing model, pose, history or original pixels");
                check(actor.DrawingWork.MaskPasses==before.MaskPasses&&renderer.PresentationCompositePasses==0,
                    "Isolation resize reuses the same native masks and avoids all intermediate presentation copies");
                double error1=Error(baseline,reference),error2=Error(antialiased,reference);
                report["model1xErrorTo4x"]=error1;report["model2xErrorTo4x"]=error2;
                check(error2<error1,"2x rendering of the actual textured Cubism model improves pixel accuracy against its same-pose 4x reference");
                File.WriteAllBytes(Path.Combine(directory,"model-quality-1x.png"),Png.Encode(baseline));
                File.WriteAllBytes(Path.Combine(directory,"model-quality-2x.png"),Png.Encode(antialiased));
                File.WriteAllBytes(Path.Combine(directory,"model-quality-4x-reference.png"),Png.Encode(reference));
                var maskAtRest=actor.DrawingWork;
                _ = renderer.Frame(31,true);
                check(actor.DrawingWork.PoseRefreshes>maskAtRest.PoseRefreshes&&actor.DrawingWork.MaskPasses>maskAtRest.MaskPasses,
                    "Advancing native animation invalidates the paused-pose and mask reuse");
            });
            await using var session=new RealtimePreviewSession();
            await session.LoadSceneAsync(source with{Width=160,Height=240,Supersample=2});await session.SeekAsync(1);
            var beforeResize=session.LastFrame!;var beforeParameters=await session.ReadActorParametersAsync(actorId);
            await session.ResizePreviewAsync(320,480,1);
            var resized=session.LastFrame!;
            check(resized.SessionId==beforeResize.SessionId&&resized.Frame==beforeResize.Frame
                &&resized.Pixels.Width==320&&resized.Pixels.Height==480
                &&resized.ActorAnimations!.SequenceEqual(beforeResize.ActorAnimations!)
                &&beforeParameters.SequenceEqual(await session.ReadActorParametersAsync(actorId)),
                "Session resize changes the actual presentation pixel grid while preserving current parameters, time and native session identity");
            var transform=new IsolatedActorTransform(.57,.46,.85,3,1);
            await session.SetIsolatedTransformAsync(transform);
            var saved=DirectorProject.Document(source.ProjectPath).DeepClone().AsObject();
            saved["output"]!["isolateTransform"]=ActorIsolation.TransformJson(transform);
            string editedPath=Path.Combine(directory,"quality-live-edits.project.json");
            File.WriteAllText(editedPath,saved.ToJsonString());
            await session.CommitSceneProjectAsync(source with{ProjectPath=editedPath});
            var advanced=new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe(object? sender,PreviewFrame value){if(value.Frame>resized.Frame)advanced.TrySetResult(value);}
            session.FrameReady+=Observe;
            try
            {
                session.Play((resized.Frame+2)/30d);
                var played=await advanced.Task.WaitAsync(TimeSpan.FromSeconds(15));
                check(played.SessionId==beforeResize.SessionId,
                    "Saving a presentation-only drag and immediately playing retains the loaded native model and performance");
            }
            finally{session.Pause();await session.SynchronizeAsync();session.FrameReady-=Observe;}
            var beforeEdit=session.LastFrame!;long sequence=beforeEdit.Sequence;
            var control=new JsonObject{["cmd"]="Director.Actor",["TargetName"]=actorId,
                ["Values"]=new JsonObject{["controls"]=new JsonObject{["physics"]=false}},["Duration"]=0,["AtFrame"]=beforeEdit.Frame};
            await session.ApplySceneEditsAsync([control],transform);
            check(session.LastFrame!.Sequence==sequence+1&&session.LastFrame.Frame==beforeEdit.Frame,
                "A combined native controller and transform edit publishes exactly one new frame");
            await session.ResizePreviewAsync(160,240,2);
            check(session.LastFrame!.SessionId==beforeResize.SessionId&&session.LastFrame.Pixels.Rgba.Length==160*240*4,
                "Repeated session resizing retains a bounded final-size CPU pixel transfer");
            var parameter=beforeParameters.First(value=>value.Maximum>value.Minimum);
            var pin=new JsonObject{["cmd"]="Director.Parameter",["TargetName"]=actorId,["Parameter"]=parameter.Id,
                ["Value"]=parameter.Maximum,["Duration"]=0,["AtFrame"]=beforeEdit.Frame};
            await session.ApplySceneEditsAsync([pin]);
            saved["commands"]!.AsArray().Insert(2,control.DeepClone());saved["commands"]!.AsArray().Insert(3,pin.DeepClone());
            File.WriteAllText(editedPath,saved.ToJsonString());
            await session.CommitSceneProjectAsync(source with{ProjectPath=editedPath});
            transform=transform with{X=.59};await session.SetIsolatedTransformAsync(transform);
            saved["output"]!["isolateTransform"]=ActorIsolation.TransformJson(transform);
            File.WriteAllText(editedPath,saved.ToJsonString());
            await session.CommitSceneProjectAsync(source with{ProjectPath=editedPath});
            await session.SeekAsync(beforeEdit.Frame/30d);
            var replayed=(await session.ReadActorParametersAsync(actorId)).Single(value=>value.Id==parameter.Id);
            check(session.LastFrame!.SessionId!=beforeResize.SessionId&&replayed.IsOverridden
                &&Math.Abs(replayed.Value-parameter.Maximum)<1e-5,
                "A later presentation-only commit preserves pending replay of earlier parameter and controller history changes");
            report["ok"]=true;
        }
        catch(Exception error){report["exception"]=error.ToString();throw;}
        finally{File.WriteAllText(Path.Combine(directory,"preview-quality-model.json"),report.ToJsonString(new(){WriteIndented=true}));}
    }
}
