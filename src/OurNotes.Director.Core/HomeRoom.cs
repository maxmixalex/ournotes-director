using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OurNotes.Director.Core;

/// <summary>Draws active room geometry with its archived Unity shader programs and material state.</summary>
public sealed class HomeRoom : IDisposable
{
    readonly OffscreenGl gl;
    readonly UnityShaderLibrary shaders;
    readonly HomeCamera camera;
    readonly HomeLighting? lighting;
    readonly List<Part> parts = [];
    sealed record Part(int Index, int Queue, Vector3 Center, JsonNode Material, int SubShader, int[] Passes,
        IReadOnlyDictionary<string,float[]> Attributes, uint[] Indices, Dictionary<string,uint> Textures, Dictionary<string,float[]> Values, string[] Keywords);
    public sealed record DrawItem(int Queue, int SortingOrder, float Distance, int Index, Action Draw);
    public int VisibleMeshes { get; }
    public int PrimitiveCount => parts.Count;

    public HomeRoom(OffscreenGl gl, JsonNode home, HomeCamera camera, Func<string,byte[]> read)
    {
        this.gl = gl; this.camera = camera;
        var shaderIndex = Archive.S(home["shaders"] ?? throw new InvalidDataException("Home shaders are absent."), "index");
        int slash = shaderIndex.LastIndexOf('/');
        shaders = new(read, slash < 0 ? "" : shaderIndex[..(slash + 1)]);
        try
        {
            var room = new HomeGlb(read(Archive.S(home, "room")));
            var nodes = home["roomNodes"] as JsonArray ?? throw new InvalidDataException("Home room nodes are absent.");
            var materials = home["roomMaterials"] as JsonArray ?? throw new InvalidDataException("Home room materials are absent.");
            if (nodes.Count != room.Meshes.Length || materials.Count != (room.Document["materials"] as JsonArray)?.Count)
                throw new InvalidDataException("Home room metadata does not match its GLB meshes/materials.");
            if (room.Meshes.SelectMany((mesh,n)=>Actor.B(nodes[n]?["active"])?mesh.Primitives:[]).Any(p=>Archive.S(materials[p.Material]!,"shader")=="Universal Render Pipeline/Lit"))
                lighting = new(home,read);
            var textures = new Dictionary<int,(uint Texture, int Width, int Height)>();
            uint white = gl.Upload(new(1,1,[255,255,255,255]));
            uint blackCube = lighting is null ? 0 : gl.SolidCube(0,0,0,255);
            for (int n = 0; n < nodes.Count; n++)
            {
                if (!Actor.B(nodes[n]?["active"])) continue;
                VisibleMeshes++;
                var mesh = room.Meshes[n];
                for (int p = 0; p < mesh.Primitives.Length; p++)
                {
                    var primitive = mesh.Primitives[p];
                    if (primitive.Material < 0 || primitive.Material >= materials.Count) throw new InvalidDataException("Home room material index is out of range.");
                    var material = materials[primitive.Material]!;
                    if (Archive.S(material,"name") != Archive.S(room.Element("materials",primitive.Material),"name")) throw new InvalidDataException("Home room material ordering disagrees with its GLB.");
                    string shaderName = Archive.S(material,"shader");
                    var definition = shaders.Definition(shaderName);
                    int sub = SelectSubShader(definition);
                    var subShader = definition["subShaders"]![sub]!;
                    int[] passes = ForwardPasses(subShader);
                    int queue = p == 0 && nodes[n]?["renderQueue"] is not null ? (int)Actor.N(nodes[n]!["renderQueue"]) : MaterialQueue(material, subShader);
                    var values = shaders.Defaults(shaderName);
                    foreach (var field in new[] { "floats", "ints" }) foreach (var (name,value) in material[field]?.AsObject() ?? []) values[name] = [Actor.N(value)];
                    foreach (var (name,value) in material["colors"]?.AsObject() ?? []) values[name] = Color(value);
                    var bindings = new Dictionary<string,uint> { ["_MainTex"] = white };
                    if (lighting is not null) bindings["unity_SpecCube0"] = blackCube;
                    foreach (var (name,env) in material["texEnvs"]?.AsObject() ?? [])
                    {
                        values[name+"_ST"] = [NumberAt(env?["scale"],0,1),NumberAt(env?["scale"],1,1),NumberAt(env?["offset"],0,0),NumberAt(env?["offset"],1,0)];
                        if (env?["texture"] is null) { bindings[name] = white; continue; }
                        int textureIndex = env["texture"]!.GetValue<int>();
                        if (!textures.TryGetValue(textureIndex,out var texture))
                        {
                            var info = room.Element("textures",textureIndex);
                            var pixels = room.Image(info["source"]!.GetValue<int>());
                            var sampler = info["sampler"] is { } si ? room.Element("samplers",si.GetValue<int>()) : null;
                            int minFilter = (int)Actor.N(sampler?["minFilter"],9987), magFilter = (int)Actor.N(sampler?["magFilter"],9729);
                            int wrapS = (int)Actor.N(sampler?["wrapS"],10497), wrapT = (int)Actor.N(sampler?["wrapT"],10497);
                            // glTF's texture storage has a top-left origin; restored Unity UVs require flipped rows.
                            uint id = gl.UploadUnity(pixels,magFilter==9728?0:1,Wrap(wrapS),Wrap(wrapT),1);
                            gl.SetTextureSampler(id,minFilter,magFilter,wrapS,wrapT,minFilter is not (9728 or 9729));
                            textures[textureIndex] = texture = (id,pixels.Width,pixels.Height);
                        }
                        bindings[name] = texture.Texture;
                        values[name+"_TexelSize"] = [1f/texture.Width,1f/texture.Height,texture.Width,texture.Height];
                    }
                    var attributes = new Dictionary<string,float[]> { ["in_POSITION0"] = primitive.Positions, ["in_TEXCOORD0"] = primitive.Uvs, ["in_COLOR0"] = Enumerable.Repeat(1f,primitive.Positions.Length/3*4).ToArray() };
                    if (primitive.Normals is not null) attributes["in_NORMAL0"] = primitive.Normals;
                    var keywords = (material["keywords"]?.AsArray().Select(x=>x!.ToString()) ?? []).Concat(lighting?.Keywords ?? []).Distinct().ToArray();
                    // Preserve the original programs. Missing/unsupported material variants fail before frame output.
                    foreach (int pass in passes) _ = shaders.ProgramVariant(shaderName,pass,sub,keywords);
                    var center = AdvMatrix.Transform(camera.Room,new(mesh.Center,1));
                    parts.Add(new(parts.Count,queue,new(center.X,center.Y,center.Z),material,sub,passes,attributes,primitive.Indices,bindings,values,keywords));
                }
            }
        }
        catch { shaders.Dispose(); throw; }
    }

