using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed unsafe partial class Actor
{
    public Dictionary<string,float> DirectorParameters{get;}=[];
    public float CanvasHeight=>canvasHeight;
    internal JsonNode CanvasMetadata => gameModel["canvas"]!;
    public void DirectorAnimation(bool enabled){gameAnimation=!enabled;}
    public void WarmupDirector(int frames){if(frames<0||frames>300)throw new ArgumentException("warmupFrames must be 0..300");for(int i=0;i<frames;i++)Step(new Shot(),0,1f/30);Motion(current?.Name??DefaultMotion,0);Expression(expression==""?DefaultExpression:expression,0);}
    public float ParameterValue(string id)=>ids.TryGetValue(id,out int index)?Values[index]:throw new ArgumentException("Unknown model parameter: "+id);
    readonly JsonNode gameModel,gamePrefab;
    GameActorDrawing? gameDrawing;
    public void EnableGameDrawing(Archive archive,string site,OffscreenGl gl){gameDrawing??=new(archive,site,Manifest,gameModel,gamePrefab,Prefab,Core,gl,textures);InitializeGameAnimation();}
    public void DrawGame(IReadOnlyDictionary<string,float[]> globals)=> (gameDrawing??throw new InvalidOperationException("Game materials not initialized")).Draw(globals,renderOpacity*Spec.Opacity);
    public IReadOnlyList<AdvRenderItem> GameDrawItems(IReadOnlyDictionary<string,float[]> globals,int baseOrder)=>(gameDrawing??throw new InvalidOperationException("Game materials not initialized")).Items(globals,renderOpacity*Spec.Opacity,baseOrder);
    public void ToggleRim(float[] color,float shadow){if(gameDrawing is null)throw new InvalidOperationException("Game materials not initialized");gameDrawing.RimEnabled=!gameDrawing.RimEnabled;gameDrawing.RimColor=color;gameDrawing.ShadowIntensity=Math.Clamp(shadow,0,1);}
    public bool GameLighting{get=>gameDrawing?.LightingEnabled??false;set{if(gameDrawing is not null)gameDrawing.LightingEnabled=value;}}
    public void SetDirectorTint(float[] tint,float brightness){if(gameDrawing is null)throw new InvalidOperationException("Game drawing not initialized");gameDrawing.DirectorTint=tint;gameDrawing.DirectorBrightness=brightness;}
    public PreviewTarget? PreviewBounds(float[] viewProjection)=>Spec.Opacity<=0||renderOpacity<=0?null:gameDrawing?.PreviewBounds(Spec.Name,Spec.Name,viewProjection);
    internal void RefreshPreviewParameters()
    {
        foreach(var (id,value) in DirectorParameters)Values[ids[id]]=Math.Clamp(value,Core.Min[ids[id]],Core.Max[ids[id]]);
        Core.Update(Values);
    }
}
