using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Haven.Core;

public sealed record StageTransform
{
    public float[] Translation { get; init; } = [0, 0, 0];
    public float[] Rotation { get; init; } = [0, 0, 0, 1];
    public float Scale { get; init; } = 1;
    [System.Text.Json.Serialization.JsonIgnore] public Vector3 Position => new(Translation[0], Translation[1], Translation[2]);
    [System.Text.Json.Serialization.JsonIgnore] public Quaternion Orientation => new(Rotation[0], Rotation[1], Rotation[2], Rotation[3]);
}

public sealed record House
{
    public int FormatVersion { get; init; } = 2;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "My haven";
    public uint TerritoryId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool CustomLayout { get; init; }
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;
    public StageTransform Transform { get; init; } = new();
    public JsonElement Stage { get; init; }
    public Furnishing[] Furnishings { get; init; } = [];
    public InteriorOptions Interior { get; init; } = new();
    public MusicOptions Music { get; init; } = new();
    /// <summary>NPCs saved in the home (housing protocol 29); omitted when there are none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HomeNpc[]? Npcs { get; init; }
}

public static partial class HouseFormat
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxObjects = 4096;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static House Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("House exceeds the 1 MiB snapshot budget.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 32 });
        CheckJson(document.RootElement);
        var house = document.RootElement.Deserialize<House>(Json) ?? throw new InvalidDataException("Empty house.");
        Validate(house);
        return house;
    }

    public static byte[] Serialize(House house)
    {
        Validate(house);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(house, Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("House exceeds the 1 MiB snapshot budget.");
        return bytes;
    }

    public static void Validate(House house)
    {
        if (house.FormatVersion is not (1 or 2) || house.Id == Guid.Empty) throw new InvalidDataException("Unsupported house format or missing identity.");
        if (string.IsNullOrWhiteSpace(house.Name) || house.Name.Length > 120 || house.Name.Any(char.IsControl))
            throw new InvalidDataException("House name must contain 1–120 printable characters.");
        if (house.TerritoryId is 0 or > ushort.MaxValue) throw new InvalidDataException("Choose a housing territory.");
        var t = house.Transform ?? throw new InvalidDataException("Missing transform.");
        if (t.Translation is not { Length: 3 } || t.Rotation is not { Length: 4 }
            || !t.Translation.Concat(t.Rotation).All(float.IsFinite) || !float.IsFinite(t.Scale) || t.Scale <= 0 || t.Scale > 100
            // Bounded like furnishings' positions: the whole scene and its collision move by it.
            || t.Translation.Any(x => Math.Abs(x) > 10000))
            throw new InvalidDataException("Invalid stage transform.");
        if (Math.Abs(t.Orientation.LengthSquared() - 1) > 0.01f) throw new InvalidDataException("Stage rotation must be a unit quaternion.");
        ValidateHousingOptions(house);
        CustomCanvas.Validate(house);
        CheckJson(house.Stage);
        var stage = house.Stage;
        if (stage.ValueKind != JsonValueKind.Object || !stage.TryGetProperty("FormatVersion", out var version)
            || !version.TryGetInt32(out var format) || format != 2)
            throw new InvalidDataException("A Stagehand format 2 stage is required.");
        if (stage.TryGetProperty("EmbeddedModpacks", out var mods) && (mods.ValueKind != JsonValueKind.Object || mods.EnumerateObject().Any()))
            throw new InvalidDataException("This alpha shares game housing assets only. Remove embedded modpacks.");
        if (!stage.TryGetProperty("Objects", out var objects)) throw new InvalidDataException("Missing stage objects.");
        var count = 0;
        ValidateObjects(objects, 0, ref count);
    }

    internal static void ValidateHousingOptions(House house)
    {
        if (house.Furnishings == null || house.Furnishings.Length > MaxObjects) throw new InvalidDataException("Invalid furniture count.");
        var ids = new HashSet<Guid>();
        foreach (var f in house.Furnishings)
            if (f == null || f.Id == Guid.Empty || !ids.Add(f.Id)
                || (f.BuildingPart == null ? f.ItemId == 0 && f.CustomFurniture == null : f.ItemId != 0
                    || !BuildingParts.IsHousingAsset(f.BuildingPart) || f.Picture != 0 || f.CustomPicture != null || f.Portrait)
                || f.Picture > int.MaxValue
                || f.CustomPicture != null && (!ContentLocker.IsFingerprint(f.CustomPicture) || f.Picture != 0 || f.SurfaceFinish != 0)
                || f.Framing != null && (f.CustomPicture == null || !f.Framing.Valid)
                || f.CustomFurniture != null && (f.BuildingPart != null || !ContentLocker.IsFingerprint(f.CustomFurniture))
                || f.Position is not { Length: 3 }
                || !f.Position.All(x => float.IsFinite(x) && Math.Abs(x) <= 10000) || !SafeAngles.Sane(f.Rotation, f.Pitch, f.Roll) || f.StainId > ushort.MaxValue || f.SurfaceFinish > int.MaxValue
                || !float.IsFinite(f.Scale) || f.Scale is < 0.05f or > 10
                || f.SoundVolume is float volume && (f.BuildingPart != null || !float.IsFinite(volume) || volume is < 0 or > 1)
                || f.Display != 0 && (f.BuildingPart != null || f.Display > 100_000 || f.Picture != 0 || f.CustomPicture != null || f.SurfaceFinish != 0)
                || f.Aquarium is { } tank && (f.BuildingPart != null || tank.Fish is not { Length: <= Aquariums.MaxTier } || tank.Fish.Any(x => x is 0 or > 100_000)
                    || tank.Decor > Aquariums.MaxDecor || f.Display != 0))
                throw new InvalidDataException("Invalid furniture identity, item, or transform.");
        PoseSets.Validate(house);
        HomeNpcs.Validate(house);
        var interior = house.Interior;
        if (interior == null || interior.Floors is not { Length: 3 } || interior.Floors.Any(x => x == null)
            || interior.Brightness is float brightness && (!float.IsFinite(brightness) || brightness is < 0 or > 5)
            || interior.HiddenPieces is { } hidden && (house.CustomLayout || hidden.Length > ShellRemodel.MaxHidden || hidden.Any(x => x == null || x.Instance == 0 || x.MovedTo == Guid.Empty)
                || hidden.Select(x => x.Instance).Distinct().Count() != hidden.Length))
            throw new InvalidDataException("Invalid interior settings.");
        if (!HomeMoods.Valid(interior, ValidMusic)) throw new InvalidDataException("Invalid moods, weather or time of day.");
        if (!ValidMusic(house.Music)) throw new InvalidDataException("Invalid orchestrion playlist.");
    }

    internal static bool ValidMusic(MusicOptions? music) =>
        !(music == null || !float.IsFinite(music.Volume) || music.Volume is < 0 or > 1 || music.GapSeconds is < 0 or > 3600
            || music.StartedAtUnixMs < 0 || music.StartedAtUnixMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            || music.Playlist == null || music.Playlist.Length > 64
            || music.Playlist.Any(t => t == null || t.Repeats is < 1 or > 10 || (t.CustomMusic == null
                ? t.OrchestrionId == 0 || t.OrchestrionId > ushort.MaxValue || t.Title != null || t.Milliseconds != 0
                : t.OrchestrionId != 0 || !ContentLocker.IsFingerprint(t.CustomMusic) || string.IsNullOrWhiteSpace(t.Title) || t.Title.Length > 60
                    || t.Title.Any(char.IsControl) || t.Milliseconds is <= 0 or > Music.MusicEncoder.MaxMilliseconds)));

    private static void ValidateObjects(JsonElement objects, int depth, ref int count)
    {
        if (depth > 8 || objects.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid or excessively nested stage group.");
        foreach (var item in objects.EnumerateObject())
        {
            if (++count > MaxObjects) throw new InvalidDataException("Scene exceeds the 4096 object rendering budget.");
            var value = item.Value;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("Type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Missing or invalid object type.");
            if (value.TryGetProperty("ModpackId", out var mod) && (mod.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(mod.GetString())))
                throw new InvalidDataException("Modded resources are outside this alpha's housing format.");
            switch (type.GetString())
            {
                case "Group":
                    if (!value.TryGetProperty("Objects", out var children)) throw new InvalidDataException("Missing group objects.");
                    ValidateObjects(children, depth + 1, ref count);
                    break;
                case "BgObject":
                    if (!value.TryGetProperty("ModelGamePath", out var model) || model.ValueKind != JsonValueKind.String
                        || !HousingPath().IsMatch(model.GetString() ?? "") || model.GetString()!.Contains(".."))
                        throw new InvalidDataException("Furniture must use an installed bgcommon/hou/ .mdl game asset.");
                    break;
                case "Light": break;
                default: throw new InvalidDataException("This housing alpha supports furniture, groups, and lights.");
            }
        }
    }

    internal static void CheckJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in value.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate JSON field.");
                CheckJson(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) CheckJson(element);
        else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetSingle(out var n) || !float.IsFinite(n)))
            throw new InvalidDataException("Invalid numeric scene value.");
    }

    [GeneratedRegex(@"^bgcommon/hou/[a-zA-Z0-9_/.-]+\.mdl$", RegexOptions.CultureInvariant)]
    private static partial Regex HousingPath();
}

