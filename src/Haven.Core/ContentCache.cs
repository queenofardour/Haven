namespace Haven.Core;

/// <summary>
/// The custom content the homes and saved groups on this PC use: pictures (originals), the frames built from them,
/// furniture and animation packages, and songs (playlists and moods). What isn't listed here and isn't the player's own
/// was downloaded for someone else's home, and may be cleared: it is downloaded again on the next visit.
/// </summary>
public sealed class ContentUse
{
    public HashSet<string> Pictures { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Frames { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Furniture { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Animations { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Songs { get; } = new(StringComparer.Ordinal);
    public HashSet<Guid> Npcs { get; } = [];

    public static ContentUse Of(IEnumerable<House> homes, IEnumerable<SavedGroup> groups)
    {
        var use = new ContentUse();
        foreach (var home in homes)
        {
            use.Add(home.Furnishings);
            foreach (var track in home.Music.Playlist) if (track.CustomMusic is { } song) use.Songs.Add(song);
            foreach (var mood in home.Interior.Moods ?? [])
                foreach (var track in mood.Music?.Playlist ?? []) if (track.CustomMusic is { } song) use.Songs.Add(song);
            foreach (var npc in home.Npcs ?? []) use.Npcs.Add(npc.Id);
        }
        foreach (var group in groups) use.Add(group.Pieces);
        return use;
    }

    private void Add(IEnumerable<Furnishing> pieces)
    {
        foreach (var piece in pieces)
        {
            if (piece.CustomPicture is { } picture) { Pictures.Add(picture); Frames.Add(PictureCanvas.FrameKey(picture, piece.Framing, piece.Portrait)); }
            if (piece.CustomFurniture is { } furniture) Furniture.Add(furniture);
            foreach (var set in piece.Poses ?? []) if (set.Package is { } package) Animations.Add(package);
        }
    }
}

/// <summary>A downloaded item kept on this PC: what it is, how much room it takes, and when a home last used it.</summary>
public sealed record CachedContent(string Kind, string Hash, long Bytes, DateTime LastUsed);

/// <summary>
/// How much of what was downloaded for other people's homes this PC keeps (Settings → Custom content → On this PC).
/// Past the limit, the items used longest ago go first.
/// </summary>
public static class ContentCache
{
    public const long DefaultLimit = 2_000_000_000;
    /// <summary>The limits Settings offers, in bytes (decimal units, as Settings shows sizes).</summary>
    public static readonly long[] Limits = [500_000_000, 1_000_000_000, 2_000_000_000, 5_000_000_000, 10_000_000_000];

    /// <summary>A limit Settings offers, or the default for anything else (a hand-edited or damaged setting).</summary>
    public static long Sane(long limit) => Limits.Contains(limit) ? limit : DefaultLimit;

    /// <summary>What to clear so the rest fits the limit: the items used longest ago, until it does.</summary>
    public static IReadOnlyList<CachedContent> Over(IReadOnlyCollection<CachedContent> cached, long limit)
    {
        var total = cached.Sum(c => Math.Max(0, c.Bytes));
        var clear = new List<CachedContent>();
        foreach (var item in cached.OrderBy(c => c.LastUsed).ThenBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Hash, StringComparer.Ordinal))
        {
            if (total <= limit) break;
            clear.Add(item);
            total -= Math.Max(0, item.Bytes);
        }
        return clear;
    }
}
