using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class RuntimeDependencyTests
{
    public static int Run(string packageDirectory)
    {
        var directory = Path.GetFullPath(packageDirectory);
        var core = NativeDependencies.CoreLibrary(baseDirectory: directory);
        var version = NativeDependencies.ReadCoreVersion(core);
        if (version != 0x06000001) throw new Exception($"Packaged Core version changed: 0x{version:x8}.");
        if (!Path.GetFullPath(core).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new Exception("Core was loaded from outside the package.");
        Console.WriteLine($"Packaged Cubism Core loaded: 0x{version:x8}.");
        ManagedLipSyncTests.Run();
        if (!OperatingSystem.IsWindows())
        {
            if (NativeDependencies.MotionSyncLibrary(core, directory) is not null) throw new Exception("Linux must use C# CRI Lips; no Linux MotionSync SDK exists.");
            Console.WriteLine("Linux uses the C# CRI Lips analyser; no incompatible Android/Windows MotionSync library was loaded.");
            return 0;
        }
        var motionSync = NativeDependencies.MotionSyncLibrary(core, directory) ?? throw new Exception("Packaged MotionSync DLL is missing.");
        if (NativeMotionSync.ProbeVersion(motionSync) != 0x05000004) throw new Exception("Wrong MotionSync ABI.");
        var mappings = new JsonArray();
        var audio = new JsonArray();
        foreach (var phoneme in new[] { "Silence", "A", "I", "U", "E", "O" })
        {
            mappings.Add((JsonNode)new JsonObject
            {
                ["AudioParameterId"] = phoneme,
                ["Targets"] = new JsonArray(new JsonObject
                {
                    ["Parameter"] = new JsonObject { ["gameObject"] = "Parameters/ParamMouthOpenY" },
                    ["Value"] = phoneme == "Silence" ? 0 : 1
                })
            });
            audio.Add((JsonNode)new JsonObject { ["Scale"] = 1, ["Enabled"] = true });
        }
        var setting = new JsonObject
        {
            ["CubismParameters"] = new JsonArray(new JsonObject { ["Parameter"] = new JsonObject { ["gameObject"] = "Parameters/ParamMouthOpenY" } }),
            ["Mappings"] = mappings,
            ["AudioParameters"] = audio,
            ["PostProcessing"] = new JsonObject { ["BlendRatio"] = .5, ["Smoothing"] = 0 },
            ["EmphasisLevel"] = 0
        };
        using var analysis = new NativeMotionSync(motionSync, setting);
        var pcm = new float[96000];
        for (var i = 0; i < pcm.Length; i++)
        {
            var t = i / 48000f;
            var envelope = .5f + .5f * MathF.Sin(2 * MathF.PI * 3 * t);
            pcm[i] = envelope * (.35f * MathF.Sin(2 * MathF.PI * 180 * t) + .2f * MathF.Sin(2 * MathF.PI * 720 * t) + .1f * MathF.Sin(2 * MathF.PI * 1260 * t));
        }
        var callbacks = 0;
        var peak = 0f;
        for (var offset = 0; offset < pcm.Length; offset += 1600)
            analysis.Feed(pcm.AsSpan(offset, 1600), values =>
            {
                if (values.Length != 1 || !float.IsFinite(values[0])) throw new Exception("MotionSync returned invalid PCM analysis output.");
                callbacks++;
                peak = Math.Max(peak, values[0]);
            });
        if (callbacks == 0 || peak <= 0) throw new Exception($"MotionSync did not analyse the voiced PCM: callbacks={callbacks}, peak={peak}.");
        Console.WriteLine($"Packaged MotionSync 5.0.4 analysed 96,000 PCM samples: {callbacks} callbacks, peak mouth opening {peak:G6}.");
        return 0;
    }
}
