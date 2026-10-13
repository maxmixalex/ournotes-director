namespace OurNotes.Director.Core;

/// <summary>
/// A deterministic audio-volume fallback when neither MotionSync nor archived CRI
/// analysis is available. It drives opening only; it does not infer phonemes or vowels.
/// </summary>
public sealed class VoiceEnvelopeLipSync
{
    float opening;
    public float Feed(ReadOnlySpan<float> pcm,float seconds)
    {
        double squares=0;foreach(float sample in pcm)if(float.IsFinite(sample))squares+=(double)sample*sample;
        float rms=pcm.Length==0?0:(float)Math.Sqrt(squares/pcm.Length);
        float target=Math.Clamp(MathF.Sqrt(Math.Max(0,rms-.003f)*8),0,.85f);
        float smoothing=1-MathF.Exp(-Math.Max(0,seconds)/(target>opening?.024f:.075f));
        opening+=(target-opening)*smoothing;
        if(target==0&&opening<.001f)opening=0;
        return opening;
    }
    public void Reset()=>opening=0;
}
