using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

/// <summary>Shared by scene, home and Live2D. Intermediate native frames are allowed
/// to finish while incoming pointer samples replace only the waiting target.</summary>
internal sealed class TimelineScrubber(Func<double, CancellationToken, Task> seek, Action<Exception> failed)
{
    readonly LatestSeekQueue queue = new(seek, failed);
    internal bool IsBusy => queue.IsBusy;
    internal void Request(double seconds) => queue.Request(seconds);
    internal void Cancel() => queue.Cancel();
    internal Task FlushAsync() => queue.FlushAsync();
}
