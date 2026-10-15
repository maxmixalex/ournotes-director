using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class VoiceDecodeCacheTests
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "voice-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "voice.wav"); File.WriteAllText(path, "fixture");
            var original = Enumerable.Range(0, 48000).Select(i => i / 48000f).ToArray();
            var cache = new VoiceDecodeCache(decoder: (_, _) => new(0, 2, original, 1));
            var audio = new AdvAudio(new AdvLoop(), []);
            var bank = new ExternalVoiceBank(audio, root, "decoder", cache);
            AdvCue Read(ExternalVoiceBank owner, AdvAudio mixer, double start, double duration)
            {
                var row = owner.Resolve(AdvProgram.Row(new JsonObject { ["cmd"] = "Voice", ["AudioPath"] = path,
                    ["AudioIn"] = start, ["AudioDuration"] = duration }, "voice"));
                return mixer.GetCue(row.Data["VoiceIDs"]![0]!.GetValue<long>());
            }
            var left = Read(bank, audio, .1, .2); var right = Read(bank, audio, .3, .2);
            check(left.Samples == 9600 && right.Samples == 9600,
                "Voice cuts use double sample endpoints, not float duration ceilings");
            check(left.Pcm.Concat(right.Pcm).SequenceEqual(original.AsSpan(4800, 19200).ToArray()),
                "Adjacent voice cuts reproduce the exact original source samples without a duplicate or gap");
            var replayAudio = new AdvAudio(new AdvLoop(), []);
            var replay = new ExternalVoiceBank(replayAudio, root, "decoder", cache);
            check(Read(replay, replayAudio, .1, .4).Pcm.SequenceEqual(left.Pcm.Concat(right.Pcm)) && cache.DecodeCount == 1,
                "Replay and different crop ranges share one decoded file");
            check(original[4800] == .1f, "Cropping does not overwrite the shared PCM");

            var all = VoiceSampleRange.FromSeconds(0, 1d / 30, 48000);
            var next = VoiceSampleRange.FromSeconds(1d / 30, 1d / 30, 48000);
            check(all.Count == 1600 && next.Offset == 1600 && next.Count == 1600,
                "Thirty-fps voice boundaries remain precisely 1600 samples apart");
            check(VoiceSampleRange.FromSeconds(.75, 0, 48000) == new VoiceSampleRange(36000, 12000),
                "Omitted duration retains the remainder of a voice file");
            bool rejected = false;
            try { VoiceSampleRange.FromSeconds(1, .2, 48000); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Voice in points at EOF cannot create empty output");

            File.AppendAllText(path, "modified"); _ = cache.Read(path, "decoder");
            check(cache.DecodeCount == 2, "Replacing a voice file invalidates its shared decode");
            _ = cache.Read(path, "other-decoder");
            check(cache.DecodeCount == 3, "Changing the decoder cannot reuse another decoder's PCM");
            var small = new VoiceDecodeCache(16, (_, _) => new(0, 2, new float[4], 1));
            string other = Path.Combine(root, "other.wav"); File.WriteAllText(other, "fixture2");
            _ = small.Read(path, "d"); _ = small.Read(other, "d"); _ = small.Read(path, "d");
            check(small.RetainedBytes <= 16 && small.DecodeCount == 3, "Decoded audio uses a bounded least-recently-used cache");
            int failures = 0;
            var retry = new VoiceDecodeCache(decoder: (_, _) => ++failures == 1
                ? throw new IOException("intentional decoder failure") : new(0, 2, original, 1));
            try { _ = retry.Read(path, "d"); } catch (IOException) { }
            check(retry.Read(path, "d").Pcm.Length == 48000 && retry.DecodeCount == 2,
                "A failed decode is evicted and can be retried");
            var parallel = new VoiceDecodeCache(decoder: (_, _) => { Thread.Sleep(20); return new(0, 2, original, 1); });
            var requests = Enumerable.Range(0, 8).Select(_ => Task.Run(() => parallel.Read(path, "d"))).ToArray();
            Task.WaitAll(requests);
            check(parallel.DecodeCount == 1 && requests.All(task => ReferenceEquals(task.Result.Pcm, original)),
                "Concurrent soundtrack and viewport requests share the same in-flight decode");
        }
        finally { Directory.Delete(root, true); }
    }
}
