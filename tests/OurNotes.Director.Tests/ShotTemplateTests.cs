using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

internal static class ShotTemplateTests
{
    public static void Run(Action<bool,string> assert)
    {
        var root=Path.Combine(Path.GetTempPath(),"ournotes-shot-template-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Write(string relative,JsonNode value)
        {
            string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,value.ToJsonString());
        }
        JsonObject Asset(string site,string json)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(json);string asset="assets/"+Archive.Hash(bytes)+".json";
            Write("sites/"+site+"/"+asset,JsonNode.Parse(json)!);
            // JsonNode may normalize whitespace. Hash and size describe the stored bytes.
            File.WriteAllBytes(Path.Combine(root,"sites",site,asset),bytes);
            return new(){["asset"]=asset,["size"]=bytes.Length,["stored"]=bytes.Length};
        }
        JsonObject Model(string site,string id,int character,bool present=true)
        {
            if(present)Write($"sites/{site}/models/{id}.json",new JsonObject{["files"]=new JsonObject()});
            return new(){["site"]=site,["id"]=id,["character"]=character,["manifest"]=$"models/{id}.json"};
        }
        JsonObject Story(string site,string id,string language,bool simple=false)
        {
            var files=new JsonObject
            {
                ["story.json"]=Asset(site,"""{"episode":"episode.json","scene":"scene.json"}"""),
                ["episode.json"]=Asset(site,"""{"commands":[],"master":{"_playbackMode":0}}"""),
            };
            if(!simple)files["scene.json"]=Asset(site,"""{"settings":{"playerSettings":{"_initializeEpisodes":[],"_finalizeEpisodes":[],"_waitCommandLingeringTimeOnAutoPlay":0.5}}}""");
            Write($"sites/{site}/stories/{id}.json",new JsonObject
            {
                ["advId"]=id,["language"]=language,["story"]=new JsonObject{["playbackMode"]=simple?1:0},["files"]=files,
                ["languages"]=new JsonObject{[language]=new JsonObject{["files"]=new JsonObject()}},
            });
            return new(){["site"]=site,["advId"]=id,["manifest"]=$"stories/{id}.json",["languages"]=new JsonArray(language)};
        }
        bool Rejects(Action action,string reason)
        {
            try{action();return false;}catch(ArgumentException exception){return exception.Message.Contains(reason,StringComparison.OrdinalIgnoreCase);}
        }
        void Models(params JsonObject[] rows)=>Write("index/models.json",new JsonObject{["models"]=new JsonArray(rows.Select(row=>(JsonNode?)row.DeepClone()).ToArray())});
        try
        {
            Write("index/stats.json",new JsonObject());
            var hikari=Model("jp","adv_live2d_hikari_001_school",1);
            var hikariOther=Model("jp","adv_live2d_hikari_002_casual",1);
            var kanon=Model("jp","adv_live2d_kanon_003_school",2);
            var missing=Model("jp","adv_live2d_absent_004_school",3,present:false);
            var hotaru=Model("intl","adv_live2d_hotaru_016_casual_spring_01",16);
            var hotaruGuest=Model("jp","adv_live2d_hotaru_016_casual_spring_01",16);
            var natsume=Model("intl","adv_live2d_natsume_017_casual_spring_01",17);
            Models(hikari,hikariOther,kanon,missing,hotaru,natsume,hotaruGuest);
            Write("discovery.json",JsonNode.Parse("""
                {"band":{"bandTextId":"Band_Name_millsage","characters":[{"id":16},{"id":17}]},
                 "regions":{"jp":{"band":{"bandTextId":"Band_Name_other","characters":[{"id":1},{"id":2}]}}}}
                """)!);
            var japanese=Story("jp","900","ja");
            var preferred=Story("jp","901","ja");
            var english=Story("jp","902","en");
            var simple=Story("jp","899","ja",simple:true);
            var legacy=Story("intl","10060","zh-Hans");
            Write("index/catalog.json",new JsonObject{["stories"]=new JsonArray(japanese,preferred,english,simple,legacy)});
            Story("jp","10060","zh-Hans"); // Leftover source from an older selection is deliberately excluded.
            Write("sites/jp/director/resources.json",new JsonObject{["baseAdvId"]=901});
            var archive=new Archive(root);

            var shot=Shot.CreateTemplate(archive,"jp");
            assert(shot.Actors.Single().Model==Archive.S(hikari,"id")&&shot.Actors[0].Name=="hikari",
                "shot defaults prefer selected band members over a downloaded Hotaru cameo and ignore missing or other-site models");
            assert(shot.Source is {Adv:"901",Site:"jp",Language:"ja"},
                "shot records the selected normal director base and downloaded language instead of the old Millsage source");
            var portrait=Shot.CreateTemplate(archive,"jp","portrait",Archive.S(kanon,"id"));
            assert(portrait.Actors.Single().Model==Archive.S(kanon,"id")&&portrait.Camera.Zoom==2.5f,
                "portrait template honors an explicit downloaded model");
            var duet=Shot.CreateTemplate(archive,"jp","duet",Archive.S(hikari,"id"));
            assert(duet.Actors.Select(actor=>actor.Model).SequenceEqual(new[]{Archive.S(hikari,"id"),Archive.S(kanon,"id")})&&duet.Actors[0].X==.35f&&duet.Actors[1].X==.65f,
                "duet prefers two different characters while keeping the explicitly requested first model");
            var legacyDuet=Shot.CreateTemplate(archive,"intl","duet");
            assert(legacyDuet.Actors.Select(actor=>actor.Name).SequenceEqual(new[]{"hotaru","natsume"})&&legacyDuet.Source!.Adv=="10060",
                "Millsage archives keep the existing Hotaru and Natsume defaults when those models are selected");
            var englishShot=Shot.CreateTemplate(archive,"jp",language:"en");
            assert(englishShot.Source is {Adv:"902",Language:"en"}&&Rejects(()=>Shot.CreateTemplate(archive,"jp",language:"ko"),"language 'ko'"),
                "explicit template language selects a compatible source and rejects undownloaded languages");
            assert(Rejects(()=>Shot.CreateTemplate(archive,"jp",model:Archive.S(natsume,"id")),"not in the selected archive"),
                "explicit model selection cannot silently substitute a model from another site");
            assert(Shot.CreateTemplate(archive,"jp",model:Archive.S(hotaruGuest,"id")).Actors.Single().Model==Archive.S(hotaruGuest,"id"),
                "explicit model selection can still use a downloaded cameo from another band");

            string path=Path.Combine(root,"shot.json");
            File.WriteAllText(path,JsonOutput.Serialize(shot,Shot.JsonOptions));
            var project=DirectorProject.Read(path);
            var document=DirectorProject.Document(path);
            var normal=AdvProgram.Load(archive,project.Site,project.Adv);
            var program=AdvProgram.From(document,normal.Scene!["settings"]!["playerSettings"]!);
            assert(project is {Adv:"901",Site:"jp",Language:"ja"}&&program.Rows.Single(row=>row.Command=="Character").Text("Model")==Archive.S(hikari,"id"),
                "generated template round trips through unified project loading and the actual native command parser without story 10060");
            assert(Rejects(()=>new Shot{Source=new(){Adv=""},Actors=[new(){Model="model"}]}.Validate(),"source"),
                "shot source validation rejects incomplete explicit source metadata");

            Models(hikari,hikariOther);
            var costumes=Shot.CreateTemplate(archive,"jp","duet");
            assert(costumes.Actors.Select(actor=>actor.Model).Distinct().Count()==2&&costumes.Actors.Select(actor=>actor.Name).SequenceEqual(new[]{"hikari","hikari2"}),
                "duet with two costumes uses distinct models and unique actor names");
            Models(hikari);
            assert(Rejects(()=>Shot.CreateTemplate(archive,"jp","duet"),"two different downloaded models"),
                "duet fails clearly when the selection contains only one model");
            var withoutLanguage=japanese.DeepClone();withoutLanguage["languages"]=new JsonArray();
            Write("index/catalog.json",new JsonObject{["stories"]=new JsonArray(withoutLanguage)});
            assert(Rejects(()=>Shot.CreateTemplate(archive,"jp"),"normal ADV"),
                "a language-filtered source cannot fall back to its undownloaded original UI and fonts");
            Write("index/catalog.json",new JsonObject{["stories"]=new JsonArray(simple.DeepClone())});
            assert(Rejects(()=>Shot.CreateTemplate(archive,"jp"),"normal ADV"),
                "a simple-only selection cannot use an old normal story left on disk for its template");
            Models();
            assert(Rejects(()=>Shot.CreateTemplate(archive,"jp"),"No downloaded models"),
                "template generation reports an empty current model selection");
        }
        finally{Directory.Delete(root,recursive:true);}
    }
}
