namespace OurNotes.Director.Core;

public sealed partial class AdvUi
{
    readonly Lazy<CriLipsData?> criData;
    readonly Dictionary<int,CriVoiceLipSync> criVoices=[];
    void AttachVoiceLipSync(Actor actor,AdvSound voice,bool forceCri=false)
    {
        CriVoiceLipSync? fallback=null;
        if((forceCri||!actor.HasMotionSync)&&criData.Value is CriLipsData data){
            if(!criVoices.TryGetValue(voice.Id,out fallback)){criVoices[voice.Id]=fallback=new(data,audio,voice);voice.OnFinished.Add(()=>criVoices.Remove(voice.Id));}
        }
        actor.StartVoiceLipSync(audio,voice,fallback,forceCri);
    }
    int[] PlayTalkVoices(AdvRow row,AdvPlayback player)
    {
        string split=Archive.S(settings,"_targetNameSplitKey");var names=row.Text("TargetName").Split(split==""?",":split,StringSplitOptions.RemoveEmptyEntries);
        string mode=row.Text("Parameter1").Trim().ToLowerInvariant();bool ignore=row.Flag("IgnoreLipSync");
        if(mode is "airlipsync" or "airlipsync_holdopen"){
            StopVoices();float multiplier=row.Number("Parameter2");if(multiplier==0)multiplier=1;
            if(!ignore)foreach(string name in names)ResolveCharacter?.Invoke(name)?.StartTimedPseudoLipSync(TextLength(text),player.Speed,multiplier,mode=="airlipsync_holdopen");
            return [];
        }
        bool everyone=names.Length>0&&mode=="everyonelipsync"&&(row.Data["VoiceIDs"]?.AsArray().Count??0)>0;
        var result=PlayVoices(row,!everyone);
        if(!ignore&&result.Length>0){var first=audio.Playing.First(s=>s.Id==result[0]);
            if(everyone){foreach(var actor in ShowingCharacters?.Invoke()??[])AttachVoiceLipSync(actor,first,true);}
            else if(names.Length>result.Length)foreach(string name in names.Skip(result.Length))if(ResolveCharacter?.Invoke(name) is Actor actor)AttachVoiceLipSync(actor,first,true);
        }
        if(result.Length==0&&!ignore)foreach(string name in names)ResolveCharacter?.Invoke(name)?.StartTimedPseudoLipSync(TextLength(text),player.Speed);
        return result;
    }
}
