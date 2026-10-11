using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class SpineTests
{
    const string Quad = """
    {"skeleton":{"hash":"director-test","spine":"4.2.40","fps":60},
     "bones":[{"name":"root"},{"name":"moving","parent":"root"}],
     "slots":[{"name":"picture","bone":"moving","attachment":"pixel"}],
     "skins":[{"name":"default","attachments":{"picture":{"pixel":{"path":"pixel","width":2,"height":2}}}}],
     "animations":{"home_start":{"bones":{"moving":{"translate":[{}, {"time":1,"x":1}]}}}}}
    """;

    static JsonObject Options(bool tintBlack = false, bool pma = true) => new()
    {
        ["_animationName"] = "home_start", ["loop"] = 0, ["timeScale"] = 1,
        ["initialFlipX"] = false, ["initialFlipY"] = false, ["pmaVertexColors"] = pma, ["tintBlack"] = tintBlack
    };

    static SpineAsset Asset(JsonNode json, Pixels? image = null, bool pmaTexture = false, int rotation = 0)
    {
        image ??= new Pixels(4, 4, Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 100, 50, 200, 128 }).ToArray());
        string atlas = $"page.png\nsize:{image.Width},{image.Height}\nfilter:Nearest,Nearest\npma:{pmaTexture.ToString().ToLowerInvariant()}\npixel\nbounds:0,0,{image.Width},{image.Height}\nrotate:{rotation}\n";
        var files = new Dictionary<string, byte[]>
        {
            ["test.json"] = Encoding.UTF8.GetBytes(json.ToJsonString()), ["test.atlas"] = Encoding.UTF8.GetBytes(atlas), ["page.png"] = Png.Encode(image)
        };
        return SpineAsset.Load(JsonNode.Parse("""{"name":"test","skeleton":"test.json","atlases":["test.atlas"],"scale":1,"defaultMix":0.2,"mixes":[]}""")!, file => files[file]);
    }

    static JsonNode WeightedMesh()
    {
        var json = JsonNode.Parse(Quad)!;
        json["skins"]![0]!["attachments"]!["picture"]!["pixel"] = JsonNode.Parse("""
        {"type":"mesh","path":"pixel","uvs":[0,1,1,1,1,0,0,0],"triangles":[0,1,2,2,3,0],"hull":4,
         "vertices":[1,1,-1,-1,1, 1,1,1,-1,1, 1,1,1,1,1, 1,1,-1,1,1]}
        """);
        json["animations"]!["home_start"]!["attachments"] = JsonNode.Parse("""
        {"default":{"picture":{"pixel":{"deform":[{}, {"time":1,"vertices":[0,0,0,0,0,1,0,1]}]}}}}
        """);
        return json;
    }

    static JsonNode Clipped()
    {
        var json = JsonNode.Parse(Quad)!;
        json["slots"]!.AsArray().Insert(0, JsonNode.Parse("""{"name":"clip","bone":"root","attachment":"mask"}"""));
        json["skins"]![0]!["attachments"]!["clip"] = JsonNode.Parse("""
        {"mask":{"type":"clipping","end":"picture","vertexCount":4,"vertices":[-1,-1,-1,1,0,1,0,-1]}}
        """);
        return json;
    }

    public static void Run(Action<bool, string> assert)
    {
        var asset = Asset(JsonNode.Parse(Quad)!);
        assert(asset.Version == "4.2.40" && asset.FramesPerSecond == 60 && asset.Animations.Single().Duration == 1, "Spine reads actual runtime version and complete animation duration");
        var actor = new SpineActor(asset, Options());
        assert(actor.Bounds is { } start && Vector3.Distance(start.Min, new(-1, -1, 0)) < 1e-6f, "Spine frame zero is the entry pose");
        actor.Advance(30, 60);
        assert(Math.Abs(actor.Bounds!.Value.Center.X - .5f) < 1e-5f, "Spine advances by frame time without an initial-frame offset");
        string geometry = Geometry(actor);
        actor.Advance(60, 60); actor.Advance(30, 60);
        assert(Geometry(actor) == geometry, "Spine backseek reconstructs exact animation state");
        actor.Reset(); for (int i = 0; i <= 30; i++) actor.Advance(i, 60);
        assert(Geometry(actor) == geometry, "Spine random frame sampling and sequential playback use the same fixed steps");
        actor.ApplyEndState();
        assert(Math.Abs(actor.Bounds!.Value.Center.X - 1) < 1e-6f, "Spine can reproduce the upstream held home pose");
        actor.Advance(0, 60);
        assert(Math.Abs(actor.Bounds!.Value.Center.X) < 1e-6f, "Spine export resets an upstream held pose to the true first frame");

        var weighted = new SpineActor(Asset(WeightedMesh()), Options());
        weighted.Advance(60, 60);
        var bounds = weighted.Bounds!.Value;
        assert(Math.Abs(bounds.Min.X) < 1e-5f && Math.Abs(bounds.Max.X - 2) < 1e-5f && Math.Abs(bounds.Max.Y - 2) < 1e-5f,
            "Spine weighted mesh applies bone motion and vertex deform together");

        var clipped = new SpineActor(Asset(Clipped()), Options());
        assert(clipped.Mesh.Batches.Sum(b => b.Indices.Length) > 0 && Math.Abs(clipped.Bounds!.Value.Max.X) < 1e-5f,
            "Spine clipping produces triangulated geometry restricted to the mask");
        var clipEnd = Clipped();
        clipEnd["slots"]!.AsArray().Add(JsonNode.Parse("""{"name":"after","bone":"root","attachment":"pixel"}"""));
        clipEnd["skins"]![0]!["attachments"]!["after"] = JsonNode.Parse("""{"pixel":{"path":"pixel","x":2,"width":2,"height":2}}""");
        var after = new SpineActor(Asset(clipEnd), Options());
        assert(after.Bounds!.Value.Max.X == 3, "Spine clipping ends at the declared slot rather than clipping later attachments");
        var shared = after.Asset;
        var onlyClippedCharacter = new SpineActor(shared, Options(), includedSlots: new HashSet<string>(["picture"]));
        var onlyLaterCharacter = new SpineActor(shared, Options(), includedSlots: new HashSet<string>(["after"]));
        assert(Math.Abs(onlyClippedCharacter.Bounds!.Value.Max.X) < 1e-5f && Math.Abs(onlyLaterCharacter.Bounds!.Value.Min.X - 1) < 1e-5f,
            "Spine shared-skeleton slot filtering keeps each character and still processes skipped clipping end slots");
        onlyClippedCharacter.Advance(30, 60);
        assert(Math.Abs(onlyClippedCharacter.Bounds!.Value.Min.X + .5f) < 1e-5f,
            "Spine shared-skeleton filtering preserves the full bone animation");

        var ik = Asset(JsonNode.Parse("""
        {"skeleton":{"hash":"ik-test","spine":"4.2.40"},
         "bones":[{"name":"root"},{"name":"aim","parent":"root","length":1},{"name":"target","parent":"root","y":2}],
         "ik":[{"name":"aim-at-target","bones":["aim"],"target":"target","mix":1}],
         "slots":[{"name":"picture","bone":"aim","attachment":"pixel"}],
         "skins":[{"name":"default","attachments":{"picture":{"pixel":{"path":"pixel","x":1,"width":0.2,"height":0.2}}}}],
         "animations":{"home_start":{}}}
        """)!);
        var constrained = new SpineActor(ik, Options());
        assert(Vector3.Distance(constrained.Bounds!.Value.Center, new(0, 1, 0)) < 1e-5f,
            "Spine IK constraint rotates attachment geometry toward its target");

        var reordered = JsonNode.Parse(Quad)!;
        reordered["slots"]!.AsArray().Add(JsonNode.Parse("""{"name":"after","bone":"root","attachment":"pixel"}"""));
        reordered["skins"]![0]!["attachments"]!["after"] = JsonNode.Parse("""{"pixel":{"path":"pixel","x":2,"width":2,"height":2}}""");
        reordered["animations"]!["home_start"]!["drawOrder"] = JsonNode.Parse("""[{"time":1,"offsets":[{"slot":"picture","offset":1}]}]""");
        var drawingOrder = new SpineActor(Asset(reordered), Options()); drawingOrder.ApplyEndState();
        assert(drawingOrder.Mesh.Batches.Single().Positions[0] == 3,
            "Spine draw-order timelines control the submitted triangles");
        drawingOrder.Reset(); drawingOrder.Advance(60, 60);
        assert(drawingOrder.Mesh.Batches.Single().Positions[0] == 3,
            "Spine non-looping final output frame applies discrete end keys despite float timestep accumulation");
        reordered["animations"]!["home_start"]!["bones"]!["moving"]!["translate"]![1]!["time"] = 2;
        var middleKey = new SpineActor(Asset(reordered), Options()); middleKey.Advance(60, 60);
        assert(middleKey.Mesh.Batches.Single().Positions[0] == 3,
            "Spine intermediate discrete keys apply on their exact output frame");
        var looping = new SpineActor(asset, Options(), loopOverride: true);
        looping.Advance(30, 60);
        float halfway = looping.Bounds!.Value.Center.X;
        looping.Advance(60, 60);
        assert(Math.Abs(halfway - .5f) < 1e-6f && Math.Abs(looping.Bounds!.Value.Center.X) < 1e-6f,
            "Spine loop boundaries return to the first pose without a one-frame float-clock delay");

        var invalid = JsonNode.Parse(Quad)!; invalid["skeleton"]!["spine"] = "4.1.24";
        bool rejected = false; try { Asset(invalid); } catch (NotSupportedException e) { rejected = e.Message.Contains("4.2.x", StringComparison.Ordinal); }
        assert(rejected, "Spine rejects incompatible editor versions before parsing skeleton data");
        rejected = false; try { new SpineActor(asset, Options(), "missing"); } catch (ArgumentException) { rejected = true; }
        assert(rejected, "Spine rejects a nonexistent requested animation");
    }

    static string Geometry(SpineActor actor)
        => string.Join(";", actor.Mesh.Batches.Select(b => Archive.Hash(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Positions.AsSpan()).ToArray())));

    public static int RunGraphics()
    {
        using var gl = new OffscreenGl(32, 32);
        using var drawing = new SpineDrawing(gl);
        int checks = 0;
        byte[] Pixel(SpineActor actor, float[]? background = null)
        {
            gl.Begin(background ?? [0, 0, 0, 0]); drawing.Draw(actor, AdvMatrix.Identity());
            return gl.Read(32, 32).Rgba[((16 * 32 + 16) * 4)..((16 * 32 + 16) * 4 + 4)];
        }
        void Check(byte[] actual, float[] expected, string label)
        {
            for (int i = 0; i < 4; i++) if (Math.Abs(actual[i] / 255f - expected[i]) > 3f / 255)
                throw new Exception($"Spine {label}, channel {i}: {actual[i] / 255f} != {expected[i]}");
            checks++;
        }

        var tinted = JsonNode.Parse(Quad)!;
        tinted["slots"]![0]!["color"] = "8080ff80";
        tinted["slots"]![0]!["dark"] = "008000";
        var input = new Pixels(4, 4, Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 200, 100, 50, 128 }).ToArray());
        var straight = Asset(tinted, input);
        Check(Pixel(new(straight, Options())), [100 / 255f, 50 / 255f, 50 / 255f, 64 / 255f], "straight texture and PMA vertex colors");
        Check(Pixel(new(straight, Options(pma: false))), [100 / 255f, 50 / 255f, 50 / 255f, 64 / 255f], "straight vertex-color input");
        Check(Pixel(new(straight, Options(tintBlack: true))), [100 / 255f, 128 / 255f, 50 / 255f, 64 / 255f], "two-color tint");
        var pmaPixels = new Pixels(4, 4, Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 100, 50, 25, 128 }).ToArray());
        Check(Pixel(new(Asset(tinted, pmaPixels, true), Options())), [100 / 255f, 50 / 255f, 50 / 255f, 64 / 255f], "already-premultiplied atlas");
        Check(Pixel(new(Asset(tinted, pmaPixels, true), Options(tintBlack: true))), [100 / 255f, 128 / 255f, 50 / 255f, 64 / 255f], "premultiplied atlas two-color tint");

        float[] destination = [.2f, .3f, .4f, 1]; float[] source = [100 / 255f, 50 / 255f, 200 / 255f]; float alpha = 128 / 255f;
        foreach (string mode in new[] { "normal", "additive", "multiply", "screen" })
        {
            var json = JsonNode.Parse(Quad)!; json["slots"]![0]!["blend"] = mode;
            float[] expected = Enumerable.Range(0, 3).Select(i => mode switch
            {
                "normal" => source[i] * alpha + destination[i] * (1 - alpha),
                "additive" => source[i] * alpha + destination[i],
                "multiply" => destination[i] * (source[i] * alpha + 1 - alpha),
                _ => source[i] * alpha + destination[i] * (1 - source[i] * alpha)
            }).Append(1f).ToArray();
            Check(Pixel(new(Asset(json), Options()), destination), expected, mode + " blend");
        }

        var clipped = new SpineActor(Asset(Clipped()), Options());
        gl.Begin([0, 0, 0, 0]); drawing.Draw(clipped, AdvMatrix.Identity());
        byte[] image = gl.Read(32, 32).Rgba;
        if (image[(16 * 32 + 8) * 4 + 3] != 128 || image[(16 * 32 + 24) * 4 + 3] != 0)
            throw new Exception("Spine clipping leaked outside its mask or removed visible pixels.");
        checks++;

        var colors = new Pixels(2, 2, [255,0,0,255, 0,255,0,255, 0,0,255,255, 255,255,255,255]);
        gl.Begin([0, 0, 0, 0]); drawing.Draw(new(Asset(JsonNode.Parse(Quad)!, colors, rotation: 90), Options()), AdvMatrix.Identity());
        image = gl.Read(32, 32).Rgba;
        foreach (var sample in new[] { (X:8,Y:8,Color:new float[]{0,0,1,1}), (X:24,Y:8,Color:new float[]{1,0,0,1}),
            (X:8,Y:24,Color:new float[]{1,1,1,1}), (X:24,Y:24,Color:new float[]{0,1,0,1}) })
            Check(image[((sample.Y * 32 + sample.X) * 4)..((sample.Y * 32 + sample.X) * 4 + 4)], sample.Color, "90-degree atlas region");
        Console.WriteLine($"Passed {checks} native Spine PMA, two-color tint, blend, clipping, and atlas rotation checks.");
        return 0;
    }
}
