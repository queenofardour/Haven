using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Haven.Core;

/// <summary>
/// One furnishing of a layout from outside Haven, in the interior's own coordinates (a preset home's). Yaw is in
/// degrees, as Haven stores it. A layout names its dye either as a stain row (a real house) or as a colour 0xRRGGBB
/// (a MakePlace file); 0 / null is undyed. Material is the item a piece shows, when it shows one: a frame's painting, a
/// partition's wallpaper, a vase's flower or a canvas's print (MakePlace's properties.material, or what a real house's
/// piece shows). Fish are an aquarium's fish, as AquariumFish rows (a real house only).
/// </summary>
public sealed record LayoutPiece(uint ItemId, string Name, Vector3 Position, float Yaw, uint Stain = 0, uint? Color = null, uint Material = 0,
    uint[]? Fish = null);

/// <summary>
/// An interior finish: Type is Wall, Floor or Light (others, such as Window and Door, are not offered by Haven);
/// Level is Ground Floor, Upper Floor, Basement or Main (a mansion's central hall). A real house names the
/// HousingInterior row; a MakePlace file names the item.
/// </summary>
public sealed record LayoutFixture(string Type, string Level, string Name, uint ItemId = 0, uint FixtureId = 0);

/// <summary>A home's layout: its shell (MakePlace size and district, when known), furnishings and finishes.</summary>
public sealed record HomeLayout(string? HouseSize, string? District, LayoutPiece[] Pieces, LayoutFixture[] Fixtures, int OutdoorPieces = 0,
    float? Brightness = null)
{
    public const int MaxPieces = 4096;
}

public static class HomeLayouts
{
    public static readonly string[] Levels = ["Ground Floor", "Upper Floor", "Basement"];

    // Interior shell codes: district prefix and size suffix of the territory name (s1i1 is a small Mist house).
    private static readonly Dictionary<string, string> Districts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mist"] = "s1", ["Lavender Beds"] = "f1", ["The Lavender Beds"] = "f1", ["Goblet"] = "w1", ["The Goblet"] = "w1",
        ["Shirogane"] = "e1", ["Empyreum"] = "r1",
    };
    private static readonly Dictionary<string, int> Sizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Apartment"] = 1, ["Small"] = 2, ["Medium"] = 3, ["Large"] = 4,
    };

    /// <summary>The shell a MakePlace layout was made for, as Haven's launcher sizes it (1 apartment, 2 cottage,
    /// 3 house, 4 mansion; 0 is an inn, which MakePlace doesn't plan) and the two-letter district code of its territory
    /// name; null when the file does not say.</summary>
    public static (string District, int Size)? Shell(HomeLayout layout) =>
        layout.District is {} district && Districts.TryGetValue(district, out var code) && layout.HouseSize is {} size && Sizes.TryGetValue(size, out var index)
            ? (code, index) : null;

    /// <summary>The yaw about the vertical that MakePlace stores as a quaternion about its Z (up) axis, as the game
    /// stores it: MakePlace writes the game's rotation r as a roll of −r.</summary>
    public static float YawFromMakePlace(Quaternion q)
    {
        var zAngle = MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z));
        return Degrees(-zAngle);
    }

    /// <summary>A game rotation in radians as Haven's yaw in degrees, in (−180, 180].</summary>
    public static float Degrees(float radians)
    {
        var degrees = radians * 180 / MathF.PI % 360;
        return degrees > 180 ? degrees - 360 : degrees <= -180 ? degrees + 360 : degrees;
    }

    /// <summary>The dye whose colour is nearest, or 0 when there is none to choose.</summary>
    public static uint NearestDye(uint rgb, IEnumerable<(uint Id, uint Color)> dyes)
    {
        static int Channel(uint c, int shift) => (int)(c >> shift & 255);
        uint best = 0; var distance = int.MaxValue;
        foreach (var (id, color) in dyes)
        {
            if (id == 0) continue;
            var d = 0;
            foreach (var shift in new[] { 16, 8, 0 }) { var delta = Channel(color, shift) - Channel(rgb, shift); d += delta * delta; }
            if (d < distance) { distance = d; best = id; }
        }
        return best;
    }
}

