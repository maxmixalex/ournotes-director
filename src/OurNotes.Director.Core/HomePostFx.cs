using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Preserves enabled HomeView volumes for the complete scene, using the original URP programs.</summary>
public sealed class HomePostFx : IDisposable
{
    readonly UnityShaderLibrary shaders;
    readonly AdvPostFx post;
    readonly JsonNode stack;
    public HomePostFx(OffscreenGl gl, HomeResources resources, JsonNode stack)
    {
        this.stack = stack;
        var story = JsonNode.Parse(resources.Read("story.json"))!;
        var scene = JsonNode.Parse(resources.Read(Archive.S(story,"scene")))!;
        string prefix = Archive.S(scene["shaders"] ?? new JsonObject(), "index", "shaders/shaders.json");
        int slash = prefix.LastIndexOf('/'); prefix = slash < 0 ? "" : prefix[..(slash + 1)];
        shaders = new(resources.Read, prefix);
        try
        {
            post = new(gl, shaders);
            _ = shaders.Program("Hidden/Universal Render Pipeline/LutBuilderLdr");
        }
        catch { shaders.Dispose(); throw; }
    }
    public void Render(int frame) => post.RenderColor(stack, frame);
    internal void ReleasePresentationTargets() => post.ReleasePresentationTargets();
    internal long PresentationTargetBytes => post.PresentationTargetBytes;
    internal int PresentationTargetCount => post.PresentationTargetCount;
    public void Dispose() => shaders.Dispose();

    public static JsonNode? Stack(JsonNode home)
    {
        if (!Actor.B(home["sceneRoot"]?["sceneCamera"]?["additionalCameraData"]?["m_RenderPostProcessing"])) return null;
        var volumes = (home["sceneRoot"]?["volumes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (home["volume"] is JsonObject placed) volumes.Add(placed);
        var profiles = new List<AdvVolume>();
        foreach (var volume in volumes.Where(v => Actor.B(v["active"]) && Actor.B(v["enabled"]) && Actor.N(v["layer"]) == 15)
                     .OrderBy(v => Actor.N(v["priority"])))
        {
            float weight = Actor.N(volume["weight"]);
            if (!float.IsFinite(weight)) throw new InvalidDataException("Non-finite home volume weight.");
            if (weight <= 0 || volume["profile"] is null) continue;
            if (!Actor.B(volume["isGlobal"])) throw new NotSupportedException("Local home volumes are unsupported.");
            var components = new JsonArray();
            foreach (var component in volume["components"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (!Actor.B(component["active"])) continue;
                var converted = new JsonObject { ["asset"] = Archive.S(component,"class"), ["active"] = true };
                foreach (var (key, value) in component)
                {
                    if (key is "class" or "active") continue;
                    if (key is "texture" or "dirtTexture") throw new NotSupportedException("Home volume textures are not included in the host metadata.");
                    converted[key] = new JsonObject { ["m_OverrideState"] = true, ["m_Value"] = value?.DeepClone() };
                }
                components.Add((JsonNode)converted);
            }
            if (components.Count > 0) profiles.Add(new(new JsonObject { ["components"] = components }, weight));
        }
        if (profiles.Count == 0) return null;
        var stack = AdvVolume.Evaluate(profiles);
        if (Actor.N(stack["DepthOfField"]?["mode"]) != 0 || Actor.N(stack["MotionBlur"]?["intensity"]) > 0
            || Actor.N(stack["AdvCurvedLens"]?["intensity"]) > 0 || Actor.N(stack["FilmGrain"]?["intensity"]) > 0)
            throw new NotSupportedException("Home camera depth effects, curved lens or film grain are unsupported.");
        return stack;
    }
}
