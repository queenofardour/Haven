namespace Haven.Core;

public readonly record struct FixtureChange(uint Floor, uint Part, uint FixtureId);

/// <summary>What InteriorWatch asks for when the game has stopped taking Haven's interior requests.</summary>
public enum InteriorWatchStep { None, Report, ReloadShell }

/// <summary>
/// Notices when the game's layout stops taking Haven's interior requests: wallpaper, flooring, lights or brightness
/// waiting for the layout's own housing pass (HousingLayoutDataUpdatePending, OutdoorAreaDataUpdated) for longer than
/// any load takes. A friend's home once showed the shell's defaults while the Room panel showed their choices, and a
/// brightness change did nothing until they left and came back; choosing the default (which reloads the shell) and
/// then their choice fixed it. So a stuck interior is reported once and the shell reloaded once per home.
/// </summary>
public sealed class InteriorWatch
{
    public const long StuckMilliseconds = 10_000;
    public const int MaxReloads = 1;
    private long? waitingSince;
    private bool reported;
    public int Reloads { get; private set; }
    public long WaitedMilliseconds(long now) => waitingSince is { } since ? now - since : 0;

    /// <param name="waiting">Haven has interior work queued and the layout is not taking it.</param>
    public InteriorWatchStep Observe(long now, bool waiting)
    {
        if (!waiting) { waitingSince = null; reported = false; return InteriorWatchStep.None; }
        waitingSince ??= now;
        if (reported || now - waitingSince.Value < StuckMilliseconds) return InteriorWatchStep.None;
        reported = true;
        if (Reloads >= MaxReloads) return InteriorWatchStep.Report;
        Reloads++;
        return InteriorWatchStep.ReloadShell;
    }

    /// <summary>A new scene (the shell was loaded again): the wait starts over, the reload count stays.</summary>
    public void Rebind() { waitingSince = null; reported = false; }
    /// <summary>The interior settled, or another home was entered: a later stall may reload again.</summary>
    public void Reset() { Rebind(); Reloads = 0; }
}

public static class InteriorPlan
{
    public static bool NeedsShellReset(InteriorOptions? previous, InteriorOptions next) => previous != null &&
        (previous.MainHallCeilingLight != 0 && next.MainHallCeilingLight == 0 ||
        Enumerable.Range(0, 3).Any(i => previous.Floors[i].Wallpaper != 0 && next.Floors[i].Wallpaper == 0
            || previous.Floors[i].Flooring != 0 && next.Floors[i].Flooring == 0
            || previous.Floors[i].CeilingLight != 0 && next.Floors[i].CeilingLight == 0));

    public static IEnumerable<FixtureChange> Changes(InteriorOptions? previous, InteriorOptions next)
    {
        for (uint i = 0; i < 3; i++)
        {
            var old = previous?.Floors[i] ?? new InteriorFloor(); var floor = next.Floors[i];
            foreach (var (id, was, part) in new[] { (floor.Wallpaper, old.Wallpaper, 0u), (floor.Flooring, old.Flooring, 3u), (floor.CeilingLight, old.CeilingLight, 4u) })
                if (id != 0 && id != was) yield return new(i, part, id);
        }
        if (next.MainHallCeilingLight != 0 && next.MainHallCeilingLight != (previous?.MainHallCeilingLight ?? 0))
            yield return new(3, 4, next.MainHallCeilingLight);
    }
    /// <summary>Whether two interiors ask for the same finishes and brightness (Floors is an array, which records compare by reference).</summary>
    public static bool Same(InteriorOptions? a, InteriorOptions? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Brightness != b.Brightness || a.MainHallCeilingLight != b.MainHallCeilingLight || a.Floors.Length != b.Floors.Length) return false;
        for (var i = 0; i < a.Floors.Length; i++) if (a.Floors[i] != b.Floors[i]) return false;
        return true;
    }

    /// <summary>One line for the log: brightness, then wallpaper, flooring and light per floor.</summary>
    public static string Describe(InteriorOptions options) =>
        $"brightness {(options.Brightness is { } b ? b.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "shell")}, floors "
        + string.Join(" ", options.Floors.Select(f => $"[{f.Wallpaper},{f.Flooring},{f.CeilingLight}]"))
        + (options.MainHallCeilingLight != 0 ? $", hall {options.MainHallCeilingLight}" : "");

    // The native preview buffer defaults to 1.0 (full brightness), not the housing UI's 0–5.
    public static float LightLevel(float brightness) => Math.Clamp(brightness, 0, 5) / 5f;
}
