using System.Text.Json;

namespace Haven.Core;

/// <summary>
/// The Furnish drawer's per-PC Favourites and Recent lists. Keys are catalogue widget IDs: an item ID for furnishings,
/// the building part path for building pieces. Recent holds the last <see cref="RecentLimit"/> picks, newest first.
/// </summary>
public sealed class FurnishLists
{
    public const int RecentLimit = 24;
    public List<string> Favourites { get; set; } = [];
    public List<string> Recent { get; set; } = [];

    public bool IsFavourite(string key) => Favourites.Contains(key);
    public void ToggleFavourite(string key) { if (!Favourites.Remove(key)) Favourites.Add(key); }
    public void Used(string key)
    {
        Recent.Remove(key); Recent.Insert(0, key);
        if (Recent.Count > RecentLimit) Recent.RemoveRange(RecentLimit, Recent.Count - RecentLimit);
    }

    // A missing or unreadable file starts empty: these lists are a convenience, never worth an error.
    public static FurnishLists Load(string path)
    {
        try
        {
            var lists = File.Exists(path) ? JsonSerializer.Deserialize<FurnishLists>(File.ReadAllText(path)) : null;
            if (lists == null) return new();
            lists.Favourites = lists.Favourites.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            lists.Recent = lists.Recent.Where(x => !string.IsNullOrEmpty(x)).Distinct().Take(RecentLimit).ToList();
            return lists;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, overwrite: true);
    }
}
