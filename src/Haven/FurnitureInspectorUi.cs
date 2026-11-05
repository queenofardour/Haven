using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

internal enum InspectAction { None, Move, Duplicate, Delete, Music, Approve, Reject, Link, Unlink, LeaveGroup, SelectGroup, Turn, Align, Distribute }
// What a multi-piece action needs besides its kind.
internal readonly record struct GroupCommand(InspectAction Action, float Degrees = 0, Haven.Core.AlignEdge Edge = default, int Axis = 0);
internal readonly record struct GroupInspectorState(int Count, bool Linked, bool Editable, bool Moving);
internal readonly record struct InspectorState(string Name, bool Imported, Vector3 Position, float Rotation, Vector3 Scale, bool Editable, bool Moving,
    bool Orchestrion = false, bool Building = false, string Detail = "", ImTextureID? Icon = null, bool Custom = false);

// The floating inspector's contents, drawn with HavenKit inside
// FurnitureInspectorWindow: an icon header, Move / Duplicate / Delete, live transforms and appearance.
internal static class FurnitureInspectorUi
{
    public const float Width = 340;
    public static (Vector2 Min, Vector2 Max) TransformHeaderBounds { get; private set; }
    public static (Vector2 Min, Vector2 Max) ApproveBounds { get; private set; }
    public static (Vector2 Min, Vector2 Max) RejectBounds { get; private set; }

    // A staged collaborator change awaiting the owner.
    public static InspectAction Review(string name, string author, bool deleted, bool enabled)
    {
        Header(name, "Staged by " + author, null);
        HavenKit.Banner(deleted ? BannerKind.Danger : BannerKind.Info, deleted ? "Proposed removal" : "Awaiting your approval",
            "Only you see this preview. Approving publishes the change to everyone.");
        ImGui.BeginDisabled(!enabled);
        var width = (ImGui.GetContentRegionAvail().X - Px(Space2)) / 2;
        var action = InspectAction.None;
        if (HavenKit.Button("Approve", ButtonKind.Primary, new(width, 0))) action = InspectAction.Approve;
        ApproveBounds = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        ImGui.SameLine(0, Px(Space2)); if (HavenKit.Button("Reject", ButtonKind.Danger, new(width, 0))) action = InspectAction.Reject;
        RejectBounds = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        ImGui.EndDisabled();
        if (!enabled) HavenKit.Caption("Publish or discard your own draft and finish placement before reviewing.");
        return action;
    }

    public static InspectAction Body(InspectorState state, Action transforms, Action? appearance)
    {
        var action = InspectAction.None;
        Header(state.Name, state.Detail.Length > 0 ? state.Detail : state.Building ? "Building piece" : state.Imported ? "Imported from Stagehand" : "Furnishing", state.Icon, state.Custom);
        ImGui.BeginDisabled(!state.Editable);
        var trash = Px(ControlHeight); var buttons = state.Imported ? 1 : 2;
        var width = (ImGui.GetContentRegionAvail().X - trash - Px(Space2) * buttons) / buttons;
        ImGui.BeginDisabled(state.Moving);
        if (HavenKit.Button("Move", ButtonKind.Primary, new(width, 0))) action = InspectAction.Move;
        ImGui.EndDisabled();
        if (!state.Imported) { ImGui.SameLine(0, Px(Space2)); if (HavenKit.Button("Duplicate", size: new(width, 0))) action = InspectAction.Duplicate; }
        ImGui.SameLine(0, Px(Space2));
        if (DeleteButton()) action = InspectAction.Delete;
        ImGui.Dummy(Px(0, Space1));
        HavenKit.SectionTitle("Position");
        TransformHeaderBounds = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        transforms();
        if (appearance != null) { ImGui.Dummy(Px(0, Space1)); HavenKit.SectionTitle("Appearance"); appearance(); }
        ImGui.EndDisabled();
        if (state.Orchestrion) { ImGui.Dummy(Px(0, Space1)); if (HavenKit.Button("Open orchestrion", ButtonKind.Secondary, new(-1, 0), HavenIcon.Music)) action = InspectAction.Music; }
        return action;
    }

