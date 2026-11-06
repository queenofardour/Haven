using System.IO.Compression;
using System.Text;

namespace Haven.Core.World;

/// <summary>A zone of the game's world: its level folder below bg/ (TerritoryType.Bg up to /level/) and its place name.</summary>
public sealed record WorldZone(string Folder, string Name);

/// <summary>How a world model's placements make it solid: not at all, by its bounding box, or by a collision mesh.</summary>
public enum WorldCollision : byte { None, Box, Mesh }

/// <summary>
/// One piece of the game's world: a model or shared group some zone's layout places. Zone is
/// the zone placing it most (its name comes from there), Uses how often the layouts place it, Zones in how many zones.
/// A model keeps the collision its first solid placement gives it (a group's members carry their own). Lights: how many
/// lights a group holds itself (with its nested groups'), which Haven's own lights draw; a model never has any (a
/// lantern's light in the world is placed beside it by the zone's layout, so the model alone only glows).
/// </summary>
/// Effects: how many always-playing effects a group holds (with its nested groups'), which Haven's own effects draw.
/// Sounds: how many always-playing sounds, which Haven's own sounds play.
public sealed record WorldPiece(string Path, int Zone, int Uses, int Zones, WorldCollision Collision = WorldCollision.None, string? CollisionPath = null, int Lights = 0, int Effects = 0, int Sounds = 0)
{
    public bool Group => Path.EndsWith(".sgb", StringComparison.Ordinal);
    /// <summary>Whether placing the piece brings a light of its own (with Haven's lights on).</summary>
    public bool GivesLight => Lights > 0;
    /// <summary>Whether placing the piece brings effects of its own (flames, spray, sparkles; with Haven's effects on).</summary>
    public bool HasEffects => Effects > 0;
    /// <summary>Whether placing the piece brings sounds of its own (a fountain's trickle; with Haven's sounds on).</summary>
    public bool HasSounds => Sounds > 0;

    /// <summary>
    /// What the piece costs the home's scene budget before it is built: a model is one draw object (and one collider
    /// unless the game never makes it solid), known from the index. A group's model count isn't in the index, so it's
    /// null and the caller builds the group to count it.
    /// </summary>
    public (int Objects, int Colliders)? UnbuiltCost => Group ? null : (1, Collision == WorldCollision.None ? 0 : 1);
}

/// <summary>
/// Every model and shared group the game places in its world, as this PC's game data has them. The plugin builds it in
/// the background from the player's own install (WorldIndexBuilder) and keeps it in its config folder, so a world path
/// in a home is drawn only when this game has placed it somewhere: paths are identifiers, never payloads, and nothing is
/// fetched or shipped. Rebuilt when the game's version (or the set of installed expansions) changes, or the format.
/// Saved as gzipped lines of tab-separated fields, which stay small (a few MB for the whole game) and read fast.
/// </summary>
public sealed class WorldIndex
{
    // 2 (0.66.1): each piece's lights; 3 (0.66.5): and effects; 4 (0.66.7): and sounds.
    public const int Format = 4;
    private const string Magic = "haven-world-index";
    public string GameVersion { get; }
    public IReadOnlyList<WorldZone> Zones { get; }
    /// <summary>Sorted by path.</summary>
    public IReadOnlyList<WorldPiece> Pieces { get; }
    private readonly Dictionary<string, WorldPiece> byPath;

    public WorldIndex(string gameVersion, IReadOnlyList<WorldZone> zones, IEnumerable<WorldPiece> pieces)
    {
        GameVersion = gameVersion; Zones = zones;
        Pieces = [.. pieces.OrderBy(x => x.Path, StringComparer.Ordinal)];
        byPath = new(Pieces.Count, StringComparer.Ordinal);
        foreach (var piece in Pieces)
        {
            if (!BuildingParts.IsWorldAsset(piece.Path)) throw new ArgumentException("Not a world piece's path: " + piece.Path);
            if (piece.Zone < 0 || piece.Zone >= zones.Count) throw new ArgumentException("A world piece names no zone: " + piece.Path);
            if (!byPath.TryAdd(piece.Path, piece)) throw new ArgumentException("A world piece is listed twice: " + piece.Path);
        }
    }

