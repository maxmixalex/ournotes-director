using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// URP VolumeParameter interpolation and registration ordering, from engine/postfx.js and story/field.js.
public sealed class AdvVolume
{
    public JsonNode Profile{get;set;}
    public float Weight{get;set;}
    public AdvVolume(JsonNode profile,float weight=1){Profile=profile;Weight=weight;}
    static readonly JsonNode defaults=JsonNode.Parse("""
    {
      "Bloom":{"skipIterations":1,"threshold":0.9,"intensity":0,"scatter":0.7,"clamp":65472,"tint":{"r":1,"g":1,"b":1,"a":1},"highQualityFiltering":0,"filter":0,"downscale":0,"maxIterations":6,"dirtTexture":null,"dirtIntensity":0},
      "ChannelMixer":{"redOutRedIn":100,"redOutGreenIn":0,"redOutBlueIn":0,"greenOutRedIn":0,"greenOutGreenIn":100,"greenOutBlueIn":0,"blueOutRedIn":0,"blueOutGreenIn":0,"blueOutBlueIn":100},
      "ChromaticAberration":{"intensity":0},
      "ColorAdjustments":{"postExposure":0,"contrast":0,"colorFilter":{"r":1,"g":1,"b":1,"a":1},"hueShift":0,"saturation":0},
      "ColorCurves":{"master":null,"red":null,"green":null,"blue":null,"hueVsHue":null,"hueVsSat":null,"satVsSat":null,"lumVsSat":null},
      "DepthOfField":{"mode":0,"gaussianStart":10,"gaussianEnd":30,"gaussianMaxRadius":1,"highQualitySampling":0,"focusDistance":10,"aperture":5.6,"focalLength":50,"bladeCount":5,"bladeCurvature":1,"bladeRotation":0},
      "FilmGrain":{"type":0,"intensity":0,"response":0.8,"texture":null},
      "LensDistortion":{"intensity":0,"xMultiplier":1,"yMultiplier":1,"center":{"x":0.5,"y":0.5},"scale":1},
      "LiftGammaGain":{"lift":{"x":1,"y":1,"z":1,"w":0},"gamma":{"x":1,"y":1,"z":1,"w":0},"gain":{"x":1,"y":1,"z":1,"w":0}},
      "MotionBlur":{"mode":0,"quality":0,"intensity":0,"clamp":0.05},
      "ShadowsMidtonesHighlights":{"shadows":{"x":1,"y":1,"z":1,"w":0},"midtones":{"x":1,"y":1,"z":1,"w":0},"highlights":{"x":1,"y":1,"z":1,"w":0},"shadowsStart":0,"shadowsEnd":0.3,"highlightsStart":0.55,"highlightsEnd":1},
      "SplitToning":{"shadows":{"r":0.5,"g":0.5,"b":0.5,"a":1},"highlights":{"r":0.5,"g":0.5,"b":0.5,"a":1},"balance":0},
      "Tonemapping":{"mode":0,"neutralHDRRangeReductionMode":2,"acesPreset":3,"hueShiftAmount":0,"detectPaperWhite":0,"paperWhite":300,"detectBrightnessLimits":1,"minNits":0.005,"maxNits":1000},
      "Vignette":{"color":{"r":0,"g":0,"b":0,"a":1},"center":{"x":0.5,"y":0.5},"intensity":0,"smoothness":0.2,"rounded":0},
      "WhiteBalance":{"temperature":0,"tint":0},
      "AdvCurvedLens":{"center":{"x":0.5,"y":0.5},"intensity":0,"size":0.35,"softness":0.2,"horizontalRate":1,"verticalRate":0.15,"scale":1,"attenuateByCameraDistance":1,"reverse":1}
    }
    """)!;
    static readonly HashSet<string> discrete=["mode","quality","type","highQualityFiltering","filter","downscale","dirtTexture","highQualitySampling","texture","rounded","attenuateByCameraDistance","reverse","neutralHDRRangeReductionMode","acesPreset","detectPaperWhite","detectBrightnessLimits"];
    static readonly HashSet<string> integers=["skipIterations","maxIterations","bladeCount"];
    public static JsonNode Evaluate(IEnumerable<AdvVolume> volumes)
    {
        var result=defaults.DeepClone();
        foreach(var volume in volumes){float t=Math.Clamp(volume.Weight,0,1);if(t<=0)continue;
            foreach(var component in volume.Profile["components"]!.AsArray().OfType<JsonNode>()){
                if(!Actor.B(component["active"]))continue;string name=Archive.S(component,"asset");var target=result[name]??throw new NotSupportedException("Unsupported volume component: "+name);
                foreach(var pair in component.AsObject()){var parameter=pair.Value;if(parameter is not JsonObject||!Actor.B(parameter["m_OverrideState"]))continue;
                    if(!target.AsObject().ContainsKey(pair.Key))throw new NotSupportedException("Unknown volume parameter: "+name+"."+pair.Key);
                    var value=parameter["m_Value"];var old=target[pair.Key];
                    if(name=="ColorCurves"||discrete.Contains(pair.Key))target[pair.Key]=value?.DeepClone();
                    else if(value is JsonObject vector&&old is JsonObject original){foreach(var axis in vector)original[axis.Key]=Actor.N(original[axis.Key])+(Actor.N(axis.Value)-Actor.N(original[axis.Key]))*t;}
                    else{float v=Actor.N(old)+(Actor.N(value)-Actor.N(old))*t;target[pair.Key]=integers.Contains(pair.Key)?(float)(int)v:v;}
                }
            }
        }
        return result;
    }
}
