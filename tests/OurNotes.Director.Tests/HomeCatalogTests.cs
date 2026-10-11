using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

internal static class HomeCatalogTests
{
    public static void Run(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "ournotes-home-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "index"));
        File.WriteAllText(Path.Combine(root, "index", "stats.json"), "{}");
        try
        {
            var spot = JsonNode.Parse("""
                {"spotId":40005,"master":{"_id":40005,"_bandId":4},"name":{"simplifiedChinese":"测试主页"},
                 "characters":[{"path":"home/Hotaru","_characterId":16},
                   {"path":"home/Natsume_bottom","_characterId":17},{"path":"home/Natsume","_characterId":17},
                   {"path":"home/Nagi_back","_characterId":18},{"path":"home/Houka_front","_characterId":20},
                   {"path":"home/Houka _bottom","_characterId":20},{"path":"home/Kanata","_characterId":6}],
                 "spineCharacters":[],"skeletons":[]}
                """)!.AsObject();
            var partNames = new[] { "Hotaru", "Natsume_bottom", "Natsume", "Nagi_back", "Nagi_over", "Nagi_back_2",
                "Houka_front", "Houka_back", "Houka_L_arm", "Houka _bottom", "Kanata", "Bg", "Chair", "Nagina" };
            foreach (var name in partNames) spot["spineCharacters"]!.AsArray().Add(Part("home/" + name));
            // A blank reference is retained in source metadata but is not a renderable part.
            spot["spineCharacters"]!.AsArray().Add(new JsonObject { ["path"] = "home/Placeholder", ["skeletonData"] = null, ["animation"] = null, ["world"] = null });
            var intl = new FixtureSite(root, "intl");
            var jp = new FixtureSite(root, "jp");
            intl.Story(42, spot, "tap", descriptor: false);
            intl.Story(43, spot, "area");
            jp.Story(44, spot, "area");
            // Old archives need neither a home index nor spot groups in their story indexes.
            intl.Index(43, 42); jp.Index(44);
            var filesBefore = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            var archive = new Archive(root);
            var catalog = new HomeCatalog(archive);
            var listed = catalog.List();
            assert(listed.Count == 2 && catalog.List("intl").Count == 1, "home catalog deduplicates by site and spot without conflating regions");
            var home = catalog.Open(40005);
            assert(home.Site == "intl" && home.AdvId == 43 && home.Sources.Count == 2, "home catalog prefers the international area source and preserves every source");
            assert(catalog.Open(40005, "intl", 42).AdvId == 42, "home catalog allows explicit selection of a tap source from an old manifest");
            assert(home.Characters.Count == 5 && home.SpineParts.Count == partNames.Length, "home characters include each person once and all renderable skeleton parts remain available");
            assert(home.SelectCharacter("natsume").Parts.Count == 2 && home.SelectCharacter("houka").Parts.Count == 4,
                "home character export merges duplicate tap targets and all upper/lower, back and limb skeletons");
            assert(home.IncludedSlots(home.SelectCharacter("natsume"), home.SelectCharacter("natsume").Parts[0]) is null,
                "exclusive character skeletons need no slot filtering or extra skeleton reads");
            assert(home.SelectCharacter("nagi").PartPaths.SequenceEqual(new[] { "home/Nagi_back", "home/Nagi_over", "home/Nagi_back_2" }),
                "home character stem includes sibling parts even when its only tap target is named with a suffix");
            assert(home.SelectCharacter("17").Key == "natsume" && home.SelectCharacter("KANATA").SourceCharacterId == 6 && home.SelectCharacter("kanata").Name == "Kanata",
                "home selection accepts source IDs while preserving path identity instead of remapping placeholder IDs");
            assert(home.UnassignedParts.Select(p => p["path"]!.ToString()).SequenceEqual(new[] { "home/Bg", "home/Chair", "home/Nagina" }),
                "home background and props stay in the full scene and character prefixes require a boundary");
            assert(home.SpineDirectory == "host/spot/spine" && home.Read("host/spot/spot.json").SequenceEqual(Encoding.UTF8.GetBytes(spot.ToJsonString())),
                "home scene reads original hashed compressed assets without extracted copies");
            assert(ReferenceEquals(home, catalog.Open(40005)) && filesBefore.SequenceEqual(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)),
                "home metadata is cached within the catalog and reading an old archive does not modify it");
            assert(JsonNode.Parse(JsonOutput.Serialize(home.ToJson()))!["characters"]!.AsArray().Count == 5,
                "home summaries serialize through the Native AOT JSON path");
            var firstOrder = catalog.List().ToJsonString();
            intl.Index(42, 43);
            assert(firstOrder == new HomeCatalog(archive).List().ToJsonString(), "home source order is deterministic when the story index order changes");
            Reject(() => home.SelectCharacter("unknown"), assert, "home selection rejects missing characters with an actionable error");
            Reject(() => catalog.Open(40005, "intl", 44), assert, "home source selection cannot cross the requested site");
            Reject(() => catalog.List("../intl"), assert, "home site selection remains within the archive");

            var generic = JsonNode.Parse("""
                {"spotId":77,"master":{"_id":77},"characters":[
                  {"path":"Character_1","_characterId":1},{"path":"Character_2","_characterId":2}],"spineCharacters":[],"skeletons":[]}
                """)!.AsObject();
            foreach (var name in new[] { "Character_1", "Character_1_shadow", "Character_2", "Character_2_back" }) generic["spineCharacters"]!.AsArray().Add(Part(name));
            intl.Story(45, generic, "area"); intl.Index(42, 43, 45);
            var separate = new HomeCatalog(archive).Open(77);
            assert(separate.Characters.Count == 2 && separate.SelectCharacter("1").Parts.Count == 2 && separate.SelectCharacter("2").Parts.Count == 2,
                "ambiguous generic prefixes do not merge distinct source characters, including root-level paths");

            var unusual = (JsonObject)generic.DeepClone();
            unusual["spotId"] = 78; unusual["master"]!["_id"] = 78;
            unusual["characters"] = new JsonArray(new JsonObject { ["path"] = "home/..\\unsafe:name", ["_characterId"] = 8 });
            unusual["spineCharacters"] = new JsonArray(Part("home/..\\unsafe:name"));
            intl.Story(46, unusual, "area"); intl.Index(42, 43, 45, 46);
            var key = new HomeCatalog(archive).Open(78).Characters.Single().Key;
            assert(key == "unsafe-name" && key.All(c => char.IsLetterOrDigit(c) || c == '-'),
                "home batch keys cannot carry source path separators, traversal segments or reserved punctuation into filenames");
            unusual["characters"]![0]!["path"] = "home/CON";
            unusual["spineCharacters"] = new JsonArray(Part("home/CON"));
            intl.Story(46, unusual, "area");
            assert(new HomeCatalog(archive).Open(78).Characters.Single().Key == "character-con",
                "home batch filenames also avoid Windows device names");

            intl.Story(47, unusual, "area", hostSpotId: 79); intl.Index(47);
            Reject(() => new HomeCatalog(archive).Open(79), assert, "home loading rejects inconsistent host and spot IDs");
            intl.Story(48, unusual, "area", hostKind: "afterlive"); intl.Index(48);
            Reject(() => new HomeCatalog(archive).List(), assert, "home loading rejects an inconsistent declared and materialized host kind");

            SharedSkeletons(root, intl, archive, assert);
            NonInteractiveCharacters(intl, archive, assert);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void NonInteractiveCharacters(FixtureSite site, Archive archive, Action<bool, string> assert)
    {
        foreach (var rule in new[]
        {
            (SpotId: 30002, Name: "Manager", Skeleton: "03_mugen_01_vrfloor_02_manager.json"),
            (SpotId: 30003, Name: "Marukun", Skeleton: "03_yumemita_01_vrfloor_03_marukun.json"),
        })
        {
            var part = Part("home/" + rule.Name);
            var spot = new JsonObject
            {
                ["spotId"] = rule.SpotId, ["master"] = new JsonObject { ["_id"] = rule.SpotId }, ["characters"] = new JsonArray(),
                ["spineCharacters"] = new JsonArray(part, Part("home/" + rule.Name + "_shadow"), Part("home/Bg"), Part("home/Effect")),
                ["skeletons"] = new JsonArray(new JsonObject { ["name"] = part["skeletonData"]!.DeepClone(), ["skeleton"] = rule.Skeleton }),
            };
            site.Story(85, spot, "area"); site.Index(85);
            var home = new HomeCatalog(archive).Open(rule.SpotId);
            var character = home.SelectCharacter(rule.Name);
            assert(character.Association == "verifiedNonInteractive" && character.SourceCharacterId is null && character.Targets.Count == 0
                && character.Parts.Count == 2 && home.UnassignedParts.Count == 2,
                $"verified non-interactive {rule.Name} includes its sibling parts without fabricating a playable ID or including scene props");
            spot["spotId"] = 999; spot["master"]!["_id"] = 999;
            site.Story(85, spot, "area");
            assert(new HomeCatalog(archive).Open(999).Characters.Count == 0,
                $"the {rule.Name} exception does not apply to another spot with the same component name");
            spot["spotId"] = rule.SpotId; spot["master"]!["_id"] = rule.SpotId;
            spot["skeletons"]![0]!["skeleton"] = "unverified.json";
            site.Story(85, spot, "area");
            assert(new HomeCatalog(archive).Open(rule.SpotId).Characters.Count == 0,
                $"the {rule.Name} exception requires the verified skeleton filename");
            spot["skeletons"]![0]!["skeleton"] = rule.Skeleton;
            part["path"] = "home/" + rule.Name + "Copy";
            site.Story(85, spot, "area");
            assert(new HomeCatalog(archive).Open(rule.SpotId).Characters.Count == 0,
                $"the {rule.Name} exception requires an exact component name rather than a loose prefix");
        }
    }

    private static void SharedSkeletons(string root, FixtureSite site, Archive archive, Action<bool, string> assert)
    {
        var spot = JsonNode.Parse("""
            {"spotId":81,"master":{"_id":81},"characters":[
              {"path":"home/AkiBeni/Spine GameObject (shared)/Aki","_characterId":1},
              {"path":"home/AkiBeni/Spine GameObject (shared)/Beni_back","_characterId":2}],
             "spineCharacters":[],"skeletons":[]}
            """)!.AsObject();
        foreach (var suffix in new[] { "", "_back" })
        {
            var sharedPart = Part("home/AkiBeni" + suffix);
            spot["spineCharacters"]!.AsArray().Add(sharedPart);
            spot["skeletons"]!.AsArray().Add(new JsonObject { ["name"] = sharedPart["skeletonData"]!.DeepClone(), ["skeleton"] = "shared" + suffix + ".json" });
        }
        var skeleton = JsonNode.Parse("""
            {"bones":[{"name":"root"},{"name":"scene_aki_hip","parent":"root"},
              {"name":"scene_beni_hip","parent":"root"},{"name":"scene_aki_beni_face","parent":"scene_beni_hip"}],
             "slots":[
              {"name":"scene_aki_body","bone":"scene_aki_hip"},
              {"name":"scene_beni_body","bone":"scene_beni_hip"},
              {"name":"scene_aki_hand","bone":"scene_beni_hip"},
              {"name":"scene_aki_beni_face","bone":"scene_aki_beni_face"},
              {"name":"秋_eyes","bone":"scene_aki_hip"},{"name":"秋_hair","bone":"root"},
              {"name":"sleeve","bone":"scene_beni_hip"},{"name":"weighted_mesh","bone":"root"},
              {"name":"scene_bg_table","bone":"root"},{"name":"clip","bone":"root"},{"name":"reference","bone":"root"}],
             "skins":[{"name":"default","attachments":{}}]}
            """)!.AsObject();
        var attachments = skeleton["skins"]![0]!["attachments"]!.AsObject();
        foreach (var slot in skeleton["slots"]!.AsArray().OfType<JsonObject>().Where(s => s["name"]!.ToString() != "reference"))
            attachments[slot["name"]!.ToString()] = new JsonObject { ["art"] = new JsonObject { ["width"] = 10, ["height"] = 10 } };
        attachments["weighted_mesh"] = JsonNode.Parse("""{"mesh":{"type":"mesh","uvs":[0,0,1,0],"vertices":[1,2,0,0,1,1,2,1,0,1]}}""");
        attachments["clip"] = JsonNode.Parse("""{"mask":{"type":"clipping","end":"scene_beni_body","vertices":[0,0,1,0,1,1]}}""");
        var back = JsonNode.Parse("""
            {"bones":[{"name":"root"}],"slots":[{"name":"scene_aki_shadow","bone":"root"}],
             "skins":{"default":{"scene_aki_shadow":{"art":{"width":10,"height":10}}}}}
            """)!.AsObject();
        var files = new Dictionary<string, JsonObject>
        {
            ["host/spot/spine/shared.json"] = skeleton, ["host/spot/spine/shared_back.json"] = back,
        };
        site.Story(81, spot, "area", extraFiles: files); site.Index(81);
        var home = new HomeCatalog(archive).Open(81);
        var aki = home.SelectCharacter("aki"); var beni = home.SelectCharacter("beni");
        var part = home.SpineParts[0];
        var akiSlots = home.IncludedSlots(aki, part)!; var beniSlots = home.IncludedSlots(beni, part)!;
        assert(home.Characters.Count == 2 && home.UnassignedParts.Count == 0 && home.SpineParts.Count == 2
            && aki.Parts.Count == 2 && beni.Parts.Count == 2 && aki.SharedPartPaths.Count == 2,
            "nested tap ancestry groups every shared skeleton once in the scene and into each character including sibling back layers");
        assert(akiSlots.SetEquals(new[] { "scene_aki_body", "scene_aki_hand", "秋_eyes", "秋_hair" })
            && beniSlots.SetEquals(new[] { "scene_beni_body", "scene_aki_beni_face", "sleeve", "weighted_mesh" }),
            "shared slot ownership respects artwork identity, named bone ancestors, locally learned Japanese names and weighted meshes");
        assert(!akiSlots.Overlaps(beniSlots) && !akiSlots.Contains("scene_bg_table") && !beniSlots.Contains("scene_bg_table"),
            "shared characters do not leak one another or root scene furniture into individual exports");
        assert(home.IncludedSlots(beni, home.SpineParts[1])!.Count == 0 && home.IncludedSlots(aki, home.SpineParts[1])!.SetEquals(new[] { "scene_aki_shadow" }),
            "a shared back layer may have no artwork for one member and is skipped without losing that character's other skeleton");
        var description = home.ToJson(includeSlotFilters: true);
        assert(description["characters"]!.AsArray().All(c => c!["exportSupported"]!.GetValue<bool>())
            && description["characters"]![0]!["slotFilters"]![0]!["sharedSceneSlots"]![0]!.ToString() == "scene_bg_table",
            "home show can validate shared character isolation and report exact slots plus shared scene furniture");

        skeleton["slots"]!.AsArray().Add(new JsonObject { ["name"] = "body_fragment", ["bone"] = "root" });
        attachments["body_fragment"] = new JsonObject { ["art"] = new JsonObject { ["width"] = 10, ["height"] = 10 } };
        site.Story(81, spot, "area", extraFiles: files);
        var unresolved = new HomeCatalog(archive).Open(81);
        Reject(() => unresolved.IncludedSlots(unresolved.Characters[0], unresolved.SpineParts[0]), assert,
            "an unidentified drawable slot blocks individual export instead of silently dropping possible character artwork");
        assert(unresolved.SpineParts.Count == 2 && unresolved.ToJson()["characters"]!.AsArray().Count == 2
            && unresolved.ToJson(includeSlotFilters: true)["characters"]![0]!["exportSupported"]!.GetValue<bool>() == false,
            "unsupported character isolation is explicit in home show while full scene metadata remains usable");
    }

    private static JsonObject Part(string path) => new()
    {
        ["path"] = path, ["skeletonData"] = path + "_SkeletonData",
        ["animation"] = new JsonObject { ["_animationName"] = "home_start", ["loop"] = 0 },
        ["world"] = new JsonArray(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
    };

    private static void Reject(Action action, Action<bool, string> assert, string label)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or NotSupportedException) { assert(true, label); return; }
        assert(false, label);
    }

