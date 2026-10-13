using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

internal static class HomeSpineRecoveryTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Home recovery: " + message); checks++; }
        using var fixture = new Fixture();
        var scene = fixture.Open(); var recovery = scene.SpineRecoveries.Single();
        Check(scene.SelectCharacter("alice").Parts.Count == 2 && recovery.SharedBones == 2 && recovery.Samples == 61 && recovery.MaximumBoneErrorPixels == 0,
            "a same-character rear file is recovered only after verifying every shared bone over the full animation");
        Check(scene.Spot["spineCharacters"]![1]!["skeletonData"] is null && scene.Spot["spineCharacters"]![1]!["world"] is null
            && !recovery.ToJson()["sourceMatrixAvailable"]!.GetValue<bool>() && !recovery.ToJson()["originalSceneProjectionExact"]!.GetValue<bool>(),
            "recovery preserves original missing values and does not claim an original prefab placement");
        var resources = new HomeResources(scene); var character = scene.SelectCharacter("alice"); var animation = new HomeAnimation(resources, character);
        Check(animation.Parts.Single(p => p.Path.EndsWith("_back", StringComparison.Ordinal)).SortingOrder == 3
            && animation.Parts.Single(p => p.Path == "home/Alice").SortingOrder == 4,
            "a verified rear layer renders immediately behind its reference component");
        var timing = HomeTiming.Plan(animation.Duration, fps: 30);
        using (var renderer = new HomeRenderer(animation, 192, 192, 1, timing, "fit"))
        {
            var pixels = renderer.Frame(15);
            Check(pixels.Rgba.Where((_, i) => i % 4 == 3).Min() == 0 && CountColor(pixels, 255, 0, 0) > 20 && CountColor(pixels, 0, 0, 255) > 20,
                "front attachments win their overlap and the lower rear attachments remain present on transparent output");
        }
        animation.Advance(13, 30);
        var pose = animation.Parts.SelectMany(p => p.Actor.Mesh.Batches).SelectMany(b => b.Positions).ToArray();
        var first = animation.MeasureBounds(4f / 3, 0, 31, 30);
        Check(pose.SequenceEqual(animation.Parts.SelectMany(p => p.Actor.Mesh.Batches).SelectMany(b => b.Positions))
            && ReferenceEquals(first, animation.MeasureBounds(4f / 3, 0, 31, 30)),
            "cached full-motion bounds do not alter an existing actor's current pose");
        var nativeA = animation.MeasureNativeCanvas(sourceAspect: 1); var nativeB = animation.MeasureNativeCanvas(sourceAspect: 16f / 9);
        Check(Math.Abs(nativeA.Width - nativeB.Width) <= 16 && Math.Abs(nativeA.Height - nativeB.Height) <= 8
            && nativeA.Width % 16 == 0 && nativeA.Height % 8 == 0 && nativeA.Bounds.Parts.Count == 2,
            "native artwork canvas includes both layers and is independent of an arbitrary landscape preview aspect");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool observed = false;
            try { animation.MeasureBounds(4f / 3, 0, 31, 30, cancelled.Token); } catch (OperationCanceledException) { observed = true; }
            Check(observed, "cancellation applies even when native bounds have already been cached");
        }
        var edits = new HomeEditState
        {
            Background = false, Framing = "fit", Characters = new Dictionary<string, HomeCharacterTrack>
            { ["alice"] = new() { Keys = [new(0, new()), new(1, new(.7, .4, 1.8))] } }
        };
        using (var renderer = new HomeRenderer(new(resources, character), 256, 192, 1, timing, "fit", editState: edits, fitEditedBounds: true, sourceAspect: 16f / 9))
        {
            bool inside = true;
            for (int frame = 0; frame <= 30; frame++) inside &= Inside(renderer.Frame(frame), 2);
            Check(inside, "edited fitted output retains every translated and scaled rear/front frame within one fixed crop");
        }
        fixture.Reset(); fixture.Back["bones"]![1]!["x"] = -2; fixture.Save();
        Reject(fixture.Open(), Check, "different setup bones cannot be silently aligned");
        fixture.Reset(); fixture.Back["animations"]!["home_start"]!["bones"]!["moving"]!["translate"]![1]!["x"] = 3; fixture.Save();
        Reject(fixture.Open(), Check, "different bone animation channels cannot be silently aligned");
        fixture.Reset(); fixture.Front["animations"]!["home_start"]!["bones"]!["root"] = JsonNode.Parse("""{"translate":[{"x":1},{"time":1,"x":1}]}"""); fixture.Save();
        Reject(fixture.Open(), Check, "additional reference animation driving a shared bone is rejected by actual runtime pose verification");
        fixture.Reset(); fixture.Spot["skeletons"]![1]!["atlases"] = new JsonArray("unrelated.atlas"); fixture.Save();
        Reject(fixture.Open(), Check, "matching names cannot borrow a different atlas or image coordinate system");
        fixture.Reset(); fixture.Spot["spineCharacters"]![1]!["path"] = "home/Bob_back";
        fixture.Spot["skeletons"]![1]!["skeleton"] = "home_bob_back.json"; fixture.Save();
        Reject(fixture.Open(), Check, "a missing rear component outside the target's character boundary is not assigned to that character");
        fixture.Reset(); var duplicate = fixture.Spot["skeletons"]![1]!.DeepClone(); duplicate["name"] = "duplicate"; fixture.Spot["skeletons"]!.AsArray().Add(duplicate); fixture.Save();
        Reject(fixture.Open(), Check, "ambiguous rear skeleton files remain an explicit unresolved component");
        fixture.Reset(); fixture.Front["slots"]![0]!["attachment"] = "pixel"; fixture.Save();
        Reject(fixture.Open(), Check, "a rear candidate must fill genuinely missing attachments instead of duplicating a complete body");
        fixture.Reset(); fixture.Spot["spineCharacters"]![1]!["path"] = "home/Alice_effect"; fixture.Save();
        Check(fixture.Open().SpineRecoveries.Count == 0, "unreferenced effects are never inferred to be rear character artwork");
        fixture.Reset(); fixture.Spot["spineCharacters"]![1]!["world"] = fixture.Spot["spineCharacters"]![0]!["world"]!.DeepClone(); fixture.Save();
        Reject(fixture.Open(), Check, "partially available original component settings are not overwritten by a reconstructed placement");
        fixture.Reset(); var explicitPart = fixture.Spot["spineCharacters"]![1]!;
        explicitPart["skeletonData"] = "alice_back"; explicitPart["animation"] = fixture.Spot["spineCharacters"]![0]!["animation"]!.DeepClone();
        explicitPart["world"] = fixture.Spot["spineCharacters"]![0]!["world"]!.DeepClone(); explicitPart["world"]![12] = 3.5; fixture.Save();
        var explicitScene = fixture.Open();
        Check(explicitScene.SpineRecoveries.Count == 0 && double.Parse(explicitScene.SpineParts[1]["world"]![12]!.ToString(), System.Globalization.CultureInfo.InvariantCulture) == 3.5,
            "an original complete component always retains its actual independent world transform");
        Console.WriteLine($"Passed {checks} home rear-layer recovery, rejection, full-motion bounds and edited framing checks.");
        return 0;
    }

    static void Reject(HomeScene scene, Action<bool, string> check, string message)
        => check(scene.SpineRecoveries.Count == 0 && scene.UnresolvedSpineParts.Count == 1 && scene.SelectCharacter("alice").Parts.Count == 1, message);
    static int CountColor(Pixels p, byte r, byte g, byte b)
    {
        int count = 0;
        for (int i = 0; i < p.Rgba.Length; i += 4) if (p.Rgba[i] == r && p.Rgba[i + 1] == g && p.Rgba[i + 2] == b && p.Rgba[i + 3] == 255) count++;
        return count;
    }
    static bool Inside(Pixels p, int margin)
    {
        bool visible = false;
        for (int y = 0; y < p.Height; y++) for (int x = 0; x < p.Width; x++)
        {
            if (p.Rgba[(y * p.Width + x) * 4 + 3] == 0) continue;
            visible = true; if (x < margin || x >= p.Width - margin || y < margin || y >= p.Height - margin) return false;
        }
        return visible;
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "ournotes-home-recovery-" + Guid.NewGuid().ToString("N"));
        readonly JsonObject manifest; readonly JsonObject initialSpot; readonly JsonObject initialFront; readonly JsonObject initialBack;
        public JsonObject Spot { get; private set; } = null!;
        public JsonObject Front { get; private set; } = null!;
        public JsonObject Back { get; private set; } = null!;
        string ManifestPath => Path.Combine(root, "sites", "intl", "stories", "42.json");
        public Fixture()
        {
            HomeRenderingTests.WriteFixture(root); manifest = Archive.ReadJson(ManifestPath).AsObject();
            var archive = new Archive(root); initialSpot = JsonNode.Parse(archive.Logical("intl", manifest, "host/spot/spot.json"))!.AsObject();
            initialSpot["spineCharacters"]![0]!["sortingOrder"] = 4;
            initialSpot["spineCharacters"]!.AsArray().Add(JsonNode.Parse("""{"path":"home/Alice_back","skeletonData":null,"animation":null,"world":null}"""));
            var record = initialSpot["skeletons"]![0]!.DeepClone(); record["name"] = "alice_back"; record["skeleton"] = "home_alice_back.json";
            initialSpot["skeletons"]!.AsArray().Add(record);
            initialFront = JsonNode.Parse("""
                {"skeleton":{"hash":"verified-home-rear-fixture","spine":"4.2.40","fps":30},"bones":[{"name":"root"},{"name":"moving","parent":"root","x":-1}],
                 "slots":[{"name":"lower","bone":"moving"},{"name":"upper","bone":"moving","attachment":"pixel","color":"ff0000ff"}],
                 "skins":[{"name":"default","attachments":{"upper":{"pixel":{"path":"pixel","width":0.6,"height":0.6}}}}],
                 "animations":{"home_start":{"bones":{"moving":{"translate":[{},{"time":1,"x":2}]}}}}}
                """)!.AsObject();
            initialBack = (JsonObject)initialFront.DeepClone();
            initialBack["slots"] = JsonNode.Parse("""[{"name":"lower","bone":"moving","attachment":"pixel","color":"0000ffff"}]""");
            initialBack["skins"] = JsonNode.Parse("""[{"name":"default","attachments":{"lower":{"pixel":{"path":"pixel","y":-0.2,"width":1,"height":1.2}}}}]""");
            Put("host/spot/spine/page.png", Png.Encode(new(4, 4, Enumerable.Repeat((byte)255, 64).ToArray())));
            Reset(); Save();
        }
        public void Reset() { Spot = (JsonObject)initialSpot.DeepClone(); Front = (JsonObject)initialFront.DeepClone(); Back = (JsonObject)initialBack.DeepClone(); }
        public void Save()
        {
            Put("host/spot/spot.json", JsonOutput.SerializeToUtf8Bytes(Spot));
            Put("host/spot/spine/alice.json", JsonOutput.SerializeToUtf8Bytes(Front));
            Put("host/spot/spine/" + Spot["skeletons"]![1]!["skeleton"], JsonOutput.SerializeToUtf8Bytes(Back));
            File.WriteAllBytes(ManifestPath, JsonOutput.SerializeToUtf8Bytes(manifest));
        }
        void Put(string logical, byte[] bytes)
        {
            string asset = "assets/" + Archive.Hash(bytes) + Path.GetExtension(logical);
            File.WriteAllBytes(Path.Combine(root, "sites", "intl", asset), bytes);
            manifest["files"]![logical] = new JsonObject { ["asset"] = asset, ["size"] = bytes.Length };
        }
        public HomeScene Open() => new HomeCatalog(new Archive(root)).Open(77);
        public void Dispose() => Directory.Delete(root, true);
    }
}
