using System.Globalization;

namespace OurNotes.Director.Core;

public sealed partial class AdvUi
{
    VoiceTrackPlayback? scheduledVoices;

    IEnumerable<AdvWait> ClipVoice(AdvRow input, AdvPlayback playback)
    {
        var row = ResolveExternalVoice(input);
        string separator = Archive.S(settings, "_targetNameSplitKey", ",");
        var speakers = row.Text("TargetName").Split(separator.Length == 0 ? "," : separator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = row.Data["VoiceIDs"]?.AsArray().Select(id => long.Parse(id!.ToString(), CultureInfo.InvariantCulture)).ToArray() ?? [];
        scheduledVoices ??= new(audio);
        var started = scheduledVoices.Start(speakers, ids, row.Index, row.Flag("IgnoreLipSync") ? null : (name, sound) =>
        {
            if (ResolveCharacter?.Invoke(name) is Actor actor) AttachVoiceLipSync(actor, sound);
        });
        while (!playback.Cancelled && started.Any(sound => audio.IsPlaying(sound.Id))) yield return loop.YieldUpdate();
    }
}
