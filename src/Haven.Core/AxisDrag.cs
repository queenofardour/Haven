using System.Numerics;

namespace Haven.Core;

// A fixed drag plane contains one house axis and faces the initial viewing ray.
// All updates are absolute from the captured start; the other two coordinates
// cannot drift, including with a rotated/scaled house or an imported parent.
public sealed class AxisDrag
{
    public int Axis { get; }
    private readonly Vector3 start, worldStart, worldAxis, planeNormal;
    private readonly float axisLength;
    private float previousCoordinate, offset;

    private AxisDrag(int axis, Vector3 start, Vector3 worldStart, Vector3 worldAxis, float axisLength, Vector3 normal, float coordinate)
    { Axis = axis; this.start = start; this.worldStart = worldStart; this.worldAxis = worldAxis; this.axisLength = axisLength; planeNormal = normal; previousCoordinate = coordinate; }

    public static AxisDrag? Begin(int axis, Vector3 position, Matrix4x4 house, Vector3 rayOrigin, Vector3 rayDirection)
    {
        if (axis is < 0 or > 2 || !Finite(position) || !Finite(rayOrigin) || !Finite(rayDirection) || rayDirection.LengthSquared() < .000001f) return null;
        var localAxis = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        var worldStart = Vector3.Transform(position, house);
        var worldAxis = Vector3.TransformNormal(localAxis, house); var length = worldAxis.Length();
        if (!Finite(worldStart) || !float.IsFinite(length) || length < .00001f) return null;
        worldAxis /= length; rayDirection = Vector3.Normalize(rayDirection);
        var normal = rayDirection - worldAxis * Vector3.Dot(rayDirection, worldAxis);
        // An axis aimed almost straight at the camera has no reliable mouse travel.
        if (normal.LengthSquared() < .01f) return null;
        normal = Vector3.Normalize(normal);
        if (Coordinate(worldStart, worldAxis, length, normal, rayOrigin, rayDirection) is not float coordinate) return null;
        return new(axis, position, worldStart, worldAxis, length, normal, coordinate);
    }

    /// <summary>
    /// The dragged position: the pointer's travel along the axis, times the rate (a tenth of it when fine), each update's
    /// step at most maxStep (an axis seen nearly end-on turns a small pointer move into a long way).
    /// </summary>
    public Vector3? Update(Vector3 rayOrigin, Vector3 rayDirection, float spacing = 0, bool fine = false, float rate = 1, float maxStep = float.PositiveInfinity)
    {
        if (Coordinate(worldStart, worldAxis, axisLength, planeNormal, rayOrigin, rayDirection) is not float coordinate) return null;
        offset += Math.Clamp((coordinate - previousCoordinate) * rate * (fine ? .1f : 1), -maxStep, maxStep);
        previousCoordinate = coordinate;
        var result = start; var value = start[Axis] + offset;
        if (float.IsFinite(spacing) && spacing > 0 && Math.Abs(offset) > .000001f) value = MathF.Round(value / spacing) * spacing;
        if (!float.IsFinite(value)) return null;
        result[Axis] = Math.Clamp(value, -10000, 10000);
        return result;
    }

    private static float? Coordinate(Vector3 origin, Vector3 axis, float length, Vector3 normal, Vector3 rayOrigin, Vector3 direction)
    {
        if (!Finite(rayOrigin) || !Finite(direction)) return null;
        var denominator = Vector3.Dot(direction, normal);
        if (Math.Abs(denominator) < .0001f) return null;
        var distance = Vector3.Dot(origin - rayOrigin, normal) / denominator;
        if (!float.IsFinite(distance) || distance < 0) return null;
        var value = Vector3.Dot(rayOrigin + direction * distance - origin, axis) / length;
        return float.IsFinite(value) ? value : null;
    }

    public static float ScreenDistance(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start; var length = segment.LengthSquared();
        if (!float.IsFinite(length) || length < 1) return float.PositiveInfinity;
        var t = Math.Clamp(Vector2.Dot(point - start, segment) / length, 0, 1);
        return Vector2.Distance(point, start + segment * t);
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
