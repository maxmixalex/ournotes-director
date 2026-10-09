using System.Numerics;
using System.Text.Json.Nodes;
using System.Globalization;

namespace OurNotes.Director.Core;

// A native instance of the exported phone prefab. Command timing and chat memory live above this canvas.
public sealed partial class AdvChatCanvas : IDisposable
{
    readonly OffscreenGl gl;readonly AdvCanvasImages images;readonly AdvCanvasTexts textRenderer;
    readonly JsonNode data,ui;readonly List<AdvCanvasNode> nodes=[];readonly Dictionary<AdvCanvasNode,AdvTmpText> texts=[];
    readonly Dictionary<string,AdvCanvasNode> original=[];readonly List<AdvCanvasNode> bubbles=[];
    readonly AdvCanvasNode canvas,view,target;AdvCanvasNode? root;JsonNode? records,windowConfig;
    AdvCanvasLayout? layout;Dictionary<AdvCanvasNode,AdvCanvasNode[]> children=[];
    int cloneIndex;string windowName="";
    readonly List<(AdvCanvasNode? Node,AdvCanvasNode? Lock,int? Count)> ownReads=[];
    readonly List<AdvCanvasNode> lockBubbles=[];
    int currentRead,lastReadApplied=-1;
    float? contentBottom;
    readonly bool initialResize;bool viewportBound;
    bool layoutDirty=true,transformsDirty=true;
    (Vector2 Position,Vector2 Size,Vector3 Scale,Quaternion Rotation) layoutPose;
    public float Width{get;}public float Height{get;}public float PlaneDistance{get;}
    public int SortingOrder{get;}
    public JsonNode ViewConfig{get;}
    public AdvCanvasNode Target=>target;
    public bool Visible{get=>root?.Active??false;set{if(root is not null&&root.Active!=value){root.Active=value;layoutDirty=true;}}}
    public int BubbleCount=>bubbles.Count;
    public int LockBubbleCount=>lockBubbles.Count;
    public int ChatId{get;private set;}
    public object Snapshot()=>JsonOutput.Object(("Visible", Visible), ("ChatId", ChatId), ("ScreenMode", ScreenMode), ("BubbleCount", BubbleCount), ("LockBubbleCount", LockBubbleCount), ("position", new[]{target.AnchoredPosition.X,target.AnchoredPosition.Y}), ("scale", new[]{target.Scale.X,target.Scale.Y,target.Scale.Z}), ("scroll", ScrollPosition), ("lockScroll", LockScrollPosition), ("reads", ownReads.Select(e=>e.Count??currentRead).ToArray()), ("typing", Node("_typingContentText") is AdvCanvasNode n&&texts.TryGetValue(n,out var text)?JsonOutput.Object(("Text", text.Text), ("MaxVisible", text.MaxVisible), ("boxHeight", n.Parent?.Height)):null));
    public JsonNode Master(int id)=>data["chats"]?[id.ToString()]??throw new InvalidDataException("Chat master absent: "+id);
    public AdvChatCanvas(Archive archive,string site,JsonNode manifest,JsonNode definition,string language,OffscreenGl gl)
    {
        this.gl=gl;data=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(definition,"chat"),language))!;
        ui=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(definition,"ui"),language))!;
        images=new(archive,site,manifest,gl);textRenderer=new(archive,site,manifest,language,gl);
        var widget=ui["chatWidget"]?["nodes"]?.AsArray()??throw new InvalidDataException("Chat widget missing");
        var cr=widget.Single(n=>n?["canvas"] is not null)!;var vr=widget.Single(n=>n?["chatView"] is not null)!;ViewConfig=vr["chatView"]!;
        string parent=Archive.S(ViewConfig,"_windowParentRect");var tr=widget.Single(n=>Archive.S(n!,"path")==parent)!;
        float rw=Actor.N(cr["canvasScaler"]?["m_ReferenceResolution"]?["x"],1920),rh=Actor.N(cr["canvasScaler"]?["m_ReferenceResolution"]?["y"],1080),scale=Math.Min(gl.Width/rw,gl.Height/rh);
        Width=gl.Width/scale;Height=gl.Height/scale;PlaneDistance=Actor.N(cr["canvas"]?["m_PlaneDistance"],1);
        SortingOrder=(int)Actor.N(cr["canvas"]?["m_SortingOrder"],10000);
        float initialScale=Math.Min(2340/rw,1080/rh);initialResize=Width!=2340/initialScale||Height!=1080/initialScale;
        canvas=new(cr,null){Scale=Vector3.One,AnchorMin=Vector2.Zero,AnchorMax=Vector2.Zero,Pivot=Vector2.Zero,AnchoredPosition=Vector2.Zero,SizeDelta=new(Width,Height)};
        view=new(vr,canvas);target=new(tr,view);defaultPosition=target.AnchoredPosition;nodes.AddRange([canvas,view,target]);
    }
    static string Ref(JsonNode? r)=>r?["gameObject"]?.ToString()??r?["transform"]?.ToString()??"";
    AdvCanvasNode? Node(string field)=>original.GetValueOrDefault(Ref(windowConfig?[field]));
    void SetText(AdvCanvasNode? node,string value){if(node is not null&&texts.TryGetValue(node,out var text)&&text.Text!=value){text.Text=value;layoutDirty=true;}}
    public void SetText(string field,string value)=>SetText(Node(field),value);
    public bool Select(int chatId)
    {
        var master=data["chats"]?[chatId.ToString()]??throw new InvalidDataException("Chat master absent: "+chatId);
        string name=Archive.S(master,"_chatWindowAssetName");if(ChatId==chatId&&root is not null)return false;
        records=data["windows"]?[name]?["nodes"]??throw new InvalidDataException("Chat window absent: "+name);windowName=name;ChatId=chatId;
        nodes.RemoveRange(3,nodes.Count-3);original.Clear();texts.Clear();bubbles.Clear();cloneIndex=0;contentBottom=null;
        foreach(var rec in records.AsArray().OfType<JsonNode>()){
            string path=Archive.S(rec,"path"),parent=path.Contains('/')?path[..path.LastIndexOf('/')]:"";
            var n=new AdvCanvasNode(rec.DeepClone(),original.GetValueOrDefault(parent)??target);nodes.Add(n);original[path]=n;
            if(textRenderer.Fonts["chatTexts"]?[name]?[path] is JsonNode binding)texts[n]=new(textRenderer.Fonts,binding);
        }
        root=nodes[3];root.AnchoredPosition=Vector2.Zero;root.Active=true;windowConfig=root.Component("AdvChatWindow")??throw new InvalidDataException("AdvChatWindow component absent");
        baseScale=target.Scale=new(Height/root.SizeDelta.Y,Height/root.SizeDelta.Y,1);
        var pos=windowConfig["_windowPosition"];target.AnchoredPosition=new(Actor.N(pos?["x"]),Actor.N(pos?["y"]));
        var rotation=windowConfig["_windowRotation"];if(Actor.N(rotation?["x"])!=0||Actor.N(rotation?["y"])!=0)throw new NotSupportedException("Chat window rotation outside canvas plane");
        target.Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,Actor.N(rotation?["z"])*MathF.PI/180);
        foreach(string field in new[]{"_incomingCallStatusText","_lockScreenStatusText"}){var n=Node(field);if(n is not null)SetText(n,ui["chatStatusTexts"]?[Archive.S(windowConfig,field+"Key")]?.ToString()??throw new InvalidDataException("Chat status text absent"));}
        Clear();SetScreen(0);return true;
    }
    public void Clear()
    {
        foreach(var n in nodes.Where(n=>n.Component("AdvMyChatNode") is not null||n.Component("AdvOtherChatNode") is not null))n.Active=false;
        bubbles.Clear();lockBubbles.Clear();ownReads.Clear();currentRead=0;lastReadApplied=-1;Rebuild();SetTyping("",int.MaxValue,false);
    }
    void Rebuild(){children=nodes.ToDictionary(n=>n,n=>nodes.Where(c=>c.Parent==n).ToArray());layout=new(nodes,n=>texts.TryGetValue(n,out var t)&&t.Enabled?t.PreferredSize(n.Width):null);layoutDirty=true;}
    public void Layout()
    {
        if(layout is null)return;var pose=(target.AnchoredPosition,target.SizeDelta,target.Scale,target.Rotation);
        if(layoutDirty||transformsDirty||pose!=layoutPose){
            // ContentSizeFitter and wrapping can settle over successive canvas rebuilds.
            // Cache only a rebuild that no longer changes any rect or derived transform.
            var before=nodes.Select(n=>(n.AnchorMin,n.AnchorMax,n.AnchoredPosition,n.SizeDelta,n.Width,n.Height,n.Matrix)).ToArray();
            layout.Layout(Width,Height);layoutDirty=false;
            for(int i=0;i<nodes.Count;i++){var n=nodes[i];var old=before[i];if(old.AnchorMin!=n.AnchorMin||old.AnchorMax!=n.AnchorMax||old.AnchoredPosition!=n.AnchoredPosition||old.SizeDelta!=n.SizeDelta||old.Width!=n.Width||old.Height!=n.Height||!old.Matrix.AsSpan().SequenceEqual(n.Matrix)){layoutDirty=true;break;}}
        }
        layoutPose=pose;transformsDirty=false;
    }
    public void SetScreen(int mode)
    {
        if(mode is not (0 or 1 or 2))throw new ArgumentOutOfRangeException(nameof(mode));
        if(mode==2&&(ScreenMode!=2||!Visible))ResetLock();ScreenMode=mode;layoutDirty=true;
        string[] fields=["_normalScreenObject","_incomingCallObject","_lockScreenObject"];for(int i=0;i<fields.Length;i++)if(Node(fields[i]) is AdvCanvasNode node)node.Active=i==mode;
    }
    public void SetBattery(int percent)
    {
        SetText("_batteryPercentageText",percent+(Actor.B(windowConfig?["_showBatteryPercentSymbol"])?"%":""));
        if(Node("_batteryFillImage")?.Image is JsonNode image)image["m_FillAmount"]=Math.Clamp(percent/100f,0,1);
    }
    public void SetWindowName(string name,int members)
    {
        name=name.Normalize();var elements=StringInfo.GetTextElementEnumerator(name);int width=0;
        while(elements.MoveNext()){string element=elements.GetTextElement();int c=element[0],w=c==0xA0||c is >=0x20 and <0x7F or >=0xFF61 and <0xFFA0?1:2;if(width+w>12){name=name[..elements.ElementIndex].TrimEnd()+"...";break;}width+=w;}
        if(name!=""&&Actor.B(windowConfig?["_isGroupChat"])&&Actor.N(windowConfig?["_chatLayoutMode"])==0&&members is >=2 and <=99)name+="("+members+")";
        SetText("_windowNameText",name);
    }
    public void Send(int sender,string message,string name,int readCount=0,string readText="",string? stamp=null,bool restoring=false)
    {
        bool other=sender!=ChatId;AdvCanvasNode? mainRead=null,lockRead=null;bool main=!(restoring&&ScreenMode==2),locked=ScreenMode==2&&(restoring||Node("_lockChatNodeParent")?.ActiveInHierarchy==true);
        if(main)mainRead=MakeBubble(sender,message,name,stamp,false);
        if(locked)lockRead=MakeBubble(sender,message,name,stamp,true);
        if(!other&&(main||locked))ownReads.Add((mainRead,lockRead,readCount>=1?readCount:null));
        SetRead(!other?readCount:0,readText);Rebuild();if(!other)SetTyping("",int.MaxValue,false);
    }
    AdvCanvasNode? MakeBubble(int sender,string message,string name,string? stamp,bool locked)
    {
        bool other=sender!=ChatId;var parent=Node(locked?"_lockChatNodeParent":"_chatNodeParent");if(parent is null){if(locked)return null;throw new InvalidDataException("Chat content absent");}
        var template=locked?original.Values.FirstOrDefault(n=>n.Path.StartsWith(parent.Path+"/",StringComparison.Ordinal)&&n.Component(other?"AdvOtherChatNode":"AdvMyChatNode") is not null):Node(other?"_otherChatNodePrefab":"_myChatNodePrefab");
        if(template is null){if(locked)return null;throw new InvalidDataException("Chat node template absent");}
        var copy=new Dictionary<string,AdvCanvasNode>();string prefix=parent.Path+"/Bubble"+(++cloneIndex);
        foreach(var source in records!.AsArray().OfType<JsonNode>().Where(n=>Archive.S(n,"path")==template.Path||Archive.S(n,"path").StartsWith(template.Path+"/",StringComparison.Ordinal))){
            string path=Archive.S(source,"path");var rec=source.DeepClone();rec["path"]=prefix+path[template.Path.Length..];
            var n=new AdvCanvasNode(rec,path==template.Path?parent:copy[path[..path.LastIndexOf('/')]]);copy[path]=n;nodes.Add(n);
            if(textRenderer.Fonts["chatTexts"]?[windowName]?[path] is JsonNode binding)texts[n]=new(textRenderer.Fonts,binding);
        }
        var bubble=copy[template.Path];bubble.Active=true;(locked?lockBubbles:bubbles).Add(bubble);var config=bubble.Component(other?"AdvOtherChatNode":"AdvMyChatNode")!;
        AdvCanvasNode? Part(string field)=>copy.GetValueOrDefault(Ref(config[field]));
        bool identity=Actor.N(windowConfig!["_chatLayoutMode"])==0||Actor.N(windowConfig["_chatLayoutMode"])==1&&Actor.B(windowConfig["_isGroupChat"]);
        SetText(Part("_senderNameText"),name);var nameNode=Part("_senderNameRootObject")??Part("_senderNameText");if(nameNode is not null)nameNode.Active=identity;
        var icon=Part("_chatIconImage");var iconNode=Part("_chatIconRootObject")??icon?.Parent??icon;if(iconNode is not null)iconNode.Active=identity;
        string iconName=Archive.S(Master(sender),"_chatIconAssetName");var iconSprite=data["icons"]?[iconName];if(iconName.Length>0&&iconSprite is null)throw new InvalidDataException("Chat icon absent: "+iconName);if(icon?.Image is JsonNode iconImage)iconImage["m_Sprite"]=iconSprite?.DeepClone();
        SetText(Part("_talkText"),message);if(Part("_textBox") is AdvCanvasNode box)box.Active=stamp is null;
        if(Part("_stampImage") is AdvCanvasNode stampNode){stampNode.Active=stamp is not null;if(stamp is not null&&stampNode.Image is JsonNode im)im["m_Sprite"]=data["stamps"]?[stamp]?.DeepClone()??throw new InvalidDataException("Chat stamp absent: "+stamp);}
        return Part("_readText");
    }
    void ResetLock()
    {
        foreach(var n in lockBubbles)n.Active=false;lockBubbles.Clear();layoutDirty=true;
        for(int i=0;i<ownReads.Count;i++){var e=ownReads[i];ownReads[i]=(e.Node,null,e.Count);}
        LockScrollPosition=0;
    }
    public int SetTyping(string value,int visible,bool scroll=true)
    {
        var n=Node("_typingContentText");if(n is null||!texts.TryGetValue(n,out var text))return value.Length;
        if(text.Text!=value){text.Text=value;layoutDirty=true;}text.MaxVisible=visible;Layout();
        if(n.Parent is AdvCanvasNode box){
            var le=box.Component("LayoutElement");if(le is null){le=new JsonObject{["class"]="LayoutElement",["m_Enabled"]=1,["m_MinWidth"]=-1,["m_MinHeight"]=-1,["m_PreferredWidth"]=-1,["m_PreferredHeight"]=-1,["m_FlexibleWidth"]=-1,["m_FlexibleHeight"]=-1,["m_LayoutPriority"]=1};box.Record["components"]!.AsArray().Add(le);}
            float min=Actor.N(le["m_MinHeight"]);if(min<0){min=box.Height;if(min<=0)min=Actor.N(ViewConfig["_typingTextBoxMinHeight"],63);le["m_MinHeight"]=min;}
            if(n.ActiveInHierarchy)text.Generate(n.Width,n.Height,n.Pivot);
            var pad=box.Component("VerticalLayoutGroup")?["m_Padding"];
            float preferred=Math.Max(min,Actor.N(pad?["m_Top"])+Actor.N(pad?["m_Bottom"])+text.VisibleTextHeight+text.Margin.Y+text.Margin.W);
            if(Actor.N(le["m_PreferredHeight"])!=preferred){le["m_PreferredHeight"]=preferred;layoutDirty=true;}
        }
        Layout();ApplyTypingPadding();Layout();if(scroll)ScrollToBottom();return text.CharacterCount;
    }
    void ApplyTypingPadding()
    {
        var padding=Node("_chatNodeParent")?.Component("VerticalLayoutGroup")?["m_Padding"];if(padding is null)return;
        contentBottom??=Actor.N(padding["m_Bottom"]);var indicator=Node("_typingIndicatorBottomRect");float height=0;
        if(indicator?.ActiveInHierarchy==true){height=layout?.PreferredSize(indicator,1)??0;if(height<=0)height=indicator.Height;}
        float bottom=contentBottom.Value+(int)Math.Max(height,0);if(Actor.N(padding["m_Bottom"])!=bottom){padding["m_Bottom"]=bottom;layoutDirty=true;}
    }
    public void SetRead(int count,string label)
    {
        if(count>0){int limit=Math.Min(lastReadApplied+1,ownReads.Count);for(int i=0;i<ownReads.Count;i++){var e=ownReads[i];ownReads[i]=(e.Node,e.Lock,Math.Max(e.Count??(i<limit?currentRead:0),count));}currentRead=count;lastReadApplied=ownReads.Count-1;}
        foreach(var e in ownReads)foreach(var node in new[]{e.Node,e.Lock}){if(node is null)continue;int value=e.Count??currentRead;node.Alpha=value>0&&Actor.N(windowConfig?["_chatLayoutMode"])!=1?1:0;SetText(node,Actor.B(windowConfig?["_isGroupChat"])?(label.Length>0?label+" ":"")+value:label);}
    }
    (float Min,float ViewMin,float Size,float ViewSize) Bounds(bool locked)
    {
        Layout();var content=Node(locked?"_lockChatNodeParent":"_chatNodeParent");var viewport=locked?original.GetValueOrDefault(Ref(Node("_lockScrollRect")?.Component("ScrollRect")?["m_Viewport"])):Node("_viewport");if(content is null||viewport is null)return default;
        var m=viewport.Matrix;float det=m[0]*m[5]-m[1]*m[4],c=-m[1]/det,d=m[0]/det,ty=-c*m[12]-d*m[13];
        float left=-content.Pivot.X*content.Width,bottom=-content.Pivot.Y*content.Height;
        var ys=new[]{new Vector2(left,bottom),new(left+content.Width,bottom),new(left+content.Width,bottom+content.Height),new(left,bottom+content.Height)}.Select(p=>{var q=AdvMatrix.Transform(content.Matrix,new(p,0,1));return c*q.X+d*q.Y+ty;}).ToArray();
        float min=ys.Min(),max=ys.Max(),size=max-min,center=(min+max)*.5f,excess=viewport.Height-size;if(excess>0){center-=excess*(content.Pivot.Y-.5f);size=viewport.Height;}
        return(center-size*.5f,-viewport.Pivot.Y*viewport.Height,size,viewport.Height);
    }
    float GetScroll(bool locked){var b=Bounds(locked);return b.Size<=b.ViewSize?b.ViewMin>b.Min?1:0:(b.ViewMin-b.Min)/(b.Size-b.ViewSize);}
    void SetScroll(bool locked,float value){var b=Bounds(locked);var content=Node(locked?"_lockChatNodeParent":"_chatNodeParent");if(content is null)return;float y=content.AnchoredPosition.Y,next=y+b.ViewMin-value*(b.Size-b.ViewSize)-b.Min;if(Math.Abs(y-next)>.01f){content.AnchoredPosition=content.AnchoredPosition with{Y=next};transformsDirty=true;}}
    public float ScrollPosition{get=>GetScroll(false);set=>SetScroll(false,value);}
    public float LockScrollPosition{get=>GetScroll(true);set=>SetScroll(true,value);}
    public bool CanScroll=>Node("_scrollRect")?.ActiveInHierarchy==true;
    public bool CanScrollLock=>Node("_lockScrollRect")?.ActiveInHierarchy==true;
    public void ScrollToBottom(){Layout();if(CanScroll){ApplyTypingPadding();ScrollPosition=0;}if(CanScrollLock)LockScrollPosition=0;Layout();}
    public void Draw(AdvCamera camera)
    {
        if(!Visible)return;AdvanceRenderState();Layout();var globals=AdvCanvasTexts.Globals(Width,Height,gl.Width,gl.Height,camera.Fov,camera.Near,camera.Far,PlaneDistance);
        void Walk(AdvCanvasNode n,AdvCanvasNode? soft){if(!n.ActiveInHierarchy||n.InheritedAlpha<=0)return;
            bool masking=n.Image is not null&&Actor.B(n.Component("Mask")?["m_Enabled"]);
            void Content(){
                if(n.Image is not null&&(!masking||Actor.B(n.Component("Mask")?["m_ShowMaskGraphic"])))images.Image(n,Width,Height,Actor.B(n.Component("SoftMaskable")?["m_Enabled"])?soft:null);
                if(texts.TryGetValue(n,out var text))textRenderer.Draw(n,text,globals.Values,globals.WorldScale);
                var nextSoft=Actor.B(n.Component("SoftMask")?["m_Enabled"])?n:soft;foreach(var c in children[n])Walk(c,nextSoft);
            }
            if(masking)gl.WithStencilMask(()=>images.Image(n,Width,Height),Content);else Content();
        }
        Walk(canvas,null);
    }
    // The reference prefab starts at its 2340x1080 design viewport. Its first visible draw
    // invokes OnResolutionChanged, including during a pending slide. Preserve that frame state when seeking.
    public void AdvanceRenderState()
    {
        if(viewportBound||!Visible)return;viewportBound=true;
        if(initialResize){var transform=ModeTransform();target.AnchoredPosition=transform.Position;target.Scale=transform.Scale;}
    }
    public void Dispose(){textRenderer.Dispose();images.Dispose();}
}
