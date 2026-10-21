namespace OurNotes.Director.Desktop.Core;

/// <summary>Budget the expensive scene pass while the pointer is held. This is not
/// an export setting; a settled seek always restores the reference drawing path.</summary>
public static class SceneInteractionPreview
{
    public static double Scale(bool scrubbing, string device, double sceneMilliseconds, double previousScale = 1)
    {
        if (!scrubbing) return 1;
        ArgumentNullException.ThrowIfNull(device);
        if (device.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase)
            || device.Contains("softpipe", StringComparison.OrdinalIgnoreCase)
            || device.Contains("GDI Generic", StringComparison.OrdinalIgnoreCase)
            || device.Contains("Software Rasterizer", StringComparison.OrdinalIgnoreCase)) return .25;
        if (previousScale is not (1 or .5 or .25)) previousScale = 1;
        // Do not oscillate between FBO sizes inside a gesture. The next settled
        // frame restores full quality and supplies a new full-resolution baseline.
        double wanted = double.IsFinite(sceneMilliseconds) && sceneMilliseconds > 80 ? .25
            : double.IsFinite(sceneMilliseconds) && sceneMilliseconds > 33 ? .5 : 1;
        return Math.Min(previousScale, wanted);
    }
}
