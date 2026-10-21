using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    ScrollViewer? parameterScroll;
    long controlsRefreshStamp;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? resolutionTimer;
    (int Width, int Height, int Supersample)? previewSizeOverride;
    int previewSampling = 2;
    Task previewQualityTask = Task.CompletedTask;

    void InitializeAdaptivePreview()
    {
        resolutionTimer = DispatcherQueue.CreateTimer(); resolutionTimer.IsRepeating = false;
        resolutionTimer.Interval = TimeSpan.FromMilliseconds(200);
        resolutionTimer.Tick += (_, _) => _ = SafeAsync(ApplyPreviewSizeAsync);
        viewport.PresentationSizeChanged += (_, _) =>
        {
            if (!active || document is null || previewSizeOverride is not null || !viewport.HasCurrentSource) return;
            var size = DesiredPreviewSize(document);
            if (viewport.LastFrame is { } frame && frame.Pixels.Width == size.Width && frame.Pixels.Height == size.Height
                && viewport.PreviewSupersample == size.Supersample) return;
            resolutionTimer.Stop(); resolutionTimer.Start();
        };
    }

    (int Width, int Height, int Supersample) DesiredPreviewSize(OurNotes.Director.Desktop.Core.Live2DDocument current)
        => previewSizeOverride ?? viewport.RecommendedPreviewSize(current.Width, current.Height, previewSampling);

    void SelectPreviewSampling(int sampling)
    {
        // This is a presentation preference for this workbench instance. Resizing
        // the current renderer keeps its pose/session and leaves export settings alone.
        previewSampling = sampling;
        resolutionTimer?.Stop();
        var pending = previewQualityTask = ApplyPreviewSizeAsync();
        _ = SafeAsync(() => pending);
    }

    internal async Task ApplyPreviewSizeAsync()
    {
        if (!active || disposed || document is null || !viewport.HasCurrentSource) return;
        await previewGate.WaitAsync(lifetime.Token);
        try
        {
            if (!active || disposed || document is null || !ReferenceEquals(document, presentedDocument)
                || !viewport.HasCurrentSource) return;
            var owner = document; long version = loadVersion;
            var size = DesiredPreviewSize(owner);
            if (viewport.LastFrame is { } frame && frame.Pixels.Width == size.Width && frame.Pixels.Height == size.Height
                && viewport.PreviewSupersample == size.Supersample) return;
            await viewport.ResizePreviewAsync(size.Width, size.Height, size.Supersample, lifetime.Token);
            // F1 or layout can change while the native redraw is in flight. The
            // retained old frame may already match that newer size, so its event
            // can legitimately skip scheduling. Reconcile after this frame lands.
            if (active && !disposed && version == loadVersion && viewport.HasCurrentSource
                && ReferenceEquals(document, owner) && ReferenceEquals(presentedDocument, owner)
                && DesiredPreviewSize(owner) != size)
            { resolutionTimer!.Stop(); resolutionTimer.Start(); }
        }
        finally { previewGate.Release(); }
    }

    async Task SetPreviewSizeOverrideAsync((int Width, int Height, int Supersample)? size)
    {
        resolutionTimer?.Stop(); previewSizeOverride = size; await ApplyPreviewSizeAsync();
    }

    HashSet<DependencyObject> FocusedAncestors()
    {
        var result = new HashSet<DependencyObject>();
        if (XamlRoot is null) return result;
        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
             element is not null; element = VisualTreeHelper.GetParent(element)) result.Add(element);
        return result;
    }

    bool ParameterRowIsVisible(FrameworkElement row)
    {
        if (parameterScroll is not { ActualHeight: > 0 } scroll || !row.IsLoaded) return false;
        double y = row.TransformToVisual(scroll).TransformPoint(new Point()).Y;
        return y < scroll.ActualHeight && y + row.ActualHeight > 0;
    }

    void RefreshPlayingControls(bool playing)
    {
        // Playback remains frame accurate in the monitor and transport. The inspector
        // reports controller/animation state at 10 Hz and immediately on pause/seek.
        if (playing && Stopwatch.GetElapsedTime(controlsRefreshStamp).TotalMilliseconds < 100) return;
        controlsRefreshStamp = Stopwatch.GetTimestamp(); RefreshTimeDependentControls();
    }
}
