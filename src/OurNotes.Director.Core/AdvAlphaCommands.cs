namespace OurNotes.Director.Core;

// AdvAlphaCommand: no layers fades both together; explicit layers are awaited in list order.
public static class AdvAlphaCommands
{
    public static void Bind(AdvPlayback player,AdvLoop loop,Func<float,float,AdvWait> still,Func<float,float,AdvWait> video)
    {
        player.Register("Alpha",(row,p)=>Execute(row,p));
        IEnumerable<AdvWait> Execute(AdvRow row,AdvPlayback p){yield return p.NoWait(row,Fade(row,p));}
        IEnumerable<AdvWait> Fade(AdvRow row,AdvPlayback p)
        {
            float alpha=Math.Clamp(row.Number("Parameter1"),0,1),duration=p.CalcDuration(row.Number("Duration"));
            var layers=row.Data["CanvasLayers"]?.AsArray();
            if(layers is null||layers.Count==0){yield return loop.All(still(alpha,duration),video(alpha,duration));yield break;}
            foreach(var layer in layers){
                if(p.Cancelled)yield break;
                int index=(int)Actor.N(layer);
                if(index==6)yield return video(alpha,duration);
                else if(index==7)yield return still(alpha,duration);
            }
        }
    }
}
