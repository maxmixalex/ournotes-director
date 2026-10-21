using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>A tiny real Still command sequence exercises the media/history boundary
/// without models, the Cubism SDK, FFmpeg or downloaded game resources.</summary>
static class PreviewFrameMediaBoundaryTests
{
    sealed record Sample(int Frame,bool Visible,byte[] Rgba,NativeCommandTiming[] Commands,int? Completion);

    public static async Task RunAsync(Action<bool,string> check)
    {
        NativeTestGraphics.Configure();
        string root=Path.Combine(Path.GetTempPath(),"ournotes-preview-media-"+Guid.NewGuid().ToString("N"));
        try
        {
            string project=WriteFixture(root);
            var reference=new Dictionary<int,Sample>();
            int[] targets=[2,8,12,24];
            using(var renderer=Create(root,project))
                foreach(int frame in targets)
                {
                    var pixels=renderer.Frame(frame,true);
                    reference[frame]=Snapshot(renderer,pixels);
                }
            check(!reference[2].Visible&&reference[8].Visible&&reference[12].Visible&&!reference[24].Visible,
                "Real Still commands enter and leave media presentation during a forward seek");
            check(!reference[2].Rgba.AsSpan().SequenceEqual(reference[8].Rgba)
                &&reference[2].Rgba.AsSpan().SequenceEqual(reference[24].Rgba),
                "The synthetic Still changes actual pixels and restores the original background when hidden");

            var drawn=new List<int>();var blocked=new List<int>();
            using(var renderer=Create(root,project))
            {
                void Capture(int current)
                {
                    if(renderer.Stills.Visible)
                    {
                        blocked.Add(current);
                        if(renderer.CanDrawHistory)throw new InvalidOperationException("Active Still incorrectly allows an extra history draw.");
                        return;
                    }
                    if(!renderer.CanDrawHistory)throw new InvalidOperationException("History drawing did not recover after Still deactivation.");
                    var sample=renderer.CaptureHistoryFrame(true);
                    if(sample.Number!=current||sample.Pixels.ByteLength!=32*24*4)
                        throw new InvalidOperationException("History captured a different frame or canvas.");
                    drawn.Add(current);
                }
                foreach(int frame in targets)
                {
                    var pixels=renderer.FrameWithHistory(frame,true,null,false,default,Capture);
                    var actual=Snapshot(renderer,pixels);var expected=reference[frame];
                    check(actual.Frame==expected.Frame&&actual.Visible==expected.Visible
                        &&actual.Completion==expected.Completion&&actual.Commands.SequenceEqual(expected.Commands)
                        &&actual.Rgba.AsSpan().SequenceEqual(expected.Rgba),
                        $"History on/off preserves exact Still pixels and original command timing at frame {frame}");
                    if(frame!=8)continue;

                    int draws=0;renderer.Scene.DrawForeground=()=>draws++;
                    bool refused=false;
                    try{renderer.CaptureHistoryFrame(true);}catch(InvalidOperationException){refused=true;}
                    check(refused&&draws==0&&renderer.Loop.Frame==8&&renderer.Stills.Visible,
                        "A direct history draw is rejected before rendering or advancing an active Still");

                    var before=renderer.Player.CommandTimings().ToArray();
                    renderer.CapturePreviewPixels=true;renderer.PreviewCaptureScale=.25;
                    var repeated=renderer.Frame(8,true);var copied=renderer.CapturedPreviewPixels;
                    check(draws==1&&renderer.Loop.Frame==8&&renderer.Stills.Visible
                        &&renderer.Player.CommandTimings().SequenceEqual(before)
                        &&repeated.Rgba.AsSpan().SequenceEqual(expected.Rgba),
                        "Final-FBO readback during Still uses one normal draw and leaves pixels and interpreter state unchanged");
                    check(copied is {Width:32,Height:24}&&copied.ByteLength==32*24*4
                        &&copied.Rgba.Where((_,i)=>i%4==3).Any(alpha=>alpha>0),
                        "Active media still supplies a real bounded CPU snapshot of its final FBO");
                    renderer.CapturePreviewPixels=false;renderer.PreviewCaptureScale=1;renderer.Scene.DrawForeground=null;
                }
            }
            check(blocked.SequenceEqual(Enumerable.Range(4,12))
                &&drawn.Any(frame=>frame<4)&&drawn.Any(frame=>frame>=16)
                &&!drawn.Any(frame=>frame is >=4 and <16),
                "A single forward history traversal skips the entire Still interval and resumes after it");

            // Exercise the production SceneState callback too: eligibility changes
            // inside a seek, and final requested media frames remain cacheable.
            await using(var session=new RealtimePreviewSession())
            {
                var source=new ScenePreviewSource(root,project,"","",128,96,2,GpuPresentation:false);
                await session.LoadSceneAsync(source);
                await session.SeekAsync(8/30d);var active=session.LastFrame!;
                await session.SeekAsync(12/30d);var later=session.LastFrame!;
                check(active.FrameCache is {} a&&later.FrameCache is {} b&&a.CapturedFrames==b.CapturedFrames
                    &&active.Pixels.Rgba.AsSpan().SequenceEqual(reference[8].Rgba)
                    &&later.Pixels.Rgba.AsSpan().SequenceEqual(reference[12].Rgba),
                    "Production seek adds no intermediate history draws while Still remains visible");
                await session.ScrubAsync(8/30d);var cached=session.LastFrame!;
                check(cached.IsCached&&!cached.NativeStateSynchronized&&cached.Frame==8
                    &&cached.FrameCache?.NativeFrame==12&&cached.Pixels.Rgba.AsSpan().SequenceEqual(reference[8].Rgba),
                    "A completed Still frame remains available as an exact cached image without native rewind");
                await session.SeekAsync(24/30d);var restored=session.LastFrame!;
                check(restored.NativeStateSynchronized&&restored.FrameCache is {} after&&later.FrameCache is {} during
                    &&after.CapturedFrames>during.CapturedFrames
                    &&restored.Pixels.Rgba.AsSpan().SequenceEqual(reference[24].Rgba),
                    "Production history resumes after Still hides and preserves the final native image");

                // A saved live edit marks future replay as necessary, but native
                // state is already at the displayed frame. A second edit/commit
                // must not replay the first save and discard the newer grade.
                var document=DirectorProject.Document(project);
                foreach(float exposure in new[]{-1f,-.25f})
                {
                    var previous=session.LastFrame!;
                    var edit=new JsonObject{["cmd"]="Director.Scene",["Values"]=new JsonObject
                        {["grade"]=new JsonObject{["exposure"]=exposure}}};
                    await session.ApplySceneEditsAsync([edit]);var edited=session.LastFrame!;
                    byte[] editedPixels=edited.Pixels.Rgba.ToArray();
                    check(edited.Frame==24&&edited.SessionId==restored.SessionId
                        &&!editedPixels.AsSpan().SequenceEqual(previous.Pixels.Rgba),
                        $"Live exposure {exposure} changes the displayed pixels without replaying the previous save");
                    document["initialize"]!.AsArray().Add(edit.DeepClone());
                    File.WriteAllText(project,document.ToJsonString());
                    await session.CommitSceneProjectAsync(source);
                    check(ReferenceEquals(session.LastFrame,edited)&&session.LastFrame!.Frame==24
                        &&session.LastFrame.SessionId==restored.SessionId
                        &&session.LastFrame.Pixels.Rgba.AsSpan().SequenceEqual(editedPixels),
                        $"Committing live exposure {exposure} preserves its exact frame, pixels and native session");
                }
            }
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    static AdvStoryRenderer Create(string root,string project)=>new(new Archive(root),"intl","","43","zh-Hans",128,96,project,"",1);
    static Sample Snapshot(AdvStoryRenderer renderer,Pixels pixels)=>new(renderer.Loop.Frame,renderer.Stills.Visible,
        pixels.Rgba.ToArray(),renderer.Player.CommandTimings().ToArray(),renderer.Player.CompletionFrame);

    static string WriteFixture(string root)
    {
        string project=RealtimeSceneFixture.Write(root);
        string manifestPath=Path.Combine(root,"sites","intl","stories","43.json");
        var manifest=Archive.ReadJson(manifestPath);var files=manifest["files"]!.AsObject();
        void Put(string logical,string text)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(text);
            string asset="assets/"+Archive.Hash(bytes)+Path.GetExtension(logical);
            File.WriteAllBytes(Path.Combine(root,"sites","intl",asset),bytes);
            files[logical]=new JsonObject{["asset"]=asset,["size"]=bytes.Length};
        }
        Put("story.json","""{"episode":"episode.json","scene":"scene.json","ui":"ui/ui.json","stills":"stills.json","models":{}}""");
        Put("stills.json","""
        {"stills":{"test_still":{"nodes":[{
          "path":"Still","active":true,
          "rect":{"m_AnchorMin":{"x":0.5,"y":0.5},"m_AnchorMax":{"x":0.5,"y":0.5},
            "m_AnchoredPosition":{"x":0,"y":0},"m_SizeDelta":{"x":720,"y":480},"m_Pivot":{"x":0.5,"y":0.5}},
          "components":[
            {"class":"CanvasGroup","m_Enabled":1,"m_Alpha":1},
            {"class":"AdvStill","_canvasGroup":{"gameObject":"Still"},"_doTweenSequences":[]},
            {"class":"Image","m_Enabled":1,"m_Type":0,"m_Color":{"r":0.2,"g":0.8,"b":0.4,"a":1}}
          ]
        }]}}}
        """);
        // This is a lifecycle/presentation boundary test, not a replacement for
        // archived game shader parity. Tiny deterministic shaders keep the real
        // media camera's LUT and post pipeline executable without game assets.
        Put("shaders/shaders.json","""
        [
          {"name":"Hidden/Universal Render Pipeline/LutBuilderLdr","variants":[{"pass":0,"subShader":0,"keywords":[],"file":"media-lut.glsl"}]},
          {"name":"Hidden/Universal Render Pipeline/UberPost","variants":[{"pass":0,"subShader":0,"keywords":[],"file":"media-copy.glsl"}]}
        ]
        """);
        Put("shaders/media-lut.glsl","""
        #ifdef VERTEX
        #version 300 es
        in vec4 in_POSITION0;
        void main(){gl_Position=in_POSITION0;}
        #endif
        #ifdef FRAGMENT
        #version 300 es
        precision highp float;
        out vec4 result;
        void main(){result=vec4(0.0,0.0,0.0,1.0);}
        #endif
        """);
        Put("shaders/media-copy.glsl","""
        #ifdef VERTEX
        #version 300 es
        in vec4 in_POSITION0;
        in vec2 in_TEXCOORD0;
        out vec2 uv;
        void main(){gl_Position=in_POSITION0;uv=in_TEXCOORD0;}
        #endif
        #ifdef FRAGMENT
        #version 300 es
        precision highp float;
        uniform sampler2D _BlitTexture;
        in vec2 uv;
        out vec4 result;
        void main(){result=texture(_BlitTexture,uv);}
        #endif
        """);
        File.WriteAllText(manifestPath,manifest.ToJsonString());
        var document=RealtimeSceneFixture.Document();
        document["commands"]=JsonNode.Parse("""
        [
          {"cmd":"Delay","Frames":4},
          {"cmd":"Still","TargetAssetName":"test_still","Parameter1":0,"Parameter2":1,"Parameter3":-1},
          {"cmd":"Delay","Frames":12},
          {"cmd":"Still","TargetAssetName":"test_still","Parameter1":0,"Parameter3":-1},
          {"cmd":"Delay","Frames":4}
        ]
        """);
        File.WriteAllText(project,document.ToJsonString());return project;
    }
}
