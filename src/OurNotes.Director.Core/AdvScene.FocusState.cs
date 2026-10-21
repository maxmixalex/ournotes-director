namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    void RemoveStageActor(Actor actor)
    {
        foreach (int position in shown.Where(pair => ReferenceEquals(pair.Value, actor)).Select(pair => pair.Key).ToArray())
        {
            shown.Remove(position);
            int index = (position - 1) / 2;
            // A slot belongs to the visible character. Leaving the slot must not
            // transfer its defocus or dimming to the next character placed there.
            // Stop the old writers as well: an unfinished DoF/fade must not restore
            // the departed character's state on a later animation tick.
            foreach (string key in new[] { "alpha" + index, "bright" + index, "slotBlur" + index })
                if (running.Remove(key, out var tween)) tween.Kill();
            fadeSerial[index]++;
            slotAlpha[index] = slotBrightness[index] = 1;
            slotBlur[index] = 0;
        }
        // Native Out retains the name/position maps for later scripted commands.
        // Future explicit DoF rows still operate on those original slot numbers.
    }
}
