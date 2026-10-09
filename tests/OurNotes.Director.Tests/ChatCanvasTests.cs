using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class ChatCanvasTests
{
    public static int Run(string output)
    {
        var archive=new Archive(Path.GetFullPath("legacy/millsage-story-archive/archive"));var manifest=archive.Manifest("intl","stories","10061");
        var definition=JsonNode.Parse(archive.Logical("intl",manifest,"story.json"))!;
        var episode=JsonNode.Parse(archive.Logical("intl",manifest,"episode.json"))!;var scene=JsonNode.Parse(archive.Logical("intl",manifest,Archive.S(definition,"scene")))!;
        var loop=new AdvLoop();var camera=AdvCamera.FromScene(loop,new(loop),scene);
        using var gl=new OffscreenGl(1280,592);using var chat=new AdvChatCanvas(archive,"intl",manifest,definition,"zh-Hans",gl);
        chat.Select(10069);chat.SetText("_windowNameText",episode["text"]!["advchat_kanata_01"]!["simplifiedChinese"]!.ToString());chat.SetBattery(90);
        foreach(var row in episode["commands"]!.AsArray().Where(c=>Archive.S(c!,"cmd")=="ChatTalk").Take(3)){
            string text=episode["text"]![Archive.S(row!,"AdvTextID")]!["simplifiedChinese"]!.ToString();string name=episode["text"]![row!["TargetTextIDs"]![0]!.ToString()]!["simplifiedChinese"]!.ToString();
            chat.Send((int)Actor.N(row["TargetChatID"]),text,name);
        }
        chat.ScrollToBottom();gl.Begin([.15f,.15f,.2f,1]);chat.Draw(camera);var image=gl.Read(640,296);
        if(chat.BubbleCount!=3||image.Rgba.Where((_,i)=>i%4!=3).Distinct().Count()<100)throw new Exception("Native chat did not render its original bubbles and glyphs");
        File.WriteAllBytes(output,Png.Encode(image));Console.WriteLine("Rendered three original native chat bubbles with emoji: "+output);return 0;
    }
}
