using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// A project is an envelope around the original episode commands, not another player.
public sealed record DirectorProject(string Adv,string Site,string Language,JsonObject Output)
{
    public const string Format="ournotes-director/project";
    public static JsonNode Document(string path){var node=Archive.ReadJson(path);return node is JsonObject obj&&obj["format"] is null&&obj["actors"] is JsonArray?ShotProject.Convert(path):node;}
    public static DirectorProject Read(string path)
    {
        var root=Document(path) as JsonObject??throw new ArgumentException("Project must be an object");
        CheckKeys(root,["format","version","source","output","initialize","commands","finalize","text"],"project");
        if(Archive.S(root,"format")!=Format||Archive.S(root,"version")!="1")throw new ArgumentException("Project requires format ournotes-director/project and version 1");
        var source=root["source"] as JsonObject??throw new ArgumentException("Project source is required");
        CheckKeys(source,["adv","site","language"],"source");
        string adv=Archive.S(source,"adv");if(string.IsNullOrWhiteSpace(adv))throw new ArgumentException("Project source.adv is required");
        var output=root["output"] as JsonObject??new JsonObject();
        if(root["output"] is not null&&root["output"] is not JsonObject)throw new ArgumentException("Project output must be an object");
        CheckKeys(output,["width","height","supersample","codec","cineformQuality","clean","letterbox"],"output");
        foreach(string key in new[]{"width","height","supersample"})if(output[key] is {} value){
            if(!int.TryParse(value.ToString(),out int n)||n<(key=="supersample"?1:16)||n>(key=="supersample"?4:key=="width"?7680:4320))throw new ArgumentException("Invalid project output."+key);
        }
        if(output["clean"] is JsonValue clean&&!clean.TryGetValue<bool>(out _))throw new ArgumentException("Project output.clean must be boolean");
        if(output["clean"] is not null&&output["clean"] is not JsonValue)throw new ArgumentException("Project output.clean must be boolean");
        if(output["codec"] is not null)_=VideoEncoding.NormalizeCodec(Archive.S(output,"codec"));
        if(output["cineformQuality"] is not null)_=VideoEncoding.NormalizeCineFormQuality(Archive.S(output,"cineformQuality"));
        if(output["letterbox"] is not null&&Archive.S(output,"letterbox") is not ("game" or "none"))throw new ArgumentException("Project output.letterbox must be game or none");
        foreach(string section in new[]{"initialize","commands","finalize"}){
            if(root[section] is null)continue;
            if(root[section] is not JsonArray rows)throw new ArgumentException("Project "+section+" must be an array");
            for(int i=0;i<rows.Count;i++){
                var row=rows[i] as JsonObject??throw new ArgumentException($"Project {section}[{i}] must be an object");
                string cmd=Archive.S(row,"cmd");
                var command=AdvProgram.Row(row,$"{section}[{i}]");
                DirectorValidation.Check(command);
                if(!command.Ignored)_=AdvPlayback.FixedDelayFrames(command);
            }
        }
        if(root["text"] is not null&&root["text"] is not JsonObject)throw new ArgumentException("Project text must be an object");
        return new(adv,Archive.S(source,"site","intl"),Archive.S(source,"language","zh-Hans"),output);
    }
    static void CheckKeys(JsonObject value,string[] allowed,string path)
    {foreach(var pair in value)if(!allowed.Contains(pair.Key))throw new ArgumentException($"Unknown {path} field: {pair.Key}");}
}
