using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class RealtimePreviewTests
{
    public static int WriteFixture(string root){_ = RealtimeSceneFixture.Write(root);return 0;}
    public static int Run()=>RunAsync().GetAwaiter().GetResult();

    static async Task<int> RunAsync()
    {
        int checks=0;
        void Check(bool condition,string label)
        {
            if(!condition)throw new Exception("Realtime preview: "+label);
            checks++;
        }
        string root=Path.Combine(Path.GetTempPath(),"ournotes-realtime-"+Guid.NewGuid().ToString("N"));
        HomeRenderingTests.WriteFixture(root);
        string sceneProject=RealtimeSceneFixture.Write(root);
        var files=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var scene=new HomeCatalog(new Archive(root)).Open(77,"intl",42);
        string actor=scene.SelectCharacter("alice").Key;
        var initial=new HomeEditState{Background=false,Framing="fit"};
        var project=new HomeProject(new HomeProjectSource(77,"intl",42)){Duration=1,EditState=initial};
        var source=new HomePreviewSource(root,project,Width:128,Height:96);
        var session=new RealtimePreviewSession();
        var threads=new ConcurrentDictionary<int,bool>();
        var failures=new ConcurrentQueue<Exception>();
        session.FrameReady+=(_,_)=>threads.TryAdd(Environment.CurrentManagedThreadId,true);
        session.Failed+=(_,exception)=>failures.Enqueue(exception);
        int testThread=Environment.CurrentManagedThreadId;
        try
        {
            await session.LoadHomeAsync(source).WaitAsync(TimeSpan.FromSeconds(30));
            var first=session.LastFrame!;
            Check(first.Pixels.Rgba.Length==128*96*4&&first.Pixels.Rgba.Where((_,i)=>i%4==3).Any(alpha=>alpha>0),
                "loading produces a real nonempty native RGBA image");
            Check(first.Pixels.Rgba[3]==0&&first.Frame==0&&first.Time==0&&!first.IsPlaying,
                "the first home frame keeps transparency and its exact paused timeline position");
            Check(first.Targets.Any(target=>target.Id==actor&&target.Width>0&&target.Height>0),
                "selection targets come from the native character's projected geometry");
            Check(threads.Count==1&&!threads.ContainsKey(testThread),"GL rendering owns one dedicated thread distinct from the caller");

            await session.SeekAsync(.5).WaitAsync(TimeSpan.FromSeconds(10));
            var middle=session.LastFrame!;
            Check(middle.Frame==15&&middle.Time==.5&&!middle.Pixels.Rgba.SequenceEqual(first.Pixels.Rgba),
                "seeking forwards evaluates the actual moving Spine animation");
            Check(middle.SessionId==first.SessionId&&middle.Sequence>first.Sequence,
                "forward updates retain the context and loaded animation session");
            await session.SeekAsync(0).WaitAsync(TimeSpan.FromSeconds(10));
            var replay=session.LastFrame!;
            Check(replay.Pixels.Rgba.SequenceEqual(first.Pixels.Rgba)&&replay.SessionId==first.SessionId,
                "backwards Spine seek is deterministic and retains the same native session");

            var moved=initial with{Characters=new Dictionary<string,HomeCharacterTrack>(StringComparer.Ordinal)
                {[actor]=new(){Transform=new(.125,.05,1)}}};
            await session.SetHomeEditStateAsync(moved).WaitAsync(TimeSpan.FromSeconds(10));
            var dragged=session.LastFrame!;var before=first.Targets.Single(t=>t.Id==actor);var after=dragged.Targets.Single(t=>t.Id==actor);
            Check(dragged.Frame==0&&dragged.SessionId==first.SessionId&&!dragged.Pixels.Rgba.SequenceEqual(first.Pixels.Rgba),
                "dragging redraws real pixels at the current time without restarting the model or context");
            Check(Math.Abs(after.X-before.X-.125)<.00001&&Math.Abs(after.Y-before.Y-.05)<.00001,
                "native target bounds track normalized canvas displacement on both axes");
            await session.SetHomeEditStateAsync(initial).WaitAsync(TimeSpan.FromSeconds(10));
            Check(session.LastFrame!.Pixels.Rgba.SequenceEqual(first.Pixels.Rgba),"releasing a live transform returns to the exact source pixels");

            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            int block=1;
            void BlockFrame(object? sender,PreviewFrame frame)
            {
                if(Interlocked.Exchange(ref block,0)!=1)return;
                entered.Set();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException("Preview coalescing test release timed out.");
            }
            session.FrameReady+=BlockFrame;
            var active=session.SeekAsync(.1);
            Check(entered.Wait(TimeSpan.FromSeconds(10)),"native draw can be observed before its asynchronous request finishes");
            long blockedSequence=session.LastFrame!.Sequence;
            var seeks=Enumerable.Range(0,50).Select(i=>session.SeekAsync(i/60d)).ToArray();
            var finalSeek=session.SeekAsync(.9);release.Set();
            await Task.WhenAll(seeks.Append(active).Append(finalSeek)).WaitAsync(TimeSpan.FromSeconds(10));
            session.FrameReady-=BlockFrame;
            Check(session.LastFrame!.Frame==27&&session.LastFrame.Sequence==blockedSequence+1,
                "rapid pending scrub requests coalesce into one draw of the final requested frame");

            var saved=session.LastFrame!;
            try
            {
                await session.SetHomeEditStateAsync(initial with{Characters=new Dictionary<string,HomeCharacterTrack>{{"missing-character",new()}}});
                Check(false,"unknown characters must fail");
            }
            catch(ArgumentException){Check(true,"invalid live edits return a real native error");}
            Check(ReferenceEquals(session.LastFrame,saved),"a failed draw never replaces the last successful native frame with a blank fallback");
            await session.SetHomeEditStateAsync(initial).WaitAsync(TimeSpan.FromSeconds(10));
            Check(failures.Count==1&&session.LastFrame!.Sequence>saved.Sequence,"the same session can recover after a rejected edit");

            await session.SeekAsync(0);
            var played=new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPlayed(object? sender,PreviewFrame frame)
            {
                if(frame.IsPlaying&&frame.Time>=.6)played.TrySetResult(frame);
            }
            session.FrameReady+=OnPlayed;session.Play();
            var live=await played.Task.WaitAsync(TimeSpan.FromSeconds(10));
            session.Pause();session.FrameReady-=OnPlayed;
            await session.SeekAsync(live.Time);
            Check(live.Frame>=18&&live.FramesPerSecond>0&&live.RenderMilliseconds>0&&live.SessionId==first.SessionId,
                "playback advances the retained native animation and reports measured render performance");
            Check(!session.IsPlaying&&!session.LastFrame!.IsPlaying,"pause preserves the selected timeline frame");
            Check(threads.Count==1,"load, scrub, live edit and playback all stay on the same GL owner thread");
            Check(files.SequenceEqual(Directory.GetFiles(root,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal)),
                "preview creates no disk frames, video proxies or generated media files");

            await CheckSceneAsync(root,sceneProject,session,Check);
            await CheckAutomaticTimingAsync(root,Check);

            await session.DisposeAsync();
            try{await session.SeekAsync(0);Check(false,"disposed session must reject requests");}
            catch(ObjectDisposedException){Check(true,"disposing terminates the render thread and rejects new requests");}
            Console.WriteLine($"Passed {checks} persistent native preview, scrub, live transform, raw frame and playback checks.");
            return 0;
        }
        finally
        {
            await session.DisposeAsync();
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }

    static async Task CheckAutomaticTimingAsync(string root,Action<bool,string> check)
    {
        string path=Path.Combine(root,"studio-auto-timing.json");File.WriteAllText(path,RealtimeSceneFixture.Document().ToJsonString());
        var source=new ScenePreviewSource(root,path,"","",128,96,.1,PreviewDurationMode.Automatic);
        await using var session=new RealtimePreviewSession();
        await session.LoadSceneAsync(source).WaitAsync(TimeSpan.FromSeconds(20));
        long initialSession=session.LastFrame!.SessionId;var first=session.LastFrame.Pixels;
        check(session.LastFrame.Frame==0&&session.LastFrame.DurationMode==PreviewDurationMode.Automatic,
            "automatic scene duration starts with a real first frame instead of seeking the visible renderer to the end");
        var timing=await session.ResolveSceneTimingAsync().WaitAsync(TimeSpan.FromSeconds(20));
        check(timing.IsComplete&&timing.EndFrame==30&&Math.Abs(timing.Duration!.Value-31/30d)<1e-10,
            "automatic duration observes the actual interpreter completion and includes its last export frame");
        check(timing.Commands.Count==3&&timing.Commands.Last().Section=="commands"&&timing.Commands.Last().StartFrame==0&&timing.Commands.Last().EndFrame==30,
            "native scene timing preserves initialization and real tween start/end positions");
        await session.SeekAsync(.5);
        check(session.LastFrame!.Frame==15&&session.LastFrame.SessionId==initialSession&&session.LastFrame.DurationResolved,
            "background duration analysis preserves the live context and replaces the provisional short duration");
        await session.SeekAsync(100);
        check(session.LastFrame!.Frame==30&&session.LastFrame.Timing?.IsComplete==true,
            "automatic seeking clamps at the true last frame without rendering after story completion");
        await session.LoadSceneAsync(source);
        check(session.LastFrame!.Timing?.IsComplete==true&&session.LastFrame.Pixels.Rgba.SequenceEqual(first.Rgba),
            "reloading an unchanged project uses cached measured duration and keeps deterministic first-frame pixels");

        int actualEnd=await Task.Run(()=>
        {
            using var rendered=new AdvStoryRenderer(new Archive(root),"intl","","43","zh-Hans",128,96,path,"",1);
            for(int frame=0;frame<100;frame++){rendered.Frame(frame,true);if(rendered.Player.Finished)return frame;}
            return -1;
        });
        check(actualEnd==timing.EndFrame,"drawing every native frame reaches the same end as duration analysis without frame readbacks");
        var lengthy=RealtimeSceneFixture.Document();lengthy["commands"]=new JsonArray((JsonNode)new JsonObject{["cmd"]="Delay",["Frames"]=30*36000});
        string longPath=Path.Combine(root,"studio-cancel-timing.json");File.WriteAllText(longPath,lengthy.ToJsonString());
        await session.LoadSceneAsync(source with{ProjectPath=longPath});
        var pending=session.ResolveSceneTimingAsync();
        await session.LoadSceneAsync(source with{DurationMode=PreviewDurationMode.Fixed,Duration=1});
        bool cancelled=false;try{await pending.WaitAsync(TimeSpan.FromSeconds(15));}catch(OperationCanceledException){cancelled=true;}
        check(cancelled&&session.LastFrame!.Frame==0&&session.LastFrame.DurationMode==PreviewDurationMode.Fixed,
            "switching projects cancels old native duration analysis without overwriting the new source or pixels");
    }

    static async Task CheckSceneAsync(string root,string projectPath,RealtimePreviewSession home,Action<bool,string> check)
    {
        await using var scene=new RealtimePreviewSession();
        var source=new ScenePreviewSource(root,projectPath,"","",128,96,1);
        await scene.LoadSceneAsync(source).WaitAsync(TimeSpan.FromSeconds(20));
        var first=scene.LastFrame!;
        check(first.Frame==0&&first.Pixels.Rgba.Where((_,index)=>index%4==3).Any(alpha=>alpha>0)&&first.Targets.Count==0,
            "the real ADV interpreter renders a valid transparent scene with no Cubism actors");
        await scene.SeekAsync(.5).WaitAsync(TimeSpan.FromSeconds(10));
        var middle=scene.LastFrame!;
        check(middle.Frame==15&&!middle.Pixels.Rgba.SequenceEqual(first.Pixels.Rgba)&&middle.SessionId==first.SessionId,
            "Director.Camera timeline changes real scene pixels without replacing the live context");
        var homeBefore=home.LastFrame!;
        await home.SeekAsync(.3);
        check(home.LastFrame!.SessionId==homeBefore.SessionId&&home.LastFrame.Frame==9,
            "home and scene contexts coexist on independent rendering threads");
        await scene.ApplySceneEditsAsync([new JsonObject{["cmd"]="Director.Scene",["Values"]=new JsonObject{["grade"]=new JsonObject{["exposure"]=-1}}}]);
        var graded=scene.LastFrame!;
        check(graded.Frame==15&&graded.SessionId==middle.SessionId&&!graded.Pixels.Rgba.SequenceEqual(middle.Pixels.Rgba),
            "immediate scene grading redraws the current native frame without timeline advance");
        var updated=RealtimeSceneFixture.Document();
        updated["initialize"]!.AsArray().Add((JsonNode)new JsonObject{["cmd"]="Director.Scene",["Values"]=new JsonObject{["grade"]=new JsonObject{["exposure"]=-1}}});
        string committed=Path.Combine(root,"studio-scene-committed.json");File.WriteAllText(committed,updated.ToJsonString());
        await scene.CommitSceneProjectAsync(source with{ProjectPath=committed});
        check(ReferenceEquals(scene.LastFrame,graded),"committing a live scene edit preserves the exact frame, time and session immediately");
        File.Delete(projectPath);
        await scene.SeekAsync(.5).WaitAsync(TimeSpan.FromSeconds(10));
        var restored=scene.LastFrame!;
        check(restored.Frame==15&&restored.SessionId>graded.SessionId&&restored.Pixels.Rgba.SequenceEqual(graded.Pixels.Rgba),
            "the next seek replays the committed project after its old snapshot has been released");
        await scene.CommitSceneProjectAsync(source with{ProjectPath=committed});
        await home.SeekAsync(0);
        var scenePlayed=new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var homePlayed=new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void ScenePlayed(object? sender,PreviewFrame frame){if(frame.IsPlaying&&frame.Time>=.7)scenePlayed.TrySetResult(frame);}
        void HomePlayed(object? sender,PreviewFrame frame){if(frame.IsPlaying&&frame.Time>=.2)homePlayed.TrySetResult(frame);}
        scene.FrameReady+=ScenePlayed;home.FrameReady+=HomePlayed;
        try
        {
            home.Play();scene.Play();
            await Task.WhenAll(scenePlayed.Task,homePlayed.Task).WaitAsync(TimeSpan.FromSeconds(10));
            check(scenePlayed.Task.Result.SessionId==restored.SessionId&&scenePlayed.Task.Result.Frame>=21,
                "playback after an unchanged commit preserves the native context while advancing its animation");
            check(homePlayed.Task.Result.SessionId==homeBefore.SessionId&&homePlayed.Task.Result.Frame>=6,
                "both persistent studios render live frames concurrently on their own native contexts");
        }
        finally{scene.Pause();home.Pause();scene.FrameReady-=ScenePlayed;home.FrameReady-=HomePlayed;}
        await scene.SynchronizeAsync();
        updated["finalize"]=new JsonArray((JsonNode)new JsonObject{["cmd"]="Delay",["Frames"]=1});
        File.WriteAllText(committed,updated.ToJsonString());
        await scene.CommitSceneProjectAsync(source with{ProjectPath=committed});
        long beforeProgramPlay=scene.LastFrame!.SessionId;
        scene.Play();await scene.SynchronizeAsync();
        check(scene.LastFrame!.SessionId>beforeProgramPlay,
            "playback after an actual native program change replays its committed command history before continuing");
        scene.Pause();await scene.SynchronizeAsync();
        await scene.SeekAsync(0).WaitAsync(TimeSpan.FromSeconds(10));
        var beginning=scene.LastFrame!;
        await scene.SeekAsync(.5).WaitAsync(TimeSpan.FromSeconds(10));
        check(scene.LastFrame!.Pixels.Rgba.SequenceEqual(restored.Pixels.Rgba)&&beginning.SessionId==scene.LastFrame.SessionId,
            "native ADV backwards replay is deterministic and subsequent forward playback reuses its new context");
        var beforeClose=home.LastFrame!;await scene.DisposeAsync();
        await home.SeekAsync(.4).WaitAsync(TimeSpan.FromSeconds(10));
        check(home.LastFrame!.Frame==12&&home.LastFrame.SessionId==beforeClose.SessionId,
            "disposing the scene context leaves the other studio's live native rendering operational");
    }
}
