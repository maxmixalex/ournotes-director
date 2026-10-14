using System.Numerics;

namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    /// <summary>Extract the evaluated model using its native canvas, without source framing
    /// or editor offsets. Audio, animation, physics, fades and costume changes still run.</summary>
    public void DrawIsolatedActor(string target)
    {
        gl.Begin([0,0,0,0]);var actor=Character(target);if(actor is null)return;
        bool director=directorActors.TryGetValue(actor,out var state);
        if(director?!state!.Visible:!shown.Values.Contains(actor))return;
        var canvas=actor.CanvasMetadata;float width=Actor.N(canvas["width"]),height=Actor.N(canvas["height"]);
        if(width<=0||height<=0)throw new InvalidDataException("角色画布尺寸无效。");
        float pixelsPerUnit=height/actor.CanvasHeight,scale=Math.Min(gl.Width/width,gl.Height/height);
        var root=actor.Prefab.Root;var parent=root.Parent;var position=root.Position;var rotation=root.Rotation;var size=root.Scale;bool lighting=actor.GameLighting;
        try
        {
            root.Parent=null;root.Rotation=Quaternion.Identity;
            root.Position=new(((canvas["originX"] is null?width/2:Actor.N(canvas["originX"]))-width/2)*scale*2/gl.Height,((canvas["originY"] is null?height/2:Actor.N(canvas["originY"]))-height/2)*scale*2/gl.Height,0);
            root.Scale=Vector3.One*(2*scale*pixelsPerUnit/gl.Height);
            var projection=AdvMatrix.Identity();projection[0]=gl.Height/(float)gl.Width;projection[10]=-.001f;
            var globals=new Dictionary<string,float[]>(LightingGlobals(projection)){["unity_MatrixVP"]=projection,["unity_MatrixV"]=AdvMatrix.Identity(),["unity_MatrixInvV"]=AdvMatrix.Identity(),["glstate_matrix_projection"]=projection,["_WorldSpaceCameraPos"]=[0,0,-10]};
            actor.GameLighting=false;if(director)actor.SetDirectorTint(actor.Spec.Tint,actor.Spec.Brightness);
            int slot=director?-1:shown.First(pair=>ReferenceEquals(pair.Value,actor)).Key;int index=slot<0?0:(slot-1)/2;
            bool layer=!director&&(slotAlpha[index]<1||slotBrightness[index]<1||slotBlur[index]>0);
            if(layer)gl.BeginLayer();actor.DrawGameIsolated(globals);
            if(layer){gl.Blur(shaders,slotBlur[index],blurRadius);gl.EndLayer(slotAlpha[index],slotBrightness[index]);}
        }
        finally{root.Parent=parent;root.Position=position;root.Rotation=rotation;root.Scale=size;actor.GameLighting=lighting;}
    }
}
