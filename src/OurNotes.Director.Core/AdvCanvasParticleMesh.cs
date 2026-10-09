using System.Numerics;
using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

// Coffee UIParticle BakeRotationAndScale followed by CombineMeshes and the camera-canvas mapping.
public static class AdvCanvasParticleMesh
{
    public static AdvEffectParticles.Mesh Build(AdvParticles system,AdvTransform transform,JsonNode renderer,float width,float height,float canvasScale,Vector3 scale,Vector3 uiPosition,bool absolute)
    {
        var projection=AdvMatrix.Identity();projection[0]=2/(width*canvasScale);projection[5]=2/(height*canvasScale);
        var geometry=new AdvEffectParticles(system,transform,renderer).Geometry(AdvMatrix.Identity(),projection);
        bool world=Actor.N(system.Data["moveWithTransform"])==1;var psPosition=transform.WorldPosition;
        var relative=absolute?Vector3.Zero:(psPosition-uiPosition)*(scale-Vector3.One);
        var source=geometry.Attributes["in_POSITION0"];int count=source.Length/3;var output=new float[count*4];bool hasNormals=geometry.Attributes.TryGetValue("in_NORMAL0",out var normals);normals??=new float[count*3];
        if(geometry.Attributes.Keys.Any(k=>k is not ("in_POSITION0" or "in_NORMAL0" or "in_COLOR0" or "in_TEXCOORD0")))throw new NotSupportedException("Extra baked particle streams on a UI canvas are unsupported by reference");
        if(!geometry.Attributes.ContainsKey("in_COLOR0"))geometry.Attributes["in_COLOR0"]=Enumerable.Repeat(1f,count*4).ToArray();
        if(geometry.UnityUvs){
            var tex=new float[count*2];for(int i=0;i<count;i++)tex[i*2+1]=1;if(geometry.Attributes.TryGetValue("in_TEXCOORD0",out var packed)&&count>0){int stride=packed.Length/count;for(int i=0;i<count;i++){tex[i*2]=packed[i*stride];tex[i*2+1]=1-(stride>1?packed[i*stride+1]:0);}}
            geometry.Attributes["in_TEXCOORD0"]=tex;
        }
        for(int i=0;i<source.Length/3;i++){
            var point=new Vector3(source[i*3],source[i*3+1],source[i*3+2]);var normal=new Vector3(normals[i*3],normals[i*3+1],normals[i*3+2]);
            if(!world){var p=AdvMatrix.Transform(geometry.Matrix,new(point,0));point=new(p.X,p.Y,p.Z);var n=AdvMatrix.Transform(geometry.Matrix,new(normal,0));normal=new(n.X,n.Y,n.Z);}
            point=point*scale+relative+(world?Vector3.Zero:psPosition);
            output[i*4]=2*point.X/(canvasScale*width);output[i*4+1]=2*point.Y/(canvasScale*height);output[i*4+3]=1;
            normal*=scale;if(normal.LengthSquared()>1e-24f)normal=Vector3.Normalize(normal);
            for(int k=0;k<3;k++)normals[i*3+k]=hasNormals?normal[k]:k==2?-1:0;
        }
        geometry.Attributes["in_POSITION0"]=output;
        geometry.Attributes["in_NORMAL0"]=normals;
        return geometry with{Indices=geometry.Submeshes is null?geometry.Indices:geometry.Submeshes.SelectMany(s=>s).ToArray(),Matrix=AdvMatrix.Identity(),UnityUvs=false};
    }
}
