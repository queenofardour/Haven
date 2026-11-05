using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;

namespace HavenNative;

// A home's weather and time of day (InteriorOptions.Weather/TimeOfDay, moods). Both are client-only: the time is held
// with the time freeze (TimeFreezeService, Brio's method) and the weather written to the environment, only a weather
// the room's level holds (writing one it doesn't can crash the resource loader; MapSettingsService.ApplyWeather).
// Re-applied once a second, as the engine's own map-state replay can release them after a load, but never in GPose,
// so photo tools keep their say; released as soon as the home is left.
public sealed partial class NativeEngine
{
    private bool atmosphereTime;      // the time freeze is Haven's
    private byte atmosphereWeather;   // a weather Haven set, so clearing the choice puts the room's own back
    private long atmosphereAt;

    /// <summary>The weathers this room's level holds (id, name), from the live environment; empty while it loads.</summary>
    public unsafe IReadOnlyList<(uint Id, string Name)> HousingWeathers(uint territory)
    {
        var list = new List<(uint, string)>();
        if (!HousingZoneReady(territory)) return list;
        var env = EnvManager.Instance();
        if (env == null || env->EnvScene == null) return list;
        var table = (byte*)env->EnvScene + 0x30;
        var sheet = dataManager.GetExcelSheet<Weather>();
        for (var i = 0; i < 32; i++)
        {
            var id = table[i];
            if (id == 0 || list.Any(x => x.Item1 == id)) continue;
            var name = sheet.TryGetRow(id, out var row) ? row.Name.ToString() : "";
            list.Add((id, name.Length > 0 ? name : "Weather " + id));
        }
        return list;
    }

    /// <summary>The Eorzean time of day now, in minutes after midnight.</summary>
    public unsafe int HousingTimeOfDay
    {
        get
        {
            var fw = Framework.Instance();
            if (fw == null) return 12 * 60;
            var seconds = ((fw->ClientTime.EorzeaTime % 86400) + 86400) % 86400;
            return (int)(seconds / 60);
        }
    }

    /// <summary>Holds the home's weather and time; call every frame while the home is shown.</summary>
    public unsafe void HousingHoldAtmosphere(uint territory, uint? weather, int? time, bool now = false)
    {
        if (!HousingZoneReady(territory) || clientState.IsGPosing) return;
        var tick = Environment.TickCount64;
        if (!now && tick < atmosphereAt) return;
        atmosphereAt = tick + 1000;
        if (time is int minutes)
        {
            if (!timeFreeze.Available) { }
            else if (!timeFreeze.IsFrozen || Math.Abs(HousingTimeOfDay - minutes) > 1 || !atmosphereTime)
            {
                timeFreeze.FreezeAt(minutes / 60, minutes % 60);
                if (!atmosphereTime) log.Information($"[ATMOSPHERE] holding the time at {Haven.Core.HomeMoods.Clock(minutes)}.");
                atmosphereTime = true;
            }
        }
        else if (atmosphereTime) { timeFreeze.Unfreeze(); atmosphereTime = false; log.Information("[ATMOSPHERE] the time follows Eorzea's clock again."); }

        var env = EnvManager.Instance();
        if (env == null || env->EnvScene == null) return;
        var table = (byte*)env->EnvScene + 0x30;
        bool Holds(uint id) { for (var i = 0; i < 32; i++) if (table[i] == id) return true; return false; }
        if (weather is uint chosen && chosen is > 0 and <= 255 && Holds(chosen))
        {
            if (env->ActiveWeather != chosen)
            {
                env->ActiveWeather = (byte)chosen;
                if (atmosphereWeather != chosen) log.Information($"[ATMOSPHERE] weather {chosen} (territory {territory}).");
            }
            atmosphereWeather = (byte)chosen;
        }
        else if (atmosphereWeather != 0)
        {
            // The choice was cleared (or the room can't show it): the room's own first weather comes back.
            var natural = table[0];
            if (natural != 0) env->ActiveWeather = natural;
            atmosphereWeather = 0;
        }
    }

    /// <summary>Lets the time flow, forgets the weather Haven set and stops the weather heard outside; on leaving a home.</summary>
    public void HousingReleaseAtmosphere()
    {
        if (atmosphereTime) { timeFreeze.Unfreeze(); atmosphereTime = false; log.Information("[ATMOSPHERE] released the time on leaving the home."); }
        atmosphereWeather = 0; atmosphereAt = 0;
        StopOutside();
        district.Release();
    }

