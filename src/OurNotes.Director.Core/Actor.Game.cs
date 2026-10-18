using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed unsafe partial class Actor
{
    public Dictionary<string,float> DirectorParameters{get;}=[];
    public float CanvasHeight=>canvasHeight;
    internal JsonNode CanvasMetadata=>gameModel["canvas"]!;
    // Screen-space corrections sit above the original performance, so dragging a source
    // actor never detaches its game slot, restarts its animation or discards camera motion.
    internal float EditorOffsetX,EditorOffsetY,EditorScale=1;
    // A null override preserves the game's default-motion restart rule. Only explicit
    // workbench commands change it; captured performances keep their original mixer.
    internal bool? AuthoredMotionLoop;
    internal void SetMotionLoop(bool loop){Spec.Loop=loop;AuthoredMotionLoop=loop;}
    public void DirectorAnimation(bool enabled){gameAnimation=!enabled;}
    public void WarmupDirector(int frames){if(frames<0||frames>300)throw new ArgumentException("warmupFrames must be 0..300");for(int i=0;i<frames;i++)Step(new Shot(),0,1f/30);Motion(current?.Name??DefaultMotion,0);Expression(expression==""?DefaultExpression:expression,0);}
    public float ParameterValue(string id)=>ids.TryGetValue(id,out int index)?Values[index]:throw new ArgumentException("Unknown model parameter: "+id);
    readonly JsonNode gameModel,gamePrefab;
    GameActorDrawing? gameDrawing;
    public void EnableGameDrawing(Archive archive,string site,OffscreenGl gl){gameDrawing??=new(archive,site,Manifest,gameModel,gamePrefab,Prefab,Core,gl,textures);InitializeGameAnimation();}
    bool HasEditorOverlay=>EditorOffsetX!=0||EditorOffsetY!=0||EditorScale!=1;
    float[] OverlayProjection(float[] projection)
    {
        if(!HasEditorOverlay)return projection;
        var p=AdvMatrix.Transform(projection,new Vector4(Prefab.Root.WorldPosition,1));
        float cx=Math.Abs(p.W)>.000001f?p.X/p.W:0,cy=Math.Abs(p.W)>.000001f?p.Y/p.W:0;
        var overlay=AdvMatrix.Identity();overlay[0]=overlay[5]=EditorScale;
        overlay[12]=2*EditorOffsetX+cx*(1-EditorScale);overlay[13]=-2*EditorOffsetY+cy*(1-EditorScale);
        return AdvMatrix.Multiply(overlay,projection);
    }
    IReadOnlyDictionary<string,float[]> OverlayGlobals(IReadOnlyDictionary<string,float[]> globals)
    {
        if(!HasEditorOverlay||!globals.TryGetValue("unity_MatrixVP",out var projection))return globals;
        return new Dictionary<string,float[]>(globals){["unity_MatrixVP"]=OverlayProjection(projection)};
    }
    public void DrawGame(IReadOnlyDictionary<string,float[]> globals)=>(gameDrawing??throw new InvalidOperationException("Game materials not initialized")).Draw(OverlayGlobals(globals),renderOpacity*Spec.Opacity);
    internal void DrawGameIsolated(IReadOnlyDictionary<string,float[]> globals)=>(gameDrawing??throw new InvalidOperationException("Game materials not initialized")).Draw(globals,renderOpacity*Spec.Opacity);
    public IReadOnlyList<AdvRenderItem> GameDrawItems(IReadOnlyDictionary<string,float[]> globals,int baseOrder)=>(gameDrawing??throw new InvalidOperationException("Game materials not initialized")).Items(OverlayGlobals(globals),renderOpacity*Spec.Opacity,baseOrder);
    public void ToggleRim(float[] color,float shadow){if(gameDrawing is null)throw new InvalidOperationException("Game materials not initialized");gameDrawing.RimEnabled=!gameDrawing.RimEnabled;gameDrawing.RimColor=color;gameDrawing.ShadowIntensity=Math.Clamp(shadow,0,1);}
    public bool GameLighting{get=>gameDrawing?.LightingEnabled??false;set{if(gameDrawing is not null)gameDrawing.LightingEnabled=value;}}
    public void SetDirectorTint(float[] tint,float brightness){if(gameDrawing is null)throw new InvalidOperationException("Game drawing not initialized");gameDrawing.DirectorTint=tint;gameDrawing.DirectorBrightness=brightness;}
    public PreviewTarget? PreviewBounds(float[] viewProjection)=>Spec.Opacity<=0||renderOpacity<=0?null:gameDrawing?.PreviewBounds(Spec.Name,Spec.Name,OverlayProjection(viewProjection));
    internal PreviewTarget? PreviewIsolatedBounds(float[] viewProjection)=>Spec.Opacity<=0||renderOpacity<=0?null:gameDrawing?.PreviewBounds(Spec.Name,Spec.Name,viewProjection);
    float[]? previewAutomaticValues;
    internal void RefreshPreviewParameters()
    {
        previewAutomaticValues??=Core.Floats("ParameterValues",Core.ParameterIds.Length);
        var displayed=(float[])previewAutomaticValues.Clone();
        foreach(var(id,value)in DirectorParameters)displayed[ids[id]]=Math.Clamp(value,Core.Min[ids[id]],Core.Max[ids[id]]);
        Core.Update(displayed);
    }
}
