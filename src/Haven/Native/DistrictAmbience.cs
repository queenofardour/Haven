using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace HavenNative;

/// <summary>
/// A home's district ambience in the weather heard outside (DistrictWeather). The game plays the home's district set in
/// fair weather (housing interiors only have fair skies; a real Shirogane house plays the coast's wind and gulls too), so
/// while a weather is chosen each fair-weather loop the game plays is stopped and its version for that weather, from the
/// same set, plays in its place (or nothing, where the set has none). Each loop's place ("slot", day and night alike) is
/// then Haven's for the visit: when the weather is cleared it plays the fair-weather loop itself, and when the game moves
/// to the night's loop it plays the night's version. Nothing is touched until a weather is first chosen. Played as the
/// game plays weather sounds (HousingAmbientSound). A loop the game keeps starting again is left alone after a few tries.
/// </summary>
internal sealed unsafe class DistrictAmbience(IPluginLog log)
{
    private sealed class Slot(IPluginLog log)
    {
        public readonly HousingAmbientSound Player = new(log);
        public string Fine = "";
        public string? Playing;
    }

    private readonly Dictionary<string, Slot> slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Since, int Count)> stops = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> givenUp = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> set = [];
    private uint setTerritory;
    private string? weather;
    private long nextCheck;

    /// <summary>
    /// Keeps the district's loops in the weather heard outside; while a home is shown. <paramref name="sounds"/> gives a
    /// territory's district set; <paramref name="own"/> is the outside sound, never touched.
    /// </summary>
    public void Update(uint territory, string? key, Func<uint, IReadOnlyList<string>> sounds, nint own)
    {
        var tick = Environment.TickCount64;
        if (tick < nextCheck) return;
        nextCheck = tick + 250;
        if (territory != setTerritory) { Release(); setTerritory = territory; set = sounds(territory); }
        if (key != weather)
        {
            weather = key;
            foreach (var slot in slots.Values) Play(slot);
        }
        // Until a weather is first chosen the game's own ambience is left as it is.
        if (key == null && slots.Count == 0) return;
        var manager = SoundManager.Instance();
        if (manager == null) return;
        var ours = slots.Values.Select(s => s.Player.Handle).Where(h => h != 0).ToHashSet();
        List<(nint Sound, string File)>? found = null;
        var count = 0;
        for (var sound = manager->ActiveSoundDataListHead; sound != null && count++ < 512; sound = (SoundData*)sound->Next)
        {
            if (!sound->IsActive || (nint)sound == own || ours.Contains((nint)sound) || !sound->IsPlaying()) continue;
            var file = sound->GetFileName().ToString().Replace('\\', '/');
            if (!Haven.Core.DistrictWeather.FairWeather(file) || givenUp.Contains(Haven.Core.DistrictWeather.Slot(file))) continue;
            (found ??= []).Add(((nint)sound, file));
        }
        // Stopped after the walk: a stop may change the list.
        foreach (var (handle, file) in found ?? [])
        {
            var name = Haven.Core.DistrictWeather.Slot(file);
            var times = stops.TryGetValue(name, out var was) && tick - was.Since < 60_000 ? (was.Since, was.Count + 1) : (tick, 1);
            stops[name] = times;
            if (times.Item2 > 5)
            {
                givenUp.Add(name);
                if (slots.Remove(name, out var lost)) lost.Player.Stop();
                log.Warning($"[ATMOSPHERE] the game keeps starting {file} again; leaving the district's {name} as it is.");
                continue;
            }
            try { ((SoundData*)handle)->Stop(500); }
            catch (Exception ex) { log.Warning(ex, $"[ATMOSPHERE] {file} could not be stopped."); continue; }
            if (!slots.TryGetValue(name, out var slot)) slots[name] = slot = new Slot(log);
            if (slot.Fine != file) { slot.Fine = file; slot.Playing = null; }
            Play(slot);
        }
        // A loop of Haven's that stopped by itself is started again.
        foreach (var slot in slots.Values)
            if (slot.Playing != null && !slot.Player.Alive) { slot.Playing = null; Play(slot); }
    }

    private void Play(Slot slot)
    {
        var want = Haven.Core.DistrictWeather.Variant(slot.Fine, weather, set);
        if (want == slot.Playing && (want == null || slot.Player.Alive)) return;
        slot.Player.Stop();
        slot.Playing = null;
        if (want == null) { log.Information($"[ATMOSPHERE] the district's {Short(slot.Fine)} is quiet in {weather}."); return; }
        if (!slot.Player.Play(want, 1)) { log.Warning($"[ATMOSPHERE] the district's {Short(want)} could not start."); return; }
        slot.Playing = want;
        log.Information($"[ATMOSPHERE] the district's {Short(slot.Fine)} plays as {Short(want)}.");
    }

    private static string Short(string file) => file[(file.LastIndexOf('/') + 1)..];

    /// <summary>Stops Haven's district loops; on leaving the home, a scene change and unload.</summary>
    public void Release()
    {
        foreach (var slot in slots.Values) slot.Player.Stop();
        if (slots.Count > 0) log.Information($"[ATMOSPHERE] stopped the district's {slots.Count} loop(s).");
        slots.Clear(); stops.Clear(); givenUp.Clear();
        weather = null; setTerritory = 0; set = [];
    }
}