    private sealed class FixtureSite(string root, string site)
    {
        private void Write(string relative, byte[] bytes)
        {
            var path = Path.Combine(root, "sites", site, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        private JsonObject Put(JsonObject value)
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
            var asset = "assets/" + Archive.Hash(bytes) + ".json.gz";
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(bytes);
            Write(asset, output.ToArray());
            return new JsonObject { ["asset"] = asset, ["size"] = bytes.Length, ["stored"] = output.Length };
        }

        public void Story(int advId, JsonObject spot, string talk, bool descriptor = true, int? hostSpotId = null,
            string hostKind = "home", IReadOnlyDictionary<string, JsonObject>? extraFiles = null)
        {
            var host = new JsonObject { ["kind"] = hostKind, ["home"] = new JsonObject
            {
                ["spotId"] = hostSpotId.HasValue ? JsonValue.Create(hostSpotId.Value) : spot["spotId"]!.DeepClone(), ["spot"] = "host/spot/spot.json", ["room"] = "host/spot/room.glb", ["talk"] = talk,
            } };
            var manifest = new JsonObject
            {
                ["advId"] = advId,
                ["files"] = new JsonObject { ["host/host.json"] = Put(host), ["host/spot/spot.json"] = Put(spot) },
            };
            if (extraFiles is not null)
                foreach (var file in extraFiles) manifest["files"]![file.Key] = Put(file.Value);
            if (descriptor) manifest["host"] = new JsonObject { ["kind"] = "home", ["doc"] = "host/host.json" };
            Write($"stories/{advId.ToString(CultureInfo.InvariantCulture)}.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        }

        public void Index(params int[] ids)
        {
            var index = new JsonObject { ["stories"] = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject
            {
                ["advId"] = id, ["manifest"] = $"stories/{id.ToString(CultureInfo.InvariantCulture)}.json", ["groups"] = new JsonArray(),
            }).ToArray()) };
            Write("stories.json", Encoding.UTF8.GetBytes(index.ToJsonString()));
        }
    }
}
