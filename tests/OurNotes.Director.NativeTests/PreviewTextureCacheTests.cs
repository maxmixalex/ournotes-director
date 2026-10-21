using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class PreviewTextureCacheTests
{
    public static void Run(Action<bool,string> check)
    {
        static byte[] PngBytes(int size,byte value)=>Png.Encode(new(size,size,Enumerable.Repeat(value,size*size*4).ToArray()));
        byte[] a=PngBytes(8,17),b=PngBytes(8,89),c=PngBytes(8,201);
        var cache=new PreviewTextureCache(512);
        var first=cache.Read(a);cache.Read(b);var repeated=cache.Read(a.ToArray());
        check(ReferenceEquals(first,repeated)&&cache.Hits==1&&cache.Decodes==2&&cache.RetainedBytes==512,
            "Verified identical PNG bytes reuse immutable pixels within the configured cache budget");
        cache.Read(c);cache.Read(b);
        check(cache.Decodes==4&&cache.Count==2&&cache.RetainedBytes<=cache.LimitBytes,
            "Model PNG cache evicts the least recently used entry before exceeding its byte limit");
        var large=cache.Read(PngBytes(32,57));
        check(large.Width==32&&cache.Count==2&&cache.RetainedBytes<=cache.LimitBytes,
            "A texture larger than the cache budget decodes without being retained");
        check(first.Rgba.All(value=>value==17),"Evicting a texture never mutates pixels already returned to a renderer");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();long attempts=cache.Decodes;bool rejected=false;
        try{cache.Read(a,cancelled.Token);}catch(OperationCanceledException){rejected=true;}
        check(rejected&&cache.Decodes==attempts&&cache.RetainedBytes==512,
            "Cancelled texture loading does not insert partial pixels or start a decode");
        rejected=false;
        try{cache.Read(new byte[8]);}catch(InvalidDataException){rejected=true;}
        check(rejected&&cache.Count==2&&cache.Read(b).Rgba.All(value=>value==89),
            "A malformed texture cannot replace a valid cached image");

        string root=Path.Combine(Path.GetTempPath(),"ournotes-texture-cache-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"index"));File.WriteAllText(Path.Combine(root,"index","stats.json"),"{}");
            var archive=new Archive(root){PreviewTextures=new(512)};
            JsonObject Put(byte[] bytes)
            {
                string asset="assets/"+Archive.Hash(bytes)+".png",path=archive.SitePath("intl",asset);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);
                return new(){["asset"]=asset,["size"]=bytes.Length};
            }
            var entry=Put(a);var manifest=new JsonObject{["files"]=new JsonObject{["model.png"]=entry}};
            archive.ModelTexture("intl",manifest,"model.png");string path=archive.SitePath("intl",Archive.S(entry,"asset"));
            File.Delete(path);rejected=false;
            try{archive.ModelTexture("intl",manifest,"model.png");}catch(FileNotFoundException){rejected=true;}
            check(rejected,"A cached PNG cannot hide a missing archive asset during reconstruction");
            File.WriteAllBytes(path,a.Select(value=>(byte)(value^1)).ToArray());rejected=false;
            try{archive.ModelTexture("intl",manifest,"model.png");}catch(InvalidDataException){rejected=true;}
            check(rejected,"A cached PNG cannot hide a content hash failure in the current archive bytes");
            File.WriteAllBytes(path,a);manifest["files"]!["model.png"]=Put(b);
            var replacement=archive.ModelTexture("intl",manifest,"model.png");
            check(replacement.Rgba.All(value=>value==89),"Replacing an asset manifest decodes the newly verified source bytes");
            archive.LoadCancellation=cancelled.Token;rejected=false;
            try{archive.ModelTexture("intl",manifest,"model.png");}catch(OperationCanceledException){rejected=true;}
            check(rejected&&archive.Logical("intl",manifest,"model.png").SequenceEqual(b),
                "A foreground texture cancellation never contaminates ordinary archive reads used by shared PCM decoding");
            archive.LoadCancellation=default;
            check(ReferenceEquals(replacement,archive.ModelTexture("intl",manifest,"model.png")),
                "Clearing a completed construction token permits verified cache reuse on the next request");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
