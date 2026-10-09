using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// TMP atlas rendering for RectTransform canvases, including sprite glyphs and the ADV camera canvas projection.
public sealed class AdvCanvasTexts : IDisposable
{
    readonly Archive archive;readonly string site,language,directory;readonly JsonNode manifest;
    readonly OffscreenGl gl;readonly UnityShaderLibrary shaders;
    readonly Dictionary<string,(uint Texture,int Width,int Height)> textures=[];
    sealed record Batch(UnityShader Program,Dictionary<string,float[]> Values,Dictionary<string,uint> Textures,Dictionary<string,float[]> Attributes,ushort[] Indices,bool Sprite);
    sealed class MeshCache
    {
        public IReadOnlyList<AdvTmpText.Glyph>? Glyphs;
        public float[] Matrix=[];
        public float LossY,Alpha;
        public Vector4? Tint,Outline;
        public List<Batch> Batches=[];
    }
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<AdvTmpText,MeshCache> meshes=new();
    public JsonNode Fonts{get;}
    public AdvCanvasTexts(Archive archive,string site,JsonNode manifest,string language,OffscreenGl gl,string directory="ui/")
    {
        this.archive=archive;this.site=site;this.manifest=manifest;this.language=language;this.gl=gl;
        this.directory=directory.TrimEnd('/')+"/";
        Fonts=JsonNode.Parse(archive.Logical(site,manifest,this.directory+"fonts.json",language))!;
        string shaderIndex=Archive.S(Fonts["shaders"]??new JsonObject(),"index","shaders/shaders.json");
        shaders=new(archive,site,manifest,this.directory+shaderIndex[..(shaderIndex.LastIndexOf('/')+1)],language);
    }
    (uint Texture,int Width,int Height) Texture(string key)
    {
        if(textures.TryGetValue(key,out var ready))return ready;
        var entry=Fonts["textures"]![key]!;var pixels=Png.Decode(archive.Logical(site,manifest,directory+Archive.S(entry,"texture"),language));return textures[key]=(gl.Upload(pixels),pixels.Width,pixels.Height);
    }
    public static (Dictionary<string,float[]> Values,float WorldScale) Globals(float width,float height,int pixelsWide,int pixelsHigh,float fov,float near,float far,float distance)
    {
        var p=AdvMatrix.Perspective(fov,pixelsWide/(float)pixelsHigh,near,far);float s=2*distance*(float)Math.Tan(fov*Math.PI/360)/height;
        float[] m=[s,0,0,0,0,s,0,0,0,0,-s,0,-s*width/2,-s*height/2,-distance,1],inverse=[1/s,0,0,0,0,1/s,0,0,0,0,-1/s,0,width/2,height/2,-distance/s,1];
        return(new(){{"unity_MatrixVP",p},{"glstate_matrix_projection",p},{"unity_ObjectToWorld",m},{"unity_WorldToObject",inverse},{"_WorldSpaceCameraPos",[0,0,0]},{"_ScreenParams",[pixelsWide,pixelsHigh,1+1f/pixelsWide,1+1f/pixelsHigh]},{"_UIMaskSoftnessX",[0]},{"_UIMaskSoftnessY",[0]},{"unity_GUIZTestMode",[4]}},s);
    }
    public static Dictionary<string,float[]> OrthographicGlobals(float width,float height,int pixelsWide,int pixelsHigh)
    {
        float[] p=[2/width,0,0,0,0,2/height,0,0,0,0,-.002f,0,-1,-1,0,1];
        return new(){{"unity_MatrixVP",p},{"glstate_matrix_projection",p},{"unity_ObjectToWorld",AdvMatrix.Identity()},{"unity_WorldToObject",AdvMatrix.Identity()},{"_WorldSpaceCameraPos",[width/2,height/2,-10]},{"_ScreenParams",[pixelsWide,pixelsHigh,1+1f/pixelsWide,1+1f/pixelsHigh]},{"_UIMaskSoftnessX",[0]},{"_UIMaskSoftnessY",[0]},{"unity_GUIZTestMode",[4]}};
    }
    public void Draw(AdvCanvasNode node,AdvTmpText text,IReadOnlyDictionary<string,float[]> globals,float worldScale,Vector4? tint=null,Vector4? outline=null)
    {
        if(!node.ActiveInHierarchy||!text.Enabled||node.InheritedAlpha<=0)return;
        var glyphs=text.Generate(node.Width,node.Height,node.Pivot);int cursor=0;
        float lossY=MathF.Sqrt(node.Matrix[4]*node.Matrix[4]+node.Matrix[5]*node.Matrix[5]+node.Matrix[6]*node.Matrix[6])*worldScale;
        var cache=meshes.GetValue(text,_=>new());float alpha=node.InheritedAlpha;
        bool rebuild=!ReferenceEquals(cache.Glyphs,glyphs)||cache.LossY!=lossY||cache.Alpha!=alpha||cache.Tint!=tint||cache.Outline!=outline||!cache.Matrix.AsSpan().SequenceEqual(node.Matrix);
        if(rebuild){
        cache.Batches.Clear();
        while(cursor<glyphs.Count){var first=glyphs[cursor];var texture=Texture(first.Texture);var material=first.Material;
            string shader=Archive.S(material["shader"]!,"shader");var program=shaders.Program(shader,0,material["keywords"]!.AsArray().Select(k=>k!.ToString()).ToArray());
            int uvComponents=program.AttributeComponents("in_TEXCOORD0");if(uvComponents is not (2 or 4))throw new NotSupportedException("TMP texture coordinate format: "+uvComponents);
            var positions=new List<float>();var coords=new List<float>();var colors=new List<float>();var coords1=new List<float>();var normals=new List<float>();var indices=new List<ushort>();
            while(cursor<glyphs.Count&&indices.Count<96000){var g=glyphs[cursor];if(!ReferenceEquals(g.Material,material)||g.Texture!=first.Texture||g.Sprite!=first.Sprite)break;cursor++;
                ushort start=(ushort)(positions.Count/4);var r=g.Rect;Vector2[] corners=[new(r.X,r.Y),new(r.X+r.Z,r.Y),new(r.X+r.Z,r.Y+r.W),new(r.X,r.Y+r.W)];var uv=g.Uv;
                float[] us=[uv.X,uv.Z,uv.Z,uv.X],vs=[uv.Y,uv.Y,uv.W,uv.W];
                for(int i=0;i<4;i++){var p=AdvMatrix.Transform(node.Matrix,new(corners[i],0,1));positions.AddRange([p.X,p.Y,p.Z,1]);coords.AddRange([us[i],1-vs[i]]);if(uvComponents==4)coords.AddRange([0,g.Scale*lossY]);
                    var color=g.RichColor?g.Color with{W=Math.Min(g.Color.W,tint?.W??1)}:tint??g.Color;colors.AddRange([color.X,color.Y,color.Z,color.W*node.InheritedAlpha]);coords1.AddRange([i is 1 or 2?1:0,i>=2?1:0]);normals.AddRange([0,0,-1]);}
                foreach(int offset in new[]{0,1,2,0,2,3})indices.Add((ushort)(start+offset));
            }
            var values=shaders.Defaults(shader);
            foreach(var pair in material["floats"]!.AsObject())values[pair.Key]=[Actor.N(pair.Value)];foreach(var pair in material["colors"]!.AsObject())values[pair.Key]=new[]{"r","g","b","a"}.Select(k=>Actor.N(pair.Value?[k])).ToArray();
            if(outline is Vector4 o)values["_OutlineColor"]=[o.X,o.Y,o.Z,o.W];
            values["_TextureWidth"]=[texture.Width];values["_TextureHeight"]=[texture.Height];values["_MainTex_ST"]=[1,1,0,0];values["_TextureSampleAdd"]=[0,0,0,0];values["_ClipRect"]=[-32767,-32767,32767,32767];
            cache.Batches.Add(new(program,values,new Dictionary<string,uint>{{"_MainTex",texture.Texture}},
                new Dictionary<string,float[]>{{"in_POSITION0",positions.ToArray()},{"in_COLOR0",colors.ToArray()},{"in_TEXCOORD0",coords.ToArray()},{"in_TEXCOORD1",coords1.ToArray()},{"in_NORMAL0",normals.ToArray()}},indices.ToArray(),first.Sprite));
        }
        cache.Glyphs=glyphs;cache.Matrix=(float[])node.Matrix.Clone();cache.LossY=lossY;cache.Alpha=alpha;cache.Tint=tint;cache.Outline=outline;
        }
        foreach(var batch in cache.Batches){foreach(var pair in globals)batch.Values[pair.Key]=pair.Value;
            gl.GameMaterial(batch.Program,batch.Values,batch.Textures,batch.Attributes,batch.Indices,batch.Sprite?5:1,10,batch.Sprite?5:1,10);}
    }
    public void Dispose()=>shaders.Dispose();
}