/// <summary>
/// Reads a MakePlace layout (.json): the format of the MakePlace planner and its plugin. Furnishings are
/// interiorFurniture entries with a name, an itemId and a transform whose location is (x, z, y) of the game's axes,
/// divided by interiorScale, and whose rotation is a quaternion about that Z (up) axis. properties.color is the dye as
/// RRGGBBAA hex, and properties.material ({name, itemId}) the item it shows. Items standing on others are their attachments, with their own full transforms. interiorFixture
/// lists the finishes by type and level, plus a District entry; houseSize names the shell. lightLevel is the game's room
/// brightness, 0 (darkest) to 5 (brightest), as the game's "Room brightness set to N" counts it (layouts' screenshots
/// at 1 of 5 were compared, 1 November 2026: they match; dim rooms are a popular choice). Exterior entries are counted
/// and left out: Haven homes are interiors. Values are read leniently, since files from different MakePlace versions
/// vary (negative item IDs, empty property lists), but positions must be finite and the size bounded.
/// </summary>
public static class MakePlaceLayout
{
    public const int MaxBytes = 16 * 1024 * 1024;

    public static HomeLayout Read(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes) throw new InvalidDataException("This MakePlace layout is larger than 16 MiB.");
        JsonDocument document;
        try { document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { throw new InvalidDataException("This file is not a MakePlace layout: " + ex.Message); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("interiorFurniture", out _) && !root.TryGetProperty("interiorFixture", out _))
                throw new InvalidDataException("This file is not a MakePlace layout: it has no interior furniture or fixtures.");
            var scale = Number(root, "interiorScale") is float s && s > 0 && float.IsFinite(s) ? s : 1;
            List<LayoutPiece> pieces = [];
            void Add(JsonElement furniture, int depth)
            {
                if (furniture.ValueKind != JsonValueKind.Object || depth > 8) return;
                if (pieces.Count >= HomeLayout.MaxPieces) throw new InvalidDataException($"This layout has more than {HomeLayout.MaxPieces} furnishings.");
                if (Piece(furniture, scale) is {} piece) pieces.Add(piece);
                if (furniture.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
                    foreach (var child in attachments.EnumerateArray()) Add(child, depth + 1);
            }
            foreach (var furniture in Array(root, "interiorFurniture")) Add(furniture, 0);
            var outdoor = Array(root, "exteriorFurniture").Count();
            List<LayoutFixture> fixtures = [];
            string? district = null;
            foreach (var fixture in Array(root, "interiorFixture"))
            {
                if (fixture.ValueKind != JsonValueKind.Object) continue;
                var type = Text(fixture, "type"); var name = Text(fixture, "name");
                if (type.Equals("District", StringComparison.OrdinalIgnoreCase)) { district = name; continue; }
                fixtures.Add(new(type, Text(fixture, "level"), name, ItemId(fixture)));
            }
            var size = Text(root, "houseSize");
            float? brightness = Number(root, "lightLevel") is float level && float.IsFinite(level) && level >= 0 ? Math.Clamp(MathF.Round(level), 0, 5) : null;
            return new(size.Length > 0 ? size : null, district, pieces.ToArray(), fixtures.ToArray(), outdoor, brightness);
        }
    }

    private static LayoutPiece? Piece(JsonElement furniture, float scale)
    {
        if (!furniture.TryGetProperty("transform", out var transform) || transform.ValueKind != JsonValueKind.Object) return null;
        var location = Floats(transform, "location", 3);
        if (location == null) return null;
        var position = new Vector3(location[0], location[2], location[1]) / scale;
        if (!float.IsFinite(position.LengthSquared()) || MathF.Abs(position.X) > 10000 || MathF.Abs(position.Y) > 10000 || MathF.Abs(position.Z) > 10000) return null;
        var rotation = Floats(transform, "rotation", 4) is {} r ? new Quaternion(r[0], r[1], r[2], r[3]) : Quaternion.Identity;
        var yaw = rotation.LengthSquared() > 1e-6f ? HomeLayouts.YawFromMakePlace(Quaternion.Normalize(rotation)) : 0;
        uint? color = null;
        if (furniture.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            && Text(properties, "color") is { Length: >= 6 } hex && uint.TryParse(hex.AsSpan(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            color = rgb;
        var material = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("material", out var shown) && shown.ValueKind == JsonValueKind.Object ? ItemId(shown) : 0;
        return new(ItemId(furniture), Text(furniture, "name"), position, float.IsFinite(yaw) ? yaw : 0, Color: color, Material: material);
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static float? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? (float)d : null;
    // Some MakePlace versions saved negative or fractional item IDs; those cannot be looked up by ID, only by name.
    private static uint ItemId(JsonElement element) =>
        element.TryGetProperty("itemId", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) && id is > 0 and <= uint.MaxValue ? (uint)id : 0;
    private static float[]? Floats(JsonElement element, string name, int count)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < count) return null;
        var result = new float[count];
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (i == count) break;
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var d) || !double.IsFinite(d)) return null;
            result[i++] = (float)d;
        }
        return result;
    }
}

