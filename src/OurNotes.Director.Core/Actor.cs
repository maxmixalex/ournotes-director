// Animation sampling and controller behavior adapted from ournotes-player 0.1.6.
// See THIRD-PARTY.md and LICENSE (AGPL-3.0-only).
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed unsafe partial class Actor : IDisposable
{
    public NativeModel Core {get;} public JsonNode Manifest{get;} public ActorSpec Spec{get;}
    public AdvPrefab Prefab{get;}
    bool animationPaused;
    public bool AnimationPaused{get=>animationPaused;set{if(animationPaused==value)return;animationPaused=value;if(!value&&gameAnimation)ResumeGameRequests();}}
    public bool OverrideAngle{get;set;}
    public float AngleX{get;set;}
    public float BodyAngleX{get;set;}
    public bool LookEnabled{get;set;}
    public float LookX{get;set;}
    public float LookY{get;set;}
    public AdvParameterLoop ParameterLoop{get;}
    public Vector3 HeadPosition=>Prefab.TryTransform(Prefab.Find("Live2DCharacter").Path+"/Anchors/Head")?.WorldPosition??Vector3.Zero;
    readonly Queue<(string Motion,string Expression,float Fade,float Wait)> motionQueue=[];
    float queueWait;
    public void QueueMotion(string motion,string expr,float fade,float wait)
    {
        if(wait<=0){motionQueue.Clear();Motion(motion,fade);Expression(expr==""?DefaultExpression:expr,fade);return;}
        if(motionQueue.Count==0)queueWait=wait;motionQueue.Enqueue((motion,expr,fade,wait));
    }
    readonly Dictionary<string,int> ids; readonly Dictionary<string,Clip> clips=[];readonly Dictionary<string,JsonNode> expressions=[];
    readonly uint[] textures;readonly float canvasHeight;readonly List<(int i,JsonNode c)> harmonics=[];readonly List<int> eyes=[];
    readonly JsonNode? blink;readonly PhysicsRig? physics;readonly AdvRandom random;float nextBlink,blinkTime,blinkPhaseStart;int blinkPhase;
    float eyeOpening=1,blinkStopStart=1,blinkStopTime,blinkStopDuration;public bool EyeBlinkStopped{get;private set;}
    public void SetEyeBlinkStopped(bool stopped,float duration){if(!stopped){EyeBlinkStopped=false;return;}if(!EyeBlinkStopped){EyeBlinkStopped=true;blinkStopStart=eyeOpening;blinkStopTime=0;blinkStopDuration=duration;}else if(duration<=0)blinkStopDuration=0;}
    Clip? current;float motionTime;float[]? transition;float fadeTime,fadeDuration;string expression="";string? previousExpression;float expressionTime,expressionFade;
    public PreviewActorAnimation PreviewAnimation()=>new(Spec.Name,Spec.Model,current?.Name??DefaultMotion,
        gameAnimation?gameExpression.Length>0?gameExpression:DefaultExpression:expression.Length>0?expression:DefaultExpression,motionTime);
    public float[] Values{get;private set;} public string DefaultMotion{get;}public string DefaultExpression{get;}
    float renderOpacity=1;float elapsed;
    public string GeometryHash{get;private set;}="";
    static readonly float[] White=[1,1,1,1],Black=[0,0,0,0];
    public static float N(JsonNode? n,float fallback=0)=>n is null?fallback:float.Parse(n.ToString(),CultureInfo.InvariantCulture);
    public static bool B(JsonNode? n)=>n is not null && n.ToString() is "true" or "1";
    public Actor(Archive archive,string site,string dll,ActorSpec spec,OffscreenGl? gl,uint seed,AdvRandom? randomStream=null)
    {
        Spec=spec;Manifest=archive.Manifest(site,"models",spec.Model);var model=JsonNode.Parse(archive.Logical(site,Manifest,"model.json"))!;var prefab=JsonNode.Parse(archive.Logical(site,Manifest,Archive.S(model,"prefab")))!;
        gameModel=model;gamePrefab=prefab;Prefab=new(prefab);Core=new(dll,archive.Logical(site,Manifest,Archive.S(model,"moc3")));
        try{
        ids=Core.ParameterIds.Select((id,i)=>(id,i)).ToDictionary(p=>p.id,p=>p.i);Values=(float[])Core.Defaults.Clone();
        InitializeLipSync(dll);
        var components=prefab["nodes"]![0]!["components"]!.AsArray();JsonNode? Comp(string name)=>components.FirstOrDefault(c=>Archive.S(c!,"class")==name);
        var ch=Comp("Live2DCharacter")!;DefaultMotion=Archive.S(ch,"DefaultMotionName");DefaultExpression=Archive.S(ch,"DefaultExpressionName");
        ParameterLoop=new(Comp("CubismFadeController")?["CubismFadeMotionList"]?["CubismFadeMotionObjects"],Core);
        foreach(var c in ch["_motionList"]!.AsArray().OfType<JsonNode>()){var clip=new Clip(c,ids);clips.Add(clip.Name,clip);}
        foreach(var e in Comp("CubismExpressionController")?["ExpressionsList"]?["CubismExpressionObjects"]?.AsArray().OfType<JsonNode>()??[])expressions.Add(Archive.S(e,"name").Replace(".exp3",""),e);
        foreach(var node in prefab["nodes"]!.AsArray().OfType<JsonNode>())if(ids.TryGetValue(Archive.S(node,"name"),out var i))foreach(var c in node["components"]!.AsArray().OfType<JsonNode>()){if(Archive.S(c,"class")=="CubismEyeBlinkParameter")eyes.Add(i);if(Archive.S(c,"class")=="CubismHarmonicMotionParameter")harmonics.Add((i,c));}
        blink=Comp("CubismAutoEyeBlinkInput");random=randomStream??new(seed);nextBlink=Interval();
        if(Comp("CubismPhysicsController")?["_rig"] is JsonNode rig)physics=new(rig,Core,ids);
        canvasHeight=N(model["canvas"]!["height"])/N(model["canvas"]!["pixelsPerUnit"]);
        textures=gl is null?[]:model["textures"]!.AsArray().Select(t=>gl.Upload(Png.Decode(archive.Logical(site,Manifest,t!.ToString())))).ToArray();
        Motion(spec.Motion??DefaultMotion,0);Expression(spec.Expression??DefaultExpression,0);
        foreach(var id in spec.Parameters.Keys)if(!ids.ContainsKey(id))throw new ArgumentException($"Unknown parameter {spec.Name}:{id}");
        }catch{Core.Dispose();throw;}
    }
    internal int LoadedTextureCount=>textures.Length;
    string ParameterLabel(string id)
    {
        var node=gamePrefab["nodes"]!.AsArray().FirstOrDefault(n=>Archive.S(n!,"name")==id);
        var info=node?["components"]?.AsArray().FirstOrDefault(c=>Archive.S(c!,"class")=="CubismDisplayInfoParameterName");
        return info is null?"":Archive.S(info,"Name");
    }
    public object Description()=>JsonOutput.Object(("parameters", Core.ParameterIds.Select((id,i)=>JsonOutput.Object(("id", id), ("name", ParameterLabel(id)), ("min", Core.Min[i]), ("max", Core.Max[i]), ("default", Core.Defaults[i])))), ("motions", clips.Values.Select(c=>JsonOutput.Object(("id", c.Name), ("duration", c.Length)))), ("expressions", expressions.Keys), ("physics", physics is not null), ("drawables", Core.DrawableCount));
    public object ExpressionDescription(string name)
    {
        if(!expressions.TryGetValue(name,out var data))throw new ArgumentException($"Unknown expression {Spec.Model}:{name}");
        return JsonOutput.Object(("model", Spec.Model), ("id", name), ("fadeIn", N(data["FadeInTime"])), ("fadeOut", N(data["FadeOutTime"])), ("parameters", data["Parameters"]!.AsArray().Select(p=>{
            string id=Archive.S(p!,"Id");return JsonOutput.Object(("id", id), ("name", ParameterLabel(id)), ("value", N(p!["Value"])), ("blend", (int)N(p["Blend"]) switch{1=>"add",2=>"multiply",_=>"overwrite"}), ("available", ids.ContainsKey(id)));
        })));
    }
    public bool HasParameter(string name)=>ids.ContainsKey(name);
    public void Motion(string name,float fade){if(name=="")return;if(!clips.TryGetValue(name,out var clip))throw new ArgumentException($"Unknown motion {Spec.Model}:{name}");if(gameAnimation){if(AnimationPaused)gamePendingMotion=(name,fade);else GameStartMotion(clip,fade);return;}transition=(float[])Values.Clone();fadeTime=0;fadeDuration=fade;current=clip;motionTime=0;}
    public void Expression(string name,float fade){if(name=="")return;if(!expressions.ContainsKey(name))throw new ArgumentException($"Unknown expression {Spec.Model}:{name}");if(gameAnimation){if(AnimationPaused)gamePendingExpression=(name,fade);else GameStartExpression(name,fade);return;}previousExpression=expression;expression=name;expressionTime=0;expressionFade=fade;}
    float Interval()=>N(blink?["Mean"],2.5f)+random.Range(-N(blink?["MaximumDeviation"],2),N(blink?["MaximumDeviation"],2));
    float AutoBlink(float dt)
    {
        if(!Spec.Blink||blink is null)return 1;blinkTime+=dt*N(blink["Timescale"],10);float value;
        switch(blinkPhase){
            case 0:if(!EyeBlinkStopped&&(current is null||current.Name==DefaultMotion||gameAnimation&&gameMotions.All(m=>elapsed>m.End))){nextBlink-=dt;if(nextBlink<0){blinkPhase=1;blinkPhaseStart=blinkTime;}}value=1;break;
            case 1:float closing=blinkTime-blinkPhaseStart;if(closing>=1){blinkPhase=2;blinkPhaseStart=blinkTime;}value=1-closing;break;
            case 2:if((blinkTime-blinkPhaseStart)/.5f>=1){blinkPhase=3;blinkPhaseStart=blinkTime;}value=0;break;
            default:float opening=(blinkTime-blinkPhaseStart)/1.5f;if(opening>=1){blinkPhase=0;nextBlink=Interval();value=1;}else value=opening;break;
        }
        return value;
    }
    public void Step(Shot shot,double time,float dt)
    {
        if(AnimationPaused&&!gameAnimation)return;
        if(motionQueue.Count>0){queueWait-=dt*Spec.Speed; if(queueWait<=0){var next=motionQueue.Dequeue();Motion(next.Motion,next.Fade);Expression(next.Expression==""?DefaultExpression:next.Expression,next.Fade);if(motionQueue.TryPeek(out var queued))queueWait=queued.Wait;}}
        if(AnimationPaused)return;
        if(gameAnimation&&current is not null&&(AuthoredMotionLoop??current.Name==DefaultMotion)&&gameMotions.Count>0&&gameMotions.All(m=>elapsed>m.End))GameStartMotion(current,0);
        Values=(float[])(gameAnimation?gameStore:Core.Defaults).Clone();float eye=gameAnimation?eyeOpening:AutoBlink(dt);renderOpacity=1;
        if(gameAnimation&&current is not null)motionTime=Math.Min(motionTime+dt*Spec.Speed,Math.Max(0,current.Length-.0001f));
        current?.Sample(motionTime,!gameAnimation&&Spec.Loop,Values,ref eye,ref renderOpacity);
        if(gameAnimation){GameFadeMotion();Array.Copy(Values,gameStore,Values.Length);}
        if(transition is not null&&fadeTime<fadeDuration){float w=.5f-.5f*MathF.Cos(MathF.PI*fadeTime/fadeDuration);for(int i=0;i<Values.Length;i++)Values[i]=transition[i]+(Values[i]-transition[i])*w;}
        float[] ApplyExpression(string name,float[] v){if(expressions.TryGetValue(name,out var expr))foreach(var p in expr["Parameters"]!.AsArray().OfType<JsonNode>())if(ids.TryGetValue(Archive.S(p,"Id"),out var i)){float n=N(p["Value"]);v[i]=(int)N(p["Blend"]) switch{1=>v[i]+n,2=>v[i]*n,_=>n};}return v;}
        var before=(float[])Values.Clone();if(gameAnimation)GameApplyExpressions(dt);else ApplyExpression(expression,Values);
        if(!gameAnimation&&expressionTime<expressionFade){ApplyExpression(previousExpression??"",before);float w=.5f-.5f*MathF.Cos(MathF.PI*expressionTime/expressionFade);for(int i=0;i<Values.Length;i++)Values[i]=before[i]+(Values[i]-before[i])*w;}
        ParameterLoop.Step(dt*Spec.Speed,Values);
        if(EyeBlinkStopped){blinkStopTime+=dt*Spec.Speed;float amount=blinkStopDuration>0?Math.Clamp(blinkStopTime/blinkStopDuration,0,1):1;eye=blinkStopStart+(1-blinkStopStart)*amount;if(current?.Name==DefaultMotion)foreach(int i in eyes)Values[i]+=(Core.Defaults[i]-Values[i])*amount;}
        eyeOpening=eye;
        foreach(int i in eyes)Values[i]*=eye;
        if(gameAnimation)GameHarmonics(dt);else if(Spec.Breath)foreach(var (i,c) in harmonics){float range=Core.Max[i]-Core.Min[i];Values[i]+=Core.Min[i]+N(c["NormalizedOrigin"])*range+range*N(c["NormalizedRange"])*MathF.Sin(elapsed*MathF.Tau/N(c["Duration"],3));}
        foreach(var (id,value) in Spec.Parameters)Values[ids[id]]=value;
        foreach(var track in shot.Tracks.Where(t=>t.Target==Spec.Name&&t.Property.StartsWith("param:"))){var id=track.Property[6..];if(!ids.TryGetValue(id,out var i))throw new ArgumentException($"Unknown parameter {id}");Values[i]=shot.Value(Spec.Name,track.Property,time,Values[i]);}
        if(Spec.Physics)physics?.Evaluate(Values,dt);
        ApplyVoiceLipSync(dt);
        ApplyPseudoLipSync(dt);
        if(OverrideAngle){if(ids.TryGetValue("ParamAngleX",out int ax))Values[ax]+=AngleX;if(ids.TryGetValue("ParamBodyAngleXAdd",out int add))Values[add]=BodyAngleX;else if(ids.TryGetValue("ParamBodyAngleX",out int bx))Values[bx]+=BodyAngleX;}
        if(LookEnabled){if(ids.TryGetValue("ParamEyeBallX",out int x))Values[x]=LookX;if(ids.TryGetValue("ParamEyeBallY",out int y))Values[y]=LookY;}
        var automatic=DirectorParameters.Count>0?(float[])Values.Clone():null;
        foreach(var (id,value) in DirectorParameters)Values[ids[id]]=value;
        for(int i=0;i<Values.Length;i++)Values[i]=Math.Clamp(Values[i],Core.Min[i],Core.Max[i]);
        if(automatic is not null)for(int i=0;i<automatic.Length;i++)automatic[i]=Math.Clamp(automatic[i],Core.Min[i],Core.Max[i]);
        if(gameAnimation)
        {
            // Keep the native two-frame animation buffer. Authored parameter pins sit
            // over that displayed pose, so paused set/release and replay agree exactly.
            gameDisplay.Enqueue(automatic??(float[])Values.Clone());
            previewAutomaticValues=gameDisplay.Count>2?gameDisplay.Dequeue():gameDisplay.Peek();
            if(DirectorParameters.Count>0)RefreshPreviewParameters();else Core.Update(previewAutomaticValues);
            eyeOpening=AutoBlink(dt);
        }
        else{previewAutomaticValues=automatic??Values;Core.Update(Values);}
        if(!gameAnimation)motionTime+=dt*Spec.Speed;fadeTime+=dt;expressionTime+=dt;elapsed+=dt;
    }
    public void Draw(OffscreenGl gl,Shot shot,double time,float[]? worldToClip=null)
    {
        int n=Core.DrawableCount;var counts=Core.Ints("DrawableVertexCounts",n);var indicesN=Core.Ints("DrawableIndexCounts",n);var orders=Core.Ints("RenderOrders",n);var ti=Core.Ints("DrawableTextureIndices",n);var masksN=Core.Ints("DrawableMaskCounts",n);var opacity=Core.Floats("DrawableOpacities",n);
        var pos=(nint*)Core.Pointer("DrawableVertexPositions");var uvs=(nint*)Core.Pointer("DrawableVertexUvs");var inds=(nint*)Core.Pointer("DrawableIndices");var masks=(nint*)Core.Pointer("DrawableMasks");var flags=(byte*)Core.Pointer("DrawableConstantFlags");
        var multiply=Core.Floats("DrawableMultiplyColors",n*4);var screen=Core.Floats("DrawableScreenColors",n*4);
        float Get(string p,float v)=>shot.Value(Spec.Name,p,time,v);
        var x=Get("x",Spec.X);var y=Get("y",Spec.Y);var scale=Get("height",Spec.Height)*2/canvasHeight;var aspect=shot.Output.Width/(float)shot.Output.Height;var rotate=Get("rotation",Spec.Rotation)*MathF.PI/180;var co=MathF.Cos(rotate);var si=MathF.Sin(rotate);
        var zoom=shot.Value("camera","zoom",time,shot.Camera.Zoom);var cx=shot.Value("camera","x",time,shot.Camera.X);var cy=shot.Value("camera","y",time,shot.Camera.Y);var roll=shot.Value("camera","roll",time,shot.Camera.Roll)*MathF.PI/180;var cr=MathF.Cos(roll);var sr=MathF.Sin(roll);
        float[][] xy=new float[n][],uv=new float[n][];ushort[][] triangles=new ushort[n][];
        for(int d=0;d<n;d++){xy[d]=new float[counts[d]*2];uv[d]=new float[counts[d]*2];triangles[d]=new ushort[indicesN[d]];Marshal.Copy(pos[d],xy[d],0,xy[d].Length);Marshal.Copy(uvs[d],uv[d],0,uv[d].Length);new ReadOnlySpan<ushort>((void*)inds[d],indicesN[d]).CopyTo(triangles[d]);for(int j=0;j<counts[d];j++){float px=xy[d][j*2]*scale,py=xy[d][j*2+1]*scale;float ax=(px*co-py*si)+(x*2-1-cx*2)*aspect;float ay=px*si+py*co+(1-y*2+cy*2);xy[d][j*2]=(ax*cr-ay*sr)*zoom/aspect;xy[d][j*2+1]=(ax*sr+ay*cr)*zoom;uv[d][j*2+1]=1-uv[d][j*2+1];}}
        if(worldToClip is not null)for(int d=0;d<n;d++){
            var raw=new ReadOnlySpan<float>((void*)pos[d],counts[d]*2);xy[d]=new float[counts[d]*4];
            for(int j=0;j<counts[d];j++){var p=AdvMatrix.Transform(worldToClip,new Vector4(raw[j*2],raw[j*2+1],0,1));xy[d][j*4]=p.X;xy[d][j*4+1]=p.Y;xy[d][j*4+2]=p.Z;xy[d][j*4+3]=p.W;}
        }
        using(var data=new MemoryStream()){foreach(var vertices in xy)data.Write(MemoryMarshal.AsBytes(vertices.AsSpan()));data.Write(MemoryMarshal.AsBytes(opacity.AsSpan()));data.Write(MemoryMarshal.AsBytes(orders.AsSpan()));GeometryHash=Archive.Hash(data.ToArray());}
        float bright=Get("brightness",Spec.Brightness);float[] tint=[Spec.Tint[0]*bright,Spec.Tint[1]*bright,Spec.Tint[2]*bright,Spec.Tint[3]];
        string lastMask="";
        foreach(var d in Enumerable.Range(0,n).OrderBy(i=>orders[i])){
            if(opacity[d]<=.00001||indicesN[d]==0)continue;
            if(masksN[d]>0){var members=new ReadOnlySpan<int>((void*)masks[d],masksN[d]).ToArray();var key=string.Join(',',members);if(key!=lastMask){gl.BeginMask();foreach(var m in members)gl.Draw(textures[ti[m]],xy[m],uv[m],triangles[m],1,White,Black,White,0,false,false,true);gl.EndMask();lastMask=key;}}
            gl.Draw(textures[ti[d]],xy[d],uv[d],triangles[d],opacity[d]*renderOpacity*Get("opacity",Spec.Opacity),multiply[(d*4)..(d*4+4)],screen[(d*4)..(d*4+4)],tint,(flags[d]&1)!=0?1:(flags[d]&2)!=0?2:0,masksN[d]>0,(flags[d]&8)!=0);
        }
    }
    public void Dispose(){mouthAnalysis?.Dispose();gameDrawing?.Dispose();Core.Dispose();}
    sealed class Clip
    {
        public string Name{get;}public float Length{get;}public int InstanceId{get;}readonly List<(float t,float[] k)>[] curves;readonly int[] destinations;readonly string[] fields;readonly float[] constants;
        public Clip(JsonNode node,Dictionary<string,int> ids)
        {
            Name=Archive.S(node,"clip");Length=N(node["stopTime"]);if(N(node["dense"]?["curveCount"])!=0||N(node["startTime"])!=0||N(node["cycleOffset"])!=0)throw new NotSupportedException($"Unsupported clip layout {Name}");
            InstanceId=node["events"]?.AsArray().LastOrDefault(e=>Archive.S(e!,"functionName")=="InstanceId")?["intParameter"]?.GetValue<int>()??-1;
            var bindings=node["bindings"]!.AsArray();destinations=new int[bindings.Count];fields=new string[bindings.Count];for(int i=0;i<bindings.Count;i++){var b=bindings[i]!;var path=Archive.S(b,"path");destinations[i]=path.StartsWith("Parameters/")&&ids.TryGetValue(path[11..],out var dest)?dest:-1;fields[i]=path==""?Archive.S(b,"attribute"):"";}
            curves=Enumerable.Range(0,(int)N(node["streamed"]!["curveCount"])).Select(_=>new List<(float,float[])>()).ToArray();foreach(var f in node["streamed"]!["frames"]!.AsArray())foreach(var key in f![1]!.AsArray()){var k=key!.AsArray();curves[(int)N(k[0])].Add((N(f[0]),[N(k[1]),N(k[2]),N(k[3]),N(k[4])]));}constants=node["constant"]!.AsArray().Select(v=>N(v)).ToArray();
        }
        public void Sample(float time,bool loop,float[] values,ref float eye,ref float opacity)
        {
            float t=loop&&Length>0?time%Length:Math.Min(time,Length);
            for(int c=0;c<destinations.Length;c++){float v;if(c<curves.Length){var keys=curves[c];int lo=0,hi=keys.Count-1;while(lo<hi){int mid=(lo+hi+1)/2;if(keys[mid].t<=t)lo=mid;else hi=mid-1;}var key=keys[lo];float x=t-key.t;var k=key.k;v=((k[0]*x+k[1])*x+k[2])*x+k[3];}else v=constants[c-curves.Length];if(destinations[c]>=0)values[destinations[c]]=v;else if(fields[c]=="EyeOpening")eye=v;else if(fields[c]=="Opacity")opacity=v;}
        }
    }
}
