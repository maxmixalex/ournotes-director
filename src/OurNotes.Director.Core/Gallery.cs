using System.Text.Json;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public static class Gallery
{
    public static void Tile(Pixels target,Pixels image,int column,int row,int width,int height)
    {
        float scale=Math.Min(width/(float)image.Width,height/(float)image.Height);int w=(int)(image.Width*scale),h=(int)(image.Height*scale),ox=column*width+(width-w)/2,oy=row*height+(height-h)/2;
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){int s=((y*image.Height/h)*image.Width+x*image.Width/w)*4;int d=((oy+y)*target.Width+ox+x)*4;float a=image.Rgba[s+3]/255f;for(int c=0;c<3;c++)target.Rgba[d+c]=(byte)(image.Rgba[s+c]*a+target.Rgba[d+c]*(1-a));target.Rgba[d+3]=255;}
    }
    static Pixels Sheet(int count,int w,int h){var image=new Pixels(w*4,h*((count+3)/4),new byte[w*4*h*((count+3)/4)*4]);for(int i=0;i<image.Rgba.Length;i+=4){image.Rgba[i]=30;image.Rgba[i+1]=33;image.Rgba[i+2]=42;image.Rgba[i+3]=255;}return image;}
    public static object Models(Archive archive,string site,string dll,string model,string kind,int offset,int limit,double time,string output,bool overwrite)
    {
        if(kind is not ("motion" or "expression"))throw new ArgumentException("gallery --kind must be motion or expression");
        if(time<0||time>60||!double.IsFinite(time)||limit>48)throw new ArgumentException("gallery time 0..60, limit <=48");
        var info=archive.Model(site,model);var all=info[kind=="motion"?"motions":"expressions"]!.AsArray().Select(n=>n!.ToString()).ToArray();var chosen=all.Skip(offset).Take(limit).ToArray();if(chosen.Length==0)throw new ArgumentException("Empty gallery page");
        output=Path.GetFullPath(output);if(!overwrite&&(File.Exists(output)||File.Exists(output+".json")))throw new IOException("Gallery exists");
        var sheet=Sheet(chosen.Length,320,360);var items=new List<object>();
        for(int i=0;i<chosen.Length;i++){
            var spec=new ActorSpec{Name="actor",Model=model};if(kind=="motion")spec.Motion=chosen[i];else spec.Expression=chosen[i];
            var shot=new Shot{Duration=Math.Ceiling((time+1)*30)/30,Output=new(){Width=640,Height=720,RenderScale=1},Camera=new(){Zoom=kind=="expression"?3:1,Y=kind=="expression"?-.24f:0},Actors=[spec]};
            using var renderer=new Renderer(archive,site,dll,shot,output+".shot.json");var frame=renderer.Frame((int)Math.Floor(time*30));Tile(sheet,frame,i%4,i/4,320,360);items.Add(JsonOutput.Object(("index", offset+i), ("id", chosen[i]), ("column", i%4), ("row", i/4), ("time", time)));Console.Error.WriteLine($"gallery {i+1}/{chosen.Length}: {chosen[i]}");
        }
        var result=JsonOutput.Object(("archive", archive.Root), ("site", site), ("model", model), ("kind", kind), ("total", all.Length), ("offset", offset), ("items", items), ("output", output), ("nextOffset", offset+chosen.Length<all.Length?(int?)(offset+chosen.Length):null));
        archive.Export(site,Png.Encode(sheet),output,JsonOutput.Object(("model", model), ("kind", kind)),overwrite);File.WriteAllText(output+".json",JsonOutput.Serialize(result,Shot.JsonOptions));return result;
    }
    public static object Assets(Archive archive,string site,string query,int offset,int limit,string output,bool overwrite)
    {
        if(limit>48)throw new ArgumentException("gallery limit <=48");
        var all=archive.Rows("files").Where(r=>Archive.S(r,"site")==site&&Archive.S(r,"logicalPath").EndsWith(".png",StringComparison.OrdinalIgnoreCase)&&Archive.S(r,"logicalPath").Contains(query,StringComparison.OrdinalIgnoreCase)).DistinctBy(r=>Archive.S(r,"asset")).ToArray();
        var chosen=all.Skip(offset).Take(limit).ToArray();if(chosen.Length==0)throw new ArgumentException("No PNG assets matched");output=Path.GetFullPath(output);if(!overwrite&&(File.Exists(output)||File.Exists(output+".json")))throw new IOException("Gallery exists");
        var sheet=Sheet(chosen.Length,320,180);var items=new List<object>();for(int i=0;i<chosen.Length;i++){var r=chosen[i];var pixels=Png.Decode(archive.Decode(site,Archive.S(r,"asset")));Tile(sheet,pixels,i%4,i/4,320,180);items.Add(JsonOutput.Object(("index", offset+i), ("column", i%4), ("row", i/4), ("width", pixels.Width), ("height", pixels.Height), ("source", r)));}
        var result=JsonOutput.Object(("archive", archive.Root), ("site", site), ("query", query), ("total", all.Length), ("offset", offset), ("items", items), ("output", output), ("nextOffset", offset+chosen.Length<all.Length?(int?)(offset+chosen.Length):null));archive.Export(site,Png.Encode(sheet),output,JsonOutput.Object(("query", query), ("offset", offset)),overwrite);File.WriteAllText(output+".json",JsonOutput.Serialize(result,Shot.JsonOptions));return result;
    }
}
