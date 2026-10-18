using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    readonly NumberBox actorCanvasMultiplier = new()
    {
        Header = "角色画布倍数（X）", Minimum = 1, Maximum = 10, Value = 1,
        SmallChange = .05, LargeChange = .25, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
    };
    readonly TextBlock actorCanvasStatus = Ui.Text("1 倍包住完整动画；增大倍数可在四周留白。", 11, true);
    readonly ProgressBar actorCanvasProgress = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    Button? fitActorCanvasButton;
    Button? cancelActorCanvasButton;
    CancellationTokenSource? actorCanvasCancellation;
    Task actorCanvasTask = Task.CompletedTask;

    FrameworkElement CreateActorCanvasControls()
    {
        fitActorCanvasButton = StudioLayout.AsyncTool("仅角色 · 适应完整动作", () => SafeAsync(FitActorCanvasAsync), "\uE9A6");
        fitActorCanvasButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(fitActorCanvasButton, "仅角色画布：适应完整动作");
        cancelActorCanvasButton = StudioLayout.Tool("取消计算", () => actorCanvasCancellation?.Cancel());
        cancelActorCanvasButton.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(fitActorCanvasButton, "按项目完整时长计算角色的最小矩形范围，宽和高各乘所设倍数。整段动画使用固定画布。");
        return Ui.Stack(6, actorCanvasMultiplier, fitActorCanvasButton, actorCanvasProgress, actorCanvasStatus, cancelActorCanvasButton);
    }

    void RefreshActorCanvasControls()
    {
        bool busy = actorCanvasCancellation is not null;
        if (fitActorCanvasButton is not null) fitActorCanvasButton.IsEnabled = document is not null && !busy;
        actorCanvasMultiplier.IsEnabled = document is not null && !busy;
        if (busy) return;
        actorCanvasMultiplier.Value = document?.CanvasMultiplier ?? 1;
        actorCanvasStatus.Text = document?.Canvas is not null
            ? $"仅角色 · {document.Width} × {document.Height} · {document.CanvasMultiplier:0.##} 倍。修改动作后可重新计算。"
            : "1 倍包住完整动画；增大倍数可在四周留白。";
    }

    Task FitActorCanvasAsync()
    {
        if (actorCanvasCancellation is not null) return actorCanvasTask;
        actorCanvasTask = FitActorCanvasCoreAsync();
        return actorCanvasTask;
    }

    async Task FitActorCanvasCoreAsync()
    {
        if (document is null) return;
        double multiplier = actorCanvasMultiplier.Value;
        if (!double.IsFinite(multiplier) || multiplier < 1)
            throw new ArgumentException("画布倍数需要大于或等于 1。");
        Pause(); await FlushEditsAsync();
        var current = document;
        if (current is null) return;
        if (modelDrag is { IsCompleted: false })
            throw new InvalidOperationException("请先完成模型拖动，再计算完整动作画布。");
        long edited = editVersion;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        actorCanvasCancellation = cancellation;
        fitActorCanvasButton!.IsEnabled = false; actorCanvasMultiplier.IsEnabled = false;
        cancelActorCanvasButton!.Visibility = Visibility.Visible;
        actorCanvasProgress.Visibility = Visibility.Visible; actorCanvasProgress.Value = 0;
        actorCanvasStatus.Text = "正在计算完整动作范围…";
        try
        {
            using var input = current.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "live2d-canvas"));
            var full = TimelineRange.Full(current.Duration, 30);
            var progress = new Progress<ActorCanvasProgress>(value =>
            {
                if (cancellation.IsCancellationRequested || !ReferenceEquals(actorCanvasCancellation, cancellation)) return;
                actorCanvasProgress.Value = value.Fraction * 100;
                actorCanvasStatus.Text = $"完整动作 · {value.CompletedFrames} / {value.TotalFrames} 帧";
            });
            var bounds = await ActorCanvasAnalysis.AnalyzeAsync(PreviewSource(input, current),
                full.StartFrame, full.EndFrameExclusive, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(document, current) || edited != editVersion || modelDrag is { IsCompleted: false })
            { Notice("计算期间模型或动作已更改，请重新计算画布。", false); return; }
            var fit = bounds.Fit(multiplier);
            Edit(value => value.ApplyCanvasFit(fit)); RefreshControls();
            await FlushEditsAsync(); viewport.FitCanvas(); SaveDraft();
            Notice($"画布已设为 {fit.Width} × {fit.Height}，覆盖完整动画的 {full.FrameCount} 帧，宽高各取 {multiplier:0.##} 倍。", false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!disposed) Notice("已取消画布计算。", false); }
        finally
        {
            actorCanvasCancellation = null;
            actorCanvasProgress.Visibility = Visibility.Collapsed;
            cancelActorCanvasButton!.Visibility = Visibility.Collapsed;
            if (!disposed) RefreshActorCanvasControls();
        }
    }
}
