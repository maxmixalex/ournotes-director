using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OurNotes.Director.Core;

public sealed partial class AdvTmpText
{
    static readonly Regex RubyTags=new("<r(uby)?=\"?([\\s\\S]*?)\"?>([\\s\\S]*?)</r(uby)?>|<ruby>([\\s\\S]*?)<rt>([\\s\\S]*?)</rt></ruby>|<r>([\\s\\S]*?)<rt>([\\s\\S]*?)</rt></r>");
    string? storyText;
    public void SetStoryText(string value)
    {
        if(storyText==value)return;
        string shown=value;float top=originalMarginTop;
        if(binding["uiRubyText"] is {} ui&&Archive.S(binding,"class") is "RubyTextMeshProUGUI" or "RubyEmojiTextMeshProUGUI"){
            var r=binding["ruby"]??throw new InvalidDataException("Missing TMP ruby settings");
            if(!string.IsNullOrWhiteSpace(Archive.S(r,"_rubyLineHeight")))throw new NotSupportedException("TMP ruby line-height");
            var output=new StringBuilder();int index=0;float cur=0,rubyCur=0,scale=N(r["_rubyScale"]);
            static string F(float v)=>v==0?"0":v.ToString("G7",CultureInfo.InvariantCulture);
            foreach(Match m in RubyTags.Matches(value)){
                if(autoSize)throw new NotSupportedException("TMP ruby autosizing");
                string head=value[index..m.Index];if(head.Length>0){cur+=PreferredWidthOf(head);output.Append(head);}
                string reading=m.Groups[5].Success?m.Groups[6].Value:m.Groups[7].Success?m.Groups[8].Value:m.Groups[2].Value;
                string basis=m.Groups[5].Success?m.Groups[5].Value:m.Groups[7].Success?m.Groups[7].Value:m.Groups[3].Value;
                float rubyW=PreferredWidthOf(reading)*scale,baseW=PreferredWidthOf(basis),offset=-(baseW*.5f+rubyW*.5f),comp=(baseW-rubyW)*.5f;
                int type=(int)N(r["_rubyShowType"]);
                if(type==2){float x=baseW+offset+cur,d=x-rubyCur;if(d>=0)rubyCur=rubyW+x;else{output.Append("<space=").Append(F(-d)).Append('>');rubyCur=rubyW+rubyCur+N(r["_rubyMargin"]);cur-=d;}cur+=baseW;}
                else if(type==1)cur+=baseW;
                else if(type==0)cur+=comp<0?baseW:rubyW;
                else throw new NotSupportedException("TMP ruby show type "+type);
                output.Append("<nobr>");if(type==0&&comp<0)output.Append("<space=").Append(F(-comp)).Append('>');
                output.Append(basis).Append("<space=").Append(F(offset)).Append("><voffset=").Append(Archive.S(r,"_rubyVerticalOffset")).Append("><size=").Append(F(scale*100)).Append("%>").Append(reading).Append("</size></voffset><space=").Append(F(comp)).Append("></nobr>");
                index=m.Index+m.Length;
            }
            output.Append(value[index..]);shown=output.ToString();
            if(Regex.IsMatch(value.Split('\n')[0],"<ruby\\s*=\\s*[^\"'>]+>|<r\\s*=\\s*[^\"'>]+>"))top=N(ui["_rubyMarginTop"]);
        }
        if(margin.Y!=top){margin.Y=top;preferredCache.Clear();glyphCache=null;}
        Text=shown;storyText=value;
    }
    float PreferredWidthOf(string value)
    {
        var saved=elements;
        try{elements=Parse(value);return elements.Length==0?0:Preferred(Layout(size,32767,32767,false).Width,margin.X,margin.Z);}
        finally{elements=saved;}
    }
}
