using OurNotes.Director.Desktop.Core;

static class LatestEditQueueTests
{
    public static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();
    static async Task RunAsync(Action<bool, string> check)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<int>(); int active = 0, maximum = 0;
        var queue = new LatestEditQueue<int>(async sample =>
        {
            maximum = Math.Max(maximum, ++active); seen.Add(sample);
            if (sample == 0) await release.Task;
            active--;
        });
        var tasks = new List<Task> { queue.Post(0) };
        for (int i = 1; i <= 100; i++) tasks.Add(queue.Post(i));
        tasks.Add(queue.Post(101, terminal: true));
        tasks.Add(queue.Post(200)); tasks.Add(queue.Post(201, terminal: true));
        release.SetResult(); await Task.WhenAll(tasks);
        check(seen.SequenceEqual(new[] { 0, 101, 201 }), "Waiting drag samples coalesce without losing either gesture's release");
        check(maximum == 1, "Asynchronous drag initialization, redraw and final commit never overlap");
        var recover = new LatestEditQueue<int>(sample => sample < 0 ? Task.FromException(new InvalidOperationException("test")) : Task.CompletedTask);
        bool failed = false;
        try { await recover.Post(-1, true); } catch (InvalidOperationException) { failed = true; }
        await recover.Post(1, true);
        check(failed, "An invalid gesture reports its error and does not poison subsequent edits");
    }
}
