using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

/// <summary>Real child-process integration tests, independent of archives, graphics drivers and installed FFmpeg.</summary>
public static class EngineRunnerTests
{
    const string WorkerFlag = "--worker-fixture";
    const string MediaEnvironment = "OURNOTES_DIRECTOR_TEST_MEDIA_WORKER";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Call before normal test dispatch. Returns null in the normal parent test process.</summary>
    public static async Task<int?> HandleWorkerAsync(string[] args)
    {
        int marker = Array.IndexOf(args, WorkerFlag);
        if (marker < 0)
        {
            if (Environment.GetEnvironmentVariable(MediaEnvironment) == "1" && args.FirstOrDefault() == "-hide_banner")
                return await MediaWorkerAsync(args);
            return null;
        }
        Console.OutputEncoding = new UTF8Encoding(false);
        string mode = marker + 1 < args.Length ? args[marker + 1] : "";
        string? Opt(string key) => Option(args, key);
        if (mode == "child")
        {
            Signal(Opt("--ready"), ProcessIdentity());
            await Task.Delay(System.Threading.Timeout.Infinite); return 0;
        }
        if (mode is "tree" or "tree-exit")
        {
            using var child = new Process { StartInfo = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true } };
            foreach (string arg in new[] { WorkerFlag, "child", "--ready", Opt("--child-ready")! }) child.StartInfo.ArgumentList.Add(arg);
            child.Start();
            var identity = ProcessIdentity(); identity["childPid"] = child.Id;
            Signal(Opt("--ready"), identity);
            if (mode == "tree-exit") { WriteResult(new() { ["childPid"] = child.Id }); return 0; }
            await Task.Delay(System.Threading.Timeout.Infinite); return 0;
        }
        if (mode == "invalid")
        {
            Console.Write(Opt("--payload") ?? "not JSON"); return 0;
        }
        if (mode == "help")
        {
            Console.Write(new JsonObject { ["schema"] = "ournotes-director/1", ["commands"] = new JsonArray("fixture help") }.ToJsonString());
            return 0;
        }
        if (mode == "check")
        {
            var story = new JsonObject { ["id"] = "fixture", ["compiled"] = true, ["initialized"] = true };
            story[Opt("--field") ?? "initialized"] = false;
            WriteResult(new() { ["stories"] = new JsonArray(story) }); return 0;
        }
        if (mode == "fail")
        {
            Console.Error.WriteLine("fixture stderr 错误详情");
            WriteError("fixture 请求失败"); return int.Parse(Opt("--exit") ?? "3", CultureInfo.InvariantCulture);
        }

