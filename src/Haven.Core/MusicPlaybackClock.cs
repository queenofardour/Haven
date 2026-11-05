namespace Haven.Core;

public sealed class MusicPlaybackClock
{
    private MusicOptions? queue;
    private long delay;
    private long? waitingSince;

    // The native orchestrion queues resource loading. Do not spend song time until
    // its first playing frame; neither a late join nor a slow load should trim it.
    public void AwaitPlayback(long now) => waitingSince = now;
    public void ObservePlayback(long now, bool playing)
    {
        if (waitingSince is not {} previous) return;
        delay += Math.Max(0, now - previous);
        waitingSince = playing ? null : now;
    }
    public void RestartTrack(MusicSlot slot, IReadOnlyList<long> durations)
    {
        if (!slot.Silent) delay += Math.Max(0, durations[slot.PlaylistIndex] - (long)Math.Round(slot.RemainingSeconds * 1000));
    }

    public MusicSlot? Locate(MusicOptions options, long now, IReadOnlyList<long> durations, bool beginPlayback = false)
    {
        if (!options.Playing) { Reset(); return null; }
        var same = queue != null && queue.StartedAtUnixMs == options.StartedAtUnixMs && queue.GapSeconds == options.GapSeconds
            && queue.Loop == options.Loop && queue.Shuffle == options.Shuffle && queue.ShuffleSeed == options.ShuffleSeed
            && (ReferenceEquals(queue.Playlist, options.Playlist) || queue.Playlist.SequenceEqual(options.Playlist));
        if (!same)
        {
            var shared = MusicSchedule.Locate(options, now, durations);
            if (!beginPlayback) return shared;
            queue = options;
            waitingSince = null;
            // Native orchestrion playback starts at the beginning. A visitor or
            // re-entry must hear that complete track, rather than cutting it off
            // at a shared boundary reached before its local audio has finished.
            delay = shared is { Silent: false } slot
                ? durations[slot.PlaylistIndex] - (long)Math.Round(slot.RemainingSeconds * 1000) : 0;
        }
        return MusicSchedule.Locate(options, now - delay, durations);
    }

    /// <summary>
    /// Takes on edited options while the track that is playing carries on: the local clock is set so that track has
    /// been playing for playedMs, however the shared schedule was re-timed (MusicSchedule.Rebase).
    /// </summary>
    public void Continue(MusicOptions options, long now, IReadOnlyList<long> durations, long playedMs)
    {
        if (MusicSchedule.Locate(options, now, durations) is not { Silent: false } shared) return;
        queue = options;
        waitingSince = null;
        delay = durations[shared.PlaylistIndex] - (long)Math.Round(shared.RemainingSeconds * 1000) - playedMs;
    }

    public void Reset() { queue = null; delay = 0; waitingSince = null; }
}

public enum MusicStreamState { Missing, Pending, Playing, Ended }
public readonly record struct MusicPlaybackKey(int Index, long Cycle, int Repetition, bool Silent, long StartedAt, uint Roll);

public static class MusicPlaybackPolicy
{
    /// <summary>
    /// Whether the file a playing orchestrion sound loaded is the one Haven asked for. A game track loads under its own
    /// path. A custom song (music/haven/…) is served by Penumbra from a file on this PC, and the game's resource then
    /// names that file instead ("|prefix|C:\…\content\music\…\hash.l2.scd"), which still carries the song's
    /// fingerprint. 0.34.0 and 0.35.0 compared paths exactly, so Haven never recognised a custom song as its own:
    /// it could neither stop it nor play anything after it, and kept asking the game to start it again.
    /// </summary>
    public static bool SameFile(string requested, string loaded)
    {
        if (loaded == requested) return true;
        // A game track a Penumbra mod replaces loads from the mod's folder, usually under the same game path
        // ("|default_1|C:\…\SomeMod\music\ffxiv\…\BGM_ORCH_001.scd"); 0.52.2 and earlier didn't recognise it, so
        // the stream was never stopped and later crashed the game's sound thread.
        var normal = loaded.Replace('\\', '/');
        if (normal.EndsWith("/" + requested, StringComparison.OrdinalIgnoreCase)) return true;
        const string custom = "music/haven/";
        if (!requested.StartsWith(custom, StringComparison.Ordinal) || requested.Length < custom.Length + 32) return false;
        var fingerprint = requested.Substring(custom.Length, 32);
        return fingerprint.All(Uri.IsHexDigit) && loaded.Contains(fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Continuous(MusicOptions music, bool hasNativeLoop) => music.Playing && music.Loop
        && music.Playlist.Length == 1 && music.GapSeconds == 0 && hasNativeLoop;
    public static bool NeedsRecovery(MusicStreamState state) => state is MusicStreamState.Missing or MusicStreamState.Ended;
    public static MusicPlaybackKey Key(MusicOptions music, MusicSlot? slot, bool hasNativeLoop)
    {
        if (slot is not {} current) return new(-1, 0, 0, true, music.StartedAtUnixMs, 0);
        var continuous = Continuous(music, hasNativeLoop);
        return new(current.PlaylistIndex, continuous ? 0 : current.Cycle, continuous ? 0 : current.Repetition,
            current.Silent, music.StartedAtUnixMs, TrackKey(music.Playlist[current.PlaylistIndex]));
    }
    // A game track by its row; a custom song by its fingerprint, in the upper half so the two never meet.
    public static uint TrackKey(MusicTrack track) => track.CustomMusic is { Length: >= 8 } hash
        ? 0x80000000u | Convert.ToUInt32(hash[..8], 16) & 0x7FFFFFFFu : track.OrchestrionId;
}
