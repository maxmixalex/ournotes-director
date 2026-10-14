using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Separates the source player's viewport (and its opening wait) from the
/// output canvas. Removing visible letterbox bands must never advance the performance.</summary>
public static class ActorIsolation
{
    public static JsonObject Output(JsonObject? original, string target, int width, int height, int supersample = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        RenderDimensions.Validate(width, height, supersample);
        var output = original?.DeepClone().AsObject() ?? new JsonObject();
        output["playbackViewport"] ??= new JsonObject
        {
            ["width"] = original?["width"]?.DeepClone() ?? JsonValue.Create(1920),
            ["height"] = original?["height"]?.DeepClone() ?? JsonValue.Create(1080),
            ["letterbox"] = original?["letterbox"]?.DeepClone() ?? JsonValue.Create("game")
        };
        output["width"] = width; output["height"] = height; output["supersample"] = supersample;
        output["clean"] = true; output["letterbox"] = "none"; output["isolateActor"] = target;
        ValidateViewport(output["playbackViewport"]);
        return output;
    }

    public static void ValidateViewport(JsonNode? node)
    {
        if (node is null) return;
        if (node is not JsonObject value) throw new ArgumentException("output.playbackViewport must be an object");
        if (value.Any(pair => pair.Key is not ("width" or "height" or "letterbox")))
            throw new ArgumentException("Unknown playbackViewport field");
        foreach (string key in new[] { "width", "height" })
            if (value[key] is not JsonValue number || !number.TryGetValue<int>(out int dimension)
                || dimension < 16 || dimension > RenderDimensions.MaxSide)
                throw new ArgumentException("Invalid playbackViewport." + key);
        if (value["letterbox"]?.ToString() is not ("game" or "none"))
            throw new ArgumentException("Invalid playbackViewport.letterbox");
    }

    public static bool HasLetterbox(int width, int height, string? mode)
    {
        if (mode == "none") return false;
        float aspect = width > height ? 2.1666667f : 1.7777778f;
        return width / (float)height + .0001f < aspect && (height - width / aspect) / 2 > 1;
    }
}