    public IEnumerable<DrawItem> Items(float[] viewProjection, int frame, int fps = 30)
    {
        var globals = Globals(viewProjection,frame,fps);
        foreach (var part in parts)
            yield return new(part.Queue,0,Vector3.Distance(camera.Position,part.Center),part.Index,()=>Draw(part,globals));
    }

    Dictionary<string,float[]> Globals(float[] viewProjection,int frame,int fps)
    {
        float t = frame/(float)fps;
        var values = new Dictionary<string,float[]>()
        {
            ["unity_ObjectToWorld"]=camera.Room,["unity_WorldToObject"]=camera.RoomInverse,
            ["unity_MatrixVP"]=viewProjection,["unity_MatrixV"]=camera.View,["glstate_matrix_projection"]=camera.Projection(gl.Width/(float)gl.Height),
            ["_ProjectionParams"]=[1,camera.Near,camera.Far,1/camera.Far], ["_ScreenParams"]=[gl.Width,gl.Height,1+1f/gl.Width,1+1f/gl.Height],
            ["_WorldSpaceCameraPos"]=[camera.Position.X,camera.Position.Y,camera.Position.Z],
            ["unity_OrthoParams"]=[camera.OrthographicSize*gl.Width/gl.Height,camera.OrthographicSize,0,0],
            ["_Time"]=[t/20,t,t*2,t*3],["_GlobalMipBias"]=[0,0]
        };
        if (lighting is not null) foreach (var (key,value) in lighting.Values) values[key] = value;
        return values;
    }

