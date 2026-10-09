using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class NativeSceneTests
{
    public static int Run()
    {
        string? workspace=null;for(var d=new DirectoryInfo(Environment.CurrentDirectory);d is not null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"legacy","millsage-story-archive","archive"))){workspace=d.FullName;break;}
        if(workspace is null)throw new DirectoryNotFoundException("Run native scene checks from the workspace");
        string project=Path.Combine(workspace,"ournotes-director"),archivePath=Path.Combine(workspace,"legacy","millsage-story-archive","archive"),dll=Path.Combine(project,"CubismSdkForNative-5-r.5","Core","dll","windows","x86_64","Live2DCubismCore.dll");
        int count=0;void Check(bool ok,string description){if(!ok)throw new Exception(description);count++;}
        using(var gl=new OffscreenGl(32,32)){
            uint white=gl.Upload(new(1,1,[255,255,255,255]));float[] uv=[0,0,1,0,1,1,0,1],quad=[-1,-1,1,-1,1,1,-1,1];
            void Draw(float[] position,float[] color)=>gl.Draw(white,position,uv,[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],color,0,false,false);
            gl.Begin([0,0,0,1]);Draw([-2,-2,0,2,2,-2,0,2,2,2,0,2,-2,2,0,2],[1,0,0,1]);var homogeneous=gl.Read(32,32);
            Check(homogeneous.Rgba[0]==255&&homogeneous.Rgba[1]==0,"Homogeneous clip coordinates project through W");
            gl.Begin([0,0,0,1]);Draw([-1,-1,2,1,1,-1,2,1,1,1,2,1,-1,1,2,1],[1,0,0,1]);Check(gl.Read(32,32).Rgba[0]==0,"Vertices beyond far clip plane are clipped");
            gl.Begin([0,0,0,0]);gl.BeginLayer();Draw(quad,[1,0,0,1]);Draw(quad,[1,0,0,1]);gl.EndLayer(.5f);var faded=gl.Read(32,32);
            Check(faded.Rgba[0]==255&&Math.Abs(faded.Rgba[3]-128)<=1,"Group alpha applies once after overlapping drawables composite");
            gl.Begin([0,0,1,1]);uint capture=gl.Capture();gl.Begin([1,0,0,1]);gl.Composite(capture,.5f);var mixed=gl.Read(32,32);
            Check(Math.Abs(mixed.Rgba[0]-127)<=1&&Math.Abs(mixed.Rgba[2]-128)<=1,"GPU capture crossfade composites old and new scene");
            gl.Begin([0,0,0,0]);Draw([-1,-1,1,-1,-1,1,-1,1],[1,0,0,1]);var resolved=gl.Read(8,8);
            Check(resolved.Rgba.Where((v,i)=>i%4==3).Any(v=>v>0&&v<255),"Supersampling resolves fractional diagonal coverage");
            Check(Enumerable.Range(0,64).Where(i=>resolved.Rgba[i*4+3]>0).All(i=>resolved.Rgba[i*4]==255&&resolved.Rgba[i*4+1]==0),"Premultiplied resolve preserves edge color without dark fringes");
            bool invalid=false;try{gl.Read(7,8);}catch(ArgumentException){invalid=true;}Check(invalid,"Readback rejects nonuniform or fractional resolution ratios");
            gl.Begin([0,0,0,1]);Draw([-1,-1,1,-1,-1,1,-1,1],[.3f,.8f,.6f,.7f]);
            var cpuResolve=gl.Read(8,8);var gpuResolve=gl.Present(8,8,4,0,(_,_,_)=>{});
            Check(cpuResolve.Rgba.SequenceEqual(gpuResolve.Rgba),"GPU 4x area resolve matches CPU byte rounding, coverage and orientation");
            var expected=new Queue<byte[]>();int received=0;
            for(int i=0;i<7;i++){
                gl.Begin([0,0,i/8f,1]);Draw([-1,-1,1,-1,-1,1,-1,1],[i/8f,.8f,.6f,.7f]);
                expected.Enqueue(gl.Present(8,8,4,0,(_,_,_)=>{}).Rgba);
                gl.QueuePresent(8,8,4,0,(_,_,_)=>{});
                if(gl.PendingPixels==3){Check(gl.ReadQueued().Rgba.SequenceEqual(expected.Dequeue()),"Queued GPU readback preserves frame order and exact pixels "+received++);}
            }
            while(gl.PendingPixels>0)Check(gl.ReadQueued().Rgba.SequenceEqual(expected.Dequeue()),"Queued GPU tail drains exactly "+received++);
            Check(received==7&&expected.Count==0,"GPU queue neither loses nor duplicates final frames");
            gl.Begin([0,0,0,1]);
            gl.WithStencilMask(()=>Draw([-1,-1,0,-1,0,1,-1,1],[1,1,1,1]),()=>{
                Draw(quad,[1,0,0,1]);
                gl.WithStencilMask(()=>Draw([-1,-1,1,-1,1,0,-1,0],[1,1,1,1]),()=>Draw(quad,[0,1,0,1]));
            });
            var nested=gl.Read(32,32).Rgba;
            Check(nested[0]==255&&nested[(31*32)*4+1]==255&&nested[31*4]==0&&nested[(32*32-1)*4+1]==0,"Nested phone masks restrict contents to their intersection");
            Draw(quad,[0,0,1,1]);Check(gl.Read(32,32).Rgba.Where((_,i)=>i%4==2).All(v=>v==255),"Leaving nested masks restores unmasked drawing");
            uint alpha=gl.Upload(new(2,1,[255,255,255,0,255,255,255,255]));gl.Begin([0,0,0,1]);
            gl.WithStencilMask(()=>gl.Draw(alpha,quad,uv,[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],[1,1,1,1],0,false,false),()=>Draw(quad,[1,0,0,1]));
            var clipped=gl.Read(32,32).Rgba;Check(clipped[0]==0&&clipped[31*4]==255,"Transparent mask texels do not admit phone contents");
        }
        var archive=new Archive(archivePath);
        byte[] sequential;
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10060","zh-Hans",160,90,Path.Combine(project,"samples","game-frames.json"),"ffmpeg",1)){
            Pixels last=renderer.Frame(0);for(int frame=1;frame<=65;frame++)last=renderer.Frame(frame);sequential=last.Rgba;
        }
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10060","zh-Hans",160,90,Path.Combine(project,"samples","game-frames.json"),"ffmpeg",1))
            Check(renderer.Frame(65).Rgba.SequenceEqual(sequential),"Seeking preserves the exact frame animator and postprocessing result");
        foreach(var sample in new[]{("game-particles.json",110),("game-primitives.json",285)}){
            using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10060","zh-Hans",160,90,Path.Combine(project,"samples",sample.Item1),"ffmpeg",1)){
                Pixels last=renderer.Frame(0);for(int frame=1;frame<=sample.Item2;frame++)last=renderer.Frame(frame);sequential=last.Rgba;
            }
            using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10060","zh-Hans",160,90,Path.Combine(project,"samples",sample.Item1),"ffmpeg",1))
                Check(renderer.Frame(sample.Item2).Rgba.SequenceEqual(sequential),"Sequential playback matches seeking with particles or background capture: "+sample.Item1);
        }
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10060","zh-Hans",320,180,Path.Combine(project,"samples","game-primitives.json"),"ffmpeg")){
            var a=renderer.Frame(8);Check(renderer.Scene.Shown.Count==2,"Native In places both real models in game slots");
            var b=renderer.Frame(75);Check(renderer.Scene.Camera.Euler.Y>0,"PanV2 changes perspective camera while rendering");Check(!a.Rgba.SequenceEqual(b.Rgba),"Native command timeline changes actual pixels");
            renderer.Frame(285);Check(renderer.Scene.Shown.Count==1&&renderer.Scene.StageName.Contains("000248"),"Out and awaited Stage capture finish on native frame clock");
        }
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10060","zh-Hans",320,180,Path.Combine(project,"samples","game-dialogue.json"),"ffmpeg")){
            renderer.Frame(8);Check(renderer.Audio.Playing.Count==1,"Talk starts decoded original voice");
            var samples=renderer.Audio.MixFrame();for(int f=9;f<16;f++){renderer.Frame(f);samples=renderer.Audio.MixFrame();if(samples.Any(v=>Math.Abs(v)>.0001))break;}
            Check(samples.Any(v=>Math.Abs(v)>.0001),"Native timeline emits audible PCM");renderer.Frame(150);Check(renderer.Ui.Text.Contains("再无"),"Voice completion and typewriter advance to the next localized line");
            var ui=renderer.Frame(150);var clean=renderer.Frame(150,true);Check(!ui.Rgba.SequenceEqual(clean.Rgba),"Clean MAD output suppresses dialogue graphics without changing timeline");
        }
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10060","zh-Hans",320,180,Path.Combine(project,"samples","game-effects.json"),"ffmpeg")){
            var caption=renderer.Frame(30);var clean=renderer.Frame(30,true);Check(!caption.Rgba.SequenceEqual(clean.Rgba),"Location draws the original moving caption and gradient");
            var normal=renderer.Frame(78,true);var memory=renderer.Frame(120,true);Check(!normal.Rgba.SequenceEqual(memory.Rgba),"PostEffect compiles and executes original LUT and five-pass camera DoF");
            renderer.Frame(280,true);Check(renderer.Scene.Camera.ShakeOffset.LengthSquared()<1e-8,"Camera shake toggle returns to the unshaken pose");
        }
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10060","zh-Hans",320,180,Path.Combine(project,"samples","game-frames.json"),"ffmpeg")){
            var before=renderer.Frame(0);var flare=renderer.Frame(60);Check(renderer.Frames.VisibleCount==2&&!before.Rgba.SequenceEqual(flare.Rgba),"Original additive lens-flare material and animated letterbox render natively");
            renderer.Frame(220);Check(renderer.Frames.VisibleCount>=1,"Frame animator completes named states and reaches eye-blink overlay");
        }
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10079","zh-Hans",320,180,Path.Combine(project,"samples","game-still.json"),"ffmpeg",2)){
            var still=renderer.Frame(60);Check(renderer.Stills.Visible&&renderer.Stills.PostEffectSuppressed,"Still shows and suppresses video-camera volumes");
            Check(!still.Rgba.SequenceEqual(renderer.Frame(110).Rgba),"Original still scale animation changes pixels");
            renderer.Frame(220);Check(renderer.Player.Finished&&!renderer.Stills.Visible&&!renderer.Stills.PostEffectSuppressed,"Still fade-out releases its visible state and volume suppression");
        }
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10079","zh-Hans",320,180,Path.Combine(project,"samples","game-still-shake.json"),"ffmpeg",1)){
            var resting=renderer.Frame(20);var shaken=renderer.Frame(45);
            Check(renderer.Stills.ShakeOffset.LengthSquared()>0&&!resting.Rgba.SequenceEqual(shaken.Rgba),"Still layer shake moves the original illustration");
            // Original web engine, same script/frame 45 and UnityRandom seed 722.
            Check(System.Numerics.Vector3.Distance(renderer.Stills.ShakeOffset,new(-2.01589394f,4.15122461f,1.57828176f))<.0001f,"Still shake world position matches the original engine");
            Check(renderer.Scene.Camera.ShakeOffset==System.Numerics.Vector3.Zero&&renderer.Ui.ShakeOffset==System.Numerics.Vector3.Zero,"Still-only shake leaves camera and dialogue stationary");
            var restored=renderer.Frame(115);Check(renderer.Stills.ShakeOffset.LengthSquared()<1e-8&&resting.Rgba.SequenceEqual(restored.Rgba),"Still shake returns to the exact unshaken pixels");
        }
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10060","zh-Hans",160,90,Path.Combine(project,"samples","game-lipsync.json"),"ffmpeg",1)){
            var openings=new List<float>();for(int frame=8;frame<65;frame++){renderer.Frame(frame);openings.Add(renderer.Scene.Character("hotaru")!.MouthOpen);}
            var actor=renderer.Scene.Character("hotaru")!;Check(actor.LipSyncMissing is null&&openings.Max()>.1f&&openings.Max()-openings.Min()>.05f,"MotionSync 5.0.4 maps original speech PCM to changing mouth parameters");
            renderer.Frame(400);Check(renderer.Player.Finished&&!actor.VoiceLipSyncActive,"Voice completion detaches its mouth driver");
        }
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10060","zh-Hans",160,90,Path.Combine(project,"samples","game-air-lipsync.json"),"ffmpeg",1)){
            renderer.Frame(30);var actor=renderer.Scene.Character("hotaru")!;
            Check(actor.PseudoLipSyncActive&&actor.MouthOpen>.1f&&renderer.Audio.Playing.Count==0,"AirLipSync animates without audio on the native timeline");
            renderer.Frame(600);Check(renderer.Player.Finished&&!actor.PseudoLipSyncActive,"Timed pseudo and hold-open modes finish on the script clock");
        }
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10070","zh-Hans",320,180,Path.Combine(project,"samples","game-video.json"),"ffmpeg",1)){
            var caption=renderer.Frame(60);Check(renderer.Video.Playing,"Original video decodes on the native timeline");
            Check(!caption.Rgba.SequenceEqual(renderer.Frame(60,true).Rgba),"Clean video output omits subtitles");
            renderer.Audio.ChangeVolume("Voice",0);Check(renderer.Audio.MixFrame().Any(v=>Math.Abs(v)>.0001),"Movie audio remains audible when voice volume is zero");
            renderer.Frame(250);Check(renderer.Player.Finished&&!renderer.Video.Visible,"Clip stop marker ends playback and its subtitle timeline");
        }
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10062","zh-Hans",160,90,Path.Combine(project,"samples","game-shared-lipsync.json"),"ffmpeg",1)){
            float peak=0;for(int frame=8;frame<65;frame++){renderer.Frame(frame);peak=Math.Max(peak,renderer.Scene.Character("nagi")!.MouthOpen);}
            Check(peak>.1f&&renderer.Scene.Character("nagi")!.LipSyncMissing is null,"EveryoneLipSync routes a shared CRI Lips analyzer to all showing actors");
            renderer.Frame(400);Check(renderer.Player.Finished&&renderer.Scene.Shown.Values.All(a=>!a.VoiceLipSyncActive),"Shared CRI voice completion detaches every mouth driver");
        }
        Console.WriteLine($"Passed {count} native GPU/scene/audio checks.");return 0;
    }
}
