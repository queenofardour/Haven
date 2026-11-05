using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Haven;

internal readonly record struct UiBounds(Vector2 Position, Vector2 Size)
{
    public bool Contains(Vector2 point) => Size.X > 0 && Size.Y > 0 && point.X >= Position.X && point.Y >= Position.Y
        && point.X < Position.X + Size.X && point.Y < Position.Y + Size.Y;
}

internal static class EditorUiInput
{
    public static bool BlocksWorld(UiBounds inspector, UiBounds toolbar, UiBounds hud, UiBounds drawer = default)
    {
        // Explicit current-frame bounds also cover newly opened or resized panels:
        // ImGui's hovered-window lookup is normally calculated before their draw.
        var pointer = ImGui.GetMousePos();
        var io = ImGui.GetIO();
        return inspector.Contains(pointer) || toolbar.Contains(pointer) || hud.Contains(pointer) || drawer.Contains(pointer)
            || ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.AllowWhenBlockedByPopup)
            || ImGui.IsAnyItemActive() && (io.MouseDown[0] || io.MouseDown[1])
            || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
    }
}
