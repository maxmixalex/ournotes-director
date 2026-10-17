using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

/// <summary>Copy-on-write localized dialogue editing. Duplicated/imported lines may share
/// a text resource; editing one clip must not rewrite another character's line.</summary>
public static class CreativeDialogue
{
    public static string LanguageField(string language) => language switch
    {
        "zh-Hans" => "simplifiedChinese", "zh-Hant" => "traditionalChinese",
        "ja" => "japanese", "en" => "english", "ko" => "korean",
        _ => throw new ArgumentException("Unsupported dialogue language: " + language)
    };
    public static string Read(CreativeScene scene,string clipId)
    {
        var clip=scene.Clip(clipId);string id=clip["data"]?["AdvTextID"]?.ToString()??"";
        return scene.Root["text"]?[id]?[LanguageField(scene.Language)]?.ToString()
            ??scene.Root["text"]?[id]?[scene.Language]?.ToString()??"";
    }
    public static void Write(CreativeScene scene,string clipId,string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clip=scene.Clip(clipId);var data=clip["data"]!.AsObject();
        if(data["cmd"]?.ToString()!="Talk")throw new ArgumentException("该片段不是对白。");
        string field=LanguageField(scene.Language),original=data["AdvTextID"]?.ToString()??"";
        var table=scene.Root["text"] as JsonObject;
        if(table is null){table=new();scene.Root["text"]=table;}
        bool own=clip["dialogueTextId"]?.ToString()==original&&original.Length>0
            &&scene.Clips.OfType<JsonObject>().Count(c=>c["data"]?["AdvTextID"]?.ToString()==original)==1;
        string id=own?original:"editor-line-"+CreativeScene.Id();
        var value=table[original]?.DeepClone() as JsonObject??new();
        value[field]=text;table[id]=value;data["AdvTextID"]=id;clip["dialogueTextId"]=id;clip["label"]=text;
    }
}
