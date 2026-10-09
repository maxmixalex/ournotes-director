using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Original exported effect programs and material properties, compiled by the native GL host.
public sealed class AdvEffectMaterials : IDisposable
{
    readonly Archive archive;readonly string site;readonly JsonNode manifest;readonly OffscreenGl gl;readonly UnityShaderLibrary shaders;
    readonly Dictionary<string,uint> textures=[];readonly uint white;
    readonly bool ownsShaders;
    readonly Dictionary<string,int> shaderQueues=[];
    public int Queue(JsonNode material){int queue=(int)Actor.N(material["renderQueue"],-1);if(queue>=0)return queue;string shader=Archive.S(material["shader"]!,"shader");if(!shaderQueues.TryGetValue(shader,out queue))shaderQueues[shader]=queue=shaders.Queue(shader);return queue;}
    public AdvEffectMaterials(Archive archive,string site,JsonNode manifest,OffscreenGl gl,UnityShaderLibrary? sharedShaders=null){this.archive=archive;this.site=site;this.manifest=manifest;this.gl=gl;ownsShaders=sharedShaders is null;shaders=sharedShaders??new(archive,site,manifest);white=gl.Upload(new(1,1,[255,255,255,255]));}
    uint Texture(JsonNode t){string p=Archive.S(t,"texture");if(!textures.TryGetValue(p,out var tex)){var settings=t["settings"];textures[p]=tex=gl.UploadUnity(Png.Decode(archive.Logical(site,manifest,p)),(int)Actor.N(settings?["m_FilterMode"],1),(int)Actor.N(settings?["m_WrapU"],1),(int)Actor.N(settings?["m_WrapV"],1),(int)Actor.N(t["mipCount"],1));}return tex;}
    public void Draw(AdvEffectSprite sprite,IReadOnlyDictionary<string,float[]> globals)
    {
        if(!sprite.Visible)return;
        Draw(sprite.Material,globals,sprite,null);
    }
    public void DrawMesh(JsonNode material,IReadOnlyDictionary<string,float[]> globals,AdvEffectParticles.Mesh mesh)=>Draw(material,globals,null,mesh);
    public void Draw(AdvEffectParticles particles,IReadOnlyDictionary<string,float[]> globals,float[] cameraWorld,float[] projection)
    {
        if(!particles.Visible)return;
        var mesh=particles.Geometry(cameraWorld,projection);
        if(mesh.Submeshes is null)Draw(particles.Material!,globals,null,mesh);
        else for(int i=0;i<Math.Min(mesh.Submeshes.Count,particles.Materials.Count);i++)Draw(particles.Materials[i],globals,null,mesh with{Indices=mesh.Submeshes[i]});
    }
    void Draw(JsonNode material,IReadOnlyDictionary<string,float[]> globals,AdvEffectSprite? sprite,AdvEffectParticles.Mesh? mesh)
    {
        string name=Archive.S(material["shader"]!,"shader");
        var program=shaders.Program(name,0,material["keywords"]?.AsArray().Select(k=>k!.ToString()).ToArray()??[]);
        var values=shaders.Defaults(name);var bindings=new Dictionary<string,uint>{{"_MainTex",white}};
        foreach(var pair in globals)values[pair.Key]=pair.Value;
        foreach(string section in new[]{"ints","floats"})foreach(var pair in material[section]?.AsObject()??new JsonObject())values[pair.Key]=[Actor.N(pair.Value)];
        foreach(var pair in material["colors"]?.AsObject()??new JsonObject())values[pair.Key]=new[]{"r","g","b","a"}.Select(k=>Actor.N(pair.Value?[k])).ToArray();
        foreach(var pair in material["textures"]?.AsObject()??new JsonObject()){
            var t=pair.Value!;values[pair.Key+"_ST"]=[Actor.N(t["scale"]?["x"],1),Actor.N(t["scale"]?["y"],1),Actor.N(t["offset"]?["x"]),Actor.N(t["offset"]?["y"])];
            bindings[pair.Key]=t["texture"] is JsonNode desc?Texture(desc):white;
            if(t["texture"] is JsonNode dimensions){float tw=Actor.N(dimensions["width"]),th=Actor.N(dimensions["height"]);values[pair.Key+"_TexelSize"]=[1/tw,1/th,tw,th];}
        }
        if(sprite is not null){bindings["_MainTex"]=Texture(sprite.Texture);float w=Actor.N(sprite.Texture["width"]),h=Actor.N(sprite.Texture["height"]);values["_MainTex_TexelSize"]=[1/w,1/h,w,h];}
        values["unity_ObjectToWorld"]=sprite?.Transform.WorldMatrix()??mesh!.Matrix;values.TryAdd("_GlobalMipBias",[0,0]);values.TryAdd("unity_GUIZTestMode",[4]);
        values.TryAdd("_ClipRect",[0,0,0,0]);values.TryAdd("_TextureSampleAdd",[0,0,0,0]);values.TryAdd("_UIMaskSoftnessX",[0]);values.TryAdd("_UIMaskSoftnessY",[0]);
        bool uniformColor=program.Properties.Contains("unity_SpriteColor"),uniformFlip=program.Properties.Contains("unity_SpriteProps");
        if(sprite is not null){var color=sprite.Color;values["unity_SpriteColor"]=[color.X,color.Y,color.Z,color.W];var flip=sprite.Flip;values["unity_SpriteProps"]=[flip.X,flip.Y,flip.Z,flip.W];}
        var state=shaders.State(name);int S(JsonNode? n,int fallback=0){if(n is null)return fallback;string property=Archive.S(n,"name");return property!="<noninit>"&&values.TryGetValue(property,out var v)?(int)v[0]:(int)Actor.N(n["val"],fallback);}
        var blend=state["rtBlend0"]!;
        float F(JsonNode? n){string property=n is null?"":Archive.S(n,"name");return property!="<noninit>"&&values.TryGetValue(property,out var v)?v[0]:Actor.N(n?["val"]);}
        foreach(string face in new[]{"stencilOpFront","stencilOpBack"})if(state[face] is {} ops){
            foreach(string field in new[]{"comp","pass","fail","zFail"})if(S(ops[field],field=="comp"?8:0)!=(field=="comp"?8:0))throw new NotSupportedException("Per-face stencil ops unsupported by reference");
        }
        var stencil=state["stencilOp"];
        var renderState=new OffscreenGl.MaterialState(S(blend["blendOp"]),S(blend["blendOpAlpha"]),S(blend["colMask"],15),S(state["zTest"],4),S(state["zWrite"])!=0,F(state["offsetFactor"]),F(state["offsetUnits"]),
            new(S(stencil?["comp"],8),S(stencil?["pass"]),S(stencil?["fail"]),S(stencil?["zFail"]),S(state["stencilRef"]),S(state["stencilReadMask"],255),S(state["stencilWriteMask"],255)));
        IReadOnlyDictionary<string,float[]> attributes=sprite?.Attributes(uniformColor,uniformFlip)??mesh!.Attributes;int vertices=attributes["in_POSITION0"].Length/3;
        if(vertices==0)return;
        // Retain unchanged streams. Conversion must never mutate cached geometry or
        // streams shared by different submeshes/material variants.
        Dictionary<string,float[]>? converted=null;
        foreach(var (nameOfAttribute,source) in attributes){
            int required=program.AttributeComponents(nameOfAttribute),width=source.Length/vertices;
            bool flipUv=mesh?.UnityUvs!=true&&nameOfAttribute=="in_TEXCOORD0";
            if(!flipUv&&(required==0||width==required))continue;
            int targetWidth=required==0?width:required;
            var packed=new float[vertices*targetWidth];
            for(int i=0;i<vertices;i++)for(int k=0;k<targetWidth;k++){
                float value=k<width?source[i*width+k]:k==3?1:0;
                // Preserve the reference conversion of two-component UV streams.
                if(flipUv&&k<width&&(i*width+k)%2==1)value=1-value;
                packed[i*targetWidth+k]=value;
            }
            converted??=new(attributes);
            converted[nameOfAttribute]=packed;
        }
        if(converted is not null)attributes=converted;
        if(sprite is not null)gl.GameMaterial(program,values,bindings,attributes,sprite.Geometry().Indices,S(blend["srcBlend"],1),S(blend["destBlend"]),S(blend["srcBlendAlpha"],1),S(blend["destBlendAlpha"]),S(state["culling"]),state:renderState);
        else gl.GameMaterial32(program,values,bindings,attributes,mesh!.Indices,S(blend["srcBlend"],1),S(blend["destBlend"]),S(blend["srcBlendAlpha"],1),S(blend["destBlendAlpha"]),S(state["culling"]),state:renderState);
    }
    public void Dispose(){if(ownsShaders)shaders.Dispose();}
}
