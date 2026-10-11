using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>The archived Spot scene's unlit ambient environment for URP/Lit room meshes.</summary>
public sealed class HomeLighting
{
    public string[] Keywords { get; }
    public Dictionary<string, float[]> Values { get; } = new()
    {
        ["_MainLightPosition"] = [0,0,1,0], ["_MainLightColor"] = [0,0,0,1], ["_AdditionalLightsCount"] = [0,0,0,0],
        ["_AdditionalLightsPosition"] = new float[64], ["_AdditionalLightsColor"] = new float[64],
        ["_AdditionalLightsAttenuation"] = new float[64], ["_AdditionalLightsSpotDir"] = new float[64],
        ["unity_LightData"] = [0,0,1,0], ["unity_LightIndices"] = new float[8], ["unity_SpecCube0_HDR"] = [1,1,0,0],
        ["unity_SHAr"] = [0,0,0,1], ["unity_SHAg"] = [0,0,0,1], ["unity_SHAb"] = [0,0,0,1],
        ["unity_SHBr"] = [0,0,0,0], ["unity_SHBg"] = [0,0,0,0], ["unity_SHBb"] = [0,0,0,0], ["unity_SHC"] = [0,0,0,0],
    };

    public HomeLighting(JsonNode home, Func<string,byte[]> read)
    {
        var root = home["sceneRoot"]!;
        var rs = root["renderSettings"] ?? throw new InvalidDataException("Home Lit material requires the original render settings.");
        var lights = (root["lights"] as JsonArray ?? []).Concat(home["lights"] as JsonArray ?? []);
        if (lights.Any(l => Actor.B(l?["active"]) && Actor.B(l?["m_Enabled"]))) throw new NotSupportedException("Home Lit scene uses direct lights or reflection probes.");
        if (Actor.B(rs["m_Fog"]) || rs["m_Sun"] is not null || Actor.N(rs["lightmaps"]) != 0) throw new NotSupportedException("Home Lit fog, sun or lightmaps are not supported.");
        if (Actor.N(rs["m_AmbientMode"]) != 3 || Actor.N(rs["m_AmbientIntensity"]) != 1
            || new[] { "r", "g", "b" }.Any(k => Actor.N(rs["m_AmbientSkyColor"]?[k]) != 1)) throw new NotSupportedException("Home Lit rendering requires the archived white Flat ambient.");
        if (rs["m_SkyboxMaterial"] is not null || rs["m_CustomReflection"] is not null || Actor.N(rs["m_DefaultReflectionMode"]) != 0)
            throw new NotSupportedException("Home Lit reflection maps are not supported.");
        var story = JsonNode.Parse(read("story.json"))!;
        var graphics = JsonNode.Parse(read(Archive.S(story,"scene")) )?["player"] ?? throw new InvalidDataException("Home Lit pipeline settings are absent.");
        string pipeName = Archive.S(graphics,"defaultPipeline");
        var pipe = graphics["pipelines"]?[pipeName] ?? throw new InvalidDataException("Home Lit pipeline asset is absent: " + pipeName);
        int index = (int)Actor.N(root["sceneCamera"]?["additionalCameraData"]?["m_RendererIndex"], -1);
        if (index < 0) index = (int)Actor.N(pipe["m_DefaultRendererIndex"]);
        var list = pipe["m_RendererDataList"] as JsonArray;
        if (list is null || index >= list.Count) throw new InvalidDataException("Home Lit renderer index is out of range.");
        var renderer = graphics["renderers"]?[list[index]!.ToString()] ?? throw new InvalidDataException("Home Lit renderer settings are absent.");
        if (Actor.N(renderer["m_RenderingMode"]) != 0) throw new NotSupportedException("Home Lit requires the archived Forward renderer.");
        foreach (var feature in renderer["m_RendererFeatures"]?.AsArray().OfType<JsonObject>() ?? [])
            if (Actor.B(feature["m_Active"]) && Archive.S(feature,"class").Split('.').Last() is "ScreenSpaceAmbientOcclusion" or "DecalRendererFeature" or "ScreenSpaceShadows")
                throw new NotSupportedException("Home Lit renderer feature: " + Archive.S(feature,"class"));
        if (Actor.B(pipe["m_MainLightShadowsSupported"]) || Actor.B(pipe["m_AdditionalLightShadowsSupported"]) || Actor.B(pipe["m_SupportsLightLayers"]))
            throw new NotSupportedException("Home Lit shadows or light layers are not supported.");
        var keywords = new List<string>();
        int sh = (int)Actor.N(pipe["m_ShEvalMode"]);
        if (sh is 0 or 1) keywords.Add("EVALUATE_SH_VERTEX");
        else if (sh == 2) keywords.Add("EVALUATE_SH_MIXED");
        else if (sh != 3) throw new NotSupportedException("Home Lit SH evaluation mode: " + sh);
        int additional = (int)Actor.N(pipe["m_AdditionalLightsRenderingMode"]);
        if (additional == 1) keywords.Add("_ADDITIONAL_LIGHTS");
        else if (additional == 2) keywords.Add("_ADDITIONAL_LIGHTS_VERTEX");
        else if (additional != 0) throw new NotSupportedException("Home Lit additional lights mode: " + additional);
        if (Actor.B(pipe["m_ReflectionProbeBlending"])) keywords.Add("_REFLECTION_PROBE_BLENDING");
        if (Actor.B(pipe["m_ReflectionProbeBoxProjection"])) keywords.Add("_REFLECTION_PROBE_BOX_PROJECTION");
        Keywords = keywords.ToArray();
    }
}
