using System.Numerics;
using Haven.Core;

internal static class LightingTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        static LayoutLight Light(byte r, byte g, byte b, float intensity, float attenuation, float rangeRate) =>
            new(2, attenuation, rangeRate, new Vector3(r, g, b) / 255f, intensity, 45, .5f, null, true, false, false, .45f, Vector2.Zero);
        await check("Furnishing lights reach as far as the client renders them, on the authored curve", () =>
        {
            // Hingan Andon Lamp: warm, attenuation 3 (cubic), RangeRate 1.
            var andon = FurnitureLighting.Render(Light(255, 198, 114, 1, 3, 1));
            Assert(andon.Curve == LightCurve.Cubic && andon.FalloffFactor == 1 && Math.Abs(andon.Range - 3.71f) < .01f);
            // Squared colour, so the light is deeper than the authored swatch.
            Assert(andon.Color.X == 1 && Math.Abs(andon.Color.Y - .603f) < .001f && Math.Abs(andon.Color.Z - .2f) < .001f);
            // Wall Lantern: attenuation 2 (quadratic) reaches 7.1 m; a Riviera Floor Lamp's RangeRate 3 falls off faster.
            var lantern = FurnitureLighting.Render(Light(255, 212, 102, 1, 2, 1));
            Assert(lantern.Curve == LightCurve.Quadratic && Math.Abs(lantern.Range - 7.14f) < .01f);
            var floor = FurnitureLighting.Render(Light(255, 241, 189, 1, 2, 3));
            Assert(floor.FalloffFactor == 3 && Math.Abs(floor.Range - 4.12f) < .01f);
        });
        await check("Light conversion keeps the client's curve thresholds, factor floor and shadow plane", () =>
        {
            Assert(FurnitureLighting.Render(Light(255, 255, 255, 1, 1.2f, 1)).Curve == LightCurve.Linear);
            Assert(FurnitureLighting.Render(Light(255, 255, 255, 1, 2.2f, 1)).Curve == LightCurve.Quadratic);
            Assert(FurnitureLighting.Render(Light(255, 255, 255, 1, 2.21f, 1)).Curve == LightCurve.Cubic);
            Assert(FurnitureLighting.Render(Light(255, 255, 255, 1, 2, .2f)).FalloffFactor == 1);
            Assert(FurnitureLighting.Render(Light(255, 255, 255, 1, 2, 1) with { ShadowClipRange = 0 }).ShadowNear == .1f);
            // Dimmer lights reach less far; a black light reaches nowhere.
            Assert(FurnitureLighting.Render(Light(255, 255, 255, .5f, 2, 1)).Range < FurnitureLighting.Render(Light(255, 255, 255, 1, 2, 1)).Range);
            Assert(FurnitureLighting.Render(Light(0, 0, 0, 1, 2, 1)).Range == 0);
        });
        await check("Haven's own lights carry exactly what Stagehand is given for the same light, placed in the world", () =>
        {
            // A spot light with a pattern, both shadows and specular, in a piece turned a quarter, 2 m up, in a home moved and scaled.
            var authored = new LayoutLight(3, 2, 1, new Vector3(1, 212 / 255f, 102 / 255f), 1, 40, 3, "bgcommon/hou/indoor/general/0110/texture/fun_b0_m0110_0a_l.tex",
                true, true, true, .45f, new Vector2(10, -5));
            var piece = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 2, 0);
            var home = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(100, 0, 50);
            var light = NativeLights.FromLayout(authored, Matrix4x4.CreateTranslation(1, 0, 0) * piece * home);
            var rendered = FurnitureLighting.Render(authored);
            Assert(light.Shape == NativeLightShape.Spot && light.Flags == (NativeLightFlags.Specular | NativeLightFlags.ObjectShadows | NativeLightFlags.CharacterShadows));
            Assert(light.Color == rendered.Color && light.Intensity == 1 && light.Curve == rendered.Curve && light.FalloffFactor == rendered.FalloffFactor && light.Range == rendered.Range);
            Assert(light.SpotDegrees == 40 && light.ConeFalloffDegrees == 3 && light.FlatSkewDegrees == new Vector2(10, -5) && light.Texture == authored.Texture);
            Assert(light.ShadowNear == rendered.ShadowNear && light.ShadowFar == MathF.Max(rendered.Range, .1f) && light.CharacterShadowRange == rendered.Range);
            // Never dynamic shadows: the client turns them off for furnishing lights.
            Assert(!light.Flags.HasFlag(NativeLightFlags.DynamicShadows));
            // (1, 0, 0) turned a quarter about Y is (0, 0, -1), then 2 m up, then the home's scale and place.
            Assert(Vector3.Distance(light.Position, new Vector3(100, 4, 48)) < 1e-4f && Vector3.Distance(light.Scale, new Vector3(2)) < 1e-4f && light.Sane);
            // A flat light is flat, anything else not spot or flat (a line light) shines as a point, as through Stagehand.
            Assert(NativeLights.FromLayout(authored with { Type = 4 }, Matrix4x4.Identity).Shape == NativeLightShape.Flat);
            Assert(NativeLights.FromLayout(authored with { Type = 2 }, Matrix4x4.Identity).Shape == NativeLightShape.Point);
            Assert(NativeLights.FromLayout(authored with { Type = 5, Texture = null }, Matrix4x4.Identity) is { Shape: NativeLightShape.Point, Texture: "" });
            // The game's render shapes and switches, by number (FFXIVClientStructs Render.LightShape and LightFlags).
            Assert((uint)NativeLightShape.World == 1 && (uint)NativeLightShape.Point == 2 && (uint)NativeLightShape.Spot == 3 && (uint)NativeLightShape.Flat == 4);
            Assert((uint)NativeLightFlags.Specular == 1 && (uint)NativeLightFlags.DynamicShadows == 2 && (uint)NativeLightFlags.CharacterShadows == 4 && (uint)NativeLightFlags.ObjectShadows == 8);
            // A light from a broken transform or layout is never handed to the game.
            Assert(!(light with { Position = new(float.NaN, 0, 0) }).Sane && !(light with { Range = float.PositiveInfinity }).Sane && !(light with { Color = new(-1, 0, 0) }).Sane);
            Assert(!(light with { Position = new(0, 1e6f, 0) }).Sane);
        });
        await check("The blank canvas's fill light is Stagehand's: 6 m above the middle, brighter with the room's brightness", () =>
        {
            var home = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromYawPitchRoll(1, 0, 0)) * Matrix4x4.CreateTranslation(10, 20, 30);
            var fill = NativeLights.CanvasFill(home, null);
            Assert(Vector3.Distance(fill.Position, new Vector3(10, 26, 30)) < 1e-4f && fill.Shape == NativeLightShape.Point && fill.Range == 24);
            Assert(Math.Abs(fill.Intensity - 1.4f) < 1e-6f && Math.Abs(NativeLights.CanvasFill(home, 0).Intensity) < 1e-6f && Math.Abs(NativeLights.CanvasFill(home, 2.5f).Intensity - .7f) < 1e-6f);
            // Stagehand's defaults for what Haven doesn't set: every shadow and specular, quadratic, 0.25-100 m shadows, 50 m for characters.
            Assert(fill.Flags == (NativeLightFlags.Specular | NativeLightFlags.DynamicShadows | NativeLightFlags.CharacterShadows | NativeLightFlags.ObjectShadows));
            Assert(fill.Curve == LightCurve.Quadratic && fill.FalloffFactor == 1 && fill.ShadowNear == .25f && fill.ShadowFar == 100 && fill.CharacterShadowRange == 50 && fill.Color == Vector3.One);
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Lighting assertion failed."); }
}
