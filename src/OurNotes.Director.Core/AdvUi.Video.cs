namespace OurNotes.Director.Core;

public sealed partial class AdvUi
{
    string subtitles="";
    public Func<bool>? VideoPlaying{get;set;}
    public void ClearVideoText(){subtitles="";talkVisible=false;StopVoices();UpdateBackdrop();}
    IEnumerable<AdvWait> Subtitles(AdvRow r,AdvPlayback p)
    {
        string id=r.Text("AdvTextID");if(id is "" or "0"){subtitles="";yield break;}
        subtitles=Localize(id);if((r.Data["VoiceIDs"]?.AsArray().Count??0)>0)PlayVoices(r);
        if(r.Flag("IsNoWait"))yield break;
        if(VideoPlaying?.Invoke()==true){do{yield return loop.YieldUpdate();}while(VideoPlaying()&&!p.Cancelled);foreach(var w in p.DelayAdjusted(p.CalcDuration(Actor.N(settings["_waitSubtitlesLingeringTimeOnAutoPlay"]))))yield return w;}
        else {float duration=Math.Max(Actor.N(settings["_minTalkDisplayTime"]),Actor.N(settings["_waitTalkTextUnitTime"])*TextLength(subtitles));foreach(var w in p.DelayAdjusted(p.CalcDuration(duration)))yield return w;}
        StopVoices();
    }
}
