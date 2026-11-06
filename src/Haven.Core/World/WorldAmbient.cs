using System.Text.Json;
using System.Text.RegularExpressions;

namespace Haven.Core.World;

/// <summary>What a placeable piece of the world's ambience is.</summary>
public enum AmbientKind { Effect, Sound }

/// <summary>
/// How a sound repeats: a loop (its audio loops), a cycle (random variants one after another: drips, creaks, thunder),
/// or neither. Effects are all continuous (the catalogue keeps only those).
/// </summary>
public enum AmbientRepeat { Loop, Cycle, None }

/// <summary>
/// One entry of the world's ambience catalogue: an effect (.avfx) or a sound (.scd) the game
/// places in its zones, with a name and category, the zone that places it most, how often the layouts place it, and for
/// effects a size (metres, the largest side of its extent; 0 when unknown) and the colour it is most often placed with
/// (RGBA, 0-1). Built offline by the catalogue tool (`--ambient`) and embedded in Haven; the plugin offers an entry only
/// when this PC's game has its file.
/// </summary>
/// An effect also has an offset (metres, X Y Z, at scale 1): where its piece's origin puts it so that what it draws is
/// centred on the piece with its bottom on the floor (many are drawn far from their file's origin). Guess marks a name
/// its namers weren't sure of.
public sealed record WorldAmbientEntry(string Path, AmbientKind Kind, string Name, string Category, string Zone, int Uses,
    float Size = 0, AmbientRepeat Repeat = AmbientRepeat.Loop, float[]? Colour = null, float[]? Offset = null, bool? Guess = null)
{
    /// <summary>Whether its path is one a home may hold for its kind.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Valid => Kind == AmbientKind.Effect ? WorldAmbient.IsEffectPath(Path) : WorldAmbient.IsSoundPath(Path);
}

public static partial class WorldAmbient
{
    // Effect categories (from the effect survey's token table) and sound categories (from the sound survey's).
    public static readonly string[] EffectCategories = ["Fire & embers", "Water", "Smoke & steam", "Fog, mist & cloud", "Light & glow", "Sparkle & magic", "Weather & sky", "Nature", "Other"];
    public static readonly string[] SoundCategories = ["Water", "Fire", "Wind & weather", "Machinery", "Creaks & rigging", "Crowds & voices", "Creatures", "Bells & chimes", "Magic & aether", "Earth & stone", "Ambience", "Other"];

    /// <summary>
    /// A world effect a home may place: a region's or a zone's effect (bg/&lt;expansion&gt;/…/vfx/eff/…​.avfx) or a common
    /// one (bgcommon/…/vfx…/…​.avfx). Each folder is one lowercase name, so a path climbs nowhere.
    /// </summary>
    public static bool IsEffectPath(string path) => path.Length <= BuildingParts.MaxPathLength && EffectPath().IsMatch(path);

    /// <summary>A world sound a home may place: one of the game's ambient sound files (bgcommon/sound/… or bgcommon/env/sound/…​.scd).</summary>
    public static bool IsSoundPath(string path) => path.Length <= BuildingParts.MaxPathLength && SoundPath().IsMatch(path);

    [GeneratedRegex(@"\A(?:bg/(?:ffxiv|ex[1-9])/(?:[a-z0-9_]+/){1,6}vfx/(?:[a-z0-9_]+/){0,3}[a-z0-9_]+\.avfx|bgcommon/(?:[a-z0-9_]+/){0,6}vfx[a-z0-9_]*/(?:[a-z0-9_]+/){0,4}[a-z0-9_]+\.avfx)\z", RegexOptions.CultureInvariant)]
    private static partial Regex EffectPath();

    // Sound files keep the case the layouts give them (spot_wtr_River_2ch): letters of both cases, digits, _ and - in names.
    [GeneratedRegex(@"\Abgcommon/(?:env/)?sound/(?:[a-z0-9_]+/){0,6}[A-Za-z0-9_\-]+\.scd\z", RegexOptions.CultureInvariant)]
    private static partial Regex SoundPath();
}

/// <summary>The ambience catalogue embedded in Haven.Core (World/world-ambient.json), or one read from its JSON.</summary>
public sealed class WorldAmbientTable
{
    private readonly Dictionary<string, WorldAmbientEntry> entries;
    public IReadOnlyCollection<WorldAmbientEntry> Entries => entries.Values;
    public int Count => entries.Count;

    public WorldAmbientTable(IEnumerable<WorldAmbientEntry> list)
    {
        entries = new(StringComparer.Ordinal);
        foreach (var entry in list)
        {
            if (entry == null || !entry.Valid || entries.ContainsKey(entry.Path)) continue;
            var name = SafeText.Clean(entry.Name, 80);
            if (name.Length == 0) continue;
            var category = SafeText.Clean(entry.Category, 40);
            entries[entry.Path] = entry with
            {
                Name = name, Category = category.Length > 0 ? category : "Other", Zone = SafeText.Clean(entry.Zone, 60),
                Uses = Math.Max(0, entry.Uses), Size = float.IsFinite(entry.Size) && entry.Size >= 0 ? entry.Size : 0,
                Colour = entry.Colour is { Length: 4 } c && c.All(v => float.IsFinite(v) && v is >= 0 and <= 4) ? c : null,
            };
        }
    }

    public WorldAmbientEntry? Find(string path) => entries.GetValueOrDefault(path);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>A table from its JSON (an array of entries); entries that don't read or name a path a home can't hold are skipped.</summary>
    public static WorldAmbientTable Parse(string json)
    {
        try { return new(JsonSerializer.Deserialize<WorldAmbientEntry[]>(json, Json) ?? []); }
        catch (JsonException) { return new([]); }
    }

    public static string Write(IEnumerable<WorldAmbientEntry> list) => JsonSerializer.Serialize(list.OrderBy(e => e.Kind).ThenBy(e => e.Path, StringComparer.Ordinal),
        new JsonSerializerOptions(Json) { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    /// <summary>The table embedded in Haven.Core; empty until the tool has written one.</summary>
    public static WorldAmbientTable Embedded()
    {
        using var stream = typeof(WorldAmbientTable).Assembly.GetManifestResourceStream("Haven.Core.World.world-ambient.json");
        if (stream == null) return new([]);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
