using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvUi
{
    // Validate the text resources before model warm-up and video encoding, as StoryUI.checkTexts does.
    // This checks glyph lookup and rich-text parsing; it is not a full timeline/render validation.
    public void CheckTexts(IEnumerable<AdvRow> rows)
    {
        var layouts=new Dictionary<string,AdvTmpText>();var checkedTexts=new HashSet<(string Path,string Text)>();
        void Check(AdvRow row,string role,string value){
            if(value.Length==0)return;
            var targets=nodes.Values.Where(n=>Archive.S(n,"name")==role&&n["textStyle"] is not null).ToArray();
            if(targets.Length==0)throw new InvalidDataException($"Text preflight row {row.Index}: UI text node {role} is absent");
            foreach(var node in targets){string path=Archive.S(node,"path");if(!checkedTexts.Add((path,value)))continue;
                try{
                    if(!layouts.TryGetValue(path,out var layout))layouts[path]=layout=new(fonts,fonts["texts"]![path]??throw new InvalidDataException("Text font binding absent"));
                    layout.SetStoryText(value);_ = layout.CharacterCount;
                }catch(Exception e)when(e is NotSupportedException or InvalidDataException or FormatException){throw new InvalidDataException($"Text preflight row {row.Index} ({row.Command}), {path}: {e.Message}",e);}
            }
        }
        foreach(var row in rows.Where(r=>!r.Ignored)){
            string id=row.Text("AdvTextID");
            if(row.Command=="Talk"&&id is not ("" or "0")){
                Check(row,"TalkText",Localize(id));Check(row,"SpeakerText",SpeakerName(row,MasterIds,Localize));
            }else if(row.Command=="Subtitles"&&id is not ("" or "0"))Check(row,"SubtitlesText",Localize(id));
        }
    }
}
