using System.Text.Json;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public static class DirectorSequence
{
    public static object Run(Archive archive,string site,string dll,string adv,string language,string script,string output,int width,int height,int supersample,double time,double duration,bool clean,bool resume,bool untilEnd,string ffmpeg)
    {
        if(!double.IsFinite(time)||!double.IsFinite(duration)||time<0||duration<=0||time+duration>36000)throw new ArgumentException("Invalid sequence time/duration");
        output=Path.GetFullPath(output);string report=Path.Combine(output,"render.json");
        if(Directory.Exists(output)&&!resume)throw new IOException("Sequence directory exists; use a new directory or --resume");
        if(resume&&Directory.Exists(output)&&Directory.EnumerateFileSystemEntries(output).Any()&&!File.Exists(report))throw new InvalidDataException("Cannot resume without render.json");
        using var renderer=new AdvStoryRenderer(archive,site,dll,adv,language,width,height,script,ffmpeg,supersample);
        var document=DirectorProject.Document(script);var dependencyHashes=new SortedDictionary<string,string>();
        string models=archive.SitePath(site,"models");
        foreach(string file in Directory.EnumerateFiles(models,"*.json"))dependencyHashes[Path.GetFileName(file)]=Archive.Hash(File.ReadAllBytes(file));
        foreach(var row in new[]{"initialize","commands","finalize"}.SelectMany(key=>(document[key] as JsonArray??new JsonArray()).OfType<JsonNode>())){
            if(row["Values"]?["backgroundImage"]?["path"] is {} image){string path=Path.GetFullPath(image.ToString(),Path.GetDirectoryName(Path.GetFullPath(script))!);dependencyHashes[path]=Archive.Hash(File.ReadAllBytes(path));}
        }
        string jobHash=Archive.Hash(JsonOutput.SerializeToUtf8Bytes(JsonOutput.Object(("document", document), ("site", site), ("adv", adv), ("language", language), ("width", width), ("height", height), ("supersample", supersample), ("time", time), ("duration", duration), ("clean", clean), ("untilEnd", untilEnd), ("Root", archive.Root), ("Device", renderer.Device), ("dependencyHashes", dependencyHashes), ("storyManifest", archive.Manifest(site,"stories",adv)), ("core", Archive.Hash(File.ReadAllBytes(dll))), ("engine", BuildIdentity.Sha256()))));
        var hashes=new Dictionary<int,string>();
        if(File.Exists(report)){
            var prior=Archive.ReadJson(report);if(Archive.S(prior,"jobHash")!=jobHash)throw new InvalidDataException("Resume input, resources, renderer or GPU changed; choose a new directory");
            foreach(var pair in prior["frameHashes"]!.AsObject())hashes[int.Parse(pair.Key)]=pair.Value!.ToString();
        }
        Directory.CreateDirectory(output);int count=(int)Math.Ceiling(duration*30),start=(int)Math.Floor(time*30+1e-8),written=0,reused=0,frames=0;bool complete=false,finished=false;
        foreach(string file in Directory.EnumerateFiles(output,"*.png"))if(int.TryParse(Path.GetFileNameWithoutExtension(file),out int n)&&n>=count)throw new IOException("Sequence contains frames beyond requested duration");
        void Atomic(string path,byte[] bytes){string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(temporary,bytes);File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}}
        void Save()=>Atomic(report,JsonOutput.SerializeToUtf8Bytes(JsonOutput.Object(("jobHash", jobHash), ("complete", complete), ("finished", finished), ("frames", frames), ("frameHashes", hashes), ("width", width), ("height", height), ("fps", 30), ("alpha", "straight"), ("Device", renderer.Device), ("source", Path.GetFullPath(script))),Shot.JsonOptions));
        Save();
        try{
            for(int i=0;i<count;i++){
                // Re-simulate and draw to preserve capture/animation state; resume avoids
                // PNG compression and rewriting verified output, not timeline replay.
                var image=renderer.Frame(start+i,clean);string path=Path.Combine(output,$"{i:D6}.png");
                if(resume&&hashes.TryGetValue(i,out string? expected)&&File.Exists(path)&&Archive.Hash(File.ReadAllBytes(path))==expected)reused++;
                else{byte[] bytes=Png.Encode(image);Atomic(path,bytes);hashes[i]=Archive.Hash(bytes);written++;}
                frames=i+1;finished=renderer.Player.Finished;Save();
                if(untilEnd&&finished)break;
            }
            if(untilEnd&&!finished)throw new InvalidOperationException("Sequence did not finish within --duration; partial frames retained for resume");
            complete=true;Save();return JsonOutput.Object(("output", output), ("sidecar", report), ("complete", complete), ("finished", finished), ("frames", frames), ("written", written), ("reused", reused), ("jobHash", jobHash));
        }finally{Save();}
    }
}
