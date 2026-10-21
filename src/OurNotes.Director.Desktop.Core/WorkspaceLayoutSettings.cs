namespace OurNotes.Director.Desktop.Core;

/// <summary>Saved splitter proportions; each editor keeps its own arrangement.</summary>
public sealed record WorkspaceLayoutSettings(double TopLeft = .31, double BottomLeft = .28, double TopHeight = .61)
{
    public static WorkspaceLayoutSettings DefaultFor(string workspace) => workspace == "live2d" ? new(.72, .28, 1) : new();

    public WorkspaceLayoutSettings Normalize(string workspace)
    {
        var defaults = DefaultFor(workspace);
        static double Fraction(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, .16, .84) : fallback;
        return new(Fraction(TopLeft, defaults.TopLeft), Fraction(BottomLeft, defaults.BottomLeft),
            workspace == "live2d" ? 1 : Fraction(TopHeight, defaults.TopHeight));
    }
}
