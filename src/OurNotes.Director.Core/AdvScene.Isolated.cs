using System.Numerics;

namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    /// <summary>Draw all currently visible actors with their original camera, slots,
    /// fades and lighting. No room, foreground UI, captured background or scene effects.</summary>
    public void DrawCharacters()
    {
        gl.Begin([0,0,0,0]);
        var globals=LightingGlobals(Camera.ViewProjection(gl.Width/(float)gl.Height));
        var ordered=shown.OrderByDescending(pair=>pair.Value.Prefab.Root.WorldPosition.Z).ThenBy(pair=>priority[(pair.Key-1)/2]).ToArray();
        static bool Approx(float a,float b)=>Math.Abs(a-b)<Math.Max(1e-6f*Math.Max(Math.Abs(a),Math.Abs(b)),float.Epsilon*8);
        bool Same(int a,int b)=>Approx(slotAlpha[a],slotAlpha[b])&&Approx(slotBrightness[a],slotBrightness[b])&&Approx(slotBlur[a],slotBlur[b]);
        // Keep the source compositor's grouping: fading overlapping actors separately
        // would change their shared edge/alpha compared with the original performance.
        for(int first=0;first<ordered.Length;)
        {
            int slot=(ordered[first].Key-1)/2,last=first+1;
            while(last<ordered.Length&&Same(slot,(ordered[last].Key-1)/2))last++;
            float alpha=slotAlpha[slot],brightness=slotBrightness[slot],blur=slotBlur[slot];
            bool separate=alpha<1||brightness<1||blur>0;
            if(separate)gl.BeginLayer();
            AdvRenderItem.DrawAll(ordered[first..last].SelectMany((pair,index)=>pair.Value.GameDrawItems(globals,(first+index)*1000)));
            if(separate){gl.Blur(shaders,blur,blurRadius);gl.EndLayer(alpha,brightness);}
            first=last;
        }
        DrawDirectorActors(globals);
    }

    /// <summary>Extract the evaluated model using its native canvas, without source framing
    /// or editor offsets. Audio, animation, physics, fades and costume changes still run.</summary>
    public PreviewTarget? IsolatedPreviewTarget { get; private set; }
    public void DrawIsolatedActor(string target, IsolatedActorTransform? presentation = null, IsolatedActorCanvas? canvas = null)
    {
        gl.Begin([0,0,0,0]);
        IsolatedPreviewTarget=IsolatedActorBounds(target,presentation,canvas?.ReferenceWidth??gl.Width,
            canvas?.ReferenceHeight??gl.Height,canvas,true);
    }

    /// <summary>Measures the evaluated drawable geometry in the original isolation
    /// canvas, including off-canvas vertices, without drawing or reading pixels.</summary>
    public PreviewTarget? MeasureIsolatedActorBounds(string target,IsolatedActorTransform? presentation,int referenceWidth,int referenceHeight)
        =>IsolatedActorBounds(target,presentation,referenceWidth,referenceHeight,null,false);

    PreviewTarget? IsolatedActorBounds(string target,IsolatedActorTransform? presentation,int referenceWidth,int referenceHeight,IsolatedActorCanvas? crop,bool draw)
    {
        presentation??=new();
        var actor=Character(target);if(actor is null||presentation.Opacity<=0)return null;
        bool director=directorActors.TryGetValue(actor,out var state);
        if(director?!state!.Visible:!shown.Values.Contains(actor))return null;
        var canvas=actor.CanvasMetadata;float width=Actor.N(canvas["width"]),height=Actor.N(canvas["height"]);
        if(width<=0||height<=0)throw new InvalidDataException("角色画布尺寸无效。");
        float pixelsPerUnit=height/actor.CanvasHeight,scale=Math.Min(referenceWidth/width,referenceHeight/height);
        var root=actor.Prefab.Root;var parent=root.Parent;var position=root.Position;var rotation=root.Rotation;var size=root.Scale;bool lighting=actor.GameLighting;
        try
        {
            root.Parent=null;root.Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,(float)presentation.Rotation*MathF.PI/180);
            var origin=new Vector3(((canvas["originX"] is null?width/2:Actor.N(canvas["originX"]))-width/2)*scale*2/referenceHeight,((canvas["originY"] is null?height/2:Actor.N(canvas["originY"]))-height/2)*scale*2/referenceHeight,0);
            root.Position=Vector3.Transform(origin*(float)presentation.Scale,root.Rotation)+new Vector3((float)(presentation.X-.5)*2*referenceWidth/referenceHeight,(float)(.5-presentation.Y)*2,0);
            root.Scale=Vector3.One*(2*scale*pixelsPerUnit/referenceHeight*(float)presentation.Scale);
            var projection=AdvMatrix.Identity();projection[0]=referenceHeight/(float)referenceWidth;projection[10]=-.001f;
            if(crop is not null)
            {
                double fit=Math.Min(gl.Width/(crop.Width*referenceWidth),gl.Height/(crop.Height*referenceHeight));
                float sx=(float)(referenceWidth*fit/gl.Width),sy=(float)(referenceHeight*fit/gl.Height);
                var framing=AdvMatrix.Identity();framing[0]=sx;framing[5]=sy;
                framing[12]=sx*(float)(1-2*(crop.X+crop.Width/2));
                framing[13]=sy*(float)(2*(crop.Y+crop.Height/2)-1);
                projection=AdvMatrix.Multiply(framing,projection);
            }
            actor.GameLighting=false;if(director)actor.SetDirectorTint(actor.Spec.Tint,actor.Spec.Brightness);
            int slot=director?-1:shown.First(pair=>ReferenceEquals(pair.Value,actor)).Key;int index=slot<0?0:(slot-1)/2;
            float alpha=(float)presentation.Opacity*(director?1:slotAlpha[index]),brightness=director?1:slotBrightness[index],blur=director?0:slotBlur[index];
            if(alpha<=0)return null;
            if(draw)
            {
                var globals=new Dictionary<string,float[]>(LightingGlobals(projection)){["unity_MatrixVP"]=projection,["unity_MatrixV"]=AdvMatrix.Identity(),["unity_MatrixInvV"]=AdvMatrix.Identity(),["glstate_matrix_projection"]=projection,["_WorldSpaceCameraPos"]=[0,0,-10]};
                bool layer=alpha<1||brightness<1||blur>0;
                if(layer)gl.BeginLayer();actor.DrawGameIsolated(globals);
                if(layer){gl.Blur(shaders,blur,blurRadius);gl.EndLayer(alpha,brightness);}
            }
            var bounds=actor.PreviewIsolatedBounds(projection,freshMatrices:!draw);
            // The two-pass source lens blur uses a half-size intermediate. Reserve
            // its maximum filter support and bilinear taps in reference pixel units;
            // an alpha-free geometry analysis must not crop that visible halo.
            if(!draw&&bounds is not null&&blur>0)
            {
                double radius=4*Math.Ceiling(Math.Max(0,blurRadius))+4;
                double x=radius/referenceWidth,y=radius/referenceHeight;
                bounds=bounds with{X=bounds.X-x,Y=bounds.Y-y,Width=bounds.Width+2*x,Height=bounds.Height+2*y};
            }
            return bounds;
        }
        finally{root.Parent=parent;root.Position=position;root.Rotation=rotation;root.Scale=size;actor.GameLighting=lighting;}
    }
}
