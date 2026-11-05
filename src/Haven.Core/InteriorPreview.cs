namespace Haven.Core;

// The native remodel API previews one fixture at a time. Keep each command
// isolated until its first pass and follow-up have been consumed by the layout.
public sealed class InteriorPreview
{
    private readonly Dictionary<(uint Floor, uint Part), FixtureChange> pending = [];
    private FixtureChange? replay;
    // Every requested fixture sent once more after the interior settled (Refresh); not counted as Pending.
    private readonly Queue<FixtureChange> refresh = new();
    private bool refreshSent;
    public InteriorOptions? Requested { get; private set; }
    public bool Pending => pending.Count != 0;
    /// <summary>Every requested fixture has been taken by the game at least once since the scene was bound.</summary>
    public bool Settled { get; private set; }
    /// <summary>A refresh is still sending fixtures.</summary>
    public bool Refreshing => refresh.Count != 0;

    /// <summary>
    /// Sends every requested fixture once more. A game whose assets weren't loaded yet (the first entry after starting
    /// the game, on a slower PC) can drop a fixture taken while another was still loading; by now they're cached. It
    /// never makes the interior Pending again, so the scene stays shown.
    /// </summary>
    public void Refresh()
    {
        refresh.Clear(); refreshSent = false;
        if (Requested != null) foreach (var change in InteriorPlan.Changes(null, Requested)) refresh.Enqueue(change);
    }

    public bool Update(InteriorOptions options, bool refreshLight, bool nativeReady,
        Action<FixtureChange> setFixture, Action setLight)
    {
        var changes = InteriorPlan.Changes(Requested, options).ToArray();
        foreach (var change in changes)
            pending[(change.Floor, change.Part)] = change;
        if (changes.Length > 0) { refresh.Clear(); refreshSent = false; }
        Requested = options;
        if (replay is {} old && (!pending.TryGetValue((old.Floor, old.Part), out var latest) || latest != old)) replay = null;

        // Never replace a command which the game has not consumed. A GPose
        // brightness refresh can still update its level without replacing it,
        // but only once the interior has settled: until then the layout is
        // still in its own first housing pass, and flagging it for an update
        // then risks the shell's defaults staying on show.
        if (!nativeReady)
        {
            if (refreshLight && Settled) { setLight(); return true; }
            return false;
        }
        var next = replay ?? (pending.Count == 0 ? (FixtureChange?)null : pending.Values.First());
        if (next is {} fixture)
        {
            setFixture(fixture);
            setLight();
            // Native failures leave the queued choice/phase available to retry.
            if (replay != null) { pending.Remove((fixture.Floor, fixture.Part)); replay = null; }
            else replay = fixture;
            if (pending.Count == 0 && replay == null) Settled = true;
            return true;
        }
        Settled = true;
        if (refresh.TryPeek(out var again))
        {
            // Each fixture twice, as the first application does: its pass, then its follow-up.
            setFixture(again);
            setLight();
            if (refreshSent) { refresh.Dequeue(); refreshSent = false; } else refreshSent = true;
            return true;
        }
        if (refreshLight) { setLight(); return true; }
        return false;
    }

    public void Clear() { pending.Clear(); replay = null; Requested = null; Settled = false; refresh.Clear(); refreshSent = false; }
}
