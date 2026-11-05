using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Haven;

internal enum HavenIcon { Home, Chair, Paint, Music, Friends, Library, Plus, Search, Check, Play, Stop, Leaf, Undo, Redo, Wrench, Refresh, Close, Alert, Info, Back, More, Settings, Copy, Star, Grid, Trash, Custom, Camera, Download, FreeCamera }

// Shared native draw primitives under the HavenKit components: line icons, colour conversion and the invisible
// native button that gives custom-drawn controls focus, navigation and hit testing.
internal static class HavenUi
{
    public static Func<ImFontPtr>? IconFontProvider;
    public static float S => ImGuiHelpers.GlobalScale;
    public static Vector4 Rgb(uint color) => new(((color >> 16) & 255) / 255f, ((color >> 8) & 255) / 255f, (color & 255) / 255f, 1);
    public static uint U(Vector4 color) { color.W *= ImGui.GetStyle().Alpha; return ImGui.ColorConvertFloat4ToU32(color); }

    // Native buttons retain keyboard navigation; the draw list supplies their appearance.
    public static bool Surface(string id, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Zero);
        var clicked = ImGui.Button(id, size);
        ImGui.PopStyleColor(3); return clicked;
    }

    // Small original line icons: no font dependency, textures or loading work.
    public static void DrawIcon(ImDrawListPtr draw, HavenIcon icon, Vector2 center, float size, Vector4 color)
    {
        var c = U(color); var unit = size / 24; var stroke = Math.Max(1, size / 12);
        Vector2 P(float x, float y) => center + new Vector2(x - 12, y - 12) * unit;
        void L(float x, float y, float a, float b) => draw.AddLine(P(x, y), P(a, b), c, stroke);
        void R(float x, float y, float a, float b) => draw.AddRect(P(x, y), P(a, b), c, 2 * unit, ImDrawFlags.None, stroke);
        void C(float x, float y, float r) => draw.AddCircle(P(x, y), r * unit, c, 16, stroke);
        switch (icon)
        {
            case HavenIcon.Wrench:
                if (IconFontProvider != null) draw.AddText(IconFontProvider(), size, center - new Vector2(size / 2), c, "\uf0ad");
                break;
            case HavenIcon.Home: L(2, 11, 12, 3); L(12, 3, 22, 11); L(5, 9, 5, 21); L(5, 21, 19, 21); L(19, 21, 19, 9); R(10, 14, 14, 21); break;
            case HavenIcon.Chair: R(5, 3, 19, 14); R(3, 13, 21, 18); L(5, 18, 5, 22); L(19, 18, 19, 22); break;
            case HavenIcon.Paint: R(3, 3, 17, 10); L(17, 6, 21, 6); L(21, 6, 21, 14); L(21, 14, 12, 14); L(12, 14, 12, 17); R(10, 17, 14, 22); break;
            case HavenIcon.Music: L(9, 18, 9, 5); L(9, 5, 21, 3); L(21, 3, 21, 16); L(9, 9, 21, 7); C(6, 19, 3); C(18, 17, 3); break;
            case HavenIcon.Friends: C(9, 7, 4); L(2, 21, 2, 18); L(2, 18, 5, 14); L(5, 14, 13, 14); L(13, 14, 16, 18); L(16, 18, 16, 21); L(2, 21, 16, 21); C(19, 8, 3); L(19, 15, 22, 18); L(22, 18, 22, 21); break;
            case HavenIcon.Library: R(3, 4, 8, 21); R(10, 4, 15, 21); L(17, 5, 21, 4); L(21, 4, 23, 20); L(23, 20, 19, 21); L(19, 21, 17, 5); break;
            case HavenIcon.Plus: L(4, 12, 20, 12); L(12, 4, 12, 20); break;
            case HavenIcon.Download: L(12, 3, 12, 15); L(6, 10, 12, 16); L(12, 16, 18, 10); L(3, 16, 3, 21); L(3, 21, 21, 21); L(21, 21, 21, 16); break;
            case HavenIcon.Search: C(10, 10, 7); L(15, 15, 22, 22); break;
            case HavenIcon.Check: L(4, 12, 9, 17); L(9, 17, 21, 5); break;
            case HavenIcon.Play: L(6, 3, 21, 12); L(21, 12, 6, 21); L(6, 21, 6, 3); break;
            case HavenIcon.Stop: R(4, 4, 20, 20); break;
            case HavenIcon.Leaf: L(5, 21, 17, 6); L(7, 17, 3, 11); L(3, 11, 7, 4); L(7, 4, 21, 2); L(21, 2, 21, 14); L(21, 14, 14, 20); L(14, 20, 7, 17); L(11, 13, 17, 13); break;
            case HavenIcon.Undo: L(8, 4, 2, 10); L(2, 10, 8, 16); L(2, 10, 15, 10); L(15, 10, 20, 14); L(20, 14, 20, 20); break;
            case HavenIcon.Redo: L(16, 4, 22, 10); L(22, 10, 16, 16); L(22, 10, 9, 10); L(9, 10, 4, 14); L(4, 14, 4, 20); break;
            case HavenIcon.Close: L(6, 6, 18, 18); L(18, 6, 6, 18); break;
            case HavenIcon.Alert: L(12, 3, 22, 20); L(22, 20, 2, 20); L(2, 20, 12, 3); L(12, 9, 12, 14); L(12, 16.5f, 12, 17.5f); break;
            case HavenIcon.Info: C(12, 12, 9); L(12, 11, 12, 17); L(12, 7, 12, 8); break;
            case HavenIcon.Back: L(15, 5, 8, 12); L(8, 12, 15, 19); break;
            case HavenIcon.More: draw.AddCircleFilled(P(5, 12), 1.8f * unit, c, 12); draw.AddCircleFilled(P(12, 12), 1.8f * unit, c, 12); draw.AddCircleFilled(P(19, 12), 1.8f * unit, c, 12); break;
            case HavenIcon.Settings:
                C(12, 12, 3.5f); C(12, 12, 8);
                for (var i = 0; i < 8; i++) { var a = i * MathF.PI / 4; draw.AddLine(center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * 8 * unit, center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * 11 * unit, c, stroke * 1.4f); }
                break;
            case HavenIcon.Copy: R(8, 8, 20, 20); L(4, 15, 4, 4); L(4, 4, 15, 4); break;
            case HavenIcon.Star: DrawStar(draw, center, size, color, false); break;
            case HavenIcon.Grid: R(4, 4, 20, 20); L(4, 12, 20, 12); L(12, 4, 12, 20); break;
            // The free camera: a view that moves every way.
            case HavenIcon.FreeCamera:
                L(12, 2, 12, 22); L(2, 12, 22, 12);
                L(9, 5, 12, 2); L(12, 2, 15, 5); L(9, 19, 12, 22); L(12, 22, 15, 19);
                L(5, 9, 2, 12); L(2, 12, 5, 15); L(19, 9, 22, 12); L(22, 12, 19, 15); C(12, 12, 3); break;
            case HavenIcon.Camera: R(2, 7, 22, 20); C(12, 13.5f, 4); L(8, 7, 10, 4); L(10, 4, 14, 4); L(14, 4, 16, 7); break;
            // Custom content made by a player: a package with a sparkle.
            case HavenIcon.Custom: R(2, 10, 16, 22); L(2, 15, 16, 15); L(9, 10, 9, 15); L(19, 1, 19, 9); L(15, 5, 23, 5); L(17, 3, 21, 7); L(21, 3, 17, 7); break;
            case HavenIcon.Trash: L(4, 7, 20, 7); L(10, 11, 10, 17); L(14, 11, 14, 17); L(6, 7, 7, 20); L(7, 20, 17, 20); L(17, 20, 18, 7); L(9, 7, 9, 4); L(9, 4, 15, 4); L(15, 4, 15, 7); break;
            case HavenIcon.Refresh:
                draw.PathArcTo(center, 8 * unit, MathF.PI * .25f, MathF.PI * 1.75f, 20);
                draw.PathStroke(c, ImDrawFlags.None, stroke);
                L(18, 1, 18, 6); L(18, 6, 13, 6); break;
        }
    }

    // Five-pointed star; filled as a fan of triangles from the centre (the outline is concave).
    public static void DrawStar(ImDrawListPtr draw, Vector2 center, float size, Vector4 color, bool filled)
    {
        var c = U(color); var outer = size * .48f; var inner = outer * .45f; var points = new Vector2[10];
        for (var i = 0; i < 10; i++)
        {
            var angle = -MathF.PI / 2 + i * MathF.PI / 5; var radius = i % 2 == 0 ? outer : inner;
            points[i] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }
        for (var i = 0; i < 10; i++)
            if (filled) draw.AddTriangleFilled(center, points[i], points[(i + 1) % 10], c);
            else draw.AddLine(points[i], points[(i + 1) % 10], c, Math.Max(1, size / 14));
    }
}