public sealed class HouseStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string PathFor(Guid id) => Path.Combine(DirectoryPath, $"{id:N}.haven.json");
    public string DeletedDirectoryPath => Path.Combine(DirectoryPath, "deleted");

    public string Delete(Guid id)
    {
        var source = PathFor(id);
        if (!File.Exists(source)) throw new FileNotFoundException("This saved home no longer exists.", source);
        Directory.CreateDirectory(DeletedDirectoryPath);
        var recovery = Path.Combine(DeletedDirectoryPath, $"{id:N}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.haven.json");
        // Move within the same store: if archiving fails, the saved home stays put.
        File.Move(source, recovery);
        return recovery;
    }

    private readonly HashSet<string> upgradeChecked = new(StringComparer.OrdinalIgnoreCase);
    public void Save(House house)
    {
        var bytes = HouseFormat.Serialize(house);
        Directory.CreateDirectory(DirectoryPath);
        var path = PathFor(house.Id);
        // The version 1 backup check reads the old file, so it runs once per home, not on every save.
        var first = false; if (house.FormatVersion == 2) lock (upgradeChecked) first = upgradeChecked.Add(path);
        if (house.FormatVersion == 2 && first && File.Exists(path) && Read(path).FormatVersion == 1)
        {
            var backups = Path.Combine(DirectoryPath, "backups");
            Directory.CreateDirectory(backups);
            var backup = Path.Combine(backups, $"{house.Id:N}-v1.haven.json");
            if (!File.Exists(backup)) File.Copy(path, backup);
        }
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static House Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > HouseFormat.MaxBytes) throw new InvalidDataException("House file exceeds 1 MiB.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return HouseFormat.Parse(bytes.ToArray());
    }

    public IReadOnlyList<House> LoadAll(Action<string, Exception>? onError = null)
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var result = new List<House>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.haven.json"))
            try { result.Add(Read(path)); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException)
            { onError?.Invoke(path, ex); }
        return result.OrderBy(x => x.Name).ToArray();
    }
}
