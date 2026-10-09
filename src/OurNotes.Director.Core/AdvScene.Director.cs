using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    sealed class DirectorActor
    {
        public string Space="screen";public Vector3 Position=new(.5f,.5f,0),Rotation,Scale=Vector3.One;
        public float Height=.95f;public int Order;public bool Visible=true;
    }
    readonly Dictionary<Actor,DirectorActor> directorActors=[];
    readonly CameraSpec directorFraming=new();readonly GradeSpec directorGrade=new();
    uint directorBackground;float directorBackgroundAspect;
    public string ProjectDirectory{get;set;}=Environment.CurrentDirectory;
    public void ApplyDirectorGrade()=>gl.GradeScene(directorGrade);
    public IEnumerable<Actor> VisibleActors=>shown.Values.Concat(directorActors.Where(p=>p.Value.Visible).Select(p=>p.Key)).Distinct();
    public object[] DirectorState()=>directorActors.Select(p=>(object)JsonOutput.Object(("name", p.Key.Spec.Name), ("model", p.Key.Spec.Model), ("space", p.Value.Space), ("visible", p.Value.Visible), ("position", new[]{p.Value.Position.X,p.Value.Position.Y,p.Value.Position.Z}), ("height", p.Value.Height), ("parameters", p.Key.DirectorParameters.ToDictionary(v=>v.Key,v=>JsonOutput.Object(("requested", v.Value), ("evaluated", p.Key.ParameterValue(v.Key))))))).ToArray();
    public bool DirectorTransparent=>clearColor[3]<1;
    void BindDirector(AdvPlayback player)
    {
        foreach(string command in AdvProgram.DirectorCommands)player.Register(command,(r,p)=>[p.NoWait(r,Director(r,p))]);
    }
    IEnumerable<AdvWait> Director(AdvRow row,AdvPlayback player)
    {
        string target=row.Text("TargetName"),prefix="director/"+row.Command+"/"+target+"/";
        float duration=player.CalcDuration(row.Number("Duration"));
        var ease=row.Text("Ease")==""?AdvEase.Linear:Enum.Parse<AdvEase>(row.Text("Ease"));
        var waits=new List<AdvWait>();
        void Scalar(string key,Func<float> get,Action<float> set,float value)=>waits.Add(Tween(prefix+key,get,value,duration,ease,set));
        void Vector(string key,Func<Vector3> get,Action<Vector3> set,JsonNode node,int length=3){
            var array=node.AsArray();if(array.Count!=length)throw new ArgumentException(key+" needs "+length+" components");
            for(int k=0;k<length;k++){int axis=k;float value=Actor.N(array[k]);Scalar(key+new[]{"X","Y","Z"}[axis],()=>get()[axis],v=>{var vector=get();vector[axis]=v;set(vector);},value);}
        }
        if(row.Command=="Director.Parameter"){
            var actor=Character(target)??throw new ArgumentException("Unknown actor: "+target);string parameter=row.Text("Parameter");
            float current=actor.ParameterValue(parameter);string key=prefix+parameter;
            if(row.Text("Operation")=="release"){
                if(running.Remove(key,out var active))active.Kill();actor.DirectorParameters.Remove(parameter);yield break;
            }
            if(row.Text("Operation") is not ("" or "set"))throw new ArgumentException("Parameter operation must be set or release");
            if(row.Data["Value"] is null)throw new ArgumentException("Director.Parameter requires Value");
            actor.DirectorParameters.TryAdd(parameter,current);
            Scalar(parameter,()=>actor.DirectorParameters[parameter],v=>actor.DirectorParameters[parameter]=v,row.Number("Value"));
        }else{
            var values=row.Data["Values"] as JsonObject??throw new ArgumentException(row.Command+" requires Values");
            DirectorActor? state=null;Actor? actor=null;
            if(row.Command=="Director.Actor"){
                actor=Character(target)??throw new ArgumentException("Unknown actor: "+target);
                if(!directorActors.TryGetValue(actor,out state)){
                    bool wasShown=shown.Values.Contains(actor);
                    directorActors[actor]=state=new();
                    if(!wasShown){actor.ResetGameAnimation();actor.Motion(actor.DefaultMotion,0);actor.Expression(actor.DefaultExpression,0);}
                    foreach(int slot in shown.Where(p=>p.Value==actor).Select(p=>p.Key).ToArray())shown.Remove(slot);
                    positions.Remove(target);actor.Prefab.Root.Parent=null;
                }
                if(row.Text("Space") is {Length:>0} space){if(space is not ("world" or "screen"))throw new ArgumentException("Actor Space must be world or screen");state.Space=space;}
            }
            foreach(var (key,value) in values){
                if(value is null)throw new ArgumentException("Null director value: "+key);
                if(state is not null&&actor is not null){
                    switch(key){
                        case "position":Vector(key,()=>state.Position,v=>state.Position=v,value,state.Space=="screen"?2:3);break;
                        case "rotation":Vector(key,()=>state.Rotation,v=>state.Rotation=v,value);break;
                        case "scale":Vector(key,()=>state.Scale,v=>state.Scale=v,value);break;
                        case "height":Scalar(key,()=>state.Height,v=>state.Height=v,Actor.N(value));break;
                        case "positionX":Scalar(key,()=>state.Position.X,v=>state.Position=state.Position with{X=v},Actor.N(value));break;
                        case "positionY":Scalar(key,()=>state.Position.Y,v=>state.Position=state.Position with{Y=v},Actor.N(value));break;
                        case "rotationZ":Scalar(key,()=>state.Rotation.Z,v=>state.Rotation=state.Rotation with{Z=v},Actor.N(value));break;
                        case "opacity":Scalar(key,()=>actor.Spec.Opacity,v=>actor.Spec.Opacity=v,Actor.N(value));break;
                        case "brightness":Scalar(key,()=>actor.Spec.Brightness,v=>actor.Spec.Brightness=v,Actor.N(value));break;
                        case "tint":if(value.AsArray().Count!=4)throw new ArgumentException("Tint needs RGBA");for(int k=0;k<4;k++){int channel=k;Scalar(key+k,()=>actor.Spec.Tint[channel],v=>actor.Spec.Tint[channel]=v,Actor.N(value[k]));}break;
                        case "physics":actor.Spec.Physics=value.GetValue<bool>();break;
                        case "breath":actor.Spec.Breath=value.GetValue<bool>();break;
                        case "blink":actor.SetEyeBlinkStopped(!value.GetValue<bool>(),0);break;
                        case "speed":Scalar(key,()=>actor.Spec.Speed,v=>actor.Spec.Speed=v,Actor.N(value));break;
                        case "loop":actor.Spec.Loop=value.GetValue<bool>();break;
                        case "animation":if(value.ToString() is not ("game" or "director"))throw new ArgumentException("Unknown animation mode");actor.DirectorAnimation(value.ToString()=="director");break;
                        case "warmupFrames":actor.WarmupDirector(value.GetValue<int>());break;
                        case "visible":state.Visible=value.GetValue<bool>();break;
                        case "order":state.Order=value.GetValue<int>();break;
                        default:throw new NotSupportedException("Director.Actor value: "+key);
                    }
                }else if(row.Command=="Director.Camera"){
                    switch(key){
                        case "position":if(value.AsArray().Count!=3)throw new ArgumentException("Camera position needs XYZ");waits.Add(Camera.MoveX(Actor.N(value[0]),duration,ease));waits.Add(Camera.MoveY(Actor.N(value[1]),duration,ease));waits.Add(Camera.MoveZ(Actor.N(value[2]),duration,ease));break;
                        case "rotation":if(value.AsArray().Count!=3)throw new ArgumentException("Camera rotation needs XYZ");waits.Add(Camera.RotateX(Actor.N(value[0]),duration,ease));waits.Add(Camera.RotateY(Actor.N(value[1]),duration,ease));waits.Add(Camera.RotateZ(Actor.N(value[2]),duration,ease));break;
                        case "fieldOfView":waits.Add(Camera.Zoom(Camera.FieldOfView/Actor.N(value),duration,ease));break;
                        case "framing":foreach(var (field,entry) in value.AsObject())switch(field){
                            case "x":Scalar(key+field,()=>directorFraming.X,v=>directorFraming.X=v,Actor.N(entry));break;
                            case "y":Scalar(key+field,()=>directorFraming.Y,v=>directorFraming.Y=v,Actor.N(entry));break;
                            case "zoom":Scalar(key+field,()=>directorFraming.Zoom,v=>directorFraming.Zoom=v,Actor.N(entry));break;
                            case "roll":Scalar(key+field,()=>directorFraming.Roll,v=>directorFraming.Roll=v,Actor.N(entry));break;
                            default:throw new ArgumentException("Unknown framing field: "+field);
                        }break;
                        default:throw new NotSupportedException("Director.Camera value: "+key);
                    }
                }else if(row.Command=="Director.Scene"){
                    switch(key){
                        case "background":if(value.AsArray().Count!=4)throw new ArgumentException("Background requires RGBA");for(int k=0;k<4;k++){int axis=k;Scalar(key+k,()=>clearColor[axis],v=>clearColor[axis]=v,Actor.N(value[k]));}break;
                        case "stageVisible":if(value.GetValue<bool>())throw new NotSupportedException("Use Stage to restore a stage");SetStage("");break;
                        case "backgroundImage":{
                            byte[] bytes;
                            if(value["path"] is {} path)bytes=File.ReadAllBytes(Path.GetFullPath(path.ToString(),ProjectDirectory));
                            else if(value["adv"] is {} adv&&value["logicalPath"] is {} logical)bytes=archive.Logical(site,archive.Manifest(site,"stories",adv.ToString()),logical.ToString());
                            else throw new ArgumentException("backgroundImage needs path or adv/logicalPath");
                            var image=Png.Decode(bytes);directorBackground=gl.Upload(image);directorBackgroundAspect=image.Width/(float)image.Height;SetStage("");break;
                        }
                        case "grade":foreach(var (field,entry) in value.AsObject())switch(field){
                            case "exposure":Scalar(key+field,()=>directorGrade.Exposure,v=>directorGrade.Exposure=v,Actor.N(entry));break;
                            case "contrast":Scalar(key+field,()=>directorGrade.Contrast,v=>directorGrade.Contrast=v,Actor.N(entry));break;
                            case "saturation":Scalar(key+field,()=>directorGrade.Saturation,v=>directorGrade.Saturation=v,Actor.N(entry));break;
                            case "vignette":Scalar(key+field,()=>directorGrade.Vignette,v=>directorGrade.Vignette=v,Actor.N(entry));break;
                            default:throw new ArgumentException("Unknown grade field: "+field);
                        }break;
                        default:throw new NotSupportedException("Director.Scene value: "+key);
                    }
                }
            }
        }
        yield return loop.All(waits.ToArray());
    }
    float[] DirectorProjection()
    {
        float aspect=gl.Width/(float)gl.Height;var projection=AdvMatrix.Identity();projection[0]=directorFraming.Zoom/aspect;projection[5]=directorFraming.Zoom;projection[10]=-.001f;
        return AdvMatrix.Multiply(projection,AdvMatrix.Multiply(AdvMatrix.Trs(Vector3.Zero,new(0,0,directorFraming.Roll),Vector3.One),AdvMatrix.Trs(new(-directorFraming.X*2*aspect,directorFraming.Y*2,0),Vector3.Zero,Vector3.One)));
    }
    void DrawDirectorBackground()
    {
        if(directorBackground==0)return;
        float aspect=gl.Width/(float)gl.Height,sx=Math.Max(1,directorBackgroundAspect/aspect),sy=Math.Max(1,aspect/directorBackgroundAspect);var projection=DirectorProjection();
        float[] xy=[-sx*aspect,-sy,sx*aspect,-sy,sx*aspect,sy,-sx*aspect,sy];
        for(int i=0;i<4;i++){var p=AdvMatrix.Transform(projection,new(xy[i*2],xy[i*2+1],0,1));xy[i*2]=p.X;xy[i*2+1]=p.Y;}
        gl.Draw(directorBackground,xy,[0,1,1,1,1,0,0,0],[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],[1,1,1,1],0,false,false);
    }
    void DrawDirectorActors(IReadOnlyDictionary<string,float[]> globals)
    {
        foreach(var (actor,state) in directorActors.OrderBy(p=>p.Value.Order)){
            if(!state.Visible)continue;
            actor.SetDirectorTint(actor.Spec.Tint,actor.Spec.Brightness);
            var root=actor.Prefab.Root;root.SetEuler(state.Rotation);
            if(state.Space=="screen"){
                float aspect=gl.Width/(float)gl.Height;
                root.Position=new((state.Position.X-.5f)*2*aspect,(.5f-state.Position.Y)*2,0);
                root.Scale=state.Scale*(state.Height*2/actor.CanvasHeight);
                var projection=DirectorProjection();
                var screenGlobals=new Dictionary<string,float[]>(globals){["unity_MatrixVP"]=projection,["unity_MatrixV"]=AdvMatrix.Identity(),["unity_MatrixInvV"]=AdvMatrix.Identity(),["glstate_matrix_projection"]=projection,["_WorldSpaceCameraPos"]=[0,0,-10]};
                actor.GameLighting=false;actor.DrawGame(screenGlobals);
            }else{root.Position=state.Position;root.Scale=state.Scale;actor.GameLighting=true;actor.DrawGame(globals);}
        }
    }
}
