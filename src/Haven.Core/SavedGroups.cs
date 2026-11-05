using System.Numerics;
using System.Text.Json;

namespace Haven.Core;

/// <summary>
/// A set of furnishings kept in the player's own library (a dressed table, a bar corner), to drop into any home. The
/// pieces are kept about their middle: X and Z from the middle of their positions, Y from the lowest.
/// </summary>
public sealed record SavedGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Group";
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;
    public Furnishing[] Pieces { get; init; } = [];
}

public static class SavedGroups
{
    public const int MaxPieces = 512, MaxName = 60;

    /// <summary>The pieces about their middle, ids and group links dropped (each placement gets its own).</summary>
    public static SavedGroup Capture(string name, IReadOnlyCollection<Furnishing> pieces)
    {
        if (pieces.Count == 0) throw new InvalidDataException("Choose the pieces to keep first.");
        if (pieces.Count > MaxPieces) throw new InvalidDataException($"A saved group holds up to {MaxPieces} pieces.");
        var middle = pieces.Aggregate(Vector3.Zero, (sum, f) => sum + f.Translation) / pieces.Count;
        middle.Y = pieces.Min(f => f.Translation.Y);
        var kept = pieces.Select(f => { var p = f.Translation - middle; return f with { Id = Guid.NewGuid(), Group = null, Position = [p.X, p.Y, p.Z] }; }).ToArray();
        var group = new SavedGroup { Name = Clean(name), Pieces = kept };
        Validate(group);
        return group;
    }

    /// <summary>
    /// Fresh copies to place: new ids, one new group linking them (when there are several), and which piece leads
    /// the placement (the one nearest the middle, so the set gathers round the cursor).
    /// </summary>
    public static (Furnishing Leader, Furnishing[] Followers) Copies(SavedGroup group)
    {
        Guid? link = group.Pieces.Length > 1 ? Guid.NewGuid() : null;
        var copies = group.Pieces.Select(f => f with { Id = Guid.NewGuid(), Group = link }).ToArray();
        var leader = copies.OrderBy(f => new Vector2(f.Translation.X, f.Translation.Z).LengthSquared()).First();
        return (leader, [.. copies.Where(f => f.Id != leader.Id)]);
    }

    public static string Clean(string name)
    {
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > MaxName) name = name[..MaxName].TrimEnd();
        return name.Length == 0 ? "Group" : name;
    }

    public static void Validate(SavedGroup group)
    {
        if (group.Id == Guid.Empty || string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > MaxName || group.Name.Any(char.IsControl)
            || group.Pieces is not { Length: > 0 and <= MaxPieces })
            throw new InvalidDataException("Invalid saved group.");
        // The pieces follow a home's own rules (a placeholder home holds them).
        HouseFormat.ValidateHousingOptions(new House { TerritoryId = 1, Furnishings = group.Pieces });
    }

    public static byte[] Serialize(SavedGroup group) { Validate(group); return JsonSerializer.SerializeToUtf8Bytes(group, HouseFormat.Json); }
    public static SavedGroup Parse(byte[] bytes)
    {
        if (bytes.Length > HouseFormat.MaxBytes) throw new InvalidDataException("Saved group exceeds 1 MiB.");
        var group = JsonSerializer.Deserialize<SavedGroup>(bytes, HouseFormat.Json) ?? throw new InvalidDataException("Empty saved group.");
        Validate(group);
        return group;
    }
}

/// <summary>The player's saved groups, one file each in <c>groups/</c> (shared by all their characters, like layouts).</summary>
public sealed class SavedGroupStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private string PathFor(Guid id) => Path.Combine(DirectoryPath, $"{id:N}.group.json");

    public IReadOnlyList<SavedGroup> List()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        List<SavedGroup> groups = [];
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.group.json"))
        {
            try { groups.Add(SavedGroups.Parse(File.ReadAllBytes(path))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        }
        return groups.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public void Save(SavedGroup group)
    {
        var bytes = SavedGroups.Serialize(group);
        Directory.CreateDirectory(DirectoryPath);
        var path = PathFor(group.Id); var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Moves the file into <c>deleted/</c>, so a mistake can be undone by hand.</summary>
    public void Delete(Guid id)
    {
        var source = PathFor(id);
        if (!File.Exists(source)) return;
        var deleted = Path.Combine(DirectoryPath, "deleted");
        Directory.CreateDirectory(deleted);
        File.Move(source, Path.Combine(deleted, $"{id:N}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.group.json"));
    }
}
