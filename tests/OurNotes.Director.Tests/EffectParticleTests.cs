using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class EffectParticleTests
{
    public static int SubEmitters(string fixture)
    {
        int checks=0;float maximum=0;
        foreach(var test in Archive.ReadJson(fixture).AsArray()){
            var parent=new AdvParticles(test!["parent"]!,new(1));var child=new AdvParticles(test["child"]!,new(1));int frame=0;
            parent.LinkSubEmitters(_=>child);child.ActiveInHierarchy=()=>frame<30||frame>=40;
            void Configure(){parent.SetTransform(new(){Position=new(1+frame*.01f,.5f,0)});child.SetTransform(new(){Position=new(-2+frame*.01f,-1,0)});}
            Configure();parent.Play();if(test["playChild"] is null||Actor.B(test["playChild"]))child.Play();
            foreach(var state in test["states"]!.AsArray()){
                Configure();parent.Simulate(1f/30);child.Simulate(1f/30);
                for(int s=0;s<2;s++){
                    var actual=s==0?parent:child;var expected=state![s]!;
                    if(actual.Particles.Count!=expected["particles"]!.AsArray().Count)throw new Exception($"Sub-emitter count differs case {checks/100}, frame {frame}, system {s}: {actual.Particles.Count} != {expected["particles"]!.AsArray().Count}");
                    void Compare(float a,float b){float error=Math.Abs(a-b);maximum=Math.Max(maximum,error);if(error>.0001f)throw new Exception($"Sub-emitter differs frame {frame}, system {s}: {a} != {b}");}
                    Compare(actual.Time,Actor.N(expected["time"]));
                    for(int i=0;i<actual.Particles.Count;i++){var a=actual.Particles[i];var e=expected["particles"]![i]!;Compare(a.Age,Actor.N(e["age"]));Compare(a.Life,Actor.N(e["life"]));for(int k=0;k<3;k++)Compare(a.Position[k],Actor.N(e["pos"]![k]));}
                }
                checks++;frame++;
            }
            if(test["playChild"] is not null&&!Actor.B(test["playChild"]))continue;
            var parentData=test["parent"]!.DeepClone();var childData=test["child"]!.DeepClone();parentData["class"]="ParticleSystem";childData["class"]="ParticleSystem";
            foreach(var reference in parentData["SubModule"]!["subEmitters"]!.AsArray())reference!["emitter"]!["gameObject"]="root/child";
            var prefabData=JsonNode.Parse("""{"nodes":[{"path":"root","localPosition":{"x":1,"y":0.5},"components":[]},{"path":"root/child","localPosition":{"x":-3,"y":-1.5},"components":[]}]}""")!;
            var rootComponents=prefabData["nodes"]![0]!["components"]!.AsArray();rootComponents.Add(parentData);rootComponents.Add(JsonNode.Parse("""{"class":"AdvParticleEffect","_particleSystem":{"gameObject":"root"}}"""));
            prefabData["nodes"]![1]!["components"]!.AsArray().Add(childData);
            foreach(var node in prefabData["nodes"]!.AsArray())node!["components"]!.AsArray().Add(JsonNode.Parse("""{"class":"ParticleSystemRenderer","m_Materials":[]}"""));
            var prefab=new AdvPrefab(prefabData);var instance=new AdvEffectInstance(prefabData,new(1),new(prefabData),prefab);instance.Initialize();instance.Play();
            for(int f=0;f<25;f++){
                instance.Root.Position=new(1+f*.01f,.5f,0);instance.Simulate(1f/30);
                for(int s=0;s<2;s++){
                    var particles=instance.Renderers[s].Particles!.System.Particles;var expected=test["states"]![f]![s]!["particles"]!.AsArray();
                    if(particles.Count!=expected.Count)throw new Exception("Scene host sub-emitter count differs");
                    for(int i=0;i<particles.Count;i++)for(int k=0;k<3;k++)if(Math.Abs(particles[i].Position[k]-Actor.N(expected[i]!["pos"]![k]))>.0001f)throw new Exception("Scene host sub-emitter position differs");
                }
            }
            instance.Stop(true,new AdvLoop());if(instance.Renderers.Any(r=>r.Particles!.System.Particles.Count!=0))throw new Exception("Stopping the effect left sub-emitter particles alive");
        }
        Console.WriteLine($"Passed {checks} original parent/sub-emitter frames and 300 scene host frames; maximum error {maximum:G9}.");return 0;
    }
    public static int Canvas()
    {
        var archive=new Archive(Path.GetFullPath("ournotes-director/references/effect-samples"));var manifest=archive.Manifest("intl","stories","10020");var data=JsonNode.Parse(archive.Logical("intl",manifest,"effects.json"))!;
        var components=data["effects"]!.AsObject().First().Value!["nodes"]!.AsArray().First(n=>Archive.S(n!,"path").EndsWith("/EfDustL"))!["components"]!.AsArray();
        using var gl=new OffscreenGl(320,180);using var shaders=new UnityShaderLibrary(archive,"intl",manifest);uint white=gl.Upload(new(2,2,[255,0,0,255,0,255,0,255,0,0,255,255,255,255,255,255]));
        foreach(int space in new[]{0,1}){
            var ps=components.First(c=>Archive.S(c!,"type",Archive.S(c!,"class"))=="ParticleSystem")!.DeepClone();ps["moveWithTransform"]=space;ps["scalingMode"]=2;
            var renderer=Archive.ReadJson("ournotes-director/tests/OurNotes.Director.Tests/Fixtures/effect-particle-meshes.json")[0]!["renderer"]!.DeepClone();renderer["m_RenderMode"]=0;renderer["m_MaxParticleSize"]=.5f;renderer["m_RenderAlignment"]=0;renderer["m_Materials"]![0]!["shader"]!["shader"]="Mobile/Particles/Additive";
            var record=JsonNode.Parse("""{"path":"ui","active":true,"rect":{"m_AnchorMin":{"x":0.5,"y":0.5},"m_AnchorMax":{"x":0.5,"y":0.5}},"components":[{"class":"UIParticle","m_Enabled":1,"m_Particles":[],"m_Scale3D":{"x":1,"y":1,"z":1},"m_PositionMode":1}]}""")!;
            record["components"]!.AsArray().Add(ps);record["components"]!.AsArray().Add(renderer);var node=new AdvCanvasNode(record,null);node.Layout(1920,1080);
            var nodes=new Dictionary<string,AdvCanvasNode>{{"ui",node}};var particles=new AdvCanvasParticles(nodes,new(1),1920,1080,5,gl,shaders,_=>white);
            particles.Update(true,0,1f/30);node.Particles!.Clear();((IList<AdvParticles.Particle>)node.Particles.Particles).Add(new(){Position=new(0,0,space==1?100:0),Size=new(2,2,2),Life=10,Color=[1,1,1,1],Random=new float[43]});
            foreach(string module in new[]{"SizeModule","ColorModule","NoiseModule","UVModule"})if(node.Particles.Data[module] is JsonNode m)m["enabled"]=false;
            gl.Begin([0,0,0,1]);particles.Draw(node);var before=gl.Read(320,180);node.AnchoredPosition=new(400,0);node.Layout(1920,1080);particles.Update(true,1,0);gl.Begin([0,0,0,1]);particles.Draw(node);var after=gl.Read(320,180);
            if(!before.Rgba.Where((v,i)=>i%4!=3).Any(v=>v>0)||before.Rgba.SequenceEqual(after.Rgba)!=(space==1)){Console.Error.WriteLine("UI particle simulation-space placement failed: "+space+", lit="+before.Rgba.Where((v,i)=>i%4!=3).Count(v=>v>0)+", equal="+before.Rgba.SequenceEqual(after.Rgba));return 1;}
            renderer["m_UseCustomVertexStreams"]=true;renderer["m_VertexStreams"]=new JsonArray(0,1,3,4);gl.Begin([0,0,0,1]);particles.Draw(node);
            if(!gl.Read(320,180).Rgba.SequenceEqual(after.Rgba)){Console.Error.WriteLine("Custom UI particle UV stream differs from default layout");return 1;}
            renderer["m_UseCustomVertexStreams"]=false;
            renderer["m_Materials"]![0]!["textures"]!["_MainTex"]!["scale"]=JsonNode.Parse("""{"x":0,"y":0}""");gl.Begin([0,0,0,1]);particles.Draw(node);
            if(gl.Read(320,180).Rgba.SequenceEqual(after.Rgba)){Console.Error.WriteLine("UI particle material texture scaling was ignored");return 1;}
            var zeroUv=gl.Read(320,180);renderer["m_Materials"]![0]!["textures"]!["_MainTex"]!["scale"]=JsonNode.Parse("""{"x":1,"y":1}""");renderer["m_UseCustomVertexStreams"]=true;renderer["m_VertexStreams"]=new JsonArray(0);
            gl.Begin([0,0,0,1]);particles.Draw(node);
            if(!gl.Read(320,180).Rgba.SequenceEqual(zeroUv.Rgba)){Console.Error.WriteLine("Missing UI particle UV/color streams do not use the source defaults");return 1;}
            float time=node.Particles.Time;node.Alpha=.005f;particles.Update(true,2,1f/30);node.Alpha=1;node.Scale=System.Numerics.Vector3.Zero;node.Layout(1920,1080);particles.Update(true,3,1f/30);gl.Begin([0,0,0,1]);particles.Draw(node);
            if(node.Particles.Time!=time||gl.Read(320,180).Rgba.Where((v,i)=>i%4!=3).Any(v=>v>0)){Console.Error.WriteLine("Hidden or zero-volume UI particles were simulated/rendered");return 1;}
        }
        Console.WriteLine("Passed native UI particle GPU placement, material texture scaling and hidden/zero-volume simulation checks.");return 0;
    }
    public static int World(string fixture)
    {
        int frames=0;float maximum=0;
        foreach(var test in Archive.ReadJson(fixture).AsArray()){
            var system=new AdvParticles(test!["component"]!,new(1));var transform=new AdvTransform{Position=new(1,2,3),Scale=new(2,.6f,1.3f)};transform.SetEuler(new(15,32,18));system.SetTransform(transform);if(test["renderer"] is JsonNode renderer)_=new AdvEffectParticles(system,transform,renderer);system.Play();
            foreach(var frame in test["frames"]!.AsArray()){
                var matrix=frame!["matrix"]!;transform.Position=new(Actor.N(matrix[12]),Actor.N(matrix[13]),Actor.N(matrix[14]));system.SetTransform(transform);system.Simulate(1f/30);frames++;
                var expected=frame["particles"]!.AsArray();if(expected.Count!=system.Particles.Count){Console.Error.WriteLine($"World particle count differs at {frames}: {system.Particles.Count} != {expected.Count}");return 1;}
                for(int i=0;i<expected.Count;i++){
                    if(expected[i]?["flip"] is JsonArray flip)for(int k=0;k<3;k++)if(system.Particles[i].Flip[k]!=Actor.N(flip[k])){Console.Error.WriteLine("Particle random flip differs");return 1;}
                    if(expected[i]?["align"] is JsonArray align)for(int j=0;j<3;j++)for(int k=0;k<3;k++)if(system.Particles[i].Alignment is not {Length:3} axes||Math.Abs(axes[j][k]-Actor.N(align[j]![k]))>.00001f){Console.Error.WriteLine("Particle initial alignment differs");return 1;}
                }
                for(int i=0;i<expected.Count;i++)foreach(var (key,v) in new[]{("pos",system.Particles[i].Position),("vel",system.Particles[i].Velocity),("anim",system.Particles[i].Animated)})for(int k=0;k<3;k++){
                    float error=Math.Abs(v[k]-Actor.N(expected[i]![key]![k]));maximum=Math.Max(maximum,error);if(error>.0001f){Console.Error.WriteLine($"World particle {frames}/{i}/{key}/{k}: {error}; position={system.Particles[i].Position}, velocity={system.Particles[i].Velocity}, animated={system.Particles[i].Animated}; expected={expected[i]}");return 1;}
                }
            }
        }
        Console.WriteLine($"Passed {frames} world particle simulation states; maximum error {maximum:G9}.");return 0;
    }
    public static int Groups(string fixture)
    {
        var data=Archive.ReadJson(fixture);var exported=data["prefab"]!;var prefab=new AdvPrefab(exported);var collection=prefab.Find("AdvStage").Component["_particleEffectGroupCollection"]!;
        var loop=new AdvLoop();var groups=new AdvEffectGroups(exported,prefab,collection,new(exported),new(0x41504546),loop);if(groups.Instances.Count!=2)throw new Exception("Stage group shared effect duplicated");int checks=0;
        foreach(var expected in data["states"]!.AsArray()){
            int frame=(int)Actor.N(expected!["frame"]);if(frame is 0 or 160)groups.Play();if(frame is 30 or 60)groups.Change(1);if(frame==90)groups.Change(2);if(frame==110)groups.Change(99);if(frame==130)groups.Change(0);if(frame==150)groups.Stop();
            foreach(var effect in groups.Instances)effect.Animate(1f/30);foreach(var effect in groups.Instances)effect.Simulate(1f/30);loop.Drain();
            if(groups.Current!=(int)Actor.N(expected["current"]))throw new Exception("Stage group index not retained");
            for(int i=0;i<groups.Instances.Count;i++){
                var effect=groups.Instances[i];var e=expected["effects"]![i]!;var state=System.Text.Json.JsonSerializer.SerializeToNode(effect.Snapshot())!;
                if(effect.Playing!=Actor.B(e["playing"])||effect.Visible!=Actor.B(e["visible"])||!state["systems"]!.AsArray().Select(s=>(int)Actor.N(s!["count"])).SequenceEqual(e["counts"]!.AsArray().Select(c=>(int)Actor.N(c))))throw new Exception($"Stage effects differ at frame {frame}, effect {i}");
                if(effect.Renderers.Any(r=>r.Layer!=12))throw new Exception("Stage effect lost serialized renderer layer");
            }checks++;
        }
        Console.WriteLine($"Passed {checks} original stage effect group states and shared references.");return 0;
    }
    public static int GroupScene(string fixture)
    {
        var archive=new Archive(Path.GetFullPath("ournotes-director/references/effect-samples"));var manifest=archive.Manifest("intl","stories","10020");var definition=JsonNode.Parse(archive.Logical("intl",manifest,"story.json"))!;var scene=JsonNode.Parse(archive.Logical("intl",manifest,"scene.json"))!;
        var stage=scene["stages"]!.AsObject().First();var exported=stage.Value!;var prefab=new AdvPrefab(exported);string root=prefab.Find("AdvStage").Path;
        var sample=Archive.ReadJson(fixture)["prefab"]!.DeepClone();
        void Rebase(JsonNode? node){if(node is JsonArray a){foreach(var n in a)Rebase(n);}else if(node is JsonObject o)foreach(var pair in o.ToArray()){if(pair.Key is "path" or "gameObject" or "transform"&&pair.Value is JsonValue v&&v.TryGetValue<string>(out var text)&&text.StartsWith("stage/",StringComparison.Ordinal))o[pair.Key]=root+text[5..];else Rebase(pair.Value);}}
        Rebase(sample);
        var collection=sample["nodes"]![0]!["components"]![0]!["_particleEffectGroupCollection"]!.DeepClone();prefab.Find("AdvStage").Component["_particleEffectGroupCollection"]=collection;
        foreach(var node in sample["nodes"]!.AsArray().Skip(1))exported["nodes"]!.AsArray().Add(node!.DeepClone());
        var loop=new AdvLoop();using var gl=new OffscreenGl(320,180);using var native=new AdvScene(archive,"intl","",manifest,definition,scene,gl,loop,new(loop));
        var groups=native.StageEffectGroups[stage.Key];native.SetStage(stage.Key);if(!groups.Instances[0].Playing||groups.Instances[1].Playing)throw new Exception("Stage initial group not played");
        void Change(int index){var row=new AdvRow("0","StageEnv",new JsonObject{["Parameter1"]="Effect",["Parameter2"]=index.ToString()});var player=new AdvPlayback(loop,new AdvProgram{Rows=[row]});native.Bind(player);player.Register("TalkWindow",(r,p)=>[]);player.Prepare=p=>[];player.Start();loop.Drain();player.ThrowIfFailed();}
        Change(1);if(groups.Instances.Any(e=>!e.Playing))throw new Exception("StageEnv did not play shared group");
        native.Camera.Position=new(0,1,-10);native.Camera.Euler=System.Numerics.Vector3.Zero;foreach(var effect in groups.Instances)effect.Root.Position=new(0,0,5);
        loop.Step();native.Draw();var light=gl.Read(320,180);
        Change(2);native.Draw();var dark=gl.Read(320,180);if(groups.Instances.Any(e=>e.Visible)||light.Rgba.SequenceEqual(dark.Rgba))throw new Exception("StageEnv empty group did not change rendered pixels");
        Change(0);native.SetStage("");if(groups.Instances.Any(e=>e.Playing))throw new Exception("Leaving stage did not stop effects");
        native.SetStage(stage.Key);if(!groups.Instances[0].Playing||groups.Current!=0)throw new Exception("Reentering stage lost selected group");
        Console.WriteLine("Passed 5 native stage effect rendering and StageEnv command checks.");return 0;
    }
    public static int Scene()
    {
        string project=Path.GetFullPath("ournotes-director");var archive=new Archive(Path.Combine(project,"references","effect-samples"));
        using var renderer=new AdvStoryRenderer(archive,"intl",Path.Combine(project,"CubismSdkForNative-5-r.5","Core","dll","windows","x86_64","Live2DCubismCore.dll"),"10020","zh-Hans",320,180,Path.Combine(project,"samples","game-effect.json"),"ffmpeg",2);
        var dark=renderer.Frame(10);var effect=renderer.Scene.Effects["storyteller"];if(effect.Playing||effect.Visible)throw new Exception("Effect visible before command");
        var light=renderer.Frame(35);if(!effect.Playing||!effect.Visible||dark.Rgba.SequenceEqual(light.Rgba))throw new Exception("Effect command did not render on stage");
        effect.Layer=8;var orphan=renderer.Frame(35);if(!dark.Rgba.SequenceEqual(orphan.Rgba))throw new Exception("Offscreen rendering drew an effect without a matching character group");effect.Layer=12;
        var stopped=renderer.Frame(83);if(effect.Playing||effect.Visible||!dark.Rgba.SequenceEqual(stopped.Rgba))throw new Exception("Effect stop did not restore stage");
        renderer.Frame(110);if(!effect.Playing||!effect.Visible)throw new Exception("Effect failed to replay requested animation");
        renderer.Frame(190);if(effect.Playing||effect.Visible||!renderer.Player.Finished)throw new Exception("Effect script did not finish cleanly");
        Console.WriteLine("Passed 6 native Effect command/stage/replay/stop checks.");return 0;
    }
    public static int Instance(string fixture)
    {
        var archive=new Archive(Path.GetFullPath("ournotes-director/references/effect-samples"));var manifest=archive.Manifest("intl","stories","10020");var data=JsonNode.Parse(archive.Logical("intl",manifest,"effects.json"))!;
        var effect=new AdvEffectInstance(data["effects"]!.AsObject().First().Value!,new AdvRandom(0x41504546),new(data));var loop=new AdvLoop();effect.Initialize();int checks=0;
        foreach(var expected in Archive.ReadJson(fixture).AsArray()){
            int frame=(int)Actor.N(expected!["frame"]);
            if(frame is 0 or 50 or 130)effect.Play(1);if(frame==30)effect.Stop(false,loop);if(frame==70)effect.TryPlayState("edv_effect_spotlight_loop");
            if(frame==90){effect.SetSpeed(2);effect.SetRelativeOrder(1000,1075,1999);}if(frame==120)effect.Stop(true,loop);if(frame==150)effect.SetOrder(23);if(frame==170)effect.Stop(false,loop);
            effect.Animate(1f/30);effect.Simulate(1f/30);loop.Drain();
            var actual=System.Text.Json.JsonSerializer.SerializeToNode(effect.Snapshot())!;
            void Match(JsonNode? a,JsonNode? e,string path){
                if(e is JsonObject obj){foreach(var pair in obj)if(pair.Key!="frame")Match(a?[pair.Key],pair.Value,path+"."+pair.Key);}
                else if(e is JsonArray array){if(a is not JsonArray aa||aa.Count!=array.Count)throw new Exception("Effect instance array differs");for(int i=0;i<array.Count;i++)Match(a[i],array[i],path+"["+i+"]");}
                else if(e is JsonValue value&&value.TryGetValue<double>(out double number)){if(a is null||Math.Abs(double.Parse(a.ToString(),System.Globalization.CultureInfo.InvariantCulture)-number)>.0001)throw new Exception($"Effect frame {frame} {path}: {a} != {e}");}
                else if(!JsonNode.DeepEquals(a,e))throw new Exception($"Effect frame {frame} {path}: {a} != {e}");
            }
            Match(actual,expected,"");checks++;
        }
        Console.WriteLine($"Passed {checks} original effect instance lifecycle and sorting states.");return 0;
    }
    public static int Meshes(string fixture)
    {
        var archive=new Archive(Path.GetFullPath("ournotes-director/references/effect-samples"));var manifest=archive.Manifest("intl","stories","10020");
        var data=JsonNode.Parse(archive.Logical("intl",manifest,"effects.json"))!;
        var node=data["effects"]!.AsObject().First().Value!["nodes"]!.AsArray().First(n=>Archive.S(n!,"path").EndsWith("/EfDustL"))!;
        var component=node["components"]!.AsArray().First(c=>Archive.S(c!,"type",Archive.S(c!,"class"))=="ParticleSystem")!;
        static float[] Array(JsonNode? n)=>n!.AsArray().Select(v=>Actor.N(v)).ToArray();
        static System.Numerics.Vector3 V(JsonNode? n){var a=Array(n);return new(a[0],a[1],a[2]);}
        float max=0;int count=0;
        foreach(var test in Archive.ReadJson(fixture).AsArray()){
            var system=new AdvParticles(component,new AdvRandom(1));system.Data["scalingMode"]=(int)Actor.N(test!["scaling"]);system.Data["moveWithTransform"]=(int)Actor.N(test["simulationSpace"]);
            if(test["alignToDirection"] is not null)system.Data["ShapeModule"]!["alignToDirection"]=Actor.B(test["alignToDirection"]);
            if(test["customData"] is not null)system.Data["CustomDataModule"]=test["customData"]!.DeepClone();
            if(test["rotation3D"] is not null)system.Data["InitialModule"]!["rotation3D"]=Actor.B(test["rotation3D"]);
            foreach(var p in test["particles"]!.AsArray())((IList<AdvParticles.Particle>)system.Particles).Add(new(){Position=V(p!["pos"]),Velocity=V(p["vel"]),Animated=V(p["anim"]),Age=Actor.N(p["age"]),Life=Actor.N(p["life"]),Size=V(p["size0"]),Rotation=V(p["rot"]),Spin=Actor.N(p["spin"]),Color=Array(p["color0"]),Random=Array(p["rnd"]),Stable=p["stable"] is null?new float[4]:Array(p["stable"]),UvRandom=Actor.N(p["uvr"]?[0]),UvStart=Actor.N(p["uvr"]?[1]),Flip=p["flip"] is null?System.Numerics.Vector3.One:V(p["flip"]),Alignment=p["align"]?.AsArray().Select(V).ToArray()});
            var transform=new AdvTransform{Position=new(1,2,3),Scale=new(2,.6f,1.3f)};transform.SetEuler(new(15,32,18));
            var renderer=new AdvEffectParticles(system,transform,test["renderer"]!);var mesh=renderer.Geometry(Array(test["camera"]),Array(test["projection"]),test["cameraVelocity"] is null?default:V(test["cameraVelocity"]));
            if(test["canvas"] is JsonNode canvas)mesh=AdvCanvasParticleMesh.Build(system,transform,test["renderer"]!,Actor.N(canvas["width"]),Actor.N(canvas["height"]),Actor.N(canvas["canvasScale"]),V(canvas["scale"]),V(canvas["uiPosition"]),Actor.B(canvas["absolute"]));
            void Compare(float actual,float expected){float error=Math.Abs(actual-expected);max=Math.Max(max,error);if(error>.00003f)throw new Exception($"Effect mesh {count}: {actual} != {expected}");}
            if(test["worldCenter"] is not null){var center=renderer.WorldCenter();for(int k=0;k<3;k++)Compare(center[k],Actor.N(test["worldCenter"]![k]));}
            for(int k=0;k<16;k++)Compare(mesh.Matrix[k],Actor.N(test["matrix"]![k]));
            if(!mesh.Indices.SequenceEqual(test["indices"]!.AsArray().Select(n=>(uint)Actor.N(n))))throw new Exception("Effect particle indices differ");
            if(test["submeshes"] is JsonArray subs){
                if(mesh.Submeshes is null||mesh.Submeshes.Count!=subs.Count)throw new Exception("Effect particle submesh count differs");
                for(int i=0;i<subs.Count;i++)if(!mesh.Submeshes[i].SequenceEqual(subs[i]!.AsArray().Select(n=>(uint)Actor.N(n))))throw new Exception("Effect particle submesh indices differ");
            }
            int stride=(int)Actor.N(test["stride"]);var expected=Array(test["vertices"]);
            foreach(var pair in test["attributes"]!.AsObject()){
                int width=(int)Actor.N(pair.Value![0]),offset=(int)Actor.N(pair.Value[1]);var actual=mesh.Attributes[pair.Key];
                for(int i=0;i<expected.Length/stride;i++)for(int k=0;k<width;k++)Compare(!mesh.UnityUvs&&pair.Key=="in_TEXCOORD0"&&k==1?1-actual[i*width+k]:actual[i*width+k],expected[i*stride+offset+k]);
            }
            count++;
        }
        Console.WriteLine($"Passed {count} original particle meshes; maximum error {max:R}.");return 0;
    }
    public static int Run(string fixture)
    {
        var archive=new Archive(Path.GetFullPath("ournotes-director/references/effect-samples"));
        var manifest=archive.Manifest("intl","stories","10020");
        var data=JsonNode.Parse(archive.Logical("intl",manifest,"effects.json"))!;
        var nodes=data["effects"]!.AsObject().First().Value!["nodes"]!.AsArray();int checks=0;
        foreach(var test in Archive.ReadJson(fixture).AsArray()){
            string path=Archive.S(test!,"path");var node=nodes.First(n=>Archive.S(n!,"path")==path)!;
            var ps=new AdvParticles(node["components"]!.AsArray().First(c=>Archive.S(c!,"type",Archive.S(c!,"class"))=="ParticleSystem")!,new AdvRandom(0x41504546));
            ps.Play();
            foreach(var state in test!["states"]!.AsArray()){
                int frame=(int)Actor.N(state!["frame"]);if(frame>0)ps.Simulate(1f/30);
                if(frame==30)ps.Stop();if(frame==50)ps.Play();if(frame==90)ps.Stop(true);if(frame==120)ps.Play();
                if(Math.Abs(ps.Time-Actor.N(state["time"]))>.0001f||ps.Particles.Count!=(int)Actor.N(state["count"])||ps.Playing!=Actor.B(state["playing"])||ps.Emitting!=Actor.B(state["emitting"]))
                    throw new Exception($"{path} frame {frame}: native time={ps.Time}, count={ps.Particles.Count}, playing={ps.Playing}, emitting={ps.Emitting}; reference {state}");
                checks++;
            }
        }
        Console.WriteLine($"Passed {checks} original effect particle lifecycle states.");return 0;
    }
}