    void Draw(Part part,Dictionary<string,float[]> globals)
    {
        var values = new Dictionary<string,float[]>(part.Values);
        foreach (var (key,value) in globals) values[key]=value;
        string name = Archive.S(part.Material,"shader");
        foreach (int pass in part.Passes)
        {
            var program = shaders.ProgramVariant(name,pass,part.SubShader,part.Keywords);
            var state = shaders.State(name,pass,part.SubShader);
            int I(JsonNode? n,int fallback=0) => (int)F(n,fallback);
            float F(JsonNode? n,float fallback=0)
            {
                string property = n is null ? "" : Archive.S(n,"name");
                return values.TryGetValue(property,out var v) ? v[0] : Actor.N(n?["val"],fallback);
            }
            var blend = state["rtBlend0"];
            var stencil = state["stencilOp"];
            var renderState = new OffscreenGl.MaterialState(I(blend?["blendOp"]),I(blend?["blendOpAlpha"]),I(blend?["colMask"],15),I(state["zTest"],4),I(state["zWrite"])!=0,F(state["offsetFactor"]),F(state["offsetUnits"]),
                new(I(stencil?["comp"],8),I(stencil?["pass"]),I(stencil?["fail"]),I(stencil?["zFail"]),I(state["stencilRef"]),I(state["stencilReadMask"],255),I(state["stencilWriteMask"],255)));
            var attributes = new Dictionary<string,float[]>(part.Attributes);
            int count = part.Attributes["in_POSITION0"].Length/3;
            foreach (var (attribute,data) in part.Attributes)
            {
                int required = program.AttributeComponents(attribute), source = data.Length/count;
                if (required == 0 || required == source) continue;
                var expanded = new float[count*required];
                for (int i=0;i<count;i++) for (int j=0;j<required;j++) expanded[i*required+j]=j<source?data[i*source+j]:j==3?1:0;
                attributes[attribute]=expanded;
            }
            // Room matrices normally preserve orientation; reverse triangle winding for mirrored placements.
            var indices = part.Indices;
            var m = camera.Room;
            float determinant = m[0]*(m[5]*m[10]-m[9]*m[6])-m[4]*(m[1]*m[10]-m[9]*m[2])+m[8]*(m[1]*m[6]-m[5]*m[2]);
            if (determinant<0)
            {
                indices=(uint[])indices.Clone();
                for (int i=0;i<indices.Length;i+=3) (indices[i+1],indices[i+2])=(indices[i+2],indices[i+1]);
            }
            gl.GameMaterial32(program,values,part.Textures,attributes,indices,I(blend?["srcBlend"],1),I(blend?["destBlend"]),I(blend?["srcBlendAlpha"],1),I(blend?["destBlendAlpha"]),I(state["culling"]),state:renderState);
        }
    }

    public static IEnumerable<DrawItem> Sort(IEnumerable<DrawItem> items) => items.OrderBy(x=>x.Queue<=2500?0:1).ThenBy(x=>x.SortingOrder).ThenBy(x=>x.Queue).ThenBy(x=>x.Queue<=2500?x.Distance:-x.Distance).ThenBy(x=>x.Index);
    static int SelectSubShader(JsonNode definition)
    {
        var subs = definition["subShaders"]?.AsArray() ?? throw new InvalidDataException("Home shader has no subshaders.");
        for(int i=0;i<subs.Count;i++) {string? pipeline=Tag(subs[i]?["tags"],"RenderPipeline");if(pipeline is null||pipeline.Equals("UniversalPipeline",StringComparison.OrdinalIgnoreCase))return i;}
        throw new NotSupportedException("Home shader has no Universal pipeline subshader.");
    }
    static int[] ForwardPasses(JsonNode sub)
    {
        var passes=sub["passes"]?.AsArray()??throw new InvalidDataException("Home shader has no passes.");
        string[] modes=["SRPDefaultUnlit","UniversalForward","UniversalForwardOnly"];
        var selected=new List<int>();
        foreach(string mode in modes)for(int i=0;i<passes.Count;i++)if(string.Equals(Tag(passes[i]?["tags"],"LightMode")??Tag(passes[i]?["state"]?["m_Tags"],"LightMode")??"SRPDefaultUnlit",mode,StringComparison.OrdinalIgnoreCase))selected.Add(i);
        return selected.Count>0?selected.ToArray():throw new NotSupportedException("Home shader has no forward pass.");
    }
    public static int MaterialQueue(JsonNode material,JsonNode sub)
    {
        int queue=(int)Actor.N(material["renderQueue"],-1);if(queue>=0)return queue;
        string tag=Tag(sub["tags"],"Queue")??"Geometry";
        if(int.TryParse(tag,out queue))return queue;
        var match=Regex.Match(tag,@"^\s*([A-Za-z]+)\s*([+-]\s*\d+)?\s*$");
        int basis=match.Groups[1].Value.ToLowerInvariant() switch{"background"=>1000,"geometry"=>2000,"alphatest"=>2450,"transparent"=>3000,"overlay"=>4000,_=>throw new NotSupportedException("Home shader queue: "+tag)};
        return basis+(match.Groups[2].Success?int.Parse(match.Groups[2].Value.Replace(" ",""),System.Globalization.CultureInfo.InvariantCulture):0);
    }
    static string? Tag(JsonNode? value,string key)
    {
        var tags=value as JsonArray??value?["tags"] as JsonArray;
        return tags?.FirstOrDefault(x=>string.Equals(x?[0]?.ToString(),key,StringComparison.OrdinalIgnoreCase))?[1]?.ToString();
    }
    static float[] Color(JsonNode? value)=>value is JsonArray a?a.Select(x=>Actor.N(x)).ToArray():new[]{"r","g","b","a"}.Select(k=>Actor.N(value?[k])).ToArray();
    static float NumberAt(JsonNode? n,int i,float fallback)=>n is JsonArray a&&i<a.Count?Actor.N(a[i],fallback):fallback;
    static int Wrap(int wrap)=>wrap switch{10497=>0,33071=>1,33648=>2,_=>throw new NotSupportedException("Home texture wrapping: "+wrap)};
    public void Dispose()=>shaders.Dispose();
}
