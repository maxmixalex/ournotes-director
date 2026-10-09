using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Unity camera basis (+Z forward) and column-major OpenGL clip matrices, matching engine/math.js.
public static class AdvMatrix
{
    public static float[] Identity()=>[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
    public static float[] Multiply(float[] a,float[] b)
    {
        var result=new float[16];
        for(int c=0;c<4;c++)for(int r=0;r<4;r++){double v=0;for(int k=0;k<4;k++)v+=(double)a[k*4+r]*b[c*4+k];result[c*4+r]=(float)v;}
        return result;
    }
    private readonly record struct Q(double X,double Y,double Z,double W)
    {
        public static Q operator*(Q a,Q b)=>new(a.W*b.X+a.X*b.W+a.Y*b.Z-a.Z*b.Y,a.W*b.Y-a.X*b.Z+a.Y*b.W+a.Z*b.X,a.W*b.Z+a.X*b.Y-a.Y*b.X+a.Z*b.W,a.W*b.W-a.X*b.X-a.Y*b.Y-a.Z*b.Z);
    }
    public static float[] Trs(Vector3 position,Vector3 euler,Vector3 scale)
    {
        static double Half(float degrees)=>degrees*Math.PI/360;
        double rx=Half(euler.X),ry=Half(euler.Y),rz=Half(euler.Z);
        var q=new Q(0,Math.Sin(ry),0,Math.Cos(ry))*new Q(Math.Sin(rx),0,0,Math.Cos(rx))*new Q(0,0,Math.Sin(rz),Math.Cos(rz));
        var (x,y,z,w)=q;var m=Identity();
        m[0]=(float)((1-2*(y*y+z*z))*scale.X);m[1]=(float)(2*(x*y+z*w)*scale.X);m[2]=(float)(2*(x*z-y*w)*scale.X);
        m[4]=(float)(2*(x*y-z*w)*scale.Y);m[5]=(float)((1-2*(x*x+z*z))*scale.Y);m[6]=(float)(2*(y*z+x*w)*scale.Y);
        m[8]=(float)(2*(x*z+y*w)*scale.Z);m[9]=(float)(2*(y*z-x*w)*scale.Z);m[10]=(float)((1-2*(x*x+y*y))*scale.Z);
        m[12]=position.X;m[13]=position.Y;m[14]=position.Z;return m;
    }
    public static float[] InverseRigid(float[] m)
    {
        var o=Identity();for(int r=0;r<3;r++)for(int c=0;c<3;c++)o[c*4+r]=m[r*4+c];
        for(int r=0;r<3;r++)o[12+r]=(float)(-((double)o[r]*m[12]+(double)o[4+r]*m[13]+(double)o[8+r]*m[14]));return o;
    }
    public static float[] Perspective(float fov,float aspect,float near,float far)
    {
        if(!float.IsFinite(fov)||fov<=0||fov>=180||!float.IsFinite(aspect)||aspect<=0||!float.IsFinite(near)||near<=0||!float.IsFinite(far)||far<=near)throw new ArgumentException("Invalid perspective camera");
        double f=1/Math.Tan(fov*Math.PI/360);var m=new float[16];m[0]=(float)(f/aspect);m[5]=(float)f;
        m[10]=(float)(-((double)far+near)/((double)far-near));m[11]=-1;m[14]=(float)(-(2.0*far*near)/((double)far-near));return m;
    }
    public static Vector4 Transform(float[] m,Vector4 p)=>new(
        (float)((double)m[0]*p.X+(double)m[4]*p.Y+(double)m[8]*p.Z+(double)m[12]*p.W),
        (float)((double)m[1]*p.X+(double)m[5]*p.Y+(double)m[9]*p.Z+(double)m[13]*p.W),
        (float)((double)m[2]*p.X+(double)m[6]*p.Y+(double)m[10]*p.Z+(double)m[14]*p.W),
        (float)((double)m[3]*p.X+(double)m[7]*p.Y+(double)m[11]*p.Z+(double)m[15]*p.W));
}

public sealed class AdvCamera
{
    private readonly AdvLoop loop;
    private readonly AdvTweens runner;
    private readonly Dictionary<string,AdvTween> tweens=[];
    private readonly float defaultFov;
    public Vector3 Position{get;set;}
    public Vector3 Euler{get;set;}
    public Vector3 ShakeOffset{get;set;}
    public float Near{get;}
    public float Far{get;}
    public float FieldOfView{get;set;}
    public float ZoomRatio{get;private set;}=1;
    public float Fov=>FieldOfView/ZoomRatio;
    public AdvCamera(AdvLoop loop,AdvTweens runner,float fov,float near,float far)
    {this.loop=loop;this.runner=runner;defaultFov=FieldOfView=fov;Near=near;Far=far;_ = AdvMatrix.Perspective(fov,1,near,far);}
    public static AdvCamera FromScene(AdvLoop loop,AdvTweens runner,JsonNode scene)
    {
        var node=scene["cameraManager"]!["nodes"]!.AsArray().First(n=>Archive.S(n!,"path")=="CameraManager/MainCamera")!;
        var camera=node["components"]!.AsArray().First(c=>Archive.S(c!,"type")=="Camera")!;
        if(camera["orthographic"]?.GetValue<bool>()==true)throw new NotSupportedException("Orthographic main camera unsupported by reference");
        return new(loop,runner,camera["field of view"]!.GetValue<float>(),camera["near clip plane"]!.GetValue<float>(),camera["far clip plane"]!.GetValue<float>());
    }
    private AdvWait Axis(string key,Func<float> get,Action<float> set,float value,float duration,AdvEase ease)
    {
        if(tweens.Remove(key,out var old))old.Kill();
        if(duration<=0){set(value);return loop.Ready();}
        // Camera uses a captured start, unlike field's lazy getter.
        float start=get();var tween=runner.To(()=>start,value,duration,ease,set);tweens[key]=tween;return tween.Completed;
    }
    public AdvWait MoveX(float x,float duration=0,AdvEase ease=AdvEase.OutQuad)=>Axis("x",()=>Position.X,v=>Position=Position with{X=v},x,duration,ease);
    public AdvWait MoveY(float y,float duration=0,AdvEase ease=AdvEase.OutQuad)=>Axis("y",()=>Position.Y,v=>Position=Position with{Y=v},y,duration,ease);
    public AdvWait MoveZ(float z,float duration=0,AdvEase ease=AdvEase.OutQuad)=>Axis("z",()=>Position.Z,v=>Position=Position with{Z=v},z,duration,ease);
    public AdvWait RotateX(float x,float duration=0,AdvEase ease=AdvEase.OutQuad)=>Axis("rx",()=>Euler.X,v=>Euler=Euler with{X=v},x,duration,ease);
    public AdvWait RotateY(float y,float duration=0,AdvEase ease=AdvEase.OutQuad)=>Axis("ry",()=>Euler.Y,v=>Euler=Euler with{Y=v},y,duration,ease);
    public AdvWait RotateZ(float z,float duration=0,AdvEase ease=AdvEase.OutQuad)=>Axis("rz",()=>Euler.Z,v=>Euler=Euler with{Z=v},z,duration,ease);
    public AdvWait Zoom(float ratio,float duration=0,AdvEase ease=AdvEase.OutQuad)=>Axis("zoom",()=>ZoomRatio,v=>ZoomRatio=v,ratio,duration,ease);
    public void ResetFov()=>FieldOfView=defaultFov;
    public void Reset(){foreach(var tween in tweens.Values)tween.Kill();tweens.Clear();Position=Euler=ShakeOffset=Vector3.Zero;ZoomRatio=1;}
    public float[] View(){var flip=AdvMatrix.Identity();flip[10]=-1;return AdvMatrix.Multiply(flip,AdvMatrix.InverseRigid(AdvMatrix.Trs(Position+ShakeOffset,Euler,Vector3.One)));}
    public float[] ViewProjection(float aspect)=>AdvMatrix.Multiply(AdvMatrix.Perspective(Fov,aspect,Near,Far),View());
    public void BindRotations(AdvPlayback player)
    {
        player.Register("Tilt",(r,p)=>Run(r,p,true));player.Register("Role",(r,p)=>Run(r,p,false));
    }
    private IEnumerable<AdvWait> Run(AdvRow row,AdvPlayback player,bool tilt){yield return player.NoWait(row,Inner(row,player,tilt));}
    private IEnumerable<AdvWait> Inner(AdvRow row,AdvPlayback player,bool tilt)
    {
        var delay=player.CommonDelay(player.CalcDuration(row.Number("DelaySeconds")));yield return delay;if(!delay.GetResult())yield break;
        float value=float.TryParse(row.Text("Parameter1"),NumberStyles.Float,CultureInfo.InvariantCulture,out float number)&&float.IsFinite(number)?number:0;
        var ease=Enum.TryParse<AdvEase>(row.Text("Parameter2"),false,out var parsed)&&Enum.IsDefined(parsed)?parsed:AdvEase.OutQuad;
        float duration=player.CalcDuration(row.Number("Duration"));
        yield return tilt?RotateX(-value,duration,ease):RotateZ(value,duration,ease);
    }
}