/// <summary>
/// A layout saved for reuse: the furnishings and finishes of a real house (saved from inside it) or a MakePlace file,
/// for the shell they were made in. It starts a new home or replaces a home's furnishings on the same shell. Source
/// says where it came from. The records are a home's own, so applying one is a straight copy.
/// </summary>
public sealed record SavedLayout
{
    public int FormatVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public uint TerritoryId { get; init; }
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;
    public Furnishing[] Furnishings { get; init; } = [];
    public InteriorOptions Interior { get; init; } = new();
}

public static class LayoutFormat
{
    public static SavedLayout Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > HouseFormat.MaxBytes) throw new InvalidDataException("Layout exceeds 1 MiB.");
        SavedLayout? layout;
        try { layout = JsonSerializer.Deserialize<SavedLayout>(bytes, HouseFormat.Json); }
        catch (JsonException ex) { throw new InvalidDataException("This file is not a saved Haven layout: " + ex.Message); }
        Validate(layout ?? throw new InvalidDataException("Empty layout."));
        return layout;
    }

    public static byte[] Serialize(SavedLayout layout)
    {
        Validate(layout);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(layout, HouseFormat.Json);
        if (bytes.Length > HouseFormat.MaxBytes) throw new InvalidDataException("Layout exceeds 1 MiB.");
        return bytes;
    }

    // The same limits as a home's furnishings and finishes; building parts belong to custom canvases, not layouts.
    public static void Validate(SavedLayout layout)
    {
        if (layout.FormatVersion != 1 || layout.Id == Guid.Empty) throw new InvalidDataException("Unsupported layout format or missing identity.");
        if (string.IsNullOrWhiteSpace(layout.Name) || layout.Name.Length > 120 || layout.Name.Any(char.IsControl)
            || layout.Source == null || layout.Source.Length > 200 || layout.Source.Any(char.IsControl))
            throw new InvalidDataException("Layout name must contain 1–120 printable characters.");
        if (layout.TerritoryId is 0 or > ushort.MaxValue) throw new InvalidDataException("A layout needs its housing territory.");
        HouseFormat.ValidateHousingOptions(new House { Name = layout.Name, TerritoryId = layout.TerritoryId, Furnishings = layout.Furnishings, Interior = layout.Interior });
    }

    /// <summary>The layout's furnishings with fresh identities, so each home that uses it owns its own pieces.</summary>
    public static Furnishing[] Copy(SavedLayout layout) => layout.Furnishings.Select(f => f with { Id = Guid.NewGuid() }).ToArray();
}

/// <summary>Saved layouts as one file each, written atomically like homes; a deleted layout keeps a recovery copy.</summary>
public sealed class LayoutStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private string PathFor(Guid id) => Path.Combine(DirectoryPath, $"{id:N}.layout.json");

    public IReadOnlyList<SavedLayout> List()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        List<SavedLayout> layouts = [];
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.layout.json"))
        {
            try { layouts.Add(Read(path)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return layouts.OrderByDescending(x => x.SavedAt).ToArray();
    }

    public void Save(SavedLayout layout)
    {
        var bytes = LayoutFormat.Serialize(layout);
        Directory.CreateDirectory(DirectoryPath);
        var path = PathFor(layout.Id); var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Delete(Guid id)
    {
        var source = PathFor(id);
        if (!File.Exists(source)) return;
        var deleted = Path.Combine(DirectoryPath, "deleted");
        Directory.CreateDirectory(deleted);
        File.Move(source, Path.Combine(deleted, $"{id:N}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.layout.json"));
    }

    public static SavedLayout Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > HouseFormat.MaxBytes) throw new InvalidDataException("Layout file exceeds 1 MiB.");
        return LayoutFormat.Parse(File.ReadAllBytes(path));
    }
}