    // Several pieces: a linked group, or a selection made with Ctrl- or Shift-click. Everything here is one undoable change.
    public static GroupCommand Group(GroupInspectorState state)
    {
        var command = new GroupCommand(InspectAction.None);
        Header($"{state.Count} pieces", state.Linked ? "Linked group" : "Selection", null);
        ImGui.BeginDisabled(!state.Editable);
        var trash = Px(ControlHeight);
        var width = (ImGui.GetContentRegionAvail().X - trash - Px(Space2) * 2) / 2;
        ImGui.BeginDisabled(state.Moving);
        if (HavenKit.Button("Move", ButtonKind.Primary, new(width, 0))) command = new(InspectAction.Move);
        ImGui.EndDisabled();
        ImGui.SameLine(0, Px(Space2)); if (HavenKit.Button("Duplicate", size: new(width, 0))) command = new(InspectAction.Duplicate);
        ImGui.SameLine(0, Px(Space2)); if (DeleteButton()) command = new(InspectAction.Delete);
        ImGui.Dummy(Px(0, Space1));
        HavenKit.SectionTitle("Group");
        if (state.Linked) { if (HavenKit.Button("Unlink group", ButtonKind.Secondary, new(-1, 0))) command = new(InspectAction.Unlink); }
        else if (HavenKit.Button("Link as a group", ButtonKind.Secondary, new(-1, 0))) command = new(InspectAction.Link);
        HavenKit.Caption(state.Linked
            ? "Clicking any of these pieces selects them all. Alt-click picks one piece, to adjust or take it out of the group."
            : "Linked pieces move, turn, copy and delete together. Ctrl- or Shift-click adds or removes pieces.");
        ImGui.Dummy(Px(0, Space1));
        HavenKit.SectionTitle("Turn", "about their centre");
        var third = (ImGui.GetContentRegionAvail().X - Px(Space2) * 2) / 3;
        if (HavenKit.Button("-15°##turn-left", size: new(third, 0))) command = new(InspectAction.Turn, -15);
        ImGui.SameLine(0, Px(Space2)); if (HavenKit.Button("+15°##turn-right", size: new(third, 0))) command = new(InspectAction.Turn, 15);
        ImGui.SameLine(0, Px(Space2)); if (HavenKit.Button("+90°##turn-quarter", size: new(third, 0))) command = new(InspectAction.Turn, 90);
        ImGui.Dummy(Px(0, Space1));
        HavenKit.SectionTitle("Align", "edges and centres");
        GroupCommand Align(string label, Haven.Core.AlignEdge edge, string tip)
        {
            var clicked = HavenKit.Button(label, size: new(third, 0)); HavenKit.Tooltip(tip);
            return clicked ? new(InspectAction.Align, Edge: edge) : default;
        }
        foreach (var (axis, low, high) in new[] { ("X", Haven.Core.AlignEdge.XMin, Haven.Core.AlignEdge.XMax), ("Z", Haven.Core.AlignEdge.ZMin, Haven.Core.AlignEdge.ZMax) })
        {
            var mid = axis == "X" ? Haven.Core.AlignEdge.XCentre : Haven.Core.AlignEdge.ZCentre;
            var a = Align($"{axis} low##align-{axis}-low", low, $"Line up their lowest {axis} edges");
            ImGui.SameLine(0, Px(Space2)); var b = Align($"{axis} centre##align-{axis}-mid", mid, $"Line up their centres along {axis}");
            ImGui.SameLine(0, Px(Space2)); var c = Align($"{axis} high##align-{axis}-high", high, $"Line up their highest {axis} edges");
            foreach (var pick in new[] { a, b, c }) if (pick.Action != InspectAction.None) command = pick;
        }
        var half = (ImGui.GetContentRegionAvail().X - Px(Space2)) / 2;
        if (HavenKit.Button("Level bottoms##align-bottom", size: new(half, 0))) command = new(InspectAction.Align, Edge: Haven.Core.AlignEdge.Bottom);
        HavenKit.Tooltip("Stand them all at the lowest piece's height");
        ImGui.SameLine(0, Px(Space2)); if (HavenKit.Button("Level tops##align-top", size: new(half, 0))) command = new(InspectAction.Align, Edge: Haven.Core.AlignEdge.Top);
        HavenKit.Tooltip("Line up their tops with the highest piece");
        ImGui.BeginDisabled(state.Count < 3);
        if (HavenKit.Button("Space along X##spread-x", size: new(half, 0))) command = new(InspectAction.Distribute, Axis: 0);
        HavenKit.Tooltip("Even gaps between their centres, keeping the outermost two in place");
        ImGui.SameLine(0, Px(Space2)); if (HavenKit.Button("Space along Z##spread-z", size: new(half, 0))) command = new(InspectAction.Distribute, Axis: 2);
        HavenKit.Tooltip("Even gaps between their centres, keeping the outermost two in place");
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        HavenKit.Caption("Shortcuts: Delete removes, Ctrl+D duplicates, Ctrl+G links, Ctrl+Shift+G unlinks, Esc clears the selection.");
        return command;
    }

