namespace OurNotes.Director.Core;

/// <summary>One real Cubism parameter, including the value used by the displayed native geometry.</summary>
public sealed record PreviewParameter(string Id, string Name, double Minimum, double Maximum,
    double Default, double Value, bool IsOverridden);

public sealed unsafe partial class Actor
{
    string[]? previewParameterNames;

    /// <summary>Must be called by the renderer's owner thread. Values is the next
    /// animation evaluation for game actors; Cubism's current buffer is the rendered pose.</summary>
    public IReadOnlyList<PreviewParameter> PreviewParameters()
    {
        previewParameterNames ??= Core.ParameterIds.Select(ParameterLabel).ToArray();
        var displayed = Core.Floats("ParameterValues", Core.ParameterIds.Length);
        return Core.ParameterIds.Select((id, index) => new PreviewParameter(id,
            previewParameterNames[index], Core.Min[index], Core.Max[index], Core.Defaults[index],
            displayed[index], DirectorParameters.ContainsKey(id) || Spec.Parameters.ContainsKey(id))).ToArray();
    }
}
