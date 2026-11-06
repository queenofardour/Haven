using System.Numerics;

namespace Haven.Core;

/// <summary>The game's render light shapes (FFXIVClientStructs Render.LightShape).</summary>
public enum NativeLightShape : uint { World = 1, Point = 2, Spot = 3, Flat = 4 }

/// <summary>The game's render light switches (FFXIVClientStructs Render.LightFlags).</summary>
[Flags]
public enum NativeLightFlags : uint { None = 0, Specular = 1, DynamicShadows = 2, CharacterShadows = 4, ObjectShadows = 8 }

/// <summary>
/// One light Haven draws itself: where it is in the world and every value its render light gets.
/// The values are exactly those Haven gives Stagehand for the same light (FurnitureCatalogue.LightOf and the blank
/// canvas's fill light), so drawing it either way looks the same; only who makes the light differs.
/// </summary>
public sealed record NativeLight(NativeLightShape Shape, NativeLightFlags Flags, Vector3 Position, Quaternion Rotation, Vector3 Scale,
    Vector3 Color, float Intensity, LightCurve Curve, float FalloffFactor, float Range, float SpotDegrees, float ConeFalloffDegrees,
    Vector2 FlatSkewDegrees, string Texture, float ShadowNear, float ShadowFar, float CharacterShadowRange)
{
    /// <summary>Whether every number is one the game can use (a bad layout or transform is never handed to it).</summary>
    public bool Sane =>
        float.IsFinite(Position.X) && float.IsFinite(Position.Y) && float.IsFinite(Position.Z) && Math.Abs(Position.X) < 100_000 && Math.Abs(Position.Y) < 100_000 && Math.Abs(Position.Z) < 100_000
        && float.IsFinite(Rotation.X) && float.IsFinite(Rotation.Y) && float.IsFinite(Rotation.Z) && float.IsFinite(Rotation.W)
        && float.IsFinite(Scale.X) && float.IsFinite(Scale.Y) && float.IsFinite(Scale.Z)
        && float.IsFinite(Color.X) && float.IsFinite(Color.Y) && float.IsFinite(Color.Z) && Color.X >= 0 && Color.Y >= 0 && Color.Z >= 0
        && float.IsFinite(Intensity) && Intensity >= 0 && float.IsFinite(FalloffFactor) && float.IsFinite(Range) && Range >= 0
        && float.IsFinite(SpotDegrees) && float.IsFinite(ConeFalloffDegrees) && float.IsFinite(FlatSkewDegrees.X) && float.IsFinite(FlatSkewDegrees.Y)
        && float.IsFinite(ShadowNear) && float.IsFinite(ShadowFar) && float.IsFinite(CharacterShadowRange);
}

public static class NativeLights
{
    /// <summary>The most lights the world pieces of one home bring (a zone group can hold a street's lanterns).</summary>
    public const int MaxWorldLights = 256;

    /// <summary>
    /// A furnishing's (or building piece's) light: its layout light as the client converts it (FurnitureLighting.Render),
    /// placed by its full world transform. Line lights, which neither the game's render shapes nor Stagehand name,
    /// shine as points, as they do through Stagehand.
    /// </summary>
    public static NativeLight FromLayout(LayoutLight authored, Matrix4x4 world)
    {
        var rendered = FurnitureLighting.Render(authored);
        if (!Matrix4x4.Decompose(world, out var scale, out var rotation, out var position)) (scale, rotation, position) = (Vector3.One, Quaternion.Identity, world.Translation);
        var flags = (authored.Specular ? NativeLightFlags.Specular : 0) | (authored.ObjectShadows ? NativeLightFlags.ObjectShadows : 0)
            | (authored.CharacterShadows ? NativeLightFlags.CharacterShadows : 0);
        return new(authored.Type switch { 3 => NativeLightShape.Spot, 4 => NativeLightShape.Flat, _ => NativeLightShape.Point }, flags,
            position, rotation, scale, rendered.Color, 1, rendered.Curve, rendered.FalloffFactor, rendered.Range,
            authored.ConeDegrees, authored.ConeFalloffDegrees, authored.PlaneRotation, authored.Texture ?? "",
            rendered.ShadowNear, MathF.Max(rendered.Range, .1f), rendered.Range);
    }

    /// <summary>
    /// The blank canvas's fill light, 6 m above the home's middle, as Haven gives it to Stagehand: Stagehand's defaults for
    /// everything Haven doesn't set (every shadow and specular on, quadratic, 0.25 to 100 m shadows, 50 m for characters).
    /// </summary>
    public static NativeLight CanvasFill(Matrix4x4 home, float? brightness)
    {
        Matrix4x4.Decompose(home, out var scale, out var rotation, out _);
        return new(NativeLightShape.Point, NativeLightFlags.Specular | NativeLightFlags.DynamicShadows | NativeLightFlags.CharacterShadows | NativeLightFlags.ObjectShadows,
            Vector3.Transform(new Vector3(0, 6, 0), home), rotation, scale, Vector3.One, 1.4f * (brightness ?? 5) / 5, LightCurve.Quadratic, 1, 24,
            30, 5, Vector2.Zero, "", .25f, 100, 50);
    }
}