    // Leaves the top-right corner free for the window's close button.
    private static void Header(string name, string detail, ImTextureID? icon, bool custom = false)
    {
        var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList(); var tile = new Vector2(Px(42));
        if (custom) { draw.AddRectFilled(min, min + tile, HavenUi.U(SuccessFill), Px(RadiusControl)); HavenUi.DrawIcon(draw, HavenIcon.Custom, min + tile / 2, Px(22), Success); }
        else if (icon is { } texture) draw.AddImageRounded(texture, min, min + tile, Vector2.Zero, Vector2.One, HavenUi.U(Vector4.One), Px(RadiusControl));
        else { draw.AddRectFilled(min, min + tile, HavenUi.U(IconWell), Px(RadiusControl)); HavenUi.DrawIcon(draw, HavenIcon.Chair, min + tile / 2, Px(20), TextSecondary); }
        var x = min.X + tile.X + Px(Space3); var right = min.X + ImGui.GetContentRegionAvail().X - Px(32 + Space2);
        draw.PushClipRect(new(x, min.Y), new(right, min.Y + tile.Y), true);
        float nameHeight;
        using (HavenFonts.Use(HavenText.Title)) { nameHeight = ImGui.GetTextLineHeight(); draw.AddText(new(x, min.Y + Px(1)), HavenUi.U(Text), name); }
        using (HavenFonts.Use(HavenText.Small)) draw.AddText(new(x, min.Y + Px(3) + nameHeight), HavenUi.U(TextSecondary), detail);
        draw.PopClipRect();
        ImGui.Dummy(new(ImGui.GetContentRegionAvail().X, tile.Y + Px(Space1)));
    }

    private static bool DeleteButton()
    {
        var size = new Vector2(Px(ControlHeight)); var min = ImGui.GetCursorScreenPos();
        var clicked = HavenUi.Surface("##inspector-delete", size); var hovered = ImGui.IsItemHovered(); var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, HavenUi.U(hovered ? Vector4.Lerp(DangerFill, Danger, .12f) : DangerFill), Px(RadiusControl));
        HavenUi.DrawIcon(draw, HavenIcon.Trash, min + size / 2, Px(17), Danger);
        HavenKit.Tooltip("Delete");
        return clicked;
    }
}

// The window belongs to the editor, not the current selection. Its stable ImGui ID
// retains the dragged position across objects, closes/reopens and plugin reloads.
internal sealed class FurnitureInspectorWindow
{
    private Vector2? lastPosition;
    // A drag in progress: where the window and the mouse were when it began. Haven moves the window itself, so it
    // follows the mouse but never leaves the screen (ImGui's own move let it overshoot, then snap back).
    private Vector2? dragWindow, dragMouse;
    public UiBounds Bounds { get; private set; }
    // avoid: a panel the inspector must not cover (the open Furnish drawer). A saved or dragged position over it
    // is pushed to its left, so the two never overlap.
    public void Draw(ref bool open, Action body, UiBounds avoid = default)
    {
        Bounds = default;
        if (!open) { dragWindow = dragMouse = null; return; }
        var viewport = ImGui.GetMainViewport(); var width = Px(FurnitureInspectorUi.Width);
        // Anywhere on screen, keeping the header (and its close button) and the first rows visible.
        var minimum = viewport.Pos + Px(8, 8);
        var maximum = Vector2.Max(minimum, viewport.Pos + viewport.Size - new Vector2(width + Px(8), Px(176)));
        if (avoid.Size.X > 0) maximum.X = Math.Max(minimum.X, Math.Min(maximum.X, avoid.Position.X - width - Px(Space4)));
        // Opens on the left, below the toolbar: the Furnish drawer owns the right edge.
        var wanted = lastPosition ?? viewport.Pos + Px(Space5, 94);
        if (dragWindow is {} from && dragMouse is {} grabbed)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) wanted = from + ImGui.GetMousePos() - grabbed;
            else dragWindow = dragMouse = null;
        }
        var position = Vector2.Clamp(wanted, minimum, maximum);
        lastPosition = position;
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new(width, 0), ImGuiCond.Always);
        var maxHeight = Math.Max(Px(160), viewport.Pos.Y + viewport.Size.Y - position.Y - Px(8));
        ImGui.SetNextWindowSizeConstraints(new(width, Px(160)), new(width, maxHeight));
        HavenKit.PushTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space4, Space4)); ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusDrawer));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .96f });
        var visible = ImGui.Begin("Inspect###Haven.Inspector", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove);
        ImGui.PopStyleColor(); ImGui.PopStyleVar(2);
        if (visible)
        {
            HavenKit.KeepOnTop();
            var top = ImGui.GetCursorPos();
            body();
            var after = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new(ImGui.GetWindowContentRegionMax().X - Px(32), top.Y));
            if (HavenKit.IconButton("##inspector-close", HavenIcon.Close, "Close", size: 32)) open = false;
            ImGui.SetCursorPos(after);
            // Grabbing the inspector anywhere but a control (its header, labels, the space between) moves it. A dialog it
            // opened (an animation set, framing a picture) is its own: ImGui counts popups as part of the window that
            // opened them unless told not to, and dragging the dialog moved the inspector too.
            if (dragWindow == null && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemHovered()
                && ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.NoPopupHierarchy))
            { dragWindow = position; dragMouse = ImGui.GetMousePos(); }
            if (dragWindow != null) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }
        // Keep this frame's rectangle even when its close button was just pressed.
        Bounds = new(ImGui.GetWindowPos(), ImGui.GetWindowSize());
        ImGui.End(); HavenKit.PopTheme();
    }
}
