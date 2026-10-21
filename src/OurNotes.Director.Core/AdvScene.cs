// Native port of story/field.js and character/camera/placement commands in ournotes-player (AGPL-3.0-only).
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvScene : IDisposable
{
    readonly Archive archive;readonly string site,dll,language;readonly JsonNode manifest,definition,settings;
    readonly OffscreenGl gl;readonly AdvLoop loop;readonly AdvTweens tweens;
    readonly UnityShaderLibrary shaders;
    readonly AdvPrefab fieldPrefab,backgroundPrefab;
    readonly AdvTransform field,background,spriteTransform;
    readonly AdvTransform[] slots,anchors;
    readonly Dictionary<string,JsonNode> stages=[];
    readonly Dictionary<string,AdvPrefab> stagePrefabs=[];
    readonly Dictionary<string,int> stageLightGroups=[];
    readonly Dictionary<string,uint> textures=[];
    readonly Dictionary<(string,int),Actor> actors=[];
    readonly Dictionary<string,int> costumes=[],positions=[];
    readonly Dictionary<int,Actor> shown=[];
    readonly Dictionary<string,AdvTween> running=[];
    readonly Dictionary<string,float> rotationY=[];
    readonly Shot shot=new();
    readonly int[] priority=[1,3,4,2,0];
    readonly float[] slotAlpha=[1,1,1,1,1];
    readonly float[] clearColor;
    readonly float[] slotBrightness=[1,1,1,1,1];float backgroundBrightness=1;
    readonly float[] slotBlur=new float[5];float backgroundBlur,blurRadius=12;int focusDistance;
    static readonly float[] BlurRadii=[.75f,2.25f,3.75f,5.5f,7.75f,9.75f,12];
    readonly Dictionary<Actor,(float X,float Y)> originalLook=[];
    readonly HashSet<string> stoppedEyeBlinks=[];
    JsonNode? stage;
    Vector3 focusPosition,panBase;float focusZoom=1;
    AdvWait? pendingCapture;uint captureTexture;bool captureActive;float captureAlpha;
    public AdvCamera Camera{get;}
    public IReadOnlyDictionary<int,Actor> Shown=>shown;
    public string StageName{get;private set;}="";
    internal bool TimingOnly{get;init;}
    internal int PreloadedActorCount=>actors.Count;
    internal int PreloadedModelTextureCount=>actors.Values.Sum(actor=>actor.LoadedTextureCount);
    internal int CaptureWaitCount{get;private set;}
    internal int CaptureDrawCount{get;private set;}
    public AdvScene(Archive archive,string site,string dll,JsonNode manifest,JsonNode definition,JsonNode scene,string language,OffscreenGl gl,AdvLoop loop,AdvTweens tweens)
    {
        this.archive=archive;this.site=site;this.dll=dll;this.manifest=manifest;this.definition=definition;this.language=language;this.gl=gl;this.loop=loop;this.tweens=tweens;
        shaders=new(archive,site,manifest);
        InitializePost(scene);
        settings=scene["settings"]!["playerSettings"]!;Camera=AdvCamera.FromScene(loop,tweens,scene);
        var cameraNode=scene["cameraManager"]!["nodes"]!.AsArray().First(n=>Archive.S(n!,"path")=="CameraManager/MainCamera")!;
        var cameraSettings=cameraNode["components"]!.AsArray().First(c=>Archive.S(c!,"type")=="Camera")!;var clear=cameraSettings["m_BackGroundColor"]!;
        clearColor=new[]{"r","g","b","a"}.Select(k=>Actor.N(clear[k])).ToArray();
        fieldPrefab=new(scene["characterField"]!);var fc=fieldPrefab.Find("AdvCharacterField").Component;
        field=fieldPrefab.Transform(Archive.S(fc["_field"]!,"transform"));
        slots=fc["_characterStages"]!.AsArray().Select(r=>fieldPrefab.Transform(Archive.S(r!,"transform"))).ToArray();
        anchors=fc["_focusAnchors"]!.AsArray().Select(r=>fieldPrefab.Transform(Archive.S(r!,"transform"))).ToArray();
        backgroundPrefab=new(scene["backgroundField"]!);var bc=backgroundPrefab.Find("AdvBackgroundField").Component;
        background=backgroundPrefab.Transform(Archive.S(bc["_field"]!,"transform"));spriteTransform=backgroundPrefab.Transform(Archive.S(bc["_renderer"]!,"gameObject"));
        InitializeBackground(bc);
        foreach(var entry in scene["stages"]!.AsObject()){var prefab=new AdvPrefab(entry.Value!);stagePrefabs.Add(entry.Key,prefab);stages.Add(entry.Key,prefab.Find("AdvStage").Component);}
        AdvAnimRecords? stageAnimationRecords=null;
        foreach(var entry in stages){var collection=entry.Value["_particleEffectGroupCollection"];if(collection?["_groups"]?.AsArray().Any(g=>g?["_particleEffects"]?.AsArray().Count>0)!=true)continue;
            stageAnimationRecords??=new(scene);effectMaterials??=new(archive,site,manifest,gl,shaders);
            stageEffects[entry.Key]=new(scene["stages"]![entry.Key]!,stagePrefabs[entry.Key],collection,stageAnimationRecords,effectRandom,loop);
        }
        shot.Output=new(){Width=gl.Width,Height=gl.Height,RenderScale=1};
        loop.On(AdvPhase.PreLateEnd,()=>{if(!TimingOnly)foreach(var actor in shown.Values.Concat(directorActors.Keys).Distinct())actor.Step(shot,loop.Time,(float)loop.DeltaTime);});
        loop.On(AdvPhase.Animation,AnimateEffects);
    }
    public void Preload(IEnumerable<AdvRow> rows,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PreloadEffects(rows);
        foreach(var row in rows.Where(r=>!r.Ignored&&r.Command=="Character")){
            cancellationToken.ThrowIfCancellationRequested();
            var key=(row.Text("TargetName"),(int)row.Number("TargetAssetIndex"));if(actors.ContainsKey(key))continue;
            var address="Character/Live2D/"+row.Text("TargetAssetName");
            string id=row.Text("Model") is {Length:>0} explicitModel?explicitModel:definition["models"]?[address]?.GetValue<string>()??throw new InvalidDataException("Model address absent from story: "+address);
            bool customSeed=row.Data["Seed"] is not null;
            var actor=new Actor(archive,site,dll,new(){Name=key.Item1,Model=id},TimingOnly?null:gl,customSeed?uint.Parse(row.Text("Seed"),CultureInfo.InvariantCulture):722,customSeed?null:shakeRandom);
            // Timing uses the same native model, motion metadata and four warmup
            // steps, but does not decode textures or allocate drawing/mask meshes.
            if(TimingOnly)actor.EnableGameTiming();else actor.EnableGameDrawing(archive,site,gl);
            var ch=actor.Prefab.Find("Live2DCharacter").Component;
            actor.Prefab.Root.Position=AdvPrefab.Vector(ch["BasePosition"]);actor.Prefab.Root.Scale=new(Actor.N(ch["BaseScale"],1));actor.Prefab.Root.Rotation=Quaternion.Identity;
            actors.Add(key,actor);
        }
        foreach(var actor in actors.Values)actor.BeginGameWarmup();
        for(int frame=0;frame<4;frame++)foreach(var actor in actors.Values){cancellationToken.ThrowIfCancellationRequested();actor.Step(shot,frame/30d,1f/30);}
    }
    public Actor? Character(string name)=>actors.GetValueOrDefault((name,costumes.GetValueOrDefault(name)));
    public AdvTransform Slot(int pos)=>pos is 1 or 3 or 5 or 7 or 9?slots[(pos-1)/2]:throw new ArgumentException("PositionType has no character slot: "+pos);
    bool HasStageActor(int pos)=>(pos is 1 or 3 or 5 or 7 or 9)&&actors.Values.Any(actor=>ReferenceEquals(actor.Prefab.Root.Parent,Slot(pos)));
    public Vector3 FocusPoint(int pos)=>pos>=1&&pos<=9?anchors[pos-1].WorldPosition:Vector3.Zero;
    static float Param(AdvRow r,int index)=>float.TryParse(r.Text("Parameter"+index),NumberStyles.Float,CultureInfo.InvariantCulture,out float v)&&float.IsFinite(v)?v:0;
    static AdvEase Ease(AdvRow r,AdvEase fallback=AdvEase.OutQuad)=>Enum.TryParse<AdvEase>(r.Text("Parameter2"),false,out var e)&&Enum.IsDefined(e)?e:fallback;
    public void SetStage(string name,int pointsIndex=0)
    {
        stageEffects.GetValueOrDefault(StageName)?.Stop();
        stage=name==""?null:stages.GetValueOrDefault(name)??throw new InvalidDataException("Unknown stage: "+name);StageName=name;
        if(stageEffects.TryGetValue(name,out var group)){group.Play();group.SetSpeed(1);}
        SetStageVolume();
        for(int i=0;i<slots.Length;i++){if(running.Remove("slotY"+i,out var old))old.Kill();rotationY["slotY"+i]=0;slots[i].Rotation=Quaternion.Identity;}
        if(stage is null){field.Position=Vector3.Zero;foreach(var t in slots.Concat(anchors))t.Position=Vector3.Zero;foreach(var t in slots)t.Scale=Vector3.One;Camera.ResetFov();Camera.Reset();panBase=Vector3.Zero;return;}
        var entries=stage["_focusPointsCollection"]!["_entries"]!.AsArray();var points=entries[pointsIndex>=0&&pointsIndex<entries.Count?pointsIndex:0]!["_focusPointPositions"]!.AsArray();
        for(int i=0;i<anchors.Length;i++)anchors[i].Position=AdvPrefab.Vector(points[i]);
        for(int i=0;i<slots.Length;i++){slots[i].Position=AdvPrefab.Vector(points[i*2]);slots[i].Scale=new(Actor.N(stage["_characterFieldScale"],1));}
        field.Position=AdvPrefab.Vector(stage["_characterFieldPosition"]);background.Position=AdvPrefab.Vector(stage["_backgroundFieldPosition"]);spriteTransform.Scale=new(Actor.N(stage["_backgroundFieldScale"],1));
        Camera.FieldOfView=Actor.N(stage["_fov"]);Camera.RotateY(0);panBase=Vector3.Zero;
        var pos=AdvPrefab.Vector(stage["_initialCameraPosition"]);var rot=AdvPrefab.Vector(stage["_initialCameraRotation"]);
        if(pos.LengthSquared()>=1e-10){Camera.Position=pos;panBase=pos;}if(rot.LengthSquared()>=1e-10)Camera.Euler=rot;
    }
    public void Show(string name,int pos,string motion="",string expression="",float fade=0)
    {
        // AdvCharacterField.ExistsCharacter tests the slot's actual children. An
        // In can move an already visible character without an intervening Out.
        if(HasStageActor(pos))return;var actor=Character(name);positions[name]=pos;if(actor is null)return;
        // Keep one renderer entry for that model after reparenting. Otherwise the
        // old slot both draws the moved actor again and blocks the next In there
        // (10060: hotaru 5 -> 1 -> 9, then kanata enters the now-empty slot 5).
        RemoveStageActor(actor);
        if(directorActors.Remove(actor)){
            var character=actor.Prefab.Find("Live2DCharacter").Component;
            actor.Prefab.Root.Position=AdvPrefab.Vector(character["BasePosition"]);
            actor.Prefab.Root.Scale=new(Actor.N(character["BaseScale"],1));
            actor.Prefab.Root.Rotation=Quaternion.Identity;
            actor.DirectorAnimation(false);
        }
        actor.ResetGameAnimation();
        actor.Prefab.Root.Parent=pos is 1 or 3 or 5 or 7 or 9?Slot(pos):null;
        actor.Motion(motion==""?actor.DefaultMotion:motion,fade);actor.Expression(expression==""?actor.DefaultExpression:expression,fade);actor.OverrideAngle=actor.LookEnabled=false;
        actor.SetEyeBlinkStopped(stoppedEyeBlinks.Contains(name),0);
        if(pos is 1 or 3 or 5 or 7 or 9){shown[pos]=actor;slotAlpha[(pos-1)/2]=1;}
    }
    AdvWait Tween(string key,Func<float> from,float to,float seconds,AdvEase ease,Action<float> set)
    {
        if(running.Remove(key,out var old))old.Kill();var tween=tweens.To(from,to,seconds,ease,set);running[key]=tween;return tween.Completed;
    }
    AdvWait Rotate(string key,AdvTransform transform,float angle,float seconds,AdvEase ease)=>Tween(key,()=>rotationY.GetValueOrDefault(key),angle,seconds,ease,v=>{rotationY[key]=v;transform.SetEuler(new(0,v,0));});
    AdvWait RotateSlots(float angle,float seconds,AdvEase ease)=>loop.All(slots.Select((s,i)=>Rotate("slotY"+i,s,angle,seconds,ease)).ToArray());
    AdvWait MoveSlot(int pos,Vector3 delta,float seconds)
    {
        var t=Slot(pos);var end=t.Position+delta;
        // DOLocalMove does not kill other position tweens.
        return loop.All(new[]{tweens.To(()=>t.Position.X,end.X,seconds,AdvEase.OutQuad,v=>t.Position=t.Position with{X=v}).Completed,tweens.To(()=>t.Position.Y,end.Y,seconds,AdvEase.OutQuad,v=>t.Position=t.Position with{Y=v}).Completed,tweens.To(()=>t.Position.Z,end.Z,seconds,AdvEase.OutQuad,v=>t.Position=t.Position with{Z=v}).Completed});
    }
    public void Bind(AdvPlayback player)
    {
        BindDirector(player);
        Camera.BindRotations(player);
        BindShakes(player);
        player.Register("PostEffect",PostEffect);
        player.Register("StageEnv",StageEnvironment);
        player.Register("RimLight",RimLight);
        player.Register("EyeBlink",EyeBlink);
        player.Register("Effect",Effect);
        foreach(string name in new[]{"Character","Costume","In","Out","Expression","Motion","Pause","Resume","Stage","Focus","Track","Pedestal","Zoom","Pan","PanV2","MoveToDirection","MoveToRight","MoveToLeft","MoveToUp","MoveToDown","MoveToForward","MoveToBack","Forward","Back","Look","LookTarget","Angle","MotionLoop","Brightness","DoF"})player.Register(name,Handle);
    }
    IEnumerable<AdvWait> Handle(AdvRow row,AdvPlayback player)
    {
        if(row.Command=="Character")yield break;
        if(row.Command=="Costume"){costumes[row.Text("TargetName")]=(int)row.Number("TargetAssetIndex");yield break;}
        var actor=Character(row.Text("TargetName"));
        if(row.Command=="Expression"){actor?.Expression(row.Text("ExpressionName")==""?actor.DefaultExpression:row.Text("ExpressionName"),row.Number("MotionFadeIn"));yield break;}
        if(row.Command=="Pause"||row.Command=="Resume"){if(actor is not null)actor.AnimationPaused=row.Command=="Pause";yield break;}
        if(row.Command=="Motion"){if(actor is not null)actor.QueueMotion(row.Text("MotionName"),row.Text("ExpressionName"),row.Number("MotionFadeIn"),row.Number("MotionWait"));yield break;}
        if(row.Command=="MotionLoop"){if(actor is null)throw new InvalidDataException("MotionLoop requires a loaded character");if(row.Text("Parameter1").Equals("stop",StringComparison.OrdinalIgnoreCase))actor.ParameterLoop.Stop(row.Number("MotionFadeIn"));else actor.ParameterLoop.Play(row.Text("MotionName"),row.Number("MotionFadeIn"));yield break;}
        if(row.Command is "Forward" or "Back"){
            int idx=((int)row.Number("PositionType")-1)/2;var order=new List<int>{4,0,3,1,2};order.Remove(idx);if(row.Command=="Forward")order.Add(idx);else order.Insert(0,idx);for(int i=0;i<order.Count;i++)if(order[i]>=0&&order[i]<5)priority[order[i]]=i;yield break;
        }
        yield return player.NoWait(row,Inner(row,player));
    }
    IEnumerable<AdvWait> Inner(AdvRow row,AdvPlayback p)
    {
        int pos=(int)row.Number("PositionType");float duration=p.CalcDuration(row.Number("Duration"));var ease=Ease(row);string name=row.Text("TargetName");
        switch(row.Command){
            case "DoF":{
                var wait=p.CommonDelay(p.CalcDuration(row.Number("DelaySeconds")));yield return wait;if(!wait.GetResult())yield break;float intensity=Param(row,1);
                if(pos==0)pos=positions.GetValueOrDefault(name);var tasks=new List<AdvWait>();blurRadius=BlurRadii[Math.Clamp(focusDistance,0,6)];var layers=row.Data["CanvasLayers"]?.AsArray();
                if(layers is not null&&layers.Any(n=>Actor.N(n)==0))tasks.Add(Tween("bgBlur",()=>backgroundBlur,Math.Clamp(intensity,0,1),duration,ease,v=>backgroundBlur=v));
                if(layers is null||layers.Count==0||layers.Any(n=>Actor.N(n)==2))if(pos is 1 or 3 or 5 or 7 or 9){int i=(pos-1)/2;tasks.Add(Tween("slotBlur"+i,()=>slotBlur[i],Math.Clamp(intensity,0,1),duration,ease,v=>slotBlur[i]=v));}
                yield return loop.All(tasks.ToArray());yield break;}
            case "Brightness":{
                float bright=Math.Clamp(Param(row,1),0,1);var actor=Character(name);var tasks=new List<AdvWait>();
                AdvWait CharacterBrightness(int position){if(position is not (1 or 3 or 5 or 7 or 9))return loop.Ready();int i=(position-1)/2;return Tween("bright"+i,()=>slotBrightness[i],bright,duration,AdvEase.OutQuad,v=>slotBrightness[i]=v);}
                AdvWait BackgroundBrightness()=>Tween("bgBright",()=>backgroundBrightness,bright,duration,AdvEase.OutQuad,v=>backgroundBrightness=v);
                if(actor is not null&&positions.TryGetValue(name,out int place))tasks.Add(CharacterBrightness(place));
                else if(actor is not null)tasks.Add(Tween("brightUnplaced"+name,()=>actor.Spec.Brightness,bright,duration,AdvEase.OutQuad,v=>actor.Spec.Brightness=v));
                else if(row.Data["CanvasLayers"] is JsonArray layers&&layers.Count>0){foreach(var l in layers){if(Actor.N(l)==0)tasks.Add(BackgroundBrightness());if(Actor.N(l)==2)tasks.Add(CharacterBrightness(pos));}}
                else tasks.Add(pos!=0?CharacterBrightness(pos):BackgroundBrightness());yield return loop.All(tasks.ToArray());yield break;}
            case "Stage":
                if(duration>0){pendingCapture=new(loop);yield return pendingCapture;captureActive=true;captureAlpha=1;}
                SetStage(row.Text("TargetAssetName"));
                if(duration>0){yield return Tween("capture",()=>1,0,duration,AdvEase.OutQuad,v=>captureAlpha=v);captureActive=false;}yield break;
            case "In":if(name==""||HasStageActor(pos))yield break;Show(name,pos,row.Text("MotionName"),row.Text("ExpressionName"),row.Number("MotionFadeIn"));if(Character(name) is Actor inActor)inActor.GameLighting=!row.Text("Parameter1").Equals("unlit",StringComparison.OrdinalIgnoreCase);foreach(var w in FadeSlot(pos,0,1,duration))yield return w;yield break;
            case "Out":if(pos==0)pos=positions.GetValueOrDefault(name);foreach(var w in FadeSlot(pos,1,0,duration>0?duration:.05f))yield return w;if(!p.Cancelled){var actor=Character(name);if(actor is not null){RemoveStageActor(actor);actor.Prefab.Root.Parent=null;}}yield break;
            case "MoveToDirection":yield return MoveSlot(pos,new(Param(row,1),Param(row,2),Param(row,3)),duration);yield break;
            case "MoveToRight":case "MoveToLeft":case "MoveToUp":case "MoveToDown":case "MoveToForward":case "MoveToBack":
                float offset=Param(row,1);var dir=row.Command switch{"MoveToRight"=>Vector3.UnitX,"MoveToLeft"=>-Vector3.UnitX,"MoveToUp"=>Vector3.UnitY,"MoveToDown"=>-Vector3.UnitY,"MoveToForward"=>-Vector3.UnitZ,_=>Vector3.UnitZ};yield return MoveSlot(pos,dir*offset,duration);yield break;
        }
        var delay=p.CommonDelay(p.CalcDuration(row.Number("DelaySeconds")));yield return delay;if(!delay.GetResult())yield break;
        float value=Param(row,1);
        switch(row.Command){
            case "Angle":case "Look":case "LookTarget":{
                var target=Character(name);if(target is null)yield break;
                if(row.Command=="Angle"){
                    target.OverrideAngle=true;yield return loop.All(new[]{Tween("angleX"+name,()=>target.AngleX,value,duration,AdvEase.InOutQuad,v=>target.AngleX=v),Tween("angleBody"+name,()=>target.BodyAngleX,Param(row,2),duration,AdvEase.InOutQuad,v=>target.BodyAngleX=v)});
                }else{
                    bool enabled=!row.Text("Parameter3").Equals("stop",StringComparison.OrdinalIgnoreCase);float x=value,y=Param(row,2);
                    if(enabled&&!target.LookEnabled){int xi=Array.IndexOf(target.Core.ParameterIds,"ParamEyeBallX"),yi=Array.IndexOf(target.Core.ParameterIds,"ParamEyeBallY");originalLook[target]=(xi<0?0:target.Values[xi],yi<0?0:target.Values[yi]);target.LookX=originalLook[target].X;target.LookY=originalLook[target].Y;}
                    if(!enabled)(x,y)=originalLook.GetValueOrDefault(target);
                    else if(row.Command=="LookTarget"){if(!shown.TryGetValue(pos,out var other))yield break;var delta=other.HeadPosition-target.HeadPosition;x=Math.Clamp(delta.X*.2f,-1,1);y=Math.Clamp(delta.Y,-1,1);}
                    target.LookEnabled=true;yield return loop.All(new[]{Tween("lookX"+name,()=>target.LookX,x,duration,AdvEase.InOutQuad,v=>target.LookX=v),Tween("lookY"+name,()=>target.LookY,y,duration,AdvEase.InOutQuad,v=>target.LookY=v)});if(!enabled)target.LookEnabled=false;
                }break;}
            case "Track":panBase=panBase with{X=value+focusPosition.X};yield return Camera.MoveX(panBase.X,duration,ease);break;
            case "Pedestal":panBase=panBase with{Y=value+focusPosition.Y};yield return Camera.MoveY(panBase.Y,duration,ease);break;
            case "Zoom":{
                float ratio=focusZoom*(value>0?value:1);var fds=FocusSettings();var fd=fds["_focusData"]!.AsArray().OfType<JsonNode>().MinBy(n=>Math.Abs(Actor.N(n["_fieldZoomRatio"])-ratio))!;focusDistance=(int)Actor.N(fd["_cameraDistance"]);
                var tasks=new List<AdvWait>{Camera.Zoom(ratio,duration,ease),Tween("blurRadius",()=>blurRadius,BlurRadii[Math.Clamp(focusDistance,0,6)],duration,ease,v=>blurRadius=v)};
                if(!row.Text("Parameter3").Equals("sharp",StringComparison.OrdinalIgnoreCase)&&Actor.B(fds["_backgroundBlurEnabled"]))tasks.Add(Tween("bgBlur",()=>backgroundBlur,Math.Clamp(Actor.N(fd["_backgroundBlurIntensity"])+Param(row,3),0,1),duration,ease,v=>backgroundBlur=v));yield return loop.All(tasks.ToArray());break;}
            case "Pan":yield return loop.All(new[]{Camera.RotateY(value,duration,ease),Rotate("fieldY",field,value,duration,ease),Rotate("backgroundY",background,value,duration,ease)});break;
            case "PanV2":
                float dist=stage is null?0:Math.Abs(Actor.N(stage["_characterFieldPosition"]?["z"])-focusPosition.Z),slide=row.Text("Parameter3").Trim()==""?Actor.N(settings["_defaultPanV2FocusSlideRate"]):Param(row,3);double rad=value*.017453292;
                yield return loop.All(new[]{Camera.RotateY(value,duration,ease),Camera.MoveX(panBase.X-(float)(Math.Sin(rad)*dist*(1-slide)),duration,ease),Camera.MoveZ(panBase.Z+(float)((1-Math.Cos(rad))*dist*(1-slide)),duration,ease),RotateSlots(value,duration,ease)});break;
            case "Focus":{
                var fds=FocusSettings();
                var fd=fds["_focusData"]!.AsArray().First(v=>Actor.N(v!["_cameraDistance"])==row.Number("CameraDistance"))!;ease=Ease(row,(AdvEase)(int)Actor.N(fds["_focusEase"]));
                var pt=FocusPoint(pos);float y=Actor.N(fd["_fieldZoomOffsetY"]);var ch=Character(name);
                if(ch is not null&&positions.TryGetValue(name,out int targetPosition)){pos=targetPosition;y=ch.HeadPosition.Y-Actor.N(fd["_characterHeadFocusOffsetY"]);}
                focusPosition=panBase=new(pt.X,y,0);focusZoom=Actor.N(fd["_fieldZoomRatio"]);
                focusDistance=(int)row.Number("CameraDistance");bool sharp=row.Text("Parameter1").Equals("sharp",StringComparison.OrdinalIgnoreCase);
                var tasks=new List<AdvWait>{Camera.MoveX(pt.X,duration,ease),Camera.MoveY(y,duration,ease),Camera.MoveZ(0,duration,ease),Camera.RotateY(0,duration,ease),Camera.Zoom(focusZoom,duration,ease),RotateSlots(0,duration,ease),Tween("blurRadius",()=>blurRadius,BlurRadii[Math.Clamp(focusDistance,0,6)],duration,ease,v=>blurRadius=v)};
                if(Actor.B(fds["_backgroundBlurEnabled"]))tasks.Add(Tween("bgBlur",()=>backgroundBlur,sharp?0:Actor.N(fd["_backgroundBlurIntensity"]),duration,ease,v=>backgroundBlur=v));
                if(Actor.B(fds["_characterBlurEnabled"]))foreach(int place in positions.Values.Distinct().Where(n=>n is 1 or 3 or 5 or 7 or 9)){int i=(place-1)/2;tasks.Add(Tween("slotBlur"+i,()=>slotBlur[i],sharp||place==pos||!shown.ContainsKey(pos)?0:Actor.N(fd["_characterBlurIntensity"]),duration,ease,v=>slotBlur[i]=v));}
                yield return loop.All(tasks.ToArray());break;}
        }
    }
    JsonNode FocusSettings(){var map=settings["_focusDataSettingsMap"]!["_list"]!.AsArray();string key=stage is null?"":Archive.S(stage,"_usingFocusDataSettingsKey");return(map.FirstOrDefault(v=>Archive.S(v!,"Key")==key)??map.First(v=>Archive.S(v!,"Key")==Archive.S(settings,"_defaultFocusDataSettingsKey")))!["Value"]!;}
    IEnumerable<AdvWait> FadeSlot(int pos,float from,float to,float seconds)
    {
        if(pos is not (1 or 3 or 5 or 7 or 9))yield break;int i=(pos-1)/2;string key="alpha"+i;if(running.Remove(key,out var old))old.Kill();
        int serial=++fadeSerial[i];if(seconds<=0){slotAlpha[i]=to;yield break;}slotAlpha[i]=from;yield return loop.DelayFrames(3);if(fadeSerial[i]!=serial)yield break;
        yield return Tween(key,()=>from,to,seconds,AdvEase.OutQuad,v=>slotAlpha[i]=v);if(fadeSerial[i]==serial)slotAlpha[i]=to;
    }
    readonly int[] fadeSerial=new int[5];
    public void AdvanceRenderState()
    {
        post.AdvanceCamera(Camera,loop.Frame);
        if(pendingCapture is null)return;
        if(!TimingOnly){Draw();return;}
        // A native Stage fade waits for capture, then the next Update. Timing
        // needs that exact wait boundary, not the captured GPU pixels. Resolve
        // the same waiter at the same phase without drawing invisible models.
        var wait=pendingCapture;pendingCapture=null;CaptureWaitCount++;
        loop.YieldUpdate().OnCompleted(()=>wait.Resolve(true));
    }
    public Action? DrawForeground{get;set;}
    public Func<AdvRenderItem?>? ForegroundItem{get;set;}
    public void Draw()
    {
        gl.Begin(clearColor);DrawDirectorBackground();var vp=Camera.ViewProjection(gl.Width/(float)gl.Height);var globals=LightingGlobals(vp);
        var ordered=shown.OrderByDescending(p=>p.Value.Prefab.Root.WorldPosition.Z).ThenBy(p=>priority[(p.Key-1)/2]).ToArray();
        static bool Approx(float a,float b)=>Math.Abs(a-b)<Math.Max(1e-6f*Math.Max(Math.Abs(a),Math.Abs(b)),float.Epsilon*8);
        bool Same(int a,int b)=>Approx(slotAlpha[a],slotAlpha[b])&&Approx(slotBrightness[a],slotBrightness[b])&&Approx(slotBlur[a],slotBlur[b]);
        bool offscreen=backgroundBlur>0||captureActive||ordered.Any(p=>slotAlpha[(p.Key-1)/2]<1||slotBrightness[(p.Key-1)/2]<1||slotBlur[(p.Key-1)/2]>0);
        foreach(var effect in AllEffects().Where(e=>e.PositionType is 1 or 3 or 5 or 7 or 9)){
            int index=Array.FindIndex(ordered,p=>p.Key==effect.PositionType);
            if(index<0)effect.RestoreOrder();else effect.SetRelativeOrder(index*1000,index*1000+ordered[index].Value.Core.DrawableCount,index*1000+999);
        }
        var backgroundItems=BackgroundItems(globals).Concat(EffectItems(globals,(e,r)=>r.Layer==12)).ToArray();
        if(pendingCapture is not null){AdvRenderItem.DrawAll(backgroundItems);captureTexture=gl.Capture(captureTexture);var wait=pendingCapture;pendingCapture=null;CaptureWaitCount++;CaptureDrawCount++;loop.YieldUpdate().OnCompleted(()=>wait.Resolve(true));gl.Begin(clearColor);}
        var foreground=ForegroundItem?.Invoke();IEnumerable<AdvRenderItem> foregroundItems=foreground is null?[]:[foreground];
        if(!offscreen){
            var items=backgroundItems.Concat(ordered.SelectMany((p,i)=>p.Value.GameDrawItems(globals,i*1000))).Concat(EffectItems(globals,(e,r)=>r.Layer is >=6 and <=11)).Concat(foregroundItems);AdvRenderItem.DrawAll(items);
        }else{
        AdvRenderItem.DrawAll(backgroundItems);gl.Blur(shaders,backgroundBlur,blurRadius);if(captureActive)gl.Composite(captureTexture,captureAlpha);
        for(int first=0;first<ordered.Length;){
            int slot=(ordered[first].Key-1)/2,last=first+1;while(last<ordered.Length&&Same(slot,(ordered[last].Key-1)/2))last++;
            float alpha=slotAlpha[slot],bright=slotBrightness[slot],blur=slotBlur[slot];bool separate=alpha<1||bright<1||blur>0;
            if(separate)gl.BeginLayer();
            var members=ordered[first..last];var layers=members.Select(p=>6+(p.Key-1)/2).ToHashSet();
            AdvRenderItem.DrawAll(members.SelectMany((p,i)=>p.Value.GameDrawItems(globals,(first+i)*1000)).Concat(EffectItems(globals,(e,r)=>layers.Contains(r.Layer))));
            if(separate){gl.Blur(shaders,blur,blurRadius);gl.EndLayer(alpha,bright);}first=last;
        }
        AdvRenderItem.DrawAll(EffectItems(globals,(e,r)=>r.Layer==11).Concat(foregroundItems));
        }
        DrawDirectorActors(globals);
        DrawForeground?.Invoke();
        if(!DirectorTransparent)post.Render(VolumeStack(),Camera,loop.Frame,new[]{1f,.85f,.65f,.45f,.30f,.15f,0f}[Math.Clamp(focusDistance,0,6)]);
    }
    public void Dispose(){foreach(var actor in actors.Values)actor.Dispose();effectMaterials?.Dispose();shaders.Dispose();}
}
