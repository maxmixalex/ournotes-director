using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using OurNotes.Director.Core;
using Windows.Foundation;
using Windows.System;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    internal async Task SendShellKeySmokeAsync(MainWindow window, VirtualKey key)
    {
        await PrepareSmokeInputAsync(window);
        var target = (UIElement)window.Content;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyEventHandler handler = (_, args) => { if (args.Key == key) observed.TrySetResult(); };
        target.AddHandler(PreviewKeyDownEvent, handler, true);
        try { SendSmokeKey(key, false); await observed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { SendSmokeKey(key, true); target.RemoveHandler(PreviewKeyDownEvent, handler); }
        await Task.Delay(70);
    }

    internal sealed record ScrubSample(double Milliseconds, int Frame, long Session, bool Playing, string? Pixels);
    internal async Task<IReadOnlyList<ScrubSample>> SendTimelineScrubSmokeAsync(Window window, FrameworkElement ruler, double startX, double endX)
    {
        nint hwnd = await PrepareSmokeInputAsync(window);
        var start = SurfaceScreenPoint(hwnd, ruler.TransformToVisual(surface).TransformPoint(new Point(startX, 12)));
        var end = SurfaceScreenPoint(hwnd, ruler.TransformToVisual(surface).TransformPoint(new Point(endX, 12)));
        var samples = new List<ScrubSample>(); var clock = Stopwatch.StartNew();
        bool held = false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Presented(object? sender, PreviewFrame frame)
        {
            if (!held || frame.RenderMilliseconds <= 0) return;
            string? pixels = null;
            if (frame.Pixels.ByteLength > 0)
            {
                for (int row = 0; row < frame.Pixels.Height; row++) hash.AppendData(frame.Pixels.GetRgbaRow(row));
                pixels = Convert.ToHexString(hash.GetHashAndReset());
            }
            samples.Add(new(clock.Elapsed.TotalMilliseconds, frame.Frame, frame.SessionId, frame.IsPlaying, pixels));
        }
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PointerEventHandler releaseHandler = (_, _) => released.TrySetResult();
        ruler.AddHandler(PointerReleasedEvent, releaseHandler, true);
        FramePresented += Presented;
        MoveSmokePointer(start.X, start.Y); await Task.Delay(80);
        held = true; SendSmokeInput(new() { Flags = 0x0002 });
        try
        {
            // Mouse input keeps arriving while the UI and native renderer work.
            await Task.Run(async () =>
            {
                var moving = Stopwatch.StartNew();
                while (moving.ElapsedMilliseconds < 1400)
                {
                    double fraction = Math.Clamp(moving.Elapsed.TotalMilliseconds / 1400, 0, 1);
                    MoveSmokePointer(start.X + (int)Math.Round((end.X - start.X) * fraction), start.Y);
                    await Task.Delay(10);
                }
                MoveSmokePointer(end.X, end.Y);
            });
        }
        finally
        {
            held = false; SendSmokeInput(new() { Flags = 0x0004 });
            try { await released.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { ruler.RemoveHandler(PointerReleasedEvent, releaseHandler); FramePresented -= Presented; }
        }
        return samples;
    }
}
