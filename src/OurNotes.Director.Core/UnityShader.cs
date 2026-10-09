// Compile the game's exported shader arithmetic directly on desktop OpenGL.
// No JavaScript, browser, WebGL context, or translation runtime is involved.
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed unsafe class UnityShader : IDisposable
{
    const string Gl="opengl32.dll";
    [DllImport(Gl,CharSet=CharSet.Ansi)]static extern nint wglGetProcAddress(string name);
    [DllImport(Gl)]static extern void glBindTexture(uint target,uint texture);
    [DllImport(Gl)]static extern void glDrawArrays(uint mode,int first,int count);
    [DllImport(Gl)]static extern void glDrawElements(uint mode,int count,uint type,nint indices);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate uint Create(uint type);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate uint CreateProgram();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void One(uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Two(uint a,uint b);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Source(uint s,int count,nint source,nint lengths);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Get(uint id,uint key,out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Log(uint id,int size,out int length,StringBuilder output);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Active(uint id,uint index,int size,out int length,out int count,out uint type,StringBuilder name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate int Location(uint program,[MarshalAs(UnmanagedType.LPStr)]string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Uniform(int location,int count,nint values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void UniformInt(int location,int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Gen(int count,out uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Delete(int count,ref uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Data(uint target,nint size,nint data,uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Pointer(uint index,int size,uint type,byte normalized,int stride,nint offset);
    static readonly Dictionary<string,Delegate> functions=[];
    static T Fn<T>(string name)where T:Delegate
    {
        if(!functions.TryGetValue(name,out var f)){nint p=OperatingSystem.IsLinux()?LinuxGlContext.Proc(name):wglGetProcAddress(name);if(p is 0 or 1 or 2 or 3 or -1)throw new NotSupportedException("Missing desktop GL function: "+name);functions[name]=f=Marshal.GetDelegateForFunctionPointer<T>(p);}return(T)f;
    }
    readonly uint program,vao;readonly List<uint> buffers=[];
    readonly List<(string Name,int Components,uint Buffer,float[] Default)> attributes=[];
    public int AttributeComponents(string name)=>attributes.FirstOrDefault(a=>a.Name==name).Components;
    sealed class MeshAttribute {public required string Name;public uint Buffer;public float[] Previous=[];}
    sealed class MeshBuffers {public uint Vao,Indices;public Array PreviousIndices=Array.Empty<ushort>();public List<MeshAttribute> Attributes=[];}
    readonly Dictionary<IReadOnlyDictionary<string,float[]>,MeshBuffers> meshBuffers=new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string,float[]> transientMeshKey=[];
    readonly List<(string Name,int Location,uint Type,int Count)> uniforms=[];
    readonly Dictionary<int,float[]> uniformCache=[];
    public string Label{get;}
    public string[] Properties=>uniforms.Select(u=>u.Name).ToArray();
    public static (string Vertex,string Fragment) DesktopSources(string packed)
    {
        var blocks=new Dictionary<string,string>();string? current=null;int depth=0;var b=new StringBuilder();
        foreach(string line in packed.Split('\n')){string trimmed=line.Trim();if(depth==0&&trimmed is "#ifdef VERTEX" or "#ifdef FRAGMENT"){current=trimmed[7..];depth=1;b.Clear();continue;}if(current is null)continue;
            if(Regex.IsMatch(trimmed,@"^#\s*if"))depth++;else if(Regex.IsMatch(trimmed,@"^#\s*endif")&&--depth==0){blocks[current]=b.ToString();current=null;continue;}b.AppendLine(line);
        }
        string ConvertStage(string key){if(!blocks.TryGetValue(key,out var text))throw new InvalidDataException("Missing packed shader stage: "+key);
            text=text.Replace("#version 300 es","#version 330",StringComparison.Ordinal).Replace("#define HLSLCC_ENABLE_UNIFORM_BUFFERS 1","#define HLSLCC_ENABLE_UNIFORM_BUFFERS 0",StringComparison.Ordinal).Replace("#define UNITY_SUPPORTS_UNIFORM_LOCATION 1","#define UNITY_SUPPORTS_UNIFORM_LOCATION 0",StringComparison.Ordinal);
            text=Regex.Replace(text,@"\bprecision\s+(?:highp|mediump|lowp)\s+\w+\s*;","");return Regex.Replace(text,@"\b(?:highp|mediump|lowp)\b","");}
        return(ConvertStage("VERTEX"),ConvertStage("FRAGMENT"));
    }
    public UnityShader(string label,string packed)
    {
        Label=label;var stages=DesktopSources(packed);
        uint Compile(uint type,string text){uint s=Fn<Create>("glCreateShader")(type);nint pointer=Marshal.StringToHGlobalAnsi(text);try{Fn<Source>("glShaderSource")(s,1,(nint)(&pointer),0);}finally{Marshal.FreeHGlobal(pointer);}Fn<One>("glCompileShader")(s);Fn<Get>("glGetShaderiv")(s,0x8B81,out int ok);if(ok==0){var log=new StringBuilder(16000);Fn<Log>("glGetShaderInfoLog")(s,16000,out _,log);Fn<One>("glDeleteShader")(s);throw new InvalidDataException(label+": "+log);}return s;}
        uint vertex=Compile(0x8B31,stages.Vertex),fragment=0;
        try{fragment=Compile(0x8B30,stages.Fragment);program=Fn<CreateProgram>("glCreateProgram")();Fn<Two>("glAttachShader")(program,vertex);Fn<Two>("glAttachShader")(program,fragment);Fn<One>("glLinkProgram")(program);Fn<Get>("glGetProgramiv")(program,0x8B82,out int ok);if(ok==0){var log=new StringBuilder(16000);Fn<Log>("glGetProgramInfoLog")(program,16000,out _,log);throw new InvalidDataException(label+": "+log);}}
        finally{Fn<One>("glDeleteShader")(vertex);if(fragment!=0)Fn<One>("glDeleteShader")(fragment);}
        Fn<Get>("glGetProgramiv")(program,0x8B86,out int count);
        for(uint i=0;i<count;i++){var name=new StringBuilder(1024);Fn<Active>("glGetActiveUniform")(program,i,1024,out _,out int size,out uint type,name);string raw=name.ToString(),property=Regex.Replace(raw,@"\[0\]$","");if(property.StartsWith("Xhlslcc_UnusedX",StringComparison.Ordinal))continue;if(property.StartsWith("hlslcc_mtx4x4",StringComparison.Ordinal))property=property[13..];uniforms.Add((property,Fn<Location>("glGetUniformLocation")(program,raw),type,size));}
        Fn<Gen>("glGenVertexArrays")(1,out vao);Fn<One>("glBindVertexArray")(vao);
        Fn<Get>("glGetProgramiv")(program,0x8B89,out int attributeCount);
        for(uint i=0;i<attributeCount;i++){
            var name=new StringBuilder(1024);Fn<Active>("glGetActiveAttrib")(program,i,1024,out _,out _,out uint attributeType,name);int location=Fn<Location>("glGetAttribLocation")(program,name.ToString());
            if(location<0)continue;
            float[] values;int components;
            switch(name.ToString()){
                case "in_POSITION0":values=[-1,-1,0,1,3,-1,0,1,-1,3,0,1];components=4;break;
                case "in_TEXCOORD0":components=attributeType==0x8B52?4:2;values=components==4?[0,0,0,0,2,0,0,0,0,2,0,0]:[0,0,2,0,0,2];break;
                case "in_COLOR0":values=Enumerable.Repeat(1f,12).ToArray();components=4;break;
                default:components=attributeType switch{0x1406=>1,0x8B50=>2,0x8B51=>3,0x8B52=>4,_=>throw new NotSupportedException(label+": unsupported attribute type "+attributeType)};values=new float[components*3];break;
            }
            Fn<Gen>("glGenBuffers")(1,out uint buffer);buffers.Add(buffer);Fn<Two>("glBindBuffer")(0x8892,buffer);fixed(float* p=values)Fn<Data>("glBufferData")(0x8892,values.Length*4,(nint)p,0x88E4);Fn<Pointer>("glVertexAttribPointer")((uint)location,components,0x1406,0,0,0);Fn<One>("glEnableVertexAttribArray")((uint)location);
            attributes.Add((name.ToString(),components,buffer,values));
        }
        Fn<One>("glBindVertexArray")(0);Fn<Two>("glBindBuffer")(0x8892,0);
    }
    public void Draw(IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,uint> textures)
    {Apply(values,textures);Fn<One>("glBindVertexArray")(vao);glDrawArrays(4,0,3);Fn<One>("glBindVertexArray")(0);}
    public void DrawMesh(IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,uint> textures,IReadOnlyDictionary<string,float[]> vertices,ushort[] indices,bool persistent=false)
        =>DrawMeshCore(values,textures,vertices,indices,persistent,0x1403);
    public void DrawMesh32(IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,uint> textures,IReadOnlyDictionary<string,float[]> vertices,uint[] indices,bool persistent=false)
        =>DrawMeshCore(values,textures,vertices,indices,persistent,0x1405);
    void DrawMeshCore<T>(IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,uint> textures,IReadOnlyDictionary<string,float[]> vertices,T[] indices,bool persistent,uint indexType) where T:unmanaged,IEquatable<T>
    {
        Apply(values,textures);
        var key=persistent?vertices:transientMeshKey;
        if(!meshBuffers.TryGetValue(key,out var mesh)){meshBuffers[key]=mesh=new();Fn<Gen>("glGenVertexArrays")(1,out mesh.Vao);Fn<One>("glBindVertexArray")(mesh.Vao);
            foreach(var a in attributes){int location=Fn<Location>("glGetAttribLocation")(program,a.Name);Fn<Gen>("glGenBuffers")(1,out uint buffer);buffers.Add(buffer);mesh.Attributes.Add(new(){Name=a.Name,Buffer=buffer});Fn<Two>("glBindBuffer")(0x8892,buffer);Fn<Pointer>("glVertexAttribPointer")((uint)location,a.Components,0x1406,0,0,0);Fn<One>("glEnableVertexAttribArray")((uint)location);}
            Fn<Gen>("glGenBuffers")(1,out mesh.Indices);buffers.Add(mesh.Indices);Fn<Two>("glBindBuffer")(0x8893,mesh.Indices);
        }else Fn<One>("glBindVertexArray")(mesh.Vao);
        try{foreach(var attribute in mesh.Attributes){if(!vertices.TryGetValue(attribute.Name,out var source))throw new InvalidDataException(Label+": missing mesh attribute "+attribute.Name);
                if(attribute.Previous.AsSpan().SequenceEqual(source))continue;
                Fn<Two>("glBindBuffer")(0x8892,attribute.Buffer);fixed(float* p=source)Fn<Data>("glBufferData")(0x8892,source.Length*4,(nint)p,0x88E8);
                if(attribute.Previous.Length==source.Length)source.CopyTo(attribute.Previous,0);else attribute.Previous=(float[])source.Clone();}
            if(mesh.PreviousIndices is not T[] previousIndices||!previousIndices.AsSpan().SequenceEqual(indices)){fixed(T* p=indices)Fn<Data>("glBufferData")(0x8893,indices.Length*sizeof(T),(nint)p,0x88E4);mesh.PreviousIndices=(T[])indices.Clone();}glDrawElements(4,indices.Length,indexType,0);
        }finally{Fn<One>("glBindVertexArray")(0);Fn<Two>("glBindBuffer")(0x8892,0);}
    }
    void Apply(IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,uint> textures)
    {
        Fn<One>("glUseProgram")(program);int unit=0;
        foreach(var u in uniforms){
            if(u.Type==0x8B5E){if(!textures.TryGetValue(u.Name,out uint texture))throw new InvalidDataException(Label+": missing texture "+u.Name);Fn<One>("glActiveTexture")((uint)(0x84C0+unit));glBindTexture(0x0DE1,texture);Fn<UniformInt>("glUniform1i")(u.Location,unit++);continue;}
            if(!values.TryGetValue(u.Name,out var value))throw new InvalidDataException(Label+": missing property "+u.Name);
            if(uniformCache.TryGetValue(u.Location,out var previous)&&previous.AsSpan().SequenceEqual(value))continue;
            if(previous is not null&&previous.Length==value.Length)value.CopyTo(previous,0);else uniformCache[u.Location]=(float[])value.Clone();
            if(u.Type is 0x1404 or 0x8B56){Fn<UniformInt>("glUniform1i")(u.Location,(int)value[0]);continue;}
            int components=u.Type switch{0x1406=>1,0x8B50=>2,0x8B51=>3,0x8B52=>4,_=>throw new NotSupportedException(Label+": uniform type "+u.Type)};
            if(value.Length<u.Count*components)throw new InvalidDataException(Label+": short property "+u.Name);
            fixed(float* p=value)Fn<Uniform>("glUniform"+components+"fv")(u.Location,u.Count,(nint)p);
        }
    }
    public void Dispose(){foreach(uint buffer in buffers){uint id=buffer;Fn<Delete>("glDeleteBuffers")(1,ref id);}uint vertexArray=vao;Fn<Delete>("glDeleteVertexArrays")(1,ref vertexArray);foreach(var mesh in meshBuffers.Values)Fn<Delete>("glDeleteVertexArrays")(1,ref mesh.Vao);Fn<One>("glDeleteProgram")(program);}
}

public sealed class UnityShaderLibrary : IDisposable
{
    readonly Archive archive;readonly string site,prefix;readonly string? language;readonly JsonNode manifest,index;readonly Dictionary<string,UnityShader> compiled=[];
    readonly Dictionary<string,JsonNode> definitions=[];
    readonly Dictionary<string,UnityShader> requested=[];
    readonly Dictionary<string,Dictionary<string,float[]>> defaultValues=[];
    public UnityShaderLibrary(Archive archive,string site,JsonNode manifest,string prefix="shaders/",string? language=null){this.archive=archive;this.site=site;this.manifest=manifest;this.prefix=prefix;this.language=language;index=JsonNode.Parse(archive.Logical(site,manifest,prefix+"shaders.json",language))!;}
    public Dictionary<string,float[]> Defaults(string name)
    {
        if(defaultValues.TryGetValue(name,out var cached))return cached.ToDictionary(p=>p.Key,p=>(float[])p.Value.Clone());
        var definition=Definition(name);
        defaultValues[name]=definition["properties"]!.AsArray().OfType<JsonNode>().Where(p=>Actor.N(p["m_Type"])!=4).ToDictionary(p=>Archive.S(p,"m_Name"),p=>Enumerable.Range(0,4).Select(i=>Actor.N(p[$"m_DefValue[{i}]"])).ToArray());return Defaults(name);
    }
    JsonNode Definition(string name)
    {
        if(definitions.TryGetValue(name,out var definition))return definition;
        var entry=index.AsArray().OfType<JsonNode>().Single(n=>Archive.S(n,"name")==name);
        return definitions[name]=JsonNode.Parse(archive.Logical(site,manifest,prefix+Archive.S(entry,"parsed"),language))!;
    }
    public JsonNode State(string name,int pass=0)=>Definition(name)["subShaders"]![0]!["passes"]![pass]!["state"]!;
    public int Queue(string name)
    {
        var tags=Definition(name)["subShaders"]![0]!["tags"]?["tags"]?.AsArray();
        string? tag=tags?.FirstOrDefault(t=>string.Equals(t?[0]?.GetValue<string>(),"Queue",StringComparison.OrdinalIgnoreCase))?[1]?.GetValue<string>();
        if(tag is null)return 2000;
        var match=System.Text.RegularExpressions.Regex.Match(tag,@"^(\w+)([+-]\d+)?$");
        int start=match.Groups[1].Value switch{"Background"=>1000,"Geometry"=>2000,"AlphaTest"=>2450,"Transparent"=>3000,"Overlay"=>4000,_=>throw new NotSupportedException("Shader queue tag: "+tag)};
        return start+(match.Groups[2].Success?int.Parse(match.Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture):0);
    }
    public UnityShader Program(string name,int pass=0,params string[] keywords)
        =>ProgramVariant(name,pass,0,keywords);
    public UnityShader ProgramVariant(string name,int pass,int subShader,params string[] keywords)
    {
        string request=name+":"+pass+":"+subShader+":"+string.Join(',',keywords.Order());if(requested.TryGetValue(request,out var ready))return ready;
        var entry=index.AsArray().OfType<JsonNode>().Single(n=>Archive.S(n,"name")==name);
        var variants=entry["variants"]!.AsArray().OfType<JsonNode>().Where(v=>Actor.N(v["subShader"])==subShader&&Actor.N(v["pass"])==pass).ToArray();
        var declared=variants.SelectMany(v=>v["keywords"]!.AsArray().Select(k=>k!.ToString())).ToHashSet();var wanted=keywords.Where(declared.Contains).ToHashSet();
        var variant=variants.FirstOrDefault(v=>wanted.SetEquals(v["keywords"]!.AsArray().Select(k=>k!.ToString())))??throw new NotSupportedException("Missing shader variant: "+name);
        string path=prefix+Archive.S(variant,"file");if(!compiled.TryGetValue(path,out var program))compiled[path]=program=new(name+" pass "+pass,Encoding.UTF8.GetString(archive.Logical(site,manifest,path,language)));requested[request]=program;return program;
    }
    public void Dispose(){foreach(var program in compiled.Values)program.Dispose();}
}
