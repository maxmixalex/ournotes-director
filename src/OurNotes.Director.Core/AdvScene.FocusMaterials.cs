namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    void PrepareFocusMaterials(IEnumerable<AdvRow> rows)
    {
        if (TimingOnly || !rows.Any(row => !row.Ignored && row.Command is "DoF" or "Focus" or "Zoom")) return;
        // A focus transition can start at zero blur, so frame zero does not use
        // either lens-blur pass. Prepare the exact game shaders before editing,
        // including drivers which defer native compilation until the first draw.
        // A private tiny FBO keeps all scene pixels, captures and timeline state
        // untouched. This does not advance or replace any animation evaluator.
        using var warmup = gl.CreateDrawingCanvas(16, 16);
        using (gl.UseDrawingCanvas(warmup))
        {
            gl.Begin([0, 0, 0, 0]);
            gl.Blur(shaders, 1, blurRadius);
            _ = gl.Read(16, 16);
        }
    }
}
