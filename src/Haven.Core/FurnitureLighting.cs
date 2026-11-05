using System.Numerics;

namespace Haven.Core;

/// <summary>
/// A furnishing light as its prefab authors it (an SGB light instance). Type is the client's light shape (2 point,
/// 3 spot, 4 flat, 5 line); Color is 0-1 per channel and Intensity the HDRI multiplier.
/// </summary>
public sealed record LayoutLight(int Type, float Attenuation, float RangeRate, Vector3 Color, float Intensity, float ConeDegrees, float ConeFalloffDegrees,
    string? Texture, bool Specular, bool ObjectShadows, bool CharacterShadows, float ShadowClipRange, Vector2 PlaneRotation);

public enum LightCurve { Linear, Quadratic, Cubic }

/// <summary>What the client's render light gets from a layout light: its colour (with intensity 1), falloff curve and
/// factor, reach, and near shadow plane.</summary>
public readonly record struct RenderedLight(Vector3 Color, LightCurve Curve, float FalloffFactor, float Range, float ShadowNear);

public static class FurnitureLighting
{
    // A light reaches where its brightest channel has fallen to 5/255.
    private const float Threshold = 5f / 255;

    /// <summary>
    /// The client's conversion of a layout light into its render light, read-only from the installed client: each
    /// colour channel times the intensity, squared, at render intensity 1; Attenuation picks the curve (up to 1.2
    /// linear, up to 2.2 quadratic, cubic above); RangeRate is the falloff factor, at least 1; and the reach is where
    /// brightest / (factor · d^n) falls to the threshold. A Hingan Andon Lamp reaches 3.7 m on a cubic curve, a Wall
    /// Lantern 7.1 m on a quadratic one; 0.22.x passed RangeRate (1) as the reach, a small pool of light.
    /// </summary>
    public static RenderedLight Render(LayoutLight light)
    {
        var lit = light.Color * light.Intensity;
        var color = lit * lit;
        var curve = light.Attenuation <= 1.2f ? LightCurve.Linear : light.Attenuation <= 2.2f ? LightCurve.Quadratic : LightCurve.Cubic;
        var factor = MathF.Max(light.RangeRate, 1);
        var reach = MathF.Max(MathF.Max(color.X, color.Y), color.Z) / (factor * Threshold);
        var range = curve switch { LightCurve.Linear => reach, LightCurve.Quadratic => MathF.Sqrt(reach), _ => MathF.Pow(reach, 1f / 3) };
        if (!float.IsFinite(range) || range < 0) range = 0;
        return new(color, curve, factor, range, MathF.Max(light.ShadowClipRange, .1f));
    }
}
