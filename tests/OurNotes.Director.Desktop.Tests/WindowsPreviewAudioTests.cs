using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.WinUI;

namespace OurNotes.Director.Desktop.Tests;

static class WindowsPreviewAudioTests
{
    static readonly ScenePreviewSource Fixture = new("unused", "unused", "unused", "unused", 64, 64, 1);
    public static Task RunAsync(Action<bool, string> check)
    {
        // Distinct left/right, nonrepeating sample values expose channel swaps,
        // replayed blocks and lost samples without needing an audio device.
        var pcm = new byte[48000 * 4]; var values = MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        for (int i = 0; i < values.Length; i++) values[i] = (short)(((i * 1031L + i * i * 17L) % 30001) - 15000);
        return VerifyTransportAsync(pcm, check, new JsonArray());
    }

    public static async Task<int> RunRealVoiceAsync(string voicePath, string ffmpeg, string outputDirectory)
    {
        outputDirectory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(outputDirectory);
        voicePath = Path.GetFullPath(voicePath);
        var checks = new JsonArray(); var cases = new JsonArray();
        var report = new JsonObject
        {
            ["schema"] = "ournotes-director/audio-output/1", ["ok"] = false,
            ["output"] = "WindowsPreviewAudio pump with a controlled consumed-sample device",
            ["physicalAudioOutputTested"] = false, ["voicePath"] = voicePath,
            ["checks"] = checks, ["cases"] = cases
        };
        void Check(bool result, string name)
        {
            checks.Add(new JsonObject { ["name"] = name, ["ok"] = result });
            if (!result) throw new InvalidOperationException(name);
        }
        try
        {
            var cue = VoiceFile.Decode(voicePath, 1, ffmpeg);
            int frames = Story.Frames(cue.Samples / (double)AdvAudio.SampleRate);
            var loop = new AdvLoop(); var mixer = new AdvAudio(loop, [cue]); mixer.Play(1);
            var mix = new float[AdvAudio.SamplesPerFrame * 2];
            var expected = new byte[frames * AdvAudio.SamplesPerFrame * 4];
            for (int frame = 0; frame < frames; frame++)
            {
                mixer.MixFrame(mix);
                PreviewSoundtrack.ToPcm16(mix, expected.AsSpan(frame * 6400, 6400)); loop.Step();
            }
            float peak = cue.Pcm.Max(sample => Math.Abs(sample));
            report["decodedSampleRate"] = AdvAudio.SampleRate; report["decodedChannels"] = cue.Channels;
            report["decodedSampleFrames"] = cue.Samples; report["decodedPeak"] = peak;
            report["decodedOverRangeSamples"] = cue.Pcm.Count(sample => Math.Abs(sample) > 1);
            report["sourceSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(voicePath)));
            Check(cue.Pcm.Any(sample => Math.Abs(sample) > .001f), "Real voice fixture contains decoded non-silent PCM");
            Check(cue.Samples > 16000, "Real speech spans multiple output refill and recovery boundaries");
            await VerifyTransportAsync(expected, Check, cases);

            var scene = CreativeScene.Blank("intl", "zh-Hans"); scene.Source["adv"] = "43";
            scene.Canvas["width"] = 64; scene.Canvas["height"] = 64;
            double duration = cue.Samples / (double)AdvAudio.SampleRate;
            scene.AddClip("speaker", "voice", 0, duration, new JsonObject
            {
                ["cmd"] = "Voice", ["AudioPath"] = voicePath, ["AudioDuration"] = duration,
                ["IgnoreLipSync"] = true
            }, "Real speech");
            string project = Path.Combine(outputDirectory, "real-voice.project.json");
            File.WriteAllText(project, scene.Compile().ToJsonString());
            var source = new ScenePreviewSource("unused", project, "unused", ffmpeg, 64, 64, frames / 30d);
            ControlledDevice? device = null;
            await using (var output = new WindowsPreviewAudio((wake, count, samples) =>
            {
                var created = new ControlledDevice(wake, count, samples); Volatile.Write(ref device, created); return created;
            }, next => new SoundtrackPcmSource(next)))
            {
                output.Configure(source); output.Start(0);
                await Until(() => Volatile.Read(ref device)?.Running == true, output, "real voice preparation");
                await Drain(device!, output, expected.Length / 4);
                var actual = device!.PlayedBytes;
                Check(actual.AsSpan().SequenceEqual(expected),
                    "Actual decoder, scene mixer, soundtrack spool and Windows pump emit the reference speech PCM sample for sample");
                report["preparedSoundtrack"] = output.Diagnostics();
                report["outputSha256"] = Convert.ToHexString(SHA256.HashData(actual));
                report["referenceSha256"] = Convert.ToHexString(SHA256.HashData(expected));
                report["outputSampleFrames"] = actual.Length / 4;
                WriteWave(Path.Combine(outputDirectory, "real-voice-output.wav"), actual);
                WriteWave(Path.Combine(outputDirectory, "real-voice-reference.wav"), expected);
            }
            report["ok"] = true;
            Console.WriteLine($"Passed {checks.Count} real speech PCM and Windows output queue checks.");
            return 0;
        }
        catch (Exception error) { report["error"] = error.ToString(); Console.Error.WriteLine(error); return 1; }
        finally { File.WriteAllText(Path.Combine(outputDirectory, "report.json"), report.ToJsonString()); }
    }

    static async Task VerifyTransportAsync(byte[] pcm, Action<bool, string> check, JsonArray cases)
    {
        int total = pcm.Length / 4, reserve = WindowsPreviewAudio.BlockSamples * WindowsPreviewAudio.BufferCount;
        if (total <= reserve * 2) throw new ArgumentException("The test requires more than two queue reserves.");
        using (var candidate = new ControlledSource(pcm))
        {
            candidate.Release(total, complete: true); ControlledDevice? pendingDevice = null;
            await using var prepared = Create(candidate, value => Volatile.Write(ref pendingDevice, value));
            prepared.Configure(Fixture, prepareOnly: true);
            prepared.Start(.1); bool resumed = prepared.Resume();
            await Until(() => prepared.Diagnostics()["pumpIterations"]!.GetValue<long>() > 0, prepared, "silent candidate preparation");
            check(pendingDevice is null && !resumed && prepared.PositionSeconds == 0
                && !prepared.Diagnostics()["wanted"]!.GetValue<bool>(),
                "An old renderer Start/Resume cannot open a device or request playback of an unaccepted candidate");
            prepared.Activate();
            check(!prepared.Diagnostics()["wanted"]!.GetValue<bool>() && pendingDevice is null,
                "Accepting prepared audio does not replay a stale Start from before source acceptance");
            prepared.Start(0);
            await Until(() => Volatile.Read(ref pendingDevice)?.Running == true, prepared, "accepted source user play");
            await Drain(pendingDevice!, prepared, total);
            check(pendingDevice!.PlayedBytes.AsSpan().SequenceEqual(pcm),
                "A new user Play after source acceptance starts the prepared PCM from the requested position");
            cases.Add(new JsonObject { ["case"] = "prepare-accept-then-play", ["diagnostics"] = prepared.Diagnostics() });
        }
        using var source = new ControlledSource(pcm);
        ControlledDevice? device = null;
        await using (var output = Create(source, value => Volatile.Write(ref device, value)))
        {
            source.Release(WindowsPreviewAudio.BlockSamples);
            output.Configure(Fixture); output.Start(0);
            await Until(() => output.Diagnostics()["buffering"]!.GetValue<bool>(), output, "first partial reserve");
            check(device is { Running: false, RestartCount: 0 } && device.QueuedSamples == WindowsPreviewAudio.BlockSamples,
                "A first 20 ms speech block remains paused until a playable reserve is prepared");
            device!.Advance(4800);
            check(device.PlayedSamples == 0 && output.PositionSeconds == 0,
                "Waiting for preparation neither emits fragment bursts nor advances the audiovisual clock");
            source.Release(reserve);
            await Until(() => device.Running, output, "startup reserve");
            check(device.LastRestartQueuedSamples >= 5760,
                "Playback starts with at least 120 ms of actual PCM, not one tiny ready fragment");
            device.Advance(3840); // an 80 ms render/scheduling delay exceeds the former entire queue
            check(device.GapSamples == 0,
                "An 80 ms refill scheduling delay consumes retained speech without a device underrun");
            source.Release(total, complete: true);
            await Drain(device, output, total);
            check(device.PlayedBytes.AsSpan().SequenceEqual(pcm) && output.Diagnostics()["underruns"]!.GetValue<int>() == 0,
                "Regular output preserves every signed stereo sample across refill order and block boundaries");
            cases.Add(new JsonObject { ["case"] = "startup-and-delayed-refill", ["diagnostics"] = output.Diagnostics() });
        }

        using var delayed = new ControlledSource(pcm); device = null;
        await using (var output = Create(delayed, value => Volatile.Write(ref device, value)))
        {
            delayed.Release(reserve); output.Configure(Fixture); output.Start(0);
            await Until(() => Volatile.Read(ref device)?.Running == true, output, "underrun fixture start");
            device!.Advance(reserve + 2400); // device also advances its raw clock during this 50 ms gap
            await Until(() => output.Diagnostics()["underruns"]!.GetValue<int>() == 1, output, "exhausted output queue");
            check(!device.Running && output.PositionSeconds == reserve / (double)AdvAudio.SampleRate,
                "A drained queue freezes at the last consumed source sample, excluding driver time spent in silence");
            delayed.Release(reserve + WindowsPreviewAudio.BlockSamples);
            await Until(() => device.QueuedSamples == WindowsPreviewAudio.BlockSamples, output, "partial recovery reserve");
            check(!device.Running && device.RestartCount == 1,
                "A late 20 ms fragment does not restart an exhausted device into repeated short bursts");
            delayed.Release(total, complete: true);
            await Until(() => device.RestartCount == 2, output, "recovery reserve");
            await Drain(device, output, total);
            check(device.PlayedBytes.AsSpan().SequenceEqual(pcm) && device.ResetCount == 1
                && output.PositionSeconds == total / (double)AdvAudio.SampleRate,
                "Underrun recovery resumes the next unplayed source sample with no syllable replay, dropped PCM or clock jump");
            cases.Add(new JsonObject { ["case"] = "forced-underrun-recovery", ["injectedGapSamples"] = device.GapSamples,
                ["diagnostics"] = output.Diagnostics() });
        }

        using var pauseAtDrainSource = new ControlledSource(pcm); device = null;
        await using (var output = Create(pauseAtDrainSource, value => Volatile.Write(ref device, value)))
        {
            pauseAtDrainSource.Release(reserve); output.Configure(Fixture); output.Start(0);
            await Until(() => Volatile.Read(ref device)?.Running == true, output, "pause at drained queue start");
            // Consume the queue and a driver-clock gap immediately before its
            // Pause call takes effect. The pump must see the user pause first;
            // this reproduces that ordering without relying on thread timing.
            device!.SamplesBeforeNextPause = reserve + 2400;
            output.Pause(); await Until(() => !device.Running, output, "pause after device queue drained");
            check(device.PlayedSamples == reserve && device.GapSamples == 2400 && device.ResetCount == 0
                && output.PositionSeconds == reserve / (double)AdvAudio.SampleRate,
                "Pause immediately after a drained device freezes at the last consumed speech sample");
            pauseAtDrainSource.Release(reserve + WindowsPreviewAudio.BlockSamples);
            check(output.Resume(), "A paused empty queue retains the user's ability to resume playback");
            await Until(() => device.QueuedSamples == WindowsPreviewAudio.BlockSamples, output, "paused empty queue recovery block");
            check(!device.Running && device.RestartCount == 1 && device.ResetCount == 1
                && output.PositionSeconds == reserve / (double)AdvAudio.SampleRate,
                "Resuming an empty paused queue rebases its clock and waits for a reserve instead of emitting one late fragment");
            pauseAtDrainSource.Release(total, complete: true);
            await Until(() => device.RestartCount == 2, output, "paused empty queue recovery reserve");
            await Drain(device, output, total);
            check(device.PlayedBytes.AsSpan().SequenceEqual(pcm) && device.ResetCount == 1
                && output.PositionSeconds == total / (double)AdvAudio.SampleRate,
                "Pause-at-underrun recovery preserves all source samples without carrying silent device time into the audiovisual clock");
            cases.Add(new JsonObject { ["case"] = "pause-after-queue-drained", ["injectedGapSamples"] = device.GapSamples,
                ["diagnostics"] = output.Diagnostics() });
        }

        using var pausedSource = new ControlledSource(pcm); pausedSource.Release(total, complete: true); device = null;
        await using (var output = Create(pausedSource, value => Volatile.Write(ref device, value)))
        {
            output.Configure(Fixture); output.Start(0);
            await Until(() => Volatile.Read(ref device)?.Running == true, output, "pause fixture start");
            device!.Advance(1383);
            await Until(() => output.PositionSeconds == 1383d / AdvAudio.SampleRate, output, "partial block clock");
            output.Pause(); await Until(() => !device.Running, output, "pause output");
            int resets = device.ResetCount; device.Advance(4800);
            check(device.PlayedSamples == 1383 && output.Resume(), "Pause retains a partially consumed PCM block for resume");
            await Until(() => device.Running, output, "resume output");
            await Drain(device, output, total);
            check(device.ResetCount == resets && device.PlayedBytes.AsSpan().SequenceEqual(pcm),
                "Pause/resume preserves sub-video-frame speech samples and never resets to the last rendered frame");
            cases.Add(new JsonObject { ["case"] = "pause-resume-partial-block", ["diagnostics"] = output.Diagnostics() });
        }

        using var seekSource = new ControlledSource(pcm); seekSource.Release(total, complete: true); device = null;
        await using (var output = Create(seekSource, value => Volatile.Write(ref device, value)))
        {
            output.Configure(Fixture); output.Start(0);
            await Until(() => Volatile.Read(ref device)?.Running == true, output, "seek fixture start");
            device!.Advance(1383); const int seek = 4321;
            output.Start(seek / (double)AdvAudio.SampleRate);
            await Until(() => device.ResetCount == 1 && device.Running, output, "seek reset");
            await Drain(device, output, 1383 + total - seek);
            byte[] expected = [.. pcm.AsSpan(0, 1383 * 4), .. pcm.AsSpan(seek * 4)];
            check(device.DroppedSamples > 0 && device.PlayedBytes.AsSpan().SequenceEqual(expected)
                && output.PositionSeconds == total / (double)AdvAudio.SampleRate,
                "Explicit seek discards queued old audio and starts exactly at the new sample position");
            cases.Add(new JsonObject { ["case"] = "seek-with-queued-output", ["diagnostics"] = output.Diagnostics() });
        }

        byte[] ending = pcm[..(3171 * 4)];
        using var endingSource = new ControlledSource(ending); endingSource.Release(3171, complete: true); device = null;
        await using (var output = Create(endingSource, value => { value.PositionQuantumSamples = 48; Volatile.Write(ref device, value); }))
        {
            output.Configure(Fixture); output.Start(0);
            await Until(() => Volatile.Read(ref device)?.Running == true, output, "short complete ending");
            await Drain(device!, output, 3171);
            check(device!.PlayedBytes.AsSpan().SequenceEqual(ending) && output.Diagnostics()["underruns"]!.GetValue<int>() == 0
                && output.PositionSeconds == 3171d / AdvAudio.SampleRate,
                "A partial final buffer ends sample-exactly even with a coarse millisecond device clock, without truncation or a stalled video EOF");
            cases.Add(new JsonObject { ["case"] = "short-final-buffer", ["diagnostics"] = output.Diagnostics() });
        }
    }

    static WindowsPreviewAudio Create(ControlledSource source, Action<ControlledDevice> capture) => new(
        (wake, count, samples) => { var device = new ControlledDevice(wake, count, samples); capture(device); return device; }, _ => source);

    static async Task Drain(ControlledDevice device, WindowsPreviewAudio output, int target)
    {
        var clock = Stopwatch.StartNew();
        while (device.PlayedSamples < target)
        {
            await Until(() => device.Running && device.QueuedSamples > 0, output, "queued output to consume");
            int count = Math.Min(960, target - device.PlayedSamples);
            long iteration = output.Diagnostics()["pumpIterations"]!.GetValue<long>();
            device.Advance(Math.Min(count, device.QueuedSamples));
            await Until(() => output.Diagnostics()["pumpIterations"]!.GetValue<long>() > iteration, output, "refill completed buffer");
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Output did not reach the expected source sample.");
        }
    }
    static async Task Until(Func<bool> ready, WindowsPreviewAudio output, string stage)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            var diagnostics = output.Diagnostics();
            if (diagnostics["failed"]!.GetValue<bool>()) throw new IOException(diagnostics.ToJsonString());
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(stage + ": " + diagnostics.ToJsonString());
            await Task.Delay(1);
        }
    }

    sealed class ControlledSource(byte[] pcm) : IPreviewPcmSource, IDisposable
    {
        long available; int complete;
        public long AvailableSamples => Interlocked.Read(ref available);
        public bool IsComplete => Volatile.Read(ref complete) != 0;
        public Exception? Failure => null;
        public void RequestThrough(long samples) { }
        public void Release(int samples, bool complete = false)
        { Interlocked.Exchange(ref available, samples); if (complete) Volatile.Write(ref this.complete, 1); }
        public int Read(long startSample, Span<byte> destination)
        {
            long ready = AvailableSamples - startSample; int count = destination.Length / 4;
            if (ready < count && !IsComplete) return -1;
            count = (int)Math.Min(count, Math.Max(0, ready));
            pcm.AsSpan(checked((int)startSample * 4), count * 4).CopyTo(destination); return count;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    sealed class ControlledDevice(AutoResetEvent wake, int count, int samples) : IPreviewAudioDevice
    {
        readonly object gate = new();
        readonly byte[][] buffers = Enumerable.Range(0, count).Select(_ => new byte[samples * 4]).ToArray();
        readonly bool[] queued = new bool[count];
        readonly Queue<Block> blocks = new();
        readonly MemoryStream played = new();
        bool running; long position; int resets, restarts, gaps, dropped, restartQueued, samplesBeforePause;
        sealed class Block(int index, byte[] bytes) { public int Index = index, Offset; public byte[] Bytes = bytes; }
        public string Name => "Controlled consumed-sample device (no physical output)";
        public bool MultimediaScheduling => false;
        public bool Running { get { lock (gate) return running; } }
        public int SamplesBeforeNextPause { set => Interlocked.Exchange(ref samplesBeforePause, value); }
        public int PositionQuantumSamples { get; set; } = 1;
        public long PositionSamples { get { lock (gate) return position / PositionQuantumSamples * PositionQuantumSamples; } }
        public int ResetCount { get { lock (gate) return resets; } }
        public int RestartCount { get { lock (gate) return restarts; } }
        public int LastRestartQueuedSamples { get { lock (gate) return restartQueued; } }
        public int GapSamples { get { lock (gate) return gaps; } }
        public int DroppedSamples { get { lock (gate) return dropped; } }
        public int PlayedSamples { get { lock (gate) return checked((int)played.Length / 4); } }
        public byte[] PlayedBytes { get { lock (gate) return played.ToArray(); } }
        public int QueuedSamples { get { lock (gate) return blocks.Sum(block => (block.Bytes.Length - block.Offset) / 4); } }
        public bool IsQueued(int index) { lock (gate) return queued[index]; }
        public Span<byte> Buffer(int index) => buffers[index];
        public void Submit(int index, int bytes)
        {
            lock (gate)
            {
                if (queued[index]) throw new InvalidOperationException("Overwrote a live output buffer.");
                queued[index] = true; blocks.Enqueue(new(index, buffers[index][..bytes]));
            }
        }
        public void Pause()
        {
            int samples = Interlocked.Exchange(ref samplesBeforePause, 0);
            if (samples > 0) Advance(samples);
            lock (gate) running = false;
        }
        public void Restart() { lock (gate) { running = true; restarts++; restartQueued = QueuedSamples; } }
        public void Reset()
        {
            lock (gate)
            { dropped += QueuedSamples; blocks.Clear(); Array.Clear(queued); position = 0; resets++; running = false; }
        }
        public void Advance(int samples)
        {
            lock (gate)
            {
                if (!running) return;
                position += samples; // intentionally include underflow silence in the raw driver clock
                while (samples > 0 && blocks.TryPeek(out var block))
                {
                    int frames = Math.Min(samples, (block.Bytes.Length - block.Offset) / 4);
                    played.Write(block.Bytes, block.Offset, frames * 4); block.Offset += frames * 4; samples -= frames;
                    if (block.Offset == block.Bytes.Length) { queued[block.Index] = false; blocks.Dequeue(); }
                }
                gaps += samples;
            }
            wake.Set();
        }
        public void Dispose() { lock (gate) running = false; }
    }

    static void WriteWave(string path, byte[] pcm)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(192000);
        writer.Write((short)4); writer.Write((short)16); writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
    }
}
