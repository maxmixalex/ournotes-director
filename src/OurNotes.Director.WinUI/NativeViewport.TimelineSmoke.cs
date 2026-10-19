using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
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
        var initial = LastFrame!;
        var samples = new List<ScrubSample>(); var clock = Stopwatch.StartNew();
        var performance = new List<(double Time, PreviewFrame Frame, double Queue, double Presentation)>();
        var inputs = new List<(double Time, string Kind, double X, double Y, bool Left, int Captures)>();
        var operations = new List<(double Time, PreviewOperation Operation)>();
        bool held = false;
        double pressedAt = 0, releasedAt = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Presented(object? sender, PreviewFrame frame)
        {
            if (frame.RenderMilliseconds <= 0) return;
            double at = clock.Elapsed.TotalMilliseconds;
            string? pixels = null;
            if (frame.Pixels.ByteLength > 0)
            {
                for (int row = 0; row < frame.Pixels.Height; row++) hash.AppendData(frame.Pixels.GetRgbaRow(row));
                pixels = Convert.ToHexString(hash.GetHashAndReset());
            }
            if (held) samples.Add(new(at, frame.Frame, frame.SessionId, frame.IsPlaying, pixels));
            performance.Add((at, frame with { Pixels = new(frame.Pixels.Width, frame.Pixels.Height, []) },
                PresentationQueueMilliseconds, PresentationMilliseconds));
        }
        void Input(string kind, PointerRoutedEventArgs args)
        {
            if (inputs.Count >= 512) return;
            var point = args.GetCurrentPoint(ruler);
            inputs.Add((clock.Elapsed.TotalMilliseconds, kind, point.Position.X, point.Position.Y,
                point.Properties.IsLeftButtonPressed, ruler.PointerCaptures?.Count ?? 0));
        }
        void Operation(PreviewOperation operation)
        { if (operations.Count < 512) operations.Add((clock.Elapsed.TotalMilliseconds, operation)); }
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PointerEventHandler pressedHandler = (_, args) => Input("pressed", args);
        PointerEventHandler movedHandler = (_, args) => Input("moved", args);
        PointerEventHandler lostHandler = (_, args) => Input("capture-lost", args);
        PointerEventHandler releaseHandler = (_, args) => { Input("released", args); released.TrySetResult(); };
        ruler.AddHandler(PointerPressedEvent, pressedHandler, true);
        ruler.AddHandler(PointerMovedEvent, movedHandler, true);
        ruler.AddHandler(PointerCaptureLostEvent, lostHandler, true);
        ruler.AddHandler(PointerReleasedEvent, releaseHandler, true);
        FramePresented += Presented; PreviewOperationObserved += Operation;
        try
        {
            MoveSmokePointer(start.X, start.Y); await Task.Delay(80);
            held = true; pressedAt = clock.Elapsed.TotalMilliseconds;
            SendSmokeInput(new() { Flags = 0x0002 });
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
            held = false; releasedAt = clock.Elapsed.TotalMilliseconds;
            SendSmokeInput(new() { Flags = 0x0004 });
            try
            {
                await released.Task.WaitAsync(TimeSpan.FromSeconds(3));
                // Observe the first in-flight draw even when a slow driver finishes
                // it only after mouse-up. These extra frames never count as held input.
                await session.SynchronizeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await AwaitPresentationAsync();
            }
            finally
            {
                ruler.RemoveHandler(PointerPressedEvent, pressedHandler);
                ruler.RemoveHandler(PointerMovedEvent, movedHandler);
                ruler.RemoveHandler(PointerCaptureLostEvent, lostHandler);
                ruler.RemoveHandler(PointerReleasedEvent, releaseHandler);
                FramePresented -= Presented; PreviewOperationObserved -= Operation;
                // Preserve observations before the caller's assertions. A failing
                // scrub must retain its input, render and presentation evidence.
                var frames = new JsonArray();
                foreach (var measured in performance)
                {
                    var p = measured.Frame.Performance;
                    var sample = samples.FirstOrDefault(value => value.Milliseconds == measured.Time);
                    frames.Add((JsonNode)new JsonObject
                    {
                        ["timeMs"] = measured.Time, ["frame"] = measured.Frame.Frame, ["session"] = measured.Frame.SessionId,
                        ["playing"] = measured.Frame.IsPlaying, ["pixels"] = sample?.Pixels, ["held"] = sample is not null,
                        ["renderMs"] = measured.Frame.RenderMilliseconds, ["queueMs"] = measured.Queue,
                        ["presentationMs"] = measured.Presentation, ["simulationMs"] = p?.SimulationMilliseconds,
                        ["sceneMs"] = p?.SceneMilliseconds, ["readbackMs"] = p?.ReadbackMilliseconds,
                        ["gcPauseMs"] = p?.Diagnostics?.GcPauseMilliseconds,
                        ["shaderMs"] = p?.Diagnostics?.ShaderCompilationMilliseconds
                    });
                }
                var pointer = new JsonArray();
                foreach (var input in inputs) pointer.Add((JsonNode)new JsonObject
                { ["timeMs"] = input.Time, ["kind"] = input.Kind, ["x"] = input.X, ["y"] = input.Y,
                    ["left"] = input.Left, ["captures"] = input.Captures });
                var actions = new JsonArray();
                foreach (var op in operations) actions.Add((JsonNode)new JsonObject
                { ["timeMs"] = op.Time, ["kind"] = op.Operation.Kind, ["frame"] = op.Operation.Frame,
                    ["session"] = op.Operation.SessionId, ["renderedSequence"] = op.Operation.RenderedSequence,
                    ["presentedSequence"] = op.Operation.PresentedSequence });
                var report = new JsonObject
                {
                    ["initialFrame"] = initial.Frame, ["initialSession"] = initial.SessionId,
                    ["width"] = initial.Pixels.Width, ["height"] = initial.Pixels.Height, ["device"] = initial.Device,
                    ["startX"] = start.X, ["startY"] = start.Y, ["endX"] = end.X, ["endY"] = end.Y,
                    ["pressedMs"] = pressedAt, ["releasedMs"] = releasedAt, ["frames"] = frames,
                    ["inputs"] = pointer, ["operations"] = actions
                };
                DesktopSettings.AtomicWrite(App.SmokeEvidencePath("timeline-scrub-trace.json"), report.ToJsonString(DesktopSettings.JsonOptions));
            }
        }
        return samples;
    }
}
