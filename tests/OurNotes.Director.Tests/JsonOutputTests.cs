using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class JsonOutputTests
{
    public static void Run(Action<bool, string> assert)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };
        var node = JsonNode.Parse("""{"OriginalKey":1e999,"text":"幸福\\n萤"}""")!;
        var value = JsonOutput.Object(
            ("Visible", true),
            ("nested", JsonOutput.Object(("MaxVisible", 4), ("nullable", null))),
            ("raw", node),
            ("parameters", new Dictionary<string, float> { ["ParamEyeOpen"] = .5f }),
            ("frameHashes", new Dictionary<int, string> { [7] = "value" }),
            ("items", Enumerable.Range(0, 2).Select(i => JsonOutput.Object(("Index", i)))));
        string json = JsonOutput.Serialize(value, options);
        var parsed = JsonNode.Parse(json)!;
        assert(parsed["visible"]!.GetValue<bool>() && parsed["nested"]!["maxVisible"]!.GetValue<int>() == 4,
            "AOT output preserves nested camelCase properties");
        assert(parsed["parameters"]!["ParamEyeOpen"]!.GetValue<float>() == .5f && parsed["frameHashes"]!["7"]!.ToString() == "value",
            "AOT output preserves identifier keys and integer dictionary keys");
        assert(parsed["items"]![1]!["index"]!.GetValue<int>() == 1 && json.Contains("1e999") && parsed["raw"]!["OriginalKey"] is not null,
            "AOT output evaluates lazy records and preserves raw archive JSON");
        assert(JsonSerializer.SerializeToNode(value)!["Visible"]!.GetValue<bool>(),
            "Snapshot records retain default property casing for managed callers");

        var shot = new Shot { Actors = [new() { Model = "test", Parameters = new() { ["ParamEyeOpen"] = .5f } }] };
        string temporary = Path.Combine(Path.GetTempPath(), "ournotes-json-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(temporary, JsonOutput.Serialize(shot, Shot.JsonOptions));
            var restored = Shot.Read(temporary);
            assert(restored.Actors.Single().Model == "test" && restored.Actors[0].Parameters["ParamEyeOpen"] == .5f,
                "Shot source-generated metadata round trip");
            var converted = ShotProject.Convert(temporary);
            assert(converted["format"]!.ToString() == DirectorProject.Format && converted["commands"]!.AsArray().Count > 0,
                "Legacy Shot input still adapts through source-generated JSON");
            var malformed = JsonNode.Parse(File.ReadAllText(temporary))!;
            malformed["unknownProperty"] = 1;
            File.WriteAllText(temporary, malformed.ToJsonString());
            bool rejected = false;
            try { Shot.Read(temporary); } catch (JsonException) { rejected = true; }
            assert(rejected, "Shot source generation retains unknown-property rejection");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        var program = new AdvProgram { Rows = [new("0", "Delay", new JsonObject { ["Duration"] = 1 })] };
        var plan = new DialoguePlan { AdvId = "1", Lines = [new() { Text = "中文", Voices = [new() { Asset = "assets/x.m4a", LogicalPath = "audio/x.m4a" }] }] };
        var documents = JsonOutput.SerializeToNode(JsonOutput.Object(("program", program), ("plan", plan)), options)!;
        assert(documents["program"]!["rows"]![0]!["command"]!.ToString() == "Delay" && documents["program"]!["scene"] is null,
            "Native program output includes rows and respects JsonIgnore");
        assert(documents["plan"]!["lines"]![0]!["text"]!.ToString() == "中文" && documents["plan"]!["lines"]![0]!["voices"]![0]!["packagePath"]!.ToString() == "voices/x.m4a",
            "Dialogue output keeps nested records and computed properties");
    }
}
