using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Unity Transform/RectTransform hierarchy for exported frame prefabs.
public sealed class AdvCanvasNode
{
    public JsonNode Record{get;}public string Path{get;}public AdvCanvasNode? Parent{get;private set;}
    public Vector2 AnchorMin,AnchorMax,AnchoredPosition,SizeDelta,Pivot;
    public bool IsRectTransform{get;}
    public Vector2 LocalPosition;
    public Vector3 Scale,Euler;public Quaternion Rotation;public float LocalZ,Alpha=1;
    public bool Active{get;set;}
    public JsonNode? Image{get;}
    public AdvParticles? Particles{get;set;}
    public Action<AdvCanvasNode>? BeforeLayout{get;set;}
    public float Width{get;private set;}public float Height{get;private set;}
    public bool HasLayout{get;private set;}
    public Vector3 LayoutPosition{get;private set;}
    public float[] Matrix{get;private set;}=AdvMatrix.Identity();
    public bool ActiveInHierarchy=>Active&&(Parent?.ActiveInHierarchy??true);
    public float InheritedAlpha=>Alpha*(Parent?.InheritedAlpha??1);
    public AdvCanvasNode(JsonNode node,AdvCanvasNode? parent)
    {
        Record=node;Path=Archive.S(node,"path");Parent=parent;Active=Actor.B(node["active"]);var rect=node["rect"];IsRectTransform=rect is not null;
        static Vector2 Read(JsonNode? v)=>new(Actor.N(v?["x"]),Actor.N(v?["y"]));
        AnchorMin=Read(rect?["m_AnchorMin"]);AnchorMax=Read(rect?["m_AnchorMax"]);AnchoredPosition=Read(rect?["m_AnchoredPosition"]);SizeDelta=Read(rect?["m_SizeDelta"]);Pivot=rect is null?new(.5f):Read(rect["m_Pivot"]);
        LocalPosition=Read(node["localPosition"]);
        Scale=AdvPrefab.Vector(node["localScale"],Vector3.One);var q=node["localRotation"];Rotation=q is null?Quaternion.Identity:new(Actor.N(q["x"]),Actor.N(q["y"]),Actor.N(q["z"]),Actor.N(q["w"],1));LocalZ=Actor.N(node["localPosition"]?["z"]);
        Image=Component("Image")?.DeepClone();Alpha=Actor.N(Component("CanvasGroup")?["m_Alpha"],1);
    }
    public JsonNode? Component(string name)=>Record["components"]?.AsArray().OfType<JsonNode>().FirstOrDefault(c=>Archive.S(c,"class",Archive.S(c,"type"))==name);
    public void Reparent(AdvCanvasNode? parent)
    {
        for(var p=parent;p is not null;p=p.Parent)if(ReferenceEquals(p,this))throw new ArgumentException("Canvas hierarchy cycle",nameof(parent));
        Parent=parent;
    }
    public void Layout(float canvasWidth,float canvasHeight)
    {
        BeforeLayout?.Invoke(this);
        if(!IsRectTransform){
            // A plain Transform is positioned at its serialized local origin.
            // Its RectTransform children anchor against a zero-size parent rect.
            // Treating it as anchor (0,0) shifts underwater/EfSplash by half a canvas.
            Width=Height=0;LayoutPosition=new(LocalPosition,LocalZ);HasLayout=true;
            var transform=new AdvTransform{Position=LayoutPosition,Rotation=Rotation,Scale=Scale}.LocalMatrix();
            Matrix=Parent is null?transform:AdvMatrix.Multiply(Parent.Matrix,transform);return;
        }
        float pw=Parent?.Width??canvasWidth,ph=Parent?.Height??canvasHeight,px=Parent?.Pivot.X??0,py=Parent?.Pivot.Y??0;
        Width=pw*(AnchorMax.X-AnchorMin.X)+SizeDelta.X;Height=ph*(AnchorMax.Y-AnchorMin.Y)+SizeDelta.Y;
        if(Component("AspectRatioFitter") is JsonNode fitter&&Actor.B(fitter["m_Enabled"])){
            float ratio=Actor.N(fitter["m_AspectRatio"],1);int mode=(int)Actor.N(fitter["m_AspectMode"]);
            if(mode==1)Height=Width/ratio;else if(mode==2)Width=Height*ratio;
            else if(mode is 3 or 4){bool byWidth=(pw/ph>ratio)==(mode==4);Width=byWidth?pw:ph*ratio;Height=byWidth?pw/ratio:ph;}
        }
        var position=new Vector3(pw*(AnchorMin.X+(AnchorMax.X-AnchorMin.X)*Pivot.X-px)+AnchoredPosition.X,ph*(AnchorMin.Y+(AnchorMax.Y-AnchorMin.Y)*Pivot.Y-py)+AnchoredPosition.Y,LocalZ);
        LayoutPosition=position;HasLayout=true;
        var local=new AdvTransform{Position=position,Rotation=Rotation,Scale=Scale}.LocalMatrix();Matrix=Parent is null?local:AdvMatrix.Multiply(Parent.Matrix,local);
    }
    public AdvAnimator.Access? Bind(AdvAnimClip.Binding binding)
    {
        if(binding.Class=="ParticleSystem")return Particles?.Bind(binding.Attribute);
        if(binding.Class=="StretchPosition"&&binding.Attribute=="positionPercent"){var stretch=Component("StretchPosition");return stretch is null?null:new(()=>Actor.N(stretch["positionPercent"]),v=>stretch["positionPercent"]=v);}
        string attr=binding.Attribute;int split=attr.LastIndexOf('.');string field=split<0?attr:attr[..split],axis=split<0?"":attr[(split+1)..];
        Func<float>? get=null;Action<float>? set=null;
        if(binding.Class is "Transform" or "RectTransform"){
            if(field=="m_LocalScale"){get=()=>axis switch{"x"=>Scale.X,"y"=>Scale.Y,_=>Scale.Z};set=v=>Scale=axis switch{"x"=>Scale with{X=v},"y"=>Scale with{Y=v},_=>Scale with{Z=v}};}
            else if(field=="m_LocalRotation"){get=()=>axis switch{"x"=>Rotation.X,"y"=>Rotation.Y,"z"=>Rotation.Z,_=>Rotation.W};set=v=>Rotation=axis switch{"x"=>Rotation with{X=v},"y"=>Rotation with{Y=v},"z"=>Rotation with{Z=v},_=>Rotation with{W=v}};}
            else if(field is "localEulerAngles" or "localEulerAnglesRaw" or "m_LocalEulerAnglesHint"){get=()=>axis switch{"x"=>Euler.X,"y"=>Euler.Y,_=>Euler.Z};set=v=>{Euler=axis switch{"x"=>Euler with{X=v},"y"=>Euler with{Y=v},_=>Euler with{Z=v}};Rotation=Quaternion.CreateFromYawPitchRoll(Euler.Y*MathF.PI/180,Euler.X*MathF.PI/180,Euler.Z*MathF.PI/180);};}
            else if(field is "m_AnchoredPosition" or "m_SizeDelta" or "m_AnchorMin" or "m_AnchorMax" or "m_Pivot"){
                Vector2 Read()=>field switch{"m_AnchoredPosition"=>AnchoredPosition,"m_SizeDelta"=>SizeDelta,"m_AnchorMin"=>AnchorMin,"m_AnchorMax"=>AnchorMax,_=>Pivot};
                get=()=>axis=="x"?Read().X:Read().Y;set=v=>{var value=axis=="x"?Read() with{X=v}:Read() with{Y=v};switch(field){case "m_AnchoredPosition":AnchoredPosition=value;break;case "m_SizeDelta":SizeDelta=value;break;case "m_AnchorMin":AnchorMin=value;break;case "m_AnchorMax":AnchorMax=value;break;default:Pivot=value;break;}};
            }else if(field=="m_LocalPosition"&&axis=="z"){get=()=>LocalZ;set=v=>LocalZ=v;}
        }else if(binding.Class=="CanvasGroup"&&attr=="m_Alpha"){get=()=>Alpha;set=v=>Alpha=v;}
        else if(binding.Class=="GameObject"&&attr=="m_IsActive"){get=()=>Active?1:0;set=v=>Active=v>.5f;}
        else if(binding.Class=="Image"&&Image is not null){if(field=="m_Color"){get=()=>Actor.N(Image["m_Color"]![axis]);set=v=>Image["m_Color"]![axis]=v;}else if(attr=="m_FillAmount"){get=()=>Actor.N(Image["m_FillAmount"]);set=v=>Image["m_FillAmount"]=v;}}
        if(get is null||set is null)throw new NotSupportedException("Canvas animation property not connected: "+binding.Class+"."+attr);
        return new(get,set);
    }
    public Vector4 ClipPoint(Vector3 local,float canvasWidth,float canvasHeight,float? perspectiveDistance=null,Vector3? clipTransform=null)
    {
        var v=AdvMatrix.Transform(Matrix,new(local,1));var tr=clipTransform??new Vector3(0,0,1);
        // ScreenCanvas.viewProjection: clip W = D + canvas Z. Keep W for perspective-correct UV interpolation.
        return new((2*v.X/canvasWidth-1+tr.X)*tr.Z,(2*v.Y/canvasHeight-1+tr.Y)*tr.Z,0,perspectiveDistance is float d?1+v.Z/d*tr.Z:1);
    }
    public float[] ClipQuad(float left,float bottom,float right,float top,float canvasWidth,float canvasHeight,float? perspectiveDistance=null,Vector3? clipTransform=null)
    {
        int stride=perspectiveDistance is null?2:4;var result=new float[4*stride];Vector2[] corners=[new(left,bottom),new(right,bottom),new(right,top),new(left,top)];
        for(int i=0;i<4;i++){var v=ClipPoint(new(corners[i],0),canvasWidth,canvasHeight,perspectiveDistance,clipTransform);for(int k=0;k<stride;k++)result[i*stride+k]=v[k];}return result;
    }
}
