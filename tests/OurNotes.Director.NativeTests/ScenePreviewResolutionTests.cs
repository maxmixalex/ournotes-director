using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class ScenePreviewResolutionTests
{
    public static async Task<int> RunAsync()
    {
        NativeTestGraphics.Configure();
        int checks=0;
        void Check(bool result,string label)
        {if(!result)throw new InvalidOperationException(label);checks++;Console.WriteLine(label);}
        PreviewTextureCacheTests.Run(Check);
        string root=Path.Combine(Path.GetTempPath(),"ournotes-scene-resize-"+Guid.NewGuid().ToString("N"));
        HomeRenderingTests.WriteFixture(root);
        string project=RealtimeSceneFixture.Write(root);
        try
        {
            string core=NativeDependencies.CoreLibrary(baseDirectory:NativeTestGraphics.PackageDirectory);
            string ffmpeg=NativeDependencies.MediaTool("ffmpeg",baseDirectory:NativeTestGraphics.PackageDirectory);
            var source=new ScenePreviewSource(root,project,core,ffmpeg,128,96,3);
            await using var session=new RealtimePreviewSession();
            using var completedLoad=new CancellationTokenSource();
            await session.LoadSceneAsync(source,completedLoad.Token);completedLoad.Cancel();
            await session.SeekAsync(.5);var small=session.LastFrame!;
            Check(small.Frame==15&&small.Initialization is {ConstructorMilliseconds:>=0,TextureDecodes:0},
                "Cancelling a completed foreground load never cancels subsequent native scene execution");
            await session.ResizePreviewAsync(256,192,2);var enlarged=session.LastFrame!;
            Check(enlarged.Pixels.Width==256&&enlarged.Pixels.Height==192&&enlarged.SessionId==small.SessionId
                &&enlarged.Frame==small.Frame&&enlarged.Time==small.Time&&!session.IsPlaying&&enlarged.Targets.SequenceEqual(small.Targets),
                "Scene fullscreen resize retains native time, animation and reference hit geometry");
            Check(enlarged.Performance?.Diagnostics?.MeshUploads==0,
                "Changing a scene presentation canvas does not reload native model meshes");
            var reference=await Task.Run(()=>
            {
                using var renderer=new AdvStoryRenderer(new Archive(root),"intl",core,"43","zh-Hans",256,192,project,ffmpeg,2);
                return renderer.Frame(15,true);
            });
            Check(enlarged.Pixels.Rgba.SequenceEqual(reference.Rgba),
                "A resized scene equals real high-resolution native rendering rather than enlarged low-resolution pixels");
            await session.ResizePreviewAsync(128,96,1);
            Check(session.LastFrame!.Pixels.Rgba.SequenceEqual(small.Pixels.Rgba),
                "Restoring scene presentation dimensions restores exact paused native pixels");
            await session.SetScenePreviewScaleAsync(.25);await session.SeekAsync(.5);
            await session.SetScenePreviewScaleAsync(1);await session.ResizePreviewAsync(128,96,1);
            Check(session.LastFrame!.ScenePreviewScale==1&&session.LastFrame.Pixels.Rgba.SequenceEqual(small.Pixels.Rgba),
                "A same-size resize redraws full quality when the displayed frame still uses an interaction budget");
            await session.ResizePreviewAsync(256,192,2);var beforeCommit=session.LastFrame!;
            var document=DirectorProject.Document(project);document["text"]=new JsonObject{["unused"]="saved metadata"};
            File.WriteAllText(project,document.ToJsonString());
            await session.CommitSceneProjectAsync(source);
            Check(ReferenceEquals(beforeCommit,session.LastFrame),
                "Saving an edited project retains the current fullscreen canvas until native replay is requested");
            await session.SeekAsync(.5);
            Check(session.LastFrame!.Pixels.Width==256&&session.LastFrame.Pixels.Height==192
                &&session.LastFrame.SessionId!=beforeCommit.SessionId&&session.LastFrame.Pixels.Rgba.SequenceEqual(beforeCommit.Pixels.Rgba),
                "Replaying a saved document preserves the live presentation dimensions and original native reference viewport");
            await session.SeekAsync(.8);var beforeCancellation=session.LastFrame!;
            int started=0,failures=0;
            void Failed(object? sender,Exception error)=>Interlocked.Increment(ref failures);
            session.Failed+=Failed;
            using var cancellation=new CancellationTokenSource();
            session.SceneReconstructionStarted=()=>{started++;cancellation.Cancel();};
            bool cancelled=false;
            try{await session.SeekAsync(.1,cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
            finally{session.SceneReconstructionStarted=null;session.Failed-=Failed;}
            Check(cancelled&&started==1&&failures==0&&ReferenceEquals(beforeCancellation,session.LastFrame),
                "Cancellation after releasing the previous native context preserves the last real pixels without a renderer failure");
            await session.SetScenePreviewScaleAsync(.25);await session.SeekAsync(beforeCancellation.Time);
            var recovered=session.LastFrame!;
            Check(recovered.Frame==beforeCancellation.Frame&&recovered.SessionId!=beforeCancellation.SessionId
                &&recovered.Pixels.Width==256&&recovered.Pixels.Height==192&&recovered.ScenePreviewScale==.25&&!session.IsPlaying,
                "The next seek rebuilds a cancelled scene from its retained source, dimensions and requested quality");
            await session.SetScenePreviewScaleAsync(1);await session.ResizePreviewAsync(256,192,2);
            Check(session.LastFrame!.Pixels.Rgba.SequenceEqual(beforeCancellation.Pixels.Rgba),
                "Resuming a cancelled reconstruction restores sample-identical native pose and presentation pixels");
            Console.WriteLine($"Passed {checks} native scene resolution, texture cache and reconstruction cancellation checks.");
            return 0;
        }
        finally{Directory.Delete(root,true);}
    }
}
