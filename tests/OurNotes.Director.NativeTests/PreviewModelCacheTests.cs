using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class PreviewModelCacheTests
{
    public static void Run(Action<bool,string> check)
    {
        VerifyNumbers(check);
        string root=Path.Combine(Path.GetTempPath(),"ournotes-model-cache-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"index"));File.WriteAllText(Path.Combine(root,"index","stats.json"),"{}");
            var cache=new PreviewModelCache(16384);var archive=new Archive(root){PreviewModels=cache};
            JsonObject Put(byte[] bytes)
            {
                string asset="assets/"+Archive.Hash(bytes)+".json.gz",path=archive.SitePath("intl",asset);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using(var output=File.Create(path))using(var compressor=new GZipStream(output,CompressionLevel.SmallestSize))compressor.Write(bytes);
                return new(){["asset"]=asset,["size"]=bytes.Length,["stored"]=new FileInfo(path).Length};
            }
            byte[] bytes=Encoding.UTF8.GetBytes("{\"finite\":0.125,\"huge\":1e999,\"tiny\":-1e-999}");
            var entry=Put(bytes);var files=new JsonObject{["model.json"]=entry};var manifest=new JsonObject{["files"]=files};
            byte[] original=archive.ModelResource("intl",manifest,"model.json");
            check(ReferenceEquals(original,archive.ModelResource("intl",manifest,"model.json"))&&original.SequenceEqual(bytes)
                &&cache.AssetHits==1&&cache.AssetDecodes==1,
                "Verified compressed model bytes reuse one immutable decode without normalizing numeric tokens");
            string path=archive.SitePath("intl",Archive.S(entry,"asset"));byte[] stored=File.ReadAllBytes(path);
            File.Delete(path);Reject<FileNotFoundException>(()=>archive.ModelResource("intl",manifest,"model.json"),
                "Model source cache never hides a missing current archive file",check);
            byte[] corrupt=stored.ToArray();corrupt[corrupt.Length/2]^=0x40;File.WriteAllBytes(path,corrupt);
            Reject<InvalidDataException>(()=>archive.ModelResource("intl",manifest,"model.json"),
                "Same-size compressed source corruption cannot reuse an earlier valid model decode",check);
            File.WriteAllBytes(path,stored);entry["size"]=bytes.Length+1;
            Reject<InvalidDataException>(()=>archive.ModelResource("intl",manifest,"model.json"),
                "A cached source still enforces the current manifest decoded-size constraint",check);
            entry["size"]=bytes.Length;entry["stored"]=stored.Length+1;
            Reject<InvalidDataException>(()=>archive.ModelResource("intl",manifest,"model.json"),
                "A cached source still enforces the current manifest stored-size constraint",check);
            entry["stored"]=stored.Length;
            byte[] replacement=Encoding.UTF8.GetBytes("{\"finite\":0.25}");files["model.json"]=Put(replacement);
            check(archive.ModelResource("intl",manifest,"model.json").SequenceEqual(replacement),
                "Replacing a model manifest uses the new verified asset instead of stale source bytes");
            var part=Put(Encoding.UTF8.GetBytes("[1e999,-0.0,0.00100]"));
            var parts=new JsonArray(new JsonArray("nodes",part["asset"]!.ToString(),part["size"]!.GetValue<int>(),part["stored"]!.GetValue<long>()));
            files["prefab.json"]=new JsonObject{["parts"]=parts};
            byte[] assembled=archive.ModelResource("intl",manifest,"prefab.json");
            check(ReferenceEquals(assembled,archive.ModelResource("intl",manifest,"prefab.json"))
                &&Encoding.UTF8.GetString(assembled)=="{\"nodes\":[1e999,-0.0,0.00100]}"&&cache.MaterializationHits==1,
                "Split prefab assembly reuses verified bytes while preserving original numeric lexemes");
            string partPath=archive.SitePath("intl",part["asset"]!.ToString());File.Delete(partPath);
            Reject<FileNotFoundException>(()=>archive.ModelResource("intl",manifest,"prefab.json"),
                "A cached combined prefab still reads and validates every current part before returning",check);
            _=Put(Encoding.UTF8.GetBytes("[1e999,-0.0,0.00100]"));parts.Add(parts[0]!.DeepClone());
            Reject<InvalidDataException>(()=>archive.ModelResource("intl",manifest,"prefab.json"),
                "Prefab cache retains the duplicate split-key validation",check);
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();archive.LoadCancellation=cancelled.Token;
            Reject<OperationCanceledException>(()=>archive.ModelResource("intl",manifest,"model.json"),
                "Cancelled foreground model loading never publishes a cached source",check);
            check(archive.Logical("intl",manifest,"model.json").SequenceEqual(replacement),
                "Foreground model cancellation does not poison ordinary archive reads used by independent audio decoders");
            archive.LoadCancellation=default;
            check(archive.ModelResource("intl",manifest,"model.json").SequenceEqual(replacement),
                "A later construction can reuse complete validated sources after cancellation");
            VerifyBounds(check);VerifyClips(check);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    static void VerifyNumbers(Action<bool,string> check)
    {
        var samples=new JsonArray();
        foreach(string raw in new[]{"0","-0","-0.0","0.00100","1e999","-1e999","1e-999","-1e-999",
            "3.4028235e38","1.1754943508222875e-38","1.401298464324817e-45","16777217","-16777217","1.234567890123456789"})
            samples.Add(JsonNode.Parse(raw));
        samples.Add((JsonNode)JsonValue.Create("0.125"));samples.Add((JsonNode)JsonValue.Create("Infinity"));samples.Add((JsonNode)JsonValue.Create("NaN"));
        samples.Add((JsonNode)JsonValue.Create(0.1d));samples.Add((JsonNode)JsonValue.Create(.2m));samples.Add((JsonNode)JsonValue.Create(.3f));samples.Add((JsonNode)JsonValue.Create(7));
        foreach(var sample in samples)
        {
            float old=float.Parse(sample!.ToString(),CultureInfo.InvariantCulture),current=Actor.N(sample);
            if(BitConverter.SingleToInt32Bits(old)!=BitConverter.SingleToInt32Bits(current))throw new InvalidOperationException("Numeric conversion differs for "+sample);
        }
        check(Actor.N(null,.75f)==.75f,"Direct numeric reads preserve float bits, signed zero, overflow, string numbers and nullable defaults");
        Reject<FormatException>(()=>Actor.N(JsonValue.Create("not a number")),"Invalid numeric strings keep their original failure",check);
    }

    static void VerifyBounds(Action<bool,string> check)
    {
        var cache=new PreviewModelCache(1900);
        byte[] a=Enumerable.Repeat((byte)17,600).ToArray(),b=Enumerable.Repeat((byte)89,600).ToArray();
        byte[] Read(byte[] bytes)=>cache.ReadAsset("source",bytes,bytes.Length,()=>bytes.ToArray(),default);
        byte[] old=Read(a);Read(b);Read(a);
        check(cache.AssetDecodes==3&&cache.Count==1&&cache.RetainedBytes<=cache.LimitBytes&&old.SequenceEqual(a),
            "The model LRU evicts before its conservative byte budget and never mutates previously returned data");
        Read(new byte[4096]);
        check(cache.Count==1&&cache.RetainedBytes<=cache.LimitBytes,
            "Oversized model resources remain usable without being retained or evicting smaller cached resources");
        var disabled=new PreviewModelCache(0);disabled.ReadAsset("source",a,a.Length,()=>a,default);
        check(disabled.Count==0&&disabled.RetainedBytes==0,"A zero model-cache budget retains no decoded resources");
        using var cancel=new CancellationTokenSource();long count=cache.Count;
        Reject<OperationCanceledException>(()=>cache.ReadAsset("cancel",b,b.Length,()=>{cancel.Cancel();return b;},cancel.Token),
            "Cancellation after resource creation does not insert a partially accepted cache entry",check);
        check(cache.Count==count&&cache.RetainedBytes<=cache.LimitBytes,"Cancelled model insertion preserves earlier complete entries and capacity accounting");
    }

    static void VerifyClips(Action<bool,string> check)
    {
        var motion=JsonNode.Parse("""
            {"clip":"test","stopTime":2,"startTime":0,"cycleOffset":0,"dense":{"curveCount":0},
             "events":[{"functionName":"InstanceId","intParameter":12}],
             "bindings":[{"path":"Parameters/A","attribute":""},{"path":"","attribute":"EyeOpening"}],
             "streamed":{"curveCount":1,"frames":[[0,[[0,0,0,0.5,1.25]]],[1,[[0,0,0,0,2]]]]},"constant":[0.75]}
            """)!;
        byte[] prefab=Encoding.UTF8.GetBytes(motion.ToJsonString());var cache=new PreviewModelCache(16384);
        Actor.Clip[] Read(string[] ids)=>cache.Clips(prefab,ids,()=>[new Actor.Clip(motion,ids.Select((s,i)=>(s,i)).ToDictionary(pair=>pair.s,pair=>pair.i))],default);
        var clips=Read(["A","B"]);var reused=Read(["A","B"]);var reordered=Read(["B","A"]);
        float[] values=new float[2],other=new float[2];float eye=1,opacity=1;clips[0].Sample(.5f,false,values,ref eye,ref opacity);
        float otherEye=1,otherOpacity=1;reordered[0].Sample(.5f,false,other,ref otherEye,ref otherOpacity);
        check(ReferenceEquals(clips,reused)&&!ReferenceEquals(clips,reordered)&&values[0]==1.5f&&other[1]==1.5f&&eye==.75f&&otherEye==.75f,
            "Immutable motion compilation reuses exact source and parameter ordering while preserving polynomial values and controller fields");
        values[0]=100;float[] next=new float[2];reused[0].Sample(.5f,false,next,ref eye,ref opacity);
        check(next[0]==1.5f&&next[1]==0&&cache.ClipHits==1&&cache.ClipCompilations==2,
            "Cached motion sampling writes only the caller's parameter state, with no cross-actor mutation");
        motion["stopTime"]=3;byte[] changed=Encoding.UTF8.GetBytes(motion.ToJsonString());
        var replacement=cache.Clips(changed,["A","B"],()=>[new Actor.Clip(motion,new(){["A"]=0,["B"]=1})],default);
        check(!ReferenceEquals(clips,replacement)&&replacement[0].Length==3,
            "A changed verified prefab does not reuse previous motion metadata");
        var noIds=cache.Clips(changed,[],()=>[new Actor.Clip(motion,[])],default);
        var emptyId=cache.Clips(changed,[""],()=>[new Actor.Clip(motion,new(){[""]=0})],default);
        check(!ReferenceEquals(noIds,emptyId),"Motion keys distinguish an empty parameter list from a single empty native ID");
    }

    static void Reject<T>(Func<object?> action,string name,Action<bool,string> check) where T:Exception
    {
        bool rejected=false;try{_=action();}catch(T){rejected=true;}check(rejected,name);
    }
}
