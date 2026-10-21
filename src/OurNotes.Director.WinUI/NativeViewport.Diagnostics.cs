using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    internal sealed record PreviewOperation(string Kind, long RenderedSequence, long PresentedSequence,
        long SessionId, int Frame, int Width, int Height, int Supersample, int EditRows, bool HasTransform);
    internal event Action<PreviewOperation>? PreviewOperationObserved;

    // No diagnostic objects are allocated during normal editing. Smoke traces subscribe
    // only around the measured interaction to distinguish redraw from resize/replay/I/O.
    internal void RecordPreviewOperation(string kind, int editRows = 0, bool hasTransform = false,
        int? width = null, int? height = null, int? supersample = null)
    {
        if (PreviewOperationObserved is not { } observe) return;
        var rendered = session.LastFrame; var presented = LastFrame;
        observe(new(kind, rendered?.Sequence ?? 0, presented?.Sequence ?? 0,
            rendered?.SessionId ?? 0, rendered?.Frame ?? 0,
            width ?? rendered?.Pixels.Width ?? 0, height ?? rendered?.Pixels.Height ?? 0,
            supersample ?? PreviewSupersample, editRows, hasTransform));
    }
}
