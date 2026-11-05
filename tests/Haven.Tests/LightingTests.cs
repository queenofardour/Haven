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
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Lighting assertion failed."); }
}
