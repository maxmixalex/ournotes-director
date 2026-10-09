using System.Text.Json;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class ChatCommandTests
{
    public static int Run(string fixture)
    {
        try{return RunCore(fixture);}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static int RunCore(string fixture)
    {
        var archive=new Archive(Path.GetFullPath("legacy/millsage-story-archive/archive"));
        string dll=Path.GetFullPath("ournotes-director/CubismSdkForNative-5-r.5/Core/dll/windows/x86_64/Live2DCubismCore.dll");
        var frames=Archive.ReadJson(fixture).AsArray();string script=Archive.S(frames[0]!,"script","ournotes-director/samples/game-chat-typing.json");
        int count=0;
        using(var renderer=new AdvStoryRenderer(archive,"intl",dll,"10061","zh-Hans",640,360,script,"ffmpeg",1)){
        foreach(var expected in frames.OfType<JsonNode>()){
            int frame=(int)Actor.N(expected["frame"]);var pixels=renderer.Frame(frame);
            var actual=JsonSerializer.SerializeToNode(renderer.Ui.Chat!.Snapshot())!;
            void Equal(bool ok,string name){if(!ok)throw new Exception($"Chat frame {frame}: {name}; native {actual}; reference {expected}");count++;}
            Equal(renderer.Player.ListIndex==(int)Actor.N(expected["row"]),"command row");
            Equal(Actor.B(actual["Visible"])==Actor.B(expected["visible"]),"visibility");
            if(frame==6&&script.EndsWith("game-chat-modes.json",StringComparison.Ordinal)){
                var open=Png.Decode(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(fixture)!,"chat-open.png")));
                Equal(Rms(pixels,open,180,120,270,165)<4,"first bubble layout and avatar clipping");
            }
            if(expected["main"] is not null){Equal(renderer.Ui.Chat.BubbleCount==(int)Actor.N(expected["main"]),"main timeline count");Equal(renderer.Ui.Chat.LockBubbleCount==(int)Actor.N(expected["lock"]),"lock timeline count");}
            if(expected["boxHeight"] is null)continue;
            Equal(Math.Abs(Actor.N(actual["position"]![1])-Actor.N(expected["y"]))<.002f,"window slide position");
            if(expected["scale"] is JsonNode scale)for(int axis=0;axis<3;axis++)Equal(Math.Abs(Actor.N(actual["scale"]![axis])-Actor.N(scale[new[]{"x","y","z"}[axis]]))<.00002f,"window scale "+axis);
            Equal(Archive.S(actual["typing"]!,"Text")==Archive.S(expected,"text"),"typing text");
            Equal(Actor.N(actual["typing"]!["MaxVisible"])==Actor.N(expected["maxVisible"]),"visible character count");
            Equal(Math.Abs(Actor.N(actual["typing"]!["boxHeight"])-Actor.N(expected["boxHeight"]))<.002f,"typing box height");
        }
        }
        using var locked=new AdvStoryRenderer(archive,"intl",dll,"10061","zh-Hans",640,360,"ournotes-director/samples/game-chat-lock.json","ffmpeg",1);
        var image=locked.Frame(330);var reference=Png.Decode(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(fixture)!,"chat-lock.png")));
        if(locked.Ui.Chat!.BubbleCount!=2||locked.Ui.Chat.LockBubbleCount!=3)throw new Exception("Lock restore must populate only the lock timeline; subsequent messages populate both");count++;
        double phone=Rms(image,reference,160,40,480,327),emoji=Rms(image,reference,306,270,337,284);
        if(phone>3||emoji>4)throw new Exception($"Chat lock image differs: phone RMS {phone}, emoji RMS {emoji}");count+=2;
        Console.WriteLine($"Passed {count} native chat checks; reference phone RMS {phone:F3}, emoji RMS {emoji:F3}.");return 0;
    }
    static double Rms(Pixels image,Pixels reference,int x0,int y0,int x1,int y1)
    {
        double sum=0;int n=0;for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++)for(int c=0;c<3;c++){int i=(y*image.Width+x)*4+c,d=image.Rgba[i]-reference.Rgba[i];sum+=d*d;n++;}return Math.Sqrt(sum/n);
    }
}
