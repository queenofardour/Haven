using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Haven.Core;

/// <summary>
/// Glamourer states as Haven stores them for NPCs (NpcLook.Design): base64 of a version byte (6) and gzip-compressed
/// JSON, as Glamourer's IPC exports them (plain JSON is read too). An exported state marks
/// every advanced customization colour (Parameters: skin, hair, eyes, lips…) as applied, even ones nobody changed, and
/// an applied one overrides the matching ordinary colour, so an NPC's Skin or Hair Color could no longer be changed.
/// Glamourer's own copy keeps only the colours that differ from what the game draws for the ordinary settings; Haven
/// does the same by comparing with the state the NPC shows once those colours are let go (NpcPossession.Dress).
/// </summary>
public static class GlamourerStates
{
    // Squared distance under which two colours count as the same (Glamourer's own test is near-exact; values
    // round-trip through JSON text here).
    private const double SameColour = 1e-5;

    /// <summary>The state's JSON, or null when it isn't a Glamourer state Haven can read.</summary>
    public static JsonObject? Decode(string design)
    {
        try
        {
            var bytes = Convert.FromBase64String(design);
            if (bytes.Length == 0) return null;
            if (bytes[0] == (byte)'{') return JsonNode.Parse(bytes) as JsonObject;
            // A version byte, then gzip (Glamourer's exports end without the gzip trailer, so the deflate data is read
            // as far as it goes).
            if (bytes.Length < 11 || bytes[1] != 0x1F || bytes[2] != 0x8B) return null;
            using var deflate = new DeflateStream(new MemoryStream(bytes, 11, bytes.Length - 11), CompressionMode.Decompress);
            using var json = new MemoryStream();
            var buffer = new byte[4096];
            // A design from someone else's home: a few KB inflate to a few tens of KB; anything past MaxJson is refused.
            try { int read; while ((read = deflate.Read(buffer)) > 0) { json.Write(buffer, 0, read); if (json.Length > MaxJson) return null; } }
            catch (InvalidDataException) { }   // the missing trailer, once everything is read
            return JsonNode.Parse(json.ToArray()) as JsonObject;
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidDataException) { return null; }
    }

    /// <summary>The largest state Haven inflates (Glamourer's own are well under 100 KB).</summary>
    public const int MaxJson = 1024 * 1024;

    /// <summary>A state in Glamourer's own share format: version byte 6, then the JSON gzip-compressed, in base64.</summary>
    public static string Encode(JsonObject state)
    {
        using var output = new MemoryStream();
        output.WriteByte(6);
        using (var zip = new GZipStream(output, CompressionLevel.Optimal, true)) zip.Write(Encoding.UTF8.GetBytes(state.ToJsonString()));
        return Convert.ToBase64String(output.ToArray());
    }

    private static IEnumerable<(string Name, JsonObject Entry)> Parameters(JsonObject? state) =>
        state?["Parameters"] is JsonObject parameters
            ? parameters.Select(p => (p.Key, p.Value as JsonObject)).Where(p => p.Item2 != null).Select(p => (p.Key, p.Item2!))
            : [];
    private static bool Applied(JsonObject entry) => entry["Apply"] is JsonValue apply && apply.TryGetValue(out bool on) && on;

    /// <summary>Whether a state applies any advanced customization colour.</summary>
    public static bool PinsParameters(string design) => Parameters(Decode(design)).Any(p => Applied(p.Entry));
    /// <summary>The advanced customization colours a state applies, by Glamourer's names (SkinDiffuse, HairDiffuse…).</summary>
    public static string[] Pinned(string design) => [.. Parameters(Decode(design)).Where(p => Applied(p.Entry)).Select(p => p.Name)];

    /// <summary>The state with no advanced customization applied (the ordinary settings draw every colour); null when unreadable.</summary>
    public static string? WithoutParameters(string design)
    {
        if (Decode(design) is not { } state) return null;
        foreach (var (_, entry) in Parameters(state)) entry["Apply"] = false;
        return Encode(state);
    }

    /// <summary>
    /// The wanted state keeping only the advanced colours that differ from the plain state (the same look drawn with
    /// none applied), as Glamourer's own copy does; null when either is unreadable.
    /// </summary>
    public static string? KeepChangedParameters(string wanted, string plain)
    {
        if (Decode(wanted) is not { } state || Decode(plain) is not { } shown) return null;
        var drawn = Parameters(shown).ToDictionary(p => p.Name, p => p.Entry);
        foreach (var (name, entry) in Parameters(state))
            if (Applied(entry) && drawn.TryGetValue(name, out var game) && Same(entry, game)) entry["Apply"] = false;
        return Encode(state);
    }

    /// <summary>The customize settings that make the body (raw values, as the game's customize bytes 0, 1 and 4).</summary>
    public static readonly string[] BodySettings = ["Race", "Gender", "Clan"];
    private static readonly string[] Armour = ["Head", "Body", "Hands", "Legs", "Feet"];

    /// <summary>
    /// What a state sets that shows at a glance, where it applies it: race, gender and clan and the five armour slots'
    /// item ids. Empty when unreadable. Comparing two (Differences) tells whether a character really wears a look.
    /// </summary>
    public static Dictionary<string, ulong> Shown(string design)
    {
        var shown = new Dictionary<string, ulong>();
        if (Decode(design) is not { } state) return shown;
        foreach (var (group, keys, field) in new[] { ("Customize", BodySettings, "Value"), ("Equipment", Armour, "ItemId") })
            if (state[group] is JsonObject entries)
                foreach (var key in keys)
                    if (entries[key] is JsonObject entry && Applied(entry) && Whole(entry[field]) is { } value) shown[key] = value;
        return shown;
    }

    /// <summary>Where a drawn look differs from a wanted one (each from Shown): "Race 4, wanted 8"; empty when it matches.</summary>
    public static string[] Differences(IReadOnlyDictionary<string, ulong> wanted, IReadOnlyDictionary<string, ulong> drawn)
        => [.. wanted.Where(w => drawn.TryGetValue(w.Key, out var d) && d != w.Value).Select(w => $"{w.Key} {drawn[w.Key]}, wanted {w.Value}")];

    private static ulong? Whole(JsonNode? node) => node is not JsonValue value ? null
        : value.TryGetValue(out ulong whole) ? whole : value.TryGetValue(out double d) && d >= 0 ? (ulong)d : null;

    // The numeric fields of two entries (Value, Percentage, Red, Green, Blue, Alpha) agree.
    private static bool Same(JsonObject a, JsonObject b)
    {
        double distance = 0;
        foreach (var key in new[] { "Value", "Percentage", "Red", "Green", "Blue", "Alpha" })
        {
            var x = Number(a[key]); var y = Number(b[key]);
            if (x == null && y == null) continue;
            if (x == null || y == null) return false;
            distance += (x.Value - y.Value) * (x.Value - y.Value);
        }
        return distance < SameColour;
    }
    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double d) ? d : null;
}
