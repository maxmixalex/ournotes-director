using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

internal static class HomeRenderingTests
{
    // CLI smoke tests own this directory and remove it after exercising the exported executable.
    public static int WriteFixture(string root) { _ = new Fixture(root); return 0; }

    public static void Run(Action<bool, string> assert)
    {
        var (document, binary) = StridedTriangle();
        var glb = new HomeGlb(Glb(document, binary));
        var mesh = glb.Meshes.Single();
        var primitive = mesh.Primitives.Single();
        assert(primitive.Positions.SequenceEqual(new float[] { 1, 2, -3, 4, 5, -6, -1, 3, -2 }),
            "home GLB reads interleaved accessors with buffer offsets and restores Unity positions");
        assert(primitive.Uvs.Zip(new float[] { .25f, .9f, .5f, .4f, .75f, .1f }).All(p => Math.Abs(p.First - p.Second) < 1e-6f),
            "home GLB restores the vertical texture coordinate for Unity shaders");
        assert(primitive.Normals!.SequenceEqual(new float[] { 0, 0, -1, 0, 0, -1, 0, 0, -1 }) && primitive.Indices.SequenceEqual(new uint[] { 0, 2, 1 }),
            "home GLB reflects normals and reverses winding together with handedness");
        assert(mesh.Name == "strided" && mesh.Center == new Vector3(1.5f, 3.5f, -4),
            "home mesh sorting centers use restored geometry bounds");
        document["accessors"]![0]!["count"] = 4;
        Reject(() => new HomeGlb(Glb(document, binary)), assert, "home GLB rejects accessors extending beyond their declared buffer view");

        var camera = new HomeCamera(CameraHome(), CameraSpot());
        var projection = camera.ViewProjection(2);
        static Vector3 Project(float[] matrix, Vector3 point)
        {
            var p = AdvMatrix.Transform(matrix, new(point, 1));
            return new(p.X / p.W, p.Y / p.W, p.Z / p.W);
        }
        var center = Project(projection, Vector3.Zero);
        var right = Project(projection, new(2, 0, 0));
        var up = Project(projection, new(0, 1, 0));
        assert(camera.Position == new Vector3(0, 0, -4) && Math.Abs(center.X) < 1e-6f && Math.Abs(center.Y) < 1e-6f
            && right.X > 0 && up.Y > 0 && Math.Abs(right.X - up.Y) < 1e-6f,
            "home camera projects its look target centrally with the requested aspect and upright axes");
        assert(Math.Abs(Project(projection, new(0, 0, -3.9f)).Z + 1) < 1e-4f && Math.Abs(Project(projection, new(0, 0, 96)).Z - 1) < 1e-5f,
            "home perspective projection places physical near and far planes on the OpenGL clip planes");
        var placement = HomeCamera.Trs(new(3, 4, 5), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), new(2));
        var placed = AdvMatrix.Transform(placement, new(1, 0, 0, 1));
        var restored = AdvMatrix.Transform(HomeCamera.Inverse(placement), placed);
        assert(Vector4.Distance(placed, new(3, 6, 5, 1)) < 1e-5f && Vector4.Distance(restored, new(1, 0, 0, 1)) < 1e-5f,
            "home quaternion transforms preserve column-major Unity placement and invert non-unit scales");

