using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class PreviewRuntimeTests
{
    public static void Run(Action<bool,string> check)
    {
        // Every component/alpha combination, including a SIMD tail and unaligned input.
        var source=new byte[65539*4+2];var result=new byte[65539*4+2];source[0]=result[0]=19;source[^1]=result[^1]=71;
        for(int i=0;i<65539;i++){int p=1+i*4;source[p]=(byte)i;source[p+1]=(byte)(255-i);source[p+2]=(byte)(i*37);source[p+3]=(byte)(i>>8);}
        var original=(byte[])source.Clone();PreviewPixelConverter.CopyBgraPremultiplied(source.AsSpan(1,source.Length-2),result.AsSpan(1,result.Length-2));
        bool exact=true;for(int i=0;i<65539;i++){int p=1+i*4,a=source[p+3];exact&=result[p]==(source[p+2]*a+127)/255&&result[p+1]==(source[p+1]*a+127)/255&&result[p+2]==(source[p]*a+127)/255&&result[p+3]==a;}
        check(exact&&source.SequenceEqual(original)&&result[0]==19&&result[^1]==71,"SIMD display conversion preserves all RGBA component/alpha combinations and source storage");
        bool invalid=false;try{PreviewPixelConverter.CopyBgraPremultiplied([1,2,3],[0,0,0]);}catch(ArgumentException){invalid=true;}
        check(invalid,"Display conversion refuses incomplete pixels");

        var envelope=new VoiceEnvelopeLipSync();var silent=new float[1600];var quiet=Enumerable.Repeat(.015f,1600).ToArray();var loud=Enumerable.Repeat(.15f,1600).ToArray();
        check(envelope.Feed(silent,1f/30)==0,"Volume-driven lip fallback stays closed on silence");
        float low=0;for(int i=0;i<6;i++)low=envelope.Feed(quiet,1f/30);
        float high=0;for(int i=0;i<6;i++)high=envelope.Feed(loud,1f/30);
        check(low>.05f&&high>low&&high<=.85f,"Actual PCM amplitude controls bounded mouth opening");
        float closed=1;for(int i=0;i<30;i++)closed=envelope.Feed(silent,1f/30);
        check(closed==0,"Volume-driven lips release fully after the voice becomes silent");
        var second=new VoiceEnvelopeLipSync();envelope.Reset();
        check(envelope.Feed(quiet,1f/30)==second.Feed(quiet,1f/30),"Lip envelope resets deterministically for a seek or a new cue");

        var clock=new AdvLoop();var pcm=Enumerable.Range(0,6400).Select(i=>i/6400f).ToArray();var audio=new AdvAudio(clock,[new(9,2,pcm,2)]);
        var voice=audio.Play(9);var mono=new float[1600];check(audio.ReadRecentMono(voice,mono)==0,"Lip PCM does not read ahead of the simulated voice clock");
        clock.Step();int samples=audio.ReadRecentMono(voice,mono);
        check(samples==1600&&mono[0]==pcm[0]&&mono[^1]==pcm[3198],"Lip PCM reads the current source channel without a gain or an allocation");

        AdvRow Row(string json)=>AdvProgram.Row(JsonNode.Parse(json)!,"source-row");
        clock=new();var program=new AdvProgram{
            Initialize=[Row("""{"cmd":"Stage","IgnoreData":1}"""),Row("""{"cmd":"Delay","Frames":3}""")],
            Rows=[Row("""{"cmd":"Wait","Key":"spoken-gap"}""")],
            Finalize=[Row("""{"cmd":"Movie","IgnoreData":1}"""),Row("""{"cmd":"Delay","Frames":2}""")]};
        var player=new AdvPlayback(clock,program){Prepare=_=>[]};player.Register("TalkWindow",(_,_)=>[]);player.Start();
        while(!player.Finished&&clock.Frame<30)player.Step();var rows=player.CommandTimings();
        check(player.CompletionFrame==7&&rows.Count==3,"Native timings include prepare/auto-wait cadence and exclude disabled init/finalize commands");
        check(rows.Select(x=>x.Section).SequenceEqual(["initialize","commands","finalize"])&&rows.Select(x=>x.Index).SequenceEqual([1,0,1])
            &&rows.Select(x=>x.StartFrame).SequenceEqual([0,3,5])&&rows.Select(x=>x.EndFrame).SequenceEqual(new int?[]{3,5,7})
            &&rows.Select(x=>x.Ordinal).SequenceEqual([0,1,2])&&rows[1].Key=="spoken-gap","Native command timing records actual sections, original indices, start/completion frames and keys");

        int decoded=0;var cache=new AdvCueCache{LimitBytes=16};
        AdvCue Decode(long id){Interlocked.Increment(ref decoded);return new(id,2,[.1f,.2f,.3f,.4f],1);}
        var reused=Enumerable.Range(0,8).Select(_=>Task.Run(()=>cache.Get(1,()=>Decode(1)))).ToArray();Task.WaitAll(reused);
        check(decoded==1&&reused.All(t=>ReferenceEquals(t.Result,reused[0].Result)),"Playback and timing analysis share one immutable decode of the same voice cue");
        cache.Get(2,()=>Decode(2));cache.Get(1,()=>Decode(1));
        check(decoded==3,"Shared preview PCM cache evicts older cues at its memory limit");
    }
}
