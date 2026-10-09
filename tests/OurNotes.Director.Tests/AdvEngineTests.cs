using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class AdvEngineTests
{
    public static void Run(Action<bool,string> check)
    {
        var loop=new AdvLoop();var phases=new List<string>();
        loop.YieldUpdate().OnCompleted(()=>phases.Add("yield"));
        loop.Delay(0).OnCompleted(()=>phases.Add("delay"));
        loop.DelayFrames(1).OnCompleted(()=>phases.Add("frame"));
        foreach(var phase in Enum.GetValues<AdvPhase>())loop.On(phase,()=>phases.Add(phase.ToString()));
        loop.YieldPostLate().OnCompleted(()=>phases.Add("post-yield"));
        loop.Step();
        check(string.Join(",",phases)=="yield,delay,frame,Update,Tweens,Animation,LateUpdate,PreLateEnd,PostLate,post-yield,Render","Native player-loop phase and waiter ordering");
        loop=new();bool done=false;loop.Delay(0).OnCompleted(()=>done=true);loop.Drain();check(!done,"Zero UniTask delay waits for next Update");loop.Step();check(done,"Zero delay resolves next Update");
        loop=new();int ended=-1;loop.Delay(.1).OnCompleted(()=>ended=loop.Frame);for(int i=0;i<3;i++)loop.Step();check(ended==3,"Delay uses float elapsed and millisecond TimeSpan rounding");
        check(AdvLoop.DelaySeconds(.0005)==.001f&&AdvLoop.DelaySeconds(.00049)==0,"Half-millisecond delay rounds away from zero");
        loop=new();done=false;loop.On(AdvPhase.Update,()=>{if(loop.Frame==1)loop.Delay(0).OnCompleted(()=>done=true);});loop.Step();check(!done,"Delay created during Update cannot finish in the same frame");loop.Step();check(done,"New Update delay resumes next frame");
        loop=new();var group=new object();done=false;loop.Delay(10,group).OnCompleted(()=>done=true);loop.CancelDelays(group,true);loop.Drain();check(!done,"CancelDelay observation deferred to next Update");loop.Step();check(done,"CancelDelay completes grouped waits");

        static AdvRow Row(string json,int index=0)=>AdvProgram.Row(JsonNode.Parse(json)!,index.ToString());
        static AdvPlayback Player(AdvLoop clock,AdvProgram program,bool auto=true)
        {
            var p=new AdvPlayback(clock,program,auto){Prepare=_=>[]};
            p.Register("TalkWindow",(r,s)=>[]);return p;
        }
        loop=new();var rows=new[]{Row("""{"cmd":"Delay","Duration":0.1,"IsNoWait":1} """),Row("""{"cmd":"ForceAuto"} """,1)};
        var player=Player(loop,new(){Rows=rows});var seen=new List<(string Id,int Frame)>();player.OnCommand=(r,f)=>seen.Add((r.Index,f));player.Start();
        check(!player.Finished,"Delay ignores IsNoWait");for(int i=0;i<4;i++)player.Step();
        check(player.Finished&&seen.Last().Frame==3,"Delay command resumes after three float32 countdown updates");
        loop=new();player=Player(loop,new(){Rows=[Row("""{"cmd":"Wait","IsNoWait":1} """)],AutoWaitSeconds=.1f});player.Start();
        for(int i=0;i<3;i++)player.Step();check(!player.Finished,"Auto Wait always yields once before its linger");player.Step();check(player.Finished,"Auto Wait finishes after initial yield plus three linger ticks");
        loop=new();player=Player(loop,new(){Rows=[Row("""{"cmd":"Wait"} """)]},false);player.Start();player.Step();check(!player.Finished,"Manual Wait remains blocked");player.Tap();player.Step();check(player.Finished,"Manual tap advances only at Update");
        loop=new();player=Player(loop,new(){Rows=[Row("""{"cmd":"Wait"} """)]},false);player.Start();player.SetAuto(true);player.Step();check(!player.Finished,"Switching auto on still honors zero-time linger tick");player.Step();check(player.Finished,"Auto transition advances pending manual Wait");
        var initialized=Row("""{"cmd":"ForceAuto"} """,-2);var finalized=Row("""{"cmd":"ForceAuto"} """,-3);
        loop=new();player=Player(loop,new(){Initialize=[initialized],Rows=[Row("""{"cmd":"Delay","Duration":5} """)],Finalize=[finalized]});seen=[];player.OnCommand=(r,f)=>seen.Add((r.Index,f));player.Start();player.Stop();player.Step();check(player.Finished&&!seen.Any(x=>x.Id=="-3"),"Stopping a blocked episode skips finalize commands");
        loop=new();player=Player(loop,new(){Rows=[Row("""{"cmd":"GoTo","Parameter1":"target"} """),Row("""{"cmd":"Delay","Duration":5} """,1),Row("""{"cmd":"ForceAuto","Key":"target","IgnoreData":1} """,2),Row("""{"cmd":"ForceAuto","Key":"target"} """,3)]});seen=[];player.OnCommand=(r,f)=>seen.Add((r.Index,f));player.Start();check(player.Finished&&seen.Select(x=>x.Id).SequenceEqual(new[]{"-1","0","3"}),"GoTo uses first key including ignored rows, then skips ignored target");
        loop=new();player=Player(loop,new(){Rows=[Row("""{"cmd":"Delay","Duration":1} """)]});player.Start();player.Paused=true;player.Step(); // an already pending frame subtracts dt before observing pause
        for(int i=0;i<60;i++)player.Step();check(!player.Finished,"Host pause holds countdown across frames");player.Paused=false;for(int i=0;i<40;i++)player.Step();check(player.Finished,"Countdown resumes after host pause");
        loop=new();player=Player(loop,new(){Rows=[Row("""{"cmd":"Stage"} """)]});bool refused=false;try{player.Start();}catch(NotSupportedException){refused=true;}check(refused&&loop.Frame==0,"Unbound visual commands rejected before execution");
        var episode=JsonNode.Parse("""{"commands":[{"i":7,"cmd":"Delay","raw":3,"Duration":1}]} """)!;
        var settings=JsonNode.Parse("""{"_initializeEpisodes":[{"Command":56}],"_finalizeEpisodes":[{"Command":29}],"_waitCommandLingeringTimeOnAutoPlay":0.5} """)!;
        var program=AdvProgram.From(episode,settings);check(program.Initialize[0].Index=="init0"&&program.Finalize[0].Command=="Wait"&&program.Rows[0].Index=="7","Program compiles serialized initialize/finalize command enums and original row indexes");
        loop=new();player=Player(loop,new(){Rows=[Row("""{"cmd":"Stage","IsNoWait":1} """),Row("""{"cmd":"CancelDelay"} """,1),Row("""{"cmd":"Delay","Duration":0.1} """,2)]});ended=-1;
        IEnumerable<AdvWait> Delayed(AdvPlayback p){yield return p.CommonDelay(5);ended=p.Loop.Frame;}
        IEnumerable<AdvWait> Stage(AdvRow r,AdvPlayback p){yield return p.NoWait(r,Delayed(p));}
        player.Register("Stage",Stage);player.Start();player.Step();check(ended==1&&!player.Finished,"Detached command runs concurrently and CancelDelay releases its prologue next frame");
        loop=new();player=Player(loop,new(){Rows=[Row("""{"cmd":"Delay","Duration":10} """)]});player.Start();player.Speed=2;for(int i=0;i<160;i++)player.Step();check(player.Finished,"Speed change rescales remaining command delay");
        loop=new();var tweens=new AdvTweens(loop);float value=2;
        var tween=tweens.To(()=>value,10,.1f,AdvEase.Linear,v=>value=v);value=4;
        loop.Step();check(Math.Abs(value-6)<.00001,"Tween reads lazy start at first Tweens phase");loop.Step();loop.Step();check(tween.Done&&Math.Abs(value-10)<.00001,"Tween completes at clamped duration");
        bool tweenDone=false;tween=tweens.To(()=>value,15,0,AdvEase.Linear,v=>value=v,true);tween.Completed.OnCompleted(()=>tweenDone=true);loop.Drain();check(!tweenDone&&value==10,"Zero FloatPlugin tween defers until update");loop.Step();check(tweenDone&&value==15,"Zero FloatPlugin tween uses start plus change");
        value=0;tween=tweens.To(()=>value,20,0,AdvEase.Linear,v=>value=v);check(tween.Done&&value==20,"General zero-duration tween applies immediately");
        tween=tweens.To(()=>value,30,5,AdvEase.Linear,v=>value=v);tween.Kill();loop.Step();check(value==20&&!tween.Completed.GetResult(),"Killed tween preserves current value and signals false");
        loop=new();tweens=new(loop);value=0;int joined=-1;
        var first=tweens.To(()=>0,1,.1f,AdvEase.Linear,_=>{});var second=tweens.To(()=>0,1,.2f,AdvEase.Linear,_=>{});
        loop.All(first.Completed,second.Completed).OnCompleted(()=>joined=loop.Frame);
        for(int i=0;i<6;i++)loop.Step();check(joined==6,"Parallel tween join waits for longest tween in same Tweens drain");
        check(Math.Abs(AdvTweens.Evaluate(AdvEase.OutQuad,.5f,1)-.75f)<1e-6,"Default camera ease OutQuad midpoint");
        check(AdvTweens.Evaluate(AdvEase.OutBack,.8f,1)>1,"Back ease preserves overshoot");
        check(AdvTweens.Evaluate(AdvEase.InExpo,0,1)==0&&AdvTweens.Evaluate(AdvEase.OutExpo,1,1)==1,"Expo eases preserve exact endpoints");
        bool unsupported=false;try{AdvTweens.Evaluate(AdvEase.Flash,.5f,1);}catch(NotSupportedException){unsupported=true;}check(unsupported,"Flash ease refuses same unsupported feature as reference");
        foreach(var ease in Enum.GetValues<AdvEase>().Where(e=>(int)e<32))
        {
            check(Math.Abs(AdvTweens.Evaluate(ease,0,1))<1e-5&&Math.Abs(AdvTweens.Evaluate(ease,1,1)-1)<1e-5,"Ease endpoints: "+ease);
        }
        loop=new();var pcm=Enumerable.Repeat(.25f,3200).ToArray();var audio=new AdvAudio(loop,[new(1,2,pcm,1),new(2,0,pcm,1)]);
        var voice=audio.Play(1);var block=audio.MixFrame();check(block.Length==3200&&block.All(v=>v==.25f),"Native audio mixes mono into 1600 stereo samples per frame");
        long previous=0;loop.Step();var lips=audio.PcmSince(voice,ref previous);check(lips.Length==1600&&lips.All(v=>v==.25f),"Native lip-sync PCM advances by simulation sample clock");
        loop.Step();check(!audio.IsPlaying(voice.Id)&&voice.Stopped,"Nonlooping cue ends at exact sample boundary");
        audio.Play(2,true);check(Math.Abs(audio.MixFrame()[0]-.175f)<1e-6,"BGM default bus gain is 0.7");audio.ChangeVolume("Bgm",.5f);check(Math.Abs(audio.MixFrame()[0]-.0875f)<1e-6,"BGM category volume multiplies default gain");
        loop=new();audio=new(loop,[new(3,1,[0,1,2,3],1,1,3)]);audio.Play(3,true);block=audio.MixFrame();check(block.Take(12).SequenceEqual(new float[]{0,0,1,1,2,2,1,1,2,2,1,1}),"Looped PCM repeats cue region without repeating intro");
        loop=new();audio=new(loop,[new(1,2,Enumerable.Repeat(1f,48000).ToArray(),1)]);voice=audio.Play(1);audio.Stop(voice.Id,.1f);loop.Step();check(Math.Abs(audio.MixFrame()[0]-(1-1f/3))<1e-5,"Audio fade gain advances with Update clock");loop.Step();loop.Step();check(voice.Stopped,"Fade stops at duration");
        loop=new();int decoded=0;audio=new(loop,[]){DecodedCacheLimitBytes=16,ResolveCue=id=>{decoded++;return new(id,2,[.1f,.2f,.3f,.4f],1);}};
        var firstCached=audio.Play(10,true);var secondCached=audio.Play(11,true);var both=audio.MixFrame();
        check(audio.DecodedCacheBytes==32&&both[0]==.2f,"Audio cache keeps overlapping active cues even above its budget");
        audio.Stop(firstCached.Id);check(audio.DecodedCacheBytes==16,"Finished PCM is evicted when cache exceeds its budget");
        audio.Stop(secondCached.Id);var reused=audio.Play(11,true);check(decoded==2,"Cached PCM is reused without decoding");audio.Stop(reused.Id);
        var reloaded=audio.Play(10,true);check(decoded==3&&audio.DecodedCacheBytes==16&&audio.MixFrame().SequenceEqual(both.Select(v=>v*.5f)),"Evicted PCM reloads with identical sample positions and bounded memory");
        audio.Stop(reloaded.Id);audio.AddCue(new(10,2,[.75f],1));audio.Play(12);audio.StopAll();audio.Play(10);check(decoded==4&&audio.MixFrame()[0]==.75f,"Explicit PCM replacement stays available after decoded-cache eviction");
        var reuseBlock=new float[AdvAudio.SamplesPerFrame*2];audio.MixFrame(reuseBlock);check(reuseBlock.SequenceEqual(audio.MixFrame()),"Reusable PCM buffer matches allocated output exactly");audio.StopAll();audio.MixFrame(reuseBlock);check(reuseBlock.All(v=>v==0),"Reusable PCM buffer does not retain audio after playback stops");
        loop=new();player=Player(loop,new(){Rows=[Row("""{"cmd":"Bgm","BgmID":2} """),Row("""{"cmd":"Se","SeID":1,"DelaySeconds":0.1} """,1),Row("""{"cmd":"Delay","Duration":0.2} """,2)]});
        audio=new(loop,[new(1,1,pcm,1),new(2,0,pcm,1)]);audio.Bind(player);player.Start();check(audio.Playing.Count==1,"Bgm command starts immediately while delayed SE forks");player.Step();player.Step();check(audio.Playing.Count==1,"SE respects delayed common prologue");player.Step();check(audio.Playing.Count==2&&audio.LastBgmOrSeFrame==3,"SE starts on expected native frame");while(!player.Finished)player.Step();check(audio.Playing.Count==0,"Episode completion stops owned audio sources");
        loop=new();tweens=new(loop);var camera=new AdvCamera(loop,tweens,60,.1f,100);
        var clip=AdvMatrix.Transform(camera.ViewProjection(16f/9),new(0,0,5,1));
        check(clip.W>0&&Math.Abs(clip.X)<1e-6&&Math.Abs(clip.Y)<1e-6,"Unity forward +Z projects into front of native camera");
        var near=AdvMatrix.Transform(camera.ViewProjection(1),new(0,0,.1f,1));var far=AdvMatrix.Transform(camera.ViewProjection(1),new(0,0,100,1));
        check(Math.Abs(near.Z/near.W+1)<1e-5&&Math.Abs(far.Z/far.W-1)<1e-5,"Projection maps near/far to OpenGL minus-one/plus-one clip depth");
        camera.Position=new(4,3,-2);clip=AdvMatrix.Transform(camera.ViewProjection(1),new(4,3,3,1));check(Math.Abs(clip.X)<1e-5&&Math.Abs(clip.Y)<1e-5,"World-to-camera removes translation in Unity basis");
        var transform=AdvMatrix.Trs(new(1,2,3),new(15,30,40),System.Numerics.Vector3.One);var inverse=AdvMatrix.InverseRigid(transform);var identity=AdvMatrix.Multiply(transform,inverse);
        check(identity.Zip(AdvMatrix.Identity()).All(pair=>Math.Abs(pair.First-pair.Second)<1e-5),"Unity Z-X-Y rotation and rigid inverse compose to identity");
        var move=camera.MoveX(10,.2f,AdvEase.Linear);loop.Step();var interrupted=camera.MoveX(20,0);loop.Drain();check(!move.GetResult()&&interrupted.GetResult()&&camera.Position.X==20,"Replacing camera-axis tween cancels old completion");
        loop=new();tweens=new(loop);camera=new(loop,tweens,60,.1f,100);player=Player(loop,new(){Rows=[Row("""{"cmd":"Tilt","Parameter1":"15","DelaySeconds":0.1,"Duration":0} """)]});camera.BindRotations(player);player.Start();player.Step();player.Step();check(camera.Euler.X==0,"Tilt waits for camera common prologue");player.Step();check(camera.Euler.X==-15&&player.Finished,"Tilt inverts pitch according to reference command");
    }
}
