using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    AdvTransform planeTransform=null!;
    JsonNode planeRenderer=null!,planeMesh=null!,backgroundSpriteRenderer=null!;
    Dictionary<string,float[]> planeAttributes=[];
    uint[] planeIndices=[];
    Vector3 planeCenter;
    AdvEffectSprite? backgroundDrawing;
    JsonNode? lastBackgroundSprite;
    Vector3 backgroundSpriteCenter;
    void InitializeBackground(JsonNode component)
    {
        string planePath=Archive.S(component["_field"]!,"transform")+"/BaseRenderer";
        planeTransform=backgroundPrefab.Transform(planePath);planeRenderer=backgroundPrefab.Component(planePath,"MeshRenderer");
        planeMesh=backgroundPrefab.Component(planePath,"MeshFilter")["m_Mesh"]!;
        backgroundSpriteRenderer=backgroundPrefab.Component(Archive.S(component["_renderer"]!,"gameObject"),"SpriteRenderer");
        if(planeMesh["submeshes"]!.AsArray().Count!=1)throw new NotSupportedException("Background plane needs one submesh");
        float[] Flatten(JsonNode node)=>node.AsArray().SelectMany(v=>v!.AsArray().Select(n=>Actor.N(n))).ToArray();
        planeAttributes=new(){{"in_POSITION0",Flatten(planeMesh["vertices"]!)},{"in_TEXCOORD0",Flatten(planeMesh["uv0"]!)}};
        planeIndices=planeMesh["submeshes"]![0]!.AsArray().Select(n=>checked((uint)Actor.N(n))).ToArray();
        planeCenter=BoundsCenter(planeMesh["vertices"]!);
        effectMaterials??=new(archive,site,manifest,gl,shaders);
    }
    static Vector3 BoundsCenter(JsonNode vertices)
    {
        var low=new Vector3(float.PositiveInfinity);var high=new Vector3(float.NegativeInfinity);
        foreach(var vertex in vertices.AsArray()){var v=new Vector3(Actor.N(vertex![0]),Actor.N(vertex[1]),vertex.AsArray().Count>2?Actor.N(vertex[2]):0);low=Vector3.Min(low,v);high=Vector3.Max(high,v);}
        return vertices.AsArray().Count==0?Vector3.Zero:(low+high)*.5f;
    }
    float BackgroundDistance(AdvTransform transform,Vector3 center)
    {
        var world=AdvMatrix.Transform(transform.WorldMatrix(),new(center,1));return Vector3.DistanceSquared(new(world.X,world.Y,world.Z),Camera.Position+Camera.ShakeOffset);
    }
    IEnumerable<AdvRenderItem> BackgroundItems(IReadOnlyDictionary<string,float[]> globals)
    {
        if(stage is null)yield break;
        yield return new((int)Actor.N(planeRenderer["m_SortingOrder"],-100),BackgroundDistance(planeTransform,planeCenter),()=>
            effectMaterials!.DrawMesh(planeRenderer["m_Materials"]![0]!,globals,new(planeAttributes,planeIndices,planeTransform.WorldMatrix(),UnityUvs:true)));
        if(stage["_backgroundSprite"] is {} sprite){
            backgroundDrawing??=new(backgroundSpriteRenderer,spriteTransform,sprite);
            backgroundDrawing.Sprite=sprite;
            if(!ReferenceEquals(lastBackgroundSprite,sprite)){lastBackgroundSprite=sprite;backgroundSpriteCenter=BoundsCenter(sprite["vertices"]!);}
            var color=stage["_backgroundColor"]!;var rendererColor=backgroundSpriteRenderer["m_Color"]!;
            backgroundDrawing.Color=new(Actor.N(rendererColor["r"])*(Actor.N(color["r"])*backgroundBrightness),
                Actor.N(rendererColor["g"])*(Actor.N(color["g"])*backgroundBrightness),
                Actor.N(rendererColor["b"])*(Actor.N(color["b"])*backgroundBrightness),Actor.N(rendererColor["a"])*Actor.N(color["a"]));
            yield return new((int)Actor.N(backgroundSpriteRenderer["m_SortingOrder"]),BackgroundDistance(spriteTransform,backgroundSpriteCenter),()=>effectMaterials!.Draw(backgroundDrawing,globals));
        }
    }
}
