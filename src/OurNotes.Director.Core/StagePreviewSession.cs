using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Small stage thumbnails on one dedicated GL thread. No character, audio or
/// source performance is loaded; the editing viewport/project is never touched.</summary>
public sealed class StagePreviewSession : IAsyncDisposable
{
    readonly BlockingCollection<Request> queue = new();
    readonly Dictionary<string, Task<byte[]>> images = new(StringComparer.Ordinal);
    readonly object gate = new();
    readonly Task worker;
    bool closed;
    sealed record Request(string Stage, TaskCompletionSource<byte[]> Completion);

    public StagePreviewSession(Archive archive, string site, string source, string language)
    {
        worker = Task.Factory.StartNew(() => Run(archive, site, source, language),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public Task<byte[]> GetAsync(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) throw new ArgumentException("舞台不能为空。");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (images.TryGetValue(stage, out var cached)) return cached;
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            images.Add(stage, completion.Task); queue.Add(new(stage, completion)); return completion.Task;
        }
    }
    void Run(Archive archive, string site, string source, string language)
    {
        AdvStoryRenderer? renderer = null;
        try
        {
            foreach (var request in queue.GetConsumingEnumerable())
            {
                lock (gate) if (closed) { request.Completion.TrySetCanceled(); continue; }
                try
                {
                    if (renderer is null)
                    {
                        var project = CreativeScene.Blank(site, language).Compile();
                        project["source"]!["adv"] = source;
                        project["initialize"] = new JsonArray(); project["finalize"] = new JsonArray();
                        project["commands"] = new JsonArray((JsonNode)new JsonObject { ["cmd"] = "Delay", ["Frames"] = 1 });
                        renderer = new(archive, site, "", source, language, 320, 180, null, "", 1, project);
                    }
                    renderer.Scene.SetStage(request.Stage);
                    request.Completion.TrySetResult(Png.Encode(renderer.Frame(0, true)));
                }
                catch (Exception failure)
                {
                    request.Completion.TrySetException(failure);
                    renderer?.Dispose(); renderer = null;
                }
            }
        }
        finally { renderer?.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (closed) return; closed = true; queue.CompleteAdding(); }
        await worker.ConfigureAwait(false); queue.Dispose();
    }
}
