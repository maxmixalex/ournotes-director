namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    bool draftPending;
    bool DraftMustWait => playbackRequested || viewport.IsPlaying || modelDrag is { IsCompleted: false };

    void ScheduleDraft()
    {
        draftPending = true; draftTimer.Stop(); ResumeDraft();
    }
    void DeferDraft()
    {
        draftPending |= draftTimer.IsRunning; draftTimer.Stop();
    }
    void ResumeDraft()
    {
        if (draftPending && active && document is not null && !DraftMustWait && !draftTimer.IsRunning)
            draftTimer.Start();
    }
    void SaveDraftWhenIdle()
    {
        // Stop may race an already queued Tick. Keep the pending save until the
        // gesture completes or playback pauses, instead of serializing during input.
        if (DraftMustWait) { DeferDraft(); return; }
        SaveDraft();
    }
}
