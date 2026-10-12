using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace OurNotes.Director.Desktop.Core;

public enum DesktopTaskStatus { Queued, Running, Succeeded, Failed, Cancelled, Interrupted }

public sealed class DesktopTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required EngineRequest Request { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DesktopTaskStatus Status { get; internal set; } = DesktopTaskStatus.Queued;
    public string Message { get; internal set; } = "等待开始";
    public double? Fraction { get; internal set; }
    public EngineResult? Result { get; internal set; }
    public string? ReportPath { get; internal set; }
    public CancellationTokenSource Cancellation { get; } = new();
    internal TaskCompletionSource<EngineResult> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<EngineResult> Completion => Source.Task;
    public string StatusLabel => Status switch
    {
        DesktopTaskStatus.Queued => "等待中", DesktopTaskStatus.Running => "正在处理", DesktopTaskStatus.Succeeded => "已完成",
        DesktopTaskStatus.Failed => "未完成", DesktopTaskStatus.Cancelled => "已取消", _ => "上次运行已中断"
    };
}

public sealed class TaskQueue : IAsyncDisposable
{
    private readonly EngineRunner _runner;
    private readonly Channel<DesktopTask> _channel = Channel.CreateUnbounded<DesktopTask>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<DesktopTask> _tasks = [];
    private readonly string _directory;
    private readonly Task _worker;
    private bool _disposed;
    public event Action<DesktopTask>? Changed;
    public IReadOnlyList<DesktopTask> Tasks { get { lock (_tasks) return _tasks.ToArray(); } }
    public bool IsBusy => Tasks.Any(t => t.Status is DesktopTaskStatus.Queued or DesktopTaskStatus.Running);

    public TaskQueue(EngineRunner runner, string? directory = null)
    {
        _runner = runner; _directory = directory ?? Path.Combine(DesktopSettings.DataDirectory, "tasks");
        LoadHistory(); _worker = ConsumeAsync();
    }

    public DesktopTask Enqueue(EngineRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int projectIndex = request.CommandId.StartsWith("render.", StringComparison.Ordinal) || request.CommandId == "shot.validate" ? 2
            : request.CommandId.StartsWith("story.", StringComparison.Ordinal) ? request.Arguments.ToList().IndexOf("--script") + 1
            : request.CommandId.StartsWith("home.", StringComparison.Ordinal) ? request.Arguments.ToList().IndexOf("--project") + 1 : -1;
        if (projectIndex > 0)
        {
            // Persist the exact queued input for history/retry. Local asset paths are
            // normalized before moving it away from the user's project directory.
            var arguments = request.Arguments.ToArray();
            if (arguments.Length > projectIndex)
            {
                string source = Path.GetFullPath(arguments[projectIndex], Path.GetFullPath(request.WorkingDirectory));
                if (!File.Exists(source)) throw new FileNotFoundException("找不到这次任务的场景或指令文件。", source);
                string inputs = Path.Combine(_directory, "inputs"); Directory.CreateDirectory(inputs);
                string input = Path.Combine(inputs, Guid.NewGuid().ToString("N") + ".json");
                var raw = JsonNode.Parse(File.ReadAllText(source));
                if (raw is JsonObject obj && (obj["format"]?.ToString() == "ournotes-director/project" || obj["actors"] is JsonArray))
                { using var snapshot = SceneDocument.Open(source).CreateSnapshot(inputs); File.Copy(snapshot.FilePath, input); }
                else
                {
                    // Native scripts can be episode objects or plain command arrays.
                    // Rebase only known image references, leaving other strings intact.
                    IEnumerable<JsonNode?> rows = raw is JsonArray array ? array : raw is JsonObject episode
                        ? new[] { "initialize", "commands", "finalize" }.SelectMany(section => episode[section] as JsonArray ?? []) : [];
                    foreach (var row in rows.OfType<JsonObject>())
                        if (row["cmd"]?.ToString() == "Director.Scene" && row["Values"]?["backgroundImage"] is JsonObject image
                            && image["path"] is JsonValue pathValue && pathValue.TryGetValue<string>(out var imagePath) && !string.IsNullOrWhiteSpace(imagePath))
                            image["path"] = Path.GetFullPath(imagePath, Path.GetDirectoryName(source)!);
                    DesktopSettings.AtomicWrite(input, raw?.ToJsonString(DesktopSettings.JsonOptions) ?? "null");
                }
                arguments[projectIndex] = input;
                request = request with { Arguments = arguments };
            }
        }
        var task = new DesktopTask { Request = request };
        lock (_tasks) _tasks.Insert(0, task);
        Persist(task); Changed?.Invoke(task);
        if (!_channel.Writer.TryWrite(task))
        {
            lock (_tasks) _tasks.Remove(task);
            if (task.ReportPath is { } report && File.Exists(report)) File.Delete(report);
            throw new InvalidOperationException("任务队列已关闭。");
        }
        return task;
    }

