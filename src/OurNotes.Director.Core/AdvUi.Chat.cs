using System.Globalization;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvUi
{
    public AdvChatCanvas? Chat{get;}
    public JsonNode? MasterIds{get;set;}
    readonly Dictionary<int,List<ChatEntry>> chatMemory=[];
    sealed record ChatEntry(int Sender,string Text,string Name,int? ReadCount,string? Stamp=null);
    readonly Dictionary<(int Memory,int Sender),(int Current,int Applied)> chatReads=[];
    int? currentChatMemory;int currentChatId,chatGeneration;
    AdvTween? chatSlide,chatScroll,chatLockScroll,chatTransition;
    static int? Memory(AdvRow r)=>int.TryParse(r.Text("Parameter3"),NumberStyles.Integer,CultureInfo.InvariantCulture,out int n)&&n>0?n:null;
    string ChatName(AdvRow r)=>r.Data["TargetTextIDs"]?.AsArray().FirstOrDefault() is JsonNode n?Localize(n.ToString()):"";
    string ReadText=>Localize(Archive.S(MasterIds??new JsonObject(),"_chatReadTextId"));
    void SendChat(ChatEntry entry,bool restoring=false)=>Chat!.Send(entry.Sender,entry.Text,entry.Name,entry.ReadCount??0,ReadText,entry.Stamp,restoring);
    void ReadMemory(int memory,int sender,int count)
    {
        if(sender<=0||count<=0)return;
        if(!chatMemory.TryGetValue(memory,out var list))chatMemory[memory]=list=[];
        var state=chatReads.GetValueOrDefault((memory,sender));int own=0;
        for(int i=0;i<list.Count;i++){var e=list[i];if(e.Sender!=sender)continue;list[i]=e with{ReadCount=Math.Max(e.ReadCount??(own<state.Applied?state.Current:0),count)};own++;}
        chatReads[(memory,sender)]=(count,own);
    }
    bool StoreChat(AdvRow r,ChatEntry entry,int count)
    {
        int? memory=Memory(r)??(Chat!.Visible?currentChatMemory:null);
        if(memory is int mem){if(!chatMemory.TryGetValue(mem,out var list))chatMemory[mem]=list=[];list.Add(entry);ReadMemory(mem,entry.Sender,count);if(!Chat!.Visible||currentChatMemory!=mem)return false;}
        return currentChatId!=0;
    }
    void ScrollChat(AdvPlayback p)
    {
        float duration=p.CalcDuration(Actor.N(Chat!.ViewConfig["_scrollDuration"],.2f));
        chatScroll?.Kill();if(Chat.CanScroll)chatScroll=uiTweens.To(()=>Chat.ScrollPosition,0,duration,AdvEase.OutQuad,v=>Chat.ScrollPosition=v);
        chatLockScroll?.Kill();if(Chat.CanScrollLock)chatLockScroll=uiTweens.To(()=>Chat.LockScrollPosition,0,duration,AdvEase.OutQuad,v=>Chat.LockScrollPosition=v);
    }
    IEnumerable<AdvWait> ChatWait(AdvRow r,AdvPlayback p,int length,int[] sounds)
    {
        if(r.Flag("IsNoWait"))yield break;
        do{yield return loop.YieldUpdate();}while(typing&&!p.Cancelled);float start=loop.Time;
        if(sounds.Length>0){foreach(int id in sounds){do{yield return loop.YieldUpdate();}while(audio.IsPlaying(id)&&!p.Cancelled);}foreach(var wait in p.DelayAdjusted(p.CalcDuration(Actor.N(settings["_waitAfterVoiceTime"]))))yield return wait;}
        else foreach(var wait in p.DelayAdjusted(p.CalcDuration(Actor.N(settings["_waitTalkTextUnitTime"])*length)))yield return wait;
        foreach(var wait in p.DelayAdjusted(p.CalcDuration(Actor.N(settings["_minTalkDisplayTime"]))-(loop.Time-start)))yield return wait;
        yield return loop.YieldUpdate();
    }
    IEnumerable<AdvWait> ChatWindow(AdvRow r,AdvPlayback p){yield return p.NoWait(r,ShowChatWindow(r,p));}
    IEnumerable<AdvWait> ShowChatWindow(AdvRow r,AdvPlayback p)
    {
        var chat=Chat!;int mode=(int)r.Number("Parameter2");if(mode is not (1 or 2))mode=0;
        int id=(int)r.Number("TargetChatID");var memory=Memory(r);bool wasVisible=chat.Visible;bool changed=chat.Select(id),same=!changed&&currentChatId==id&&currentChatMemory==memory;
        int generation=++chatGeneration;chatSlide?.Kill();chatScroll?.Kill();chatLockScroll?.Kill();chatTransition?.Kill();var master=chat.Master(id);
        bool close=wasVisible&&same&&chat.ScreenMode==mode;
        if(changed||!wasVisible||close)audio.PlayChatSe(master["_inOutSoundId"]?.GetValue<long>()??0);
        if(close){
            if(chat.ScreenMode==1){chat.SetScreen(0);var normal=chat.ModeTransform();chat.Target.AnchoredPosition=normal.Position;chat.Target.Scale=normal.Scale;}
            chatSlide=uiTweens.To(()=>chat.Target.AnchoredPosition.Y,-chat.Height,p.CalcDuration(Actor.N(chat.ViewConfig["_hideEaseDuration"],.2f)),(AdvEase)(int)Actor.N(chat.ViewConfig["_hideEase"],18),v=>chat.Target.AnchoredPosition=chat.Target.AnchoredPosition with{Y=v});
            yield return chatSlide.Completed;if(generation==chatGeneration)chat.Visible=false;yield break;
        }
        int previousMode=chat.ScreenMode;currentChatId=id;currentChatMemory=memory;chat.SetScreen(mode);chat.SetModeName(ChatName(r),(int)r.Number("Parameter4"));chat.SetBattery((int)r.Number("Parameter1"));
        var transform=chat.ModeTransform();
        if(wasVisible&&!changed&&previousMode!=mode){var startPos=chat.Target.AnchoredPosition;var startScale=chat.Target.Scale;float progress=0;
            chatTransition=uiTweens.To(()=>progress,1,p.CalcDuration(Actor.N(chat.ViewConfig["_screenModeTransitionDuration"],.18f)),(AdvEase)(int)Actor.N(chat.ViewConfig["_screenModeTransitionEase"],9),v=>{progress=v;float eased=AdvTweens.Evaluate(AdvEase.OutQuad,v,1);chat.Target.AnchoredPosition=System.Numerics.Vector2.Lerp(startPos,transform.Position,eased);chat.Target.Scale=System.Numerics.Vector3.Lerp(startScale,transform.Scale,eased);});
        }else{chat.Target.AnchoredPosition=transform.Position;chat.Target.Scale=transform.Scale;}
        if(changed||!same||!wasVisible){chat.Clear();if(memory is int mem&&chatMemory.TryGetValue(mem,out var entries))foreach(var entry in entries)SendChat(entry,true);chat.ScrollToBottom();}
        if(changed||!wasVisible){
            float end=transform.Position.Y;
            chat.Target.AnchoredPosition=chat.Target.AnchoredPosition with{Y=-chat.Height};chat.Visible=true;
            if(chat.BubbleCount>0||chat.LockBubbleCount>0){yield return loop.YieldUpdate();if(generation!=chatGeneration)yield break;chat.ScrollToBottom();}
            chatSlide=uiTweens.To(()=>chat.Target.AnchoredPosition.Y,end,p.CalcDuration(Actor.N(chat.ViewConfig["_showEaseDuration"],.3f)),(AdvEase)(int)Actor.N(chat.ViewConfig["_showEase"],18),v=>chat.Target.AnchoredPosition=chat.Target.AnchoredPosition with{Y=v});yield return chatSlide.Completed;
        }
        if(generation!=chatGeneration)yield break;foreach(var wait in ChatWait(r,p,0,[]))yield return wait;
    }
    IEnumerable<AdvWait> ChatTalk(AdvRow r,AdvPlayback p)
    {
        var chat=Chat!;string line=Localize(r.Text("AdvTextID"));int count=(int)r.Number("Parameter1");var entry=new ChatEntry((int)r.Number("TargetChatID"),line,ChatName(r),null);
        if(!StoreChat(r,entry,count))yield break;SendChat(entry with{ReadCount=count});ScrollChat(p);
        audio.PlayChatSe(chat.Master(currentChatId)["_chatSoundId"]?.GetValue<long>()??0);
        if((r.Data["VoiceIDs"]?.AsArray().Count??0)>0&&audio.LastBgmOrSeFrame==loop.Frame)yield return loop.DelayFrames(2);
        int[] sounds=[];try{if((r.Data["VoiceIDs"]?.AsArray().Count??0)>0)sounds=PlayVoices(r);foreach(var wait in ChatWait(r,p,Plain(line).Length,sounds))yield return wait;}
        finally{StopVoices();}
    }
    IEnumerable<AdvWait> ChatStamp(AdvRow r,AdvPlayback p)
    {
        int count=(int)r.Number("Parameter1");var entry=new ChatEntry((int)r.Number("TargetChatID"),"",ChatName(r),null,r.Text("TargetAssetName"));
        if(!StoreChat(r,entry,count))yield break;SendChat(entry with{ReadCount=count});ScrollChat(p);
        audio.PlayChatSe(Chat!.Master(currentChatId)["_chatSoundId"]?.GetValue<long>()??0);
        foreach(var wait in ChatWait(r,p,0,[]))yield return wait;
    }
    IEnumerable<AdvWait> ChatRead(AdvRow r,AdvPlayback p)
    {
        var chat=Chat!;int count=(int)r.Number("Parameter1");int? memory=Memory(r)??(chat.Visible?currentChatMemory:null);
        if(memory is int mem){int sender=(int)r.Number("TargetChatID");if(sender<=0&&chat.Visible)sender=currentChatId;ReadMemory(mem,sender,count);if(!chat.Visible||currentChatMemory!=mem)yield break;}
        chat.SetRead(count,ReadText);foreach(var wait in ChatWait(r,p,0,[]))yield return wait;
    }
    IEnumerable<AdvWait> ChatTyping(AdvRow r,AdvPlayback p)
    {
        if(currentChatId==0)yield break;
        string value=Localize(r.Text("AdvTextID")),plain=Plain(value);var chat=Chat!;int total=chat.SetTyping(value,0,false);
        for(int i=0;i<total&&!p.Cancelled;i++){
            chat.SetTyping(value,i+1);yield return loop.YieldUpdate();if(p.Cancelled)break;
            char c=i<plain.Length?plain[i]:'\0';if(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')continue;
            yield return loop.Delay(p.CalcDuration(Actor.N(chat.ViewConfig["_typingDelay"],.03f)));
        }
        chat.SetTyping(value,value.Length==0?int.MaxValue:total);foreach(var wait in ChatWait(r,p,total,[]))yield return wait;
    }
}
