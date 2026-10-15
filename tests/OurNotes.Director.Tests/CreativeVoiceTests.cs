using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class CreativeVoiceTests
{
    internal static void Run(Action<bool,string> check)
    {
        VoiceDecodeCacheTests.Run(check);
        var clock=new AdvLoop();
        AdvCue Cue(long id,float amplitude,int frames)=>new(id,2,Enumerable.Repeat(amplitude,frames*AdvAudio.SamplesPerFrame).ToArray(),1);
        var audio=new AdvAudio(clock,[Cue(1,.1f,3),Cue(2,.2f,4),Cue(3,.4f,8),Cue(4,.05f,1)]);
        var tracks=new VoiceTrackPlayback(audio);var bindings=new List<(string Name,int Sound)>();
        void Attach(string name,AdvSound sound)=>bindings.Add((name,sound.Id));
        var first=tracks.Start(["actor-a"],[1],"a0",Attach)[0];
        var second=tracks.Start(["actor-b"],[2],"b0",Attach)[0];
        check(Math.Abs(audio.MixFrame()[0]-.3f)<1e-6f,"Overlapping voices for two actors both reach the PCM mix");
        var replacement=tracks.Start(["actor-a"],[3],"a1",Attach)[0];
        check(first.Stopped&&!second.Stopped&&!replacement.Stopped&&Math.Abs(audio.MixFrame()[0]-.6f)<1e-6f,
            "Replacing one actor's voice cannot stop another actor");
        var narrator=tracks.Start([], [4],"narration-1")[0];
        var narrator2=tracks.Start([], [4],"narration-2")[0];
        check(!narrator.Stopped&&!narrator2.Stopped&&Math.Abs(audio.MixFrame()[0]-.7f)<1e-6f,
            "Separate narration clips do not claim a character voice track");
        clock.Step();
        check(narrator.Stopped&&!replacement.Stopped&&!second.Stopped,"Finished narration leaves speaker tracks alive");
        bool missing=false;try{tracks.Start(["actor-a"],[999],"bad");}catch(InvalidDataException){missing=true;}
        check(missing&&!replacement.Stopped,"Missing replacement audio does not silence the current take");
        for(int i=0;i<3;i++)clock.Step();
        check(second.Stopped&&!replacement.Stopped&&Math.Abs(audio.MixFrame()[0]-.4f)<1e-6f,
            "A previous voice completing cannot detach or stop the current voice");
        check(bindings.Count==3&&bindings[1].Name=="actor-b"&&bindings[2].Sound==replacement.Id,
            "Each speaker is bound to the exact voice instance that is mixed");

        var scene=CreativeScene.Blank("intl","zh-Hans");
        scene.AddClip("speaker","voice",0,1,new JsonObject{["cmd"]="Voice",["AudioPath"]="voice.wav"},"Voice");
        var project=scene.Compile();
        check(ExternalVoiceTimeline.Supports(project),"Explicit locally voiced shots can prepare audio without loading any model or graphics context");
        var old=project.DeepClone();old["commands"]![0]!["cmd"]="Talk";
        check(!ExternalVoiceTimeline.Supports(old),"Game dialogue with source-specific timing does not enter the optimized audio interpreter");
        old=project.DeepClone();old["output"]!["letterbox"]="game";
        check(!ExternalVoiceTimeline.Supports(old),"Original opening viewport waits retain the complete interpreter");
        check(SceneContent.Read(null)=="scene"&&SceneContent.Read(JsonValue.Create("characters"))=="characters","Scene drawing content defaults to the original scene");
        bool bad=false;try{SceneContent.Read(JsonValue.Create(true));}catch(ArgumentException){bad=true;}
        check(bad,"Invalid output layer selection is rejected instead of silently changing output");
    }
}
