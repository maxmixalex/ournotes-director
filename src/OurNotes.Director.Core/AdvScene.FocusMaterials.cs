namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    void PrepareFocusMaterials(IEnumerable<AdvRow> rows)
    {
        if (TimingOnly || !rows.Any(row => !row.Ignored && row.Command is "DoF" or "Focus" or "Zoom")) return;
        // Frame zero may precede the first nonzero focus transition. Preparing
        // its drawing paths must not advance the native player or scene state.
        gl.PrepareFocusMaterials(shaders, blurRadius);
    }
}
