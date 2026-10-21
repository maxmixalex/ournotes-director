using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    sealed class ModelDrag(Live2DDocument document, bool flushBeforePreview, int width, int height)
    {
        internal readonly Live2DDocument Document = document;
        internal readonly Live2DTransform Origin = document.Transform;
        internal readonly (double X, double Y) CanvasScale = DragCanvasScale(document, width, height);
        internal Live2DTransform Value = document.Transform;
        internal bool FlushBeforePreview = flushBeforePreview, IsCompleted;
        internal long Version, PresentedVersion;
    }

    ModelDrag? modelDrag;
    Task modelDragTask = Task.CompletedTask;
    bool presentingModelDrag;

    // Pointer movement changes the native isolated actor, never an XAML translation.
    // At most one native update is in flight; subsequent pointer events replace its
    // pending value, so neither a quiet mouse nor a backlog of stale positions is needed.
    void QueueModelDrag(PreviewDrag drag)
    {
        if (document is null || drag.TargetId != document.ActorId) return;
        if (modelDrag is not { IsCompleted: false } gesture || !ReferenceEquals(gesture.Document, document))
        {
            actorCanvasCancellation?.Cancel();
            Pause(); DeferDraft(); scrubber.Cancel(); previewTimer.Stop();
            modelDrag = gesture = new(document, previewDirty, viewport.LastFrame?.Pixels.Width ?? document.Width,
                viewport.LastFrame?.Pixels.Height ?? document.Height);
        }
        gesture.Value = gesture.Origin with
        { X = gesture.Origin.X + drag.DeltaX * gesture.CanvasScale.X,
          Y = gesture.Origin.Y + drag.DeltaY * gesture.CanvasScale.Y };
        gesture.Version++;
        RefreshTransformControls(gesture.Value);
        if (drag.IsCompleted)
        {
            gesture.IsCompleted = true;
            // One gesture is one undo operation. No document clone, snapshot file or
            // project commit is created for intermediate pointer positions.
            if (document.Transform != gesture.Value)
            {
                document.SetTransform(gesture.Value); editVersion++;
                previewDirty = transformPending = true;
                RefreshHeader(); ScheduleDraft();
            }
            // Capture loss uses the same completion path; a gesture that returns to
            // its origin must also resume a draft scheduled before the pointer press.
            ResumeDraft();
        }
        if (!presentingModelDrag)
        {
            var pending = modelDragTask = PresentModelDragAsync();
            _ = SafeAsync(() => pending);
        }
    }

    async Task PresentModelDragAsync()
    {
        presentingModelDrag = true;
        try
        {
            while (!disposed && modelDrag is { } gesture && ReferenceEquals(document, gesture.Document))
            {
                if (gesture.FlushBeforePreview)
                {
                    gesture.FlushBeforePreview = false;
                    if (previewDirty) await ApplyPreviewEditsAsync();
                }
                if (!ReferenceEquals(modelDrag, gesture) || !ReferenceEquals(document, gesture.Document)) continue;
                long version = gesture.Version;
                var transform = gesture.Value;
                if (version != gesture.PresentedVersion)
                {
                    await previewGate.WaitAsync(lifetime.Token);
                    try
                    {
                        if (disposed || !ReferenceEquals(document, gesture.Document) || !viewport.HasCurrentSource) return;
                        await viewport.SetIsolatedTransformAsync(new(transform.X, transform.Y, transform.Scale,
                            transform.Rotation, transform.Opacity));
                        gesture.PresentedVersion = version;
                    }
                    finally { previewGate.Release(); }
                }
                if (!ReferenceEquals(modelDrag, gesture) || gesture.Version != version) continue;
                if (!gesture.IsCompleted) return;
                modelDrag = null;
                if (previewDirty) await ApplyPreviewEditsAsync();
            }
        }
        finally { presentingModelDrag = false; }
    }

    async Task AwaitModelDragAsync()
    {
        Task pending;
        do { pending = modelDragTask; await pending; }
        while (!ReferenceEquals(pending, modelDragTask));
    }

    static (double X, double Y) DragCanvasScale(Live2DDocument current, int width, int height)
    {
        if (current.Canvas is not { } canvas) return (1, 1);
        // Match the native crop's aspect fit, including subpixel padding caused by
        // integer output dimensions; document transforms use reference-canvas units.
        double fit = Math.Min(width / (canvas.Width * canvas.ReferenceWidth),
            height / (canvas.Height * canvas.ReferenceHeight));
        return (width / (canvas.ReferenceWidth * fit), height / (canvas.ReferenceHeight * fit));
    }
}
