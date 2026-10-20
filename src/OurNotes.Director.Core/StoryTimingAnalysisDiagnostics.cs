using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Measured work from a completed native timing analysis. Decoder time
/// is cumulative across workers and overlaps preparation/playback wall time.</summary>
public sealed record StoryTimingAnalysisDiagnostics(double PreparationMilliseconds,
    double PlaybackMilliseconds,double CleanupMilliseconds,IReadOnlyDictionary<string,double> Initialization,
    int LoadedModels,int LoadedModelTextures,int CaptureWaits,int CaptureDraws,
    long CueDecodeAttempts,long DecodedPcmBytes,double CumulativeCueDecodeMilliseconds,long RetainedPcmBytes)
{
    public JsonObject ToJson()
    {
        var initialization=new JsonObject();
        foreach(var(name,value)in Initialization)initialization[name]=value;
        return new()
        {
            ["preparationMs"]=PreparationMilliseconds,["playbackMs"]=PlaybackMilliseconds,["cleanupMs"]=CleanupMilliseconds,
            ["initialization"]=initialization,["loadedModels"]=LoadedModels,["loadedModelTextures"]=LoadedModelTextures,
            ["captureWaits"]=CaptureWaits,["captureDraws"]=CaptureDraws,["cueDecodeAttempts"]=CueDecodeAttempts,["decodedPcmBytes"]=DecodedPcmBytes,
            ["cumulativeCueDecodeMs"]=CumulativeCueDecodeMilliseconds,["retainedPcmBytes"]=RetainedPcmBytes
        };
    }
}
