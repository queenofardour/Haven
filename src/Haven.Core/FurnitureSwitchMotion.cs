using System.Numerics;

namespace Haven.Core;

/// <summary>
/// What one side of a switch does to a member's transform: an optional snap to a fixed pose (C018) at a frame, then
/// an optional one-shot curve (C013) from a frame that plays once and holds its last frame. A lid that swings open,
/// a drawer that slides, a music box that turns while it plays.
/// </summary>
public sealed record SideMotion(int SnapFrame, float[]? Snap, int CurveFrame, LoopCurve? Curve)
{
    // The pose at a frame since the side began, or null before its first command (the part keeps its old pose).
    public Matrix4x4? At(double frame)
    {
        if (Curve != null && frame >= CurveFrame) return Curve.At(frame - CurveFrame);
        if (Snap != null && frame >= SnapFrame) return SnapPose(Snap);
        return null;
    }
    public Matrix4x4? Final => At(double.MaxValue);

    // C018 carries translation, rotation in radians and scale, like an SGB member's own transform.
    public static Matrix4x4 SnapPose(float[] v) => LayoutRotation.Transform(new(v[0], v[1], v[2]), new(v[3], v[4], v[5]), new(v[6], v[7], v[8]));
}

/// <summary>A member's motion on each side of a switch; a side without any leaves the member at its authored pose.</summary>
public sealed record SwitchMotion(SideMotion? On, SideMotion? Off)
{
    // Channels a curve masks out or leaves unkeyed keep the member's authored value, as looping curves do.
    public SwitchMotion WithRest(Vector3 translation, Vector3 rotationRadians, Vector3 scale)
    {
        SideMotion? Rest(SideMotion? side) => side?.Curve is {} curve ? side with { Curve = curve.WithRest(translation, rotationRadians, scale) } : side;
        return new(Rest(On), Rest(Off));
    }

    /// <summary>
    /// The member's pose on a side, some milliseconds after the switch changed to it (long.MaxValue when it has been
    /// there since the home loaded). Before the side's first command the member stays where the other side left it.
    /// </summary>
    public Matrix4x4 Pose(bool on, long sinceMilliseconds, Matrix4x4 rest)
    {
        var side = on ? On : Off;
        if (side == null) return rest;
        var frame = sinceMilliseconds == long.MaxValue ? double.MaxValue : sinceMilliseconds / 1000.0 * LoopCurve.FramesPerSecond;
        return side.At(frame) ?? (on ? Off : On)?.Final ?? rest;
    }

    /// <summary>Which side ends at the authored pose: that is the side the item is modelled in. Null if neither or both.</summary>
    public bool? RestingSide(Matrix4x4 rest)
    {
        bool Near(Matrix4x4? pose) => pose is not {} p || Close(p, rest);
        bool onRests = Near(On?.Final), offRests = Near(Off?.Final);
        return onRests == offRests ? null : onRests;
    }
    private static bool Close(Matrix4x4 a, Matrix4x4 b)
    {
        for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++) if (MathF.Abs(a[r, c] - b[r, c]) > 0.01f) return false;
        return true;
    }
}

/// <summary>One step of a one-shot timeline on a member: from its frame, a curve played once or a snap to a pose.</summary>
public sealed record ActionStep(int Frame, LoopCurve? Curve, float[]? Snap);

/// <summary>
/// A member's motion through a one-shot timeline (an arcade machine's result): each snap and curve from its frame, the
/// latest begun one holding, so a target recoils and then returns. Before the first step the member keeps its rest.
/// </summary>
public sealed record ActionMotion(ActionStep[] Steps)
{
    public ActionMotion WithRest(Vector3 translation, Vector3 rotationRadians, Vector3 scale) =>
        new([.. Steps.Select(s => s.Curve is { } curve ? s with { Curve = curve.WithRest(translation, rotationRadians, scale) } : s)]);

    public Matrix4x4 Pose(long sinceMilliseconds, Matrix4x4 rest)
    {
        var frame = sinceMilliseconds / 1000.0 * LoopCurve.FramesPerSecond;
        for (var i = Steps.Length - 1; i >= 0; i--)
        {
            var step = Steps[i];
            if (frame < step.Frame) continue;
            return step.Curve != null ? step.Curve.At(frame - step.Frame) : SideMotion.SnapPose(step.Snap!);
        }
        return rest;
    }
}