    public WorldPiece? Find(string path) => byPath.GetValueOrDefault(path);
    public bool Contains(string path) => byPath.ContainsKey(path);
    public WorldZone ZoneOf(WorldPiece piece) => Zones[piece.Zone];

    public void Write(Stream stream)
    {
        using var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine($"{Magic}\t{Format}\t{Clean(GameVersion)}\t{Zones.Count}\t{Pieces.Count}");
        foreach (var zone in Zones) writer.WriteLine($"{Clean(zone.Folder)}\t{Clean(zone.Name)}");
        foreach (var piece in Pieces)
            writer.WriteLine($"{piece.Path}\t{piece.Zone}\t{piece.Uses}\t{piece.Zones}\t{(int)piece.Collision}\t{piece.CollisionPath}\t{piece.Lights}\t{piece.Effects}\t{piece.Sounds}");
    }

    /// <summary>
    /// The index a stream holds, or null when it was made by another format or for another game version (or is damaged):
    /// then it's built again.
    /// </summary>
    public static WorldIndex? Read(Stream stream, string gameVersion)
    {
        try
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var head = reader.ReadLine()?.Split('\t');
            if (head is not [Magic, var format, var version, var zoneCount, var pieceCount] || format != Format.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || version != Clean(gameVersion) || !int.TryParse(zoneCount, out var zonesExpected) || !int.TryParse(pieceCount, out var piecesExpected)
                || zonesExpected is < 0 or > 100_000 || piecesExpected is < 0 or > 10_000_000) return null;
            var zones = new List<WorldZone>(zonesExpected);
            for (var i = 0; i < zonesExpected; i++)
                if (reader.ReadLine()?.Split('\t') is [var folder, var name]) zones.Add(new(folder, name)); else return null;
            var pieces = new List<WorldPiece>(piecesExpected);
            for (var i = 0; i < piecesExpected; i++)
            {
                if (reader.ReadLine()?.Split('\t') is not [var path, var zone, var uses, var spread, var collision, var collisionPath, var lightCount, var effectCount, var soundCount]
                    || !int.TryParse(zone, out var z) || !int.TryParse(uses, out var u) || !int.TryParse(spread, out var s)
                    || !byte.TryParse(collision, out var c) || c > (byte)WorldCollision.Mesh || !int.TryParse(lightCount, out var l) || l is < 0 or > 100_000
                    || !int.TryParse(effectCount, out var e) || e is < 0 or > 100_000
                    || !int.TryParse(soundCount, out var so) || so is < 0 or > 100_000) return null;
                if (collisionPath.Length > 0 && !IsCollisionPath(collisionPath)) return null;
                pieces.Add(new(path, z, u, s, (WorldCollision)c, collisionPath.Length > 0 ? collisionPath : null, l, e, so));
            }
            return reader.ReadLine() == null ? new WorldIndex(gameVersion, zones, pieces) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or FormatException) { return null; }
    }

    /// <summary>Writes the index to a file all at once (a temporary file moved over the old one).</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        using (var file = File.Create(temporary)) Write(file);
        File.Move(temporary, path, overwrite: true);
    }

    public static WorldIndex? Load(string path, string gameVersion)
    {
        try { using var file = File.OpenRead(path); return Read(file, gameVersion); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A model's collision mesh as layouts name it: a game path ending in .pcb, folders of lowercase names.</summary>
    public static bool IsCollisionPath(string path) => path.Length <= BuildingParts.MaxPathLength && path.EndsWith(".pcb", StringComparison.Ordinal)
        && (path.StartsWith("bg/", StringComparison.Ordinal) || path.StartsWith("bgcommon/", StringComparison.Ordinal))
        && path[..^4].Split('/').All(s => s.Length > 0 && s.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'));

    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
