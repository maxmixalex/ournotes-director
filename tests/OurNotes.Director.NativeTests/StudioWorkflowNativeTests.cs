using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class StudioWorkflowNativeTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string description)
        { if (!condition) throw new InvalidOperationException(description); checks++; }
        string root = Path.Combine(Path.GetTempPath(), "studio-workflow-" + Guid.NewGuid().ToString("N"));
        try
        {
            RealtimeSceneFixture.Write(root);
            string library = Path.Combine(root, "sites", "intl", "director"); Directory.CreateDirectory(library);
            File.Copy(Path.Combine(root, "sites", "intl", "stories", "43.json"), Path.Combine(library, "manifest.json"));
            var original = RealtimeSceneFixture.Document();
            string normalPath = Path.Combine(root, "source.json"), freePath = Path.Combine(root, "free.json");
            File.WriteAllText(normalPath, original.ToJsonString());
            var free = original.DeepClone().AsObject(); free["source"]!.AsObject().Remove("adv");
            File.WriteAllText(freePath, free.ToJsonString());
            Pixels source;
            using (var renderer = new AdvStoryRenderer(new Archive(root), "intl", "", "43", "zh-Hans", 128, 96, normalPath, "", 1))
                source = renderer.Frame(15, true);
            using (var renderer = new AdvStoryRenderer(new Archive(root), "intl", "", DirectorProject.LibrarySource, "zh-Hans", 128, 96, freePath, "", 1))
            {
                var direct = renderer.Frame(15, true);
                Check(source.Rgba.SequenceEqual(direct.Rgba), "Library source resolves the same actual resources without requiring an original story ID");
                if (OperatingSystem.IsLinux())
                {
                    renderer.PreferGpuPresentation = true;
                    var fallback = renderer.Frame(15, true);
                    Check(renderer.GpuSurface is null && renderer.GpuFallbackReason is not null && fallback.Rgba.SequenceEqual(direct.Rgba),
                        "Unsupported GPU presentation falls back to exactly the same alpha pixels");
                }
                renderer.DisableGpuPresentation("explicit regression fallback");
                Check(renderer.Frame(15, true).Rgba.SequenceEqual(direct.Rgba), "Explicit presentation fallback preserves the current frame and scene");
            }
            free["initialize"] = new JsonArray(new JsonObject { ["cmd"] = "Director.Scene", ["Values"] = new JsonObject { ["background"] = new JsonArray(0, 0, 0, 0) } });
            free["commands"] = new JsonArray(); free["finalize"] = new JsonArray();
            File.WriteAllText(freePath, free.ToJsonString());
            using (var renderer = new AdvStoryRenderer(new Archive(root), "intl", "", DirectorProject.LibrarySource, "zh-Hans", 128, 96, freePath, "", 1))
                Check(renderer.Frame(0, true).Rgba.All(value => value == 0), "Empty explicit sections render a genuinely empty transparent library scene");
            CheckGpuComposite(Check);
            Console.WriteLine($"Passed {checks} blank-scene, pixel-fallback and GPU-preview shader checks."); return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void CheckGpuComposite(Action<bool, string> check)
    {
        // Compile and execute the SAME preview shader on an actual GL context, even on
        // a GPU-less Linux runner. This tests shader pixels, not Windows hardware interop.
        using var gl = new OffscreenGl(64, 48);
        using var shader = new UnityShader("GPU preview regression", OffscreenGl.PreviewCompositeSource);
        var bytes = new byte[64 * 48 * 4];
        for (int y = 0; y < 48; y++) for (int x = 0; x < 64; x++)
        {
            int p = (y * 64 + x) * 4;
            bytes[p] = x < 16 ? (byte)0 : (byte)64; bytes[p + 1] = 0;
            bytes[p + 2] = y < 24 ? (byte)0 : (byte)32; bytes[p + 3] = x < 16 ? (byte)0 : (byte)128;
            if (bytes[p + 3] == 0) bytes[p + 2] = 0;
        }
        var source = gl.Upload(new(64, 48, bytes)); var target = gl.Surface(64, 48);
        gl.Begin([0, 0, 0, 0]);
        gl.Blit(shader, target, new Dictionary<string, float[]> { ["scale"] = [1], ["outputHeight"] = [48] }, new Dictionary<string, uint> { ["sourceImage"] = source });
        gl.CopySurface(target, gl.CurrentSurface);
        var composite = gl.Present(64, 48, 1, 0, (_, _, _) => { });
        check(composite.Rgba.Where((_, index) => index % 4 == 3).All(alpha => alpha == 255), "GPU preview shader produces the opaque checkerboard required by SwapChainPanel");
        check(composite.Rgba.Where((_, index) => index % 4 == 0).Distinct().Count() >= 4,
            "GPU preview preserves image contribution and alternating checker cells");
        // Alpha conversion is preview-only. A fresh normal frame must remain transparent.
        gl.Begin([0, 0, 0, 0]);
        var exported = gl.Present(64, 48, 1, 0, (_, _, _) => { }, true);
        check(exported.Rgba.All(value => value == 0), "Preview checker composition does not leak into later transparent output");
    }
}
