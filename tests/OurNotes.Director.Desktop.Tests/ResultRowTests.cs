using System.Text.Json.Nodes;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

public static class ResultRowTests
{
    public static void Run(Action<bool, string> check)
    {
        JsonNode Parse(string text) => JsonNode.Parse(text)!;
        JsonObject Envelope(JsonNode body) => new()
        {
            ["schema"] = "ournotes-director/1", ["ok"] = true,
            ["provenance"] = new JsonObject { ["site"] = "intl", ["archive"] = "test-archive" }, ["result"] = body.DeepClone()
        };
        var actionGroups = new List<IReadOnlyList<ResultAction>>();
        IReadOnlyList<ResultAction> Actions(ResultRow row, string command)
        {
            var actions = row.Actions(command); actionGroups.Add(actions); return actions;
        }
        var home = Parse("""
        {"spotId":40005,"advId":10868,"site":"jp","names":{"zh-Hans":"萤的主页","ja":"ほたるホーム"},
         "characters":[{"key":"hotaru","name":"Hotaru","parts":[{"path":"Hotaru","skeletonData":"body"}],"exportSupported":true},
                       {"key":"unsupported","name":"待确认角色","exportSupported":false,"unsupportedReason":"shared slots unknown"}],
         "sources":[{"site":"jp","spotId":40005,"advId":10868,"talk":false},{"site":"jp","spotId":40005,"advId":10869,"talk":true}]}
        """);
        string originalHome = home.ToJsonString();
        var characters = ResultRow.Read(Envelope(home), "home.show");
        check(characters.Count == 2 && characters[0].Id == "hotaru", "Results: home show reads character keys rather than source IDs");
        check(characters[0].Context["spotId"] == "40005" && characters[0].Context["site"] == "jp", "Results: characters inherit actual homepage and site, overriding envelope fallback");
        check(characters[0].Context["adv"] == "10868", "Results: homepage source version is retained");
        var selectedCharacter = characters[0].Selection("character");
        check(selectedCharacter.Value == "hotaru" && selectedCharacter.RelatedValues["spotId"] == "40005"
            && selectedCharacter.RelatedValues["site"] == "jp" && selectedCharacter.RelatedValues["adv"] == "10868", "Results: choosing a character preserves scene identity");
        var characterActions = Actions(characters[0], "home.show");
        check(characterActions.Single(action => action.CommandId == "home.export").Values["character"] == "hotaru", "Results: character export shortcut fills the selected character");
        check(characterActions.All(action => action.Values["spotId"] == "40005" && action.Values["site"] == "jp" && action.Values["adv"] == "10868"), "Results: all homepage shortcuts retain source and site");
        check(!characterActions.Single(action => action.CommandId == "home.show").Values.ContainsKey("character"), "Results: show shortcut does not send an unsupported character option");
        var unsupportedActions = Actions(characters[1], "home.show");
        check(unsupportedActions.All(action => action.CommandId != "home.export") && unsupportedActions.Any(action => action.CommandId == "home.clip" && action.Values["character"] == ""),
            "Results: unsupported character exposes complete scene export without promising separation");
        var homeSources = ResultRow.ForPicker(Envelope(home), "home.show", "adv");
        check(homeSources.Select(row => row.Id).SequenceEqual(new[] { "10868", "10869" }), "Results: home source picker only lists that spot's sources");
        var chosenSource = homeSources[1].Selection("adv");
        check(chosenSource.Value == "10869" && chosenSource.RelatedValues["site"] == "jp" && chosenSource.RelatedValues["spotId"] == "40005"
            && !chosenSource.RelatedValues.ContainsKey("adv"), "Results: chosen home source does not overwrite itself with the prior source");
        check(CommandCatalog.Get("home.clip").Fields.Single(field => field.Key == "adv").LookupCommandId == "home.show", "Results: source selector opens the home source catalog");

        var homeList = ResultRow.Read(new JsonObject { ["items"] = new JsonArray(home.DeepClone()) }, "home.list");
        check(homeList.Count == 1 && homeList[0].Id == "40005" && homeList[0].Title == "萤的主页", "Results: homepage list uses names locale map");
        check(homeList[0].SearchText.Contains("ほたるホーム", StringComparison.Ordinal), "Results: filtering can find another localized homepage name");
        var homeListActions = Actions(homeList[0], "home.list");
        check(homeListActions.Single(action => action.CommandId == "home.export").Values["character"] == "", "Results: scene export clears a stale character filter");
        var preset = new ResultRow("all", "全部主页", "快捷选择", JsonValue.Create("all")!) { Kind = "choice" };
        check(preset.Selection("spotId").Value == "all" && preset.Selection("spotId").RelatedValues["adv"] == "", "Results: clickable all-home option clears a single-home source override");
        check(new ResultRow("all", "全部角色", "快捷选择", JsonValue.Create("all")!) { Kind = "choice" }.Selection("character").Value == "all", "Results: clickable all-character option returns the real CLI value");

        var animations = Parse("""
        {"site":"jp","spotId":10001,"advId":10611,"character":"rana","sourceDuration":4,
         "parts":[{"path":"Rana/body","skeleton":"body","animation":"home_start","duration":4,"animations":[{"name":"home_start","duration":4},{"name":"wave","duration":2}]},
                  {"path":"Rana/shadow","skeleton":"shadow","animation":"home_start","duration":4,"animations":[{"name":"home_start","duration":4},{"name":"shadow_only","duration":1}]}]}
        """);
        var names = ResultRow.ForPicker(Envelope(animations), "home.animations", "animation");
        check(names.Count == 1 && names[0].Id == "home_start", "Results: animation picker intersects actual parts[].animations names");
        check(names[0].Selection("animation").Value == "home_start" && names[0].Selection("animation").RelatedValues["character"] == "rana",
            "Results: animation selection carries the chosen character");
        check(names[0].Selection("animation").RelatedValues["spotId"] == "10001" && names[0].Selection("animation").RelatedValues["adv"] == "10611",
            "Results: animation selection keeps exact spot and source");
        check(Actions(names[0], "home.animations").Single(action => action.CommandId == "home.contact").Values["animation"] == "home_start", "Results: animation shortcut selects an animation name, not a skeleton path");
        var partRows = ResultRow.Read(Envelope(animations), "home.animations");
        check(partRows.Count == 2 && partRows[0].Title == "body" && partRows[0].Subtitle.Contains("home_start", StringComparison.Ordinal), "Results: animation details display skeleton and current animation");
        check(Actions(partRows[0], "home.animations").Single(action => action.CommandId == "home.export").Values["animation"] == "", "Results: choosing a skeleton never invents a scene-wide animation override");
        var noCommon = animations.DeepClone(); noCommon["parts"]![1]!["animations"] = new JsonArray(new JsonObject { ["name"] = "different" });
        check(ResultRow.ForPicker(noCommon, "home.animations", "animation").Count == 0, "Results: incompatible skeleton animation sets do not offer a false override");

        var voices = Parse("""
        {"total":1,"items":[{"site":"jp","advId":10060,"commandIndex":21,"speakerTarget":"Hotaru",
          "titles":{"zh-Hans":"剧情标题"},"text":{"zh-Hans":"我们一起开始吧","ja":"一緒に始めよう"},
          "voices":[{"soundId":1234567890123456789,"logicalPath":"voice/a.m4a","asset":"a.m4a","cue":"voice_a"},
                    {"soundId":1234567890123456790,"logicalPath":"voice/b.m4a","asset":"b.m4a","cue":"voice_b"}]}]}
        """);
        var voiceRows = ResultRow.Read(Envelope(voices), "voice.search");
        check(voiceRows.Single().Title == "我们一起开始吧", "Results: voice search shows matching dialogue instead of replacing it with story title");
        check(voiceRows[0].SearchText.Contains("一緒に始めよう", StringComparison.Ordinal), "Results: voice filtering includes all returned translations");
        var extractedVoices = ResultRow.ForPicker(Envelope(voices), "voice.search", "voiceId");
        check(extractedVoices.Count == 2 && extractedVoices[0].Id == "1234567890123456789" && extractedVoices[1].Id == "1234567890123456790",
            "Results: every voice is selectable without rounding large source identifiers");
        check(extractedVoices[1].Selection("voiceId").Value == "1234567890123456790" && extractedVoices[1].Selection("voiceId").RelatedValues["adv"] == "10060",
            "Results: choosing the second voice does not silently choose the first");
        check(extractedVoices[1].Selection("soundId").RelatedValues["site"] == "jp", "Results: sound probe selection carries voice site");
        check(Actions(voiceRows[0], "voice.search").Count(action => action.CommandId == "voice.extract") == 2, "Results: dialogue actions expose every linked voice");
        check(Actions(extractedVoices[1], "voice.search").Single(action => action.CommandId == "voice.extract").Values["voiceId"] == "1234567890123456790",
            "Results: flattened voice action identifies exactly the selected asset");
        check(ResultRow.ForPicker(voices, "voice.search", "voiceId")[0].Data.ToJsonString().Contains("1234567890123456789", StringComparison.Ordinal), "Results: copied voice JSON retains original numeric identity");

        var expressionPage = Parse("""{"items":[{"id":"smile","model":"hotaru_normal"}]}""");
        var expressionRows = ResultRow.Read(Envelope(expressionPage), "expression.list");
        var expression = Actions(expressionRows.Single(), "expression.list").Single(action => action.CommandId == "expression.show");
        check(expression.Values["modelId"] == "hotaru_normal" && expression.Values["expressionId"] == "smile", "Results: expression detail receives both model and expression IDs");
        check(expressionRows[0].Selection("expressionId").RelatedValues["modelId"] == "hotaru_normal", "Results: expression picker preserves its model");
        var model = Parse("""{"id":"hotaru_normal","key":"different-resource-key","motions":[{"id":"idle","duration":3}],"expressions":["smile","sad"]}""");
        var modelRows = ResultRow.Read(Envelope(model), "model.show");
        check(modelRows.Count == 4 && modelRows[0].Id == "hotaru_normal", "Results: model detail exposes model, motions and expressions");
        var nestedExpression = Actions(modelRows.Single(row => row.Kind == "expression" && row.Id == "smile"), "model.show").Single(action => action.CommandId == "expression.show");
        check(nestedExpression.Values["modelId"] == "hotaru_normal", "Results: nested expression name never replaces its parent model ID");
        check(Actions(modelRows.Single(row => row.Kind == "motion"), "model.show").Single(action => action.CommandId == "model.gallery").Values["modelId"] == "hotaru_normal", "Results: nested motion gallery retains parent model");
        var parameterRows = ResultRow.Read(Parse("""{"model":"hotaru_normal","id":"smile","parameters":[{"id":"ParamMouthOpenY","name":"嘴部开合","value":1}]}"""), "expression.show");
        check(parameterRows.Single().Id == "ParamMouthOpenY" && parameterRows[0].Kind == "parameter" && parameterRows[0].Context["modelId"] == "hotaru_normal", "Results: expression parameter rows are not mistaken for expression IDs");
        var scalarExpressions = ResultRow.Read(new JsonArray("smile"), "expression.list", new Dictionary<string, string?> { ["modelId"] = "context_model", ["site"] = "jp" });
        check(Actions(scalarExpressions[0], "expression.list").Single(action => action.CommandId == "expression.show").Values["modelId"] == "context_model", "Results: scalar arrays use supplied command context");

        var assetPage = Parse("""
        {"items":[{"site":"jp","advId":10060,"scope":"model","modelId":"hotaru_normal","language":null,"logicalPath":"hotaru.moc3","kind":"moc3"},
                  {"site":"intl","advId":10061,"scope":"story","modelId":null,"language":"zh-Hant","logicalPath":"episode.json","kind":"json"}]}
        """);
        var assetRows = ResultRow.Read(Envelope(assetPage), "asset.search");
        check(assetRows[0].Id == "hotaru.moc3", "Results: asset row identity is its logical path rather than a source story ID");
        var modelAsset = assetRows[0].Selection("logicalPath");
        check(modelAsset.RelatedValues["model"] == "hotaru_normal" && modelAsset.RelatedValues["adv"] == "", "Results: model-scoped asset selects the model despite also containing advId");
        check(modelAsset.RelatedValues["lang"] == "", "Results: selecting a common file clears a stale language override");
        var storyAsset = assetRows[1].Selection("logicalPath");
        check(storyAsset.RelatedValues["adv"] == "10061" && storyAsset.RelatedValues["model"] == "" && storyAsset.RelatedValues["lang"] == "zh-Hant", "Results: localized story asset selects source and clears stale model");
        var assetActions = Actions(assetRows[0], "asset.search");
        check(assetActions.Single(action => action.CommandId == "asset.extract").Values["model"] == "hotaru_normal", "Results: asset extract shortcut uses the correct manifest");
        check(assetActions.All(action => action.CommandId != "asset.show") && Actions(assetRows[1], "asset.search").Any(action => action.CommandId == "asset.show"), "Results: JSON display is only offered for JSON assets");

        var commands = ResultRow.Read(Parse("""{"items":[{"i":23,"cmd":"Talk","AdvTextID":55}]}"""), "story.commands", new Dictionary<string, string?> { ["advId"] = "10060", ["site"] = "jp" });
        check(commands[0].Id == "23" && commands[0].Title == "Talk", "Results: raw command rows display instruction identity");
        check(Actions(commands[0], "story.commands").Single(action => action.CommandId == "story.show").Values["advId"] == "10060", "Results: instruction index is never used as a story ID");
        check(ResultRow.Read(Parse("""{"items":[{"i":23,"cmd":"Talk"}]}"""), "story.commands")[0].Actions("story.commands").Count == 0, "Results: absent story context does not invent a source");
        var resource = ResultRow.Read(Parse("""{"resource":{"id":"room_01","name":"教室"},"definition":{}}"""), "stage.show", new Dictionary<string, string?> { ["site"] = "jp" });
        check(Actions(resource[0], "stage.show").Single().Values["resourceId"] == "room_01", "Results: static resource details use resourceId selector");
        check(ResultRow.Read(new JsonArray("room_01"), "stage.list").Single().Actions("stage.list").Single().Values["resourceId"] == "room_01", "Results: scalar resource listings have working detail shortcuts");
        check(ResultRow.Read(Parse("""{"exports":[{"output":"characters/hotaru.mov"}]}"""), "home.export")[0].Id == "characters/hotaru.mov", "Results: batch export rows retain relative output paths");

        foreach (var actions in actionGroups)
            foreach (var action in actions)
            {
                var definition = CommandCatalog.Get(action.CommandId);
                check(action.Values.Keys.All(key => definition.Fields.Any(field => field.Key == key)), "Results: shortcut only supplies accepted fields for " + action.CommandId);
                check(definition.Fields.Where(field => field.Required && field.Key != "out").All(field => action.Values.TryGetValue(field.Key, out string? value) && !string.IsNullOrWhiteSpace(value)),
                    "Results: shortcut supplies required source selectors for " + action.CommandId);
            }
        check(home.ToJsonString() == originalHome, "Results: reading and selecting do not mutate source JSON");
        characters[0].Data["name"] = "modified copy";
        check(home.ToJsonString() == originalHome, "Results: returned rows own their JSON copies");
        check(ResultRow.Read(null, "home.list").Count == 0 && ResultRow.Read(Parse("""{"ok":false,"error":{"message":"missing"}}"""), "home.list").Count == 0,
            "Results: missing and error results have no fabricated selectable items");
    }
}
