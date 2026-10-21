using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Shuriken module semantics from the open-source reference. Random particles use a separate deterministic stream.
public sealed partial class AdvParticles
{
    public sealed class Particle
    {
        public Vector3 Position,Velocity,Animated,Size,Rotation,Noise;
        public Vector3 Flip=Vector3.One;public Vector3[]? Alignment;
        public float[] Stable=new float[4];
        internal SubBirthState?[] SubBirth=[];
        public float Age,Life,Spin=1;public float[] Color=[],Random=[];public float UvRandom,UvStart;
    }
    public JsonNode Data{get;}
    readonly AdvRandom shared;AdvRandom random;
    readonly AdvParticleCurveCache? curveCache;bool curveScope;
    readonly List<Particle> particles=[];
    public IReadOnlyList<Particle> Particles=>particles;
    public float Time{get;private set;}
    public bool Playing{get;private set;}
    public bool Emitting{get;private set;}
    public Func<Vector3,Vector3> WorldToLocal{get;set;}=v=>v;
    public Func<Vector3,Vector3> LocalToWorld{get;set;}=v=>v;
    public Func<Vector3,Vector3> ShapeToWorld{get;set;}=v=>v;
    public Vector3 EmitterPosition{get;set;}
    public Vector3 RendererFlip{get;set;}
    Vector3 previousEmitter;float stepDelta;
    AdvParticleMeshShape? meshShape;
    bool WorldSpace=>N(Data,"moveWithTransform")==1;
    Vector3 ToSimulation(Vector3 v,bool world)=>WorldSpace==world?v:WorldSpace?LocalToWorld(v):WorldToLocal(v);
    Vector3 FromSimulation(Vector3 v,bool world)=>WorldSpace==world?v:WorldSpace?WorldToLocal(v):LocalToWorld(v);
    public Vector3 ShapeScale{get;set;}=Vector3.One;
    public void SetTransform(AdvTransform transform)
    {
        var matrix=transform.WorldMatrix();var q=transform.Rotation;for(var parent=transform.Parent;parent is not null;parent=parent.Parent)q=parent.Rotation*q;
        var rotation=new AdvTransform{Rotation=q}.LocalMatrix();var x=new Vector3(rotation[0],rotation[1],rotation[2]);var y=new Vector3(rotation[4],rotation[5],rotation[6]);var z=new Vector3(rotation[8],rotation[9],rotation[10]);
        var lossy=new Vector3(new Vector3(matrix[0],matrix[1],matrix[2]).Length(),new Vector3(matrix[4],matrix[5],matrix[6]).Length(),new Vector3(matrix[8],matrix[9],matrix[10]).Length());
        int mode=(int)N(Data,"scalingMode");var scale=mode==1?transform.Scale:mode==2?Vector3.One:lossy;
        static float Divide(float v,float s)=>Math.Abs(s)>1e-12f?v/s:0;
        ShapeScale=mode==2?lossy:Vector3.One;
        LocalToWorld=v=>x*(v.X*scale.X)+y*(v.Y*scale.Y)+z*(v.Z*scale.Z);
        WorldToLocal=v=>new(Divide(Vector3.Dot(v,x),scale.X),Divide(Vector3.Dot(v,y),scale.Y),Divide(Vector3.Dot(v,z),scale.Z));
        // Reassign every step: a canvas host may rescale the previous value.
        EmitterPosition=new(matrix[12],matrix[13],matrix[14]);
        ShapeToWorld=v=>{if(mode!=0)return LocalToWorld(v);var p=AdvMatrix.Transform(matrix,new(v,0));return new(p.X,p.Y,p.Z);};
    }
    public AdvAnimator.Access? Bind(string attribute)
    {
        string[] parts=attribute.Split('.');JsonNode? parent=Data;foreach(string key in parts[..^1])parent=parent?[key];if(parent is not JsonObject obj||!obj.ContainsKey(parts[^1]))return null;
        return new(()=>Actor.N(obj[parts[^1]]),v=>obj[parts[^1]]=v);
    }
    float delay,noiseOffset;double accumulator,distanceAccumulator;int[] bursts=[];
    JsonNode Initial=>Data["InitialModule"]!;
    float Duration=>N(Data,"lengthInSec");
    static float N(JsonNode n,string key)=>Actor.N(n[key]);
    float C(JsonNode? n,float t,float r=1)=>curveScope?curveCache!.Value(n,t,r):AdvParticleCurve.Value(n,t,r);
    static bool Enabled(JsonNode? n)=>Actor.B(n?["enabled"]);
    static Vector3 Normalize(Vector3 v)=>v.LengthSquared()>1e-24f?Vector3.Normalize(v):Vector3.Zero;
    public AdvParticles(JsonNode data,AdvRandom shared):this(data,shared,false){}
    internal AdvParticles(JsonNode data,AdvRandom shared,bool cacheStepCurves)
    {
        Data=data.DeepClone();this.shared=shared;random=Actor.B(Data["autoRandomSeed"])?shared:new((uint)N(Data,"randomSeed"));
        if(cacheStepCurves)curveCache=new();
    }
    public void Play()
    {
        if(Emitting)return;
        Start(false);
        if(Actor.B(Data["prewarm"])&&Actor.B(Data["looping"]))for(int i=0;i<60;i++)Step(Duration/60);
    }
    public void Stop(bool immediate=false)
    {
        Emitting=false;if(immediate){particles.Clear();pendingBirths.Clear();}Playing=particles.Count>0;
    }
    public void Start(bool clear=true)
    {
        CheckSubEmitters();
        foreach(string name in new[]{"InheritVelocityModule","LifetimeByEmitterSpeedModule","ExternalForcesModule","SizeBySpeedModule","RotationBySpeedModule","ColorBySpeedModule","CollisionModule","TriggerModule","LightsModule","TrailModule"})
            if(Enabled(Data[name]))throw new NotSupportedException("Particle module not connected: "+name);
        if(N(Data,"moveWithTransform") is not (0 or 1))throw new NotSupportedException("Particle custom simulation space");
        if(N(Data,"ringBufferMode")!=0)throw new NotSupportedException("Particle ring buffer not supported by reference");
        random=Actor.B(Data["autoRandomSeed"])?shared:new((uint)N(Data,"randomSeed"));if(clear){particles.Clear();noiseOffset=0;}Time=0;accumulator=distanceAccumulator=0;delay=C(Data["startDelay"],0,random.Value());
        if(clear){pendingBirths.Clear();foreach(var sub in subEmitters){sub.System!.particles.Clear();sub.System.pendingBirths.Clear();}}
        bursts=new int[Data["EmissionModule"]?["m_Bursts"]?.AsArray().Count??0];Playing=Emitting=true;previousEmitter=EmitterPosition;
    }
    public void Clear(){particles.Clear();pendingBirths.Clear();Playing=Emitting=false;}
    public void Deactivate(){Clear();Time=0;delay=0;}
    public void Simulate(float dt)
    {
        if(!Playing||MainEmitter is not null)return;float total=dt*N(Data,"simulationSpeed"),h=Duration/60;int steps=h>0&&total>h?(int)Math.Ceiling(total/h):1;
        if(steps>100000)throw new ArgumentException("Particle simulation step too large");for(int i=0;i<steps;i++)Step(total/steps);
    }
    void Step(float dt)
    {
        stepDelta=dt;curveScope=curveCache is not null;
        try{StepCore(dt);foreach(var birth in pendingBirths)Spawn(birth.Age,1,birth.Position,birth.NormalizedTime);pendingBirths.Clear();foreach(var sub in subEmitters.Select(e=>e.System).Distinct())if(sub!.ActiveInHierarchy())sub.Step(dt);}
        finally{previousEmitter=EmitterPosition;curveScope=false;curveCache?.Clear();}
    }
    void StepCore(float dt)
    {
        if(Enabled(Data["NoiseModule"]))noiseOffset+=C(Data["NoiseModule"]!["scrollSpeed"],Duration>0?Time/Duration:0)*dt;
        for(int i=0;i<particles.Count;)if(Update(particles[i],dt)){EmitSubBirths(particles[i],dt);i++;}else{particles[i]=particles[^1];particles.RemoveAt(particles.Count-1);}
        if(MainEmitter is not null)return;
        if(!Emitting){Playing=particles.Count>0;return;}
        double budget=dt,start=0;if(delay>0){double d=Math.Min(delay,budget);delay=(float)(delay-d);budget-=d;start=d;if(budget<=0)return;}
        double t=Time,used=0;var emission=Data["EmissionModule"]!;bool looping=Actor.B(Data["looping"]);
        for(int guard=0;budget-used>1e-9&&guard<10000;guard++){
            if(Duration<=0||!looping&&t>=Duration)break;double segment=Math.Min(budget-used,Duration-t),end=t+segment,segmentStart=start+used;
            if(segment>0&&Enabled(emission)){
                var list=emission["m_Bursts"]!.AsArray();for(int b=0;b<Math.Min(list.Count,(int)N(emission,"m_BurstCount"));b++){
                    var burst=list[b]!;float interval=N(burst,"repeatInterval");int cycles=(int)N(burst,"cycleCount");if(cycles<=0)cycles=interval>0?int.MaxValue:1;
                    for(int n=0;n<100000&&bursts[b]<cycles;n++){float at=N(burst,"time")+bursts[b]*interval;if(at>=end)break;bursts[b]++;if(at<t||N(burst,"probability")<1&&random.Value()>=N(burst,"probability"))continue;
                        int count=(int)MathF.Floor(C(burst["countCurve"],at/Duration,random.Value())+.5f);for(int i=0;i<count;i++)Spawn((float)(dt-(segmentStart+at-t)));}
                }
                float rate=C(emission["rateOverTime"],(float)((t+end)*.5/Duration),random.Value());if(rate>0){double total=accumulator+rate*segment;int count=(int)Math.Floor(total);for(int i=1;i<=count;i++)Spawn((float)(dt-(segmentStart+(i-accumulator)/rate)));accumulator=total-count;}
            }
            used+=segment;t=end;if(t>=Duration){if(looping){t=0;Array.Clear(bursts);}else break;}
        }
        float previousTime=Time;Time=(float)t;
        float moved=Vector3.Distance(previousEmitter,EmitterPosition);
        if(Enabled(emission)&&moved>0){
            float rate=C(emission["rateOverDistance"],Duration>0?previousTime/Duration:0,random.Value());
            if(rate>0){double total=distanceAccumulator+rate*(double)moved;int count=(int)Math.Floor(total);for(int i=1;i<=count;i++){
                float fraction=(float)Math.Clamp((i-distanceAccumulator)/(rate*(double)moved),0,1);Spawn(dt*(1-fraction),fraction);
            }distanceAccumulator=total-count;}
        }
        if(!looping&&Time>=Duration){Emitting=false;if(particles.Count==0)Playing=false;}
    }
    Vector3 Sphere(){float z=random.Value()*2-1,a=random.Value()*MathF.Tau,r=MathF.Sqrt(Math.Max(0,1-z*z));return new(r*MathF.Cos(a),r*MathF.Sin(a),z);}
    (Vector3 Position,Vector3 Direction) Shape()
    {
        var s=Data["ShapeModule"]!;if(!Enabled(s))return(Vector3.Zero,Vector3.UnitZ);Vector3 p,d;float radius=Actor.N(s["radius"]?["value"]),thickness=N(s,"radiusThickness");int type=(int)N(s,"type");
        switch(type){
            case 6:(p,d)=(meshShape??=new(s)).Sample(random);break;
            case 5:case 15:case 16:
                p=new(random.Value()-.5f,random.Value()-.5f,random.Value()-.5f);int free=Math.Min(2,(int)(random.Value()*3));var box=AdvPrefab.Vector(s["boxThickness"]);
                void Snap(int axis){float sign=random.Value()<.5f?-1:1,v=sign*(.5f-.5f*(axis==0?box.X:axis==1?box.Y:box.Z)*random.Value());p=axis switch{0=>p with{X=v},1=>p with{Y=v},_=>p with{Z=v}};}
                if(type==15)Snap(free);else if(type==16){Snap((free+1)%3);Snap((free+2)%3);}d=Vector3.UnitZ;break;
            case 12:p=new(radius*(2*random.Value()-1),0,0);d=Vector3.UnitY;break;
            case 18:p=new(random.Value()-.5f,random.Value()-.5f,0);d=Vector3.UnitZ;break;
            case 0:case 1:case 2:case 3:
                d=Sphere();if(type>=2)d.Z=Math.Abs(d.Z);float inner=type is 1 or 3?1:1-thickness;p=d*(radius*MathF.Cbrt(inner*inner*inner+(1-inner*inner*inner)*random.Value()));break;
            case 4:case 10:case 11:
                float a=random.Value()*Actor.N(s["arc"]?["value"])*MathF.PI/180,r0=type==11?1:1-thickness,r=radius*MathF.Sqrt(r0*r0+(1-r0*r0)*random.Value());p=new(MathF.Cos(a)*r,MathF.Sin(a)*r,0);
                float k=radius>0?r/radius:0,tn=MathF.Tan(N(s,"angle")*MathF.PI/180);d=type==4?Normalize(new(MathF.Cos(a)*k*tn,MathF.Sin(a)*k*tn,1)):Normalize(p==Vector3.Zero?Vector3.UnitX:p);break;
            default:throw new NotSupportedException("Particle shape not connected: "+type);
        }
        var rotation=AdvPrefab.Vector(s["m_Rotation"])*MathF.PI/180;var q=Quaternion.CreateFromYawPitchRoll(rotation.Y,rotation.X,rotation.Z);
        p=Vector3.Transform(p*AdvPrefab.Vector(s["m_Scale"],Vector3.One),q)+AdvPrefab.Vector(s["m_Position"]);d=Normalize(Vector3.Transform(d,q));
        if(N(s,"randomDirectionAmount")>0)d=Normalize(Vector3.Lerp(d,Sphere(),N(s,"randomDirectionAmount")));
        if(N(s,"sphericalDirectionAmount")>0)d=Normalize(Vector3.Lerp(d,Normalize(p),N(s,"sphericalDirectionAmount")));
        if(N(s,"randomPositionAmount")>0)p+=Sphere()*MathF.Cbrt(random.Value())*N(s,"randomPositionAmount");return(p,d);
    }
    void Spawn(float age,float? fraction=null,Vector3? origin=null,float? normalizedTime=null)
    {
        var i=Initial;if(particles.Count>=(int)N(i,"maxNumParticles"))return;float t=normalizedTime??(Duration>0?Math.Min(Time/Duration,1):0),life=C(i["startLifetime"],t,random.Value());if(life<=0)return;
        float speed=C(i["startSpeed"],t,random.Value()),sx=C(i["startSize"],t,random.Value());var size=Actor.B(i["size3D"])?new Vector3(sx,C(i["startSizeY"],t,random.Value()),C(i["startSizeZ"],t,random.Value())):new Vector3(sx);
        var rot=Actor.B(i["rotation3D"])?new Vector3(C(i["startRotationX"],t,random.Value()),C(i["startRotationY"],t,random.Value()),C(i["startRotation"],t,random.Value())):new(0,0,C(i["startRotation"],t,random.Value()));
        float sign=N(i,"randomizeRotationDirection")>0&&random.Value()<N(i,"randomizeRotationDirection")?-1:1;
        var color=AdvParticleCurve.Color(i["startColor"]!,t,random.Value()).Select(c=>MathF.Floor(Math.Clamp(c,0,1)*255+.5f)/255).ToArray();var shape=Shape();
        var p=new Particle{Life=life,Position=shape.Position*ShapeScale,Velocity=shape.Direction*speed,Size=size,Rotation=rot*sign,Spin=sign,Color=color,Random=Enumerable.Range(0,43).Select(_=>random.Value()).ToArray()};
        if(WorldSpace){p.Position=ShapeToWorld(p.Position)+(origin??Vector3.Lerp(previousEmitter,EmitterPosition,fraction??(stepDelta>0?Math.Clamp(1-age/stepDelta,0,1):1)));p.Velocity=LocalToWorld(p.Velocity);}
        else if(origin is Vector3 at)p.Position+=WorldToLocal(at-EmitterPosition);
        for(int axis=0;axis<3;axis++)if(RendererFlip[axis]>0&&random.Value()<RendererFlip[axis]){var flip=p.Flip;flip[axis]=-1;p.Flip=flip;}
        if(Enabled(Data["ShapeModule"])&&Actor.B(Data["ShapeModule"]?["alignToDirection"])){var z=Normalize(shape.Direction);var x=Vector3.Cross(Vector3.UnitY,z);x=x.Length()<1e-6?Vector3.UnitX:Normalize(x);p.Alignment=[x,Vector3.Cross(z,x),z];}
        for(int j=0;j<4;j++)p.Stable[j]=random.Value();if(Enabled(Data["UVModule"])){p.UvRandom=random.Value();p.UvStart=random.Value();}
        p.SubBirth=subEmitters.Select(e=>e.Probability>=1||random.Value()<e.Probability?new SubBirthState(e.System!.Data["EmissionModule"]?["m_Bursts"]?.AsArray().Count??0):null).ToArray();
        if(Update(p,age)){particles.Add(p);EmitSubBirths(p,age);}
    }
    public Vector3 Size(Particle p)
    {
        float t=Math.Clamp(p.Age/p.Life,0,1);Vector3 s=p.Size;if(Enabled(Data["NoiseModule"]))s*=Vector3.One+p.Noise*C(Data["NoiseModule"]!["sizeAmount"],t,p.Random[42]);
        var z=Data["SizeModule"]!;if(!Enabled(z))return s;float x=C(z["curve"],t,p.Random[16]);return s*(Actor.B(z["separateAxes"])?new(x,C(z["y"],t,p.Random[17]),C(z["z"],t,p.Random[18])):new Vector3(x));
    }
    public float[] CustomData(Particle p,int channel)
    {
        var data=Data["CustomDataModule"];int mode=(int)Actor.N(data?["mode"+channel]);if(!Enabled(data)||mode==0)return new float[4];
        float t=Math.Clamp(p.Age/p.Life,0,1);
        if(mode==2)return AdvParticleCurve.Color(data!["color"+channel]!,t,p.Random[channel==0?34:35]);
        int count=(int)Actor.N(data!["vectorComponentCount"+channel]);return Enumerable.Range(0,4).Select(i=>i<count?C(data[$"vector{channel}_{i}"],t,p.Random[(channel==0?26:30)+i]):0).ToArray();
    }
    public float[] Color(Particle p)
    {
        if(!Enabled(Data["ColorModule"]))return p.Color;var c=AdvParticleCurve.Color(Data["ColorModule"]!["gradient"]!,Math.Clamp(p.Age/p.Life,0,1),p.Random[19]);return Enumerable.Range(0,4).Select(i=>p.Color[i]*c[i]).ToArray();
    }
    public Vector4 Tile(Particle p)
    {
        var u=Data["UVModule"]!;if(!Enabled(u)||((int)Actor.N(u["uvChannelMask"],-1)&1)==0)return new(0,0,1,1);
        if(N(u,"mode")!=0||N(u,"animationType")!=0||N(u,"timeMode")!=0)throw new NotSupportedException("Particle texture-sheet mode unsupported by reference");
        int x=Math.Max(1,(int)N(u,"tilesX")),y=Math.Max(1,(int)N(u,"tilesY")),count=x*y;float t=(Math.Clamp(p.Age/p.Life,0,1)*N(u,"cycles"))%1,f=C(u["frameOverTime"],t,p.UvRandom)+C(u["startFrame"],0,p.UvStart);int frame=((int)MathF.Floor(f*count)%count+count)%count;
        return new((frame%x)/(float)x,1-(frame/x+1)/(float)y,1f/x,1f/y);
    }
    bool Update(Particle p,float dt)
    {
        p.Age+=dt;if(p.Age>=p.Life)return false;float t=Math.Clamp(p.Age/p.Life,0,1);var r=p.Random;float Value(JsonNode? n,int slot)=>C(n,t,r[slot]);
        p.Velocity+=ToSimulation(new Vector3(0,-9.81f*Value(Initial["gravityModifier"],36),0),true)*dt;
        var force=Data["ForceModule"]!;if(Enabled(force)){var acceleration=new Vector3(Value(force["x"],23),Value(force["y"],24),Value(force["z"],25));p.Velocity+=ToSimulation(acceleration,Actor.B(force["inWorldSpace"]))*dt;}
        var velocity=Data["VelocityModule"]!;float speed=1;
        if(Enabled(velocity)){
            foreach(string axis in new[]{"orbitalX","orbitalY","orbitalZ","radial"})if(C(velocity[axis],t)!=0)throw new NotSupportedException("Orbital/radial particle velocity unsupported by reference");
            p.Animated=ToSimulation(new(Value(velocity["x"],0),Value(velocity["y"],1),Value(velocity["z"],2)),Actor.B(velocity["inWorldSpace"]));speed=Value(velocity["speedModifier"],10);
        }else p.Animated=Vector3.Zero;
        var limit=Data["ClampVelocityModule"]!;if(Enabled(limit)){
            Vector3 total=p.Velocity+p.Animated;float drag=Value(limit["drag"],15);if(drag>0){if(Actor.B(limit["multiplyDragByParticleSize"])){var size=Size(p);drag*=Math.Max(size.X,Math.Max(size.Y,size.Z));}if(Actor.B(limit["multiplyDragByParticleVelocity"]))drag*=total.Length();total*=Math.Max(0,1-drag*dt);}
            float dampen=N(limit,"dampen");if(Actor.B(limit["separateAxis"])){bool world=Actor.B(limit["inWorldSpace"]);total=FromSimulation(total,world);float Clamp(float v,float max)=>Math.Abs(v)>max?Math.Sign(v)*(Math.Abs(v)-(Math.Abs(v)-max)*dampen):v;total=new(Clamp(total.X,Value(limit["x"],12)),Clamp(total.Y,Value(limit["y"],13)),Clamp(total.Z,Value(limit["z"],14)));total=ToSimulation(total,world);}
            else{float max=Value(limit["magnitude"],11),length=total.Length();if(length>max&&length>0)total*=(length-(length-max)*dampen)/length;}p.Velocity=total-p.Animated;
        }
        var rotation=Data["RotationModule"]!;if(Enabled(rotation)){bool axes=Actor.B(rotation["separateAxes"]);p.Rotation+=new Vector3(axes?Value(rotation["x"],20):0,axes?Value(rotation["y"],21):0,Value(rotation["curve"],22))*p.Spin*dt;}
        Vector3 noise=Enabled(Data["NoiseModule"])?Noise(p,t):Vector3.Zero;p.Position+=(p.Velocity+p.Animated)*(dt*speed)+noise*dt;return true;
    }
    Vector3 Noise(Particle p,float t)
    {
        var n=Data["NoiseModule"]!;var r=p.Random;Vector3 sum=Vector3.Zero;float amplitude=1,frequency=N(n,"frequency"),norm=0;int octaves=Math.Max(1,(int)N(n,"octaves"));
        for(int i=0;i<octaves;i++){sum+=AdvParticleNoise.Sample(p.Position*frequency+new Vector3(noiseOffset),(int)N(n,"quality"))*amplitude;norm+=amplitude;amplitude*=N(n,"octaveMultiplier");frequency*=N(n,"octaveScale");}
        Vector3 value=sum/norm;bool axes=Actor.B(n["separateAxes"]);
        if(Actor.B(n["remapEnabled"]))value=new(C(n["remap"],(value.X+1)/2),C(n[axes?"remapY":"remap"],(value.Y+1)/2),C(n[axes?"remapZ":"remap"],(value.Z+1)/2));
        float x=C(n["strength"],t,r[37]);Vector3 strength=axes?new(x,C(n["strengthY"],t,r[38]),C(n["strengthZ"],t,r[39])):new(x);
        if(Actor.B(n["damping"])&&N(n,"frequency")>0)strength/=N(n,"frequency");p.Noise=value;p.Rotation.Z+=value.Z*strength.Z*C(n["rotationAmount"],t,r[41])*MathF.PI/180;return value*strength*C(n["positionAmount"],t,r[40]);
    }
}
