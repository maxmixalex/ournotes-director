using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>A fixed window in the original isolated output canvas. Coordinates are
/// normalized to that reference canvas and may extend beyond its original edges.</summary>
public sealed record IsolatedActorCanvas(int ReferenceWidth, int ReferenceHeight,
    double X, double Y, double Width, double Height);

public static partial class ActorIsolation
{
    public static JsonObject CanvasJson(IsolatedActorCanvas canvas) => new()
    {
        ["referenceWidth"] = canvas.ReferenceWidth, ["referenceHeight"] = canvas.ReferenceHeight,
        ["x"] = canvas.X, ["y"] = canvas.Y, ["width"] = canvas.Width, ["height"] = canvas.Height
    };

    public static IsolatedActorCanvas? ReadCanvas(JsonNode? node, string? actor)
    {
        if (node is null) return null;
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("output.isolateCanvas requires isolateActor");
        if (node is not JsonObject value) throw new ArgumentException("output.isolateCanvas must be an object");
        if (value.Any(pair => pair.Key is not ("referenceWidth" or "referenceHeight" or "x" or "y" or "width" or "height")))
            throw new ArgumentException("Unknown isolateCanvas field");
        int Dimension(string key)
        {
            if (value[key] is not JsonValue number || !number.TryGetValue<int>(out int result))
                throw new ArgumentException("Invalid output.isolateCanvas." + key);
            return result;
        }
        double Read(string key, bool positive = false)
        {
            if (value[key] is not JsonValue number || number.GetValueKind() != JsonValueKind.Number
                || !(number.TryGetValue<double>(out double result)
                    || double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                || !double.IsFinite(result) || Math.Abs(result) > 1_000_000 || positive && result <= 0)
                throw new ArgumentException("Invalid output.isolateCanvas." + key);
            return result;
        }
        int width = Dimension("referenceWidth"), height = Dimension("referenceHeight");
        RenderDimensions.Validate(width, height, 1);
        return new(width, height, Read("x"), Read("y"), Read("width", true), Read("height", true));
    }
}
