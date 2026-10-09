using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// SpriteRenderer geometry and animated properties from live/fx-effects.js.
public sealed class AdvEffectSprite
{
    JsonNode? sprite;
    readonly int mode;
    readonly bool flipX,flipY;
    public JsonNode Material{get;}
    public JsonNode Texture=>sprite?["texture"]??throw new InvalidOperationException("Sprite has no texture");
    public JsonNode? Sprite{get=>sprite;set{
        if(ReferenceEquals(sprite,value))return;
        if(mode==1&&((int)Actor.N(value?["settingsRaw"])&1)!=0)throw new NotSupportedException("Sliced packed sprite unsupported");
        sprite=value;geometry=null;
    }}
    public bool Visible=>Enabled&&sprite is not null&&Transform.ActiveInHierarchy;
    public AdvTransform Transform{get;}
    public bool Enabled{get;}
    public int SortingLayer{get;}
    public int SortingOrder{get;set;}
    public Vector4 Color{get;set;}
    public Vector2 Size{get;set;}
    public sealed record Mesh(float[] Positions,float[] Uv,ushort[] Indices);
    Mesh? geometry;Vector2 lastSize;
    public AdvEffectSprite(JsonNode data,AdvTransform transform,JsonNode? initialSprite=null)
    {
        Transform=transform;sprite=initialSprite??data["m_Sprite"]??throw new InvalidDataException("Effect sprite missing");
        mode=(int)Actor.N(data["m_DrawMode"]);
        if(mode is not (0 or 1)||Actor.N(data["m_MaskInteraction"])!=0||mode==1&&((int)Actor.N(sprite["settingsRaw"])&1)!=0)throw new NotSupportedException("Effect sprite draw mode or mask");
        var materials=data["m_Materials"]!.AsArray();if(materials.Count!=1||materials[0] is null)throw new InvalidDataException("Effect sprite needs one material");Material=materials[0]!;
        var c=data["m_Color"]!;Color=new(Actor.N(c["r"]),Actor.N(c["g"]),Actor.N(c["b"]),Actor.N(c["a"]));Size=new(Actor.N(data["m_Size"]?["x"]),Actor.N(data["m_Size"]?["y"]));
        flipX=Actor.B(data["m_FlipX"]);flipY=Actor.B(data["m_FlipY"]);Enabled=Actor.B(data["m_Enabled"]);SortingLayer=(int)Actor.N(data["m_SortingLayer"]);SortingOrder=(int)Actor.N(data["m_SortingOrder"]);
    }
    public AdvAnimator.Access? Bind(string attribute)
    {
        if(attribute.Length==9&&attribute.StartsWith("m_Color.",StringComparison.Ordinal)&&"rgba".IndexOf(attribute[^1]) is int i&&i>=0)return new(()=>Color[i],v=>{var c=Color;c[i]=v;Color=c;});
        if(attribute is "m_Size.x" or "m_Size.y"){int axis=attribute[^1]=='x'?0:1;return new(()=>Size[axis],v=>{var s=Size;s[axis]=v;Size=s;});}return null;
    }
    public Mesh Geometry()
    {
        if(sprite is null)return new([],[],[]);
        if(geometry is not null&&(mode==0||lastSize==Size))return geometry;
        var positions=new List<float>();var uv=new List<float>();var indices=new List<ushort>();
        if(mode==0){foreach(var p in sprite["vertices"]!.AsArray()){positions.AddRange([Actor.N(p![0]),Actor.N(p[1]),p.AsArray().Count>2?Actor.N(p[2]):0]);}foreach(var p in sprite["uv"]!.AsArray())uv.AddRange([Actor.N(p![0]),Actor.N(p[1])]);indices.AddRange(sprite["indices"]!.AsArray().Select(n=>checked((ushort)Actor.N(n))));}
        else{
            float ppu=Actor.N(sprite["pixelsToUnits"]);var b=sprite["border"]!;float l=Actor.N(b["x"])/ppu,r=Actor.N(b["z"])/ppu,bot=Actor.N(b["y"])/ppu,top=Actor.N(b["w"])/ppu;
            if(l+r>Size.X&&l+r>0){float k=Size.X/(l+r);l*=k;r*=k;}if(bot+top>Size.Y&&bot+top>0){float k=Size.Y/(bot+top);bot*=k;top*=k;}
            float x=-Actor.N(sprite["pivot"]?["x"])*Size.X,y=-Actor.N(sprite["pivot"]?["y"])*Size.Y;float[] xs=[x,x+l,x+Size.X-r,x+Size.X],ys=[y,y+bot,y+Size.Y-top,y+Size.Y];
            var rect=sprite["rect"]!;float tx=Actor.N(rect["x"]),ty=Actor.N(rect["y"]),w=Actor.N(rect["width"]),h=Actor.N(rect["height"]),tw=Actor.N(Texture["width"]),th=Actor.N(Texture["height"]);float[] us=[tx/tw,(tx+Actor.N(b["x"]))/tw,(tx+w-Actor.N(b["z"]))/tw,(tx+w)/tw],vs=[ty/th,(ty+Actor.N(b["y"]))/th,(ty+h-Actor.N(b["w"]))/th,(ty+h)/th];
            for(int j=0;j<3;j++)for(int i=0;i<3;i++){if(xs[i+1]<=xs[i]||ys[j+1]<=ys[j])continue;int start=positions.Count/3;foreach(var(a,c)in new[]{(i,j),(i+1,j),(i,j+1),(i+1,j+1)}){positions.AddRange([xs[a],ys[c],0]);uv.AddRange([us[a],vs[c]]);}foreach(int n in new[]{0,2,1,1,2,3})indices.Add(checked((ushort)(start+n)));}
        }
        for(int i=0;i<positions.Count;i+=3){if(flipX)positions[i]=-positions[i];if(flipY)positions[i+1]=-positions[i+1];}
        lastSize=Size;return geometry=new(positions.ToArray(),uv.ToArray(),indices.ToArray());
    }
    public Dictionary<string,float[]> Attributes(bool uniformColor=false,bool uniformFlip=false)
    {
        var mesh=Geometry();int count=mesh.Positions.Length/3;var p=(float[])mesh.Positions.Clone();var uv=(float[])mesh.Uv.Clone();var colors=new float[count*4];
        static float C(float v)=>MathF.Floor(Math.Clamp(v,0,1)*255+.5f)/255;
        for(int i=0;i<count;i++){if(uniformFlip){if(flipX)p[i*3]=-p[i*3];if(flipY)p[i*3+1]=-p[i*3+1];}for(int k=0;k<4;k++)colors[i*4+k]=uniformColor?1:C(Color[k]);uv[i*2+1]=1-uv[i*2+1];}
        return new(){{"in_POSITION0",p},{"in_COLOR0",colors},{"in_TEXCOORD0",uv}};
    }
    public Vector4 Flip=>new(flipX?-1:1,flipY?-1:1,0,0);
    public Vector3 WorldCenter()
    {
        var positions=Geometry().Positions;if(positions.Length==0)return Transform.WorldPosition;
        var matrix=Transform.WorldMatrix();var low=new Vector3(float.PositiveInfinity);var high=new Vector3(float.NegativeInfinity);
        for(int i=0;i<positions.Length;i+=3){var p=AdvMatrix.Transform(matrix,new(positions[i],positions[i+1],positions[i+2],1));var v=new Vector3(p.X,p.Y,p.Z);low=Vector3.Min(low,v);high=Vector3.Max(high,v);}
        return(low+high)*.5f;
    }
}
