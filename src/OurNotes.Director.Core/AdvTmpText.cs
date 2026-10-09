using System.Numerics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OurNotes.Director.Core;

// Native TMP metrics and layout for prefab canvases. Atlas glyphs/materials remain the exported assets.
public sealed partial class AdvTmpText
{
    readonly JsonNode fonts,binding,font,material;
    readonly IReadOnlyDictionary<string,JsonNode>? fontLines;
    readonly string materialName;
    readonly bool bold,autoSize,kerning;
    readonly HashSet<int> leading,following;
    readonly float size,minSize,maxSize,baseSize,lineSpacing,paragraphSpacing,characterSpacing,wordSpacing;
    readonly int hAlign,vAlign;
    Vector4 margin;
    readonly float originalMarginTop;
    readonly JsonNode? sprites;
    readonly Dictionary<string,string> sequences=[];
    readonly HashSet<string> sequencePrefixes=[];
    readonly Dictionary<JsonNode,JsonNode> faces=new(ReferenceEqualityComparer.Instance);
    Element[] elements=[];
    bool parsePending;
    string text="";
    public string Text {get=>text;set{if(text==value)return;text=value;parsePending=true;preferredCache.Clear();glyphCache=null;}}
    void EnsureParsed(){if(!parsePending)return;elements=Parse(text);parsePending=false;}
    readonly Dictionary<(float Width,int Wrapping),Vector2> preferredCache=[];
    (float Width,float Height,Vector2 Pivot,int Wrapping,int Visible) glyphKey;
    IReadOnlyList<Glyph>? glyphCache;
    LayoutResult? renderedLayout;
    public int CharacterCount{get{EnsureParsed();return elements.Length;}}
    public Vector4 Margin=>margin;
    public float VisibleTextHeight
    {
        get{
            if(renderedLayout is not {Lines.Count:>0} r)return size;
            int visible=Math.Clamp(MaxVisible,0,r.Characters.Count),last=0;
            if(visible>0){var c=r.Characters[visible-1];last=Math.Clamp(c.Line+(c.Element.U is 10 or 13?1:0),0,r.Lines.Count-1);}
            float height=r.Lines[0].Ascender-(r.Lines[last].Descender-r.Lines[last].Offset);
            return height>0?height:throw new NotSupportedException("TMP typing line of no height");
        }
    }
    public int Wrapping{get;set;}
    public bool Enabled=>Actor.B(binding["enabled"]);
    public int MaxVisible{get;set;}=99999;
    public float RenderedFontSize{get;private set;}
    public readonly record struct Glyph(JsonNode Font,JsonNode Material,string Texture,Vector4 Rect,Vector4 Uv,float Scale,Vector4 Color,int Character,int Line,bool Sprite,bool RichColor=false);
    sealed record SizeTag(float Value,string Unit,bool Relative)
    {
        public float Apply(float original)=>Unit=="%"?(original*Value)/100:Unit=="em"?original*Value:Relative?original+Value:Value;
    }
    sealed record Element(int U,JsonNode Font,JsonNode Glyph,int Index,float Scale,bool Sprite=false,SizeTag? Size=null,float Offset=0,float OffsetEm=0,float Space=0,float SpaceEm=0,bool NoBreak=false,SizeTag? OffsetSize=null,bool Bold=false,Vector4? Color=null);
    sealed record Character(Element Element,float Scale,float Padding,float StylePadding,float X,float Y,float W,float H,float Advance,float Spacing,float Ascender,float Descender,int Line,bool Visible);
    sealed record Line(int Start,int End,float Advance,float Ascender,float Descender,float Offset);
    sealed record LayoutResult(List<Character> Characters,List<Line> Lines,float Width,float Height,bool Overflow);
    static float N(JsonNode? n,float fallback=0)=>Actor.N(n,fallback);
    public AdvTmpText(JsonNode fonts,JsonNode binding,IReadOnlyDictionary<string,JsonNode>? fontLines=null)
    {
        this.fonts=fonts;this.binding=binding;this.fontLines=fontLines;var localized=binding["localized"]??binding;
        font=fonts["fonts"]![Archive.S(localized,"fontAsset")]??throw new InvalidDataException("TMP font absent");
        materialName=Archive.S(localized,"material");material=fonts["materials"]![materialName]??throw new InvalidDataException("TMP material absent");
        size=N(binding["m_fontSize"]);minSize=N(binding["m_fontSizeMin"]);maxSize=N(binding["m_fontSizeMax"]);baseSize=N(binding["m_fontSizeBase"]);
        bold=((int)N(binding["m_fontStyle"])&1)!=0;autoSize=Actor.B(binding["m_enableAutoSizing"]);kerning=binding["m_ActiveFontFeatures"]?.AsArray().Any(v=>N(v)==1801810542)==true;
        Wrapping=(int)N(binding["m_TextWrappingMode"]);hAlign=(int)N(binding["m_HorizontalAlignment"]);vAlign=(int)N(binding["m_VerticalAlignment"]);
        lineSpacing=N(localized["lineSpacing"]);paragraphSpacing=N(binding["m_paragraphSpacing"]);characterSpacing=N(binding["m_characterSpacing"]);wordSpacing=N(binding["m_wordSpacing"]);
        var m=binding["m_margin"];margin=new(N(m?["x"]),N(m?["y"]),N(m?["z"]),N(m?["w"]));
        originalMarginTop=margin.Y;
        var breaking=font["lineBreaking"]??fonts["lineBreaking"]??new JsonObject();
        leading=Archive.S(breaking,"leading").EnumerateRunes().Select(r=>r.Value).ToHashSet();following=Archive.S(breaking,"following").EnumerateRunes().Select(r=>r.Value).ToHashSet();
        sprites=fonts["spriteAssets"]?[Archive.S(binding,"spriteAsset")];
        foreach(var sequence in sprites?["sequences"]?.AsArray()??[]){string name=Archive.S(sequence!,"name");if(!name.Contains('-'))continue;var sb=new StringBuilder();foreach(string part in name.Split('-'))sb.Append(char.ConvertFromUtf32(Convert.ToInt32(part,16)));string key=sb.ToString();sequences.TryAdd(key,name);for(int i=1;i<=key.Length;i++)sequencePrefixes.Add(key[..i]);}
        if((int)N(binding["m_fontStyle"]) is not (0 or 1)||hAlign is not (1 or 2 or 4)||vAlign is not (256 or 512 or 1024)||Wrapping is not (0 or 1)||Actor.B(binding["m_isRightToLeft"])||Actor.B(binding["m_enableVertexGradient"])||N(binding["m_overflowMode"])!=0)
            throw new NotSupportedException("TMP prefab text settings not connected");
        Text=Archive.S(binding,"m_text");
    }
    static bool White(int u)=>u<=65535&&char.IsWhiteSpace((char)u);
    static bool Feed(int u)=>u is 10 or 11 or 0x2028 or 0x2029;
    static bool Visible(int u)=>u==9||!White(u)&&u is not (0x200B or 0xAD or 3);
    static bool Cjk(int u)=>u is >=0x2E80 and <=0x2FDF or >=0x2FF0 and <=0x312F or >=0x3190 and <=0x31BF or >=0x31C0 and <=0x31FF or >=0x3400 and <=0x4DBF or >=0x4E00 and <=0x9FFF or >=0xF900 and <=0xFAFF or >=0xFE10 and <=0xFE1F or >=0xFE30 and <=0xFE6F or >=0xFF65 and <=0xFF9F or >=0x20000 and <=0x2A6DF or >=0x2A700 and <=0x2EBE0 or >=0x2F800 and <=0x2FA1F or >=0x30000 and <=0x3134A or >=0x31350 and <=0x323AF;
    Element[] Parse(string value)
    {
        if(Actor.B(binding["m_parseCtrlCharacters"]))value=value.Replace("\\n","\n").Replace("\\r","\r").Replace("\\t","\t");
        if(Actor.B(binding["m_isRichText"])){
            value=value.Replace("<br>","\n",StringComparison.OrdinalIgnoreCase);
            var combined=new StringBuilder();for(int i=0;i<value.Length;i++){int end=i;string key="";while(end<value.Length&&sequencePrefixes.Contains(value[i..(end+1)])){key=value[i..++end];}if(sequences.TryGetValue(key,out string? name)){combined.Append("<sprite name=\"").Append(name).Append("\">");i=end-1;}else combined.Append(value[i]);}value=combined.ToString();
        }
        var colors=new Stack<Vector4>();int boldCount=bold?1:0;
        var sizes=new Stack<SizeTag>();float offset=0,offsetEm=0,space=0,spaceEm=0;bool noBreak=false;SizeTag? offsetSize=null;
        Element Styled(Element e){var r=e with{Size=sizes.Count>0?sizes.Peek():null,Offset=offset,OffsetEm=offsetEm,Space=space,SpaceEm=spaceEm,NoBreak=noBreak,OffsetSize=offsetSize,Bold=boldCount>0,Color=colors.Count>0?colors.Peek():null};space=spaceEm=0;return r;}
        var result=new List<Element>();for(int i=0;i<value.Length;){
            if(value[i]=='<'&&Actor.B(binding["m_isRichText"])){
                if(value.AsSpan(i).StartsWith("<b>",StringComparison.OrdinalIgnoreCase)){boldCount++;i+=3;continue;}
                if(value.AsSpan(i).StartsWith("</b>",StringComparison.OrdinalIgnoreCase)){if(!bold&&boldCount>0)boldCount--;i+=4;continue;}
                if(value.AsSpan(i).StartsWith("</color>",StringComparison.OrdinalIgnoreCase)){if(colors.Count>0)colors.Pop();i+=8;continue;}
                var colored=Regex.Match(value[i..],"^<(?:color=)?(#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)>",RegexOptions.IgnoreCase);
                if(colored.Success){string hex=colored.Groups[1].Value;float C(int at)=>Convert.ToInt32(hex.Substring(at,2),16)/255f;colors.Push(new(C(1),C(3),C(5),hex.Length==9?C(7):1));i+=colored.Length;continue;}
                var named=Regex.Match(value[i..],"^<color=\"?([a-z]+)\"?>",RegexOptions.IgnoreCase);
                if(named.Success){uint rgba=named.Groups[1].Value.ToLowerInvariant() switch{"red"=>0xff0000ff,"lightblue"=>0xadd8e6ff,"blue"=>0x0000ffff,"grey"=>0x808080ff,"black"=>0x000000ff,"green"=>0x00ff00ff,"white"=>0xffffffff,"orange"=>0xff8000ff,"purple"=>0xa020f0ff,"yellow"=>0xffeb04ff,_=>throw new NotSupportedException("TMP color: "+named.Value)};colors.Push(new((rgba>>24)/255f,((rgba>>16)&255)/255f,((rgba>>8)&255)/255f,(rgba&255)/255f));i+=named.Length;continue;}
                if(value.AsSpan(i).StartsWith("<nobr>",StringComparison.OrdinalIgnoreCase)){noBreak=true;i+=6;continue;}
                if(value.AsSpan(i).StartsWith("</nobr>",StringComparison.OrdinalIgnoreCase)){noBreak=false;i+=7;continue;}
                if(value.AsSpan(i).StartsWith("</voffset>",StringComparison.OrdinalIgnoreCase)){offset=offsetEm=0;i+=10;continue;}
                var positioned=Regex.Match(value[i..],"^<(space|voffset)=([+-]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[Ee][+-]?[0-9]+)?)(em|px)?>",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
                if(positioned.Success){float n=float.Parse(positioned.Groups[2].Value,CultureInfo.InvariantCulture);if(!float.IsFinite(n))throw new InvalidDataException("Non-finite TMP offset");bool emUnit=positioned.Groups[3].Value=="em";if(positioned.Groups[1].Value=="space"){if(emUnit)spaceEm+=n;else space+=n;}else{offset=emUnit?0:n;offsetEm=emUnit?n:0;offsetSize=sizes.Count>0?sizes.Peek():null;}i+=positioned.Length;continue;}
                if(value.AsSpan(i).StartsWith("</size>",StringComparison.OrdinalIgnoreCase)){if(sizes.Count>0)sizes.Pop();i+=7;continue;}
                var sized=Regex.Match(value[i..],"^<size=\"?([+-]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+))(%|em|px)?\"?>",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
                if(sized.Success){string raw=sized.Groups[1].Value;float n=float.Parse(raw,CultureInfo.InvariantCulture);if(!float.IsFinite(n))throw new InvalidDataException("Non-finite TMP size");sizes.Push(new(n,sized.Groups[2].Value.ToLowerInvariant(),raw[0] is '+' or '-'));i+=sized.Length;continue;}
                var match=Regex.Match(value[i..],"^<sprite name=\"([^\"]+)\">");if(!match.Success)throw new NotSupportedException("TMP rich text in prefab: "+value[i..]);
                var sprite=sprites?["characters"]?.AsArray().FirstOrDefault(s=>Archive.S(s!,"name")==match.Groups[1].Value)??throw new InvalidDataException("TMP named sprite missing");result.Add(Styled(Sprite(sprite) with{U=0xE000+(int)N(sprite["index"])}));i+=match.Length;continue;
            }
            var rune=Rune.GetRuneAt(value,i);result.Add(Styled(Resolve(rune.Value)));i+=rune.Utf16SequenceLength;
        }return result.ToArray();
    }
    Element Sprite(JsonNode character)=>new((int)N(character["unicode"]),sprites!,sprites!["glyphs"]![character["glyph"]!.ToString()]!,(int)N(character["index"]),N(character["scale"],1),true);
    Element Resolve(int u)
    {
        Element? FontGlyph(int code){
            Element? Find(JsonNode f){var c=f["characters"]?[code.ToString()];if(c is null)return null;int index=(int)N(c["glyph"]);return new(code,f,f["glyphs"]![index.ToString()]!,index,N(c["scale"],1));}
            var own=Find(font);if(own is not null)return own;
            if(code is 3 or 9 or 10 or 11 or 13 or 0x061C or 0x200B or 0x200E or 0x200F or 0x2028 or 0x2029 or 0x2060)
                return new(code,font,new JsonObject{["metrics"]=new JsonObject(),["scale"]=1},0,1);
            foreach(var fallback in font["fallbacks"]?.AsArray()??[])if(Find(fonts["fonts"]![fallback!.ToString()]!) is Element f)return f;
            return null;
        }
        var found=FontGlyph(u);if(found is not null)return found;
        if(sprites?["characters"]?.AsArray().FirstOrDefault(s=>N(s?["unicode"])==u) is JsonNode sprite)return Sprite(sprite);
        if(font["missingGlyph"] is {} missing){
            if(missing["unicode"] is not JsonValue value||!value.TryGetValue<int>(out int substitute)||missing["characters"] is not JsonArray characters)throw new InvalidDataException("Malformed TMP missingGlyph record");
            if(characters.Any(c=>N(c)==u))return FontGlyph(substitute)??throw new InvalidDataException($"TMP declared missing-glyph substitute U+{substitute:X} is absent");
        }
        throw new NotSupportedException($"TMP glyph U+{u:X} is absent from the connected font assets");
    }
    JsonNode Material(JsonNode f)=>ReferenceEquals(f,sprites)?fonts["materials"]![Archive.S(f,"material")]!:ReferenceEquals(f,font)?material:fonts["materials"]![materialName+" + "+fonts["fonts"]!.AsObject().First(p=>ReferenceEquals(p.Value,f)).Key]??throw new InvalidDataException("TMP fallback material absent");
    JsonNode Face(JsonNode f)
    {
        if(faces.TryGetValue(f,out var ready))return ready;var face=f["faceInfo"]!;
        if(Archive.S(fonts,"source")=="open"){
            string name=fonts["fonts"]!.AsObject().First(p=>ReferenceEquals(p.Value,f)).Key;
            (float Line,float Ascent,float Descent)? metrics=name switch{
                var n when n.EndsWith("(A-OTF-ShinGoPr6N-Regular SDF)")=>(2,.88f,-.12f),
                var n when n.EndsWith("(VibeMOPro-Medium SDF)")=>(1.2f,.83f,-.17f),
                var n when n.EndsWith("(FZLTH_GB18030L2_R SDF)")=>(1.155f,.892f,-.263f),
                var n when n.EndsWith("(Pretendard-SemiBold SDF)")=>(1.192871f,.95166f,-.241211f),_=>null};
            if(fontLines?.TryGetValue(name,out var role)==true)metrics=(N(role["lineHeightEm"]),N(role["ascentEm"]),N(role["descentEm"]));
            if(metrics is {} m){face=face.DeepClone();float scale=N(face["m_Scale"]);float em=N(face["m_PointSize"])/(scale==0?1:scale);face["m_LineHeight"]=m.Line*em;face["m_AscentLine"]=m.Ascent*em;face["m_DescentLine"]=m.Descent*em;}
        }
        faces[f]=face;return face;
    }
    static float Padding(JsonNode material,bool extra)
    {
        var f=material["floats"]!;float a=N(f["_ScaleRatioA"]),c=N(f["_ScaleRatioC"]),d=N(f["_FaceDilate"])*a;
        float uniform=Math.Max((N(f["_OutlineWidth"])+N(f["_OutlineSoftness"]))*a+d,d),pad=0;
        if(material["keywords"]!.AsArray().Any(k=>k?.ToString()=="UNDERLAY_ON"))pad=d+(N(f["_UnderlayDilate"])+N(f["_UnderlaySoftness"]))*c+Math.Max(Math.Abs(N(f["_UnderlayOffsetX"])*c),Math.Abs(N(f["_UnderlayOffsetY"])*c));
        return Math.Min(Math.Max(pad,uniform)+(extra?4:0),1)*N(f["_GradientScale"])+1.25f;
    }
    (float Advance,float Spacing) Adjustment(int index)
    {
        var e=elements[index];var pairs=kerning&&!e.Sprite?e.Font["glyphPairAdjustments"]:null;float advance=0,spacing=characterSpacing;
        void Take(int first,int second,string side){uint key=(uint)(first|second<<16);if(pairs?[key.ToString()] is not JsonNode pair)return;var v=pair[side]!;advance+=N(v["xAdvance"]);if(N(v["xPlacement"])!=0||N(v["yPlacement"])!=0)throw new NotSupportedException("TMP pair placement");if(((int)N(pair["flags"])&256)!=0)spacing=0;}
        if(index+1<elements.Length&&!elements[index+1].Sprite)Take(e.Index,elements[index+1].Index,"first");if(index>0&&!elements[index-1].Sprite)Take(elements[index-1].Index,e.Index,"second");return(advance,spacing);
    }
    LayoutResult Layout(float fontSize,float width,float height,bool wrap)
    {
        var chars=new List<Character>();var lines=new List<Line>();var fi=Face(font);
        float baseScale=fontSize/N(fi["m_PointSize"])*N(fi["m_Scale"],1),em=fontSize*.01f,gap=N(fi["m_LineHeight"])-(N(fi["m_AscentLine"])-N(fi["m_DescentLine"]));
        float x=0,offset=0,asc=-32767,desc=32767,maxAsc=0,minDesc=0,preferredWidth=0;int start=0,hard=-1;bool firstWord=true,overflow=false;
        void End(int end){if(end<start)return;int last=end;while(last>start&&!chars[last].Visible)last--;float advance=chars[last].Advance-chars[end].Spacing;lines.Add(new(start,end,advance,asc,desc,offset));}
        for(int i=0;i<elements.Length;i++){
            var e=elements[i];int u=e.U;var m=e.Glyph["metrics"]!;var face=e.Sprite?fi:Face(e.Font);
            float elementSize=e.Size?.Apply(fontSize)??fontSize;
            x+=e.Space+e.SpaceEm*elementSize;float baselineOffset=e.Offset+e.OffsetEm*(e.OffsetSize?.Apply(fontSize)??fontSize);
            float adjusted=elementSize/N(face["m_PointSize"])*N(face["m_Scale"],1),scale=adjusted*e.Scale*N(e.Glyph["scale"],1);
            float spriteScale=elementSize/N(fi["m_PointSize"])*N(fi["m_Scale"],1);
            if(e.Sprite)scale=spriteScale*(N(fi["m_AscentLine"])/N(m["m_Height"])*e.Scale*N(e.Glyph["scale"],1));
            var mat=Material(e.Font);float sp=e.Sprite?0:N(e.Font[e.Bold?"boldStyle":"normalStyle"])/4*N(mat["floats"]!["_GradientScale"])*N(mat["floats"]!["_ScaleRatioA"]);
            float pad=e.Sprite?0:Math.Min(Padding(mat,Actor.B(binding["m_enableExtraPadding"])),N(mat["floats"]!["_GradientScale"])-sp);
            var adj=Adjustment(i);var spacingFont=e.Sprite?font:e.Font;float spacing=((N(spacingFont["normalSpacingOffset"])+adj.Spacing)+(e.Bold&&!e.Sprite?N(spacingFont["boldSpacing"]):0))*em;
            float ea=N(face["m_AscentLine"])*scale,ed=N(face["m_DescentLine"])*scale;
            if(e.Sprite){ea=N(fi["m_AscentLine"])*spriteScale;ed=N(fi["m_DescentLine"])*spriteScale;}
            ea=Math.Max(ea,ea+baselineOffset);ed=Math.Min(ed,ed+baselineOffset);
            float textWidth=Math.Abs(x)+N(m["m_HorizontalAdvance"])*scale;
            if(Visible(u)&&textWidth>width+.0001f&&wrap&&i>start){
                int end=hard>=start?hard:i-1;bool first=firstWord;
                if(end+1<chars.Count)chars.RemoveRange(end+1,chars.Count-end-1);
                asc=chars.Skip(start).Max(c=>c.Ascender);desc=chars.Skip(start).Min(c=>c.Descender);End(end);
                offset+=(-desc+ea)+gap*baseScale+lineSpacing*em;i=end;start=end+1;x=0;asc=-32767;desc=32767;hard=-1;firstWord=true;
                if(first)overflow=true;continue;
            }
            if(i==start||!White(u)){asc=Math.Max(asc,ea);desc=Math.Min(desc,ed);}if(lines.Count==0)maxAsc=Math.Max(maxAsc,asc);
            minDesc=Math.Min(minDesc,desc-offset);
            if(Visible(u)){preferredWidth=Math.Max(preferredWidth,textWidth);if(textWidth>width+.0001f||maxAsc-minDesc>height+.0001f)overflow=true;}
            float baseline=N(face["m_Baseline"])*adjusted*N(face["m_Scale"],1);
            float gx=x+(N(m["m_HorizontalBearingX"])-pad-sp)*scale,gy=baseline+(N(m["m_HorizontalBearingY"])+pad)*scale-offset+baselineOffset;
            if(u==9){float tab=N(face["m_TabWidth"])*N(e.Font["tabSize"])*scale;float next=MathF.Ceiling(x/tab)*tab;x=next>x?next:x+tab;}
            else{x+=((N(m["m_HorizontalAdvance"])+adj.Advance)*scale+spacing);if(White(u)||u==0x200B)x+=wordSpacing*em;}
            chars.Add(new(e,scale,pad,sp,gx,gy-(N(m["m_Height"])+pad*2)*scale,(N(m["m_Width"])+pad*2+sp*2)*scale,(N(m["m_Height"])+pad*2)*scale,x,spacing,ea,ed,lines.Count,Visible(u)&&u!=9));
            if(u==13)x=0;
            if(Feed(u)||i==elements.Length-1||u==3){End(i);if(u==3)break;if(Feed(u)){offset+=(-desc+ea)+gap*baseScale+(lineSpacing+(u is 10 or 0x2029?paragraphSpacing:0))*em;start=i+1;hard=i;x=0;asc=-32767;desc=32767;firstWord=true;continue;}}
            if(wrap&&!e.NoBreak){int next=i+1<elements.Length?elements[i+1].U&65535:-1;
                if((White(u)||u is 0x200B or 0x2D)&&u is not (0xA0 or 0x2007 or 0x2011 or 0x202F or 0x2060)){if(!(u==0x2D&&i>start&&White(elements[i-1].U))){hard=i;firstWord=false;}}
                else if(Cjk(u)){if(!leading.Contains(u)){if(!following.Contains(next)){firstWord=false;hard=i;}if(firstWord)hard=i;}else if(firstWord&&i==start)hard=i;}
                else if(Cjk(next)&&!following.Contains(next))hard=i;else if(firstWord)hard=i;
            }
        }
        return new(chars,lines,preferredWidth,maxAsc-minDesc,overflow);
    }
    static float Preferred(float value,float first,float last)=>(int)(((value+Math.Max(first,0))+Math.Max(last,0))*100+1)/100f;
    public Vector2 PreferredSize(float width)
    {
        EnsureParsed();
        if(elements.Length==0)return Vector2.Zero;
        var key=(width,Wrapping);if(preferredCache.TryGetValue(key,out var cached))return cached;
        var horizontal=Layout(size,32767,32767,false);float inner=width-margin.X-margin.Z;
        var vertical=Layout(autoSize?maxSize:size,inner!=0?inner:32767,32767,Wrapping==1);
        var value=new Vector2(Preferred(horizontal.Width,margin.X,margin.Z),Preferred(vertical.Height,margin.Y,margin.W));
        if(preferredCache.Count>=8)preferredCache.Clear();preferredCache[key]=value;return value;
    }
    public IReadOnlyList<Glyph> Generate(float width,float height,Vector2 pivot)
    {
        EnsureParsed();
        var key=(width,height,pivot,Wrapping,MaxVisible);
        if(glyphCache is not null&&glyphKey==key)return glyphCache;
        var result=GenerateCore(width,height,pivot);glyphKey=key;glyphCache=result;return result;
    }
    IReadOnlyList<Glyph> GenerateCore(float width,float height,Vector2 pivot)
    {
        if(elements.Length==0){renderedLayout=null;return[];}float innerWidth=Math.Max(width-margin.X-margin.Z,0),innerHeight=Math.Max(height-margin.Y-margin.W,0);
        float fontSize=autoSize?Math.Clamp(baseSize,minSize,maxSize):size,lo=minSize,hi=maxSize;LayoutResult result;
        for(int i=0;;i++){
            result=Layout(fontSize,innerWidth,innerHeight,Wrapping==1);if(!autoSize||i>=100)break;
            if(result.Overflow&&fontSize>minSize){hi=fontSize;fontSize=Math.Max(minSize,(int)((fontSize-Math.Max((fontSize-lo)*.5f,.05f))*20+.5f)/20f);continue;}
            if(hi-lo>.051f&&fontSize<maxSize){lo=fontSize;fontSize=Math.Min(maxSize,(int)((fontSize+Math.Max((hi-fontSize)*.5f,.05f))*20+.5f)/20f);continue;}break;
        }
        renderedLayout=result;RenderedFontSize=fontSize;float asc=result.Lines.Count==0?0:result.Lines[0].Ascender,desc=result.Lines.Count==0?0:result.Lines.Min(l=>l.Descender-l.Offset);
        float left=-width*pivot.X+margin.X,bottom=-height*pivot.Y,top=bottom+height;
        float y=vAlign switch{256=>top-asc-margin.Y,512=>(bottom+top)/2-((asc+margin.Y+desc)-margin.W)/2,_=>bottom-desc+margin.W};
        var color=binding["m_fontColor"]!;var tint=new Vector4(N(color["r"]),N(color["g"]),N(color["b"]),N(color["a"]));var output=new List<Glyph>();
        for(int i=0;i<result.Characters.Count&&i<MaxVisible;i++){
            var c=result.Characters[i];if(!c.Visible)continue;var packed=c.Element.Glyph["packed"]??throw new InvalidDataException("TMP glyph atlas missing");var rect=c.Element.Glyph["rect"]!;
            var line=result.Lines[c.Line];float x=left+(hAlign switch{2=>(innerWidth+.0001f-line.Advance)/2,4=>innerWidth+.0001f-line.Advance,_=>0});
            string texture=Archive.S(packed,"texture");var page=fonts["textures"]![texture]!;float tw=N(page["width"]),th=N(page["height"]),p=c.Padding+c.StylePadding;
            var uv=new Vector4((N(rect["m_X"])-p+N(packed["dx"]))/tw,(N(rect["m_Y"])-p+N(packed["dy"]))/th,(N(rect["m_X"])+N(rect["m_Width"])+p+N(packed["dx"]))/tw,(N(rect["m_Y"])+N(rect["m_Height"])+p+N(packed["dy"]))/th);
            var glyphColor=!Actor.B(binding["m_overrideHtmlColors"])&&c.Element.Color is Vector4 rich?rich with{W=Math.Min(rich.W,tint.W)}:tint;
            output.Add(new(c.Element.Font,Material(c.Element.Font),texture,new(c.X+x,c.Y+y,c.W,c.H),uv,c.Element.Sprite?0:c.Element.Bold?-c.Scale:c.Scale,c.Element.Sprite?new(1,1,1,tint.W):glyphColor,c.Element.U,c.Line,c.Element.Sprite,!c.Element.Sprite&&!Actor.B(binding["m_overrideHtmlColors"])&&c.Element.Color is not null));
        }
        return output;
    }
}
