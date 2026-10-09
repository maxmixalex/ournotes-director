using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

// ScreenCanvas tiledImage: sprite rect determines tile size; packed UVs stretch across that rect.
public static class AdvImageTiles
{
    public readonly record struct Quad(float Left,float Bottom,float Right,float Top,float U0,float V0,float U1,float V1);
    public static bool Repeats(JsonNode? sprite)=>sprite is not null&&Actor.N(sprite["texture"]?["settings"]?["m_WrapU"],1)==0&&Actor.N(sprite["texture"]?["settings"]?["m_WrapV"],1)==0&&((int)Actor.N(sprite["settingsRaw"])&1)==0;
    public static IEnumerable<Quad> Build(float width,float height,float left,float bottom,JsonNode image)
    {
        var sprite=image["m_Sprite"];
        if(sprite is not null&&new[]{"x","y","z","w"}.Any(k=>Actor.N(sprite["border"]?[k])!=0))throw new NotSupportedException("Tiled sprite with a border is unsupported by the reference");
        if(!Actor.B(image["m_FillCenter"]))yield break;
        float ppu=sprite is null?1:Actor.N(sprite["pixelsToUnits"],100)/100*Actor.N(image["m_PixelsPerUnitMultiplier"],1);
        float tw=sprite is null?100:Actor.N(sprite["rect"]?["width"])/ppu,th=sprite is null?100:Actor.N(sprite["rect"]?["height"])/ppu;
        if(tw<=0)tw=width;if(th<=0)th=height;if(width<=0||height<=0)yield break;
        float texw=Actor.N(sprite?["texture"]?["width"],1),texh=Actor.N(sprite?["texture"]?["height"],1);
        float u=Actor.N(sprite?["textureRect"]?["x"])/texw,v=Actor.N(sprite?["textureRect"]?["y"])/texh,uw=Actor.N(sprite?["textureRect"]?["width"])/texw,vh=Actor.N(sprite?["textureRect"]?["height"])/texh;
        if(sprite is null||Repeats(sprite)){float su=width/tw,sv=height/th;yield return new(left,bottom,left+width,bottom+height,u*su,v*sv,(u+uw)*su,(v+vh)*sv);yield break;}
        int nx=checked((int)Math.Ceiling(width/tw)),ny=checked((int)Math.Ceiling(height/th));
        if((long)nx*ny*4>65000)throw new InvalidDataException("Too many sprite tiles");
        for(int y=0;y<ny;y++)for(int x=0;x<nx;x++){
            float x0=x*tw,y0=y*th,x1=(x+1)*tw,y1=(y+1)*th;
            float u1=u+uw,v1=v+vh;if(x1>width){u1=u+uw*(width-x0)/(x1-x0);x1=width;}if(y1>height){v1=v+vh*(height-y0)/(y1-y0);y1=height;}
            yield return new(left+x0,bottom+y0,left+x1,bottom+y1,u,v,u1,v1);
        }
    }
}
