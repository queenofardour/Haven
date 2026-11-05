namespace Haven.Core;

// Watch the additive animation lanes without treating idle/locomotion timelines as emotes.
public sealed class EmoteOverlayEdges
{
    private readonly ushort[] previous = new ushort[3];
    public ushort Observe(ReadOnlySpan<ushort> lanes, Func<ushort, ushort> findOneShot)
    {
        ushort emote = 0;
        for (var i = 0; i < previous.Length && i < lanes.Length; i++)
        {
            var timeline = lanes[i];
            if (timeline != previous[i] && timeline != 0)
            {
                var candidate = findOneShot(timeline);
                if (candidate != 0) emote = candidate;
            }
            previous[i] = timeline;
        }
        return emote;
    }
    public void Reset() => Array.Clear(previous);
}
