using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed class Renderer : IDisposable
{
    readonly Archive archive;readonly string site,dll;readonly Shot shot;readonly OffscreenGl gl;readonly List<Actor> actors=[];uint? backgroundTexture;readonly string shotPath;
    int eventIndex;int currentFrame=-1;
    public Dictionary<int,string> StateHashes{get;}=[];
    public Dictionary<int,string[]> GeometryHashes{get;}=[];
    float backgroundAspect;float[] backgroundTint=[1,1,1,1];object? backgroundSource;
    public string Device=>gl.Device;
    public Renderer(Archive archive,string site,string dll,Shot shot,string shotPath)
    {
        this.archive=archive;this.site=site;this.dll=dll;this.shot=shot;this.shotPath=Path.GetFullPath(shotPath);shot.Validate();
        gl=new(shot.Output.Width*shot.Output.RenderScale,shot.Output.Height*shot.Output.RenderScale);
        try{
            for(int i=0;i<shot.Actors.Count;i++)actors.Add(new(archive,site,dll,shot.Actors[i],gl,shot.Seed+(uint)i*7919));
            foreach(var track in shot.Tracks.Where(t=>t.Property.StartsWith("param:")))if(!actors.First(a=>a.Spec.Name==track.Target).HasParameter(track.Property[6..]))throw new ArgumentException($"Unknown track parameter {track.Property}");
            foreach(var e in shot.Events){var a=actors.First(a=>a.Spec.Name==e.Actor);if(e.Motion!=null)a.Motion(e.Motion,0);if(e.Expression!=null)a.Expression(e.Expression,0);}
            foreach(var a in actors){a.Motion(a.Spec.Motion??a.DefaultMotion,0);a.Expression(a.Spec.Expression??a.DefaultExpression,0);}
            void LoadBackground(byte[] bytes,object source){var pixels=Png.Decode(bytes);backgroundAspect=pixels.Width/(float)pixels.Height;backgroundTexture=gl.Upload(pixels);backgroundSource=JsonOutput.Object(("source", source), ("sha256", Archive.Hash(bytes)));}
            if(shot.BackgroundImage is {} image){byte[] bytes;if(image.Path is not null){bytes=File.ReadAllBytes(Path.GetFullPath(image.Path,Path.GetDirectoryName(this.shotPath)!));}else if(image.Adv is not null&&image.LogicalPath is not null){bytes=archive.Logical(site,archive.Manifest(site,"stories",image.Adv),image.LogicalPath);}else throw new ArgumentException("backgroundImage needs path or adv+logicalPath");LoadBackground(bytes,image);}
            if(shot.Stage is {} stage){var manifest=Archive.ReadJson(archive.SitePath(site,"director/manifest.json"));var scene=JsonNode.Parse(archive.Logical(site,manifest,"scene.json"))!;var prefab=scene["stages"]?[stage]??throw new ArgumentException($"Stage not found: {stage}");var component=prefab["nodes"]![0]!["components"]!.AsArray().First(c=>Archive.S(c!,"class")=="AdvStage")!;var texture=component["_backgroundSprite"]?["texture"]?["texture"]?.ToString()??throw new NotSupportedException("Stage has no background sprite; full ADV stage rendering is not implemented");var c=component["_backgroundColor"];if(c is not null)backgroundTint=[Actor.N(c["r"],1),Actor.N(c["g"],1),Actor.N(c["b"],1),Actor.N(c["a"],1)];LoadBackground(archive.Logical(site,manifest,texture),JsonOutput.Object(("stage", stage), ("texture", texture), ("mode", "stage sprite backdrop; no stage particles/lighting")));}
            for(int f=0;f<shot.WarmupFrames;f++)foreach(var a in actors)a.Step(shot,0,1f/30);
            foreach(var a in actors){a.Motion(a.Spec.Motion??a.DefaultMotion,0);a.Expression(a.Spec.Expression??a.DefaultExpression,0);}
        }catch{Dispose();throw;}
    }
    public void Advance(int frame)
    {
        if(frame<currentFrame)throw new ArgumentException("Frames must be requested in ascending order");
        while(currentFrame<frame){currentFrame++;double time=currentFrame/30d;while(eventIndex<shot.Events.Count&&shot.Events[eventIndex].T<=time+1e-9){var e=shot.Events[eventIndex++];var a=actors.First(a=>a.Spec.Name==e.Actor);if(e.Motion!=null)a.Motion(e.Motion,e.Fade);if(e.Expression!=null)a.Expression(e.Expression,e.Fade);}foreach(var a in actors)a.Step(shot,time,1f/30);}
    }
    public Pixels Frame(int frame)
    {
        Advance(frame);StateHashes[frame]=Archive.Hash(JsonOutput.SerializeToUtf8Bytes(actors.Select(a=>a.Values)));gl.Begin(shot.Background);
        if(backgroundTexture is uint texture){float aspect=shot.Output.Width/(float)shot.Output.Height;float sx=Math.Max(1,backgroundAspect/aspect),sy=Math.Max(1,aspect/backgroundAspect);float[] xy=[-sx,-sy,sx,-sy,sx,sy,-sx,sy];double time=frame/30d;float zoom=shot.Value("camera","zoom",time,shot.Camera.Zoom),cx=shot.Value("camera","x",time,shot.Camera.X),cy=shot.Value("camera","y",time,shot.Camera.Y),roll=shot.Value("camera","roll",time,shot.Camera.Roll)*MathF.PI/180;for(int i=0;i<4;i++){float x=(xy[i*2]-cx*2)*aspect,y=xy[i*2+1]+cy*2;xy[i*2]=(x*MathF.Cos(roll)-y*MathF.Sin(roll))*zoom/aspect;xy[i*2+1]=(x*MathF.Sin(roll)+y*MathF.Cos(roll))*zoom;}gl.Draw(texture,xy,[0,1,1,1,1,0,0,0],[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],backgroundTint,0,false,false);}
        foreach(var a in actors)a.Draw(gl,shot,frame/30d);GeometryHashes[frame]=actors.Select(a=>a.GeometryHash).ToArray();gl.Grade(shot.Grade);return gl.Read(shot.Output.Width,shot.Output.Height);
    }
    public object Provenance()=>JsonOutput.Object(("tool", "ournotes-director/2"), ("backend", "native-cubism-wgl-director"), ("device", Device), ("archive", archive.Root), ("site", site), ("shotPath", shotPath), ("shot", shot), ("shotSha256", Archive.Hash(File.ReadAllBytes(shotPath))), ("coreSha256", Archive.Hash(File.ReadAllBytes(dll))), ("coreVersion", actors.First().Core.Version), ("rendererSha256", BuildIdentity.Sha256()), ("models", actors.Select(a=>JsonOutput.Object(("id", a.Spec.Model), ("manifestSha256", Archive.Hash(File.ReadAllBytes(archive.SitePath(site,$"models/{a.Spec.Model}.json")))), ("files", a.Manifest["files"])))), ("backgroundSource", backgroundSource), ("simulationHz", 30), ("alpha", "straight"), ("color", "RGBA8 gamma-space"), ("fidelity", "director: standard Cubism compositing; not game Lit-URP/ADV pixel parity"));
    public void Dispose(){foreach(var a in actors)a.Dispose();gl.Dispose();}

    public static object Run(Archive archive,string site,string dll,string mode,string shotPath,string output,double time,string codec,bool overwrite,bool resume,string ffmpeg="ffmpeg",string cineformQuality=VideoEncoding.DefaultCineFormQuality)
    {
        var shot=Shot.Read(shotPath);shot.Validate();if(mode is not ("frame" or "sequence" or "clip" or "contact"))throw new ArgumentException("render mode must be frame, sequence, clip or contact");
        if(!double.IsFinite(time)||time<0||time>=shot.Duration)throw new ArgumentException("--time must lie inside the shot");
        output=Path.GetFullPath(output);bool sequence=mode=="sequence";
        var sidecar=sequence?Path.Combine(output,"render.json"):output+".render.json";
        if(!overwrite&&!resume&&(File.Exists(output)||Directory.Exists(output)||File.Exists(sidecar)))throw new IOException("Output exists; use --overwrite or --resume (sequence only)");
        if(resume&&!sequence)throw new ArgumentException("--resume is only available for PNG sequences");
        if(mode=="clip"){codec=VideoEncoding.NormalizeCodec(codec);VideoEncoding.Validate(codec,shot.Output.Width,shot.Output.Height,cineformQuality);VideoEncoding.ValidateOutputPath(codec,output);}
        if(mode=="clip"&&codec=="h264"&&shot.Background[3]<1)throw new ArgumentException("h264 requires opaque background color; use cineform/prores/ffv1 for alpha");
        Directory.CreateDirectory(sequence?output:Path.GetDirectoryName(output)!);
        using var renderer=new Renderer(archive,site,dll,shot,shotPath);var provenance=renderer.Provenance();var jobHash=Archive.Hash(JsonOutput.SerializeToUtf8Bytes(provenance,Shot.JsonOptions));
        var hashes=new Dictionary<int,string>();
        if(resume&&File.Exists(sidecar)){using var old=JsonDocument.Parse(File.ReadAllBytes(sidecar));if(old.RootElement.GetProperty("jobHash").GetString()!=jobHash)throw new InvalidDataException("Resume job differs (shot/assets/backend/device); choose a new directory");foreach(var p in old.RootElement.GetProperty("frameHashes").EnumerateObject())hashes[int.Parse(p.Name,CultureInfo.InvariantCulture)]=p.Value.GetString()!;if(old.RootElement.TryGetProperty("stateHashes",out var states))foreach(var p in states.EnumerateObject())renderer.StateHashes[int.Parse(p.Name,CultureInfo.InvariantCulture)]=p.Value.GetString()!;if(old.RootElement.TryGetProperty("geometryHashes",out var geometry))foreach(var p in geometry.EnumerateObject())renderer.GeometryHashes[int.Parse(p.Name,CultureInfo.InvariantCulture)]=p.Value.EnumerateArray().Select(v=>v.GetString()!).ToArray();}
        else if(resume&&Directory.EnumerateFiles(output).Any())throw new InvalidDataException("Cannot resume without render.json");
        var watch=Stopwatch.StartNew();int total=(int)Math.Round(shot.Duration*30),written=0,reused=0;bool complete=false;string? temporary=null;Process? encoder=null;Task<string>? errorTask=null;
        if(sequence&&Directory.EnumerateFiles(output,"*.png").Any(p=>int.TryParse(Path.GetFileNameWithoutExtension(p),out var n)&&n>=total))throw new IOException("Sequence directory contains frames beyond this shot's duration; choose a new output directory");
        object Report()=>JsonOutput.Object(("provenance", provenance), ("jobHash", jobHash), ("complete", complete), ("frameCount", mode is "frame"?1:mode=="contact"?Math.Min(9,total):total), ("simulationFrames", renderer.currentFrame+1), ("warmupFrames", shot.WarmupFrames), ("output", output), ("sidecar", sidecar), ("frameHashes", hashes), ("stateHashes", renderer.StateHashes), ("geometryHashes", renderer.GeometryHashes), ("written", written), ("reused", reused), ("elapsedSeconds", watch.Elapsed.TotalSeconds), ("codec", mode=="clip"?codec:null), ("cineformQuality", mode=="clip"&&codec=="cineform"?VideoEncoding.NormalizeCineFormQuality(cineformQuality):null));
        void SaveReport(){var tmp=sidecar+".tmp";File.WriteAllText(tmp,JsonOutput.Serialize(Report(),Shot.JsonOptions));File.Move(tmp,sidecar,true);}
        void Atomic(string path,byte[] bytes){string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(tmp,bytes);File.Move(tmp,path,overwrite||resume);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
        try{
            if(mode=="frame"){int frame=(int)Math.Floor(time*30+1e-8);var bytes=Png.Encode(renderer.Frame(frame));Atomic(output,bytes);hashes[frame]=Archive.Hash(bytes);written=1;}
            else if(mode=="contact"){
                int count=Math.Min(9,total),tw=Math.Min(480,shot.Output.Width),th=(int)Math.Round(tw*shot.Output.Height/(double)shot.Output.Width);var sheet=new Pixels(tw*3,th*3,new byte[tw*3*th*3*4]);
                for(int i=0;i<count;i++){int frame=(int)Math.Round(i*(total-1d)/Math.Max(1,count-1));var image=renderer.Frame(frame);for(int y=0;y<th;y++)for(int x=0;x<tw;x++)Array.Copy(image.Rgba,((y*image.Height/th)*image.Width+x*image.Width/tw)*4,sheet.Rgba,(((i/3)*th+y)*sheet.Width+(i%3)*tw+x)*4,4);hashes[frame]=Archive.Hash(image.Rgba);}Atomic(output,Png.Encode(sheet));written=count;
            }
            else{
                if(mode=="clip"){
                    temporary=Path.Combine(Path.GetDirectoryName(output)!,Path.GetFileNameWithoutExtension(output)+"."+Guid.NewGuid().ToString("N")+Path.GetExtension(output));
                    var start=VideoEncoding.RawVideoStartInfo(ffmpeg,codec,shot.Output.Width,shot.Output.Height,30,temporary,cineformQuality);
                    encoder=Process.Start(start)??throw new IOException("Cannot start ffmpeg");errorTask=encoder.StandardError.ReadToEndAsync();
                }
                for(int f=0;f<total;f++){
                    string path=Path.Combine(output,$"{f:D6}.png");if(sequence&&resume&&hashes.TryGetValue(f,out var hash)&&File.Exists(path)&&Archive.Hash(File.ReadAllBytes(path))==hash){renderer.Advance(f);reused++;continue;}
                    var image=renderer.Frame(f);
                    if(sequence){var bytes=Png.Encode(image);Atomic(path,bytes);hashes[f]=Archive.Hash(bytes);SaveReport();}else {encoder!.StandardInput.BaseStream.Write(image.Rgba);hashes[f]=Archive.Hash(image.Rgba);}written++;
                    if(f%30==0)Console.Error.WriteLine($"render {f+1}/{total}");
                }
                if(encoder!=null){encoder.StandardInput.Close();encoder.WaitForExit();var errors=errorTask!.GetAwaiter().GetResult();if(encoder.ExitCode!=0)throw new IOException($"ffmpeg exited {encoder.ExitCode}: {errors}");File.Move(temporary!,output,overwrite);temporary=null;}
            }
            complete=true;SaveReport();return JsonOutput.Object(("output", output), ("sidecar", sidecar), ("complete", complete), ("frameCount", mode=="frame"?1:mode=="contact"?Math.Min(9,total):total), ("written", written), ("reused", reused), ("elapsedSeconds", watch.Elapsed.TotalSeconds), ("Device", renderer.Device), ("alpha", mode=="clip"&&codec=="h264"?"none":"straight"), ("jobHash", jobHash));
        }finally{VideoEncoding.StopEncoder(encoder,errorTask);if(temporary!=null&&File.Exists(temporary))File.Delete(temporary);}
    }
}
