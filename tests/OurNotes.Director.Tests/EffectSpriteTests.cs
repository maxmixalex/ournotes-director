using System.Numerics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class EffectSpriteTests
{
    public static int BackgroundPlane()
    {
        string project=Path.GetFullPath("ournotes-director");var archive=new Archive(Path.Combine(project,"references","effect-samples"));
        using var renderer=new AdvStoryRenderer(archive,"intl",Path.Combine(project,"CubismSdkForNative-5-r.5","Core","dll","windows","x86_64","Live2DCubismCore.dll"),"10020","zh-Hans",320,180,Path.Combine(project,"samples","game-effect-character.json"),"ffmpeg",1);
        renderer.Frame(45);
        object Field(string name)=>typeof(AdvScene).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(renderer.Scene)!;
        var stage=(JsonNode)Field("stage");var originalSprite=stage["_backgroundSprite"]!;
        var backgroundRenderer=(JsonNode)Field("backgroundSpriteRenderer");
        var originalColor=backgroundRenderer["m_Color"]!.DeepClone();var initial=renderer.Frame(45).Rgba;
        backgroundRenderer["m_Color"]!["g"]=0;var tinted=renderer.Frame(45).Rgba;
        if(initial.SequenceEqual(tinted))throw new Exception("Background renderer tint was ignored");
        backgroundRenderer["m_Color"]=originalColor;
        if(!initial.SequenceEqual(renderer.Frame(45).Rgba))throw new Exception("Background tint did not restore");
        // The serialized flip is read when the renderer is constructed, as in the source SpriteRenderer host.
        backgroundRenderer["m_FlipX"]=Actor.B(backgroundRenderer["m_FlipX"])?0:1;
        typeof(AdvScene).GetField("backgroundDrawing",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(renderer.Scene,null);
        if(initial.SequenceEqual(renderer.Frame(45).Rgba))throw new Exception("Background sprite flip was ignored");
        stage["_backgroundSprite"]=null;
        var clear=(float[])Field("clearColor");clear[0]=1;clear[1]=clear[2]=0;clear[3]=1;
        var plane=(AdvTransform)Field("planeTransform");var scale=plane.Scale;
        var covered=renderer.Frame(45).Rgba;plane.Scale=Vector3.Zero;var uncovered=renderer.Frame(45).Rgba;plane.Scale=scale;
        int hidden=0;for(int i=0;i<covered.Length;i+=4)if(uncovered[i]>200&&uncovered[i+1]<20&&uncovered[i+2]<20&&covered[i]<20&&covered[i+1]<20&&covered[i+2]<20)hidden++;
        if(hidden<1000)throw new Exception($"Background plane did not cover the camera clear color without a sprite: {hidden} pixels");
        renderer.Scene.Camera.Euler=new(0,25,0);var rotated=renderer.Frame(45).Rgba;
        if(rotated.SequenceEqual(covered))throw new Exception("Background plane ignored camera rotation");
        var attributes=(Dictionary<string,float[]>)Field("planeAttributes");var lengths=attributes.ToDictionary(p=>p.Key,p=>p.Value.Length);
        for(int frame=46;frame<110;frame++)renderer.Frame(frame);
        if(attributes.Any(p=>p.Value.Length!=lengths[p.Key]))throw new Exception("Background attribute adaptation mutated cached source geometry");
        stage["_backgroundSprite"]=originalSprite;
        var restoredSprite=renderer.Frame(110).Rgba;stage["_backgroundSprite"]=null;
        if(restoredSprite.SequenceEqual(renderer.Frame(110).Rgba))throw new Exception("Background sprite failed to reappear after removal");
        Console.WriteLine($"Passed native background plane without sprite, material color, perspective and {hidden} occluded pixels.");return 0;
    }
    public static void SpriteSwapGpu()
    {
        var data=Data();using var gl=new OffscreenGl(128,128);using var materials=new AdvEffectMaterials(data.Archive,"intl",data.Manifest,gl);
        var components=data.Prefab["nodes"]!.AsArray().SelectMany(n=>n!["components"]!.AsArray()).OfType<JsonNode>().Where(c=>Archive.S(c,"type",Archive.S(c,"class"))=="SpriteRenderer").ToArray();
        var component=components[0].DeepClone();component["m_DrawMode"]=0;component["m_Enabled"]=1;component["m_Color"]=JsonNode.Parse("""{"r":1,"g":1,"b":1,"a":1}""");
        var a=component["m_Sprite"]!;a["vertices"]=JsonNode.Parse("[[ -0.8,-0.8,0],[0.8,-0.8,0],[-0.8,0.8,0],[0.8,0.8,0]]");a["uv"]=JsonNode.Parse("[[0,0],[1,0],[0,1],[1,1]]");a["indices"]=JsonNode.Parse("[0,2,1,1,2,3]");
        var b=a.DeepClone();foreach(var vertex in b["vertices"]!.AsArray())vertex![0]=Actor.N(vertex[0])*.5f;
        var sprite=new AdvEffectSprite(component,new());var identity=AdvMatrix.Identity();
        var globals=new Dictionary<string,float[]>{{"unity_MatrixVP",identity},{"unity_MatrixV",identity},{"unity_MatrixInvV",identity},{"glstate_matrix_projection",identity},{"_Time",[0,0,0,0]},{"_WorldSpaceCameraPos",[0,0,-1]},{"_ScreenParams",[128,128,1+1f/128,1+1f/128]}};
        byte[] Render(JsonNode? value){sprite.Sprite=value;gl.Begin([0,0,0,1]);materials.Draw(sprite,globals);return gl.Read(128,128).Rgba;}
        var initial=Render(a);var changed=Render(b);var hidden=Render(null);var restored=Render(a);
        if(initial.SequenceEqual(changed)||changed.SequenceEqual(hidden)||!initial.SequenceEqual(restored))throw new Exception("GPU sprite replacement/hide/restore failed");
        var alternative=components.Select(c=>c["m_Sprite"]!["texture"]!).FirstOrDefault(t=>!JsonNode.DeepEquals(t,a["texture"]));
        if(alternative is null)throw new Exception("Fixture needs distinct sprite textures");
        var different=a.DeepClone();different["texture"]=alternative.DeepClone();if(initial.SequenceEqual(Render(different)))throw new Exception("GPU retained old sprite texture");
    }
    public static int Sorting()
    {
        string project=Path.GetFullPath("ournotes-director");var archive=new Archive(Path.Combine(project,"references","effect-samples"));
        using var renderer=new AdvStoryRenderer(archive,"intl",Path.Combine(project,"CubismSdkForNative-5-r.5","Core","dll","windows","x86_64","Live2DCubismCore.dll"),"10020","zh-Hans",320,180,Path.Combine(project,"samples","game-effect-character.json"),"ffmpeg",1);
        renderer.Frame(45);var effect=renderer.Scene.Effects["storyteller"];
        if(!effect.Renderers.Any(r=>r.Order!=r.DefaultOrder))throw new Exception("Character-relative effect sorting band was not applied");
        effect.PositionType=0;effect.Layer=12;
        Pixels At(int offset){foreach(var r in effect.Renderers)r.Order=r.DefaultOrder+offset;return renderer.Frame(45);}
        var behind=At(-20000);var front=At(20000);if(behind.Rgba.SequenceEqual(front.Rgba))throw new Exception("Background-layer effects could not cross background/character sorting orders");
        // Layer 13 is excluded from the reference main-camera list, even when a placed effect remains active.
        effect.Layer=13;var excluded=renderer.Frame(45);effect.Root.Active=false;var disabled=renderer.Frame(45);
        if(!excluded.Rgba.SequenceEqual(disabled.Rgba))throw new Exception("Effect on an excluded camera layer was drawn");
        Console.WriteLine("Passed character effect bands, shared background/character/effect sorting and main-camera layer exclusion.");return 0;
    }
    public static int MeshMaterials()
    {
        var data=Data();using var gl=new OffscreenGl(128,128);using var materials=new AdvEffectMaterials(data.Archive,"intl",data.Manifest,gl);
        var r=Archive.ReadJson("ournotes-director/tests/OurNotes.Director.Tests/Fixtures/effect-mesh-particles.json")[0]!["renderer"]!.DeepClone();
        r["m_RenderAlignment"]=1;r["m_Pivot"]=new JsonObject();
        r["m_Mesh"]=JsonNode.Parse("""{"vertices":[[-0.8,-0.8,0],[0.8,-0.8,0],[0.8,0.8,0],[-0.8,0.8,0]],"submeshes":[[0,1,2],[0,2,3]]}""");
        var red=r["m_Materials"]![0]!.DeepClone();red["textures"]!["_MainTex"]!["texture"]=null;
        red["colors"]!["_TintColor"]=JsonNode.Parse("""{"r":1,"g":0,"b":0,"a":0.5}""");
        var blue=red.DeepClone();blue["colors"]!["_TintColor"]!["r"]=0;blue["colors"]!["_TintColor"]!["b"]=1;
        r["m_Materials"]=new JsonArray(red,blue);
        var system=new AdvParticles(new JsonObject(),new(1));((IList<AdvParticles.Particle>)system.Particles).Add(new(){Size=Vector3.One,Life=1,Color=[1,1,1,1]});
        var identity=AdvMatrix.Identity();var globals=new Dictionary<string,float[]>{{"unity_MatrixVP",identity},{"unity_MatrixV",identity},{"unity_MatrixInvV",identity},{"glstate_matrix_projection",identity},{"_WorldSpaceCameraPos",[0,0,-1]},{"_ScreenParams",[128,128,1+1f/128,1+1f/128]}};
        (int Red,int Blue) Render(){gl.Begin([0,0,0,1]);materials.Draw(new(system,new(),r),globals,identity,identity);var pixels=gl.Read(128,128).Rgba;int nr=0,nb=0;for(int i=0;i<pixels.Length;i+=4){if(pixels[i]>32&&pixels[i+2]==0)nr++;if(pixels[i+2]>32&&pixels[i]==0)nb++;}return(nr,nb);}
        var both=Render();
        // The same cached streams can be submitted repeatedly and to different materials.
        // Neither UV conversion nor shader attribute packing may modify them in place.
        var shared=new AdvEffectParticles(system,new(),r).Geometry(identity,identity);
        var originalStreams=shared.Attributes.ToDictionary(p=>p.Key,p=>(Reference:p.Value,Values:(float[])p.Value.Clone()));
        foreach(bool unityUvs in new[]{false,true}){
            byte[]? first=null;
            for(int draw=0;draw<3;draw++){
                gl.Begin([0,0,0,1]);
                for(int sub=0;sub<2;sub++)materials.DrawMesh(r["m_Materials"]![sub]!,globals,shared with{UnityUvs=unityUvs,Indices=shared.Submeshes![sub]});
                var pixels=gl.Read(128,128).Rgba;
                if(first is not null&&!first.SequenceEqual(pixels))throw new Exception("Repeated shared mesh draw changed pixels");
                first=pixels;
                foreach(var (key,originalStream) in originalStreams)
                    if(!ReferenceEquals(shared.Attributes[key],originalStream.Reference)||!originalStream.Values.SequenceEqual(shared.Attributes[key]))throw new Exception("Material conversion mutated shared mesh stream: "+key);
            }
        }
        // A regular effect MeshFilter/MeshRenderer must use the same original material shaders and submesh mapping.
        r["m_Mesh"]!["uv0"]=JsonNode.Parse("[[0,0],[1,0],[1,1],[0,1]]");r["m_Enabled"]=1;
        var filter=new JsonObject{["type"]="MeshFilter",["m_Mesh"]=r["m_Mesh"]!.DeepClone()};
        var nativeMesh=new AdvEffectMesh(filter,r,new());
        gl.Begin([0,0,0,1]);nativeMesh.Draw(materials,globals);var meshPixels=gl.Read(128,128).Rgba;int meshRed=0,meshBlue=0;
        for(int i=0;i<meshPixels.Length;i+=4){if(meshPixels[i]>32&&meshPixels[i+2]==0)meshRed++;if(meshPixels[i+2]>32&&meshPixels[i]==0)meshBlue++;}
        if((meshRed,meshBlue)!=both)throw new Exception("Regular mesh submaterials differ from equivalent particle mesh");
        var exported=data.Prefab.DeepClone();string root=Archive.S(exported["nodes"]![0]!,"path");var component=r.DeepClone();component["type"]="MeshRenderer";component.AsObject().Remove("class");component["m_SortingOrder"]=3;
        exported["nodes"]!.AsArray().Add(new JsonObject{["path"]=root+"/NativeMesh",["active"]=true,["layer"]=12,["components"]=new JsonArray(filter.DeepClone(),component)});
        var instance=new AdvEffectInstance(exported,new(722),new(exported));var entry=instance.Renderers.Single(e=>e.Mesh is not null);
        instance.Initialize();if(entry.Mesh!.Visible)throw new Exception("Initialized mesh effect remained visible");
        instance.Play();if(!entry.Mesh.Visible)throw new Exception("Mesh effect did not activate");instance.SetRelativeOrder(1000,1200,1999);if(entry.Order<=1200)throw new Exception("Mesh effect missed character sorting band");
        instance.SetOrder(-50);if(entry.Order!=3)throw new Exception("Particle-only order overwrote mesh default order");instance.Stop(true,new());if(entry.Mesh.Visible)throw new Exception("Stopped mesh effect remained visible");
        var bad=r.DeepClone();bad["m_Materials"]!.AsArray().RemoveAt(1);try{new AdvEffectMesh(filter,bad,new());throw new Exception("Missing mesh submaterial accepted");}catch(InvalidDataException){}
        var source=r["m_Mesh"]!;source.AsObject().Remove("uv0");var original=source["vertices"]!.AsArray();var padded=new JsonArray();for(int i=0;i<65536;i++)padded.Add(new JsonArray(0,0,0));foreach(var v in original)padded.Add(v!.DeepClone());source["vertices"]=padded;
        foreach(var sub in source["submeshes"]!.AsArray())for(int i=0;i<sub!.AsArray().Count;i++)sub[i]=(int)Actor.N(sub[i])+65536;
        var wide=Render();r["m_Materials"]!.AsArray().RemoveAt(1);var single=Render();
        if(both.Red<4000||both.Blue<4000||wide!=both||single.Red!=both.Red||single.Blue!=0){Console.Error.WriteLine($"Mesh material rendering differs: both={both}, wide={wide}, single={single}");return 1;}
        Console.WriteLine("Passed native GPU mesh submaterial colors, 32-bit vertex indices and missing-material submesh suppression.");return 0;
    }
    // Original StorySession, frame 45, seed 722, the corresponding game-effect sample at 640x360.
    public static int CompareStory(string reference,bool dim)
    {
        string project=Path.GetFullPath("ournotes-director");var archive=new Archive(Path.Combine(project,"references","effect-samples"));
        using var renderer=new AdvStoryRenderer(archive,"intl",Path.Combine(project,"CubismSdkForNative-5-r.5","Core","dll","windows","x86_64","Live2DCubismCore.dll"),"10020","zh-Hans",640,360,Path.Combine(project,"samples",dim?"game-effect-dim.json":"game-effect-character.json"),"ffmpeg",2);
        var actual=renderer.Frame(45);var expected=Png.Decode(File.ReadAllBytes(reference));double squares=0,interiorSquares=0;int maximum=0,interiorCount=0;
        if(actual.Width!=expected.Width||actual.Height!=expected.Height)throw new Exception("Reference dimensions differ");
        for(int i=0;i<actual.Rgba.Length;i++){if(i%4==3)continue;int difference=Math.Abs(actual.Rgba[i]-expected.Rgba[i]);squares+=(double)difference*difference;maximum=Math.Max(maximum,difference);int x=i/4%actual.Width,y=i/4/actual.Width;if(x>=2&&x<actual.Width-2&&y>=34&&y<326){interiorSquares+=(double)difference*difference;interiorCount++;}}
        double rms=Math.Sqrt(squares/(actual.Width*actual.Height*3));Console.WriteLine($"Original character/effect scene (dim={dim}): RGB RMS {rms:F6}, maximum {maximum}.");
        double interiorRms=Math.Sqrt(interiorSquares/interiorCount);Console.WriteLine($"Interior RGB RMS {interiorRms:F6} (excluding viewport borders).");
        if(rms>5||interiorRms>3)throw new Exception("Character/effect scene differs from original reference");return 0;
    }
    public static int Compare(string reference)
    {
        string directory=Path.GetFullPath("ournotes-director/output"),path=Path.Combine(directory,"effect-comparison-"+Guid.NewGuid().ToString("N")+".png");
        try{
            Preview(path);var actual=Png.Decode(File.ReadAllBytes(path));var expected=Png.Decode(File.ReadAllBytes(reference));
            if(actual.Width!=expected.Width||actual.Height!=expected.Height)throw new Exception("Effect reference dimensions differ");
            double squares=0;int maximum=0;for(int i=0;i<actual.Rgba.Length;i++){if(i%4==3)continue;int difference=Math.Abs(actual.Rgba[i]-expected.Rgba[i]);squares+=(double)difference*difference;maximum=Math.Max(maximum,difference);}
            double rms=Math.Sqrt(squares/(actual.Width*actual.Height*3));Console.WriteLine($"Original effect GPU reference: RGB RMS {rms:F6}, maximum {maximum}.");
            if(rms>1)throw new Exception("Native effect rendering differs from original GPU reference");return 0;
        }finally{if(!Path.GetFullPath(path).StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Cleanup outside test output");if(File.Exists(path))File.Delete(path);}
    }
    static (Archive Archive,JsonNode Manifest,JsonNode Prefab) Data()
    {
        var archive=new Archive(Path.GetFullPath("ournotes-director/references/effect-samples"));var manifest=archive.Manifest("intl","stories","10020");
        var data=JsonNode.Parse(archive.Logical("intl",manifest,"effects.json"))!;return(archive,manifest,data["effects"]!.AsObject().First().Value!);
    }
    public static int Run(string fixture)
    {
        var data=Data();int checks=0;float max=0;
        foreach(var test in Archive.ReadJson(fixture).AsArray()){
            var node=data.Prefab["nodes"]!.AsArray().First(n=>Archive.S(n!,"path")==Archive.S(test!,"path"))!;
            var c=node["components"]!.AsArray().First(c=>Archive.S(c!,"type",Archive.S(c!,"class"))=="SpriteRenderer")!.DeepClone();c["m_FlipX"]=Actor.B(test!["flip"])?1:0;c["m_FlipY"]=Actor.B(test["flip"])?1:0;
            var sprite=new AdvEffectSprite(c,new AdvTransform()){Color=new(.25f,.5f,.75f,.33f)};bool uniform=Actor.B(test["uniform"]);var attributes=sprite.Attributes(uniform,uniform);var pos=attributes["in_POSITION0"];var uv=attributes["in_TEXCOORD0"];var color=attributes["in_COLOR0"];var expected=test["vertices"]!.AsArray();
            if(expected.Count!=pos.Length/3*9||!sprite.Geometry().Indices.SequenceEqual(test["indices"]!.AsArray().Select(v=>(ushort)Actor.N(v))))throw new Exception("Effect sprite topology differs");
            for(int i=0;i<pos.Length/3;i++){float[] actual=[pos[i*3],pos[i*3+1],pos[i*3+2],color[i*4],color[i*4+1],color[i*4+2],color[i*4+3],uv[i*2],1-uv[i*2+1]];for(int k=0;k<9;k++){float error=Math.Abs(actual[k]-Actor.N(expected[i*9+k]));max=Math.Max(max,error);if(error>.00001f)throw new Exception($"Effect sprite {checks}, vertex {i}, component {k}: {error}");}}
            if(test["worldCenter"] is not null){sprite.Transform.Position=new(1,2,3);sprite.Transform.Scale=new(2,.6f,1.3f);sprite.Transform.SetEuler(new(15,32,18));var center=sprite.WorldCenter();for(int k=0;k<3;k++)if(Math.Abs(center[k]-Actor.N(test["worldCenter"]![k]))>.00001f)throw new Exception("Sprite sorting bounds differ");}
            checks++;
        }
        Console.WriteLine($"Passed {checks} original effect sprite meshes; maximum error {max:R}.");return 0;
    }
    public static int Animation(string fixture)
    {
        var data=Data();var prefab=new AdvPrefab(data.Prefab);var sprites=new Dictionary<string,AdvEffectSprite>();
        foreach(var n in data.Prefab["nodes"]!.AsArray())foreach(var c in n!["components"]!.AsArray())if(Archive.S(c!,"type",Archive.S(c!,"class"))=="SpriteRenderer"){
            string path=Archive.S(n,"path");sprites[path]=new(c!,prefab.Transform(path));
        }
        var entry=prefab.Find("Animator");var animation=new AdvEffectAnimation(entry.Component,entry.Path,prefab,new(data.Prefab),sprites,new Dictionary<string,AdvParticles>());
        if(animation.TryPlay("missing-state"))throw new Exception("Unknown effect state accepted");
        float max=0;int checks=0;
        foreach(var sample in Archive.ReadJson(fixture).AsArray()){
            int frame=(int)Actor.N(sample!["frame"]);
            if(frame==50)prefab.Root.Active=false;
            if(frame==60)prefab.Root.Active=true;
            if(frame==90&&!animation.TryPlay("edv_effect_spotlight_loop"))throw new Exception("Exported effect state missing");
            if(frame==120)animation.Speed=2;
            animation.Step(1f/30);
            foreach(var pair in sample["values"]!.AsObject()){
                float error=Math.Abs(sprites[entry.Path+"/"+pair.Key].Color.W-Actor.N(pair.Value));max=Math.Max(max,error);
                if(error>.00001f)throw new Exception($"Effect animation frame {frame}, {pair.Key}: {error}");checks++;
            }
        }
        Console.WriteLine($"Passed {checks} original effect animation values; maximum error {max:R}.");return 0;
    }
    public static int Preview(string output)
    {
        var data=Data();var prefab=new AdvPrefab(data.Prefab);using var gl=new OffscreenGl(640,360);using var materials=new AdvEffectMaterials(data.Archive,"intl",data.Manifest,gl);
        var sprites=new List<AdvEffectSprite>();foreach(var n in data.Prefab["nodes"]!.AsArray())foreach(var c in n!["components"]!.AsArray())if(Archive.S(c!,"type",Archive.S(c!,"class"))=="SpriteRenderer")sprites.Add(new(c!,prefab.Transform(Archive.S(n,"path"))));
        var particles=new List<AdvEffectParticles>();var random=new AdvRandom(0x41504546);
        foreach(var n in data.Prefab["nodes"]!.AsArray()){
            var components=n!["components"]!.AsArray();var component=components.FirstOrDefault(c=>Archive.S(c!,"type",Archive.S(c!,"class"))=="ParticleSystem");
            var renderer=components.FirstOrDefault(c=>Archive.S(c!,"type",Archive.S(c!,"class"))=="ParticleSystemRenderer");
            if(component is null||renderer?["m_Materials"]?[0] is null)continue;
            var system=new AdvParticles(component,random);system.Play();particles.Add(new(system,prefab.Transform(Archive.S(n,"path")),renderer));
        }
        var p=AdvMatrix.Identity();p[0]=.2f;p[5]=.35555556f;p[10]=-.01f;p[13]=-.6f;
        gl.Begin([.04f,.04f,.04f,1]);var globals=new Dictionary<string,float[]>{{"unity_MatrixVP",p},{"unity_MatrixV",AdvMatrix.Identity()},{"unity_MatrixInvV",AdvMatrix.Identity()},{"_Time",[0,0,0,0]}};
        globals["_ScreenParams"]=[640,360,1+1f/640,1+1f/360];globals["_ProjectionParams"]=[1,.1f,100, .01f];globals["unity_OrthoParams"]=[5,2.8125f,0,1];
        globals["glstate_matrix_projection"]=p;globals["_WorldSpaceCameraPos"]=[0,0,0];
        foreach(var sprite in sprites.OrderBy(s=>s.SortingLayer).ThenBy(s=>s.SortingOrder))materials.Draw(sprite,globals);
        foreach(var particle in particles)materials.Draw(particle,globals,AdvMatrix.Identity(),p);
        File.WriteAllBytes(output,Png.Encode(gl.Read(640,360)));Console.WriteLine(output);return 0;
    }
}
