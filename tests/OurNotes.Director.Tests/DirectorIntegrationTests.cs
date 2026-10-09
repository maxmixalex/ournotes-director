using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class DirectorIntegrationTests
{
    public static int Run()
    {
        string root=Path.GetFullPath("ournotes-director"),script=Path.Combine(root,"output",".director-test-"+Guid.NewGuid().ToString("N")+".json");
        try{
            var project=Archive.ReadJson(Path.Combine(root,"samples/unified/free-character.json"));
            var commands=project["commands"]!.AsArray();var character=commands[0]!.DeepClone();
            // Six independent actors, beyond the five game slots, in one native scene.
            for(int i=1;i<6;i++){
                var model=character.DeepClone();model["TargetName"]="extra"+i;commands.Insert(1,model);
                commands.Insert(2,new JsonObject{["cmd"]="Director.Actor",["TargetName"]="extra"+i,["Space"]="screen",["Values"]=new JsonObject{["position"]=new JsonArray(i/6d,.5),["height"]=.4}});
            }
            commands.Add(new JsonObject{["cmd"]="In",["TargetName"]="hotaru",["PositionType"]=5});
            commands.Add(new JsonObject{["cmd"]="Delay",["Duration"]=1});
            File.WriteAllText(script,project.ToJsonString());
            _=DirectorProject.Read(script);
            var archive=new Archive(Path.GetFullPath("legacy/millsage-story-archive/archive"));
            string dll=Path.Combine(root,"CubismSdkForNative-5-r.5/Core/dll/windows/x86_64/Live2DCubismCore.dll");
            using var renderer=new AdvStoryRenderer(archive,"intl",dll,"10060","zh-Hans",320,180,script,"ffmpeg",2);
            var first=renderer.Frame(30,true);var actor=renderer.Scene.Character("hotaru")!;
            if(renderer.Scene.VisibleActors.Count()!=6)throw new Exception("Free actors remain limited to five slots");
            if(!actor.DirectorParameters.TryGetValue("ParamAngleX",out float control)||Math.Abs(actor.ParameterValue("ParamAngleX")-control)>.0001f)throw new Exception("Final model parameter override was lost");
            int transparent=0,partial=0;for(int i=3;i<first.Rgba.Length;i+=4){if(first.Rgba[i]==0)transparent++;else if(first.Rgba[i]<255)partial++;}
            if(transparent<10000||partial<100)throw new Exception("Transparent native output lost alpha or antialiasing");
            renderer.QueueFrame(31,true);var queued=renderer.ReadQueuedFrame();
            if(!Enumerable.Range(0,queued.Width*queued.Height).Any(i=>queued.Rgba[i*4+3]==0))throw new Exception("Queued export lost transparency");
            renderer.Frame(100,true);
            if(actor.DirectorParameters.Count!=0)throw new Exception("Release did not restore original parameter evaluation");
            var characterData=actor.Prefab.Find("Live2DCharacter").Component;
            float baseScale=characterData["BaseScale"]!.GetValue<float>();
            if(actor.Prefab.Root.Parent is null||Math.Abs(actor.Prefab.Root.Scale.X-baseScale)>.0001f)throw new Exception("In did not restore the game placement after a free actor");
            Console.WriteLine("Passed unified native six-actor scene, final parameter override/release, direct and queued transparent output with supersampling.");return 0;
        }finally{if(File.Exists(script))File.Delete(script);}
    }
}