    public void Cancel(DesktopTask task) => task.Cancellation.Cancel();

    private async Task ConsumeAsync()
    {
        await foreach (var task in _channel.Reader.ReadAllAsync())
        {
            task.Status = DesktopTaskStatus.Running; task.Message = "正在准备…"; Persist(task); Changed?.Invoke(task);
            var result = await _runner.RunAsync(task.Request, progress =>
            { task.Message = progress.Message; task.Fraction = progress.Fraction; Changed?.Invoke(task); }, task.Cancellation.Token);
            task.Result = result; task.Status = result.Cancelled ? DesktopTaskStatus.Cancelled : result.Success ? DesktopTaskStatus.Succeeded : DesktopTaskStatus.Failed;
            task.Fraction = result.Success ? 1 : task.Fraction;
            task.Message = result.Error ?? $"完成，用时 {result.Elapsed.TotalSeconds:F1} 秒";
            Persist(task); Changed?.Invoke(task); task.Source.TrySetResult(result);
        }
    }

    private void Persist(DesktopTask task)
    {
        try
        {
            string path = Path.Combine(_directory, task.Id + ".json"); task.ReportPath = path;
            var node = new JsonObject
            {
                ["id"] = task.Id, ["createdAt"] = task.CreatedAt.ToString("O"), ["command"] = task.Request.CommandId,
                ["title"] = task.Request.Title, ["status"] = task.Status.ToString(), ["message"] = task.Message,
                ["arguments"] = new JsonArray(task.Request.Arguments.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                ["workingDirectory"] = task.Request.WorkingDirectory, ["outputPath"] = task.Request.OutputPath,
                ["outputKind"] = task.Request.OutputKind.ToString(), ["exitCode"] = task.Result?.ExitCode,
                ["result"] = task.Result?.Json?.DeepClone(), ["log"] = task.Result?.Log,
                ["elapsedSeconds"] = task.Result?.Elapsed.TotalSeconds
            };
            DesktopSettings.AtomicWrite(path, node.ToJsonString(DesktopSettings.JsonOptions));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { task.ReportPath = null; }
    }

    private void LoadHistory()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(file))!;
                var status = Enum.TryParse<DesktopTaskStatus>(root["status"]?.ToString(), out var s) ? s : DesktopTaskStatus.Interrupted;
                if (status is DesktopTaskStatus.Queued or DesktopTaskStatus.Running) status = DesktopTaskStatus.Interrupted;
                var request = new EngineRequest(root["command"]!.ToString(), root["title"]!.ToString(),
                    root["arguments"]!.AsArray().Select(a => a!.ToString()).ToArray(), root["workingDirectory"]!.ToString(), root["outputPath"]?.ToString(),
                    Enum.TryParse<CommandOutputKind>(root["outputKind"]?.ToString(), out var kind) ? kind : CommandOutputKind.Json);
                var task = new DesktopTask
                {
                    Id = root["id"]!.ToString(), Request = request, CreatedAt = DateTimeOffset.Parse(root["createdAt"]!.ToString()),
                    Status = status, Message = status == DesktopTaskStatus.Interrupted ? "应用已关闭；可检查原设置后重新运行。" : root["message"]?.ToString() ?? "",
                    ReportPath = file
                };
                var json = root["result"]?.DeepClone();
                task.Result = new EngineResult(status == DesktopTaskStatus.Succeeded, status == DesktopTaskStatus.Cancelled,
                    (int?)root["exitCode"] ?? -1, json, json?.ToJsonString(DesktopSettings.JsonOptions) ?? "", root["log"]?.ToString() ?? "",
                    request.OutputPath, TimeSpan.FromSeconds((double?)root["elapsedSeconds"] ?? 0), status == DesktopTaskStatus.Succeeded ? null : task.Message);
                task.Source.TrySetResult(task.Result); _tasks.Add(task);
            }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException or ArgumentException or NullReferenceException or OverflowException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _channel.Writer.TryComplete(); foreach (var task in Tasks.Where(t => t.Status is DesktopTaskStatus.Queued or DesktopTaskStatus.Running)) task.Cancellation.Cancel();
        await _worker;
    }
}
