using System.Numerics;

namespace Haven.Core;

public readonly record struct PlacementKeys(int X = 0, int Z = 0, int Y = 0, int Turn = 0, int Size = 0, bool Follow = false, bool Confirm = false, bool Fine = false, bool Coarse = false);
public readonly record struct PlacementCommand(Vector3 Delta, float Turn, float ScaleFactor, bool ToggleFollow, bool Confirm);

public sealed class PlacementKeyInput
{
    private PlacementKeys previous;
    private double nextRepeat;
    public PlacementCommand Update(PlacementKeys keys, double now, bool blocked)
    {
        if (blocked) { previous = keys; nextRepeat = now + 0.35; return new(default, 0, 1, false, false); }
        var changed = keys.X != previous.X || keys.Z != previous.Z || keys.Y != previous.Y || keys.Turn != previous.Turn || keys.Size != previous.Size;
        var fire = changed || now >= nextRepeat;
        var factor = keys.Fine ? 0.1f : keys.Coarse ? 10 : 1;
        var command = new PlacementCommand(fire ? new Vector3(keys.X, keys.Y, keys.Z) * (0.1f * factor) : default,
            fire ? keys.Turn * (keys.Fine ? 1 : keys.Coarse ? 45 : 15) : 0,
            fire ? MathF.Pow(1.05f, keys.Size * factor) : 1,
            keys.Follow && !previous.Follow, keys.Confirm && !previous.Confirm);
        if (fire) nextRepeat = now + (changed ? 0.35 : 0.08);
        previous = keys;
        return command;
    }
    public void Reset() { previous = default; nextRepeat = double.MaxValue; }
}

public sealed class ObjectClickInput
{
    private string? last;
    private double time;
    private Vector2 point;
    public bool Click(string? key, double now, Vector2 cursor, double interval = 0.3)
    {
        var twice = key != null && key == last && now - time <= interval && now >= time && Vector2.DistanceSquared(point, cursor) <= 36;
        last = twice ? null : key; time = now; point = cursor;
        return twice;
    }
    public void Reset() => last = null;
}

public readonly record struct MagnetSnap(Vector3 Offset, int[] Targets);

public readonly record struct PlacementBounds(Vector3 Minimum, Vector3 Maximum)
{
    public static PlacementBounds Transform(Vector3 min, Vector3 max, Matrix4x4 matrix)
    {
        var a = new Vector3(float.MaxValue); var b = new Vector3(float.MinValue);
        for (var i = 0; i < 8; i++)
        {
            var p = Vector3.Transform(new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z), matrix);
            a = Vector3.Min(a, p); b = Vector3.Max(b, p);
        }
        return new(a, b);
    }
}

public static class PlacementTools
{
    public static Vector3 ScaleProportionally(Vector3 scale, float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0 || !float.IsFinite(scale.LengthSquared()) || scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) return scale;
        var min = Math.Min(scale.X, Math.Min(scale.Y, scale.Z)); var max = Math.Max(scale.X, Math.Max(scale.Y, scale.Z));
        var lower = 0.05f / min; var upper = 10 / max;
        return lower <= upper ? scale * Math.Clamp(factor, lower, upper) : scale;
    }
    /// <summary>
    /// How far to raise a piece so it rests on an upward-facing surface: a wall-mounted piece hangs from an anchor
    /// near its top (a Stage Curtain reaches 2.8 m below it), so set down by its origin it would sink into the floor.
    /// Lowest: the piece's lowest point relative to its origin. On walls and ceilings the anchor stays where aimed.
    /// </summary>
    public static float Rest(float lowest, bool upward) => upward && float.IsFinite(lowest) && lowest < 0 ? -lowest : 0;
    // A surface faces up when its normal is within about 45 degrees of vertical; a missing normal counts as a floor.
    public static bool FacesUp(Vector3? normal) => normal is not {} n || n.LengthSquared() < 1e-6f || Vector3.Normalize(n).Y > 0.7f;
    public static Vector3 SnapInHouse(Vector3 world, Matrix4x4 house, float spacing)
    { return Matrix4x4.Invert(house, out var inverse) ? Vector3.Transform(PlacementMath.Snap(Vector3.Transform(world, inverse), spacing), house) : world; }
    public static Vector3 Magnet(PlacementBounds moving, IReadOnlyList<PlacementBounds> targets, float distance = 0.15f) => Snap(moving, targets, distance).Offset;

    /// <summary>
    /// Snaps a moving piece's bounds to its neighbours: faces flush (side by side, or resting on top), and, against a
    /// neighbour within reach, edges and centres lined up and bottoms level, so pieces meet flush. Each axis takes
    /// its smallest correction within the distance; flush faces win ties. Targets: the neighbours each axis used.
    /// </summary>
    public static MagnetSnap Snap(PlacementBounds moving, IReadOnlyList<PlacementBounds> targets, float distance = 0.15f)
    {
        var result = Vector3.Zero; var best = new Vector3(float.MaxValue); var used = new[] { -1, -1, -1 };
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            bool Overlap(int axis) => moving.Maximum[axis] >= target.Minimum[axis] - distance && moving.Minimum[axis] <= target.Maximum[axis] + distance;
            void Try(int axis, float delta, float bias = 0)
            {
                var score = Math.Abs(delta) + bias;
                if (Math.Abs(delta) <= distance && score < best[axis]) { best[axis] = score; result[axis] = delta; used[axis] = index; }
            }
            if (Overlap(1) && Overlap(2)) { Try(0, target.Minimum.X - moving.Maximum.X); Try(0, target.Maximum.X - moving.Minimum.X); }
            if (Overlap(0) && Overlap(1)) { Try(2, target.Minimum.Z - moving.Maximum.Z); Try(2, target.Maximum.Z - moving.Minimum.Z); }
            if (Overlap(0) && Overlap(2)) Try(1, target.Maximum.Y - moving.Minimum.Y);
            if (!Overlap(0) || !Overlap(1) || !Overlap(2)) continue;
            // A neighbour within reach: line up its edges and centre, and stand level with it.
            foreach (var axis in (int[])[0, 2])
            {
                Try(axis, target.Minimum[axis] - moving.Minimum[axis], .002f);
                Try(axis, target.Maximum[axis] - moving.Maximum[axis], .002f);
                Try(axis, (target.Minimum[axis] + target.Maximum[axis] - moving.Minimum[axis] - moving.Maximum[axis]) / 2, .004f);
            }
            Try(1, target.Minimum.Y - moving.Minimum.Y, .002f);
        }
        return new(result, [.. used.Where(i => i >= 0).Distinct()]);
    }
}
