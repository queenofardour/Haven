using System.Text.Json;

namespace Haven.Core;

/// <summary>The kinds of custom content Haven's server holds per owner.</summary>
public enum ServerContent { Picture, Song, Furniture, Animations }

/// <summary>A server delete still owed: what, for which uploading character (HomeOwners.Key), since when, and when to try next.</summary>
public sealed record PendingDelete(ServerContent Kind, string Hash, string Owner, DateTimeOffset Since, DateTimeOffset Next, int Attempts = 0);

/// <summary>
/// Server deletes that failed (the relay unreachable, a version mismatch, a busy locker): the file is already gone from
/// this PC, so Haven remembers to delete its server copy and tries again, as the character that uploaded it, until it
/// works. After <see cref="GiveUpAfter"/> it stops: the server deletes a copy by itself 30 days after it was last stored.
/// Kept in content/pending-deletes.json; a damaged file starts empty.
/// </summary>
public sealed class PendingDeletes(string path)
{
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(31), FirstRetry = TimeSpan.FromMinutes(1), LongestRetry = TimeSpan.FromHours(1);
    private List<PendingDelete>? entries;
    private List<PendingDelete> Entries => entries ??= Load();

    public IReadOnlyList<PendingDelete> All => Entries;

    public void Add(ServerContent kind, string hash, string owner, DateTimeOffset now)
    {
        if (owner.Length == 0 || Entries.Any(e => e.Kind == kind && e.Hash == hash && e.Owner == owner)) return;
        Entries.Add(new(kind, hash, owner, now, now + FirstRetry));
        Save();
    }

    /// <summary>The deletes this character can try now; those past GiveUpAfter are dropped.</summary>
    public IReadOnlyList<PendingDelete> Due(string owner, DateTimeOffset now)
    {
        if (Entries.RemoveAll(e => now - e.Since > GiveUpAfter) > 0) Save();
        return owner.Length == 0 ? [] : [.. Entries.Where(e => e.Owner == owner && e.Next <= now)];
    }

    public void Done(PendingDelete entry) { if (Entries.RemoveAll(e => e.Kind == entry.Kind && e.Hash == entry.Hash && e.Owner == entry.Owner) > 0) Save(); }

    /// <summary>Another failure: the next try waits twice as long as the last, up to an hour.</summary>
    public void Failed(PendingDelete entry, DateTimeOffset now)
    {
        var i = Entries.FindIndex(e => e.Kind == entry.Kind && e.Hash == entry.Hash && e.Owner == entry.Owner);
        if (i < 0) return;
        var attempts = Entries[i].Attempts + 1;
        var wait = TimeSpan.FromTicks(Math.Min(LongestRetry.Ticks, FirstRetry.Ticks << Math.Min(attempts, 10)));
        Entries[i] = Entries[i] with { Attempts = attempts, Next = now + wait };
        Save();
    }

    private List<PendingDelete> Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<PendingDelete>>(File.ReadAllBytes(path)) ?? [] : []; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return []; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(Entries));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
