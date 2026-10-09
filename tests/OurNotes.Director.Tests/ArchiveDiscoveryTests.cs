using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core.Archiving;

static class ArchiveDiscoveryTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "ournotes-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var origin = new FixtureOrigin();
            using var client = new HttpClient(origin);
            using var downloader = new ArchiveDownloader(client);
            ArchiveOptions Options(bool deep) => new()
            {
                OutputDirectory = Path.Combine(temporary, deep ? "deep" : "shallow"),
                MetadataBase = "https://fixture.invalid/metadata",
                SiteRoots = new Dictionary<string, string>
                {
                    ["intl"] = "https://fixture.invalid/intl",
                    ["jp"] = "https://fixture.invalid/jp",
                },
                DeepScan = deep,
                Concurrency = 3,
                Retries = 1,
            };
            var discovery = await downloader.DiscoverAsync(Options(true));
            var stories = discovery["stories"]!.AsArray().OfType<JsonObject>().ToDictionary(row => row["advId"]!.GetValue<long>());
            static string[] Strings(JsonNode? value) => value!.AsArray().Select(x => x!.ToString()).Order(StringComparer.Ordinal).ToArray();
            static long[] Numbers(JsonNode? value) => value!.AsArray().Select(x => x!.GetValue<long>()).Order().ToArray();
            long[] masterIds = [10060, 10061, 10062, 10063, 10304, 10549, 10836, 10837, 10844, 10845];
            var expected = masterIds.Concat([30001L, 30002L, 30003L]).Order().ToArray();
            assert(stories.Keys.Order().SequenceEqual(expected), "Archive discovery covers every first-class category, index grouping, and model cameo without unrelated stories.");
            assert(Numbers(discovery["band"]!["characters"] is JsonArray characters ? new JsonArray(characters.Select(x => x!["id"]!.DeepClone()).ToArray()) : null).SequenceEqual([16L, 17L]), "Archive band selection reads the full character set.");
            assert(Strings(stories[10060]["categories"]).SequenceEqual(["chapter", "live-result"]), "Repeated ADV IDs merge multiple category reasons without losing either source.");
            assert(Strings(stories[10061]["categories"]).SequenceEqual(["birthday"]), "Special story chapters are classified as birthdays.");
            assert(stories[10062]["episodeKind"]!.ToString() == "another" && Numbers(stories[10062]["characterIds"]).SequenceEqual([16L]), "Another episodes use their selected character even within a mixed chapter.");
            assert(stories[10063]["episodeKind"]!.ToString() == "extra", "A direct character link selects an extra episode outside the band's chapter.");
            assert(Strings(stories[10549]["categories"]).SequenceEqual(["friendship"]) && Numbers(stories[10549]["characterIds"]).SequenceEqual([16L, 99L]), "Friendship discovery retains mixed-band relationships.");
            assert(Strings(stories[10304]["categories"]).SequenceEqual(["live-result"]), "Live-result episodes are selected by participating characters.");
            assert(Strings(stories[10836]["categories"]).SequenceEqual(["home-tap"]) && stories.ContainsKey(10837), "Home-tap selection includes both direct character and spot membership links.");
            assert(Strings(stories[10844]["categories"]).SequenceEqual(["home-intro"]) && stories.ContainsKey(10845), "Home-intro selection includes both the band's spot and mixed-character spots.");
            assert(stories[10060]["asset"]!.ToString() == "adv_10060" && stories[10060]["sheetName"]!.ToString() == "sheet_10060", "Selected ADV entries retain their MasterAdv metadata.");
            assert(Strings(stories[10060]["regions"]).SequenceEqual(["intl", "jp"]), "MasterData array and _allData formats merge consistently across regions.");
            assert(Strings(stories[30001]["reasons"]).Contains("story-index:group") && Strings(stories[30002]["reasons"]).Contains("story-index:group"), "Story index groups select both chapter-band and character-group entries without a model hit.");
            assert(!Strings(stories[30001]["categories"]).Contains("model-cameo"), "Group selection does not mislabel an entry as a model cameo.");
            assert(Strings(stories[30003]["categories"]).SequenceEqual(["model-cameo"]) && Strings(stories[30003]["reasons"]).SequenceEqual(["manifest:model-character"]), "A Japan-only model appearance adds a previously unselected story.");
            assert(stories[30003]["sites"]!.AsObject().Select(x => x.Key).Order(StringComparer.Ordinal).SequenceEqual(["intl", "jp"]), "A cameo discovered in the last region is attached to the already-scanned region too.");
            assert(origin.Count("/intl/models/other.json") == 1 && origin.Count("/jp/models/other.json") == 1, "Shared model manifests are fetched once per site during discovery.");
            assert(origin.Count("/intl/stories/10060.json") == 0 && origin.Count("/intl/stories/30001.json") == 0, "Deep scanning skips manifests already selected by metadata or index groups.");
            assert(!origin.Requests.Keys.Any(path => path.Contains("/assets/", StringComparison.Ordinal)), "Discovery never acquires payload assets.");

            var shallow = await downloader.DiscoverAsync(Options(false));
            var shallowIds = shallow["stories"]!.AsArray().Select(x => x!["advId"]!.GetValue<long>()).Order().ToArray();
            assert(shallowIds.SequenceEqual(masterIds.Concat([30001L, 30002L]).Order()), "Disabling deep scan retains the complete metadata and index-group selection while excluding model-only cameos.");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private sealed class FixtureOrigin : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);
        public int Count(string path) => Requests.TryGetValue(path, out var value) ? value : 0;

        public FixtureOrigin()
        {
            Put("/metadata/current_version.json", """{"regions":{"hk-tw-mo":{"version":"fixture"},"jp":{"version":"fixture"}}}""");
            var tables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MasterBand.json"] = """[{"_id":4,"_nameTextID":"Band_Name_millsage"},{"_id":99,"_nameTextID":"Other_Band"}]""",
                ["MasterCharacter.json"] = """[{"_id":16,"_bandID":4,"_nameTextID":"Character_Name_hotaru"},{"_id":17,"_bandId":4,"nameTextId":"Character_Name_other"},{"_id":99,"_bandId":99}]""",
                ["MasterStoryChapter.json"] = """[{"_id":4,"_bandId":4,"_mainCharacterIds":[16,17]},{"_id":5,"_bandId":4,"_mainCharacterIds":[16],"_isSpecialStory":true},{"_id":6,"_bandId":99,"_mainCharacterIds":[16,99]},{"_id":7,"_bandId":99,"_mainCharacterIds":[99]},{"_id":8,"_bandId":99,"_mainCharacterIds":[99]}]""",
                ["MasterStoryEpisode.json"] = """[{"_id":1,"_advId":10060,"_chapterId":4,"_episodeNumber":1},{"_id":2,"_advId":10061,"_chapterId":5},{"_id":3,"_advId":10062,"_chapterId":6,"_isAnotherEpisode":true,"_characterId":16},{"_id":4,"_advId":10063,"_chapterId":7,"_isExtraEpisode":true,"_characterId":17},{"_id":5,"_advId":20000,"_chapterId":8}]""",
                ["MasterCharacterFriendship.json"] = """[{"_id":1,"_masterCharacterIdA":16,"_masterCharacterIdB":99},{"_id":2,"_masterCharacterIdA":99,"_masterCharacterIdB":98}]""",
                ["MasterStoryFriendshipEpisode.json"] = """[{"_id":1,"_advId":10549,"_characterFriendshipId":1,"_episodeNumber":1},{"_id":2,"_advId":20001,"_characterFriendshipId":2}]""",
                ["MasterStoryLiveResultEpisode.json"] = """[{"_id":1,"_advId":10304,"_characterIds":[16,99]},{"_id":2,"_advId":10060,"_characterIds":[17]},{"_id":3,"_advId":20002,"_characterIds":[99]}]""",
                ["MasterHomeSpot.json"] = """[{"_id":40001,"_advId":10844,"_bandId":4,"_characterIds":[16,17]},{"_id":40002,"_advId":29999,"_bandId":99,"_characterIds":[99]},{"_id":40003,"_advId":10845,"_bandId":99,"_characterIds":[16,99]}]""",
                ["MasterStoryHomeSpotTapTalkEpisode.json"] = """[{"_id":1,"_advId":10836,"_characterId":16,"_spotId":40001},{"_id":2,"_advId":10837,"_characterId":99,"_spotId":40003},{"_id":3,"_advId":20003,"_characterId":99,"_spotId":40002}]""",
                ["MasterAdv.json"] = """[{"_id":10060,"_advEpisodeAsset":"adv_10060","_sheetName":"sheet_10060","_titleTextID":"title_10060","_playbackMode":0}]""",
            };
            foreach (var (file, rows) in tables)
            {
                Put("/metadata/tw/master/" + file, rows);
                Put("/metadata/jp/master/" + file, "{\"_allData\":" + rows + "}");
            }
            long[] ids = [10060, 10061, 10062, 10063, 10304, 10549, 10836, 10837, 10844, 10845, 30001, 30002, 30003, 30004];
            foreach (var site in new[] { "intl", "jp" })
            {
                var entries = new JsonArray();
                foreach (var id in ids)
                {
                    var groups = id switch
                    {
                        30001 => JsonNode.Parse("""[{"chapter":{"bandId":4}}]"""),
                        30002 => JsonNode.Parse("""[{"characters":[{"id":16}]}]"""),
                        _ => new JsonArray(),
                    };
                    entries.Add((JsonNode)new JsonObject { ["advId"] = id, ["groups"] = groups });
                }
                Put("/" + site + "/stories.json", new JsonObject { ["stories"] = entries }.ToJsonString());
                Put("/" + site + "/stories/30003.json", site == "jp" ? """{"models":{"appearance":"models/target.json"}}""" : """{"models":{"appearance":"models/other.json"}}""");
                Put("/" + site + "/stories/30004.json", """{"models":{"appearance":"models/other.json"}}""");
                Put("/" + site + "/models/other.json", """{"model":{"character":99},"files":{}}""");
                Put("/" + site + "/models/target.json", """{"model":{"character":16},"files":{}}""");
            }
        }

        private void Put(string path, string json) => _files[path] = Encoding.UTF8.GetBytes(json);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Requests.AddOrUpdate(path, 1, (_, count) => count + 1);
            var found = _files.TryGetValue(path, out var bytes);
            return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new ByteArrayContent(bytes ?? Encoding.UTF8.GetBytes("fixture not found")),
            });
        }
    }
}
