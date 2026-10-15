namespace OurNotes.Director.Core;

/// <summary>Owns scheduled voices by speaker, not by the last instruction globally.
/// Multiple speakers/narration can overlap. Replacing a speaker stops only that speaker's
/// preceding take; cleanup from an older take cannot detach a newer mouth driver.</summary>
public sealed class VoiceTrackPlayback(AdvAudio audio)
{
    readonly Dictionary<string, AdvSound[]> owners = new(StringComparer.Ordinal);

    public AdvSound[] Start(string[] speakers, long[] cueIds, string narrationId,
        Action<string, AdvSound>? attachLipSync = null)
    {
        ArgumentNullException.ThrowIfNull(speakers);
        ArgumentNullException.ThrowIfNull(cueIds);
        var keys = speakers.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0) keys = ["\0narration:" + narrationId];
        // Resolve every cue before replacing an existing voice. A missing resource must
        // not silence an unrelated, already-playing take.
        foreach (long id in cueIds) audio.GetCue(id);
        foreach (string key in keys)
            if (owners.Remove(key, out var previous))
                foreach (var voice in previous) audio.Stop(voice.Id);
        var started = cueIds.Select(id => audio.Play(id)).ToArray();
        if (started.Length == 0) return started;
        foreach (string key in keys) owners[key] = started;
        for (int i = 0; i < speakers.Length; i++)
            if (!string.IsNullOrWhiteSpace(speakers[i])) attachLipSync?.Invoke(speakers[i], started[Math.Min(i, started.Length - 1)]);
        foreach (var sound in started) sound.OnFinished.Add(() =>
        {
            if (started.Any(s => audio.IsPlaying(s.Id))) return;
            foreach (string key in keys)
                if (owners.TryGetValue(key, out var current) && ReferenceEquals(current, started)) owners.Remove(key);
        });
        return started;
    }
}
