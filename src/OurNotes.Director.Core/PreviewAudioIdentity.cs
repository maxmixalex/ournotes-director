using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Invalidates audio only when its content or timing changes. This does not replace
/// the interpreter's audio semantics or introduce a second, incompatible mix path.</summary>
public static class PreviewAudioIdentity
{
    static readonly HashSet<string> SilentCommands=new(StringComparer.Ordinal){"Character","Motion","MotionLoop","Expression","Delay","Pause","Resume","EyeBlink","In","Out","Costume"};
    static IEnumerable<JsonObject> Rows(JsonNode document)=>new[]{"initialize","commands","finalize"}
        .SelectMany(section=>(document[section] as JsonArray??new JsonArray()).OfType<JsonObject>())
        .Where(row=>!AdvProgram.Row(row,"audio").Ignored);
    static bool Scheduled(JsonNode document)=>document["initialize"] is JsonArray{Count:0}&&document["finalize"] is JsonArray{Count:0}
        &&document["commands"] is JsonArray commands&&commands.OfType<JsonObject>().All(row=>row["AtFrame"] is not null);
    static bool Silent(JsonObject row)=>SilentCommands.Contains(Archive.S(row,"cmd"))||Archive.S(row,"cmd").StartsWith("Director.",StringComparison.Ordinal);
    public static bool HasAudio(ScenePreviewSource source)
    {
        var document=DirectorProject.Document(source.ProjectPath);
        return !Scheduled(document)||Rows(document).Any(row=>!Silent(row));
    }
    public static string Key(ScenePreviewSource source)
    {
        var document=DirectorProject.Document(source.ProjectPath);
        JsonNode audio=Scheduled(document)?new JsonArray(Rows(document).Where(row=>!Silent(row)).Select(row=>row.DeepClone()).ToArray()):document;
        string baseDirectory=Path.GetDirectoryName(Path.GetFullPath(source.ProjectPath))!;
        var files=Rows(document).Where(row=>row["AudioPath"] is not null).Select(row=>
        {
            string path=Path.GetFullPath(row["AudioPath"]!.ToString(),baseDirectory);var info=new FileInfo(path);
            return path+"/"+info.Length+"/"+info.LastWriteTimeUtc.Ticks;
        });
        string key=source.ArchivePath+"\0"+source.FfmpegPath+"\0"+source.Width+"x"+source.Height+"\0"+source.DurationMode+"\0"+source.Duration.ToString("R",CultureInfo.InvariantCulture)
            +"\0"+document["source"]?.ToJsonString()+"\0"+document["output"]?["letterbox"]+"\0"+document["output"]?["playbackViewport"]?.ToJsonString()
            +"\0"+audio.ToJsonString()+"\0"+string.Join("\n",files);
        return Archive.Hash(Encoding.UTF8.GetBytes(key));
    }
}