        var result = new JsonObject
        {
            ["arguments"] = new JsonArray(args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
            ["workingDirectory"] = Environment.CurrentDirectory, ["pid"] = Environment.ProcessId
        };
        if (mode is "file" or "file-fail" or "partial" or "directory" or "directory-fail")
        {
            string output = Opt("--out") ?? Opt("-o") ?? throw new ArgumentException("fixture output missing");
            result["output"] = output;
            if (mode is "directory" or "directory-fail")
            {
                Directory.CreateDirectory(Path.Combine(output, "nested"));
                File.WriteAllText(Path.Combine(output, "frame000001.png"), "fixture frame");
                File.WriteAllText(Path.Combine(output, "render.json"), new JsonObject
                { ["output"] = output, ["completedFrames"] = 1 }.ToJsonString());
                File.WriteAllText(Path.Combine(output, "nested", "index.json"), new JsonObject
                {
                    ["output"] = output, ["frame"] = Path.Combine(output, "frame000001.png"),
                    ["large"] = JsonNode.Parse("123456789012345678901234567890")
                }.ToJsonString());
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, "fixture primary");
                if (args.Contains("--sidecars", StringComparer.Ordinal))
                {
                    File.WriteAllText(output + ".source.json", new JsonObject
                    {
                        ["output"] = output, ["sidecar"] = output + ".source.json",
                        ["large"] = JsonNode.Parse("123456789012345678901234567890")
                    }.ToJsonString());
                    File.WriteAllText(output + ".captions.srt", "1\n00:00:00,000 --> 00:00:01,000\n字幕\n");
                }
            }
        }
        var ready = ProcessIdentity(); ready["output"] = result["output"]?.DeepClone();
        Signal(Opt("--ready"), ready);
        if (mode is "hold" or "partial") await Task.Delay(System.Threading.Timeout.Infinite);
        if (Opt("--gate") is { } gate) await UntilAsync(() => File.Exists(gate));
        if (mode == "input")
        {
            string input = args.FirstOrDefault() == "render" ? args[2] : Opt("--script") ?? throw new ArgumentException("fixture input missing");
            result["inputPath"] = input; result["input"] = JsonNode.Parse(File.ReadAllText(input));
        }
        if (mode is "file-fail" or "directory-fail") { WriteError("fixture 写入之后失败"); return 3; }
        Console.Error.WriteLine("story render 1/4");
        Console.Error.WriteLine("[assets] Unicode 输出与进度");
        if (args.Contains("--flood", StringComparer.Ordinal))
        {
            result["payload"] = new string('x', 220_000);
            string json = Envelope(result).ToJsonString();
            await Task.WhenAll(Console.Out.WriteAsync(json), Task.Run(async () =>
            {
                for (int index = 0; index < 180; index++) await Console.Error.WriteLineAsync(new string('e', 1200));
            }));
        }
        else WriteResult(result);
        return 0;
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ournotes-worker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string? previousDotnet = Environment.GetEnvironmentVariable("DOTNET_ROOT"), previousDotnetX64 = Environment.GetEnvironmentVariable("DOTNET_ROOT_X64");
        try
        {
            string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Test host path is unavailable");
            string executable = processPath;
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                // Run the built apphost so actual CLI-style positional argument indexes remain unchanged.
                executable = Path.Combine(AppContext.BaseDirectory, "OurNotes.Director.Desktop.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
                string runtimeDirectory = Path.GetDirectoryName(processPath)!;
                Environment.SetEnvironmentVariable("DOTNET_ROOT", runtimeDirectory);
                Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", runtimeDirectory);
            }
            check(File.Exists(executable), "desktop integration fixture can launch its own executable");
            var runner = new EngineRunner(executable);
            await ProcessCasesAsync(runner, folder, check);
            await OutputCasesAsync(runner, folder, check);
            await CancellationCasesAsync(runner, folder, check);
            await QueueCasesAsync(runner, folder, check);
            SettingsCases(folder, check);
            await PreviewCasesAsync(executable, folder, check);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT", previousDotnet);
            Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", previousDotnetX64);
            Directory.Delete(folder, true);
        }
    }

    static async Task ProcessCasesAsync(EngineRunner runner, string folder, Action<bool, string> check)
    {
        string working = Path.Combine(folder, "工作目录 with spaces"); Directory.CreateDirectory(working);
        string sentinel = Path.Combine(folder, "must-not-be-created");
        string[] unusual = ["space here", "中文日本語", "\"quoted\"", "a\\b\\", "line\nbreak", "; & |", "$(touch " + sentinel + ")", "`echo literal`", ""];
        var progress = new List<EngineProgress>();
        var request = Request("echo", working, unusual);
        var result = await RunAsync(runner, request, progress.Add);
        check(result.Success && result.ExitCode == 0 && !result.Cancelled && result.Result!["arguments"]!.AsArray().Select(value => value!.GetValue<string>()).SequenceEqual(request.Arguments) &&
            result.Result["workingDirectory"]!.GetValue<string>() == working && !File.Exists(sentinel),
            "desktop runner passes exact ArgumentList tokens and working directory without shell interpretation");
        check(progress.Any(item => item.Fraction == .25 && item.Message.Contains("正在渲染", StringComparison.Ordinal)) && result.Log.Contains("Unicode 输出", StringComparison.Ordinal),
            "desktop runner drains UTF-8 stderr and reports translated frame progress");
        result = await RunAsync(runner, Request("echo", folder, ["--flood"]));
        check(result.Success && result.Result!["payload"]!.GetValue<string>().Length == 220_000 && result.Log.Length > 100_000,
            "desktop runner concurrently drains stdout and stderr beyond pipe capacity");
        foreach (int exit in new[] { 2, 3, 4 })
        {
            result = await RunAsync(runner, Request("fail", folder, ["--exit", exit.ToString(CultureInfo.InvariantCulture)]));
            check(!result.Success && !result.Cancelled && result.ExitCode == exit && result.Error!.Contains("fixture 请求失败", StringComparison.Ordinal) && result.Json?["ok"]?.ToString() == "false",
                "desktop runner preserves structured error details and native exit code " + exit);
        }
        foreach (string payload in new[] { "not-json", "[]", "null", "{}", "{\"ok\":true}", "{\"schema\":\"ournotes-director/1\",\"ok\":false}" })
        {
            result = await RunAsync(runner, Request("invalid", folder, ["--payload", payload]));
            check(!result.Success && result.Error is { Length: > 0 }, "desktop runner rejects incomplete or failed result envelope: " + payload);
        }
        result = await RunAsync(runner, Request("help", folder) with { CommandId = "help" });
        check(result.Success && result.Json!["commands"] is JsonArray, "desktop runner accepts the documented help envelope without ok");
        foreach (string field in new[] { "compiled", "initialized" })
        {
            result = await RunAsync(runner, Request("check", folder, ["--field", field]) with { CommandId = "render.validate" });
            check(!result.Success && result.ExitCode == 0 && result.Json is not null,
                "desktop native validation report with " + field + "=false is not presented as successful validation");
        }
        result = await RunAsync(new EngineRunner(Path.Combine(folder, "missing-engine")), Request("echo", folder));
        check(!result.Success && result.Error!.Contains("advkit", StringComparison.Ordinal), "desktop missing-engine failure is actionable and does not throw out of the runner");
    }

    static async Task OutputCasesAsync(EngineRunner runner, string folder, Action<bool, string> check)
    {
        string outputFolder = Path.Combine(folder, "outputs"); Directory.CreateDirectory(outputFolder);
        string file = Path.Combine(outputFolder, "成片 with spaces.mov");
        var request = Request("file", folder, ["-o", file, "--sidecars"], file, CommandOutputKind.Video);
        var result = await RunAsync(runner, request);
        check(result.Success && File.ReadAllText(file) == "fixture primary" && File.Exists(file + ".source.json") && File.Exists(file + ".captions.srt"),
            "desktop file commit includes native provenance and subtitle sidecars");
        var provenance = JsonNode.Parse(File.ReadAllText(file + ".source.json"))!;
        check(result.Result!["output"]!.GetValue<string>() == file && provenance["output"]!.GetValue<string>() == file &&
            provenance["sidecar"]!.GetValue<string>() == file + ".source.json" && provenance["large"]!.ToJsonString() == "123456789012345678901234567890" && NoStaging(outputFolder),
            "desktop commits rewrite staging references in results and sidecars without narrowing numeric tokens");
        string rejectedSignal = Path.Combine(folder, "rejected-worker.json");
        result = await RunAsync(runner, request with { Arguments = [WorkerFlag, "file", "-o", file, "--ready", rejectedSignal] });
        check(!result.Success && File.ReadAllText(file) == "fixture primary" && !File.Exists(rejectedSignal),
            "desktop existing-file rejection happens before process launch and preserves existing output");
        File.WriteAllText(file, "original user primary"); File.WriteAllText(file + ".source.json", "original user metadata");
        result = await RunAsync(runner, Request("file-fail", folder, ["-o", file, "--overwrite", "--sidecars"], file, CommandOutputKind.Video));
        check(!result.Success && File.ReadAllText(file) == "original user primary" && File.ReadAllText(file + ".source.json") == "original user metadata" && NoStaging(outputFolder),
            "desktop failed renderer never replaces existing main output or sidecars");

        string collision = Path.Combine(outputFolder, "collision.mov"); File.WriteAllText(collision, "keep original");
        Directory.CreateDirectory(collision + ".source.json");
        result = await RunAsync(runner, Request("file", folder, ["-o", collision, "--overwrite", "--sidecars"], collision, CommandOutputKind.Video));
        check(!result.Success && File.ReadAllText(collision) == "keep original" && Directory.Exists(collision + ".source.json") &&
            !File.Exists(collision + ".captions.srt") && NoStaging(outputFolder),
            "desktop preflights the entire output bundle before replacing any user file");

        string directory = Path.Combine(outputFolder, "sequence");
        result = await RunAsync(runner, Request("directory", folder, ["-o", directory], directory, CommandOutputKind.Directory));
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "nested", "index.json")))!;
        check(result.Success && index["output"]!.GetValue<string>() == directory && index["frame"]!.GetValue<string>() == Path.Combine(directory, "frame000001.png") && NoStaging(outputFolder),
            "desktop directory commits preserve nested files and rewrite metadata to final paths");
        result = await RunAsync(runner, Request("directory", folder, ["-o", directory], directory, CommandOutputKind.Directory));
        check(!result.Success && File.Exists(Path.Combine(directory, "frame000001.png")), "desktop existing batch directory is preserved and rejected");
        string failed = Path.Combine(outputFolder, "failed-sequence");
        result = await RunAsync(runner, Request("directory-fail", folder, ["-o", failed], failed, CommandOutputKind.Directory));
        check(!result.Success && !Directory.Exists(failed) && NoStaging(outputFolder), "desktop failed directory output leaves no final or staged partial batch");
        string missing = Path.Combine(outputFolder, "missing.mov");
        result = await RunAsync(runner, Request("echo", folder, ["-o", missing], missing, CommandOutputKind.Video));
        check(!result.Success && !File.Exists(missing) && NoStaging(outputFolder), "desktop rejects success reports that did not create their output");

        string incremental = Path.Combine(outputFolder, "incremental");
        result = await RunAsync(runner, Request("directory-fail", folder, ["-o", incremental, "--resume"], incremental, CommandOutputKind.Directory) with { CommandId = "render.sequence" });
        check(!result.Success && File.Exists(Path.Combine(incremental, "frame000001.png")),
            "desktop resumed sequences preserve the engine's intentional partial progress");
        foreach (string command in new[] { "render.sequence", "story.sequence" })
        {
            string firstPass = Path.Combine(outputFolder, "initial-" + command);
            result = await RunAsync(runner, Request("directory-fail", folder, ["-o", firstPass], firstPass, CommandOutputKind.Directory) with { CommandId = command });
            check(!result.Success && File.Exists(Path.Combine(firstPass, "frame000001.png")) && File.Exists(Path.Combine(firstPass, "render.json")) && NoStaging(outputFolder),
                "desktop " + command + " keeps first-run frames and render manifest for retry after failure");
        }
        string homeBatch = Path.Combine(outputFolder, "failed-home-sequence");
        result = await RunAsync(runner, Request("directory-fail", folder, ["-o", homeBatch], homeBatch, CommandOutputKind.Directory) with { CommandId = "home.sequence" });
        check(!result.Success && !Directory.Exists(homeBatch) && NoStaging(outputFolder), "desktop non-resumable home batches still remove failed partial output");

        string relativeName = "relative-" + Guid.NewGuid().ToString("N") + ".bin";
        string wrongLocation = Path.GetFullPath(relativeName);
        try
        {
            result = await RunAsync(runner, Request("file", outputFolder, ["-o", relativeName], relativeName, CommandOutputKind.File));
            check(result.Success && File.Exists(Path.Combine(outputFolder, relativeName)) && !File.Exists(wrongLocation),
                "desktop relative output resolves against request working directory just like child-process arguments");
        }
        finally { if (File.Exists(wrongLocation)) File.Delete(wrongLocation); }
    }

    static async Task CancellationCasesAsync(EngineRunner runner, string folder, Action<bool, string> check)
    {
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            string ready = Path.Combine(folder, "precancelled.json");
            var result = await runner.RunAsync(Request("echo", folder, ["--ready", ready]), cancellationToken: cancelled.Token);
            check(result.Cancelled && !result.Success && !File.Exists(ready), "desktop pre-cancelled request does not launch a worker");
        }
        string outputFolder = Path.Combine(folder, "cancelled"); Directory.CreateDirectory(outputFolder);
        string output = Path.Combine(outputFolder, "partial.mov"), partialReady = Path.Combine(folder, "partial-ready.json");
        using (var cancellation = new CancellationTokenSource(Timeout))
        {
            var running = runner.RunAsync(Request("partial", folder, ["-o", output, "--sidecars", "--ready", partialReady], output, CommandOutputKind.Video), cancellationToken: cancellation.Token);
            try
            {
                await UntilAsync(() => File.Exists(partialReady)); cancellation.Cancel();
                var result = await running.WaitAsync(Timeout);
                check(result.Cancelled && !File.Exists(output) && !File.Exists(output + ".source.json") && NoStaging(outputFolder),
                    "desktop cancellation kills a writing worker and cleans staged main output and sidecars");
            }
            finally { cancellation.Cancel(); await running.WaitAsync(Timeout); }
        }
        string parentReady = Path.Combine(folder, "tree-parent.json"), childReady = Path.Combine(folder, "tree-child.json");
        string siblingReady = Path.Combine(folder, "tree-sibling.json");
        using var treeCancellation = new CancellationTokenSource(Timeout);
        using var siblingCancellation = new CancellationTokenSource(Timeout);
        var sibling = runner.RunAsync(Request("hold", folder, ["--ready", siblingReady]), cancellationToken: siblingCancellation.Token);
        var tree = runner.RunAsync(Request("tree", folder, ["--ready", parentReady, "--child-ready", childReady]), cancellationToken: treeCancellation.Token);
        JsonObject? childIdentity = null;
        try
        {
            await UntilAsync(() => File.Exists(parentReady) && File.Exists(childReady) && File.Exists(siblingReady));
            childIdentity = JsonNode.Parse(File.ReadAllText(childReady))!.AsObject();
            var siblingIdentity = JsonNode.Parse(File.ReadAllText(siblingReady))!.AsObject();
            check(IsAlive(childIdentity), "desktop cancellation fixture has a live grandchild process");
            treeCancellation.Cancel();
            var result = await tree.WaitAsync(Timeout);
            await UntilAsync(() => !IsAlive(childIdentity));
            check(result.Cancelled && !IsAlive(childIdentity), "desktop cancellation terminates the complete native/FFmpeg process tree");
            check(!sibling.IsCompleted && IsAlive(siblingIdentity), "desktop process-tree cancellation leaves an unrelated sibling worker running");
        }
        finally
        {
            treeCancellation.Cancel();
            siblingCancellation.Cancel();
            if (childIdentity is not null && IsAlive(childIdentity)) KillFixture(childIdentity);
            await Task.WhenAll(tree, sibling).WaitAsync(Timeout);
        }

        // A crashed/exited parent may leave inherited pipes open even though tree enumeration
        // can no longer establish ancestry. The Cancel button must still finish predictably.
        string exitedReady = Path.Combine(folder, "exited-parent.json"), inheritedReady = Path.Combine(folder, "inherited-pipe-child.json");
        using var inheritedCancellation = new CancellationTokenSource(Timeout);
        var inherited = runner.RunAsync(Request("tree-exit", folder, ["--ready", exitedReady, "--child-ready", inheritedReady]), cancellationToken: inheritedCancellation.Token);
        JsonObject? inheritedChild = null;
        try
        {
            await UntilAsync(() => File.Exists(exitedReady) && File.Exists(inheritedReady));
            inheritedChild = JsonNode.Parse(File.ReadAllText(inheritedReady))!.AsObject();
            var exitedParent = JsonNode.Parse(File.ReadAllText(exitedReady))!.AsObject();
            await UntilAsync(() => !IsAlive(exitedParent));
            check(!inherited.IsCompleted && IsAlive(inheritedChild), "desktop pipe fixture remains open after its owning parent exits");
            inheritedCancellation.Cancel();
            var result = await inherited.WaitAsync(TimeSpan.FromSeconds(8));
            check(result.Cancelled, "desktop cancellation has a bounded pipe wait even after the original parent has exited");
        }
        finally
        {
            inheritedCancellation.Cancel();
            if (inheritedChild is not null && IsAlive(inheritedChild)) KillFixture(inheritedChild);
            await inherited.WaitAsync(Timeout);
        }
    }

    static async Task QueueCasesAsync(EngineRunner runner, string folder, Action<bool, string> check)
    {
        string history = Path.Combine(folder, "history"), firstReady = Path.Combine(folder, "queue-first.json"), secondReady = Path.Combine(folder, "queue-second.json");
        var queue = new TaskQueue(runner, history);
        try
        {
            var first = queue.Enqueue(Request("hold", folder, ["--ready", firstReady]));
            await UntilAsync(() => File.Exists(firstReady));
            var second = queue.Enqueue(Request("echo", folder, ["--ready", secondReady]));
            await Task.Delay(100);
            check(queue.IsBusy && first.Status == DesktopTaskStatus.Running && !File.Exists(secondReady), "desktop task queue runs exactly one child at a time");
            queue.Cancel(first);
            var completed = await Task.WhenAll(first.Completion, second.Completion).WaitAsync(Timeout);
            check(completed[0].Cancelled && completed[1].Success && !queue.IsBusy && first.Status == DesktopTaskStatus.Cancelled && second.Status == DesktopTaskStatus.Succeeded && second.Fraction == 1,
                "desktop queue cancellation releases the slot and records terminal task states");
            check(first.ReportPath is not null && File.Exists(first.ReportPath) && second.ReportPath is not null &&
                JsonNode.Parse(File.ReadAllText(second.ReportPath))!["status"]!.GetValue<string>() == "Succeeded",
                "desktop tasks persist structured completion reports");

            string sourceDirectory = Path.Combine(folder, "queue-source"); Directory.CreateDirectory(sourceDirectory);
            string image = Path.Combine(sourceDirectory, "background.png"); File.WriteAllText(image, "fixture image");
            var document = SceneTemplates.Create("transparent-actor", "source-before", "model");
            var root = document.Root;
            root["commands"]![0]!["Values"]!["backgroundImage"] = new JsonObject { ["path"] = "background.png" };
            document = SceneDocument.Parse(root.ToJsonString(), baseDirectory: sourceDirectory);
            string snapshotFolder = Path.Combine(folder, "short-lived-input");
            string inputReady = Path.Combine(folder, "queued-input-ready.json"), gate = Path.Combine(folder, "queued-input-go");
            DesktopTask inputTask;
            using (var snapshot = document.CreateSnapshot(snapshotFolder))
            {
                inputTask = queue.Enqueue(new("render.frame", "input fixture",
                    ["render", "frame", snapshot.FilePath, WorkerFlag, "input", "--ready", inputReady, "--gate", gate], folder));
                check(inputTask.Request.Arguments[2] != snapshot.FilePath && File.Exists(inputTask.Request.Arguments[2]),
                    "desktop queue owns a durable input copy before the editor snapshot is released");
            }
            await UntilAsync(() => File.Exists(inputReady));
            document.SetSource(adv: "source-after"); File.WriteAllText(gate, "continue");
            var inputResult = await inputTask.Completion.WaitAsync(Timeout);
            check(inputResult.Success && inputResult.Result!["input"]!["source"]!["adv"]!.GetValue<string>() == "source-before" &&
                inputResult.Result["input"]!["commands"]![0]!["Values"]!["backgroundImage"]!["path"]!.GetValue<string>() == image,
                "desktop queued input freezes the chosen scene and preserves local asset locations");

            string script = Path.Combine(sourceDirectory, "raw-script.json");
            File.WriteAllText(script, """[{"cmd":"Director.Scene","Values":{"backgroundImage":{"path":"background.png"}}},{"cmd":"Delay","Duration":1}]""");
            var scriptTask = queue.Enqueue(new("story.frame", "raw script fixture",
                ["story", "frame", "42", "--script", script, WorkerFlag, "input"], folder));
            var scriptResult = await scriptTask.Completion.WaitAsync(Timeout);
            check(scriptResult.Success && scriptTask.Request.Arguments[4] != script &&
                scriptResult.Result!["input"]![0]!["Values"]!["backgroundImage"]!["path"]!.GetValue<string>() == image,
                "desktop queue preserves native top-level command-array scripts and rebases their known local images");
        }
        finally { await queue.DisposeAsync(); }
        int count = queue.Tasks.Count;
        check(Throws<ObjectDisposedException>(() => queue.Enqueue(Request("echo", folder))) && queue.Tasks.Count == count,
            "desktop closed queue rejects new work before adding phantom history entries");
        await using (var reloaded = new TaskQueue(runner, history))
        {
            check(reloaded.Tasks.Count == count && !reloaded.IsBusy && reloaded.Tasks.All(task => task.Completion.IsCompleted) &&
                reloaded.Tasks.Any(task => task.Status == DesktopTaskStatus.Cancelled) && reloaded.Tasks.Any(task => task.Status == DesktopTaskStatus.Succeeded),
                "desktop task history restores terminal results without automatically re-running jobs");
            var retrySource = reloaded.Tasks.First(task => task.Request.CommandId == "render.frame");
            check(File.Exists(retrySource.Request.Arguments[2]), "desktop restart retains the exact JSON input needed to retry an old scene task");
            var retry = reloaded.Enqueue(retrySource.Request);
            check((await retry.Completion.WaitAsync(Timeout)).Success, "desktop restored scene task can actually run again after its original snapshot was deleted");
        }

        string interruptedFolder = Path.Combine(folder, "interrupted-history"); Directory.CreateDirectory(interruptedFolder);
        var interrupted = History("interrupted", "Running");
        File.WriteAllText(Path.Combine(interruptedFolder, "interrupted.json"), interrupted.ToJsonString());
        var broken = History("broken", "Succeeded"); broken["elapsedSeconds"] = JsonNode.Parse("1e999");
        File.WriteAllText(Path.Combine(interruptedFolder, "broken.json"), broken.ToJsonString());
        File.WriteAllText(Path.Combine(interruptedFolder, "invalid.json"), "{broken");
        await using var recovered = new TaskQueue(runner, interruptedFolder);
        check(!recovered.IsBusy && recovered.Tasks.Any(task => task.Id == "interrupted" && task.Status == DesktopTaskStatus.Interrupted),
            "desktop history recovers interrupted tasks and tolerates malformed/overflowing reports without blocking startup");
    }

    static void SettingsCases(string folder, Action<bool, string> check)
    {
        string path = Path.Combine(folder, "settings.json");
        var settings = new DesktopSettings
        {
            ArchivePath = Path.Combine(folder, "归档 with spaces"), OutputDirectory = Path.Combine(folder, "输出"),
            Site = "jp", Language = "ja", Theme = "Dark", EnginePath = Path.Combine(folder, "my engine"),
            FFmpegPath = "ffmpeg", FFprobePath = "ffprobe", CubismSdkPath = "sdk", NativeLibraryPath = "native"
        };
        settings.Forms["render.clip"] = new(StringComparer.Ordinal) { ["codec"] = "cineform", ["custom"] = "中文\n\"literal\"", ["optional"] = null };
        for (int index = 0; index < 15; index++) settings.RememberProject(Path.Combine(folder, "project-" + index + ".json"));
        settings.RememberProject(Path.Combine(folder, "project-5.json")); settings.Save(path);
        var restored = DesktopSettings.Load(path);
        check(restored.LoadWarning is null && restored.ArchivePath == settings.ArchivePath && restored.OutputDirectory == settings.OutputDirectory &&
            restored.Site == "jp" && restored.Language == "ja" && restored.Theme == "Dark" && restored.ResolveEngine() == settings.EnginePath &&
            restored.Context(folder).WorkingDirectory == folder && restored.Forms["render.clip"]["custom"] == "中文\n\"literal\"" && restored.Forms["render.clip"]["optional"] is null,
            "desktop settings round-trip source, runtime paths, typed form strings and Unicode without reflection serialization");
        check(restored.RecentProjects.Count == 12 && restored.RecentProjects[0] == Path.Combine(folder, "project-5.json") && restored.RecentProjects.Distinct().Count() == 12,
            "desktop recent projects are bounded and deduplicated in most-recent order");
        File.WriteAllText(path, "{ damaged settings"); restored = DesktopSettings.Load(path);
        check(restored.LoadWarning is { Length: > 0 } && File.ReadAllText(path) == "{ damaged settings", "desktop malformed settings retain the original file and explain default fallback");
        string targetDirectory = Path.Combine(folder, "settings-directory"); Directory.CreateDirectory(targetDirectory);
        check(ThrowsWriteFailure(() => DesktopSettings.AtomicWrite(targetDirectory, "new data")) && Directory.Exists(targetDirectory) &&
            !Directory.EnumerateFiles(folder, "settings-directory.tmp-*").Any(), "desktop failed atomic settings write cleans only its own temporary file");
    }

    static async Task PreviewCasesAsync(string executable, string folder, Action<bool, string> check)
    {
        string? previous = Environment.GetEnvironmentVariable(MediaEnvironment);
        Environment.SetEnvironmentVariable(MediaEnvironment, "1");
        var generated = new List<string>();
        try
        {
            string input = Path.Combine(folder, "media-fixture-中文 source.mov"), calls = Path.Combine(folder, "preview-calls.txt");
            var configuration = new JsonObject { ["mode"] = "success", ["calls"] = calls };
            File.WriteAllText(input, configuration.ToJsonString());
            string image = await MediaPreview.CreateAsync(input, executable, true).WaitAsync(Timeout); generated.Add(image);
            var args = JsonNode.Parse(File.ReadAllText(image))!["arguments"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
            check(Option(args, "-i") == input && Option(args, "-pix_fmt") == "rgba" && Option(args, "-frames:v") == "1",
                "desktop still preview launches actual FFmpeg-style arguments with an intact Unicode source path and alpha output");
            string cached = await MediaPreview.CreateAsync(input, executable, true).WaitAsync(Timeout);
            check(cached == image && File.ReadAllLines(calls).Length == 1, "desktop unchanged preview uses its completed cached image without another process");
            string video = await MediaPreview.CreateAsync(input, executable, false).WaitAsync(Timeout); generated.Add(video);
            args = JsonNode.Parse(File.ReadAllText(video))!["arguments"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
            check(video != image && Option(args, "-c:v") == "libx264" && Option(args, "-c:a") == "aac" &&
                File.ReadAllText(input) == configuration.ToJsonString(), "desktop compatible video preview uses a separate cache and preserves the original media file");
            File.AppendAllText(input, " ");
            string changed = await MediaPreview.CreateAsync(input, executable, true).WaitAsync(Timeout); generated.Add(changed);
            check(changed != image && File.ReadAllLines(calls).Length == 3, "desktop preview cache invalidates when source content size changes");

            string ready = Path.Combine(folder, "preview-failure-ready.json");
            configuration["mode"] = "failure"; configuration["ready"] = ready; File.WriteAllText(input, configuration.ToJsonString());
            bool failed = false;
            try { generated.Add(await MediaPreview.CreateAsync(input, executable, true).WaitAsync(Timeout)); }
            catch (IOException error) { failed = error.Message.Contains("fixture FFmpeg failed", StringComparison.Ordinal); }
            string temporary = JsonNode.Parse(File.ReadAllText(ready))!["output"]!.GetValue<string>();
            generated.Add(temporary);
            check(failed && !File.Exists(temporary) && File.Exists(input), "desktop failed FFmpeg preview removes partial cache output and retains the source");

            ready = Path.Combine(folder, "preview-cancel-ready.json");
            configuration["mode"] = "wait"; configuration["ready"] = ready; File.WriteAllText(input, configuration.ToJsonString());
            using var cancellation = new CancellationTokenSource(Timeout);
            var preview = MediaPreview.CreateAsync(input, executable, true, cancellation.Token);
            try
            {
                await UntilAsync(() => File.Exists(ready)); temporary = JsonNode.Parse(File.ReadAllText(ready))!["output"]!.GetValue<string>();
                generated.Add(temporary);
                cancellation.Cancel();
                bool cancelled = false;
                try { generated.Add(await preview.WaitAsync(Timeout)); } catch (OperationCanceledException) { cancelled = true; }
                check(cancelled && !File.Exists(temporary), "desktop cancelling a preview terminates its encoder and removes the temporary cache file");
            }
            finally { cancellation.Cancel(); try { await preview.WaitAsync(Timeout); } catch (OperationCanceledException) { } }
        }
        finally
        {
            foreach (string path in generated) if (File.Exists(path)) File.Delete(path);
            Environment.SetEnvironmentVariable(MediaEnvironment, previous);
        }
    }

    static async Task<int> MediaWorkerAsync(string[] args)
    {
        string input = Option(args, "-i") ?? throw new ArgumentException("Media fixture input missing");
        var configuration = JsonNode.Parse(File.ReadAllText(input))!;
        string output = args[^1], mode = configuration["mode"]!.GetValue<string>();
        File.WriteAllText(output, new JsonObject { ["arguments"] = new JsonArray(args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()) }.ToJsonString());
        if (configuration["calls"] is { } calls) File.AppendAllText(calls.ToString(), "call\n");
        Signal(configuration["ready"]?.ToString(), new() { ["output"] = output, ["pid"] = Environment.ProcessId });
        if (mode == "wait") await Task.Delay(System.Threading.Timeout.Infinite);
        if (mode == "failure") { Console.Error.WriteLine("fixture FFmpeg failed"); return 4; }
        return 0;
    }

    static EngineRequest Request(string mode, string workingDirectory, string[]? extra = null, string? output = null, CommandOutputKind kind = CommandOutputKind.Json)
        => new("fixture", "fixture " + mode, [WorkerFlag, mode, .. extra ?? []], workingDirectory, output, kind);
    static async Task<EngineResult> RunAsync(EngineRunner runner, EngineRequest request, Action<EngineProgress>? progress = null)
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        return await runner.RunAsync(request, progress, cancellation.Token).WaitAsync(Timeout + TimeSpan.FromSeconds(2));
    }
    static string? Option(IReadOnlyList<string> args, string key)
    {
        for (int index = args.Count - 2; index >= 0; index--) if (args[index] == key) return args[index + 1];
        return null;
    }
    static JsonObject Envelope(JsonObject result) => new() { ["schema"] = "ournotes-director/1", ["ok"] = true, ["result"] = result };
    static void WriteResult(JsonObject result) => Console.Write(Envelope(result).ToJsonString());
    static void WriteError(string message) => Console.Write(new JsonObject { ["schema"] = "ournotes-director/1", ["ok"] = false,
        ["error"] = new JsonObject { ["message"] = message } }.ToJsonString());
    static void Signal(string? path, JsonObject value) { if (path is not null) DesktopSettings.AtomicWrite(path, value.ToJsonString()); }
    static bool NoStaging(string directory) => !Directory.EnumerateDirectories(directory, ".ournotes-task-*").Any();
    static JsonObject ProcessIdentity()
    {
        var result = new JsonObject { ["pid"] = Environment.ProcessId };
        if (OperatingSystem.IsLinux())
        {
            // Each worker supplies its own actual procfs identity. The observer must not
            // assume Process.Id and mounted /proc directory numbers use the same namespace.
            string stat = File.ReadAllText("/proc/self/stat");
            result["procPid"] = int.Parse(stat[..stat.IndexOf(' ')], CultureInfo.InvariantCulture);
            result["procStart"] = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
        }
        return result;
    }
    static bool IsAlive(JsonObject identity)
    {
        int pid = identity["pid"]!.GetValue<int>();
        if (OperatingSystem.IsLinux())
        {
            try
            {
                string stat = File.ReadAllText("/proc/" + identity["procPid"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture) + "/stat");
                string[] fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return fields[19] == identity["procStart"]!.GetValue<string>() && fields[0] is not ("Z" or "X");
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    static void KillFixture(JsonObject identity)
    {
        int pid = identity["pid"]!.GetValue<int>();
        if (OperatingSystem.IsLinux()) { SignalProcess(pid, 9); return; }
        using var process = Process.GetProcessById(pid); process.Kill(true);
    }
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    static extern int SignalProcess(int pid, int signal);
    static async Task UntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > Timeout) throw new TimeoutException("Fixture did not reach its required observable state");
            await Task.Delay(20);
        }
    }
    static JsonObject History(string id, string status) => new()
    {
        ["id"] = id, ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["command"] = "fixture", ["title"] = "fixture history",
        ["status"] = status, ["message"] = "fixture", ["arguments"] = new JsonArray(WorkerFlag, "echo"),
        ["workingDirectory"] = Path.GetTempPath(), ["outputKind"] = "Json", ["elapsedSeconds"] = 0,
        ["result"] = Envelope(new JsonObject()), ["exitCode"] = 0
    };
    static bool ThrowsWriteFailure(Action action)
    {
        try { action(); return false; }
        // Replacing a directory is IOException on Unix and UnauthorizedAccessException on Windows.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }
    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
