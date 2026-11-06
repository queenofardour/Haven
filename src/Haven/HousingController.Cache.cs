using Haven.Core;

namespace Haven;

// Custom content this PC keeps but no longer needs (Settings → Custom content → On this PC):
// - what was downloaded for other people's homes (pictures, furniture, animations) is kept up to a limit, the items
//   used longest ago going first; anything a home or saved group on this PC uses, and the player's own content, is
//   never cleared. A cleared item is downloaded again on the next visit that needs it.
// - built picture frames no home or saved group shows are deleted once a session (they are rebuilt from the original).
// Both run only outside a home, one item a frame, so nothing is cleared while a home might be loading it.
internal sealed partial class HousingController
{
    private readonly Queue<(string Kind, string Hash)> clearing = new();
    private bool contentTidied;
    private string? touchedHome;
    private (int Items, long Bytes) cleared;
    private bool clearingAll;

    // Asked once a session (the answer only matters before the first tidy).
    private bool? noHomeFiles;
    private bool NoHomeFiles()
    {
        if (noHomeFiles is { } known) return known;
        try { return (noHomeFiles = !Directory.Exists(Store.DirectoryPath) || !Directory.EnumerateFiles(Store.DirectoryPath).Any()).Value; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (noHomeFiles = false).Value; }
    }

    /// <summary>What the homes and saved groups on this PC use, the open one as it stands now.</summary>
    private ContentUse ContentInUse() => ContentUse.Of(allHouses.Where(h => h.Id != active?.Id).Append(active).OfType<House>(), SavedGroups);

    /// <summary>The most this PC keeps of content downloaded for other people's homes (bytes).</summary>
    public long DownloadLimit
    {
        get => ContentCache.Sane(engine.HousingDownloadLimit);
        set { engine.HousingDownloadLimit = ContentCache.Sane(value); Run(() => TidyDownloads(all: false)); }
    }
    /// <summary>Whether clearing downloaded content is under way.</summary>
    public bool ClearingDownloads => clearing.Count > 0;

    /// <summary>Content downloaded for other people's homes that no home or saved group on this PC uses.</summary>
    public IReadOnlyList<CachedContent> Downloads()
    {
        var use = ContentInUse();
        return [.. pictures.Downloaded().Where(d => !use.Pictures.Contains(d.Hash)),
            .. Furniture.Downloaded().Where(d => !use.Furniture.Contains(d.Hash)),
            .. Animations.Downloaded().Where(d => !use.Animations.Contains(d.Hash))];
    }
    /// <summary>Set by the plugin: removes the mods this PC keeps for NPCs (NpcModShare.RemoveKept), returning how many.</summary>
    public Func<IReadOnlyCollection<Guid>, int>? RemoveKeptNpcMods { get; set; }

    /// <summary>The homes on this PC each NPC lives in (by NPC id), and its name there.</summary>
    public IReadOnlyDictionary<Guid, (string Name, string[] Homes)> NpcHomes()
    {
        var found = new Dictionary<Guid, (string Name, string[] Homes)>();
        foreach (var home in allHouses.Where(h => h.Id != active?.Id).Append(active).OfType<House>())
            foreach (var npc in home.Npcs ?? [])
                found[npc.Id] = found.TryGetValue(npc.Id, out var known) ? (known.Name, [.. known.Homes, home.Name]) : (npc.Name, [home.Name]);
        return found;
    }

    /// <summary>Removes the mods this PC keeps for these NPCs (Settings). Outside a home only.</summary>
    public void RemoveNpcMods(IReadOnlyCollection<Guid> npcs)
    {
        if (active != null) throw new InvalidOperationException("Leave the home first: Haven removes NPCs' mods only outside a home.");
        var removed = RemoveKeptNpcMods?.Invoke(npcs) ?? 0;
        Status = removed == 1 ? "Removed one NPC's mods from this PC." : $"Removed {removed} NPCs' mods from this PC.";
    }

    /// <summary>How much room built picture frames take on this PC.</summary>
    public long FramesSize() => pictures.FramesSize();

    /// <summary>Clears what is over the limit, or (all) everything downloaded that no home on this PC uses. Outside a home only.</summary>
    public void TidyDownloads(bool all)
    {
        if (active != null) { if (all) throw new InvalidOperationException("Leave the home first: Haven clears downloaded content only outside a home."); return; }
        if (clearing.Count > 0) return;
        var downloads = Downloads();
        var chosen = all ? downloads : ContentCache.Over([.. downloads], DownloadLimit);
        foreach (var item in chosen) clearing.Enqueue((item.Kind, item.Hash));
        cleared = default; clearingAll = all;
        if (chosen.Count > 0) log.Information("[CACHE] clearing {Count} downloaded item(s), {Bytes:N0} bytes ({Why}).", chosen.Count, chosen.Sum(c => c.Bytes), all ? "asked" : $"over the limit of {DownloadLimit:N0}");
        else if (all) Status = "There's no downloaded content to clear.";
    }

    // Each frame: once a session, outside a home, built frames no home shows are deleted and downloads over the limit
    // queued; queued items are cleared one a frame while the player stays outside a home. Entering a home notes that
    // its downloaded content was used now, so it is kept longest.
    private void UpdateContentCache()
    {
        if (active?.Id.ToString() is var home && home != touchedHome)
        {
            touchedHome = home;
            if (active != null)
                foreach (var piece in active.Furnishings)
                {
                    if (piece.CustomPicture is { } picture) pictures.Touch(picture);
                    if (piece.CustomFurniture is { } furniture) Furniture.Touch(furniture);
                    foreach (var set in piece.Poses ?? []) if (set.Package is { } package) Animations.Touch(package);
                }
        }
        if (active != null) { if (clearing.Count > 0) { log.Information("[CACHE] entered a home: {Count} item(s) left to clear next time.", clearing.Count); clearing.Clear(); } return; }
        // A player with no homes of their own (only ever a guest) is tidied too, but only when the homes folder holds no
        // files at all: homes that failed to load must not make their content look unused.
        if (!contentTidied && pictures.Migrating.IsCompleted && (carriedOver || allHouses.Count == 0 && NoHomeFiles()))
        {
            contentTidied = true;
            var (frames, bytes) = pictures.TidyFrames(ContentInUse().Frames);
            if (frames > 0) log.Information("[CACHE] deleted {Count} built picture frame(s) no home shows ({Bytes:N0} bytes).", frames, bytes);
            TidyDownloads(all: false);
        }
        if (!clearing.TryDequeue(out var next)) return;
        // Asked again for each item: a home saved since (a copy of a friend's) may use it now.
        var use = ContentInUse();
        var freed = next.Kind switch
        {
            "picture" when !use.Pictures.Contains(next.Hash) => pictures.ForgetDownloaded(next.Hash),
            "furniture" when !use.Furniture.Contains(next.Hash) => Furniture.ForgetDownloaded(next.Hash),
            "animations" when !use.Animations.Contains(next.Hash) => Animations.ForgetDownloaded(next.Hash),
            _ => 0,
        };
        if (freed > 0) cleared = (cleared.Items + 1, cleared.Bytes + freed);
        if (clearing.Count > 0) return;
        log.Information("[CACHE] cleared {Count} downloaded item(s), {Bytes:N0} bytes.", cleared.Items, cleared.Bytes);
        if (clearingAll) Status = cleared.Items == 0 ? "There's no downloaded content to clear."
            : $"Cleared {cleared.Items} downloaded item{(cleared.Items == 1 ? "" : "s")} ({ContentSizes.Megabytes(cleared.Bytes)}). Haven downloads them again when a home needs them.";
    }
}
