using Haven.Core;

namespace Haven;

/// <summary>The download bar's figures: how much of a home's custom content this PC still has to fetch.</summary>
/// <param name="Missing">Items the home uses that this PC lacks and is still trying to get (failed ones are left out).</param>
/// <param name="Finished">Items fetched since the bar last went quiet.</param>
internal readonly record struct ContentDownloadState(int Missing, int Finished, long Received, long Total, ContentKind[] Kinds)
{
    public bool Active => Missing > 0;
    public int Count => Missing + Finished;
    /// <summary>From 0 to 1: finished items, plus the received share of those under way.</summary>
    public float Progress => Count == 0 ? 1 : Math.Clamp((Finished + (Total > 0 ? (float)Received / Total : 0) * Math.Min(Missing, 1)) / Count, 0, 1);
}

// A guest who agreed to a home's custom content downloads its pictures, songs, furniture and animations in the
// background; until each arrives, its furnishing shows the base item, its frame stays blank and its pose set waits.
// The download bar (FurnishingEditor.Downloads) and the loading screen say so, from this.
internal sealed record FailedDownload(ContentKind Kind, string What, string Reason);

internal sealed partial class HousingController
{
    // Downloads that failed outright (not "not shared yet"): they back off and retry, but no longer hold the bar open.
    private readonly HashSet<string> contentBroken = [];
    private ContentDownloadState contentState = new(0, 0, 0, 0, []);
    private DateTimeOffset contentChecked;
    private Guid? contentHome;

    // Downloads that failed outright stop there: the player sees what didn't arrive and why, and chooses to try again
    // or give up (they then stay as their base item, a blank frame or a silent ball until the next visit).
    private readonly Dictionary<string, FailedDownload> failedDownloads = [];
    /// <summary>What a guest's Haven couldn't download in this home and why, waiting for Try again or Give up.</summary>
    public IReadOnlyCollection<FailedDownload> FailedDownloads => failedDownloads.Values;

    private void DownloadFailed(string hash, ContentKind kind, string reason)
    {
        if (active == null || OwnsActiveHouse) return;
        var what = kind switch
        {
            ContentKind.Picture => active.Furnishings.FirstOrDefault(f => f.CustomPicture == hash) is { } frame ? $"The picture in the {Catalogue.Get(frame).Name}" : "A picture",
            ContentKind.Song => active.Music.Playlist.FirstOrDefault(t => t.CustomMusic == hash)?.Title is { Length: > 0 } title ? $"The song \"{title}\"" : "A song",
            ContentKind.Furniture => active.Furnishings.FirstOrDefault(f => f.CustomFurniture == hash) is { ItemId: not 0 } item ? $"A custom {Catalogue.Get(item).Name}" : "A custom furnishing",
            _ => active.Furnishings.Where(f => f.Poses != null).SelectMany(f => f.Poses!).FirstOrDefault(s => s.Package == hash)?.Name is { Length: > 0 } set
                ? $"The animations for \"{set}\"" : "Some animations",
        };
        failedDownloads[hash] = new(kind, what, reason);
        log.Information("[CONTENT] stopped downloading {Kind} {Hash} until the player asks again: {Reason}", kind, hash[..12], reason);
    }

    /// <summary>Tries every failed download again at once.</summary>
    public void RetryDownloads()
    {
        foreach (var (hash, failed) in failedDownloads)
        {
            contentBroken.Remove(hash);
            switch (failed.Kind)
            {
                case ContentKind.Picture: pictures.RetryDownload(hash); break;
                case ContentKind.Song: songRetries.Remove(hash); break;
                case ContentKind.Furniture: furnitureRetries.Remove(hash); break;
                default: animationRetries.Remove(hash); break;
            }
        }
        failedDownloads.Clear();
    }

    /// <summary>Stops asking: what failed stays as it is for the rest of the visit.</summary>
    public void GiveUpDownloads() => failedDownloads.Clear();

    /// <summary>What is left to download of the home's custom content, checked twice a second.</summary>
    public ContentDownloadState ContentDownloads
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            if (now < contentChecked)
            {
                var latest = ContentTransfers.Snapshot();
                return contentState with { Received = latest.Received, Total = latest.Total };
            }
            contentChecked = now.AddSeconds(.5);
            if (active?.Id != contentHome) { contentHome = active?.Id; contentBroken.Clear(); failedDownloads.Clear(); }
            var missing = new List<ContentKind>();
            if (active != null && IsSharedVisit && !OwnsActiveHouse && CustomPicturesLoaded)
            {
                foreach (var hash in pictureHashes) if (!pictures.HasOriginal(hash) && !pictures.Broken(hash)) missing.Add(ContentKind.Picture);
                foreach (var hash in songHashes) if (!Songs.Has(hash) && !contentBroken.Contains(hash)) missing.Add(ContentKind.Song);
                foreach (var hash in furnitureHashes) if (!Furniture.Has(hash) && !contentBroken.Contains(hash)) missing.Add(ContentKind.Furniture);
                foreach (var hash in PoseSets.Packages(active)) if (!Animations.Has(hash) && !contentBroken.Contains(hash)) missing.Add(ContentKind.Animations);
            }
            var transfers = ContentTransfers.Snapshot();
            if (missing.Count == 0) ContentTransfers.Settle();
            return contentState = new(missing.Count, missing.Count == 0 ? 0 : transfers.Finished, transfers.Received, transfers.Total, missing.Distinct().ToArray());
        }
    }
}
