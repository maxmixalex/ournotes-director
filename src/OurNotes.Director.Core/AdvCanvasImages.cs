using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

// Shared native Unity Image drawing for the exported canvas prefabs.
public sealed partial class AdvCanvasImages : IDisposable
{
    readonly Archive archive;readonly string site;readonly JsonNode manifest;
    readonly OffscreenGl gl;readonly UnityShaderLibrary shaders;readonly uint white;
    readonly Dictionary<string,uint> textures=[];
    public AdvCanvasImages(Archive archive,string site,JsonNode manifest,OffscreenGl gl)
    {this.archive=archive;this.site=site;this.manifest=manifest;this.gl=gl;shaders=new(archive,site,manifest);white=gl.Upload(new(1,1,[255,255,255,255]));}
    uint Texture(JsonNode desc,bool repeat=false)
    {string path=Archive.S(desc,"texture"),key=path+(repeat?"|repeat":"");if(!textures.TryGetValue(key,out uint value)){textures[key]=value=gl.Upload(Png.Decode(archive.Logical(site,manifest,path)));if(repeat)gl.RepeatTexture(value);}return value;}
    public void Image(AdvCanvasNode node,float width,float height,AdvCanvasNode? softMask=null,System.Numerics.Vector3? clipTransform=null,float? perspectiveDistance=null)
    {
        var image=node.Image!;if(!Actor.B(image["m_Enabled"])||node.InheritedAlpha<=0)return;
        var sprite=image["m_Sprite"];int originalType=(int)Actor.N(image["m_Type"]),type=sprite is null&&originalType!=2?0:originalType;
        uint texture=sprite?["texture"] is JsonNode desc?Texture(desc,type==2&&AdvImageTiles.Repeats(sprite)):white;
        if(type==1&&new[]{"x","y","z","w"}.All(k=>Actor.N(sprite?["border"]?[k])==0))type=0;
        float left=-node.Pivot.X*node.Width,bottom=-node.Pivot.Y*node.Height,right=left+node.Width,top=bottom+node.Height;
        float u=0,v=0,uw=1,vh=1;var rect=sprite?["textureRect"];
        if(rect is not null){float tw=Actor.N(sprite!["texture"]!["width"]),th=Actor.N(sprite["texture"]!["height"]);u=Actor.N(rect["x"])/tw;v=Actor.N(rect["y"])/th;uw=Actor.N(rect["width"])/tw;vh=Actor.N(rect["height"])/th;
            float rw=Actor.N(sprite["rect"]?["width"]),rh=Actor.N(sprite["rect"]?["height"]);
            if(originalType==0&&Actor.B(image["m_PreserveAspect"])&&rw>0&&rh>0){float ratio=rw/rh;if(node.Width/node.Height>ratio){float w=node.Height*ratio;left+=(node.Width-w)*node.Pivot.X;right=left+w;}else{float h=node.Width/ratio;bottom+=(node.Height-h)*node.Pivot.Y;top=bottom+h;}}
            if(type==0&&rw>0&&rh>0){float w=right-left,h=top-bottom;left+=Actor.N(sprite["textureRectOffset"]?["x"])/rw*w;bottom+=Actor.N(sprite["textureRectOffset"]?["y"])/rh*h;right=left+Actor.N(rect["width"])/rw*w;top=bottom+Actor.N(rect["height"])/rh*h;}
        }
        var c=image["m_Color"]!;float[] color=[Actor.N(c["r"],1),Actor.N(c["g"],1),Actor.N(c["b"],1),Actor.N(c["a"],1)*node.InheritedAlpha];var material=image["m_Material"];
        var gradient=node.Record["gradient"];var gradientMesh=gradient is not null&&Actor.B(gradient["enabled"])?new List<AdvUiGradient.Vertex>():null;
        void Draw(float x0,float y0,float x1,float y1,float u0,float v0,float u1,float v1){
            if(gradientMesh is not null){double[] rgba=new[]{"r","g","b","a"}.Select(k=>AdvUiGradient.Color32(Actor.N(c[k],1))).ToArray();
                AdvUiGradient.Vertex[] q=[new(x0,y0,u0,v0,rgba),new(x0,y1,u0,v1,rgba),new(x1,y1,u1,v1,rgba),new(x1,y0,u1,v0,rgba)];
                foreach(int i in new[]{0,1,2,2,3,0})gradientMesh.Add(q[i] with{});return;}
            var xy=node.ClipQuad(x0,y0,x1,y1,width,height,perspectiveDistance,clipTransform);float[] uv=[u0,1-v0,u1,1-v0,u1,1-v1,u0,1-v1];
            if(softMask is not null)DrawSoft(texture,xy,uv,Enumerable.Range(0,4).SelectMany(_=>color).ToArray(),[0,1,2,0,2,3],softMask,width,height,clipTransform);
            else if(material is null)gl.Draw(texture,xy,uv,[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],color,0,false,false);
            else DrawMaterial(material,texture,xy,uv,Enumerable.Range(0,4).SelectMany(_=>color).ToArray(),[0,1,2,0,2,3]);}
        if(type==1&&sprite?["border"] is JsonNode border){float ppu=Actor.N(sprite["pixelsToUnits"],100)/100*Actor.N(image["m_PixelsPerUnitMultiplier"],1);float bl=Actor.N(border["x"])/ppu,bb=Actor.N(border["y"])/ppu,br=Actor.N(border["z"])/ppu,bt=Actor.N(border["w"])/ppu;
            float sx=bl+br>0?Math.Min(1,node.Width/(bl+br)):1,sy=bb+bt>0?Math.Min(1,node.Height/(bb+bt)):1,tw=Actor.N(sprite["texture"]!["width"]),th=Actor.N(sprite["texture"]!["height"]);
            float ox=Actor.N(sprite["textureRectOffset"]?["x"]),oy=Actor.N(sprite["textureRectOffset"]?["y"]),rw=Actor.N(sprite["rect"]?["width"]),rh=Actor.N(sprite["rect"]?["height"]);
            float pr=rw-ox-Actor.N(rect?["width"]),pt=rh-oy-Actor.N(rect?["height"]);
            float[] xs=[left+ox/ppu,left+bl*sx,right-br*sx,right-pr/ppu],ys=[bottom+oy/ppu,bottom+bb*sy,top-bt*sy,top-pt/ppu],
                us=[u,u+(bl*ppu-ox)/tw,u+(rw-br*ppu-ox)/tw,u+uw],vs=[v,v+(bb*ppu-oy)/th,v+(rh-bt*ppu-oy)/th,v+vh];
            for(int iy=0;iy<3;iy++)for(int ix=0;ix<3;ix++)if(xs[ix+1]>xs[ix]&&ys[iy+1]>ys[iy]&&(ix!=1||iy!=1||Actor.B(image["m_FillCenter"])))Draw(xs[ix],ys[iy],xs[ix+1],ys[iy+1],us[ix],vs[iy],us[ix+1],vs[iy+1]);
        }else if(type is 0 or 1)Draw(left,bottom,right,top,u,v,u+uw,v+vh);
        else if(type==2)foreach(var tile in AdvImageTiles.Build(node.Width,node.Height,left,bottom,image))Draw(tile.Left,tile.Bottom,tile.Right,tile.Top,tile.U0,tile.V0,tile.U1,tile.V1);
        else throw new NotSupportedException("Frame image mode not connected: "+type);
        if(gradientMesh is not null){
            if(softMask is not null&&material is not null)throw new NotSupportedException("Gradient image with custom material and SoftMask");
            var vertices=AdvUiGradient.Apply(gradientMesh,gradient!);
            foreach(var chunk in vertices.Chunk(65535)){int stride=perspectiveDistance is null?2:4;var xy=new float[chunk.Length*stride];var uv=new float[chunk.Length*2];var colors=new float[chunk.Length*4];var indices=new ushort[chunk.Length];
                for(int i=0;i<chunk.Length;i++){var vtx=chunk[i];var p=node.ClipPoint(new((float)vtx.X,(float)vtx.Y,0),width,height,perspectiveDistance,clipTransform);for(int k=0;k<stride;k++)xy[i*stride+k]=p[k];uv[i*2]=(float)vtx.U;uv[i*2+1]=1-(float)vtx.V;indices[i]=(ushort)i;
                    for(int j=0;j<4;j++)colors[i*4+j]=(float)(vtx.Color[j]/255)*(j==3?node.InheritedAlpha:1);}
                if(softMask is not null)DrawSoft(texture,xy,uv,colors,indices,softMask,width,height,clipTransform);
                else if(material is null)gl.Draw(texture,xy,uv,indices,1,[1,1,1,1],[0,0,0,0],[1,1,1,1],0,false,false,colors:colors);
                else DrawMaterial(material,texture,xy,uv,colors,indices);
            }
        }
    }
    void DrawMaterial(JsonNode material,uint texture,float[] xy,float[] uv,float[] colors,ushort[] indices)
    {
        string shader=material["shader"]?["shader"]?.ToString()??throw new InvalidDataException("Frame material shader absent");
        if(shader is not ("Mobile/Particles/Additive" or "Mobile/Particles/Alpha Blended"))throw new NotSupportedException("Frame material not connected: "+shader);
        var values=shaders.Defaults(shader);values["unity_ObjectToWorld"]=AdvMatrix.Identity();values["unity_MatrixVP"]=AdvMatrix.Identity();values["_GlobalMipBias"]=[0,0];
        foreach(string section in new[]{"floats","ints"})foreach(var pair in material[section]?.AsObject()??new JsonObject())values[pair.Key]=[Actor.N(pair.Value)];
        foreach(var pair in material["colors"]?.AsObject()??new JsonObject())values[pair.Key]=new[]{"r","g","b","a"}.Select(k=>Actor.N(pair.Value?[k])).ToArray();
        var st=material["textures"]?["_MainTex"];float sy=Actor.N(st?["scale"]?["y"],1);
        // Texture uploads and canvas UVs are top-down: F(ST(F(uv))) preserves Unity's transform.
        values["_MainTex_ST"]=[Actor.N(st?["scale"]?["x"],1),sy,Actor.N(st?["offset"]?["x"]),1-sy-Actor.N(st?["offset"]?["y"])];
        int count=uv.Length/2;
        float[] vertices=xy.Length==count*4?xy:Enumerable.Range(0,count).SelectMany(i=>new[]{xy[i*2],xy[i*2+1],0f,1f}).ToArray();
        gl.Material(shaders.Program(shader,0,material["keywords"]?.AsArray().Select(k=>k!.ToString()).ToArray()??[]),values,
            new Dictionary<string,uint>{{"_MainTex",texture}},new Dictionary<string,float[]>{{"in_POSITION0",vertices},{"in_TEXCOORD0",uv},{"in_COLOR0",colors}},indices,shader=="Mobile/Particles/Additive");
    }
    public void Dispose(){softShader?.Dispose();shaders.Dispose();}
}
