using System.Numerics;
using System.Text.Json.Nodes;
using Surface=OurNotes.Director.Core.OffscreenGl.GlSurface;

namespace OurNotes.Director.Core;

// Native URP camera post chain. The GPU programs are the game's exported programs.
public sealed class AdvPostFx
{
    readonly OffscreenGl gl;readonly UnityShaderLibrary shaders;
    readonly Dictionary<(string Name,int Width,int Height,bool Hdr),Surface> targets=[];
    readonly Dictionary<string,uint> curves=[];
    readonly uint black,gray;
    readonly Surface clearedDepth;
    readonly Dictionary<(int Width,int Height),Surface> depthTargets=[];
    readonly Surface lut;
    static readonly string[] LutComponents=["WhiteBalance","ColorAdjustments","ChannelMixer","ShadowsMidtonesHighlights","SplitToning","LiftGammaGain","ColorCurves"];
    JsonObject? previousLut;
    public float GrainIntensity{get;set;}
    public Func<JsonNode,uint>? LoadTexture{get;set;}
    public JsonArray? GrainTextures{get;set;}
    public float GrainScale{get;set;}=1;
    int motionFrame=-1;float[]? motion,previousMotion;
    public AdvPostFx(OffscreenGl gl,UnityShaderLibrary shaders)
    {
        this.gl=gl;this.shaders=shaders;lut=Target("lut",256,16,false);
        black=gl.Upload(new(1,1,[0,0,0,255]));gray=gl.Upload(new(1,1,[128,128,128,255]));clearedDepth=gl.FloatSurface(1,1,1,[1]);
    }
    Surface Target(string name,int w,int h,bool hdr=true)
    {
        // Scene scrubbing uses three bounded drawing sizes in the same GL context.
        // Keep each effect's reference and interaction targets distinct, including
        // their colour formats. Returning to full quality reuses the original FBO.
        var key=(name,w,h,hdr);
        if(targets.TryGetValue(key,out var existing))return existing;
        return targets[key]=hdr?gl.FloatSurface(w,h):gl.Surface(w,h);
    }
    // The three interaction scales reuse their targets while the viewport stays
    // the same. A viewport resize can have arbitrary dimensions, so release its
    // old targets instead of retaining every intermediate window size forever.
    internal void ReleasePresentationTargets()
    {
        foreach(var key in targets.Keys.Where(key=>key.Name!="lut").ToArray())
        {
            gl.ReleaseSurface(targets[key]);targets.Remove(key);
        }
        foreach(var target in depthTargets.Values)gl.ReleaseSurface(target);
        depthTargets.Clear();
    }
    internal long PresentationTargetBytes=>targets.Keys.Where(key=>key.Name!="lut")
        .Sum(key=>(long)key.Width*key.Height*(key.Hdr?8:4))
        +depthTargets.Keys.Sum(key=>(long)key.Width*key.Height*2);
    internal int PresentationTargetCount=>targets.Keys.Count(key=>key.Name!="lut")+depthTargets.Count;
    Surface CameraDepth(Surface source)
    {
        var key=(source.Width,source.Height);
        if(depthTargets.TryGetValue(key,out var existing))return existing;
        // The ADV reference supplies a cleared camera-depth buffer. Bokeh's
        // CoC pass uses texelFetch at full-resolution integer pixel coordinates,
        // so a 1x1 constant texture reads out of bounds and can turn the entire
        // scene into maximum foreground blur. Match the camera's full extent;
        // R16F represents the cleared far-plane value 1 exactly. The GPU fill
        // happens once per cached size, without a full-size CPU pixel array.
        var depth=gl.FloatSurface(source.Width,source.Height,1);
        gl.CopySurface(clearedDepth,depth);
        return depthTargets[key]=depth;
    }
    static float N(JsonNode node,string key)=>Actor.N(node[key]);
    static float[] V(JsonNode node,string key,bool color=false)=> (color?new[]{"r","g","b","a"}:new[]{"x","y","z","w"}).Select(k=>Actor.N(node[key]?[k])).ToArray();
    public static float Linear(float c)=>c<=.04045f?c/12.92f:c<1?(float)Math.Pow((c+.055f)/1.055f,2.4):c==1?1:(float)Math.Pow(c,2.2f);
    static float[] Texel(Surface source)=>[1f/source.Width,1f/source.Height,source.Width,source.Height];
    static float[] Size(Surface source)=>[source.Width,source.Height,1f/source.Width,1f/source.Height];
    void Blit(string name,int pass,Surface target,Dictionary<string,float[]> values,Dictionary<string,uint> textures,int sub=0,params string[] keywords)
        =>gl.Blit(shaders.ProgramVariant(name,pass,sub,keywords),target,values,textures);
    uint Curve(JsonNode? curve,bool linear)
    {
        string key=curve?.ToJsonString()??(linear?"linear":"half");if(curves.TryGetValue(key,out uint texture))return texture;var pixels=new float[128];
        for(int i=0;i<128;i++){
            if(curve is null)pixels[i]=linear?i/128f:.5f;
            else{int length=(int)Actor.N(curve["<length>k__BackingField"]);if(length==0)pixels[i]=Actor.N(curve["m_ZeroValue"]);else{
                if(length>1&&Actor.B(curve["m_Loop"]))throw new NotSupportedException("Loaded looping TextureCurve has no looping curve, matching the reference");
                pixels[i]=AdvParameterLoop.Curve(curve["m_Curve"]!["m_Curve"]!.AsArray(),i/128f);
            }}
        }
        return curves[key]=gl.FloatSurface(128,1,1,pixels).Texture;
    }
    void BuildLut(JsonNode stack)
    {
        bool unchanged=previousLut is not null;
        foreach(string component in LutComponents)if(unchanged&&!JsonNode.DeepEquals(previousLut![component],stack[component]))unchanged=false;
        if(unchanged)return;
        var wb=stack["WhiteBalance"]!;float t1=N(wb,"temperature")/65,t2=N(wb,"tint")/65;
        float x=.31271f-t1*(t1<0?.1f:.05f),y=((2.87f*x-3*x*x)-.27509507f)+t2*.05f,X=x/y,Z=((1-x)-y)/y;
        float L=(.7328f*X+.4296f)-.1624f*Z,M=(-.7036f*X+1.6975f)+.0061f*Z,S=(.003f*X+.0136f)+.9834f*Z;
        var ca=stack["ColorAdjustments"]!;var cm=stack["ChannelMixer"]!;var smh=stack["ShadowsMidtonesHighlights"]!;var st=stack["SplitToning"]!;var lgg=stack["LiftGammaGain"]!;var cc=stack["ColorCurves"]!;
        float[] Balance(string name,float scale){var v=V(lgg,name);var a=v.Take(3).Select(c=>Linear(c)*scale).ToArray();float lum=(a[0]*.2126729f+a[1]*.7151522f)+a[2]*.072175f;return [..a.Select(c=>name=="gamma"?1/Math.Max(v[3]+1+(c-lum),.001f):v[3]+(name=="lift"?0:1)+(c-lum)),0];}
        float[] Range(string name){var v=V(smh,name);float w=v[3]*(v[3]>=0?4:1);return [..v.Take(3).Select(c=>Math.Max(0,Linear(c)+w)),0];}
        float[] Mix(string name)=>[N(cm,name+"OutRedIn")/100,N(cm,name+"OutGreenIn")/100,N(cm,name+"OutBlueIn")/100,0];
        var filter=V(ca,"colorFilter",true);var split=V(st,"shadows",true);split[3]=N(st,"balance")/100;
        var values=new Dictionary<string,float[]>{["_Lut_Params"]=[16,.5f/256,.5f/16,16f/15],["_ColorBalance"]=[.949237f/L,1.03542f/M,1.08728f/S,0],
            ["_ColorFilter"]=[Linear(filter[0]),Linear(filter[1]),Linear(filter[2]),filter[3]],
            ["_ChannelMixerRed"]=Mix("red"),["_ChannelMixerGreen"]=Mix("green"),["_ChannelMixerBlue"]=Mix("blue"),
            ["_HueSatCon"]=[N(ca,"hueShift")/360,N(ca,"saturation")/100+1,N(ca,"contrast")/100+1,0],
            ["_Lift"]=Balance("lift",.15f),["_Gamma"]=Balance("gamma",.8f),["_Gain"]=Balance("gain",.8f),
            ["_Shadows"]=Range("shadows"),["_Midtones"]=Range("midtones"),["_Highlights"]=Range("highlights"),
            ["_ShaHiLimits"]=[N(smh,"shadowsStart"),N(smh,"shadowsEnd"),N(smh,"highlightsStart"),N(smh,"highlightsEnd")],
            ["_SplitShadows"]=split,["_SplitHighlights"]=V(st,"highlights",true)};
        var textures=new Dictionary<string,uint>();foreach(var name in new[]{"master","red","green","blue","hueVsHue","hueVsSat","lumVsSat","satVsSat"})textures["_Curve"+char.ToUpperInvariant(name[0])+name[1..]]=Curve(cc[name],name is "master" or "red" or "green" or "blue");
        Blit("Hidden/Universal Render Pipeline/LutBuilderLdr",0,lut,values,textures);
        previousLut=new();foreach(string component in LutComponents)previousLut[component]=stack[component]?.DeepClone();
    }
    public static float[] BokehKernel(JsonNode d,float maxRadius,float rcpAspect)
    {
        var output=new float[42*4];float n=N(d,"bladeCount"),curvature=1-N(d,"bladeCurvature"),rotation=N(d,"bladeRotation")*.017453292f,pi=3.1415927f,twoPi=6.2831855f,nt=MathF.Cos(pi/n);int index=0;
        for(int ring=1;ring<4;ring++){int points=ring*7;float radius=(ring+.14285715f)/3.142857f;for(int point=0;point<points;point++){
            float phi=point*twoPi/points,dt=MathF.Cos(phi-(twoPi/n)*MathF.Truncate((phi*n+pi)/twoPi)),r=radius*MathF.Pow(nt/dt,curvature);
            float u=MathF.Cos(phi-rotation)*r*maxRadius,v=MathF.Sin(phi-rotation)*r*maxRadius;output[index++]=u;output[index++]=v;output[index++]=MathF.Sqrt(u*u+v*v);output[index++]=u*rcpAspect;
        }}return output;
    }
    Surface DepthOfField(JsonNode d,Surface source,AdvCamera camera)
    {
        if(N(d,"mode")!=2)throw new NotSupportedException("Gaussian camera DoF is unsupported by the reference");
        int w=source.Width,h=source.Height;var dst=Target("dof",w,h);var coc=Target("coc",w,h,false);var ping=Target("dofPing",w/2,h/2);var pong=Target("dofPong",w/2,h/2);
        float f=N(d,"focalLength")/1000,A=N(d,"focalLength")/N(d,"aperture"),P=N(d,"focusDistance"),maxCoC=f*A/(P-f),radius=Math.Min(14f/h,.05f),rcp=1/(ping.Width/(float)ping.Height),margin=2f/h,tz=camera.Far*(1/camera.Near),zc0=1-tz;
        var values=new Dictionary<string,float[]>{["_CoCParams"]=[P,maxCoC,radius,rcp],["_BokehKernel"]=BokehKernel(d,radius,rcp),["_DownSampleScaleFactor"]=[.5f,.5f,2,2],
            ["_BokehConstants"]=[margin,margin*2,0,0],["_SourceSize"]=Size(source),["_ZBufferParams"]=[zc0,tz,zc0/camera.Far,tz/camera.Far]};
        var textures=new Dictionary<string,uint>{["_CameraDepthTexture"]=CameraDepth(source).Texture,["_FullCoCTexture"]=coc.Texture,["_DofTexture"]=ping.Texture};
        void Pass(int index,Surface input,Surface output){values["_BlitTexture_TexelSize"]=Texel(input);textures["_BlitTexture"]=input.Texture;Blit("Hidden/Universal Render Pipeline/BokehDepthOfField",index,output,values,textures,1,"_USE_FAST_SRGB_LINEAR_CONVERSION","_ENABLE_ALPHA_OUTPUT");}
        Pass(0,source,coc);Pass(1,source,ping);Pass(2,ping,pong);Pass(3,pong,ping);Pass(4,source,dst);return dst;
    }
    Surface Bloom(JsonNode b,Surface source)
    {
        if(N(b,"filter")!=0||N(b,"downscale")!=0||Actor.B(b["highQualityFiltering"]))throw new NotSupportedException("Bloom configuration unsupported by reference");
        int w=Math.Max(1,source.Width/2),h=Math.Max(1,source.Height/2),n=Math.Min(Math.Max((int)MathF.Log2(Math.Max(w,h))-1,1),(int)N(b,"maxIterations"));
        var down=new Surface[n];var up=new Surface[n];for(int i=0;i<n;i++){down[i]=Target("bloomDown"+i,Math.Max(1,w>>i),Math.Max(1,h>>i));up[i]=Target("bloomUp"+i,down[i].Width,down[i].Height);}
        float threshold=Linear(N(b,"threshold"));float[] parameters=[.05f+.9f*Math.Clamp(N(b,"scatter"),0,1),N(b,"clamp"),threshold,threshold*.5f];
        void Pass(int pass,Surface input,Surface target,Surface? low=null)=>Blit("Hidden/Universal Render Pipeline/Bloom",pass,target,new(){["_Params"]=parameters,["_BlitTexture_TexelSize"]=Texel(input)},new(){["_BlitTexture"]=input.Texture,["_SourceTexLowMip"]=low?.Texture??black},0,pass==0?["_ENABLE_ALPHA_OUTPUT"]:[]);
        Pass(0,source,down[0]);for(int i=1;i<n;i++){Pass(1,down[i-1],up[i]);Pass(2,up[i],down[i]);}
        for(int i=n-2;i>=0;i--)Pass(3,down[i],up[i],i==n-2?down[i+1]:up[i+1]);return n==1?down[0]:up[0];
    }
    Surface MotionBlur(JsonNode b,Surface source,AdvCamera camera)
    {
        if(N(b,"mode")!=0)throw new NotSupportedException("Object motion blur unsupported by reference");
        var m=camera.ViewProjection(source.Width/(float)source.Height);var matrix=new Matrix4x4(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
        if(!Matrix4x4.Invert(matrix,out var inv))throw new InvalidDataException("Singular camera matrix");float[] inverse=[inv.M11,inv.M12,inv.M13,inv.M14,inv.M21,inv.M22,inv.M23,inv.M24,inv.M31,inv.M32,inv.M33,inv.M34,inv.M41,inv.M42,inv.M43,inv.M44];
        var dst=Target("motionBlur",source.Width,source.Height);var depth=CameraDepth(source);
        Blit("Hidden/Universal Render Pipeline/CameraMotionBlur",(int)N(b,"quality"),dst,new(){["_BlitTexture_TexelSize"]=Texel(source),["_SourceSize"]=Size(source),["_Intensity"]=[N(b,"intensity")],["_Clamp"]=[N(b,"clamp")],["_ViewProjM"]=motion!,["_PrevViewProjM"]=previousMotion!,["unity_MatrixInvVP"]=inverse,["_CameraDepthTexture_TexelSize"]=Texel(depth)},new(){["_BlitTexture"]=source.Texture,["_CameraDepthTexture"]=depth.Texture},0,"_ENABLE_ALPHA_OUTPUT");return dst;
    }
    // Home scenes use the same colour/bloom chain but have no ADV camera or camera-depth effects.
    public void RenderColor(JsonNode stack,int frame)
    {
        if(N(stack["DepthOfField"]!,"mode")!=0||N(stack["MotionBlur"]!,"intensity")>0||N(stack["AdvCurvedLens"]!,"intensity")>0)
            throw new NotSupportedException("Home depth of field, motion blur and curved lens are unsupported.");
        var source=gl.CurrentSurface;BuildLut(stack);var b=stack["Bloom"]!;
        var bloom=N(b,"intensity")>0?Bloom(b,source):new Surface(black,0,1,1);
        var output=Target("post",source.Width,source.Height);Uber(stack,source,bloom,output,frame);gl.CopySurface(output,source);
    }
    public void Render(JsonNode stack,AdvCamera camera,int frame,float curvedLensRate=1)
    {
        var source=gl.CurrentSurface;AdvanceCamera(camera,frame);
        BuildLut(stack);var color=source;
        if(N(stack["DepthOfField"]!,"mode")!=0)color=DepthOfField(stack["DepthOfField"]!,color,camera);
        if(N(stack["MotionBlur"]!,"intensity")>0)color=MotionBlur(stack["MotionBlur"]!,color,camera);
        var b=stack["Bloom"]!;var bloom=N(b,"intensity")>0?Bloom(b,color):new Surface(black,0,1,1);
        var output=Target("post",source.Width,source.Height);Uber(stack,color,bloom,output,frame);
        var lens=stack["AdvCurvedLens"]!;float eff=(Actor.B(lens["attenuateByCameraDistance"])?curvedLensRate:1)*N(lens,"intensity");
        if(eff>0&&(N(lens,"horizontalRate")>0||N(lens,"verticalRate")>0)){
            var dst=Target("curvedLens",source.Width,source.Height);var center=V(lens,"center");Blit("Adv/CurvedLens",0,dst,new(){["_BlitTexture_TexelSize"]=Texel(output),["_AdvCurvedLensParams1"]=[center[0],center[1],eff,N(lens,"size")],["_AdvCurvedLensParams2"]=[N(lens,"softness"),N(lens,"horizontalRate"),N(lens,"verticalRate"),Math.Max(N(lens,"scale"),.001f)],["_AdvCurvedLensParams3"]=[N(lens,"reverse"),0,0,0]},new(){["_BlitTexture"]=output.Texture});output=dst;
        }
        gl.CopySurface(output,source);
    }
    public void AdvanceCamera(AdvCamera camera,int frame)
    {if(frame!=motionFrame){previousMotion=motion;motion=camera.ViewProjection(gl.Width/(float)gl.Height);previousMotion??=motion;motionFrame=frame;}}
    void Uber(JsonNode stack,Surface source,Surface bloom,Surface output,int frame)
    {
        var b=stack["Bloom"]!;var g=stack["FilmGrain"]!;var v=stack["Vignette"]!;var ld=stack["LensDistortion"]!;var ca=stack["ChromaticAberration"]!;
        float[] tint=V(b,"tint",true).Take(3).Select(Linear).ToArray();float lum=(tint[0]*.2126729f+tint[1]*.7151522f)+tint[2]*.072175f;tint=lum>0?tint.Select(c=>c/lum).ToArray():[1,1,1];
        var keywords=new List<string>{"_USE_FAST_SRGB_LINEAR_CONVERSION","_ENABLE_ALPHA_OUTPUT"};if(N(b,"intensity")>0)keywords.Add("_BLOOM_LQ");
        if(N(b,"intensity")>0&&b["dirtTexture"] is not null&&N(b,"dirtIntensity")>0)throw new NotSupportedException("Bloom lens dirt unsupported by reference");
        float intensity=N(ld,"intensity");if(intensity!=0&&(N(ld,"xMultiplier")>0||N(ld,"yMultiplier")>0))keywords.Add("_DISTORTION");
        if(N(ca,"intensity")>0)keywords.Add("_CHROMATIC_ABERRATION");
        int tm=(int)N(stack["Tonemapping"]!,"mode");if(tm==2)keywords.Add("_TONEMAP_ACES");else if(tm==1)keywords.Add("_TONEMAP_NEUTRAL");
        uint grain=gray;float gw=1,gh=1,gi=N(g,"intensity")*GrainIntensity;
        if(gi>0&&(N(g,"type")!=10||g["texture"] is not null)){
            var desc=N(g,"type")==10?g["texture"]!:GrainTextures?[(int)N(g,"type")]??throw new InvalidDataException("Film grain texture absent");grain=LoadTexture?.Invoke(desc)??throw new InvalidOperationException("No volume texture loader");gw=Actor.N(desc["width"]);gh=Actor.N(desc["height"]);keywords.Add("_FILM_GRAIN");
        }
        var random=new AdvRandom((uint)frame);float ox=random.Value(),oy=random.Value(),theta=Math.Min(160,Math.Max(Math.Abs(intensity*100),1)*1.6f)*.017453292f;
        var center=V(ld,"center");var vc=V(v,"color",true);var pos=V(v,"center");
        Blit("Hidden/Universal Render Pipeline/UberPost",0,output,new(){["_BlitTexture_TexelSize"]=Texel(source),["_BloomTexture_TexelSize"]=Texel(bloom),["_Bloom_Params"]=[N(b,"intensity"),..tint],
            ["_Lut_Params"]=[1f/256,1f/16,15,MathF.Pow(2,N(stack["ColorAdjustments"]!,"postExposure"))],["_UserLut_Params"]=[0,0,0,0],
            ["_Distortion_Params1"]=[center[0]*2-1,center[1]*2-1,Math.Max(N(ld,"xMultiplier"),.0001f),Math.Max(N(ld,"yMultiplier"),.0001f)],
            ["_Distortion_Params2"]=[intensity>=0?theta:1/theta,MathF.Tan(theta*.5f)*2,1/N(ld,"scale"),intensity*100],["_Chroma_Params"]=[N(ca,"intensity")*.05f],
            ["_Vignette_Params1"]=[vc[0],vc[1],vc[2],Actor.B(v["rounded"])?source.Width/(float)source.Height:1],["_Vignette_Params2"]=[pos[0],pos[1],N(v,"intensity")*3,N(v,"smoothness")*5],
            ["_Grain_Params"]=[gi*4,N(g,"response")],["_Grain_TilingParams"]=[source.Width*GrainScale/gw,source.Height*GrainScale/gh,ox,oy]},
            new(){["_BlitTexture"]=source.Texture,["_Bloom_Texture"]=bloom.Texture,["_InternalLut"]=lut.Texture,["_UserLut"]=black,["_Grain_Texture"]=grain},0,keywords.ToArray());
    }
    public void Final(bool fxaa)
    {
        var source=gl.CurrentSurface;var target=Target("final",source.Width,source.Height,false);
        Blit("Hidden/Universal Render Pipeline/FinalPost",0,target,new(){["_SourceSize"]=Size(source)},new(){["_BlitTexture"]=source.Texture},1,fxaa?["_FXAA","_ENABLE_ALPHA_OUTPUT"]:["_ENABLE_ALPHA_OUTPUT"]);
        gl.CopySurface(target,source);
    }
}
