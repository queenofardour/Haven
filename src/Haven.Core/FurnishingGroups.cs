using System.Numerics;

namespace Haven.Core;

public enum AlignEdge { XMin, XCentre, XMax, ZMin, ZCentre, ZMax, Bottom, Top }

/// <summary>
/// Groups of linked furnishings (a look built from several pieces) and transforms of a selection. A group is the set
/// of furnishings sharing a Group ID; it moves, turns, scales, duplicates and deletes as one, and can be unlinked.
/// </summary>
public static class FurnishingGroups
{
    /// <summary>The pieces a click on one selects: its whole group, or just itself when it has none.</summary>
    public static Guid[] Members(House house, Guid id)
    {
        var item = house.Furnishings.FirstOrDefault(f => f.Id == id);
        if (item?.Group is not { } group) return item == null ? [] : [id];
        return [.. house.Furnishings.Where(f => f.Group == group).Select(f => f.Id)];
    }

    /// <summary>
    /// A follower of a moving leader: the same translation, the same turn about the leader, and the same scale about
    /// it, so the arrangement keeps its shape.
    /// </summary>
    public static Furnishing Follow(Furnishing follower, Furnishing leaderFrom, Furnishing leaderTo)
    {
        var turn = leaderTo.Rotation - leaderFrom.Rotation;
        var factor = leaderFrom.Scale > 0 && float.IsFinite(leaderTo.Scale / leaderFrom.Scale) ? leaderTo.Scale / leaderFrom.Scale : 1;
        var offset = (follower.Translation - leaderFrom.Translation) * factor;
        var moved = Vector3.Transform(offset, Matrix4x4.CreateRotationY(turn * MathF.PI / 180)) + leaderTo.Translation;
        return follower with
        {
            Position = [moved.X, moved.Y, moved.Z],
            Rotation = Degrees(follower.Rotation + turn),
            Scale = Math.Clamp(follower.Scale * factor, 0.05f, 10),
        };
    }

    /// <summary>Turns a selection about its centre (the middle of its pieces' positions), as one change.</summary>
    public static House Turn(House house, IReadOnlyCollection<Guid> ids, float degrees)
    {
        var pieces = house.Furnishings.Where(f => ids.Contains(f.Id)).ToArray();
        if (pieces.Length == 0) return house;
        var centre = pieces.Aggregate(Vector3.Zero, (sum, f) => sum + f.Translation) / pieces.Length;
        var pivot = new Furnishing { Position = [centre.X, centre.Y, centre.Z] };
        var turned = pivot with { Rotation = degrees };
        return Replace(house, pieces.Select(f => Follow(f, pivot, turned)));
    }

