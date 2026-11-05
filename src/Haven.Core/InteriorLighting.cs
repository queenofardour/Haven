namespace Haven.Core;

// Room brightness belongs to the private scene. GPose's photo exposure and camera
// lights are separate. Reapply the room level at either photo-mode boundary,
// including when the home uses the shell's default instead of an explicit value.
public sealed class InteriorLighting
{
    private float baseline = 1;
    private bool bound, gposing;
    public float Level { get; private set; } = 1;
    public float OriginalLevel => baseline;
    public bool Pending { get; private set; }

    public void Bind(float originalLevel, bool inGPose)
    {
        baseline = float.IsFinite(originalLevel) && originalLevel is >= 0 and <= 1 ? originalLevel : 1;
        Level = baseline; bound = true; gposing = inGPose; Pending = true;
    }

    public bool Observe(float? brightness, bool inGPose)
    {
        if (!bound) return false;
        var changedMode = gposing != inGPose;
        var level = brightness is {} value ? InteriorPlan.LightLevel(value) : baseline;
        if (!float.IsFinite(level)) level = baseline;
        Pending |= changedMode || level != Level;
        Level = level; gposing = inGPose;
        return changedMode;
    }

    public void Applied() { if (bound) Pending = false; }
    public void Invalidate() { if (bound) Pending = true; }
    public void Clear() { bound = false; Pending = false; baseline = Level = 1; gposing = false; }
}

/// <summary>
/// Keeps a private home's weather valid. Inside Haven the server sends no weather, so when GPose rebuilds the
/// environment the game falls back to weather 0, which has no ambient light: the room goes dark and stays dark
/// after GPose. Only that empty value is corrected; a real weather picked by a photo tool is never overridden,
/// and only weather seen outside GPose becomes the home's remembered weather.
/// </summary>
public sealed class HousingWeather
{
    public byte Remembered { get; private set; }

    /// <returns>The weather to restore, or null to leave the environment alone.</returns>
    public byte? Observe(byte active, bool gposing, byte natural)
    {
        if (active != 0)
        {
            if (!gposing) Remembered = active;
            return null;
        }
        var restore = Remembered != 0 ? Remembered : natural;
        return restore != 0 ? restore : null;
    }

    public void Clear() => Remembered = 0;
}
