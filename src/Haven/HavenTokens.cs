using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Haven;

// Body matches Dalamud's default 16 px text; 14 px read small in game.
internal enum HavenText { Caption, Small, Body, Strong, Title, Display }

// Refined modern design tokens. Redesigned
// surfaces take every colour, size and radius from here, never inline values.
// Sizes are unscaled pixels; Px applies Dalamud's global scale.
internal static class HavenTokens
{
    public static float Px(float value) => value * ImGuiHelpers.GlobalScale;
    public static Vector2 Px(float x, float y) => new Vector2(x, y) * ImGuiHelpers.GlobalScale;

    // Surfaces, from recessed to overlaid.
    public static readonly Vector4 Inset = HavenUi.Rgb(0x11151A), Window = HavenUi.Rgb(0x181C22), Card = HavenUi.Rgb(0x1F242B),
        Raised = HavenUi.Rgb(0x222831), Hover = HavenUi.Rgb(0x2A313B), Overlay = HavenUi.Rgb(0x232A33), IconWell = HavenUi.Rgb(0x262D36);
    public static readonly Vector4 Border = HavenUi.Rgb(0x2A313B), BorderStrong = HavenUi.Rgb(0x2F3742), Divider = HavenUi.Rgb(0x343D49);
    public static readonly Vector4 Text = HavenUi.Rgb(0xEEF1F5), TextSecondary = HavenUi.Rgb(0x9AA4B2), TextTertiary = HavenUi.Rgb(0x929CAB),
        TextOnRaised = HavenUi.Rgb(0xDFE4EA);
    // The accent matches the 0.12.2 in-world interaction highlight.
    public static readonly Vector4 Accent = HavenUi.Rgb(0x6EA8FF), AccentHover = HavenUi.Rgb(0x8CBCFF), AccentPressed = HavenUi.Rgb(0x5A93EA),
        OnAccent = HavenUi.Rgb(0x0D1726), AccentTint = HavenUi.Rgb(0x1D2A3C), AccentRing = new(0x6E / 255f, 0xA8 / 255f, 1, 0.22f);
    public static readonly Vector4 Danger = HavenUi.Rgb(0xFF9D94), DangerFill = HavenUi.Rgb(0x3A2226),
        OnSuccess = HavenUi.Rgb(0x0E2014),
        Success = HavenUi.Rgb(0x8FD49B), SuccessFill = HavenUi.Rgb(0x1E3326), Warning = HavenUi.Rgb(0xF2C46D), WarningFill = HavenUi.Rgb(0x3A3020);
    public static readonly Vector4 AxisX = HavenUi.Rgb(0xFF8F87), AxisY = HavenUi.Rgb(0x8FD49B), AxisZ = HavenUi.Rgb(0x8CBCFF);
    public static readonly Vector4 Shadow = new(0, 0, 0, 0.4f);

    // 4/8 spacing grid.
    public const float Space1 = 4, Space2 = 8, Space3 = 12, Space4 = 16, Space5 = 24, Space6 = 32;
    public const float RadiusField = 8, RadiusControl = 10, RadiusCard = 12, RadiusWindow = 14, RadiusDrawer = 16;
    public const float ControlHeight = 40, ToolbarHeight = 40, FieldHeight = 38, RowHeight = 48, PillHeight = 32;

    public static float Size(HavenText text) => text switch
    {
        HavenText.Caption => 13, HavenText.Small => 14, HavenText.Title => 18, HavenText.Display => 28, _ => 16,
    };
    public static bool SemiBold(HavenText text) => text is HavenText.Strong or HavenText.Title or HavenText.Display;
}

// Interface typeface. The host supplies one font per HavenText: Inter through
// Dalamud's font atlas in game, plain ImGui fonts in the preview. Without a
// provider, or before the atlas is built, the current font stays in use.
internal static class HavenFonts
{
    public static Func<HavenText, IDisposable?>? Provider;
    public static Scope Use(HavenText text) => new(Provider?.Invoke(text));
    public readonly struct Scope(IDisposable? inner) : IDisposable { public void Dispose() => inner?.Dispose(); }

    public static Vector2 Measure(HavenText text, string value) { using (Use(text)) return ImGui.CalcTextSize(value); }
}