    /// <summary>Links the pieces into one new group; any group they belonged to loses them.</summary>
    public static House Link(House house, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count < 2) return house;
        var group = Guid.NewGuid();
        return Replace(house, house.Furnishings.Where(f => ids.Contains(f.Id)).Select(f => f with { Group = group }));
    }

    /// <summary>Takes the pieces out of their groups. A group left with a single piece is dissolved too.</summary>
    public static House Unlink(House house, IReadOnlyCollection<Guid> ids)
    {
        var next = Replace(house, house.Furnishings.Where(f => ids.Contains(f.Id) && f.Group != null).Select(f => f with { Group = null }));
        var lonely = next.Furnishings.Where(f => f.Group != null).GroupBy(f => f.Group).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        return lonely.Count == 0 ? next : Replace(next, next.Furnishings.Where(f => lonely.Contains(f.Group)).Select(f => f with { Group = null }));
    }

    /// <summary>
    /// Lines the pieces up on one edge (or centre) of their bounds, at the selection's extreme for that edge: left edges
    /// to the leftmost, bottoms to the lowest. Pieces without bounds stay put.
    /// </summary>
    public static House Align(House house, IReadOnlyCollection<Guid> ids, AlignEdge edge, Func<Furnishing, PlacementBounds?> bounds)
    {
        var pieces = house.Furnishings.Where(f => ids.Contains(f.Id)).Select(f => (Item: f, Box: bounds(f))).Where(p => p.Box != null).ToArray();
        if (pieces.Length < 2) return house;
        float Value(PlacementBounds box) => edge switch
        {
            AlignEdge.XMin => box.Minimum.X, AlignEdge.XMax => box.Maximum.X, AlignEdge.XCentre => (box.Minimum.X + box.Maximum.X) / 2,
            AlignEdge.ZMin => box.Minimum.Z, AlignEdge.ZMax => box.Maximum.Z, AlignEdge.ZCentre => (box.Minimum.Z + box.Maximum.Z) / 2,
            AlignEdge.Bottom => box.Minimum.Y, _ => box.Maximum.Y,
        };
        var values = pieces.Select(p => Value(p.Box!.Value)).ToArray();
        var target = edge is AlignEdge.XMin or AlignEdge.ZMin or AlignEdge.Bottom ? values.Min()
            : edge is AlignEdge.XMax or AlignEdge.ZMax or AlignEdge.Top ? values.Max() : values.Average();
        var axis = edge is AlignEdge.XMin or AlignEdge.XCentre or AlignEdge.XMax ? 0 : edge is AlignEdge.Bottom or AlignEdge.Top ? 1 : 2;
        return Replace(house, pieces.Select((p, i) => Shift(p.Item, axis, target - values[i])));
    }

    /// <summary>Spaces the pieces' centres evenly along X (axis 0) or Z (axis 2), keeping the two outermost where they are.</summary>
    public static House Distribute(House house, IReadOnlyCollection<Guid> ids, int axis, Func<Furnishing, PlacementBounds?> bounds)
    {
        var pieces = house.Furnishings.Where(f => ids.Contains(f.Id)).Select(f => (Item: f, Box: bounds(f))).Where(p => p.Box != null)
            .Select(p => (p.Item, Centre: (p.Box!.Value.Minimum[axis] + p.Box.Value.Maximum[axis]) / 2)).OrderBy(p => p.Centre).ToArray();
        if (pieces.Length < 3) return house;
        var first = pieces[0].Centre; var step = (pieces[^1].Centre - first) / (pieces.Length - 1);
        return Replace(house, pieces.Select((p, i) => Shift(p.Item, axis, first + step * i - p.Centre)));
    }

    /// <summary>The bounds around every piece of a selection.</summary>
    public static PlacementBounds? Around(IEnumerable<Furnishing> pieces, Func<Furnishing, PlacementBounds?> bounds)
    {
        PlacementBounds? all = null;
        foreach (var piece in pieces)
            if (bounds(piece) is { } box) all = all is { } a ? new(Vector3.Min(a.Minimum, box.Minimum), Vector3.Max(a.Maximum, box.Maximum)) : box;
        return all;
    }

    // Copies -------------------------------------------------------------------------------------------------------

    public const int MaxCopies = 64;

    /// <summary>
    /// A row: the pieces and <paramref name="count"/> − 1 copies of them, each a <paramref name="step"/> further on
    /// (in the home's axes). Copies of a linked group form groups of their own.
    /// </summary>
    public static Furnishing[] Row(IReadOnlyList<Furnishing> pieces, int count, Vector3 step)
    {
        count = Math.Clamp(count, 1, MaxCopies);
        var all = new List<Furnishing>(pieces);
        for (var i = 1; i < count; i++) all.AddRange(CopySet(pieces, f => Moved(f, f.Translation + step * i)));
        return [.. all];
    }

    /// <summary>
    /// A circle: <paramref name="count"/> sets spaced evenly round a centre <paramref name="radius"/> in front of the
    /// pieces' middle (the way the leader faces), each set turned to face it as the first does (chairs round a table).
    /// </summary>
    public static Furnishing[] Circle(IReadOnlyList<Furnishing> pieces, Furnishing leader, int count, float radius)
    {
        count = Math.Clamp(count, 1, MaxCopies);
        var middle = pieces.Aggregate(Vector3.Zero, (sum, f) => sum + f.Translation) / Math.Max(1, pieces.Count);
        var centre = middle + Front(leader.Rotation) * radius;
        var pivot = new Furnishing { Position = [centre.X, centre.Y, centre.Z] };
        var all = new List<Furnishing>(pieces);
        for (var i = 1; i < count; i++)
        {
            var turned = pivot with { Rotation = 360f * i / count };
            all.AddRange(CopySet(pieces, f => Follow(f, pivot, turned)));
        }
        return [.. all];
    }

    /// <summary>The way a piece at this turn faces (its front, +Z turned), in the home's axes.</summary>
    public static Vector3 Front(float degrees) => new(MathF.Sin(degrees * MathF.PI / 180), 0, MathF.Cos(degrees * MathF.PI / 180));

    /// <summary>
    /// The pieces mirrored across a plane across X (axis 0) or Z (axis 2) at <paramref name="plane"/>: positions
    /// reflected, turns and leans mirrored. A piece itself isn't flipped (the game draws it as made), so a piece that
    /// isn't the same left and right keeps its own hand. With <paramref name="keep"/>, the originals stay and the
    /// mirrored ones are copies; without, the pieces themselves are flipped.
    /// </summary>
    public static Furnishing[] Mirror(IReadOnlyList<Furnishing> pieces, int axis, float plane, bool keep)
    {
        Furnishing Flip(Furnishing f)
        {
            var p = f.Translation; p[axis] = 2 * plane - p[axis];
            var yaw = axis == 0 ? -f.Rotation : 180 - f.Rotation;
            return f with { Position = [p.X, p.Y, p.Z], Rotation = Degrees(yaw), Roll = Lean(-f.Roll) };
        }
        return keep ? [.. pieces, .. CopySet(pieces, Flip)] : [.. pieces.Select(Flip)];
    }

    // One copy of a set: new ids, and the set's groups mapped to new groups of its own.
    private static IEnumerable<Furnishing> CopySet(IReadOnlyList<Furnishing> pieces, Func<Furnishing, Furnishing> place)
    {
        var groups = new Dictionary<Guid, Guid>();
        // Only part of a linked group among the pieces: a copy of one piece alone isn't linked to anything.
        var counts = pieces.Where(f => f.Group != null).GroupBy(f => f.Group!.Value).ToDictionary(g => g.Key, g => g.Count());
        foreach (var f in pieces)
        {
            Guid? group = f.Group is { } old && counts[old] > 1 ? groups.TryGetValue(old, out var copy) ? copy : groups[old] = Guid.NewGuid() : null;
            yield return place(f) with { Id = Guid.NewGuid(), Group = group };
        }
    }
    private static Furnishing Moved(Furnishing f, Vector3 to) => f with { Position = [to.X, to.Y, to.Z] };
    private static float Lean(float degrees) { var d = degrees % 360; return d > 180 ? d - 360 : d < -180 ? d + 360 : d; }

    private static Furnishing Shift(Furnishing item, int axis, float by)
    {
        if (!float.IsFinite(by) || by == 0) return item;
        var p = item.Translation; p[axis] += by;
        return item with { Position = [p.X, p.Y, p.Z] };
    }
    private static House Replace(House house, IEnumerable<Furnishing> changed)
    {
        var map = changed.ToDictionary(f => f.Id);
        return map.Count == 0 ? house : house with { Furnishings = [.. house.Furnishings.Select(f => map.GetValueOrDefault(f.Id, f))] };
    }
    private static float Degrees(float value)
    {
        var d = value % 360;
        return d < 0 ? d + 360 : d;
    }
}
