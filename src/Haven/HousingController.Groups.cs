using Haven.Core;

namespace Haven;

// Moving, duplicating and arranging several furnishings at once: a linked group (FurnishingGroups) or a selection.
// One piece leads (the placement, which the cursor, handles, keys and inspector drive as before); the others follow
// its move, turn and scale about it, and everything commits as one change.
internal sealed partial class HousingController
{
    // The followers as they were when the move began, and the leader then.
    private Furnishing[] followers = [];
    private Furnishing? leaderOrigin;
    private Furnishing[] followerPreviews = [];
    private Furnishing? previewedFrom;

    public bool MovingGroup => followers.Length > 0 && placement != null;
    public int MovingCount => placement == null ? 0 : followers.Length + 1;
    /// <summary>Where the followers are now, following the leader's preview.</summary>
    public IReadOnlyList<Furnishing> FollowerPreviews
    {
        get
        {
            if (!MovingGroup || leaderOrigin == null) return [];
            if (!ReferenceEquals(previewedFrom, placement))
            { previewedFrom = placement; followerPreviews = [.. followers.Select(f => FurnishingGroups.Follow(f, leaderOrigin, placement!))]; }
            return followerPreviews;
        }
    }
    private bool IsFollower(Guid id) { foreach (var f in followers) if (f.Id == id) return true; return false; }

    /// <summary>Moves several placed pieces together, led by one of them.</summary>
    public void BeginGroupPlacement(IReadOnlyCollection<Guid> ids, Guid leader)
    {
        if (active == null) return;
        if (MovingExisting) KeepPlacement(); // A move under way is kept first, so the pieces are read where they stand now.
        var item = active.Furnishings.First(f => f.Id == leader);
        BeginPlacement(item, leader);
        leaderOrigin = item;
        followers = [.. active.Furnishings.Where(f => ids.Contains(f.Id) && f.Id != leader)];
        if (followers.Length > 0) Status = $"Moving {followers.Length + 1} pieces together; click, right-click or Done sets them down, Escape cancels.";
    }

    /// <summary>Copies several pieces as a set, standing exactly on the originals until they're moved.</summary>
    public void BeginGroupDuplicate(IReadOnlyCollection<Guid> ids, Guid leader)
    {
        if (active == null) return;
        if (MovingExisting) KeepPlacement();
        var item = active.Furnishings.First(f => f.Id == leader);
        // Copies form groups of their own, matching the originals' grouping.
        var groups = new Dictionary<Guid, Guid>();
        Guid? Regroup(Guid? group) => group is { } old ? groups.TryGetValue(old, out var copy) ? copy : groups[old] = Guid.NewGuid() : null;
        BeginPlacement(item with { Group = Regroup(item.Group) }, inPlace: true);
        leaderOrigin = item;
        followers = [.. active.Furnishings.Where(f => ids.Contains(f.Id) && f.Id != leader).Select(f => f with { Id = Guid.NewGuid(), Group = Regroup(f.Group) })];
    }

    // Arrangements (rows, circles, mirrors): a preview of the pieces and their copies, led by one of the originals,
    // that the inspector's fields reshape while it shows; set down like any move (click, right-click, Done), Escape
    // or Cancel throws it away.
    private Func<IReadOnlyList<Furnishing>, IReadOnlyList<Furnishing>>? arrange;
    private Furnishing[] arrangeOriginals = [];
    public bool Arranging => arrange != null && placement != null;
    /// <summary>How many pieces the arrangement sets down (the originals and their copies).</summary>
    public int ArrangedCount => Arranging ? followers.Length + 1 : 0;
    /// <summary>The most sets (the originals counting as one) the home has room for: past it, setting down would fail.</summary>
    public int ArrangeRoom => Math.Max(2, (Haven.Core.HouseFormat.MaxObjects - (active?.Furnishings.Length ?? 0) + arrangeOriginals.Length) / Math.Max(1, arrangeOriginals.Length));

    public void BeginArrangement(IReadOnlyCollection<Guid> ids, Guid leader, Func<IReadOnlyList<Furnishing>, IReadOnlyList<Furnishing>> make)
    {
        if (active == null) return;
        if (!CanFurnish) throw new InvalidOperationException("You need decorating access to arrange furnishings.");
        if (MovingExisting) KeepPlacement();
        var originals = active.Furnishings.Where(f => ids.Contains(f.Id)).ToArray();
        if (originals.All(f => f.Id != leader)) return;
        BeginPlacement(originals.First(f => f.Id == leader), leader);
        arrangeOriginals = originals; arrange = make;
        var made = make(originals);
        placement = made.First(f => f.Id == leader);
        leaderOrigin = placement; previewedFrom = null;
        followers = [.. made.Where(f => f.Id != leader)];
        Status = "Shape the copies in the inspector, then set them down: click, right-click or Done. Escape cancels.";
    }

