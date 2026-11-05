namespace Haven.Core;

/// <summary>
/// Bounds on the lights an imported (Stagehand) stage brings into a home, applied when it is drawn rather than when it
/// is loaded, so no saved or shared home fails to open. A shared home is another player's data: without these, its
/// lights could be numberless, reach across the map or blind. Furnishing lights come from game data and are not limited.
/// </summary>
public static class SceneLights
{
    public const int MaxImported = 256;
    public const float MaxRange = 100, MaxIntensity = 50, MaxShadowRange = 100;

    public static float Range(float value) => Bound(value, MaxRange);
    public static float Intensity(float value) => Bound(value, MaxIntensity);
    public static float ShadowRange(float value) => Bound(value, MaxShadowRange);
    private static float Bound(float value, float max) => float.IsFinite(value) ? Math.Clamp(value, 0, max) : 0;
}
