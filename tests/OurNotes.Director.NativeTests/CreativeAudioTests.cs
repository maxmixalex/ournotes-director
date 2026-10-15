using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class CreativeAudioTests
{
    public static async Task<int> RunAsync(string ffmpeg,string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);outputDirectory=Path.GetFullPath(outputDirectory);
        var checks=new JsonArray();var report=new JsonObject{["ok"]=false,["checks"]=checks};
        void Check(bool result,string name){checks.Add((JsonNode)new JsonObject{["name"]=name,["ok"]=result});if(!result)throw new InvalidOperationException(name);}
        string fixture=Path.Combine(outputDirectory,"audio-fixture");RealtimeSceneFixture.Write(fixture);
        string a=Path.Combine(outputDirectory,"tone-a.wav"),b=Path.Combine(outputDirectory,"tone-b.wav");
        WriteWave(a,Enumerable.Range(0,48000).Select(i=>(short)Math.Round(5000*Math.Sin(i*2*Math.PI*220/48000))).ToArray(),1);
        WriteWave(b,Enumerable.Range(0,48000).Select(i=>(short)Math.Round(5000*Math.Sin(i*2*Math.PI*440/48000))).ToArray(),1);
        var scene=CreativeScene.Blank("intl","zh-Hans");scene.Source["adv"]="43";scene.Canvas["width"]=128;scene.Canvas["height"]=96;
        void Voice(string who,string file,double start,double duration,double input=0)
        {scene.AddClip(who,"voice",start,duration,new JsonObject{["cmd"]="Voice",["AudioPath"]=file,["AudioIn"]=input,["AudioDuration"]=duration,["IgnoreLipSync"]=true},who);}
        Voice("speaker-a",a,0,.8);Voice("speaker-b",b,.2,.6);Voice("speaker-a",a,.4,.4,.2);Voice("",b,.5,.2,.1);
        scene.AddClip("","hold",.8,.2,new JsonObject{["cmd"]="Delay",["Frames"]=0},"Hold");
        var document=scene.Compile();string project=Path.Combine(outputDirectory,"parallel-voices.project.json");
        File.WriteAllText(project,document.ToJsonString());
        try
        {
            NativeTestGraphics.Configure();
            Check(ExternalVoiceTimeline.Supports(document),"Locally voiced scheduled scene uses the audio-only interpreter");
            const int frames=31;
            var reference=new float[frames*AdvAudio.SamplesPerFrame*2];var accelerated=new float[reference.Length];
            // Both implementations run on the same thread here. Comparison is sample-exact,
            // not a render-FPS claim or a substituted silence track.
            var fast=new ExternalVoiceTimeline(document,outputDirectory,ffmpeg);
            using(var rendered=new AdvStoryRenderer(new Archive(fixture),"intl","","43","zh-Hans",128,96,project,ffmpeg,1))
                for(int frame=0;frame<frames;frame++)
                {
                    rendered.Frame(frame,true);
                    var normal=reference.AsSpan(frame*3200,3200);var optimized=accelerated.AsSpan(frame*3200,3200);
                    rendered.Audio.MixFrame(normal);fast.MixFrame(optimized);fast.Step();
                }
            Check(reference.SequenceEqual(accelerated),"Audio-only interpreter matches the graphical/export mix sample for sample across overlaps and replacement");
            Check(reference.Any(x=>Math.Abs(x)>.15),"Comparison contains actual overlapping non-silent PCM");
            var pcm=new byte[reference.Length*2];PreviewSoundtrack.ToPcm16(reference,pcm);
            WriteWave(Path.Combine(outputDirectory,"parallel-voice-mix.wav"),MemoryMarshal.Cast<byte,short>(pcm).ToArray(),2);
            var source=new ScenePreviewSource(fixture,project,"not-used-by-audio-only",ffmpeg,128,96,frames/30d);
            var timer=Stopwatch.StartNew();
            await using(var soundtrack=new PreviewSoundtrack(source))
            {
                soundtrack.RequestThrough(frames*1600L+48000);
                while(!soundtrack.IsComplete&&soundtrack.Failure is null&&timer.Elapsed<TimeSpan.FromSeconds(30))await Task.Delay(10);
                Check(soundtrack.IsComplete&&soundtrack.Failure is null,"Preview soundtrack preparation completes without loading Cubism or a second graphics context");
                var actual=new byte[pcm.Length];
                Check(soundtrack.Read(0,actual)==frames*1600&&actual.SequenceEqual(pcm),"Seekable preview soundtrack is identical to the export PCM16 mix");
                var block=new byte[960*4];Check(soundtrack.Read(4800,block)==960&&block.SequenceEqual(pcm.AsSpan(4800*4,block.Length).ToArray()),
                    "Seeking into the prepared soundtrack starts at the requested source samples");
            }
            var drawing=RealtimeSceneFixture.Document();string drawingPath=Path.Combine(outputDirectory,"drawing.project.json");
            File.WriteAllText(drawingPath,drawing.ToJsonString());
            Pixels full;
            using(var normal=new AdvStoryRenderer(new Archive(fixture),"intl","","43","zh-Hans",128,96,drawingPath,ffmpeg,1))full=normal.Frame(15,true);
            drawing["output"]!["content"]="characters";File.WriteAllText(drawingPath,drawing.ToJsonString());
            using(var transparent=new AdvStoryRenderer(new Archive(fixture),"intl","","43","zh-Hans",128,96,drawingPath,ffmpeg,1))
            {
                var pixels=transparent.Frame(15,true);
                Check(full.Rgba.Any(x=>x>0)&&pixels.Rgba.All(x=>x==0),"Character-only drawing removes the actual background, not only the UI checkbox");
                Check(transparent.Loop.Frame==15,"Layer selection does not alter the requested simulation frame");
            }
            report["ok"]=true;report["mixedSampleFrames"]=frames*1600;report["audioOnlyPreparationMilliseconds"]=timer.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Passed {checks.Count} actual PCM/export and drawing-content checks.");return 0;
        }
        finally{File.WriteAllText(Path.Combine(outputDirectory,"report.json"),report.ToJsonString());}
    }
    static void WriteWave(string path,short[] samples,int channels)
    {
        using var writer=new BinaryWriter(File.Create(path));int bytes=samples.Length*2;
        writer.Write("RIFF"u8);writer.Write(36+bytes);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)channels);
        writer.Write(48000);writer.Write(48000*channels*2);writer.Write((short)(channels*2));writer.Write((short)16);writer.Write("data"u8);writer.Write(bytes);
        foreach(short sample in samples)writer.Write(sample);
    }
}
