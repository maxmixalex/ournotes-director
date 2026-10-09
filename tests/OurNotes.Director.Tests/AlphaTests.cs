using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class AlphaTests
{
    public static int Run()
    {
        int checks=0;
        foreach(var (layers,expected) in new (string,string[])[]{("[]",["still","video"]),("[7,6]",["still","video"]),("[6,7,7]",["video","still","still"]),("[0,2,9]",[])})foreach(bool noWait in new[]{false,true}){
            var loop=new AdvLoop();var calls=new List<(string Name,int Frame)>();var row=new AdvRow("0","Alpha",JsonNode.Parse($$"""{"Parameter1":"2","Duration":0.5,"CanvasLayers":{{layers}},"IsNoWait":{{(noWait?"true":"false")}}} """)!);
            int continued=-1;var p=new AdvPlayback(loop,new AdvProgram{Rows=[row,new("1","Delay",new JsonObject{["Duration"]=.1})]});p.Register("TalkWindow",(r,p)=>[]);p.Prepare=p=>[];p.OnCommand=(r,i)=>{if(r.Index=="1")continued=loop.Frame;};
            AdvWait Fade(string name,float alpha,float duration){if(alpha!=1||duration!=.5f)throw new Exception("Alpha parameter semantics differ");calls.Add((name,loop.Frame));return loop.DelayFrames(15);}
            AdvAlphaCommands.Bind(p,loop,(a,d)=>Fade("still",a,d),(a,d)=>Fade("video",a,d));p.Start();loop.Drain();
            for(int frame=0;frame<60;frame++)p.Step();p.ThrowIfFailed();
            if(!calls.Select(c=>c.Name).SequenceEqual(expected)||!p.Finished){Console.Error.WriteLine("Alpha layer dispatch failed");return 1;}
            for(int i=1;i<calls.Count;i++)if(calls[i].Frame-calls[0].Frame!=(layers=="[]"?0:15*i)){Console.Error.WriteLine("Alpha layers did not preserve parallel/sequential timing");return 1;}
            if(continued!=(noWait||expected.Length==0?0:layers=="[]"?15:15*expected.Length)){Console.Error.WriteLine("Alpha IsNoWait continuation timing differs");return 1;}checks++;
        }
        string project=Path.GetFullPath("ournotes-director");var archive=new Archive(Path.GetFullPath("legacy/millsage-story-archive/archive"));
        using(var r=new AdvStoryRenderer(archive,"intl",Path.Combine(project,"CubismSdkForNative-5-r.5/Core/dll/windows/x86_64/Live2DCubismCore.dll"),"10079","zh-Hans",320,180,Path.Combine(project,"samples/game-still-alpha.json"),"ffmpeg",1)){
            var full=r.Frame(10);var faded=r.Frame(50);var restored=r.Frame(70);
            if(full.Rgba.SequenceEqual(faded.Rgba)||!full.Rgba.SequenceEqual(restored.Rgba)||!r.Stills.Visible){Console.Error.WriteLine("Alpha did not fade and restore the original still pixels");return 1;}
            r.Frame(100);if(!r.Player.Finished||r.Stills.Visible){Console.Error.WriteLine("Alpha sample did not finish");return 1;}checks+=2;
        }
        using(var r=new AdvStoryRenderer(archive,"intl",Path.Combine(project,"CubismSdkForNative-5-r.5/Core/dll/windows/x86_64/Live2DCubismCore.dll"),"10070","zh-Hans",160,90,Path.Combine(project,"samples/game-video.json"),"ffmpeg",1)){
            var full=r.Frame(20,true);r.Video.SetAlpha(0,0);var hidden=r.Frame(20,true);r.Video.SetAlpha(1,0);var restored=r.Frame(20,true);
            if(full.Rgba.SequenceEqual(hidden.Rgba)||!full.Rgba.SequenceEqual(restored.Rgba)||!r.Video.Playing){Console.Error.WriteLine("Video alpha altered playback or failed to restore pixels");return 1;}
            r.Video.SetAlpha(0,1);r.Frame(35,true);if(Math.Abs(r.Video.Alpha-.25f)>.00001f){Console.Error.WriteLine("Video alpha easing differs");return 1;}
            r.Frame(51,true);if(r.Video.Alpha!=0){Console.Error.WriteLine("Video alpha did not complete");return 1;}checks+=2;
        }
        Console.WriteLine($"Passed {checks} Alpha command timing, layer targeting and native still/video rendering checks.");return 0;
    }
}
