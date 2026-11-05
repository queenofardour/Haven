using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using Haven.Core.Furniture;

namespace Haven.Core.Animation;

/// <summary>
/// A prop an animation package carries as a piece of gear: the slot and model set the mod's
/// gear replaces, its files (game path → file name in the package) and the meta manipulations for that piece (Eqdp
/// entries that let every body show it). Worn through Glamourer while the animation plays, from a mod Haven installs.
/// </summary>
public sealed record PoseProp(string Slot, int Set, Dictionary<string, string> Files, string[] Meta);

/// <summary>A prop found in a mod at import: its slot, model set, and the mod's paths that make it.</summary>
public sealed record PosePropCandidate(string Slot, int Set, string[] Paths);

/// <summary>
/// Props in animation mods. Some mods draw a pose's props as gear the player wears: Amborella's Spread Suspension puts
/// its chains on the Emperor's New Earrings (accessory 53), weighted to the body so they follow the pose. Haven finds
/// such gear in the mod's files (any gear model the mod changes), packs it with the animations, and every PC checks it
/// before it is installed.
/// </summary>
public static class PoseProps
{
    /// <summary>Glamourer's slot names, each with its file code and whether it is an accessory.</summary>
    public static readonly IReadOnlyDictionary<string, (string Code, bool Accessory)> Slots = new Dictionary<string, (string, bool)>(StringComparer.Ordinal)
    {
        ["Head"] = ("met", false), ["Body"] = ("top", false), ["Hands"] = ("glv", false), ["Legs"] = ("dwn", false), ["Feet"] = ("sho", false),
        ["Ears"] = ("ear", true), ["Neck"] = ("nek", true), ["Wrists"] = ("wrs", true), ["RFinger"] = ("rir", true), ["LFinger"] = ("ril", true),
    };
    /// <summary>The slot's name for the player.</summary>
    public static string Label(string slot) => slot switch { "RFinger" => "Right ring", "LFinger" => "Left ring", _ => slot };
    /// <summary>A package carries one prop (the piece its animations are made for); a mod may offer several to choose from.</summary>
    public const int MaxProps = 1;
    public const int MaxMeta = 64;
    public const int MaxFiles = 64;
    public const int MaxFileBytes = 32 * 1024 * 1024;
    private static readonly Regex Model = new(@"^chara/(accessory|equipment)/([ae])(\d{4})/model/c\d{4}\2\3_([a-z]{3})\.mdl$", RegexOptions.CultureInvariant);
    private static readonly Regex FileName = new(@"^f[0-9a-f]{1,3}\.(mdl|mtrl|tex)$", RegexOptions.CultureInvariant);

    /// <summary>The folder a slot's set lives in, e.g. chara/accessory/a0053/.</summary>
    public static string Folder(string slot, int set) => Slots[slot].Accessory ? $"chara/accessory/a{set:D4}/" : $"chara/equipment/e{set:D4}/";

    /// <summary>
    /// The gear a mod changes (with its options selected): each slot and set whose model it replaces, with the models,
    /// materials and textures of that piece in its folder (a file name carrying the slot's code, as the game names them).
    /// </summary>
    public static IReadOnlyList<PosePropCandidate> Detect(PenumbraMod mod)
    {
        var found = new SortedDictionary<(string Slot, int Set), bool>();
        foreach (var path in mod.Files.Keys)
        {
            var match = Model.Match(path);
            if (!match.Success) continue;
            var accessory = match.Groups[1].Value == "accessory";
            var slot = Slots.FirstOrDefault(s => s.Value.Code == match.Groups[4].Value && s.Value.Accessory == accessory).Key;
            if (slot != null && int.TryParse(match.Groups[3].Value, out var set) && set > 0) found[(slot, set)] = true;
        }
        return [.. found.Keys.Take(8).Select(key => new PosePropCandidate(key.Slot, key.Set,
            [.. mod.Files.Keys.Where(p => Belongs(p, key.Slot, key.Set)).Order(StringComparer.Ordinal).Take(MaxFiles)]))];
    }

    private static bool Belongs(string path, string slot, int set) =>
        path.StartsWith(Folder(slot, set), StringComparison.Ordinal) && Kind(path) != null && Path.GetFileName(path).Contains("_" + Slots[slot].Code, StringComparison.Ordinal);

    // The kind of file a prop may carry, by its extension.
    private static string? Kind(string path) => Path.GetExtension(path) switch { ".mdl" => "mdl", ".mtrl" => "mtrl", ".tex" => "tex", _ => null };

    /// <summary>The mod's meta manipulations that concern one piece of gear (others, such as a body's, stay behind).</summary>
    public static string[] MetaFor(PenumbraMod mod, string slot, int set) => [.. mod.Meta.Where(m => Concerns(m, slot, set)).Distinct().Take(MaxMeta)];

