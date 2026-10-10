using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// ABI of Cubism MotionSync CRI 5.0.4, with the original model's phoneme mappings.
public sealed unsafe class NativeMotionSync : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]struct Mapping {public nint Id,Names,Values;public uint Count;public float Scale;public uint Enabled;}
    [StructLayout(LayoutKind.Sequential)]struct Result {public nint Values;public int Count;public uint Processed;}
    [StructLayout(LayoutKind.Sequential)]struct Config {public float Blend;public int Smooth;public float Emphasis;}
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate uint Version();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate int Initialize(nint config);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate nint Create(nint config,nint mappings,int count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Delete(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate uint Require(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate int Analyze(nint context,nint pcm,uint count,ref Result result,ref Config config);
    sealed class Engine
    {
        public readonly nint Library;public readonly uint EngineVersion;public readonly Create Create;public readonly Delete Delete;public readonly Require Require;public readonly Analyze Analyze;
        public Engine(string path){Library=NativeLibrary.Load(path);T Fn<T>(string name)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library,"csmMotionSync_"+name));
            EngineVersion=Fn<Version>("GetEngineVersion")();if(EngineVersion!=0x05000004)throw new InvalidDataException("Expected MotionSync 5.0.4, got "+EngineVersion.ToString("x"));
            if(Fn<Initialize>("InitializeEngine")(0)!=1)throw new InvalidOperationException("MotionSync initialization failed");Create=Fn<Create>("CreateContext");Delete=Fn<Delete>("DeleteContext");Require=Fn<Require>("GetRequireSampleCount");Analyze=Fn<Analyze>("Analyze");}
    }
    static readonly Dictionary<string,Engine> engines=[];
    static Engine GetEngine(string dll)
    {
        dll=Path.GetFullPath(dll);
        lock(engines){if(!engines.TryGetValue(dll,out var engine))engines[dll]=engine=new(dll);return engine;}
    }
    public static uint ProbeVersion(string dll)=>GetEngine(dll).EngineVersion;
    readonly Engine engine;readonly List<nint> allocations=[];nint context;
    Config config;readonly float[] result;float[] pending=new float[8192];int pendingCount;
    public NativeMotionSync(string dll,JsonNode setting)
    {
        engine=GetEngine(dll);
        try{
            var maps=setting["Mappings"]!.AsArray();var native=new Mapping[maps.Count];
            for(int k=0;k<maps.Count;k++){var m=maps[k]!;var targets=m["Targets"]!.AsArray();var names=new nint[targets.Count];var values=new float[targets.Count];
                for(int i=0;i<targets.Count;i++){string path=targets[i]?["Parameter"] is JsonNode parameter?Archive.S(parameter,"gameObject"):"";names[i]=String(path[(path.LastIndexOf('/')+1)..]);values[i]=Actor.N(targets[i]!["Value"]);}
                var audio=setting["AudioParameters"]![k]!;native[k]=new(){Id=String(Archive.S(m,"AudioParameterId")),Names=Alloc(names.Length*IntPtr.Size),Values=Alloc(values.Length*4),Count=(uint)names.Length,Scale=Actor.N(audio["Scale"]),Enabled=Actor.B(audio["Enabled"])?1u:0};Marshal.Copy(names,0,native[k].Names,names.Length);Marshal.Copy(values,0,native[k].Values,values.Length);
            }
            // The native context retains mapping pointers. Keep the struct array and configuration alive too.
            nint mapMemory=Alloc(native.Length*sizeof(Mapping)),cfg=Alloc(8);
            fixed(Mapping* mappings=native)Buffer.MemoryCopy(mappings,(void*)mapMemory,native.Length*sizeof(Mapping),native.Length*sizeof(Mapping));
            Marshal.WriteInt32(cfg,48000);Marshal.WriteInt32(cfg,4,32);context=engine.Create(cfg,mapMemory,native.Length);if(context==0)throw new InvalidOperationException("MotionSync context creation failed");
            result=new float[setting["CubismParameters"]!.AsArray().Count];config=new(){Blend=Actor.N(setting["PostProcessing"]!["BlendRatio"]),Smooth=(int)Actor.N(setting["PostProcessing"]!["Smoothing"]),Emphasis=Actor.N(setting["EmphasisLevel"])};
        }catch{Dispose();throw;}
    }
    nint Alloc(int bytes){nint p=Marshal.AllocHGlobal(bytes);allocations.Add(p);return p;}
    nint String(string s){nint p=Marshal.StringToHGlobalAnsi(s);allocations.Add(p);return p;}
    public void ResetInput()=>pendingCount=0;
    public void Feed(ReadOnlySpan<float> pcm,Action<float[]> receive,bool analyze=true)
    {
        if(pending.Length<pendingCount+pcm.Length)Array.Resize(ref pending,Math.Max(pending.Length*2,pendingCount+pcm.Length));pcm.CopyTo(pending.AsSpan(pendingCount));pendingCount+=pcm.Length;
        if(!analyze)return;
        int offset=0;uint required=engine.Require(context);if(required==0)throw new InvalidDataException("MotionSync requires zero samples");
        while(offset+required<pendingCount){fixed(float* input=pending)fixed(float* output=result){var r=new Result{Values=(nint)output,Count=result.Length};if(engine.Analyze(context,(nint)(input+offset),required,ref r,ref config)!=1)throw new InvalidDataException("MotionSync analysis failed");}
            receive(result);offset+=(int)required;required=engine.Require(context);if(required==0)throw new InvalidDataException("MotionSync requires zero samples");}
        if(offset>0){Array.Copy(pending,offset,pending,0,pendingCount-offset);pendingCount-=offset;}
    }
    public void Dispose(){if(context!=0){engine.Delete(context);context=0;}foreach(nint p in allocations)Marshal.FreeHGlobal(p);allocations.Clear();}
}