    /// <summary>Reshapes the arrangement under way, keeping wherever its leader has been moved since.</summary>
    public void UpdateArrangement(Func<IReadOnlyList<Furnishing>, IReadOnlyList<Furnishing>> make)
    {
        if (!Arranging || leaderOrigin == null) return;
        arrange = make;
        var made = make(arrangeOriginals);
        var lead = made.FirstOrDefault(f => f.Id == leaderOrigin.Id);
        if (lead == null) return;
        placement = FurnishingGroups.Follow(lead, leaderOrigin, placement!);
        leaderOrigin = lead; previewedFrom = null;
        followers = [.. made.Where(f => f.Id != lead.Id)];
    }

    // Saved groups (SavedGroups): the player's own library of sets, dropped into any home like a new piece.
    private SavedGroupStore? groupStore;
    private SavedGroupStore GroupLibrary => groupStore ??= new(Path.Combine(Path.GetDirectoryName(Store.DirectoryPath)!, "groups"));
    public string GroupsFolder => GroupLibrary.DirectoryPath;
    private IReadOnlyList<SavedGroup>? savedGroups;
    public IReadOnlyList<SavedGroup> SavedGroups => savedGroups ??= GroupLibrary.List();

    public void SaveGroup(IReadOnlyCollection<Guid> ids, string name)
    {
        if (active == null) return;
        var pieces = active.Furnishings.Where(f => ids.Contains(f.Id)).ToArray();
        var taken = SavedGroups.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = Haven.Core.SavedGroups.Clean(name);
        for (var i = 2; taken.Contains(unique); i++) unique = Haven.Core.SavedGroups.Clean(name) + $" ({i})";
        GroupLibrary.Save(Haven.Core.SavedGroups.Capture(unique, pieces));
        savedGroups = null;
        Status = $"Kept \"{unique}\" in Your groups ({pieces.Length} pieces). Find it under Furnish → Yours.";
    }

    /// <summary>Picks up a saved group to place: it follows the cursor like a new piece, its pieces round the leader.</summary>
    public void BeginSavedGroup(Guid id)
    {
        if (active == null) return;
        var group = SavedGroups.FirstOrDefault(g => g.Id == id) ?? throw new InvalidOperationException("That group is no longer saved.");
        var missing = group.Pieces.Where(f => f.CustomFurniture is { } hash && !Furniture.Has(hash)).Count();
        var (leader, rest) = Haven.Core.SavedGroups.Copies(group);
        BeginPlacement(leader);
        leaderOrigin = leader; previewedFrom = null;
        followers = rest;
        Status = $"Placing \"{group.Name}\": click, right-click or Done sets it down, Escape cancels."
            + (missing > 0 ? $" {missing} of its custom pieces aren't on this PC and won't show." : "");
    }

    public void RenameSavedGroup(Guid id, string name)
    {
        var group = SavedGroups.FirstOrDefault(g => g.Id == id) ?? throw new InvalidOperationException("That group is no longer saved.");
        GroupLibrary.Save(group with { Name = Haven.Core.SavedGroups.Clean(name) });
        savedGroups = null;
    }

    public void DeleteSavedGroup(Guid id) { GroupLibrary.Delete(id); savedGroups = null; Status = "Deleted the group. A copy stays in the groups folder's deleted folder."; }

    // The placement's commit, with any followers in the same change.
    private House WithPlacement(House house, Furnishing item)
    {
        var moved = FollowerPreviews.Append(item).ToArray();
        var ids = moved.Select(f => f.Id).ToHashSet();
        return house with { Furnishings = [.. house.Furnishings.Where(x => !ids.Contains(x.Id)), .. moved] };
    }
    private void ClearFollowers() { followers = []; leaderOrigin = null; followerPreviews = []; previewedFrom = null; arrange = null; arrangeOriginals = []; }

    public void RemoveMany(IReadOnlyCollection<Guid> ids)
    {
        if (active == null || ids.Count == 0) return;
        CancelPlacement();
        if (active.Furnishings.Any(x => ids.Contains(x.Id))) Commit(active with { Furnishings = [.. active.Furnishings.Where(x => !ids.Contains(x.Id))] });
    }
    public void LinkGroup(IReadOnlyCollection<Guid> ids) => Arrange(house => FurnishingGroups.Link(house, ids));
    public void UnlinkGroup(IReadOnlyCollection<Guid> ids) => Arrange(house => FurnishingGroups.Unlink(house, ids));
    public void AlignSelection(IReadOnlyCollection<Guid> ids, AlignEdge edge) => Arrange(house => FurnishingGroups.Align(house, ids, edge, Catalogue.Bounds));
    public void DistributeSelection(IReadOnlyCollection<Guid> ids, int axis) => Arrange(house => FurnishingGroups.Distribute(house, ids, axis, Catalogue.Bounds));
    public void TurnSelection(IReadOnlyCollection<Guid> ids, float degrees) => Arrange(house => FurnishingGroups.Turn(house, ids, degrees));
    private void Arrange(Func<House, House> change)
    {
        if (active == null) return;
        if (!CanFurnish) throw new InvalidOperationException("You need decorating access to arrange furnishings.");
        CancelPlacement();
        var next = change(active);
        if (!ReferenceEquals(next, active)) Commit(next);
    }
}
