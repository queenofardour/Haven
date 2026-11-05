using System.Text.Json.Serialization;

namespace Haven.Core;

/// <summary>
/// A saved mood: the room's brightness, weather and time of day, and optionally its music, under a name ("Evening",
/// "Rainy afternoon"). Applying one sets them all at once; the home carries its moods (InteriorOptions.Moods), so a
/// gathering's decorators see and use them too.
/// </summary>
public sealed record HomeMood
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Mood";
    /// <summary>The house brightness (0-5); null is the room's original lighting.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public float? Brightness { get; init; }
    /// <summary>The weather (a Weather row); null keeps the room's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public uint? Weather { get; init; }
    /// <summary>The time of day in Eorzean minutes after midnight; null follows Eorzea's clock.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? TimeOfDay { get; init; }
    /// <summary>The weather heard outside (HomeAmbience), or null for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Outside { get; init; }
    /// <summary>The music it plays, or null to leave the music as it is.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public MusicOptions? Music { get; init; }
}

public static class HomeMoods
{
    public const int MaxMoods = 12, MaxName = 40, MinutesPerDay = 24 * 60;

    /// <summary>The room's atmosphere now, under a name; with its music when <paramref name="music"/> is set.</summary>
    public static HomeMood Capture(House house, string name, bool music) => new()
    {
        Name = name.Trim(),
        Brightness = house.Interior.Brightness,
        Weather = house.Interior.Weather,
        TimeOfDay = house.Interior.TimeOfDay,
        Outside = house.Interior.Outside,
        Music = music ? house.Music with { StartedAtUnixMs = 0 } : null,
    };

    /// <summary>The room with a mood's brightness, weather and time of day (its music is set apart, by whoever may change it).</summary>
    public static InteriorOptions Apply(InteriorOptions interior, HomeMood mood) =>
        interior with { Brightness = mood.Brightness, Weather = mood.Weather, TimeOfDay = mood.TimeOfDay, Outside = mood.Outside };

    /// <summary>Whether the room shows this mood now (its music, when it has some, playing the same playlist).</summary>
    public static bool Showing(House house, HomeMood mood) =>
        house.Interior.Brightness == mood.Brightness && house.Interior.Weather == mood.Weather && house.Interior.TimeOfDay == mood.TimeOfDay
        && house.Interior.Outside == mood.Outside && (mood.Music is not { } music || SameMusic(house.Music, music));

    public static bool SameMusic(MusicOptions a, MusicOptions b) =>
        a.Playing == b.Playing && a.Shuffle == b.Shuffle && a.Loop == b.Loop && a.GapSeconds == b.GapSeconds && a.Playlist.Length == b.Playlist.Length
        && a.Playlist.Zip(b.Playlist).All(p => p.First.OrchestrionId == p.Second.OrchestrionId && p.First.CustomMusic == p.Second.CustomMusic && p.First.Repeats == p.Second.Repeats);