    // Weather heard outside (HomeAmbience): the game's own outdoor loop, played as the game plays its weather sounds
    // (HousingAmbientSound: the system sound player, not the orchestrion, whose second request in 0.64.0/0.64.1 never
    // started and cost the home its music), not positioned, at this PC's volume. Stopped on leaving and on unload.
    private HousingAmbientSound? outsideSound;
    private DistrictAmbience? districtAmbience;
    private DistrictAmbience district => districtAmbience ??= new DistrictAmbience(log);
    private readonly Dictionary<uint, IReadOnlyList<string>> districtSets = [];

    // The district sound set a territory's level names (DistrictWeather), read once per territory.
    private IReadOnlyList<string> DistrictSounds(uint territory)
    {
        if (districtSets.TryGetValue(territory, out var known)) return known;
        IReadOnlyList<string> found = [];
        try
        {
            if (dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row) && !row.Bg.IsEmpty
                && dataManager.GetFile("bg/" + row.Bg.ToString() + ".lvb")?.Data is { } lvb && Haven.Core.DistrictWeather.SetOf(lvb) is { } essb
                && dataManager.GetFile(essb)?.Data is { } set)
                found = Haven.Core.DistrictWeather.SoundsIn(set);
            log.Information($"[ATMOSPHERE] territory {territory}'s district set holds {found.Count} sound(s).");
        }
        catch (Exception ex) { log.Warning(ex, $"[ATMOSPHERE] territory {territory}'s district set could not be read."); }
        return districtSets[territory] = found;
    }
    private string? outsideSource, outsideWanted;
    private long outsideCheckAt, outsideWantedAt, outsideRestartsSince;
    private int outsideRestarts;
    private float outsideVolumeSet = -1;
    public bool HousingOutsideAvailable => true;
    public float HousingOutsideVolume
    {
        get => Math.Clamp(config.OutsideVolume, 0, 1);
        set { config.OutsideVolume = Math.Clamp(value, 0, 1); config.Save(); }
    }

    /// <summary>Plays (or keeps playing) a HomeAmbience sound for the home's time of day; null stops it.</summary>
    public void HousingHoldOutside(uint territory, string? key, int? time)
    {
        // The district's own ambience follows the weather heard outside, whatever its volume here.
        if (HousingZoneReady(territory))
            district.Update(territory, key != null && Haven.Core.HomeAmbience.Find(key) != null ? key : null, DistrictSounds, outsideSound?.Handle ?? 0);
        if (!HousingZoneReady(territory) || key == null || Haven.Core.HomeAmbience.Find(key) is not { } sound || HousingOutsideVolume <= 0) { StopOutside(); return; }
        var player = outsideSound ??= new HousingAmbientSound(log);
        var source = Haven.Core.HomeAmbience.PathFor(sound, time ?? HousingTimeOfDay);
        var tick = Environment.TickCount64;
        // A time dragged across 6 o'clock flips between the day's and the night's loop: a loop already playing changes
        // only once the choice has held for a second.
        if (source != outsideWanted) { outsideWanted = source; outsideWantedAt = tick; }
        var settled = outsideSource == null || source == outsideSource || tick - outsideWantedAt >= 1000;
        if (settled && (source != outsideSource || tick >= outsideCheckAt && !player.Alive))
        {
            // A file this install lacks is never asked for.
            if (!dataManager.FileExists(source)) { StopOutside(); return; }
            var again = source == outsideSource;
            if (again)
            {
                // The loop plays on by itself: one that keeps stopping is left off for a while, not started over and over.
                if (tick - outsideRestartsSince > 60_000) { outsideRestartsSince = tick; outsideRestarts = 0; }
                if (++outsideRestarts > 3)
                {
                    log.Warning("[ATMOSPHERE] the outside sound keeps stopping; leaving it off for five minutes.");
                    player.Stop(); outsideCheckAt = tick + 300_000;
                    return;
                }
                log.Information($"[ATMOSPHERE] started {sound.Name} outside again (it had stopped).");
            }
            outsideCheckAt = tick + 2000;
            if (!player.Play(source, HousingOutsideVolume)) { log.Warning($"[ATMOSPHERE] the outside sound {source} could not start."); outsideSource = source; outsideCheckAt = tick + 30_000; return; }
            if (!again) log.Information($"[ATMOSPHERE] playing {sound.Name} outside ({source}).");
            outsideSource = source; outsideVolumeSet = HousingOutsideVolume;
        }
        if (outsideSource != null && outsideVolumeSet != HousingOutsideVolume) { player.SetVolume(HousingOutsideVolume); outsideVolumeSet = HousingOutsideVolume; }
    }

    private void StopOutside()
    {
        if (outsideSource == null) return;
        outsideSound?.Stop();
        log.Information("[ATMOSPHERE] stopped the outside sound.");
        outsideSource = null; outsideVolumeSet = -1;
    }
}
