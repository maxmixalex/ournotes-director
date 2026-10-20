using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.Foundation;
using Windows.System;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    // Uses the actual Creative/Home monitor and full-screen shortcut. WARP is
    // explicit here: it verifies composition, not physical WGL interoperability.
    internal async Task RunGpuLayoutSmokeAsync(MainWindow window, Action<bool, string> check)
    {
        await PauseForEditingAsync(); await WaitForGpuLayoutAsync();
        var original = LastFrame ?? throw new InvalidOperationException("GPU 布局检查需要已载入的主页画面。");
        await using var fixture = new GpuCompositionFixture();
        var test = await fixture.Surface;
        check(test.IsSoftwareAdapter == true && !string.IsNullOrWhiteSpace(test.AdapterName),
            "主页节目监视器：从实际 WARP 交换链查询设备名称和软件标志，未冒充硬件互操作");
        var samples = new JsonArray();
        double priorWidth = Width, priorHeight = Height;
        var priorTransform = RenderTransform; var priorHorizontal = HorizontalAlignment; var priorVertical = VerticalAlignment;
        string? priorSelected = selected;
        uint previousPresentCount = await fixture.RunAsync(test.ReadCompositionTestPresentCount);
        bool entered = false;
        const uint firstColor = 0xFFD02BD0, secondColor = 0xFF35CB47;
        try
        {
            selected = null; HideSelection();
            compositionTestLayout = (gpu, width, height) => fixture.RunAsync(() => gpu.SetDisplaySize(width, height));
            if (!PresentGpu(original with { GpuSurface = test })) throw new InvalidOperationException("无法绑定 WARP 交换链。");
            scaledGpu = null; LayoutCanvas();
            await fixture.RunAsync(() => test.PresentCompositionTestColor(firstColor));
            await Capture("paused-window", firstColor);
            Focus(FocusState.Keyboard); await SendShellKeySmokeAsync(window, VirtualKey.F1); entered = true;
            await Capture("paused-fullscreen", firstColor);
            check(window.IsMonitorFullscreen && !IsPlaying, "GPU 画布：暂停状态实际进入全屏");

            // Ancestor scaling exercises SwapChainPanel's composition scale on
            // every runner, even when CI only provides a 100% DPI display.
            Width = ActualWidth / 1.5; Height = ActualHeight / 1.5;
            HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Top;
            RenderTransform = new ScaleTransform { ScaleX = 1.5, ScaleY = 1.5 };
            await Capture("paused-scale150", firstColor);
            await fixture.RunAsync(() => test.PresentCompositionTestColor(secondColor));
            await Capture("new-pixels-scale150", secondColor);
            RenderTransform = priorTransform; Width = priorWidth; Height = priorHeight;
            HorizontalAlignment = priorHorizontal; VerticalAlignment = priorVertical;
            Focus(FocusState.Keyboard); await SendShellKeySmokeAsync(window, VirtualKey.F1); entered = false;
            await Capture("paused-restored", secondColor);
            check(LastFrame?.Sequence == original.Sequence && LastFrame?.SessionId == original.SessionId
                && LastFrame?.Time == original.Time && !IsPlaying,
                "GPU 画布：暂停全屏、150% 合成缩放与恢复只重新呈现保留纹理，不重建会话或推进原生时间");
        }
        finally
        {
            if (entered || window.IsMonitorFullscreen) window.ExitMonitorFullscreen();
            RenderTransform = priorTransform; Width = priorWidth; Height = priorHeight;
            HorizontalAlignment = priorHorizontal; VerticalAlignment = priorVertical;
            DetachGpu(); compositionTestLayout = null;
            if (original.GpuSurface is { IsDisposed: false }) PresentGpu(original);
            selected = priorSelected; LayoutCanvas(); await WaitForGpuLayoutAsync();
            await CapturePresentedDesktopSmokeAsync(window, "home-monitor-restored-desktop");
            DesktopSettings.AtomicWrite(App.SmokeEvidencePath("gpu-composition.json"), new JsonObject
            {
                ["source"] = "DWM desktop client pixels", ["adapter"] = test.AdapterName,
                ["software"] = test.IsSoftwareAdapter, ["physicalGpuInteropTested"] = false,
                ["samples"] = samples
            }.ToJsonString(DesktopSettings.JsonOptions));
        }

        async Task Capture(string name, uint color)
        {
            ((FrameworkElement)window.Content).UpdateLayout(); await Task.Delay(180);
            ((FrameworkElement)window.Content).UpdateLayout(); LayoutCanvas(); await WaitForGpuLayoutAsync();
            MovePointerOutsideCanvasSmoke(window);
            await Task.Delay(50);
            var pixels = DesktopPixels.CaptureClient(window);
            string path = App.SmokeEvidencePath("gpu-composition-" + name + ".png");
            await File.WriteAllBytesAsync(path, Png.Encode(pixels));
            var transform = surface.TransformToVisual((UIElement)window.Content);
            double dpi = XamlRoot.RasterizationScale;
            var start = transform.TransformPoint(new(imageRect.X, imageRect.Y));
            var end = transform.TransformPoint(new(imageRect.Right, imageRect.Bottom));
            var clipStart = transform.TransformPoint(new(0, 0));
            var clipEnd = transform.TransformPoint(new(surface.ActualWidth, surface.ActualHeight));
            int left = Math.Clamp((int)Math.Ceiling(Math.Max(start.X, clipStart.X) * dpi) + 3, 0, pixels.Width);
            int top = Math.Clamp((int)Math.Ceiling(Math.Max(start.Y, clipStart.Y) * dpi) + 3, 0, pixels.Height);
            int right = Math.Clamp((int)Math.Floor(Math.Min(end.X, clipEnd.X) * dpi) - 3, left, pixels.Width);
            int bottom = Math.Clamp((int)Math.Floor(Math.Min(end.Y, clipEnd.Y) * dpi) - 3, top, pixels.Height);
            int matches = 0, total = (right - left) * (bottom - top);
            for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
            {
                int index = (y * pixels.Width + x) * 4;
                if (Math.Abs(pixels.Rgba[index] - (int)(color & 255)) <= 6
                    && Math.Abs(pixels.Rgba[index + 1] - (int)((color >> 8) & 255)) <= 6
                    && Math.Abs(pixels.Rgba[index + 2] - (int)((color >> 16) & 255)) <= 6) matches++;
            }
            double coverage = total > 0 ? matches / (double)total : 0;
            uint presentCount = await fixture.RunAsync(test.ReadCompositionTestPresentCount);
            bool didPresent = presentCount > previousPresentCount;
            previousPresentCount = presentCount;
            var corners = new JsonArray();
            (double X, double Y, uint Color)[] markers = [(2, 2, 0xFFFFFFFF), (test.Width - 2, 2, 0xFF2020EF),
                (2, test.Height - 2, 0xFFEDBC29), (test.Width - 2, test.Height - 2, 0xFF1A1A1A)];
            bool allCorners = true;
            foreach (var marker in markers)
            {
                var point = transform.TransformPoint(new(imageRect.X + imageRect.Width * marker.X / test.Width,
                    imageRect.Y + imageRect.Height * marker.Y / test.Height));
                int x = (int)Math.Round(point.X * dpi), y = (int)Math.Round(point.Y * dpi);
                int index = (y * pixels.Width + x) * 4;
                bool match = x >= 0 && x < pixels.Width && y >= 0 && y < pixels.Height
                    && Math.Abs(pixels.Rgba[index] - (int)(marker.Color & 255)) <= 6
                    && Math.Abs(pixels.Rgba[index + 1] - (int)((marker.Color >> 8) & 255)) <= 6
                    && Math.Abs(pixels.Rgba[index + 2] - (int)((marker.Color >> 16) & 255)) <= 6;
                allCorners &= match; corners.Add((JsonNode)new JsonObject { ["x"] = x, ["y"] = y, ["matched"] = match });
            }
            samples.Add((JsonNode)new JsonObject
            {
                ["name"] = name, ["path"] = path, ["width"] = pixels.Width, ["height"] = pixels.Height,
                ["left"] = left, ["top"] = top, ["right"] = right, ["bottom"] = bottom,
                ["matchingPixels"] = matches, ["expectedPixels"] = total, ["coverage"] = coverage,
                ["presentCount"] = presentCount, ["presentedSincePrevious"] = didPresent,
                ["rasterizationScale"] = dpi, ["compositionScaleX"] = gpuPanel.CompositionScaleX,
                ["compositionScaleY"] = gpuPanel.CompositionScaleY, ["nativeSequence"] = LastFrame?.Sequence,
                ["nativeTime"] = LastFrame?.Time, ["corners"] = corners
            });
            check(total > 10000 && coverage > .97,
                $"GPU 实际屏幕像素 {name}：画布边界内覆盖 {coverage:P1}，没有左上角四分之一画面或空白余区");
            check(allCorners, $"GPU 实际屏幕像素 {name}：四个不同颜色角标完整映射到画布四角，内容没有被过度放大裁切");
            check(didPresent, $"GPU 暂停缩放 {name}：真实 Present 计数前进，像素无需播放或重新定位即可更新");
        }
    }
    internal async Task CapturePresentedDesktopSmokeAsync(MainWindow window, string name)
    {
        ((FrameworkElement)window.Content).UpdateLayout(); await WaitForGpuLayoutAsync();
        MovePointerOutsideCanvasSmoke(window); await Task.Delay(80);
        var pixels = DesktopPixels.CaptureClient(window);
        await File.WriteAllBytesAsync(App.SmokeEvidencePath(name + ".png"), Png.Encode(pixels));
    }
    void MovePointerOutsideCanvasSmoke(MainWindow window)
    {
        var origin = new ScreenPoint();
        if (!ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(window), ref origin))
            throw new InvalidOperationException("无法定位截图时的指针位置。");
        MoveSmokePointer(origin.X + 2, origin.Y + 2);
        navigationHelp.IsOpen = false; diagnostics.IsOpen = false;
    }

    // WARP has the same one-owner D3D rule as production. In particular, never
    // block the XAML thread in Present while it needs to commit an attachment.
    sealed class GpuCompositionFixture : IAsyncDisposable
    {
        readonly BlockingCollection<Action> pending = new();
        readonly TaskCompletionSource<GpuPreviewSurface> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Task worker;
        public Task<GpuPreviewSurface> Surface => ready.Task;

        public GpuCompositionFixture()
        {
            worker = Task.Factory.StartNew(() =>
            {
                try
                {
                    using var surface = GpuPreviewSurface.CreateCompositionTest(320, 180);
                    ready.SetResult(surface);
                    foreach (var action in pending.GetConsumingEnumerable()) action();
                }
                catch (Exception error) { ready.TrySetException(error); throw; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        public Task RunAsync(Action action) => RunAsync(() => { action(); return true; });
        public Task<T> RunAsync<T>(Func<T> action)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(() =>
            {
                try { result.SetResult(action()); }
                catch (Exception error) { result.SetException(error); }
            });
            return result.Task;
        }
        public async ValueTask DisposeAsync()
        { pending.CompleteAdding(); try { await worker; } finally { pending.Dispose(); } }
    }
}
