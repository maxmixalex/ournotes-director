using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Exported Unity animation segments, not a script interpreter.
public static class AdvAnimation
{
    public static float Evaluate(JsonNode? curve,float time,float fallback=0)
    {
        if(curve is null)return fallback;
        if(curve["constant"] is JsonNode constant)return Actor.N(constant);
        if(curve["discrete"] is JsonArray steps){if(steps.Count==0)return fallback;var step=steps[0]!;foreach(var candidate in steps){if(Actor.N(candidate![0])>time)break;step=candidate;}return Actor.N(step[1]);}
        var keys=curve["keys"]?.AsArray();if(keys is null||keys.Count==0)return fallback;
        JsonNode key=keys[0]!;foreach(var candidate in keys){if(Actor.N(candidate![0])>time)break;key=candidate;}
        float x=time-Actor.N(key[0]);return ((Actor.N(key[1])*x+Actor.N(key[2]))*x+Actor.N(key[3]))*x+Actor.N(key[4]);
    }
    public static float[] Gradient(JsonNode gradient,float time)
    {
        float Sample(string channel,string times,string count)
        {
            int n=(int)Actor.N(gradient[count]);if(n==0)return 1;
            float previous=Actor.N(gradient["key0"]?[channel]),at=Actor.N(gradient[times+0])/65535;
            for(int i=1;i<n;i++){float next=Actor.N(gradient["key"+i]?[channel]),end=Actor.N(gradient[times+i])/65535;
                if(time<=at)return previous;if(time<=end){if((int)Actor.N(gradient["m_Mode"])==1)return next;return previous+(next-previous)*Math.Clamp((time-at)/(end-at),0,1);}previous=next;at=end;}
            return previous;
        }
        return [Sample("r","ctime","m_NumColorKeys"),Sample("g","ctime","m_NumColorKeys"),Sample("b","ctime","m_NumColorKeys"),Sample("a","atime","m_NumAlphaKeys")];
    }
}
