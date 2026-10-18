using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    internal sealed record DragInputSample(double InjectedMilliseconds, double DeliveredMilliseconds, double X, double Y);
    internal sealed record DragFrameSample(double Milliseconds, PreviewFrame Frame, string? PixelHash,
        double PresentationQueueMilliseconds, double PresentationMilliseconds);
    internal sealed record OperationSample(double Milliseconds, PreviewOperation Operation);
    internal sealed record DragTrace(PreviewTarget Origin, IReadOnlyList<DragInputSample> Inputs,
        IReadOnlyList<DragFrameSample> Frames, IReadOnlyList<OperationSample> Operations, double HeldMilliseconds);

    internal async Task<DragTrace> SendContinuousDragSmokeAsync(Window window, string actor,
        double dx, double dy, Action inspectWhileHeld)
    {
        nint hwnd = await PrepareSmokeInputAsync(window);
        var origin = LastFrame!.Targets.Single(target => target.Id == actor);
        var screen = SurfaceScreenPoint(hwnd, ActorSmokePoint(actor));
        double pixelWidth = imageRect.Width * XamlRoot.RasterizationScale;
        double pixelHeight = imageRect.Height * XamlRoot.RasterizationScale;
        var injections = new ConcurrentQueue<(double Time, double X, double Y)>();
        var inputs = new List<DragInputSample>(); var frames = new List<DragFrameSample>();
        var operations = new List<OperationSample>();
        var clock = Stopwatch.StartNew(); bool held = true;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDrag(object? sender, PreviewDrag drag)
        {
            if (drag.TargetId != actor) return;
            if (drag.IsCompleted) { released.TrySetResult(); return; }
            double now = clock.Elapsed.TotalMilliseconds;
            var closest = injections.ToArray().Where(point => point.Time <= now).OrderBy(point => Math.Abs(point.X - drag.DeltaX)
                + Math.Abs(point.Y - drag.DeltaY)).ThenByDescending(point => point.Time).FirstOrDefault();
            inputs.Add(new(closest.Time, now, drag.DeltaX, drag.DeltaY));
        }
        void OnPresented(object? sender, PreviewFrame frame)
        {
            if (held && dragTarget == actor && dragMoved && inputs.Count > 0)
            {
                double presented = clock.Elapsed.TotalMilliseconds;
                string? hash = frame.Pixels.Rgba.Length > 0 ? Convert.ToHexString(SHA256.HashData(frame.Pixels.Rgba)) : null;
                frames.Add(new(presented, frame with { Pixels = new(frame.Pixels.Width, frame.Pixels.Height, []) },
                    hash, PresentationQueueMilliseconds, PresentationMilliseconds));
            }
        }
        void OnOperation(PreviewOperation operation) => operations.Add(new(clock.Elapsed.TotalMilliseconds, operation));
        ObjectDragged += OnDrag; FramePresented += OnPresented; PreviewOperationObserved += OnOperation;
        MoveSmokePointer(screen.X, screen.Y); await Task.Delay(80);
        SendSmokeInput(new() { Flags = 0x0002 }); await Task.Delay(80);
        double motionStart = clock.Elapsed.TotalMilliseconds;
        try
        {
            // Injection runs outside the dispatcher. A blocked UI cannot turn a
            // continuous drag into a sequence of convenient, fully awaited edits.
            await Task.Run(async () =>
            {
                const double duration = 1400;
                while (true)
                {
                    double now = clock.Elapsed.TotalMilliseconds, fraction = Math.Min(1, (now - motionStart) / duration);
                    int x = (int)Math.Round(dx * pixelWidth * fraction), y = (int)Math.Round(dy * pixelHeight * fraction);
                    injections.Enqueue((now, x / pixelWidth, y / pixelHeight));
                    MoveSmokePointer(screen.X + x, screen.Y + y);
                    if (fraction >= 1) break;
                    await Task.Delay(10);
                }
            });
            inspectWhileHeld();
            return new(origin, inputs.ToArray(), frames.ToArray(), operations.ToArray(), clock.Elapsed.TotalMilliseconds - motionStart);
        }
        finally
        {
            held = false; SendSmokeInput(new() { Flags = 0x0004 });
            try { await released.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { ObjectDragged -= OnDrag; FramePresented -= OnPresented; PreviewOperationObserved -= OnOperation; }
        }
    }
}
