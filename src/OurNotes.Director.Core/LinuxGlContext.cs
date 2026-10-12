using System.Runtime.InteropServices;

namespace OurNotes.Director.Core;

// Desktop OpenGL through EGL; no window system, browser or GLES emulation.
internal sealed class LinuxGlContext : IDisposable
{
    const string Egl="libEGL.so.1";
    static readonly nint gl=NativeLibrary.Load("libGL.so.1");
    [DllImport(Egl)] static extern nint eglGetPlatformDisplay(uint platform,nint display,nint attributes);
    [DllImport(Egl)] static extern int eglInitialize(nint display,out int major,out int minor);
    [DllImport(Egl)] static extern int eglBindAPI(uint api);
    [DllImport(Egl)] static extern int eglChooseConfig(nint display,int[] attributes,out nint config,int size,out int count);
    [DllImport(Egl)] static extern nint eglCreatePbufferSurface(nint display,nint config,int[] attributes);
    [DllImport(Egl)] static extern nint eglCreateContext(nint display,nint config,nint shared,int[] attributes);
    [DllImport(Egl)] static extern int eglMakeCurrent(nint display,nint draw,nint read,nint context);
    [DllImport(Egl)] static extern int eglDestroyContext(nint display,nint context);
    [DllImport(Egl)] static extern int eglDestroySurface(nint display,nint surface);
    [DllImport(Egl)] static extern int eglTerminate(nint display);
    [DllImport(Egl)] static extern int eglGetError();
    [DllImport(Egl,CharSet=CharSet.Ansi)] static extern nint eglGetProcAddress(string name);
    nint display,surface,context;
    static bool resolverInstalled;
    static readonly object displayGate=new();
    static nint sharedDisplay;
    static int displayUsers;
    public static void Configure()
    {
        lock(displayGate)
        {
            if(resolverInstalled)return;
            NativeLibrary.SetDllImportResolver(typeof(LinuxGlContext).Assembly,(name,assembly,path)=>name=="opengl32.dll"?gl:0);
            resolverInstalled=true;
        }
    }
    static nint AcquireDisplay()
    {
        lock(displayGate)
        {
            if(displayUsers==0)
            {
                var opened=eglGetPlatformDisplay(0x31DD,0,0); // EGL_PLATFORM_SURFACELESS_MESA
                if(opened==0||eglInitialize(opened,out _,out _)==0)throw Failure("EGL initialize");
                sharedDisplay=opened;
            }
            displayUsers++;return sharedDisplay;
        }
    }
    static void ReleaseDisplay(nint display)
    {
        lock(displayGate)
        {
            if(--displayUsers==0){eglTerminate(display);sharedDisplay=0;}
        }
    }
    public static nint Proc(string name)=>NativeLibrary.TryGetExport(gl,name,out var p)?p:eglGetProcAddress(name);
    static Exception Failure(string operation)=>new NotSupportedException($"{operation} failed (EGL 0x{eglGetError():X}); desktop OpenGL 3.3 compatibility and EGL surfaceless support are required");
    public LinuxGlContext()
    {
        Configure();
        try{
            display=AcquireDisplay();
            if(eglBindAPI(0x30A2)==0)throw Failure("EGL OpenGL API");
            if(eglChooseConfig(display,[0x3033,1,0x3040,8,0x3024,8,0x3023,8,0x3022,8,0x3021,8,0x3038],out var config,1,out int count)==0||count==0)throw Failure("EGL config");
            surface=eglCreatePbufferSurface(display,config,[0x3057,1,0x3056,1,0x3038]);
            context=eglCreateContext(display,config,0,[0x3098,3,0x30FB,3,0x30FD,2,0x3038]);
            if(surface==0||context==0||eglMakeCurrent(display,surface,surface,context)==0)throw Failure("EGL context");
        }catch{Dispose();throw;}
    }
    public void Dispose(){if(display==0)return;eglMakeCurrent(display,0,0,0);if(context!=0)eglDestroyContext(display,context);if(surface!=0)eglDestroySurface(display,surface);ReleaseDisplay(display);display=surface=context=0;}
}