        var natural = HomeTiming.Plan(1, fps: 60);
        assert(natural.FirstFrame == 0 && natural.FrameCount == 61 && natural.LastFrame == 60,
            "home non-looping export includes the actual final pose");
        var trimmed = HomeTiming.Plan(1, time: .1, duration: .2, fps: 30);
        assert(trimmed.FirstFrame == 3 && trimmed.FrameCount == 6 && Math.Abs(trimmed.Duration - .2) < 1e-12,
            "home explicit durations keep the requested number of output frame intervals");
        assert(HomeTiming.Plan(1, fps: 60, loop: true).FrameCount == 60 && HomeTiming.Plan(0).FrameCount == 1,
            "home loop exports avoid a duplicate endpoint and static animations still produce one frame");
        var fractional = HomeTiming.Plan(4.0167, fps: 60);
        assert(fractional.LastFrame / 60d >= 4.0167 && (fractional.LastFrame - 1) / 60d < 4.0167,
            "home durations with fractional source keys hold through the first output frame reaching the endpoint");
        Reject(() => HomeTiming.Plan(1, duration: double.NaN), assert, "home timing rejects non-finite export durations");
        var order = HomeRoom.Sort([
            new(3000, 0, 2, 0, () => { }), new(2000, 0, 10, 1, () => { }),
            new(2000, 0, 1, 2, () => { }), new(3000, 0, 9, 3, () => { })]).Select(item => item.Index);
        assert(order.SequenceEqual(new[] { 2, 1, 3, 0 }),
            "home sorts opaque geometry front to back before transparent geometry back to front");

