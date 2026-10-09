using System.Text.Json;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class CriLipsTests
{
    public static int Run(string referencePath)
    {
        var archive=new Archive(Path.Combine(Environment.CurrentDirectory,"legacy","millsage-story-archive","archive"));
        var manifest=archive.Manifest("intl","stories","10062");var definition=JsonNode.Parse(archive.Logical("intl",manifest,"story.json"))!;var data=definition["crilips"]!;
        var lips=new CriLipsData(JsonNode.Parse(archive.Logical("intl",manifest,Archive.S(data,"descriptor")))!,archive.Logical("intl",manifest,Archive.S(data,"data")));
        var voiceManifest=archive.Manifest("intl","stories","10060");var voiceDefinition=JsonNode.Parse(archive.Logical("intl",voiceManifest,"story.json"))!;
        var episode=JsonNode.Parse(archive.Logical("intl",voiceManifest,Archive.S(voiceDefinition,"episode")))!;
        var pcm=new List<float>();foreach(long id in new long[]{3101040101001,3101040101002}){var cue=AdvAudio.Decode(archive,"intl",voiceManifest,episode,id,"ffmpeg");for(int i=0;i<cue.Pcm.Length;i+=cue.Channels)pcm.Add(cue.Pcm[i]);pcm.AddRange(new float[48000]);}
        var input=pcm.ToArray();var analyzer=new CriLipsAnalyzer(lips);var actual=new List<float>();for(int offset=0;offset<input.Length;offset+=1600)actual.Add(analyzer.Feed(input.AsSpan(offset,Math.Min(1600,input.Length-offset))));
        var expected=JsonSerializer.Deserialize<float[]>(File.ReadAllText(referencePath))!;
        if(expected.Length!=actual.Count)throw new Exception("CRI Lips reference frame count differs");
        float error=expected.Zip(actual,(a,b)=>Math.Abs(a-b)).Max();if(!float.IsFinite(error)||error>.0001f)throw new Exception($"CRI Lips reference mismatch: max error {error}");
        if(actual.Max()<.1f)throw new Exception("Speech did not open the mouth");
        var chunked=new CriLipsAnalyzer(lips);float ending=0;for(int offset=0;offset<input.Length;offset+=137)ending=chunked.Feed(input.AsSpan(offset,Math.Min(137,input.Length-offset)));
        if(ending!=actual[^1])throw new Exception("CRI Lips analysis depends on feed chunk sizes");
        Console.WriteLine($"Passed CRI Lips reference comparison: {actual.Count} frames, max error {error:G9}, peak {actual.Max():G9}.");return 0;
    }
}