    /// <summary>A name not taken by another of the home's moods ("Evening (2)").</summary>
    public static string UniqueName(IEnumerable<HomeMood> moods, string name, Guid? except = null)
    {
        name = name.Trim();
        if (name.Length > MaxName) name = name[..MaxName].TrimEnd();
        var taken = moods.Where(m => m.Id != except).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            var suffix = $" ({i})";
            var candidate = (name.Length + suffix.Length > MaxName ? name[..(MaxName - suffix.Length)].TrimEnd() : name) + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>"6:30 pm"-style Eorzean time.</summary>
    public static string Clock(int minutes)
    {
        minutes = ((minutes % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;
        var hour = minutes / 60; var minute = minutes % 60;
        return $"{(hour % 12 == 0 ? 12 : hour % 12)}:{minute:00} {(hour < 12 ? "am" : "pm")}";
    }

    internal static bool Valid(InteriorOptions interior, Func<MusicOptions, bool> validMusic)
    {
        if (interior.Weather is { } weather && weather is 0 or > 255) return false;
        if (interior.TimeOfDay is { } time && time is < 0 or >= MinutesPerDay) return false;
        if (interior.Outside is { } outside && HomeAmbience.Find(outside) == null) return false;
        if (interior.Moods is not { } moods) return true;
        // A null entry or name from a hostile home is refused here, not left to fail as a null reference below.
        if (moods.Length > MaxMoods || moods.Any(m => m == null || m.Name == null) || moods.Select(m => m.Id).Distinct().Count() != moods.Length
            || moods.Select(m => m.Name.ToLowerInvariant()).Distinct().Count() != moods.Length) return false;
        foreach (var m in moods)
            if (m.Id == Guid.Empty || string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > MaxName || m.Name.Any(char.IsControl) || m.Name != m.Name.Trim()
                || m.Brightness is float b && (!float.IsFinite(b) || b is < 0 or > 5)
                || m.Weather is { } w && w is 0 or > 255
                || m.TimeOfDay is { } t && t is < 0 or >= MinutesPerDay
                || m.Outside is { } o && HomeAmbience.Find(o) == null
                || m.Music is { } music && !validMusic(music))
                return false;
        return true;
    }
}

/// <summary>
/// Weather heard from inside: the game's own outdoor weather loops (all in the base game's data, so every install has
/// them), a day and a night take each. Housing interiors hold only Fair Skies, so this is how a home gets its rain.
/// </summary>
public static class HomeAmbience
{
    public sealed record Sound(string Key, string Name, string Day, string Night);
    public static readonly Sound[] All =
    [
        new("rain", "Rain", "bgcommon/sound/sea/sea_bg_sea_d_rain_town_loop.scd", "bgcommon/sound/sea/sea_bg_sea_n_rain_town_loop.scd"),
        new("drizzle", "Dripping rain", "bgcommon/sound/dra/dra_bg_rain_drop_less_loop.scd", "bgcommon/sound/dra/dra_bg_rain_drop_less_loop.scd"),
        new("rainstorm", "Rainstorm", "bgcommon/sound/wil/wil_bg_wil_d_rainstorm_calm_loop.scd", "bgcommon/sound/wil/wil_bg_wil_n_rainstorm_calm_loop.scd"),
        new("thunderstorm", "Thunderstorm", "bgcommon/sound/sea/sea_bg_sea_d_thndstorm_inland_loop.scd", "bgcommon/sound/sea/sea_bg_sea_n_thndstorm_inland_loop.scd"),
        new("wind", "Wind", "bgcommon/sound/sea/sea_bg_sea_d_windstorm_coast_loop.scd", "bgcommon/sound/sea/sea_bg_sea_n_windstorm_coast_loop.scd"),
        new("snow", "Snow", "bgcommon/sound/roc/roc_bg_roc_d_snow_loop.scd", "bgcommon/sound/roc/roc_bg_roc_n_snow_loop.scd"),
        new("blizzard", "Blizzard", "bgcommon/sound/roc/roc_bg_roc_d_blizzard_loop.scd", "bgcommon/sound/roc/roc_bg_roc_n_blizzard_loop.scd"),
    ];
    public static Sound? Find(string key) => All.FirstOrDefault(s => s.Key == key);
    /// <summary>The loop for a time of day (Eorzean minutes): the day's from 6 am to 6 pm.</summary>
    public static string PathFor(Sound sound, int minutes) => minutes is >= 6 * 60 and < 18 * 60 ? sound.Day : sound.Night;
}

/// <summary>
/// A home's district ambience in the weather heard outside. Every housing interior names its district's outdoor sound
/// set (a Shirogane house the coast's: wind and gulls), which real homes play on the Ambient Sounds channel too. A set
/// holds each loop in every weather it knows, named alike ("sea_bg_sea_d_fine_coast_loop", "…_d_rain_coast_loop",
/// "est_wether_coast_n_thunder_spot"), and housing interiors only ever have fair skies, so with a weather chosen the
/// fair-weather loops are swapped for that weather's.
/// </summary>
public static class DistrictWeather
{
    /// <summary>A district loop that plays in fair weather: a background or weather spot named "_fine_".</summary>
    public static bool FairWeather(string file)
    {
        var name = Name(file);
        return name.Contains("_fine_", StringComparison.OrdinalIgnoreCase)
            && (name.Contains("_bg_", StringComparison.OrdinalIgnoreCase) || name.Contains("wether", StringComparison.OrdinalIgnoreCase));
    }

    // The words a set's files use for each weather heard outside (HomeAmbience keys), the closest first.
    private static string[] Words(string key) => key switch
    {
        "rain" or "drizzle" => ["rain"],
        "rainstorm" => ["rainstorm", "rain"],
        "thunderstorm" => ["thndstorm", "thunder", "rainstorm", "rain"],
        "wind" => ["wind", "windstorm"],
        "snow" => ["snow"],
        "blizzard" => ["blizzard", "snow"],
        _ => [],
    };

    /// <summary>
    /// The district's version of a fair-weather loop for a weather heard outside, from the set's own sounds: the same
    /// kind (loop or spot) and time of day (or one for day and night, "_dn_"), naming the weather, and sharing most of
    /// the rest of its name ("coast", "calm"). The loop itself with no weather; null to stay quiet when the set has none
    /// (the Empyreum's has no rain, Shirogane's no snow).
    /// </summary>
    public static string? Variant(string file, string? key, IReadOnlyList<string> set)
    {
        if (key == null) return file;
        var mine = Tokens(file);
        if (mine.Length < 3) return null;
        var kind = mine[^1];
        var time = mine.FirstOrDefault(t => t is "d" or "n");
        var rest = mine.Where(t => t != "fine" && t != time && t != kind).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var word in Words(key))
        {
            string? best = null; var score = -1;
            foreach (var other in set)
            {
                var theirs = Tokens(other);
                if (theirs.Length < 3 || !string.Equals(theirs[^1], kind, StringComparison.OrdinalIgnoreCase)) continue;
                if (!theirs.Contains(word, StringComparer.OrdinalIgnoreCase)) continue;
                var theirTime = theirs.FirstOrDefault(t => t is "d" or "n" or "dn");
                if (time != null && theirTime != null && theirTime != time && theirTime != "dn") continue;
                var shared = theirs.Count(rest.Contains);
                if (shared > score) { best = other; score = shared; }
            }
            if (best != null) return best;
        }
        return null;
    }

    /// <summary>The sound files a district set (.essb) names.</summary>
    public static string[] SoundsIn(byte[] essb) => Strings(essb).Where(x => x.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
    /// <summary>The district set a level (.lvb) names, if any.</summary>
    public static string? SetOf(byte[] lvb) => Strings(lvb).FirstOrDefault(x => x.EndsWith(".essb", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Strings(byte[] bytes) =>
        System.Text.Encoding.ASCII.GetString(bytes.Select(b => b is >= 32 and < 127 ? b : (byte)10).ToArray()).Split((char)10).Where(x => x.Length > 4);

    private static string[] Tokens(string file)
    {
        var name = Name(file);
        var dot = name.LastIndexOf('.');
        return (dot > 0 ? name[..dot] : name).Split('_', StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).ToArray();
    }

    /// <summary>The loop's place in the set whatever the time of day: day ("_d_") and night ("_n_") share one.</summary>
    public static string Slot(string file) => Name(file).Replace("_d_", "_x_", StringComparison.OrdinalIgnoreCase).Replace("_n_", "_x_", StringComparison.OrdinalIgnoreCase);

    private static string Name(string file) => file[(file.Replace('\\', '/').LastIndexOf('/') + 1)..];
}
