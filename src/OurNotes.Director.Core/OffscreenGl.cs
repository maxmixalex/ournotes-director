using System.Runtime.InteropServices;
using System.Text;

namespace OurNotes.Director.Core;

// Hidden Win32 context; pixels are rendered only to FBOs, never captured from a window.
public sealed unsafe partial class OffscreenGl : IDisposable
{
    const string GL="opengl32.dll";
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint CreateWindowExW(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")] static extern nint GetDC(nint w);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint w,nint dc);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint w);
    [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(nint dc,byte[] pfd);
    [DllImport("gdi32.dll")] static extern bool SetPixelFormat(nint dc,int format,byte[] pfd);
    [DllImport(GL)] static extern nint wglCreateContext(nint dc);
    [DllImport(GL)] static extern bool wglMakeCurrent(nint dc,nint rc);
    [DllImport(GL)] static extern bool wglDeleteContext(nint rc);
    [DllImport(GL,CharSet=CharSet.Ansi)] static extern nint wglGetProcAddress(string name);
    [DllImport(GL)] static extern nint glGetString(uint name);
    [DllImport(GL)] static extern void glGetIntegerv(uint p,out int value);
    [DllImport(GL)] static extern void glGenTextures(int n,out uint texture);
    [DllImport(GL)] static extern void glDeleteTextures(int n,ref uint texture);
    [DllImport(GL)] static extern void glBindTexture(uint target,uint texture);
    [DllImport(GL)] static extern void glTexParameteri(uint target,uint name,int value);
    [DllImport(GL)] static extern void glTexImage2D(uint target,int level,int format,int w,int h,int border,uint input,uint type,nint pixels);
    [DllImport(GL)] static extern void glCopyTexSubImage2D(uint target,int level,int xoffset,int yoffset,int x,int y,int width,int height);
    [DllImport(GL)] static extern void glViewport(int x,int y,int w,int h);
    [DllImport(GL)] static extern void glClearColor(float r,float g,float b,float a);
    [DllImport(GL)] static extern void glClear(uint bits);
    [DllImport(GL)] static extern void glEnable(uint state);
    [DllImport(GL)] static extern void glDisable(uint state);
    [DllImport(GL)] static extern void glStencilFunc(uint function,int reference,uint mask);
    [DllImport(GL)] static extern void glStencilOp(uint fail,uint depthFail,uint pass);
    [DllImport(GL)] static extern void glStencilMask(uint mask);
    [DllImport(GL)] static extern void glColorMask(byte r,byte g,byte b,byte a);
    [DllImport(GL)] static extern void glClearStencil(int value);
    [DllImport(GL)] static extern void glDepthMask(byte value);
    [DllImport(GL)] static extern void glDepthFunc(uint function);
    [DllImport(GL)] static extern void glClearDepth(double value);
    [DllImport(GL)] static extern void glPolygonOffset(float factor,float units);
    [DllImport(GL,EntryPoint="glGetIntegerv")] static extern void glGetIntegerVector(uint name,nint values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void RenderbufferStorage(uint target,uint format,int width,int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void FramebufferRenderbuffer(uint target,uint attachment,uint bufferTarget,uint buffer);
    [DllImport(GL)] static extern void glBlendFunc(uint source,uint dest);
    [DllImport(GL)] static extern void glCullFace(uint mode);
    [DllImport(GL)] static extern void glFrontFace(uint mode);
    [DllImport(GL)] static extern void glBegin(uint mode);
    [DllImport(GL)] static extern void glEnd();
    [DllImport(GL)] static extern void glTexCoord2f(float u,float v);
    [DllImport(GL)] static extern void glColor4f(float r,float g,float b,float a);
    [DllImport(GL)] static extern void glVertex2f(float x,float y);
    [DllImport(GL)] static extern void glVertex4f(float x,float y,float z,float w);
    [DllImport(GL)] static extern void glReadPixels(int x,int y,int w,int h,uint format,uint type,byte[] output);
    [DllImport(GL,EntryPoint="glReadPixels")] static extern void glReadPixelsBuffer(int x,int y,int w,int h,uint format,uint type,nint offset);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void BufferData(uint target,nint size,nint data,uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate nint MapBufferRange(uint target,nint offset,nint length,uint access);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate byte UnmapBuffer(uint target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Gen(int n,out uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Delete(int n,ref uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Bind(uint target,uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Attach(uint target,uint attachment,uint textureTarget,uint texture,int level);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint Status(uint target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint CreateShader(uint type);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void ShaderSource(uint shader,int n,nint strings,nint lengths);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void One(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint CreateProgram();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Two(uint a,uint b);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void GetIv(uint id,uint p,out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Info(uint id,int n,out int length,StringBuilder text);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int Location(uint program,[MarshalAs(UnmanagedType.LPStr)]string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Uniform1(int loc,float a);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Uniform2(int loc,float a,float b);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Uniform4(int loc,float a,float b,float c,float d);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void UniformInt(int loc,int v);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void BlendSeparate(uint a,uint b,uint c,uint d);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void BlitFramebuffer(int sx0,int sy0,int sx1,int sy1,int dx0,int dy0,int dx1,int dy1,uint mask,uint filter);
    readonly Dictionary<string,Delegate> funcs=new();
    T Fn<T>(string name) where T:Delegate {if(!funcs.TryGetValue(name,out var f)){var ptr=OperatingSystem.IsLinux()?LinuxGlContext.Proc(name):wglGetProcAddress(name);if(ptr==0||ptr==1||ptr==2||ptr==3||ptr==-1)throw new NotSupportedException($"OpenGL function unavailable: {name}"); funcs[name]=f=Marshal.GetDelegateForFunctionPointer<T>(ptr);}return (T)f;}
    readonly LinuxGlContext? linuxContext; readonly nint window,dc,rc; readonly uint program,main,mask,mainTexture,maskTexture; readonly List<uint> textures=[];
    uint layer,layerTexture,drawTarget;
    GlSurface? blurHalf;readonly List<uint> framebuffers=[];
    readonly Dictionary<uint,uint> stencilBuffers=[];int stencilDepth;
    public readonly record struct GlSurface(uint Texture,uint Framebuffer,int Width,int Height);
    public int Width{get;} public int Height{get;} public string Device{get;}
    readonly Dictionary<string,int> uniformLocations=new(StringComparer.Ordinal);
    int Loc(string name){if(!uniformLocations.TryGetValue(name,out int location))uniformLocations[name]=location=Fn<Location>("glGetUniformLocation")(program,name);return location;}
    void Float(string name,float v)=>Fn<Uniform1>("glUniform1f")(Loc(name),v);
    void Vec4(string name,float a,float b,float c,float d)=>Fn<Uniform4>("glUniform4f")(Loc(name),a,b,c,d);
    void TextureUnit(uint unit,uint texture){Fn<One>("glActiveTexture")(0x84C0+unit);glBindTexture(0x0DE1,texture);}
    public OffscreenGl(int width,int height)
    {
        Width=width;Height=height;
        if(OperatingSystem.IsLinux())linuxContext=new();else{
        window=CreateWindowExW(0,"STATIC","OurNotes offscreen",0,0,0,1,1,0,0,0,0);if(window==0)throw new InvalidOperationException("Hidden context window failed");dc=GetDC(window);
        var pfd=new byte[40];pfd[0]=40;pfd[2]=1;pfd[4]=0x24;pfd[9]=32;pfd[16]=8;
        int format=ChoosePixelFormat(dc,pfd);if(format==0||!SetPixelFormat(dc,format,pfd))throw new NotSupportedException("No OpenGL pixel format");rc=wglCreateContext(dc);if(rc==0||!wglMakeCurrent(dc,rc))throw new NotSupportedException("Cannot create OpenGL context");
        }
        Device=Marshal.PtrToStringAnsi(glGetString(0x1F01))+" / "+Marshal.PtrToStringAnsi(glGetString(0x1F02));
        glGetIntegerv(0x0D33,out var max);if(width>max||height>max)throw new ArgumentException($"Render target exceeds GPU limit {max}");
        uint Compile(uint type,string source){var s=Fn<CreateShader>("glCreateShader")(type);var ptr=Marshal.StringToHGlobalAnsi(source);try{Fn<ShaderSource>("glShaderSource")(s,1,(nint)(&ptr),0);}finally{Marshal.FreeHGlobal(ptr);}Fn<One>("glCompileShader")(s);Fn<GetIv>("glGetShaderiv")(s,0x8B81,out var ok);if(ok==0){var log=new StringBuilder(8192);Fn<Info>("glGetShaderInfoLog")(s,8192,out _,log);throw new InvalidOperationException(log.ToString());}return s;}
        var vert=Compile(0x8B31,"#version 120\nvarying vec2 uv; varying vec4 vertexColor; void main(){gl_Position=gl_Vertex; uv=gl_MultiTexCoord0.xy; vertexColor=gl_Color;}");
        var frag=Compile(0x8B30,"""
            #version 120
            varying vec2 uv;
            varying vec4 vertexColor;
            uniform sampler2D image, clipMask;
            uniform vec2 size;
            uniform float opacity, masked, inverted, maskPass;
            uniform float uiStencilPass;
            uniform float postPass;
            uniform float compositePass, compositeAlpha, compositeBrightness;
            uniform float textPass, rulePass, ruleValue, ruleGradient;
            uniform vec4 grade;
            uniform vec4 multiplyColor, screenColor, tint;
            void main(){
              vec4 c=texture2D(image,uv)*vertexColor;
              if(uiStencilPass>0.5&&c.a*tint.a*opacity<0.001)discard;
              if(textPass>0.5){float a=smoothstep(0.5-fwidth(c.a),0.5+fwidth(c.a),c.a)*tint.a;gl_FragColor=vec4(tint.rgb*a,a);return;}
              if(rulePass>0.5){float a=clamp(c.r-ruleValue,0.0,1.0)*tint.a;gl_FragColor=vec4(tint.rgb*a,a);return;}
              if(compositePass>0.5){gl_FragColor=vec4(c.rgb*compositeBrightness,c.a)*compositeAlpha;return;}
              if(postPass>0.5){
                vec3 rgb=c.a>0.0001?c.rgb/c.a:vec3(0.0);
                rgb*=exp2(grade.x);
                rgb=(rgb-0.5)*grade.y+0.5;
                float l=dot(rgb,vec3(0.2126,0.7152,0.0722));
                rgb=mix(vec3(l),rgb,grade.z);
                vec2 p=uv*2.0-1.0;
                rgb*=1.0-grade.w*smoothstep(0.3,1.4,length(p));
                gl_FragColor=vec4(clamp(rgb,0.0,1.0)*c.a,c.a);return;
              }
              if(maskPass>0.5){gl_FragColor=vec4(c.a);return;}
              c.rgb=c.rgb*multiplyColor.rgb;
              c.rgb=c.rgb+screenColor.rgb-c.rgb*screenColor.rgb;
              c.rgb*=tint.rgb;
              float m=texture2D(clipMask,gl_FragCoord.xy/size).a;
              if(inverted>0.5)m=1.0-m;
              c.a*=opacity*tint.a*mix(1.0,m,masked);
              gl_FragColor=vec4(c.rgb*c.a,c.a);
            }
            """);
        program=Fn<CreateProgram>("glCreateProgram")();Fn<Two>("glAttachShader")(program,vert);Fn<Two>("glAttachShader")(program,frag);Fn<One>("glLinkProgram")(program);Fn<GetIv>("glGetProgramiv")(program,0x8B82,out var linked);if(linked==0)throw new InvalidOperationException("Shader link failed");Fn<One>("glDeleteShader")(vert);Fn<One>("glDeleteShader")(frag);Fn<One>("glUseProgram")(program);
        Fn<UniformInt>("glUniform1i")(Loc("image"),0);Fn<UniformInt>("glUniform1i")(Loc("clipMask"),1);Fn<Uniform2>("glUniform2f")(Loc("size"),width,height);
        var hdr=FloatSurface(width,height);mainTexture=hdr.Texture;main=hdr.Framebuffer;maskTexture=Upload(new(width,height,[]));mask=Target(maskTexture);
        glDisable(0x0B44);glDisable(0x0B71);glDisable(0x0BD0);glDisable(0x809D);glEnable(0x0BE2);glViewport(0,0,width,height);
    }
    uint Target(uint texture){Fn<Gen>("glGenFramebuffers")(1,out var f);framebuffers.Add(f);Fn<Bind>("glBindFramebuffer")(0x8D40,f);Fn<Attach>("glFramebufferTexture2D")(0x8D40,0x8CE0,0x0DE1,texture,0);if(Fn<Status>("glCheckFramebufferStatus")(0x8D40)!=0x8CD5)throw new NotSupportedException("Incomplete framebuffer");return f;}
    public GlSurface Surface(int width,int height){uint texture=Upload(new(width,height,[]));return new(texture,Target(texture),width,height);}
    public GlSurface CurrentSurface=>new(drawTarget==layer?layerTexture:mainTexture,drawTarget,Width,Height);
    public void CopySurface(GlSurface source,GlSurface target,bool linear=false)
    {
        Fn<Bind>("glBindFramebuffer")(0x8CA8,source.Framebuffer);Fn<Bind>("glBindFramebuffer")(0x8CA9,target.Framebuffer);
        try{Fn<BlitFramebuffer>("glBlitFramebuffer")(0,0,source.Width,source.Height,0,0,target.Width,target.Height,0x4000,linear?0x2601u:0x2600u);}
        finally{Fn<Bind>("glBindFramebuffer")(0x8D40,drawTarget);glViewport(0,0,Width,Height);}
    }
    public GlSurface FloatSurface(int width,int height,int channels=4,float[]? pixels=null)
    {
        if(channels is not (1 or 4)||pixels is not null&&pixels.Length!=width*height*channels)throw new ArgumentException("Invalid floating render surface");
        uint texture=Upload(new(width,height,[]));fixed(float* p=pixels)glTexImage2D(0x0DE1,0,channels==1?0x822D:0x881A,width,height,0,channels==1?0x1903u:0x1908u,0x1406,pixels is null?0:(nint)p);
        return new(texture,Target(texture),width,height);
    }
    public void Blit(UnityShader shader,GlSurface target,IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,uint> textures)
    {
        Fn<Bind>("glBindFramebuffer")(0x8D40,target.Framebuffer);glViewport(0,0,target.Width,target.Height);glDisable(0x0BE2);
        var sheet=new Dictionary<string,float[]>(values);sheet.TryAdd("_GlobalMipBias",[0,0]);sheet.TryAdd("_BlitScaleBias",[1,1,0,0]);sheet.TryAdd("_RTHandleScale",[1,1,1,1]);
        try{shader.Draw(sheet,textures);}finally{Fn<One>("glUseProgram")(program);glEnable(0x0BE2);Fn<Bind>("glBindFramebuffer")(0x8D40,drawTarget);glViewport(0,0,Width,Height);TextureUnit(1,maskTexture);TextureUnit(0,0);}
    }
    public void Blur(UnityShaderLibrary shaders,float intensity,float radius)
    {
        if(intensity<=0)return;blurHalf??=Surface(Math.Max(1,(Width+1)/2),Math.Max(1,(Height+1)/2));var target=new GlSurface(drawTarget==layer?layerTexture:mainTexture,drawTarget,Width,Height);var half=blurHalf.Value;
        const string name="Adv/DistanceAdaptiveLensBlur";var values=shaders.Defaults(name);values["_BlurSize"]=[intensity];values["_BlurRadiusMax"]=[radius];values["_ScreenParams"]=[Width,Height,1+1f/Width,1+1f/Height];values["_BlitScaleBias"]=[1,1,0,0];values["_BlitScaleBiasRt"]=[1,1,0,0];
        values["_BlitTexture_TexelSize"]=[1f/Width,1f/Height,Width,Height];Blit(shaders.Program(name,0),half,values,new Dictionary<string,uint>{{"_BlitTexture",target.Texture}});
        values["_BlitTexture_TexelSize"]=[1f/half.Width,1f/half.Height,half.Width,half.Height];Blit(shaders.Program(name,1),target,values,new Dictionary<string,uint>{{"_BlitTexture",half.Texture}});
    }
    public void Material(UnityShader shader,IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,uint> textures,IReadOnlyDictionary<string,float[]> vertices,ushort[] indices,bool additive)
    {
        glEnable(0x0BE2);glBlendFunc(0x0302,additive?1u:0x0303);
        try{shader.DrawMesh(values,textures,vertices,indices);}finally{Fn<One>("glUseProgram")(program);TextureUnit(1,maskTexture);TextureUnit(0,0);}
    }
    public void InSurface(GlSurface surface,Action draw)
    {
        Fn<Bind>("glBindFramebuffer")(0x8D40,surface.Framebuffer);glViewport(0,0,surface.Width,surface.Height);glClearColor(0,0,0,0);ClearTarget();
        try{draw();}finally{Fn<Bind>("glBindFramebuffer")(0x8D40,drawTarget);glViewport(0,0,Width,Height);}
    }
    public sealed record StencilState(int Compare=8,int Pass=0,int Fail=0,int DepthFail=0,int Reference=0,int ReadMask=255,int WriteMask=255);
    public sealed record MaterialState(int BlendOp=0,int AlphaBlendOp=0,int ColorMask=15,int DepthTest=0,bool DepthWrite=false,float OffsetFactor=0,float OffsetUnits=0,StencilState? Stencil=null);
    public void GameMaterial(UnityShader shader,IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,uint> textures,IReadOnlyDictionary<string,float[]> vertices,ushort[] indices,int src,int dst,int srcAlpha,int dstAlpha,int cull=0,bool persistent=false,MaterialState? state=null)
        =>GameMaterialCore(shader,values,textures,vertices,indices,src,dst,srcAlpha,dstAlpha,cull,persistent,state);
    public void GameMaterial32(UnityShader shader,IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,uint> textures,IReadOnlyDictionary<string,float[]> vertices,uint[] indices,int src,int dst,int srcAlpha,int dstAlpha,int cull=0,bool persistent=false,MaterialState? state=null)
        =>GameMaterialCore(shader,values,textures,vertices,indices,src,dst,srcAlpha,dstAlpha,cull,persistent,state);
    void GameMaterialCore(UnityShader shader,IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,uint> textures,IReadOnlyDictionary<string,float[]> vertices,Array indices,int src,int dst,int srcAlpha,int dstAlpha,int cull,bool persistent,MaterialState? state)
    {
        uint Factor(int value)=>value switch{0=>0,1=>1,2=>0x0306,3=>0x0300,4=>0x0307,5=>0x0302,6=>0x0301,7=>0x0304,8=>0x0305,9=>0x0308,10=>0x0303,_=>throw new ArgumentOutOfRangeException(nameof(value))};
        uint Equation(int op)=>op switch{0=>0x8006,1=>0x800A,2=>0x800B,3=>0x8007,4=>0x8008,_=>throw new NotSupportedException("Unity blend operation: "+op)};
        uint Compare(int op)=>op switch{1=>0x0200,2=>0x0201,3=>0x0202,4=>0x0203,5=>0x0204,6=>0x0205,7=>0x0206,8=>0x0207,_=>throw new NotSupportedException("Unity depth comparison: "+op)};
        uint StencilOp(int op)=>op switch{0=>0x1E00,1=>0,2=>0x1E01,3=>0x1E02,4=>0x1E03,5=>0x150A,6=>0x8507,7=>0x8508,_=>throw new NotSupportedException("Unity stencil operation: "+op)};
        var s=state??new();uint equation=Equation(s.BlendOp),alphaEquation=Equation(s.AlphaBlendOp);
        Span<int> oldStencil=stackalloc int[8];
        bool changeStencil=s.Stencil is {} stencil&&(stencilDepth>0||stencil.Compare!=0&&(stencil.Compare!=8||stencil.Pass!=0||stencil.Fail!=0||stencil.DepthFail!=0));
        if(changeStencil){
            var st=s.Stencil!;
            ReadOnlySpan<uint> names=[0x0B90,0x0B92,0x0B97,0x0B93,0x0B98,0x0B94,0x0B95,0x0B96];
            for(int i=0;i<names.Length;i++){glGetIntegerv(names[i],out int value);oldStencil[i]=value;}
            if(st.Compare==0||st.Compare==8&&st.Pass==0&&st.Fail==0&&st.DepthFail==0)glDisable(0x0B90);
            else{EnsureDepthStencil();glEnable(0x0B90);glStencilFunc(Compare(st.Compare),st.Reference,(uint)st.ReadMask);glStencilMask((uint)st.WriteMask);glStencilOp(StencilOp(st.Fail),StencilOp(st.DepthFail),StencilOp(st.Pass));}
        }
        if(src==1&&dst==0&&srcAlpha==1&&dstAlpha==0)glDisable(0x0BE2);else{glEnable(0x0BE2);Fn<BlendSeparate>("glBlendFuncSeparate")(Factor(src),Factor(dst),Factor(srcAlpha),Factor(dstAlpha));Fn<Two>("glBlendEquationSeparate")(equation,alphaEquation);}
        if(state is not null)glColorMask((byte)((s.ColorMask&8)!=0?1:0),(byte)((s.ColorMask&4)!=0?1:0),(byte)((s.ColorMask&2)!=0?1:0),(byte)((s.ColorMask&1)!=0?1:0));
        if(s.DepthTest!=0&&(s.DepthTest!=8||s.DepthWrite)){EnsureDepthStencil();glEnable(0x0B71);glDepthFunc(Compare(s.DepthTest));}else glDisable(0x0B71);
        glDepthMask(s.DepthWrite?(byte)1:(byte)0);
        if(s.OffsetFactor!=0||s.OffsetUnits!=0){glEnable(0x8037);glPolygonOffset(s.OffsetFactor,s.OffsetUnits);}
        if(cull!=0){glEnable(0x0B44);glFrontFace(0x0900);glCullFace(cull==1?0x0404u:0x0405u);}
        try{if(indices is uint[] wide)shader.DrawMesh32(values,textures,vertices,wide,persistent);else shader.DrawMesh(values,textures,vertices,(ushort[])indices,persistent);}finally{
            if(changeStencil){if(oldStencil[0]!=0)glEnable(0x0B90);else glDisable(0x0B90);glStencilFunc((uint)oldStencil[1],oldStencil[2],(uint)oldStencil[3]);glStencilMask((uint)oldStencil[4]);glStencilOp((uint)oldStencil[5],(uint)oldStencil[6],(uint)oldStencil[7]);}
            glDisable(0x0B44);glDisable(0x0B71);glDisable(0x8037);glDepthMask(1);if(state is not null)glColorMask(1,1,1,1);Fn<Two>("glBlendEquationSeparate")(0x8006,0x8006);glEnable(0x0BE2);Fn<One>("glUseProgram")(program);TextureUnit(1,maskTexture);TextureUnit(0,0);}
    }
    void EnsureDepthStencil()
    {
        glGetIntegerv(0x8CA6,out int framebuffer);if(stencilBuffers.ContainsKey((uint)framebuffer))return;
        int* viewport=stackalloc int[4];glGetIntegerVector(0x0BA2,(nint)viewport);
        Fn<Gen>("glGenRenderbuffers")(1,out uint buffer);stencilBuffers.Add((uint)framebuffer,buffer);
        Fn<Bind>("glBindRenderbuffer")(0x8D41,buffer);Fn<RenderbufferStorage>("glRenderbufferStorage")(0x8D41,0x88F0,viewport[2],viewport[3]);
        Fn<FramebufferRenderbuffer>("glFramebufferRenderbuffer")(0x8D40,0x821A,0x8D41,buffer);
        if(Fn<Status>("glCheckFramebufferStatus")(0x8D40)!=0x8CD5)throw new NotSupportedException("Depth/stencil framebuffer is incomplete");
        glDepthMask(1);glClearDepth(1);glStencilMask(255);glClearStencil(0);glClear(0x500);
    }
    void ClearTarget(){glDepthMask(1);glStencilMask(255);glClearStencil(0);glClear(0x4500);}
    public uint Upload(Pixels image){glGenTextures(1,out var t);textures.Add(t);TextureUnit(0,t);glTexParameteri(0x0DE1,0x2801,0x2601);glTexParameteri(0x0DE1,0x2800,0x2601);glTexParameteri(0x0DE1,0x2802,0x812F);glTexParameteri(0x0DE1,0x2803,0x812F);UpdateTexture(t,image);return t;}
    public uint SolidCube(byte red,byte green,byte blue,byte alpha)
    {
        glGenTextures(1,out uint texture);textures.Add(texture);Fn<One>("glActiveTexture")(0x84C0);glBindTexture(0x8513,texture);
        byte* color=stackalloc byte[]{red,green,blue,alpha};
        for(uint face=0;face<6;face++)glTexImage2D(0x8515+face,0,0x8058,1,1,0,0x1908,0x1401,(nint)color);
        glTexParameteri(0x8513,0x2801,0x2601);glTexParameteri(0x8513,0x2800,0x2601);
        glTexParameteri(0x8513,0x2802,0x812F);glTexParameteri(0x8513,0x2803,0x812F);glTexParameteri(0x8513,0x8072,0x812F);
        glBindTexture(0x8513,0);return texture;
    }
    public uint UploadUnity(Pixels image,int filter,int wrapU,int wrapV,int mipCount)
    {
        if(filter is <0 or >2||wrapU is <0 or >2||wrapV is <0 or >2)throw new ArgumentException("Unsupported Unity texture sampler");
        // Unity UVs start at the bottom row. Flip storage rather than UVs, so shader tiling and offsets stay correct.
        var flipped=new byte[image.Rgba.Length];int stride=image.Width*4;for(int y=0;y<image.Height;y++)Array.Copy(image.Rgba,y*stride,flipped,(image.Height-1-y)*stride,stride);
        uint texture=Upload(new(image.Width,image.Height,flipped));int[] wrap=[0x2901,0x812F,0x8370];
        if(mipCount>1){glTexParameteri(0x0DE1,0x813D,mipCount-1);Fn<One>("glGenerateMipmap")(0x0DE1);}
        glTexParameteri(0x0DE1,0x2800,filter==0?0x2600:0x2601);
        glTexParameteri(0x0DE1,0x2801,mipCount>1?filter==0?0x2700:filter==1?0x2701:0x2703:filter==0?0x2600:0x2601);
        glTexParameteri(0x0DE1,0x2802,wrap[wrapU]);glTexParameteri(0x0DE1,0x2803,wrap[wrapV]);return texture;
    }
    public void SetTextureSampler(uint texture,int minFilter,int magFilter,int wrapU,int wrapV,bool generateMipmaps=false)
    {
        if(minFilter is not (0x2600 or 0x2601 or 0x2700 or 0x2701 or 0x2702 or 0x2703)||magFilter is not (0x2600 or 0x2601)||wrapU is not (0x2901 or 0x812F or 0x8370)||wrapV is not (0x2901 or 0x812F or 0x8370))throw new ArgumentException("Invalid OpenGL texture sampler");
        TextureUnit(0,texture);
        if(generateMipmaps)Fn<One>("glGenerateMipmap")(0x0DE1);
        glTexParameteri(0x0DE1,0x2801,minFilter);glTexParameteri(0x0DE1,0x2800,magFilter);
        glTexParameteri(0x0DE1,0x2802,wrapU);glTexParameteri(0x0DE1,0x2803,wrapV);
    }
    public void UpdateTexture(uint texture,Pixels image){TextureUnit(0,texture);fixed(byte* p=image.Rgba)glTexImage2D(0x0DE1,0,0x8058,image.Width,image.Height,0,0x1908,0x1401,image.Rgba.Length==0?0:(nint)p);}
    public void RepeatTexture(uint texture){TextureUnit(0,texture);glTexParameteri(0x0DE1,0x2802,0x2901);glTexParameteri(0x0DE1,0x2803,0x2901);}
    public void Begin(float[] color){Float("postPass",0);Float("compositePass",0);drawTarget=main;Fn<Bind>("glBindFramebuffer")(0x8D40,main);glClearColor(color[0]*color[3],color[1]*color[3],color[2]*color[3],color[3]);ClearTarget();}
    public void BeginLayer()
    {
        if(drawTarget!=main)throw new InvalidOperationException("Nested render layers are unsupported");
        if(layer==0){var hdr=FloatSurface(Width,Height);layerTexture=hdr.Texture;layer=hdr.Framebuffer;}
        drawTarget=layer;Fn<Bind>("glBindFramebuffer")(0x8D40,layer);glClearColor(0,0,0,0);ClearTarget();
    }
    public void EndLayer(float alpha=1,float brightness=1)
    {
        if(drawTarget!=layer||layer==0)throw new InvalidOperationException("No active render layer");
        drawTarget=main;Fn<Bind>("glBindFramebuffer")(0x8D40,main);Composite(layerTexture,alpha,brightness);
    }
    public uint Capture(uint texture=0)
    {
        if(texture==0)texture=Upload(new(Width,Height,[]));
        Fn<Bind>("glBindFramebuffer")(0x8D40,main);TextureUnit(0,texture);glCopyTexSubImage2D(0x0DE1,0,0,0,0,0,Width,Height);return texture;
    }
    public void Composite(uint texture,float alpha,float brightness=1)
    {
        TextureUnit(0,texture);Float("compositePass",1);Float("compositeAlpha",alpha);Float("compositeBrightness",brightness);
        Fn<BlendSeparate>("glBlendFuncSeparate")(1,0x0303,1,0x0303);glBegin(7);
        glTexCoord2f(0,0);glVertex2f(-1,-1);glTexCoord2f(1,0);glVertex2f(1,-1);glTexCoord2f(1,1);glVertex2f(1,1);glTexCoord2f(0,1);glVertex2f(-1,1);glEnd();Float("compositePass",0);
    }
    public void BeginMask(){TextureUnit(1,0);Fn<Bind>("glBindFramebuffer")(0x8D40,mask);glClearColor(0,0,0,0);ClearTarget();Float("maskPass",1);glBlendFunc(1,0x0303);}
    // uGUI Mask nesting. The callback must draw the same mask geometry on entry and exit.
    public void WithStencilMask(Action drawMask,Action drawContent,bool showMaskGraphic=false)
    {
        if(stencilDepth>=255)throw new NotSupportedException("UI mask nesting exceeds stencil capacity");
        EnsureDepthStencil();
        if(stencilDepth==0){glStencilMask(255);glClearStencil(0);glClear(0x400);}
        void Restore(){glColorMask(1,1,1,1);Float("uiStencilPass",0);glStencilMask(0);glStencilOp(0x1E00,0x1E00,0x1E00);glStencilFunc(0x0202,stencilDepth,255);if(stencilDepth==0)glDisable(0x0B90);}
        glEnable(0x0B90);glStencilMask(255);glStencilFunc(0x0202,stencilDepth,255);glStencilOp(0x1E00,0x1E00,0x1E02);
        byte color=showMaskGraphic?(byte)1:(byte)0;glColorMask(color,color,color,color);Float("uiStencilPass",1);
        try{drawMask();}catch{Restore();throw;}
        stencilDepth++;Restore();
        try{drawContent();}
        finally{
            glColorMask(0,0,0,0);glStencilMask(255);glStencilFunc(0x0202,stencilDepth,255);glStencilOp(0x1E00,0x1E00,0x1E03);Float("uiStencilPass",1);
            try{drawMask();}finally{stencilDepth--;Restore();}
        }
    }
    public void EndMask(){Fn<Bind>("glBindFramebuffer")(0x8D40,drawTarget);TextureUnit(1,maskTexture);Float("maskPass",0);}
    public void Draw(uint texture,float[] xy,float[] uv,ushort[] indices,float opacity,float[] multiply,float[] screen,float[] tint,int blend,bool masked,bool inverted,bool maskPass=false,float[]? colors=null)
    {
        TextureUnit(0,texture);Float("opacity",opacity);Float("masked",masked?1:0);Float("inverted",inverted?1:0);Float("maskPass",maskPass?1:0);Vec4("multiplyColor",multiply[0],multiply[1],multiply[2],multiply[3]);Vec4("screenColor",screen[0],screen[1],screen[2],screen[3]);Vec4("tint",tint[0],tint[1],tint[2],tint[3]);
        if(maskPass)glBlendFunc(1,0x0303);else Fn<BlendSeparate>("glBlendFuncSeparate")(blend==2?0x0306u:1,blend==1?1u:0x0303,1,0x0303);
        bool homogeneous=xy.Length==uv.Length*2;
        glColor4f(1,1,1,1);glBegin(4);foreach(var i in indices){if(colors is not null)glColor4f(colors[i*4],colors[i*4+1],colors[i*4+2],colors[i*4+3]);glTexCoord2f(uv[i*2],uv[i*2+1]);if(homogeneous)glVertex4f(xy[i*4],xy[i*4+1],xy[i*4+2],xy[i*4+3]);else glVertex2f(xy[i*2],xy[i*2+1]);}glEnd();glColor4f(1,1,1,1);
    }
    public void Text(uint texture,float[] xy,float[] uv,float[] color)
    {Float("textPass",1);Draw(texture,xy,uv,[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],color,0,false,false);Float("textPass",0);}
    public void Rule(uint texture,float value,bool gradient,float[] color)
    {Float("rulePass",1);Float("ruleValue",value);Float("ruleGradient",gradient?1:0);Draw(texture,[-1,-1,1,-1,1,1,-1,1],[0,1,1,1,1,0,0,0],[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],color,0,false,false);Float("rulePass",0);}
    GlSurface? presentation,resolvedPresentation;
    byte[] readbackScratch=[];
    byte[] ReadbackStorage(int length)
    {
        if(readbackScratch.Length!=length)readbackScratch=GC.AllocateUninitializedArray<byte>(length);
        return readbackScratch;
    }
    UnityShader? resolveShader;
    const uint PixelPackBuffer=0x88EB;
    readonly Queue<(uint Buffer,int Width,int Height)> pendingPixels=[];
    readonly Stack<uint> freePixelBuffers=[];
    readonly List<uint> pixelBuffers=[];
    public int PendingPixels=>pendingPixels.Count;
    const string ResolveSource="""
        #ifdef VERTEX
        #version 330
        in vec4 in_POSITION0;
        void main(){gl_Position=in_POSITION0;}
        #endif
        #ifdef FRAGMENT
        #version 330
        uniform sampler2D sourceImage;
        uniform int scale;
        out vec4 outputColor;
        void main(){
            ivec2 origin=ivec2(gl_FragCoord.xy)*scale;
            ivec4 sum=ivec4(0);
            for(int y=0;y<scale;y++)for(int x=0;x<scale;x++)
                sum+=ivec4(floor(texelFetch(sourceImage,origin+ivec2(x,y),0)*255.0+0.5));
            int count=scale*scale;
            ivec3 rgb=sum.a>0?min(ivec3(255),(sum.rgb*255+sum.a/2)/sum.a):ivec3(0);
            outputColor=vec4(rgb,(sum.a+count/2)/count)/255.0;
        }
        #endif
        """;
    Pixels? ResolvePresentation(GlSurface source,int width,int height,int scale,bool queued,bool opaque=false)
    {
        var target=source;
        // The opaque 1x presentation is already an 8-bit straight-alpha image.
        // Re-resolving it would perform an identical fullscreen pass and GPU round trip.
        if(!opaque||scale!=1)
        {
            resolvedPresentation??=Surface(width,height);resolveShader??=new("Integer area resolve",ResolveSource);
            target=resolvedPresentation.Value;
            Blit(resolveShader,target,new Dictionary<string,float[]>{{"scale",[scale]}},new Dictionary<string,uint>{{"sourceImage",source.Texture}});
        }
        Fn<Bind>("glBindFramebuffer")(0x8D40,target.Framebuffer);
        if(queued){
            uint buffer;if(freePixelBuffers.Count>0)buffer=freePixelBuffers.Pop();else{Fn<Gen>("glGenBuffers")(1,out buffer);pixelBuffers.Add(buffer);}
            Fn<Bind>("glBindBuffer")(PixelPackBuffer,buffer);
            try{Fn<BufferData>("glBufferData")(PixelPackBuffer,checked(width*height*4),0,0x88E1);glReadPixelsBuffer(0,0,width,height,0x1908,0x1401,0);pendingPixels.Enqueue((buffer,width,height));}
            finally{Fn<Bind>("glBindBuffer")(PixelPackBuffer,0);}
            return null;
        }
        var pixels=ReadbackStorage(checked(width*height*4));glReadPixels(0,0,width,height,0x1908,0x1401,pixels);
        var result=GC.AllocateUninitializedArray<byte>(pixels.Length);int stride=width*4;
        for(int y=0;y<height;y++)Buffer.BlockCopy(pixels,(height-1-y)*stride,result,y*stride,stride);
        return new(width,height,result);
    }
    public Pixels Present(int width,int height,int supersample,int viewportY,Action<int,int,int> drawBands,bool transparent=false)
        =>PresentCore(width,height,supersample,viewportY,drawBands,false,transparent)!;
    public void QueuePresent(int width,int height,int supersample,int viewportY,Action<int,int,int> drawBands,bool transparent=false)
    {
        if(PendingPixels>=3)throw new InvalidOperationException("Read a queued frame before submitting another");
        PresentCore(width,height,supersample,viewportY,drawBands,true,transparent);
    }
    public Pixels ReadQueued()
    {
        if(!pendingPixels.TryPeek(out var frame))throw new InvalidOperationException("No queued frame");
        int stride=checked(frame.Width*4),length=checked(stride*frame.Height);var result=new byte[length];
        Fn<Bind>("glBindBuffer")(PixelPackBuffer,frame.Buffer);
        try{
            nint pointer=Fn<MapBufferRange>("glMapBufferRange")(PixelPackBuffer,0,length,1);
            if(pointer==0)throw new IOException("GPU pixel buffer could not be mapped");
            try{for(int y=0;y<frame.Height;y++)Marshal.Copy(pointer+(frame.Height-1-y)*stride,result,y*stride,stride);}
            finally{if(Fn<UnmapBuffer>("glUnmapBuffer")(PixelPackBuffer)==0)throw new IOException("GPU pixel buffer contents became invalid");}
            pendingPixels.Dequeue();freePixelBuffers.Push(frame.Buffer);return new(frame.Width,frame.Height,result);
        }finally{Fn<Bind>("glBindBuffer")(PixelPackBuffer,0);}
    }
    Pixels? PresentCore(int width,int height,int supersample,int viewportY,Action<int,int,int> drawBands,bool queued,bool transparent)
    {
        int sw=checked(width*supersample),sh=checked(height*supersample);
        if(presentation is null)presentation=Surface(sw,sh);
        var target=presentation.Value;if(target.Width!=sw||target.Height!=sh)throw new ArgumentException("Presentation dimensions cannot change");
        Fn<Bind>("glBindFramebuffer")(0x8D40,target.Framebuffer);glViewport(0,0,sw,sh);glClearColor(0,0,0,transparent?0:1);ClearTarget();
        try {
            glViewport(0,viewportY*supersample,Width,Height);Composite(mainTexture,1);
            glViewport(0,0,sw,sh);drawBands(sw,sh,Height);
            if(!queued&&TryGpuPresentation(target,width,height,supersample))return new Pixels(width,height,[]);
            return ResolvePresentation(target,width,height,supersample,queued,!transparent);
        }finally{Fn<Bind>("glBindFramebuffer")(0x8D40,drawTarget);glViewport(0,0,Width,Height);}
    }
    public Pixels Read(int width,int height)=>ReadPixels(width,height,Width,Height);
    Pixels ReadPixels(int width,int height,int sourceWidth,int sourceHeight)
    {
        if(width<=0||height<=0||sourceWidth%width!=0||sourceHeight%height!=0||sourceWidth/width!=sourceHeight/height)throw new ArgumentException("Readback requires uniform integer downsampling");
        var pixels=ReadbackStorage(checked(sourceWidth*sourceHeight*4));glReadPixels(0,0,sourceWidth,sourceHeight,0x1908,0x1401,pixels);
        var result=new byte[checked(width*height*4)];int scale=sourceWidth/width;
        if(scale==1&&BitConverter.IsLittleEndian)
        {
            // Most scene pixels are fully opaque; transparent character previews also
            // have large empty areas. Resolve these blocks without per-channel division.
            Parallel.For(0,height,y=>ResolveOneRow(pixels.AsSpan((height-1-y)*width*4,width*4),result.AsSpan(y*width*4,width*4)));
            return new(width,height,result);
        }
        // Area resolve in premultiplied space, then emit straight-alpha PNG/rawvideo.
        Parallel.For(0,height,y=>{for(int x=0;x<width;x++){int r=0,g=0,b=0,a=0;for(int dy=0;dy<scale;dy++)for(int dx=0;dx<scale;dx++){int p=((sourceHeight-1-(y*scale+dy))*sourceWidth+x*scale+dx)*4;r+=pixels[p];g+=pixels[p+1];b+=pixels[p+2];a+=pixels[p+3];}int d=(y*width+x)*4;if(a>0){result[d]=(byte)Math.Min(255,(r*255+a/2)/a);result[d+1]=(byte)Math.Min(255,(g*255+a/2)/a);result[d+2]=(byte)Math.Min(255,(b*255+a/2)/a);}result[d+3]=(byte)((a+scale*scale/2)/(scale*scale));}});
        return new(width,height,result);
    }
    static void ResolveOneRow(ReadOnlySpan<byte> source,Span<byte> destination)
    {
        var input=MemoryMarshal.Cast<byte,uint>(source);var output=MemoryMarshal.Cast<byte,uint>(destination);int i=0;
        var mask=new System.Numerics.Vector<uint>(255);
        if(System.Numerics.Vector.IsHardwareAccelerated)for(;i<=input.Length-System.Numerics.Vector<uint>.Count;i+=System.Numerics.Vector<uint>.Count)
        {
            var value=new System.Numerics.Vector<uint>(input.Slice(i));var alpha=value>>24;
            if(System.Numerics.Vector.EqualsAll(alpha,mask)){value.CopyTo(output.Slice(i));continue;}
            if(System.Numerics.Vector.EqualsAll(alpha,System.Numerics.Vector<uint>.Zero)){System.Numerics.Vector<uint>.Zero.CopyTo(output.Slice(i));continue;}
            for(int j=0;j<System.Numerics.Vector<uint>.Count;j++)output[i+j]=ResolvePixel(input[i+j]);
        }
        for(;i<input.Length;i++)output[i]=ResolvePixel(input[i]);
    }
    static uint ResolvePixel(uint value)
    {
        uint alpha=value>>24;if(alpha==0)return 0;if(alpha==255)return value;
        uint r=Math.Min(255,((value&255)*255+alpha/2)/alpha);
        uint g=Math.Min(255,(((value>>8)&255)*255+alpha/2)/alpha);
        uint b=Math.Min(255,(((value>>16)&255)*255+alpha/2)/alpha);
        return r|(g<<8)|(b<<16)|(alpha<<24);
    }
    public void Grade(GradeSpec grade)
    {
        if(grade.Exposure==0&&grade.Contrast==1&&grade.Saturation==1&&grade.Vignette==0)return;
        TextureUnit(1,0);Fn<Bind>("glBindFramebuffer")(0x8D40,mask);TextureUnit(0,mainTexture);Float("postPass",1);Vec4("grade",grade.Exposure,grade.Contrast,grade.Saturation,grade.Vignette);glDisable(0x0BE2);glBegin(7);glTexCoord2f(0,0);glVertex2f(-1,-1);glTexCoord2f(1,0);glVertex2f(1,-1);glTexCoord2f(1,1);glVertex2f(1,1);glTexCoord2f(0,1);glVertex2f(-1,1);glEnd();glEnable(0x0BE2);Float("postPass",0);
    }
    public void GradeScene(GradeSpec grade)
    {
        if(grade.Exposure==0&&grade.Contrast==1&&grade.Saturation==1&&grade.Vignette==0)return;
        Grade(grade);CopySurface(new(maskTexture,mask,Width,Height),CurrentSurface);
    }
    bool disposed;
    public void Dispose(){if(disposed)return;disposed=true;gpuSurface?.Dispose();gpuSurface=null;gpuCompositeShader?.Dispose();resolveShader?.Dispose();foreach(var buffer in stencilBuffers.Values){var b=buffer;Fn<Delete>("glDeleteRenderbuffers")(1,ref b);}foreach(var buffer in pixelBuffers){var b=buffer;Fn<Delete>("glDeleteBuffers")(1,ref b);}foreach(var framebuffer in framebuffers){var f=framebuffer;Fn<Delete>("glDeleteFramebuffers")(1,ref f);}foreach(var texture in textures){var t=texture;glDeleteTextures(1,ref t);}Fn<One>("glDeleteProgram")(program);if(linuxContext is not null)linuxContext.Dispose();else{wglMakeCurrent(0,0);wglDeleteContext(rc);ReleaseDC(window,dc);DestroyWindow(window);}}
}
