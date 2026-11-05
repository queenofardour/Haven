using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;

namespace Haven;

internal readonly record struct AxisHandle(int Axis, Vector2 Start, Vector2 End, bool Visible);

internal static class PlacementGizmoUi
{
    private static readonly uint[] Colors = [0xFF8A87ED, 0xFFA5D29A, 0xFFF0BE8A];
    private static readonly string[] Labels = ["X", "Y", "Z"];
    public static int HitTest(ReadOnlySpan<AxisHandle> handles, Vector2 pointer, float scale)
    {
        var best = 10 * scale; var axis = -1;
        foreach (var handle in handles)
        {
            if (!handle.Visible) continue;
            var distance = AxisDrag.ScreenDistance(pointer, handle.Start, handle.End);
            if (distance < best) { best = distance; axis = handle.Axis; }
        }
        return axis;
    }
    public static void Draw(ReadOnlySpan<AxisHandle> handles, int hovered, int active)
    {
        var draw = ImGui.GetBackgroundDrawList(); var s = HavenUi.S;
        foreach (var handle in handles)
        {
            if (!handle.Visible) continue;
            var color = handle.Axis == active || active < 0 && handle.Axis == hovered ? HavenUi.U(HavenTokens.Accent) : Colors[handle.Axis];
            var direction = Vector2.Normalize(handle.End - handle.Start); var side = new Vector2(-direction.Y, direction.X);
            var basePoint = handle.End - direction * 12 * s;
            draw.AddLine(handle.Start, basePoint, 0xD0201B17, 6 * s);
            draw.AddLine(handle.Start, basePoint, color, (handle.Axis == active ? 4 : 3) * s);
            draw.AddTriangleFilled(handle.End, basePoint + side * 5 * s, basePoint - side * 5 * s, color);
            var label = handle.End + direction * 8 * s - new Vector2(4 * s, ImGui.GetTextLineHeight() / 2);
            draw.AddText(label + new Vector2(1, 1), 0xFF171B20, Labels[handle.Axis]);
            draw.AddText(label, color, Labels[handle.Axis]);
        }
    }
}