    /// <summary>
    /// Whether a manipulation (Penumbra's JSON: Type and Manipulation) is an Eqdp, Eqp or Imc entry for exactly this
    /// slot and set. Anything else is refused, so a package can change nothing but its own piece of gear.
    /// </summary>
    public static bool Concerns(string json, string slot, int set)
    {
        if (json.Length > 2048 || !Slots.ContainsKey(slot)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Type", out var type) || type.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("Manipulation", out var m) || m.ValueKind != JsonValueKind.Object || !Unambiguous(root) || !Unambiguous(m)) return false;
            return type.GetString() switch
            {
                "Eqdp" or "Eqp" => Number(m, "SetId") == set && Text(m, "Slot") == slot,
                "Imc" => Number(m, "PrimaryId") == set && Text(m, "EquipSlot") == slot && Text(m, "ObjectType") == (Slots[slot].Accessory ? "Accessory" : "Equipment"),
                _ => false,
            };
        }
        catch (JsonException) { return false; }
    }

    // Penumbra reads manipulations with Newtonsoft, whose property names match whatever their case: a name given twice,
    // or in two cases ("SetId" and "setId"), could be read one way here and another there, so it's refused.
    private static bool Unambiguous(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name) || property.Value.ValueKind == JsonValueKind.Object && !Unambiguous(property.Value)) return false;
        return true;
    }

    /// <summary>Whether a package file name is one a prop's file may have.</summary>
    public static bool ValidFileName(string name) => FileName.IsMatch(name);

    /// <summary>Whether a game path is one a prop may change: a model, material or texture in its own set's folder.</summary>
    public static bool ValidPath(string slot, int set, string path) =>
        PenumbraMod.GamePath(path) == path && Slots.ContainsKey(slot) && path.StartsWith(Folder(slot, set), StringComparison.Ordinal) && Kind(path) != null && !path.Contains("//")
        && path.Split('/').All(SafeSegment);

    // Each part of the path becomes a folder or file in Penumbra's mod folder on the guest's PC: nothing Windows treats
    // specially (a device name such as con or nul, a trailing dot or space, "~" short names).
    private static readonly HashSet<string> DeviceNames = ["con", "prn", "aux", "nul", .. Enumerable.Range(0, 10).SelectMany(i => new[] { $"com{i}", $"lpt{i}" })];
    private static bool SafeSegment(string segment) => segment.Length is > 0 and <= 128 && segment.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.')
        && !segment.EndsWith('.') && !DeviceNames.Contains(segment.Split('.')[0]);

    /// <summary>A package file name for a prop file: f + a number + its kind.</summary>
    public static string NameFor(int index, string path) => $"f{index:x}.{Kind(path)}";

    /// <summary>Checks a prop file's header for its kind: a v5 or v6 model, a material, or a texture.</summary>
    public static void CheckFile(string name, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("A prop file is too large.");
        var ok = Path.GetExtension(name) switch
        {
            // Drawn on the player (and passed on by sync plugins like any mod): the structure furniture's files must have too.
            ".mdl" => bytes.Length >= 0x44 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) is 0x01000005 or 0x01000006 && Structured(name, bytes),
            ".mtrl" => bytes.Length >= 0x10 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x01030000 && Structured(name, bytes),
            ".tex" => CheckTexture(bytes),
            _ => false,
        };
        if (!ok) throw new InvalidDataException("A prop file in the animations is not valid.");
    }

    private static bool Structured(string name, ReadOnlySpan<byte> bytes)
    {
        try { FurnitureFiles.Check(name, bytes.ToArray()); return true; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return false; }
    }

    // A prop's texture is drawn on the player: its mips must fit inside the file, as for furniture (FurnitureFiles.Check).
    private static bool CheckTexture(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 0x50) return false;
        TextureDecoding.CheckMips(bytes.ToArray(), 8192);
        return true;
    }

    /// <summary>Checks a prop from a package's manifest: its slot, set, paths, file names and manipulations.</summary>
    public static void Check(PoseProp prop)
    {
        if (prop == null || prop.Slot == null || !Slots.ContainsKey(prop.Slot) || prop.Set is < 1 or > 9999 || prop.Files is not { Count: > 0 and <= MaxFiles }
            || prop.Meta is not { Length: <= MaxMeta })
            throw new InvalidDataException("An animation package lists a prop badly.");
        foreach (var (path, file) in prop.Files)
            if (!ValidPath(prop.Slot, prop.Set, path) || file == null || !ValidFileName(file) || Path.GetExtension(file) != Path.GetExtension(path))
                throw new InvalidDataException("An animation package's prop names a file badly.");
        if (prop.Meta.Any(m => m == null || !Concerns(m, prop.Slot, prop.Set))) throw new InvalidDataException("An animation package's prop changes more than its own gear.");
    }

    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : -1;
}
