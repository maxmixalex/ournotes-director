using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Original Cubism Lit-URP materials and the four-channel 1024px Cubism mask atlas.
public sealed unsafe class GameActorDrawing : IDisposable
{
    const string Lit="Live2D Cubism/Lit-URP-ADV-optimize",Mask="Live2D Cubism/Mask";
    sealed class Mesh {public required JsonNode Material;public required AdvTransform Transform;public required float[] Tint;public Dictionary<string,float[]> Attributes=[],MaskAttributes=[];public Dictionary<string,float[]>? Values;public Dictionary<string,uint> Textures=[],MaskTextures=[];public UnityShader? Shader;public ushort[] Indices=[];public int Texture,Junction=-1,LocalOrder,SrcColor,DstColor,SrcAlpha,DstAlpha,Cull;public byte Flags;public float[] Multiply=[],Screen=[],LastColor=[];public bool UsedAsMask;}
    sealed class Junction {public required int[] Masks;public float[] Tile=[],Transform=[];}
    readonly OffscreenGl gl;readonly NativeModel core;readonly UnityShaderLibrary shaders;readonly Mesh[] meshes;readonly uint[] textures;readonly uint white;readonly OffscreenGl.GlSurface mask;
    readonly List<Junction> junctions=[];
    readonly Dictionary<AdvTransform,float[]> currentWorldMatrices=[];
    // Immutable uniform values shared by draws; only the per-mesh dictionaries change.
    static readonly float[] Zero=[0],One=[1],ZeroVector=[0,0,0,0],Ambient=[0,0,0,1],MultiplyUv=[1,1,0,0];
    Dictionary<string,float[]>? maskValues;
    public bool LightingEnabled{get;set;}=true;
    public float[] DirectorTint{get;set;}=[1,1,1,1];public float DirectorBrightness{get;set;}=1;
    public bool DisableMultiplyLighting{get;set;}=true;
    public bool RimEnabled{get;set;}public float[]? RimColor{get;set;}public float RimThreshold{get;}public float RimSmoothness{get;}public float ShadowIntensity{get;set;}
    public GameActorDrawing(Archive archive,string site,JsonNode manifest,JsonNode model,JsonNode prefab,AdvPrefab transforms,NativeModel core,OffscreenGl gl,uint[] textures)
    {
        this.gl=gl;this.core=core;this.textures=textures;shaders=new(archive,site,manifest);white=gl.Upload(new(1,1,[255,255,255,255]));mask=gl.Surface(1024,1024);
        var character=transforms.Find("Live2DCharacter").Component;RimEnabled=Actor.B(character["_isRimLightEnabled"]);RimThreshold=Math.Clamp(Actor.N(character["_rimLightThreshold"]),0,1);RimSmoothness=Math.Clamp(Actor.N(character["_rimLightSmoothness"]),0,1);ShadowIntensity=Math.Clamp(Actor.N(character["_shadowIntensity"]),0,1);
        int n=core.DrawableCount;meshes=new Mesh[n];var counts=core.Ints("DrawableVertexCounts",n);var ic=core.Ints("DrawableIndexCounts",n);var ti=core.Ints("DrawableTextureIndices",n);var mc=core.Ints("DrawableMaskCounts",n);
        var uv=(nint*)core.Pointer("DrawableVertexUvs");var indices=(nint*)core.Pointer("DrawableIndices");var masks=(nint*)core.Pointer("DrawableMasks");var flags=(byte*)core.Pointer("DrawableConstantFlags");var multiply=core.Floats("DrawableMultiplyColors",n*4);var screen=core.Floats("DrawableScreenColors",n*4);
        foreach(var node in prefab["nodes"]!.AsArray().OfType<JsonNode>()){
            var components=node["components"]!.AsArray().OfType<JsonNode>().ToArray();var drawable=components.FirstOrDefault(c=>Archive.S(c,"class")=="CubismDrawable");if(drawable is null)continue;int i=(int)Actor.N(drawable["_unmanagedIndex"]);
            var renderer=components.Single(c=>Archive.S(c,"class")=="CubismRenderer");var material=components.Single(c=>Archive.S(c,"type")=="MeshRenderer")["m_Materials"]![0]!;
            var transform=transforms.Transform(Archive.S(node,"path"));transform.Position=System.Numerics.Vector3.Zero;
            var mesh=meshes[i]=new(){Material=material,Transform=transform,Tint=Color(renderer["_color"]),Texture=ti[i],Flags=flags[i],LocalOrder=(int)Actor.N(renderer["_localSortingOrder"]),Multiply=multiply[(i*4)..(i*4+4)],Screen=screen[(i*4)..(i*4+4)]};
            int Blend(string key,int fallback)=>(int)Actor.N(material["floats"]?[key],fallback);
            mesh.SrcColor=Blend("_SrcColor",1);mesh.DstColor=Blend("_DstColor",10);mesh.SrcAlpha=Blend("_SrcAlpha",1);mesh.DstAlpha=Blend("_DstAlpha",10);mesh.Cull=Blend("_Cull",0);
            mesh.Textures=new(){{"_MainTex",textures[mesh.Texture]},{"_MultiplyTex",white},{"cubism_MaskTexture",mask.Texture}};
            mesh.MaskTextures=new(){{"_MainTex",textures[mesh.Texture]}};
            var u=new float[counts[i]*2];Marshal.Copy(uv[i],u,0,u.Length);for(int j=1;j<u.Length;j+=2)u[j]=1-u[j];
            mesh.Attributes["in_TEXCOORD0"]=u;mesh.Attributes["in_POSITION0"]=new float[counts[i]*4];mesh.Attributes["in_COLOR0"]=new float[counts[i]*4];mesh.Attributes["in_NORMAL0"]=new float[counts[i]*4];mesh.Attributes["in_TANGENT0"]=new float[counts[i]*4];
            for(int j=0;j<counts[i];j++){mesh.Attributes["in_NORMAL0"][j*4+2]=1;mesh.Attributes["in_TANGENT0"][j*4]=1;mesh.Attributes["in_TANGENT0"][j*4+3]=1;}
            mesh.MaskAttributes=new(mesh.Attributes){["in_COLOR0"]=Enumerable.Repeat(1f,counts[i]*4).ToArray()};
            mesh.Indices=new ReadOnlySpan<ushort>((void*)indices[i],ic[i]).ToArray();
            if(mc[i]>0){var members=new ReadOnlySpan<int>((void*)masks[i],mc[i]).ToArray().Where(m=>m>=0).ToArray();int k=junctions.FindIndex(j=>j.Masks.SequenceEqual(members));if(k<0){k=junctions.Count;junctions.Add(new(){Masks=members});}mesh.Junction=k;}
        }
        if(junctions.Count>36)throw new NotSupportedException("Cubism mask atlas requires more than 36 junctions");
        foreach(var junction in junctions)foreach(int i in junction.Masks)meshes[i].UsedAsMask=true;
        int index=0;for(int channel=0;channel<4;channel++){int count=junctions.Count/4+(channel<junctions.Count%4?1:0);for(int k=0;k<count;k++){int divisions=count<2?1:count<=4?2:3;junctions[index++].Tile=[channel,k/divisions,k%divisions,1f/divisions];}}
    }
    static float[] Color(JsonNode? c)=>new[]{"r","g","b","a"}.Select(k=>Actor.N(c?[k],1)).ToArray();
    Dictionary<string,float[]> Values(string shader,JsonNode material)
    {
        var values=shaders.Defaults(shader);foreach(var p in material["floats"]!.AsObject())values[p.Key]=[Actor.N(p.Value)];foreach(var p in material["colors"]!.AsObject())values[p.Key]=Color(p.Value);return values;
    }
    public void Draw(IReadOnlyDictionary<string,float[]> globals,float opacity)
        =>AdvRenderItem.DrawAll(Items(globals,opacity,0));
    public IReadOnlyList<AdvRenderItem> Items(IReadOnlyDictionary<string,float[]> globals,float opacity,int baseOrder)
    {
        int n=meshes.Length;var positions=(nint*)core.Pointer("DrawableVertexPositions");var alpha=core.Floats("DrawableOpacities",n);var order=core.Ints("RenderOrders",n);
        Span<float> color=stackalloc float[4];
        for(int i=0;i<n;i++){
            var mesh=meshes[i];var p=mesh.Attributes["in_POSITION0"];var colors=mesh.Attributes["in_COLOR0"];var raw=new ReadOnlySpan<float>((void*)positions[i],p.Length/2);
            if(alpha[i]<=0&&!mesh.UsedAsMask)continue;
            for(int j=0;j<p.Length/4;j++){p[j*4]=raw[j*2];p[j*4+1]=raw[j*2+1];p[j*4+3]=1;}
            for(int k=0;k<4;k++)color[k]=mesh.Tint[k]*DirectorTint[k]*(k<3?DirectorBrightness:alpha[i]);
            if(!color.SequenceEqual(mesh.LastColor))
            {
                for(int j=0;j<colors.Length;j+=4)color.CopyTo(colors.AsSpan(j,4));
                if(mesh.LastColor.Length==4)color.CopyTo(mesh.LastColor);else mesh.LastColor=color.ToArray();
            }
        }
        foreach(var junction in junctions){float lx=float.PositiveInfinity,ly=lx,hx=float.NegativeInfinity,hy=hx;foreach(int i in junction.Masks){var p=meshes[i].Attributes["in_POSITION0"];for(int j=0;j<p.Length;j+=4){lx=Math.Min(lx,p[j]);ly=Math.Min(ly,p[j+1]);hx=Math.Max(hx,p[j]);hy=Math.Max(hy,p[j+1]);}}float span=Math.Max(hx-lx,hy-ly);junction.Transform=[(lx+hx)*.5f,(ly+hy)*.5f,span>0?1/span:0,0];}
        if(junctions.Count>0)gl.InSurface(mask,()=>{maskValues??=shaders.Defaults(Mask);maskValues["_ProjectionParams"]=[1,.3f,1000,.001f];var maskShader=shaders.Program(Mask);foreach(var junction in junctions)foreach(int i in junction.Masks){
            var mesh=meshes[i];maskValues["cubism_MaskTile"]=junction.Tile;maskValues["cubism_MaskTransform"]=junction.Transform;
            gl.GameMaterial(maskShader,maskValues,mesh.MaskTextures,mesh.MaskAttributes,mesh.Indices,4,1,4,1,(mesh.Flags&4)!=0?0:1,true);
        }});
        var matrices=currentWorldMatrices;matrices.Clear();
        float[] modelOpacity=[opacity],rimIntensity=[RimThreshold],rimSmooth=[RimSmoothness],shadowIntensity=[ShadowIntensity];
        float[] World(AdvTransform transform){if(!matrices.TryGetValue(transform,out var matrix))matrices[transform]=matrix=transform.Parent is null?transform.LocalMatrix():AdvMatrix.Multiply(World(transform.Parent),transform.LocalMatrix());return matrix;}
        void DrawMesh(int i){
            var mesh=meshes[i];var values=mesh.Values??=Values(Lit,mesh.Material);foreach(var p in globals)values[p.Key]=p.Value;
            var world=World(mesh.Transform);values["unity_ObjectToWorld"]=world;values["cubism_ModelOpacity"]=modelOpacity;values["cubism_MultiplyColor"]=mesh.Multiply;values["cubism_ScreenColor"]=mesh.Screen;
            if(globals.TryGetValue("_DirectorLightBounds",out var lightBounds)){int lightCount=0;for(int light=0;light<lightBounds.Length;light+=5){float dx=world[12]-lightBounds[light],dy=world[13]-lightBounds[light+1],dz=world[14]-lightBounds[light+2];if(((unchecked((int)lightBounds[light+4])>>6)&1)!=0&&MathF.Sqrt(dx*dx+dy*dy+dz*dz)<=lightBounds[light+3]+10)lightCount++;}values["unity_LightData"]=[0,Math.Min(2,lightCount),0,0];}
            values["_LightingEnabled"]=LightingEnabled&&!(DisableMultiplyLighting&&(mesh.Flags&2)!=0)?One:Zero;values["_UseMultiplyTex"]=One;values["_MultiplyUV"]=MultiplyUv;values["_MultiplyTexIntensity"]=Zero;values["_MultiplyTexAmplitude"]=ZeroVector;
            values["unity_SHAr"]=values["unity_SHAg"]=values["unity_SHAb"]=Ambient;values["unity_SHBr"]=values["unity_SHBg"]=values["unity_SHBb"]=values["unity_SHC"]=ZeroVector;
            values["_RimLightingEnabled"]=RimEnabled?One:Zero;values["_RimIntensity"]=rimIntensity;values["_RimSmooth"]=rimSmooth;values["_ShadowIntensity"]=shadowIntensity;if(RimColor is not null)values["_RimColor"]=RimColor;
            if(mesh.Junction>=0){values["cubism_MaskTile"]=junctions[mesh.Junction].Tile;values["cubism_MaskTransform"]=junctions[mesh.Junction].Transform;}
            gl.GameMaterial(mesh.Shader??=shaders.Program(Lit,0,mesh.Material["keywords"]!.AsArray().Select(k=>k!.ToString()).Append("_ADDITIONAL_LIGHTS_VERTEX").ToArray()),values,mesh.Textures,mesh.Attributes,mesh.Indices,mesh.SrcColor,mesh.DstColor,mesh.SrcAlpha,mesh.DstAlpha,mesh.Cull,true);
        }
        var camera=globals.GetValueOrDefault("_WorldSpaceCameraPos")??[0,0,0];var cameraPosition=new System.Numerics.Vector3(camera[0],camera[1],camera[2]);
        return Enumerable.Range(0,n).Where(i=>alpha[i]>0&&meshes[i].Transform.ActiveInHierarchy).Select(i=>{var m=World(meshes[i].Transform);return new AdvRenderItem(baseOrder+order[i]+meshes[i].LocalOrder,System.Numerics.Vector3.DistanceSquared(new(m[12],m[13],m[14]),cameraPosition),()=>DrawMesh(i));}).ToArray();
    }
    // Read the actual evaluated Cubism vertices and the same per-drawable world matrices
    // used by Items. Editor bounds never substitute an estimated model rectangle for pixels.
    public PreviewTarget? PreviewBounds(string id,string label,float[] viewProjection)
    {
        float left=float.PositiveInfinity,top=left,right=float.NegativeInfinity,bottom=right;
        var alpha=core.Floats("DrawableOpacities",meshes.Length);
        var positions=(nint*)core.Pointer("DrawableVertexPositions");
        for(int i=0;i<meshes.Length;i++){
            var mesh=meshes[i];
            if(alpha[i]<=0||mesh.Tint[3]<=0||mesh.Indices.Length==0||!mesh.Transform.ActiveInHierarchy)continue;
            var matrix=AdvMatrix.Multiply(viewProjection,currentWorldMatrices.GetValueOrDefault(mesh.Transform)??mesh.Transform.WorldMatrix());
            int count=mesh.Attributes["in_POSITION0"].Length/4;
            var raw=new ReadOnlySpan<float>((void*)positions[i],count*2);
            for(int j=0;j<count;j++){
                var p=AdvMatrix.Transform(matrix,new(raw[j*2],raw[j*2+1],0,1));
                if(p.W<=.00001f||!float.IsFinite(p.W))continue;
                float x=(p.X/p.W+1)*.5f,y=(1-p.Y/p.W)*.5f;
                if(!float.IsFinite(x)||!float.IsFinite(y))continue;
                left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);
            }
        }
        return right>left&&bottom>top?new(id,label,left,top,right-left,bottom-top):null;
    }
    public void Dispose()=>shaders.Dispose();
}
