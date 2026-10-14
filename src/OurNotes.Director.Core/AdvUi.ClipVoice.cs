using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvUi
{
    readonly Dictionary<string, int[]> clipVoices = new(StringComparer.Ordinal);

    // Authored clips own their audio independently. Starting one actor's line must not
    // stop a second actor or narration. A new line for the SAME actor replaces that actor.
    IEnumerable<AdvWait> ClipVoice(AdvRow input, AdvPlayback playback)
    {
        var row = ResolveExternalVoice(input);
        string actorKey = row.Text("TargetName");
        string key = actorKey.Length > 0 ? actorKey : "narration:" + row.Index;
        if (clipVoices.Remove(key, out var previous)) foreach (int id in previous) audio.Stop(id);
        var ids = new List<int>();
        foreach (var id in row.Data["VoiceIDs"]?.AsArray() ?? new JsonArray())
        {
            var sound = audio.Play(id!.GetValue<long>(), crossFade: 0);
            ids.Add(sound.Id);
            if (!row.Flag("IgnoreLipSync") && ResolveCharacter?.Invoke(actorKey) is Actor actor) AttachVoiceLipSync(actor, sound);
        }
        var owned = ids.ToArray(); clipVoices[key] = owned;
        while (!playback.Cancelled && ids.Any(audio.IsPlaying)) yield return loop.YieldUpdate();
        if (clipVoices.TryGetValue(key, out var current) && ReferenceEquals(current, owned)) clipVoices.Remove(key);
    }
}
