using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;
using Haven.Core;
using Haven.Core.World;
using StbImageWriteSharp;

namespace Haven;

/// <summary>
/// "Everything in the game": every model and zone group the game places in its world, to place
/// in a Haven home as a building piece. The world index is built from this PC's own game data, in the background the
/// first time it's needed, and kept in the config folder (world/world-index.gz) until the game's version changes; a
/// world path in a home is drawn only when this index holds it and this game has every file it draws (a free trial
/// lacks Endwalker's and Dawntrail's). Each piece becomes a building part when first wanted, on a background task (a
/// placeholder that draws nothing stands in meanwhile), and its thumbnail is drawn the first time the drawer shows it
/// and kept as a PNG. Nothing is fetched or shipped: paths are identifiers, files come from the player's own game.
/// </summary>
internal sealed class WorldCatalogue : IDisposable
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly string folder, thumbnails;
    private readonly CancellationTokenSource stopping = new();
    // The index read, every piece's name, the drawer's entries (in name order, numbers counted: 2 before 10) and the
    // filters' choices, all worked out once.
    private sealed record Loaded(WorldIndex Index, Dictionary<string, WorldName> Names, FurnitureEntry[] Entries, string[] Categories, string[] Zones);
    private Task<Loaded>? loading;
    private readonly Func<BuildingCatalogue?> buildings;
    private readonly ConcurrentDictionary<string, BuildingPart> parts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> building = new(StringComparer.Ordinal), unavailable = new(StringComparer.Ordinal);
    // Bumped when the renderer draws differently, so old thumbnails are drawn again (2: collision and placeholder
    // surfaces, which the game never draws, left out). Older folders are deleted on loading.
    private const int ThumbnailVersion = 2;

    public WorldCatalogue(IDataManager data, string configDirectory, IPluginLog log, Func<BuildingCatalogue?> buildings)
    {
        this.data = data; this.log = log; this.buildings = buildings;
        folder = Path.Combine(configDirectory, "world");
        thumbnails = Path.Combine(folder, $"thumbnails-{ThumbnailVersion}");
    }

    /// <summary>The game data this index was built from: each repository's version, so a patch or an expansion
    /// installed (or a free trial's account upgraded) builds it again.</summary>
    private string GameVersion => string.Join(",", data.GameData.Repositories.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Key + "=" + r.Value.Version));

    /// <summary>Starts reading (or building) the index, once. Cheap to call every frame.</summary>
    public void Start() => loading ??= Task.Run(Load, stopping.Token);

    public bool Ready => loading is { IsCompletedSuccessfully: true };
    public string Status => loading == null ? "" : loading.IsFaulted ? "The world catalogue couldn't be read: " + loading.Exception!.GetBaseException().Message
        : Ready ? "" : "Listing every model in the game's world (only the first time, for a few seconds)…";
    public WorldIndex? Index => Ready ? loading!.Result.Index : null;
    /// <summary>The drawer's entries: every piece, by name.</summary>
    public FurnitureEntry[] Entries => Ready ? loading!.Result.Entries : [];
    public string[] Categories => Ready ? loading!.Result.Categories : [];
    public string[] Zones => Ready ? loading!.Result.Zones : [];

    private void DeleteOldThumbnails()
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var old in Directory.EnumerateDirectories(folder, "thumbnails-*"))
                if (!string.Equals(Path.GetFullPath(old), Path.GetFullPath(thumbnails), StringComparison.OrdinalIgnoreCase)) Directory.Delete(old, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warning(ex, "[WORLD] Old thumbnails couldn't be deleted; they'll be tried again next time."); }
    }

    private Loaded Load()
    {
        var version = GameVersion;
        DeleteOldThumbnails();
        var cache = Path.Combine(folder, "world-index.gz");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var index = WorldIndex.Load(cache, version);
        if (index == null)
        {
            var territories = data.GetExcelSheet<TerritoryType>().Where(t => !t.Bg.IsEmpty)
                .Select(t => new WorldTerritory(t.RowId, t.Bg.ToString(), t.PlaceName.ValueNullable?.Name.ToString() ?? "")).ToArray();
            index = WorldIndexBuilder.Build(territories, data.GetFile<LgbFile>, data.GetFile<SgbFile>, version, stopping.Token);
            try { index.Save(cache); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warning(ex, "[WORLD] The world index couldn't be kept; it will be built again next time."); }
            log.Information("[WORLD] built the world index in {Seconds:F1} s: {Pieces} pieces in {Zones} zones", clock.Elapsed.TotalSeconds, index.Pieces.Count, index.Zones.Count);
        }
        var names = WorldNames.All(index, WorldNameTable.Embedded());
        // A group is the game's own assembly of parts (an airship with its propeller and lanterns, moving as it does);
        // its parts are listed on their own too, so a group says it's a set.
        foreach (var piece in index.Pieces.Where(p => p.Group)) names[piece.Path] = WorldNames.AsSet(names[piece.Path]);
        var hidden = WorldHidden.Embedded();
        var entries = index.Pieces.Where(p => !hidden.Contains(p.Path)).Select(p => new FurnitureEntry(0, 0, 0, 0, names[p.Path].Name, names[p.Path].Category, index.ZoneOf(p).Name, 0, 0, p.Path))
            .Select(e => (Entry: e, Key: NameOrder(e.Name))).OrderBy(x => x.Key.Text, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Key.Number)
            .Select(x => x.Entry).ToArray();
        return new(index, names, entries, [.. entries.Select(e => e.Category).Distinct().Order()], [.. index.Zones.Select(z => z.Name).Distinct().Order()]);
    }

    // A name's text and its closing number ("Kugane lantern 12": "Kugane lantern", 12), so variants sort by number.
    private static (string Text, int Number) NameOrder(string name)
    {
        var space = name.LastIndexOf(' ');
        return space > 0 && int.TryParse(name.AsSpan(space + 1), out var number) ? (name[..space], number) : (name, 0);
    }

    /// <summary>Whether a world piece brings a light of its own (a group holding one; with Haven's lights on).</summary>
    public bool GivesLight(string path) => Index?.Find(path) is { GivesLight: true };
    /// <summary>Whether a world piece brings always-playing effects of its own (with Haven's effects on).</summary>
    public bool HasEffects(string path) => Index?.Find(path) is { HasEffects: true };
    /// <summary>Whether a world piece brings always-playing sounds of its own (with Haven's sounds on).</summary>
    public bool HasSounds(string path) => Index?.Find(path) is { HasSounds: true };
    /// <summary>Whether a path is a world piece this PC's index holds (so it may be placed and drawn).</summary>
    public bool Contains(string path) => Index?.Contains(path) == true;

    /// <summary>
    /// A world piece's building part: the real one once built, else a placeholder that draws nothing (while the index
    /// is read or the part is built, and for a piece this game can't draw: not in its index, or files it lacks).
    /// </summary>
    public BuildingPart Part(string path, bool wait = false)
    {
        if (parts.TryGetValue(path, out var part)) return part;
        Start();
        if (wait) loading?.Wait();
        if (Index is not { } index || index.Find(path) is not { } piece || unavailable.ContainsKey(path) || buildings() is not { } catalogue) return Placeholder(path);
        if (wait) return Build(catalogue, index, piece) ?? Placeholder(path);
        // Built one at a time on one background task, in the order wanted (a home full of world pieces fills in a
        // moment after entering, without a burst of tasks all reading the game data at once).
        if (building.TryAdd(path, 0))
        {
            toBuild.Enqueue((catalogue, index, piece));
            if (Interlocked.Exchange(ref builder, 1) == 0) Task.Run(BuildQueued);
        }
        return Placeholder(path);
    }

    /// <summary>The index's piece for a world path whose part isn't built yet (so it still draws as a placeholder), else null.</summary>
    public WorldPiece? Unbuilt(string path) => Ready && !parts.ContainsKey(path) && !unavailable.ContainsKey(path) && Index!.Find(path) is { } piece ? piece : null;

    /// <summary>
    /// The scene cost of an unbuilt piece for the budget check. A model is known from the index; a group is built now
    /// (once; the part is kept) and counted by what it holds. A piece this game can't draw costs nothing.
    /// </summary>
    public (int Objects, int Colliders) UnbuiltCost(WorldPiece piece)
    {
        if (piece.UnbuiltCost is { } cost) return cost;
        var part = Part(piece.Path, wait: true).Prefab;
        return (part.Models.Length + part.Lights.Length, part.Colliders.Length);
    }

    private readonly ConcurrentQueue<(BuildingCatalogue Catalogue, WorldIndex Index, WorldPiece Piece)> toBuild = new();
    private int builder;
    private void BuildQueued()
    {
        while (true)
        {
            while (!stopping.IsCancellationRequested && toBuild.TryDequeue(out var job)) Build(job.Catalogue, job.Index, job.Piece);
            Volatile.Write(ref builder, 0);
            // A piece queued after the loop emptied but before the flag dropped would otherwise wait for the next one.
            if (stopping.IsCancellationRequested || toBuild.IsEmpty || Interlocked.Exchange(ref builder, 1) != 0) return;
        }
    }

    private BuildingPart? Build(BuildingCatalogue catalogue, WorldIndex index, WorldPiece piece)
    {
        if (parts.TryGetValue(piece.Path, out var built)) return built;
        try
        {
            var name = loading!.Result.Names[piece.Path];
            var part = catalogue.World(piece, name, index.ZoneOf(piece).Name, Readable);
            parts[piece.Path] = part;
            return part;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (unavailable.TryAdd(piece.Path, 0)) log.Warning("[WORLD] {Path} isn't drawn: {Message}", piece.Path, ex.Message);
            return null;
        }
        finally { building.TryRemove(piece.Path, out _); }
    }

    // A file the game data holds, as bytes; null when it hasn't it (never asked of the game itself).
    private byte[]? Readable(string path)
    {
        try { return data.FileExists(path) ? data.GetFile(path)?.Data : null; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    // Placeholders are kept once the index is read (before, each names a piece only "World piece").
    private readonly ConcurrentDictionary<string, BuildingPart> placeholders = new(StringComparer.Ordinal);
    private BuildingPart Placeholder(string path) => Ready ? placeholders.GetOrAdd(path, MakePlaceholder) : MakePlaceholder(path);
    private BuildingPart MakePlaceholder(string path)
    {
        var name = Ready && loading!.Result.Names.TryGetValue(path, out var n) ? n : new WorldName("World piece", WorldNames.Other);
        var zone = Index?.Find(path) is { } piece ? Index.ZoneOf(piece).Name : "World";
        return new(new FurnitureEntry(0, 0, 0, 0, name.Name, name.Category, zone, 0, 0, path), new FurniturePrefab([], [], [], []), zone, []);
    }

    // Thumbnails --------------------------------------------------------------

    private readonly ConcurrentDictionary<string, string?> thumbnailFiles = new(StringComparer.Ordinal);
    private readonly LinkedList<string> wanted = new();
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private int drawing;
    private const int MaxWanted = 256;

    /// <summary>An effect piece's tint as its zone places it, for its thumbnail.</summary>
    public Func<string, float[]?>? EffectColour { get; set; }

    /// <summary>
    /// A piece's thumbnail file (a PNG), or null while it's drawn, or when the piece draws nothing (the drawer then shows
    /// a plain tile). The newest asked are drawn first, one at a time, so the grid on screen fills in first.
    /// </summary>
    public string? Thumbnail(string path)
    {
        if (thumbnailFiles.TryGetValue(path, out var file)) return file;
        var png = ThumbnailFile(path);
        if (File.Exists(png)) return thumbnailFiles[path] = png;
        if (File.Exists(Path.ChangeExtension(png, ".none"))) return thumbnailFiles[path] = null;
        lock (wanted)
        {
            if (queued.Add(path)) wanted.AddFirst(path);
            else { wanted.Remove(path); wanted.AddFirst(path); }
            while (wanted.Count > MaxWanted) { queued.Remove(wanted.Last!.Value); wanted.RemoveLast(); }
        }
        if (Interlocked.Exchange(ref drawing, 1) == 0) Task.Run(DrawThumbnails);
        return null;
    }

    private string ThumbnailFile(string path)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..20].ToLowerInvariant();
        return Path.Combine(thumbnails, hash[..2], hash + ".png");
    }

    private void DrawThumbnails()
    {
        var textures = new Dictionary<string, ThumbnailTexture?>(StringComparer.Ordinal);
        while (!stopping.IsCancellationRequested)
        {
            string path;
            lock (wanted)
            {
                if (wanted.First is not { } first) { Volatile.Write(ref drawing, 0); return; }
                path = first.Value; wanted.RemoveFirst(); queued.Remove(path);
            }
            if (textures.Count > 400) textures.Clear();
            var png = ThumbnailFile(path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(png)!);
                // A world effect (the drawer's Effects) is drawn from its effect file; a piece from its models.
                var picture = Haven.Core.World.WorldAmbient.IsEffectPath(path) ? EffectThumbnails.Render(path, Readable, EffectColour?.Invoke(path), textures)
                    : Contains(path) ? WorldThumbnails.Render(WorldIndexBuilder.PieceModels(path, p => data.FileExists(p) ? data.GetFile<SgbFile>(p) : null), Readable, textures) : null;
                if (picture == null) { File.WriteAllBytes(Path.ChangeExtension(png, ".none"), []); thumbnailFiles[path] = null; continue; }
                using (var stream = File.Create(png + ".tmp"))
                    new ImageWriter().WritePng(picture, ThumbnailRenderer.Size, ThumbnailRenderer.Size, ColorComponents.RedGreenBlueAlpha, stream);
                File.Move(png + ".tmp", png, overwrite: true);
                thumbnailFiles[path] = png;
            }
            // A thumbnail that can't be drawn or kept leaves the tile plain; it's never an error.
            catch (Exception ex) when (ex is not OutOfMemoryException) { thumbnailFiles[path] = null; log.Debug("[WORLD] no thumbnail for {Path}: {Message}", path, ex.Message); }
        }
        Volatile.Write(ref drawing, 0);
    }

    // The token isn't disposed: background work still reads it as it winds down.
    public void Dispose() => stopping.Cancel();
}
