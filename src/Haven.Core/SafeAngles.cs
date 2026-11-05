namespace Haven.Core;

/// <summary>
/// Angle wrapping and float bounds for values that arrive from other players. A subtract-2π loop never finishes for a
/// huge or infinite angle (1e30 − 2π is 1e30 in single precision), which froze the game; these never loop.
/// </summary>
public static class SafeAngles
{
    /// <summary>Wraps an angle to [−π, π]; anything not finite becomes 0.</summary>
    public static float Wrap(float angle) => float.IsFinite(angle) ? MathF.IEEERemainder(angle, MathF.Tau) : 0;

    /// <summary>Positions, offsets and angles from the wire: finite and within ±100,000 (world coordinates stay within a few thousand).</summary>
    public static bool Sane(float value) => float.IsFinite(value) && MathF.Abs(value) <= 100_000;
    public static bool Sane(float a, float b, float c) => Sane(a) && Sane(b) && Sane(c);
}
