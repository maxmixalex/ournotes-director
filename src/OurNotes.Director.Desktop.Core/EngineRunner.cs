using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OurNotes.Director.Desktop.Core;

public sealed record EngineRequest(string CommandId, string Title, IReadOnlyList<string> Arguments, string WorkingDirectory,
    string? OutputPath = null, CommandOutputKind OutputKind = CommandOutputKind.Json);

public sealed record EngineProgress(string Message, double? Fraction = null);

public sealed record EngineResult(bool Success, bool Cancelled, int ExitCode, JsonNode? Json, string StandardOutput, string Log,
    string? OutputPath, TimeSpan Elapsed, string? Error)
{
    public JsonNode? Result => Json is JsonObject envelope && envelope.ContainsKey("result") ? envelope["result"] : Json;
}

/// <summary>Runs the same packaged native engine as the CLI, with no shell and no UI-thread work.</summary>
public sealed class EngineRunner(string executable)
{
    public string Executable { get; } = Path.GetFullPath(executable);
    private const int MaximumOutputCharacters = 32 * 1024 * 1024;
    private static readonly Regex FrameProgress = new(@"(?:story render|frames?|\[[^\]]+\])\s*[=:]?\s*(\d+)\s*/\s*(\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public async Task<EngineResult> RunAsync(EngineRequest request, Action<EngineProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var output = new StringBuilder(); var log = new StringBuilder();
        var outputLock = new object(); var logLock = new object();
        string CapturedOutput() { lock (outputLock) return output.ToString(); }
        string CapturedLog() { lock (logLock) return log.ToString(); }
        void Log(string line) { lock (logLock) log.AppendLine(line); }
        string? stagingRoot = null;
        bool preserveStaging = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(Executable)) throw new FileNotFoundException("找不到随包的 advkit 程序。请完整解压安装包，或在设置中选择程序。", Executable);
            var arguments = request.Arguments.ToArray();
            string? stagedOutput = null;
            bool directoryOutput = request.OutputKind == CommandOutputKind.Directory;
            // These commands keep resumable progress from their first run; home batches are atomic.
            bool incrementalOutput = request.CommandId is "archive" or "render.sequence" or "story.sequence";
            bool canStage = request.OutputPath is { Length: > 0 } && !incrementalOutput;
            bool overwrite = arguments.Contains("--overwrite");
            if (canStage)
            {
                var destination = Path.GetFullPath(request.OutputPath!, Path.GetFullPath(request.WorkingDirectory));
                if (Directory.Exists(destination) || File.Exists(destination) && (!overwrite || directoryOutput))
                    throw new IOException(directoryOutput ? "输出目录已存在，请选择一个新目录。" : "输出文件已存在。请更换文件名，或勾选替换已有文件。");
                string parent = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(parent);
                stagingRoot = Path.Combine(parent, ".ournotes-task-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingRoot);
                stagedOutput = Path.Combine(stagingRoot, Path.GetFileName(destination));
                var index = Array.FindLastIndex(arguments, a => a is "--out" or "-o");
                if (index < 0 || index + 1 >= arguments.Length) throw new ArgumentException("输出操作缺少目标路径。");
                arguments[index + 1] = stagedOutput;
            }
            using var process = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = Executable, WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) throw new IOException("无法启动渲染程序。");
            var mappedTree = LinuxNamespaceTree.Capture(process.Id, out bool mappedProcfs);
            var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var readCancellation = new CancellationTokenSource();
            // Only signal here. Process enumeration/killing must not block the UI's Cancel() call.
            using var registration = cancellationToken.Register(() => cancellation.TrySetResult());
            async Task ReadOutput()
            {
                try
                {
                    var buffer = new char[8192]; int count;
                    while ((count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), readCancellation.Token).ConfigureAwait(false)) > 0)
                    {
                        lock (outputLock)
                        {
                            if (output.Length + count > MaximumOutputCharacters)
                                throw new InvalidDataException("结果过大，请减少每页数量或导出到文件。");
                            output.Append(buffer, 0, count);
                        }
                    }
                }
                catch (Exception error) { readFailure.TrySetResult(error); throw; }
            }
            async Task ReadLog()
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync(readCancellation.Token).ConfigureAwait(false) is { } line)
                    {
                        lock (logLock)
                        {
                            if (log.Length > 512 * 1024) log.Remove(0, 128 * 1024);
                            log.AppendLine(line);
                        }
                        var match = FrameProgress.Match(line);
                        double? fraction = null;
                        if (match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var done)
                            && double.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var total) && total > 0)
                            fraction = Math.Clamp(done / total, 0, 1);
                        progress?.Invoke(new EngineProgress(TranslateProgress(line), fraction));
                    }
                }
                catch (Exception error) { readFailure.TrySetResult(error); throw; }
            }
            var exited = process.WaitForExitAsync();
            var reads = Task.WhenAll(ReadOutput(), ReadLog());
            var completed = Task.WhenAll(reads, exited);
            var first = await Task.WhenAny(completed, cancellation.Task, readFailure.Task).ConfigureAwait(false);
            if (first != completed || cancellationToken.IsCancellationRequested)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        if (mappedProcfs)
                        {
                            try
                            {
                                if (mappedTree is not null) mappedTree.Terminate(Log);
                                else Log("无法核实容器中的子进程映射，将停止父进程并关闭输出读取。");
                            }
                            finally
                            {
                                // Foreign /proc IDs must not be passed to .NET's tree walker.
                                if (!process.HasExited) process.Kill(entireProcessTree: false);
                            }
                        }
                        else if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
                    { Log("停止任务进程时返回：" + error.Message); }
                }).ConfigureAwait(false);
                if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(false) != exited)
                    Log("父进程尚未确认退出；已停止等待其输出。请检查系统进程权限。");
                // A child can inherit stdout/stderr even after its parent exits. Cancellation must remain bounded.
                if (await Task.WhenAny(completed, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false) != completed)
                {
                    readCancellation.Cancel();
                    try { process.StandardOutput.Dispose(); process.StandardError.Dispose(); }
                    catch (IOException closeError) { Log("关闭任务输出时返回：" + closeError.Message); }
                    await Task.WhenAny(completed, Task.Delay(TimeSpan.FromMilliseconds(500))).ConfigureAwait(false);
                }
                if (completed.IsCompleted)
                {
                    try { await completed.ConfigureAwait(false); }
                    catch (Exception) { /* The original read failure is retained separately below. */ }
                }
                else _ = completed.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                cancellationToken.ThrowIfCancellationRequested();
                if (readFailure.Task.IsCompletedSuccessfully) throw await readFailure.Task.ConfigureAwait(false);
                throw new IOException("进程输出读取未能完成。");
            }
            await completed.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            JsonNode? json = null;
            try { json = JsonNode.Parse(CapturedOutput()); }
            catch (JsonException) { }
            bool validEnvelope = json is JsonObject envelope && envelope["schema"]?.ToString() == "ournotes-director/1"
                && (envelope["ok"]?.ToString() == "true" || request.CommandId == "help" && envelope["commands"] is JsonArray);
            bool success = process.ExitCode == 0 && validEnvelope;
            string? error = success ? null : DescribeError(json, process.ExitCode, CapturedLog());
            if (success && request.CommandId is "render.validate" or "story.native-check"
                && json?["result"]?["stories"] is JsonArray checkedStories)
            {
                var failed = checkedStories.OfType<JsonObject>().Where(s => s["compiled"]?.ToString() == "false" || s["initialized"]?.ToString() == "false").ToArray();
                if (failed.Length > 0)
                {
                    success = false;
                    error = $"{failed.Length} 个剧情未通过资源或首帧检查：" + string.Join("；", failed.Select(s => s["reason"]?.ToString() ?? s["advId"]?.ToString()));
                }
            }
            if (success && stagedOutput is not null)
            {
                var destination = Path.GetFullPath(request.OutputPath!, Path.GetFullPath(request.WorkingDirectory));
                if (directoryOutput)
                {
                    if (!Directory.Exists(stagedOutput)) throw new IOException("任务报告完成，但没有生成输出目录。");
                    RewriteManifests(stagedOutput, stagedOutput, destination);
                    Directory.Move(stagedOutput, destination);
                }
                else
                {
                    if (!File.Exists(stagedOutput)) throw new IOException("任务报告完成，但没有生成输出文件。");
                    PublishFileSet(stagedOutput, destination, overwrite, ref preserveStaging);
                }
                json = directoryOutput ? RewritePaths(json, stagedOutput, destination)
                    : RewritePaths(json, stagingRoot!, Path.GetDirectoryName(destination)!);
            }
            return new EngineResult(success, false, process.ExitCode, json, json?.ToJsonString(DesktopSettings.JsonOptions) ?? CapturedOutput(),
                CapturedLog(), request.OutputPath, stopwatch.Elapsed, error);
        }
        catch (OperationCanceledException)
        { return new EngineResult(false, true, -1, null, CapturedOutput(), CapturedLog(), request.OutputPath, stopwatch.Elapsed, "任务已取消。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new EngineResult(false, false, -1, null, CapturedOutput(), CapturedLog(), request.OutputPath, stopwatch.Elapsed, error.Message); }
        finally
        {
            if (stagingRoot is not null && !preserveStaging)
                try { Directory.Delete(stagingRoot, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Some Linux containers expose their ancestor's procfs while fork/kill use an inner PID
    /// namespace. Only in that case, map our own direct child and its verified descendants.
    /// Never interpret a bare /proc directory name as a PID in the calling namespace.
    /// </summary>
    private sealed class LinuxNamespaceTree
    {
        private sealed record ProcessIdentity(int Pid, int Parent, int[] NamespacePids, ulong StartTime);
        private readonly ProcessIdentity owner, root;
        private readonly int namespaceIndex;
        private LinuxNamespaceTree(ProcessIdentity owner, ProcessIdentity root, int namespaceIndex)
        { this.owner = owner; this.root = root; this.namespaceIndex = namespaceIndex; }

        public static LinuxNamespaceTree? Capture(int childPid, out bool mappedProcfs)
        {
            mappedProcfs = false;
            if (!OperatingSystem.IsLinux()) return null;
            var owner = Read("self");
            if (owner is null || owner.Pid == Environment.ProcessId) return null;
            mappedProcfs = true;
            int index = owner.NamespacePids.Length - 1;
            if (index < 1 || owner.NamespacePids[index] != Environment.ProcessId) return null;
            // Parentage disambiguates identical namespace-local PID numbers in other containers.
            var candidates = Snapshot().Where(child => child.Parent == owner.Pid
                && child.NamespacePids.Length == owner.NamespacePids.Length && child.NamespacePids[index] == childPid).Take(2).ToArray();
            return candidates.Length == 1 ? new(owner, candidates[0], index) : null;
        }

        public void Terminate(Action<string> log)
        {
            if (!Matches(Read("self"), owner) || !Matches(Read(root.Pid.ToString(CultureInfo.InvariantCulture)), root)) return;
            var descendants = new Dictionary<int, ProcessIdentity> { [root.Pid] = root };
            var order = new List<ProcessIdentity> { root };
            try
            {
                // Stop before traversing so the root cannot create new children during cancellation.
                if (!Signal(root, 19)) return;
                var timer = Stopwatch.StartNew();
                for (int round = 0; round < 8 && timer.Elapsed < TimeSpan.FromSeconds(1); round++)
                {
                    var children = Snapshot().ToLookup(item => item.Parent);
                    int previousCount = order.Count;
                    for (int parentIndex = 0; parentIndex < order.Count; parentIndex++)
                    {
                        var parent = order[parentIndex];
                        if (!Matches(Read(parent.Pid.ToString(CultureInfo.InvariantCulture)), parent)) continue;
                        foreach (var child in children[parent.Pid])
                        {
                            if (descendants.ContainsKey(child.Pid) || child.NamespacePids.Length <= namespaceIndex
                                || child.NamespacePids[namespaceIndex] <= 1 || child.NamespacePids[namespaceIndex] == Environment.ProcessId) continue;
                            // Preserve identities before signalling. Further-namespace descendants use
                            // the caller's NSpid column, not the child's innermost PID.
                            if (!Matches(Read(child.Pid.ToString(CultureInfo.InvariantCulture)), child)) continue;
                            descendants.Add(child.Pid, child); order.Add(child); Signal(child, 19);
                        }
                    }
                    if (order.Count == previousCount) break;
                }
            }
            finally
            {
                // Recheck start time and the complete NSpid mapping immediately before each kill.
                // If a process exited/reused its PID, leave the replacement alone.
                int stopped = 0;
                for (int index = order.Count - 1; index >= 0; index--) if (Signal(order[index], 9)) stopped++;
                if (stopped > 1) log($"已停止任务进程及 {stopped - 1} 个子进程。");
            }
        }

        private bool Signal(ProcessIdentity identity, int signal)
        {
            var current = Read(identity.Pid.ToString(CultureInfo.InvariantCulture));
            if (!Matches(current, identity) || identity.NamespacePids.Length <= namespaceIndex) return false;
            int pid = identity.NamespacePids[namespaceIndex];
            return pid > 1 && pid != Environment.ProcessId && SignalProcess(pid, signal) == 0;
        }

        private static bool Matches(ProcessIdentity? actual, ProcessIdentity expected) => actual is not null
            && actual.Pid == expected.Pid && actual.Parent == expected.Parent && actual.StartTime == expected.StartTime
            && actual.NamespacePids.AsSpan().SequenceEqual(expected.NamespacePids);

        private static IReadOnlyList<ProcessIdentity> Snapshot()
        {
            var result = new List<ProcessIdentity>();
            try
            {
                foreach (string path in Directory.EnumerateDirectories("/proc"))
                    if (int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out _)
                        && Read(Path.GetFileName(path)) is { } identity) result.Add(identity);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return result;
        }

        private static ProcessIdentity? Read(string id)
        {
            try
            {
                string path = "/proc/" + id;
                int pid = 0, parent = 0; int[] namespacePids = [];
                foreach (string line in File.ReadLines(path + "/status"))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    string field = line[..colon], value = line[(colon + 1)..].Trim();
                    if (field == "Pid") int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out pid);
                    else if (field == "PPid") int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parent);
                    else if (field == "NSpid")
                    {
                        string[] parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        namespacePids = new int[parts.Length];
                        for (int index = 0; index < parts.Length; index++)
                            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out namespacePids[index])) return null;
                    }
                }
                string stat = File.ReadAllText(path + "/stat"); int close = stat.LastIndexOf(')');
                if (pid <= 0 || namespacePids.Length == 0 || namespacePids[0] != pid || close < 0
                    || !stat.StartsWith(pid.ToString(CultureInfo.InvariantCulture) + " (", StringComparison.Ordinal)) return null;
                string[] fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length <= 19 || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int statParent)
                    || statParent != parent || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out ulong started)) return null;
                return new(pid, parent, namespacePids, started);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int SignalProcess(int pid, int signal);
    }

    private static string DescribeError(JsonNode? json, int exitCode, string log)
    {
        var error = json is JsonObject root ? root["error"] : null;
        var detail = error is JsonObject ? error["message"]?.ToString() : error?.ToString();
        if (string.IsNullOrWhiteSpace(detail)) detail = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "程序没有返回有效结果。";
        string title = exitCode switch { 2 => "参数或项目内容需要调整", 3 => "读取或写入失败", 4 => "当前素材或功能暂不支持", _ => "任务未完成" };
        return title + "：" + detail;
    }

    public static string TranslateProgress(string line)
    {
        if (line.StartsWith("story render ", StringComparison.Ordinal)) return "正在渲染 " + line[13..] + " 帧";
        if (line.StartsWith("[index]", StringComparison.Ordinal)) return "正在建立素材、剧情和语音索引…";
        if (line.StartsWith("[director]", StringComparison.Ordinal)) return "正在整理可用于场景的资源…";
        if (line.StartsWith("Archive:", StringComparison.Ordinal)) return "正在准备素材归档…";
        if (line.StartsWith("[assets", StringComparison.Ordinal)) return "正在下载并校验素材 " + line[(line.IndexOf(']') + 1)..].Trim();
        if (line.StartsWith("[manifests", StringComparison.Ordinal)) return "正在读取资源清单 " + line[(line.IndexOf(']') + 1)..].Trim();
        return line;
    }

    private static JsonNode? RewritePaths(JsonNode? node, string from, string to)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text)
            && (text.Equals(from, StringComparison.OrdinalIgnoreCase) || text.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || text.StartsWith(from + '/', StringComparison.OrdinalIgnoreCase))) return JsonValue.Create(to + text[from.Length..]);
        if (node is JsonObject obj)
        {
            var clone = new JsonObject(); foreach (var pair in obj) clone[pair.Key] = RewritePaths(pair.Value, from, to); return clone;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(child => RewritePaths(child, from, to)).ToArray());
        return node?.DeepClone();
    }

    private static void RewriteManifests(string directory, string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)) RewriteJsonFile(file, from, to);
    }

    private static void PublishFileSet(string staged, string destination, bool overwrite, ref bool preserveStaging)
    {
        var staging = Path.GetDirectoryName(staged)!; var parent = Path.GetDirectoryName(destination)!;
        var files = new[] { staged }.Concat(Directory.EnumerateFiles(staging, Path.GetFileName(staged) + ".*", SearchOption.TopDirectoryOnly)).Distinct().ToArray();
        var outputs = files.Select(file => (Source: file, Target: Path.Combine(parent, Path.GetFileName(file)))).ToArray();
        foreach (var item in outputs)
        {
            if (Directory.Exists(item.Target) || File.Exists(item.Target) && !overwrite)
                throw new IOException("输出文件或其来源记录已存在：" + item.Target);
            if (Path.GetExtension(item.Source).Equals(".json", StringComparison.OrdinalIgnoreCase)) RewriteJsonFile(item.Source, staging, parent);
        }
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        string backupRoot = Path.Combine(staging, "previous");
        // Copy every existing output before touching any destination. A sharing
        // violation therefore leaves the previous export intact.
        foreach (var item in outputs.Where(item => File.Exists(item.Target)))
        {
            Directory.CreateDirectory(backupRoot); string backup = Path.Combine(backupRoot, Path.GetFileName(item.Target));
            File.Copy(item.Target, backup); backups[item.Target] = backup;
        }
        var committed = new List<string>();
        try
        {
            foreach (var item in outputs) { File.Move(item.Source, item.Target, overwrite); committed.Add(item.Target); }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var target in committed.AsEnumerable().Reverse())
                try { if (backups.TryGetValue(target, out var backup)) File.Copy(backup, target, true); else File.Delete(target); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException) { rollbackErrors.Add(rollback); }
            if (rollbackErrors.Count > 0)
            {
                preserveStaging = true;
                throw new IOException("保存输出失败，部分旧文件暂时无法恢复。备份已保留在：" + backupRoot, failure);
            }
            throw;
        }
    }
    private static void RewriteJsonFile(string path, string from, string to)
    {
        try { var json = JsonNode.Parse(File.ReadAllText(path)); File.WriteAllText(path, RewritePaths(json, from, to)!.ToJsonString(DesktopSettings.JsonOptions), new UTF8Encoding(false)); }
        catch (JsonException) { }
    }
}
