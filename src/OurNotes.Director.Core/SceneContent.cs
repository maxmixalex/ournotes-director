using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Output-layer selection. It is a drawing option, never a command that resets
/// the original stage (and therefore its camera, actor slots, timing or light setup).</summary>
public static class SceneContent
{
    public static string Read(JsonNode? value)
    {
        if(value is null)return "scene";
        if(value is JsonValue json && json.TryGetValue<string>(out var content) && content is "scene" or "characters")return content;
        throw new ArgumentException("画面内容必须是 scene（完整场景）或 characters（仅人物透明）。");
    }
}
