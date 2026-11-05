using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

// The in-room panel slot on the right. Furnish, Room, Music and Visit share it,
// one at a time, below the toolbar. Bounds are this frame's rectangle, for keeping clicks out of the room.
internal static class SidePanelUi
{
    public const float Width = 400;
    public static UiBounds Bounds { get; private set; }

    // Pair a call with End whatever it returns. Pushes HavenKit's theme for the panel's contents.
    public static bool Begin(string title, string id, ref bool open, float top)
    {
        var viewport = ImGui.GetMainViewport();
        var size = new Vector2(Px(Width), Math.Max(Px(320), viewport.Size.Y - top - Px(Space5)));
        var position = viewport.Pos + new Vector2(viewport.Size.X - size.X - Px(Space5), top);
        Bounds = new(position, size);
        HavenKit.PushTheme();
        return HavenKit.BeginPanel(title, id, position, size, ref open);
    }
    public static void End(bool open)
    {
        HavenKit.EndPanel(); HavenKit.PopTheme();
        if (!open) Bounds = default;
    }
    public static void Clear() => Bounds = default;

    // Below the toolbar when it is showing; otherwise at the toolbar's usual height.
    public static float Top => PlacementUi.OwnerBounds.Size.Y > 0 ? PlacementUi.OwnerBounds.Position.Y + PlacementUi.OwnerBounds.Size.Y + Px(Space3) : Px(84);
}
