using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class ManagedLipSyncTests
{
    // Deliberately synthetic constant classifiers: this tests the PCM/DSP,
    // silence gate, streaming and Native AOT path without distributing game
    // weights. It does not claim to validate phoneme recognition accuracy.
    static CriLipsData Fixture()
    {
        var values = new List<float>();
        var blocks = new JsonArray();
        void Block(string name, int count, float value = 0, bool firstOnly = false)
        {
            blocks.Add((JsonNode)new JsonObject { ["name"] = name, ["offset"] = values.Count, ["count"] = count });
            for (var i = 0; i < count; i++) values.Add(!firstOnly || i == 0 ? value : 0);
        }
        void Dense(string name, int input, int output, bool selectFirst = false)
        {
            Block(name + ".w", input * output);
            Block(name + ".b", output, selectFirst ? 8 : 0, selectFirst);
        }
        void Norm(string name, int count)
        {
            Block(name + ".beta", count);
            Block(name + ".gamma", count, 1);
            Block(name + ".mean", count);
            Block(name + ".var", count, 1);
        }
        Block("net1.conv.w", 90);
        Block("net1.conv.b", 10);
        Dense("net1.d1", 220, 128);
        Dense("net1.d2", 128, 128);
        Dense("net1.d3", 128, 128);
        Dense("net1.d4", 128, 24, true);
        Dense("net3.d1", 72, 40);
        Norm("net3.bn1", 40);
        Dense("net3.d2", 40, 20);
        Norm("net3.bn2", 20);
        Dense("net3.d3", 20, 5, true);
        var motionClasses = new JsonArray();
        var types = new JsonArray();
        for (var i = 0; i < 24; i++) { motionClasses.Add((JsonNode)4); types.Add((JsonNode)0); }
        types.Add((JsonNode)4);
        var descriptor = new JsonObject
        {
            ["dtype"] = "float32", ["order"] = "LE", ["total_floats"] = values.Count, ["blocks"] = blocks,
            ["frontend"] = new JsonObject
            {
                ["biquad_lpf_cutoff_hz"] = 4000, ["resample_hz"] = 16000, ["server_hz"] = 30,
                ["update_rate_hz"] = 100, ["hop_ms"] = 10, ["frame_ms"] = 20, ["mel_bands"] = 24,
                ["mel_high_hz"] = 7500, ["window_internal_class"] = "blackman-harris", ["window_vowel"] = "hamming",
                ["silence_threshold_db"] = -50, ["labial_hold_sec"] = 0, ["motion_class_buffer"] = 3,
                ["mouth_open"] = new JsonObject { ["vowel_coef"] = new JsonArray(1, 0, 0, 0, 0), ["anti_shake_sec"] = .02 },
                ["discretizer"] = new JsonObject
                {
                    ["suppression"] = .5, ["smoothing_sec"] = .01, ["release_sec"] = .01,
                    ["hold_sec"] = 0, ["max_window_sec"] = .1, ["wildcard"] = new JsonArray(0, 0, 0, 0, 0, 1)
                }
            },
            ["constants"] = new JsonObject
            {
                ["antiflap_defaults"] = new JsonArray(1, 1, 10, 0), ["silence_coef"] = 1000,
                ["motion_class_table"] = motionClasses, ["discretizer_type_table"] = types
            }
        };
        var blob = new byte[values.Count * sizeof(float)];
        Buffer.BlockCopy(values.ToArray(), 0, blob, 0, blob.Length);
        return new CriLipsData(descriptor, blob);
    }

    public static void Run()
    {
        var data = Fixture();
        var silence = new CriLipsAnalyzer(data);
        if (silence.Feed(new float[48000]) != 0) throw new Exception("Managed CRI Lips opened the mouth for silence.");
        var pcm = new float[96000];
        for (var i = 0; i < 48000; i++) pcm[i] = .4f * MathF.Sin(2 * MathF.PI * 440 * i / 48000);
        var analysis = new CriLipsAnalyzer(data);
        var peak = 0f;
        var ending = 0f;
        for (var offset = 0; offset < pcm.Length; offset += 1600)
        {
            ending = analysis.Feed(pcm.AsSpan(offset, 1600));
            if (!float.IsFinite(ending)) throw new Exception("Managed CRI Lips returned a non-finite result.");
            peak = Math.Max(peak, ending);
        }
        if (peak < .1f || ending > .001f) throw new Exception($"Managed CRI Lips did not distinguish signal from silence: peak={peak}, ending={ending}.");
        var chunked = new CriLipsAnalyzer(data);
        var chunkedEnding = 0f;
        for (var offset = 0; offset < pcm.Length; offset += 137)
            chunkedEnding = chunked.Feed(pcm.AsSpan(offset, Math.Min(137, pcm.Length - offset)));
        if (Math.Abs(chunkedEnding - ending) > 1e-7f) throw new Exception("Managed CRI Lips depends on PCM input chunk sizes.");
        Console.WriteLine($"Managed CRI Lips analysed 96,000 PCM samples with synthetic test weights: peak={peak:G6}, silence={ending:G6}; stream chunking passed.");
    }
}