        var postHome = CameraHome();
        var lowVolume = Volume(0, 1, 20); var highVolume = Volume(10, .5f, 60);
        postHome["sceneRoot"]!["volumes"] = new JsonArray(highVolume, lowVolume);
        assert(HomePostFx.Stack(postHome) is null, "home camera disables post processing even when volumes exist");
        postHome["sceneRoot"]!["sceneCamera"]!["additionalCameraData"] = new JsonObject { ["m_RenderPostProcessing"] = 1 };
        assert(HomePostFx.Stack(postHome)!["ColorAdjustments"]!["contrast"]!.GetValue<float>() == 40,
            "home post processing sorts volume priority before applying fractional weights");
        highVolume["enabled"] = 0;
        assert(HomePostFx.Stack(postHome)!["ColorAdjustments"]!["contrast"]!.GetValue<float>() == 20,
            "disabled home volumes do not change the lower-priority result");
        highVolume["enabled"] = 1; highVolume["weight"] = 0;
        assert(HomePostFx.Stack(postHome)!["ColorAdjustments"]!["contrast"]!.GetValue<float>() == 20,
            "zero-weight home volumes leave the active profile unchanged");
        lowVolume["enabled"] = 0;
        assert(HomePostFx.Stack(postHome) is null, "home post processing omits a stack with no enabled positive-weight volumes");
        lowVolume["enabled"] = 1; postHome["volume"] = Volume(20, .25f, 10);
        assert(HomePostFx.Stack(postHome)!["ColorAdjustments"]!["contrast"]!.GetValue<float>() == 17.5f,
            "the placed home volume participates in the same ordered weighted stack as scene-root volumes");
    }

    public static int RunGraphics()
    {
        int checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("Home rendering: " + label);
            checks++;
        }
        using var fixture = new Fixture();
        var scene = new HomeCatalog(new Archive(fixture.Root)).Open(77);
        var resources = new HomeResources(scene);
        var timing = HomeTiming.Plan(1, fps: 30);
        Check(scene.Characters.Count == 1 && scene.SelectCharacter("alice").Parts.Count == 1,
            "minimal archived home reaches character selection through the public catalog");
        using (var renderer = new HomeRenderer(new(resources), 128, 96, 1, timing))
        {
            var first = renderer.Frame(0);
            var middle = renderer.Frame(15);
            Check(renderer.RoomMeshCount == 1 && !renderer.Transparent,
                "only active room nodes join the full home scene");
            Check(IsColor(middle, 4, 4, 0, 255, 0, 255) && IsColor(first, 64, 48, 0, 255, 0, 255),
                "the archived room shader draws its green material behind the animated character");
            Check(IsColor(middle, 64, 48, 0, 0, 255, 255),
                "the character moves from its real first pose into the room camera center");
        }
        scene.Home["roomNodes"]![1]!["active"] = 1;
        using (var renderer = new HomeRenderer(new(resources), 128, 96, 1, timing))
        {
            var middle = renderer.Frame(15);
            Check(renderer.RoomMeshCount == 2 && IsColor(middle, 64, 48, 255, 0, 0, 255),
                "an activated foreground room surface writes depth and occludes the Spine actor");
        }
        scene.Home["roomNodes"]![1]!["active"] = 0;
        var character = new HomeAnimation(resources, scene.SelectCharacter("alice"));
        using (var renderer = new HomeRenderer(character, 128, 96, 2, timing, framing: "fit"))
        {
            Pixels? first = null, middle = null, last = null;
            int minWidth = int.MaxValue, maxWidth = 0;
            bool inside = true;
            for (int frame = 0; frame <= 30; frame++)
            {
                var pixels = renderer.Frame(frame);
                var bounds = VisibleBounds(pixels);
                inside &= bounds.MinX >= 2 && bounds.MinY >= 2 && bounds.MaxX < pixels.Width - 2 && bounds.MaxY < pixels.Height - 2;
                minWidth = Math.Min(minWidth, bounds.MaxX - bounds.MinX + 1);
                maxWidth = Math.Max(maxWidth, bounds.MaxX - bounds.MinX + 1);
                if (frame == 0) first = pixels;
                if (frame == 15) middle = pixels;
                if (frame == 30) last = pixels;
            }
            Check(renderer.Transparent && renderer.RoomMeshCount == 0 && IsColor(middle!, 0, 0, 0, 0, 0, 0),
                "individual character output is transparent and excludes room meshes");
            Check(inside, "one crop fitted across the whole animation retains every moving frame inside its margins");
            Check(maxWidth - minWidth <= 2 && VisibleBounds(first!).MaxX < VisibleBounds(last!).MinX,
                "stable animation framing preserves movement and character scale instead of recentering each frame");
            Check(renderer.Frame(0).Rgba.SequenceEqual(first!.Rgba),
                "rendered output is byte-identical when seeking back after the final frame");
            renderer.QueueFrame(0); renderer.QueueFrame(15); renderer.QueueFrame(30);
            Check(renderer.PendingFrames == 3 && renderer.ReadQueuedFrame().Rgba.SequenceEqual(first!.Rgba)
                && renderer.ReadQueuedFrame().Rgba.SequenceEqual(middle!.Rgba)
                && renderer.ReadQueuedFrame().Rgba.SequenceEqual(last!.Rgba) && renderer.PendingFrames == 0,
                "queued native readback preserves frame order and the synchronous pixels");
        }
        using (var renderer = new HomeRenderer(new(resources), 128, 96, 1, timing, background: false))
            Check(renderer.Transparent && renderer.RoomMeshCount == 0 && renderer.Frame(15).Rgba[3] == 0,
                "full Spine animation can also omit the room for transparent compositing");
        Console.WriteLine($"Passed {checks} native home archive, room depth, transparency, stable framing, and readback checks.");
        return 0;
    }

    static bool IsColor(Pixels pixels, int x, int y, params byte[] expected)
    {
        int offset = (y * pixels.Width + x) * 4;
        return expected.Length == 4 && Enumerable.Range(0, 4).All(i => Math.Abs(pixels.Rgba[offset + i] - expected[i]) <= 2);
    }

    static (int MinX, int MinY, int MaxX, int MaxY) VisibleBounds(Pixels pixels)
    {
        int minX = pixels.Width, minY = pixels.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
            if (pixels.Rgba[(y * pixels.Width + x) * 4 + 3] != 0)
            { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        if (maxX < 0) throw new Exception("Home rendering produced an empty character frame.");
        return (minX, minY, maxX, maxY);
    }

    static void Reject(Action action, Action<bool, string> assert, string label)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException) { assert(true, label); return; }
        assert(false, label);
    }

    static JsonObject CameraHome() => JsonNode.Parse("""
        {"sceneRoot":{"objRoot":{"position":{"x":0,"y":0,"z":0},"rotation":{"x":0,"y":0,"z":0,"w":1},"scale":{"x":1,"y":1,"z":1}},
          "sceneCamera":{"camera":{"orthographic":0,"near clip plane":0.1,"far clip plane":100,"m_BackGroundColor":{"r":0,"g":0,"b":0,"a":1}}}}}
        """)!.AsObject();

    static JsonObject CameraSpot() => JsonNode.Parse("""
        {"situationSettings":{"originalOffset":{"x":0,"y":0,"z":0},"defaultPositionOffset":{"x":0,"y":0,"z":-4},
          "orbitRatio":0,"fieldOfView":60,"zoomMinFov":1,"zoomMaxFov":179,
          "backgroundPosition":{"x":0,"y":0,"z":0},"backgroundRotation":{"x":0,"y":0,"z":0},"backgroundScale":{"x":1,"y":1,"z":1}}}
        """)!.AsObject();

    static JsonObject Volume(float priority, float weight, float contrast) => new()
    {
        ["active"] = 1, ["enabled"] = 1, ["layer"] = 15, ["isGlobal"] = 1, ["priority"] = priority, ["weight"] = weight, ["profile"] = "fixture",
        ["components"] = new JsonArray(new JsonObject { ["class"] = "ColorAdjustments", ["active"] = 1, ["contrast"] = contrast })
    };

    static (JsonObject Document, byte[] Binary) StridedTriangle()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(0x12345678);
            foreach (float value in new float[] { 1, 2, 3, 0, 0, 1, .25f, .1f, 4, 5, 6, 0, 0, 1, .5f, .6f, -1, 3, 2, 0, 0, 1, .75f, .9f }) writer.Write(value);
            writer.Write((ushort)0xffff); writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
        }
        var document = JsonNode.Parse("""
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":108}],
             "bufferViews":[{"buffer":0,"byteOffset":4,"byteLength":96,"byteStride":32},{"buffer":0,"byteOffset":100,"byteLength":8}],
             "accessors":[{"bufferView":0,"byteOffset":0,"componentType":5126,"count":3,"type":"VEC3"},
               {"bufferView":0,"byteOffset":12,"componentType":5126,"count":3,"type":"VEC3"},
               {"bufferView":0,"byteOffset":24,"componentType":5126,"count":3,"type":"VEC2"},
               {"bufferView":1,"byteOffset":2,"componentType":5123,"count":3,"type":"SCALAR"}],
             "materials":[{"name":"test"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1,"TEXCOORD_0":2},"indices":3,"material":0}]}],
             "nodes":[{"name":"strided","mesh":0}]}
            """)!.AsObject();
        return (document, stream.ToArray());
    }

    static byte[] Glb(JsonObject document, byte[] binary)
    {
        byte[] json = Encoding.UTF8.GetBytes(document.ToJsonString());
        int jsonLength = (json.Length + 3) & ~3, binaryLength = (binary.Length + 3) & ~3;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(0x46546c67u); writer.Write(2); writer.Write(28 + jsonLength + binaryLength);
            writer.Write(jsonLength); writer.Write(0x4e4f534au); writer.Write(json);
            for (int i = json.Length; i < jsonLength; i++) writer.Write((byte)32);
            writer.Write(binaryLength); writer.Write(0x004e4942u); writer.Write(binary);
            for (int i = binary.Length; i < binaryLength; i++) writer.Write((byte)0);
        }
        return stream.ToArray();
    }

    static byte[] Room()
    {
        using var stream = new MemoryStream();
        var views = new JsonArray(); var accessors = new JsonArray(); var meshes = new JsonArray();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            for (int mesh = 0; mesh < 2; mesh++)
            {
                float gltfZ = mesh == 0 ? -2 : 1;
                int vertexStart = (int)stream.Position, vertexView = views.Count, firstAccessor = accessors.Count;
                foreach (float value in new float[] { -10, -10, gltfZ, 0, 0, 10, -10, gltfZ, 1, 0, 10, 10, gltfZ, 1, 1, -10, 10, gltfZ, 0, 1 }) writer.Write(value);
                views.Add((JsonNode)new JsonObject { ["buffer"] = 0, ["byteOffset"] = vertexStart, ["byteLength"] = 80, ["byteStride"] = 20 });
                accessors.Add((JsonNode)new JsonObject { ["bufferView"] = vertexView, ["componentType"] = 5126, ["count"] = 4, ["type"] = "VEC3" });
                accessors.Add((JsonNode)new JsonObject { ["bufferView"] = vertexView, ["byteOffset"] = 12, ["componentType"] = 5126, ["count"] = 4, ["type"] = "VEC2" });
                int indicesStart = (int)stream.Position, indexView = views.Count;
                foreach (ushort index in new ushort[] { 0, 2, 1, 0, 3, 2 }) writer.Write(index);
                views.Add((JsonNode)new JsonObject { ["buffer"] = 0, ["byteOffset"] = indicesStart, ["byteLength"] = 12 });
                accessors.Add((JsonNode)new JsonObject { ["bufferView"] = indexView, ["componentType"] = 5123, ["count"] = 6, ["type"] = "SCALAR" });
                meshes.Add((JsonNode)new JsonObject { ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = firstAccessor, ["TEXCOORD_0"] = firstAccessor + 1 }, ["indices"] = firstAccessor + 2, ["material"] = mesh
                }) });
            }
        return Glb(new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" }, ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = stream.Length }),
            ["bufferViews"] = views, ["accessors"] = accessors, ["meshes"] = meshes,
            ["materials"] = new JsonArray(new JsonObject { ["name"] = "Background" }, new JsonObject { ["name"] = "Front" }),
            ["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 }, new JsonObject { ["mesh"] = 1 })
        }, stream.ToArray());
    }

    sealed class Fixture : IDisposable
    {
        public string Root { get; }
        readonly JsonObject files = new();
        public Fixture(string? root = null)
        {
            Root = Path.GetFullPath(root ?? Path.Combine(Path.GetTempPath(), "ournotes-home-render-tests-" + Guid.NewGuid().ToString("N")));
            if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Home rendering fixture requires a new directory.");
            Directory.CreateDirectory(Path.Combine(Root, "index")); File.WriteAllText(Path.Combine(Root, "index", "stats.json"), "{}");
            var home = CameraHome();
            home["spotId"] = 77; home["spot"] = "host/spot/spot.json"; home["room"] = "host/spot/room.glb"; home["talk"] = "area";
            home["shaders"] = new JsonObject { ["index"] = "shaders/shaders.json" };
            home["roomNodes"] = new JsonArray(new JsonObject { ["active"] = 1, ["renderQueue"] = 2000 }, new JsonObject { ["active"] = 0, ["renderQueue"] = 2000 });
            home["roomMaterials"] = JsonNode.Parse("""
                [{"name":"Background","shader":"Test/Room","renderQueue":2000,"colors":{"_Color":[0,1,0,1]}},
                 {"name":"Front","shader":"Test/Room","renderQueue":2000,"colors":{"_Color":[1,0,0,1]}}]
                """);
            home["spineMaterials"] = JsonNode.Parse("""{"sprite":{"shader":"Universal Render Pipeline/Spine/Skeleton","renderQueue":3000,"floats":{"_StraightAlphaInput":1}}}""");
            var spot = CameraSpot();
            spot["spotId"] = 77; spot["master"] = new JsonObject { ["_id"] = 77 };
            spot["characters"] = JsonNode.Parse("""[{"path":"home/Alice","_characterId":1}]""");
            spot["spineCharacters"] = JsonNode.Parse("""
                [{"path":"home/Alice","skeletonData":"alice","sortingOrder":0,"world":[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],
                  "animation":{"_animationName":"home_start","loop":0,"timeScale":1,"pmaVertexColors":1}}]
                """);
            spot["skeletons"] = JsonNode.Parse("""[{"name":"alice","skeleton":"alice.json","atlases":["alice.atlas"],"scale":1,"defaultMix":0.2,"mixes":[]}]""");
            Put("host/host.json", new JsonObject { ["kind"] = "home", ["home"] = home }.ToJsonString());
            Put("host/spot/spot.json", spot.ToJsonString()); Put("host/spot/room.glb", Room());
            Put("host/spot/spine/alice.json", """
                {"skeleton":{"hash":"home-fixture","spine":"4.2.40","fps":30},
                 "bones":[{"name":"root"},{"name":"moving","parent":"root","x":-1}],
                 "slots":[{"name":"picture","bone":"moving","attachment":"pixel"}],
                 "skins":[{"name":"default","attachments":{"picture":{"pixel":{"path":"pixel","width":0.4,"height":0.4}}}}],
                 "animations":{"home_start":{"bones":{"moving":{"translate":[{}, {"time":1,"x":2}]}}}}}
                """);
            Put("host/spot/spine/alice.atlas", "page.png\nsize:4,4\nfilter:Nearest,Nearest\npma:false\npixel\nbounds:0,0,4,4\n");
            Put("host/spot/spine/page.png", Png.Encode(new(4, 4, Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 0, 0, 255, 255 }).ToArray())));
            Put("shaders/shaders.json", """[{"name":"Test/Room","parsed":"room.json","variants":[{"subShader":0,"pass":0,"keywords":[],"file":"room.glsl"}]}]""");
            Put("shaders/room.json", """
                {"properties":[],"subShaders":[{"tags":{"tags":[["RenderPipeline","UniversalPipeline"],["Queue","Geometry"]]},
                  "passes":[{"tags":{"tags":[["LightMode","UniversalForward"]]},"state":{
                    "rtBlend0":{"srcBlend":{"val":1},"destBlend":{"val":0},"srcBlendAlpha":{"val":1},"destBlendAlpha":{"val":0},"colMask":{"val":15}},
                    "zTest":{"val":4},"zWrite":{"val":1},"culling":{"val":0}}}]}]}
                """);
            Put("shaders/room.glsl", """
                #ifdef VERTEX
                #version 330
                in vec4 in_POSITION0;
                uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
                uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
                void main() {
                    gl_Position = mat4(hlslcc_mtx4x4unity_MatrixVP[0], hlslcc_mtx4x4unity_MatrixVP[1], hlslcc_mtx4x4unity_MatrixVP[2], hlslcc_mtx4x4unity_MatrixVP[3])
                        * mat4(hlslcc_mtx4x4unity_ObjectToWorld[0], hlslcc_mtx4x4unity_ObjectToWorld[1], hlslcc_mtx4x4unity_ObjectToWorld[2], hlslcc_mtx4x4unity_ObjectToWorld[3]) * in_POSITION0;
                }
                #endif
                #ifdef FRAGMENT
                #version 330
                uniform vec4 _Color;
                out vec4 color;
                void main() { color = _Color; }
                #endif
                """);
            Write("stories/42.json", new JsonObject
            {
                ["advId"] = 42, ["host"] = new JsonObject { ["kind"] = "home", ["doc"] = "host/host.json" }, ["files"] = files
            }.ToJsonString());
            Write("stories.json", """{"stories":[{"advId":42,"manifest":"stories/42.json","groups":[]}]}""");
        }
        void Put(string logical, string value) => Put(logical, Encoding.UTF8.GetBytes(value));
        void Put(string logical, byte[] value)
        {
            string asset = "assets/" + Archive.Hash(value) + Path.GetExtension(logical);
            string path = Path.Combine(Root, "sites", "intl", asset);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, value);
            files[logical] = new JsonObject { ["asset"] = asset, ["size"] = value.Length };
        }
        void Write(string relative, string value)
        {
            string path = Path.Combine(Root, "sites", "intl", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
