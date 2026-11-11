using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

internal enum EditorAction { None, ToggleEditing, Undo, Redo, Place, Cancel, Catalogue, Save, ApproveAll, Room, Music, Visit, Cinematic, Npcs, Remodel, FreeCamera }
internal enum RoomPanel { None, Furnish, Room, Music, Visit, Npcs, Remodel }
// What the in-room toolbar offers: the wrench and Room only to decorators, Furnish only while editing.
// Cinematic shows the cinematic mode button (a player on a pose ball); CinematicNew draws a glow on it until first used.
internal readonly record struct RoomBarState(bool CanEdit, bool Editing, bool CanUndo, bool CanRedo, bool CanRoom, RoomPanel Panel, string VisitLabel = "Visit",
    SharedDraftToolbar? Save = null, bool Cinematic = false, bool CinematicNew = false, bool CanRemodel = false, bool FreeCamera = false);
internal readonly record struct SharedDraftToolbar(string Label, bool Enabled, EditorAction Action = EditorAction.Save);
internal readonly record struct PlacementHudState(string Name, bool FollowCursor, Vector3 Position, float Rotation, float Scale, bool HasTarget, string Status);

// In-room editing chrome: the top toolbar and the one-line placement HUD.
internal static class PlacementUi
{
    public static (Vector2 Min, Vector2 Max) OwnerButtonBounds { get; private set; }
    public static (Vector2 Min, Vector2 Max) DoneButtonBounds { get; private set; }
    public static UiBounds OwnerBounds { get; private set; }
    public static UiBounds HudBounds { get; private set; }
    // Outside a home no toolbar draws; stale bounds must not keep blocking the world.
    public static void ClearBounds() { OwnerBounds = default; HudBounds = default; }
    private const ImGuiWindowFlags Chrome = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar;

    private static void Divider()
    {
        ImGui.SameLine(0, Px(Space2)); var min = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(min + new Vector2(0, Px(9)), min + new Vector2(0, Px(ToolbarHeight - 9)), HavenUi.U(HavenTokens.Divider));
        ImGui.Dummy(new(1, Px(ToolbarHeight))); ImGui.SameLine(0, Px(Space2));
    }
    private static float Mode(string label) => HavenKit.ButtonWidth(label);

    public static EditorAction OwnerBar(bool editing, bool canUndo, bool canRedo, ref bool snap, ref float grid, ref bool drawGrid, ref bool magnet,
        SharedDraftToolbar? save = null, bool drawerOpen = false)
        => OwnerBar(new(true, editing, canUndo, canRedo, true, drawerOpen ? RoomPanel.Furnish : RoomPanel.None, "Visit", save), ref snap, ref grid, ref drawGrid, ref magnet);

    // The in-room toolbar for everyone inside a home: the wrench (decorators), Furnish (while editing), Room
    // (decorators), Music and Visit, plus undo, redo, snap options and Done while editing. Width is computed up front
    // so the centred bar never jumps.
    public static EditorAction OwnerBar(RoomBarState state, ref bool snap, ref float grid, ref bool drawGrid, ref bool magnet)
    {
        var action = EditorAction.None; var viewport = ImGui.GetMainViewport(); var pad = Px(6); var icon = Px(ToolbarHeight); var gap = Px(Space1);
        var divider = Px(Space2) * 2 + 1;
        var modes = new List<(string Label, EditorAction Action, RoomPanel Panel)>();
        if (state.Editing) modes.Add(("Furnish", EditorAction.Catalogue, RoomPanel.Furnish));
        if (state.Editing && state.CanRemodel) modes.Add(("Remodel", EditorAction.Remodel, RoomPanel.Remodel));
        if (state.CanRoom) modes.Add(("Room", EditorAction.Room, RoomPanel.Room));
        modes.Add(("NPCs", EditorAction.Npcs, RoomPanel.Npcs));
        modes.Add(("Music", EditorAction.Music, RoomPanel.Music));
        modes.Add((state.VisitLabel + "##bar-visit", EditorAction.Visit, RoomPanel.Visit));
        HavenKit.PushTheme();
        var width = modes.Sum(m => Mode(m.Label)) + gap * (modes.Count - 1);
        if (state.CanEdit) width += icon + divider;
        if (state.Cinematic) width += divider + icon;
        if (state.Editing) width += divider + icon * 4 + gap * 3 + divider + Mode("Done") + (state.Save == null ? 0 : HavenKit.ButtonWidth(state.Save.Value.Label) + gap);
        else if (state.Save != null) width += divider + HavenKit.ButtonWidth(state.Save.Value.Label);
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2, Px(18)), ImGuiCond.Always, new Vector2(.5f, 0));
        ImGui.SetNextWindowSize(new(width + pad * 2, icon + pad * 2), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad)); ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .94f });
        if (ImGui.Begin("Home editing###Haven.OwnerBar", Chrome))
        {
            HavenKit.KeepOnTop();
            OwnerBounds = new(ImGui.GetWindowPos(), ImGui.GetWindowSize());
            var height = new Vector2(0, Px(ToolbarHeight));
            if (state.CanEdit)
            {
                if (HavenKit.IconButton("##owner-edit", HavenIcon.Wrench, state.Editing ? "Finish editing your home" : "Edit your home", state.Editing, ToolbarHeight)) action = EditorAction.ToggleEditing;
                OwnerButtonBounds = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
                Divider();
            }
            for (var i = 0; i < modes.Count; i++)
            {
                if (i > 0) ImGui.SameLine();
                if (HavenKit.Button(modes[i].Label, state.Panel == modes[i].Panel ? ButtonKind.Primary : ButtonKind.Ghost, height)) action = modes[i].Action;
            }
            if (state.Cinematic)
            {
                Divider();
                if (HavenKit.IconButton("##bar-cinematic", HavenIcon.Camera, "Cinematic mode: hide everything but the chat", size: ToolbarHeight)) action = EditorAction.Cinematic;
                if (state.CinematicNew)
                {
                    // New: a soft pulsing dot on the button until it's first used.
                    var corner = new Vector2(ImGui.GetItemRectMax().X - Px(5), ImGui.GetItemRectMin().Y + Px(5));
                    var pulse = .55f + .45f * MathF.Sin((float)ImGui.GetTime() * 4);
                    var draw = ImGui.GetWindowDrawList();
                    draw.AddCircleFilled(corner, Px(7) * (1 + pulse * .3f), HavenUi.U(Accent with { W = .25f * pulse }), 16);
                    draw.AddCircleFilled(corner, Px(4), HavenUi.U(Accent), 16);
                }
            }
            if (state.Editing)
            {
                Divider();
                ImGui.BeginDisabled(!state.CanUndo); if (HavenKit.IconButton("##bar-undo", HavenIcon.Undo, "Undo", size: ToolbarHeight)) action = EditorAction.Undo; ImGui.EndDisabled();
                ImGui.SameLine(); ImGui.BeginDisabled(!state.CanRedo); if (HavenKit.IconButton("##bar-redo", HavenIcon.Redo, "Redo", size: ToolbarHeight)) action = EditorAction.Redo; ImGui.EndDisabled();
                ImGui.SameLine(); if (HavenKit.IconButton("##bar-snap", HavenIcon.Grid, "Snap and grid", snap, ToolbarHeight)) ImGui.OpenPopup("##placement-options");
                ImGui.SameLine();
                if (HavenKit.IconButton("##bar-camera", HavenIcon.FreeCamera, state.FreeCamera
                        ? "Free camera is on: WASD or a middle-drag moves it, Space and C raise and lower it, the wheel zooms, a right-drag turns it. Click to follow your character again."
                        : "Free camera: look around the room without moving your character", state.FreeCamera, ToolbarHeight))
                    action = EditorAction.FreeCamera;
                Divider();
                if (state.Save is { } save)
                {
                    ImGui.BeginDisabled(!save.Enabled);
                    if (HavenKit.Button(save.Label + "##bar-save", ButtonKind.Primary, height)) action = save.Action;
                    ImGui.EndDisabled(); ImGui.SameLine();
                }
                if (HavenKit.Button("Done", state.Save == null ? ButtonKind.Primary : ButtonKind.Secondary, height)) action = EditorAction.ToggleEditing;
                DoneButtonBounds = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
                if (HavenKit.BeginMenu("##placement-options")) { Options(ref snap, ref grid, ref drawGrid, ref magnet); ImGui.EndPopup(); }
            }
            else if (state.Save is { } save)
            {
                Divider(); ImGui.BeginDisabled(!save.Enabled);
                if (HavenKit.Button(save.Label + "##bar-save", ButtonKind.Primary, height)) action = save.Action;
                ImGui.EndDisabled();
            }
        }
        ImGui.End(); ImGui.PopStyleColor(); ImGui.PopStyleVar(3); HavenKit.PopTheme();
        return action;
    }

    private static void Options(ref bool snap, ref float grid, ref bool drawGrid, ref bool magnet)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space2, Space2));
        ImGui.Dummy(new(Px(260), 0));
        HavenKit.Toggle("Snap to grid", ref snap, Px(260));
        HavenKit.Slider("##grid", "Grid spacing", ref grid, .05f, 1, $"{grid:0.00} m", Px(260));
        HavenKit.Toggle("Show floor grid", ref drawGrid, Px(260));
        HavenKit.Toggle("Snap to nearby edges", ref magnet, Px(260));
        ImGui.PopStyleVar();
    }

    // One line of essentials while placing; the full key list is in the ? tooltip. Kept clear of the
    // inspector and the Furnish drawer.
    public static EditorAction Hud(PlacementHudState state, UiBounds inspector = default, UiBounds drawer = default)
    {
        var action = EditorAction.None; var viewport = ImGui.GetMainViewport();
        var left = viewport.Pos.X + Px(16); var right = viewport.Pos.X + viewport.Size.X - Px(16);
        foreach (var panel in new[] { inspector, drawer })
        {
            if (panel.Size.X <= 0) continue;
            var middle = viewport.Pos.X + viewport.Size.X / 2;
            if (panel.Position.X + panel.Size.X / 2 < middle) left = Math.Max(left, panel.Position.X + panel.Size.X + Px(16));
            else right = Math.Min(right, panel.Position.X - Px(16));
        }
        if (right - left < Px(420)) { left = viewport.Pos.X + Px(16); right = viewport.Pos.X + viewport.Size.X - Px(16); }
        var available = right - left; var compact = available < Px(860);
        HavenKit.PushTheme();
        ImGui.SetNextWindowPos(new((left + right) / 2, viewport.Pos.Y + viewport.Size.Y - Px(Space5)), ImGuiCond.Always, new Vector2(.5f, 1));
        ImGui.SetNextWindowSizeConstraints(Vector2.Zero, new(available, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space3, 6)); ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space3, Space2));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow)); ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .94f });
        if (ImGui.Begin("Place furnishing###Haven.PlacementHud", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar))
        {
            HavenKit.KeepOnTop();
            HudBounds = new(ImGui.GetWindowPos(), ImGui.GetWindowSize());
            var line = Px(ControlHeight);
            void Label(HavenText style, Vector4 color, string text, float maxWidth = 0)
            {
                using (HavenFonts.Use(style))
                {
                    var size = ImGui.CalcTextSize(text); var width = maxWidth > 0 ? Math.Min(size.X, maxWidth) : size.X;
                    var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
                    draw.PushClipRect(min, min + new Vector2(width, line), true);
                    draw.AddText(min + new Vector2(0, (line - ImGui.GetTextLineHeight()) / 2), HavenUi.U(color), text);
                    draw.PopClipRect(); ImGui.Dummy(new(width, line));
                }
            }
            Label(HavenText.Strong, HavenTokens.Text, state.Name, Px(compact ? 160 : 220));
            var p = state.Position;
            ImGui.SameLine(); Label(HavenText.Small, TextSecondary, FormattableString.Invariant($"X {p.X:0.00}  Y {p.Y:0.00}  Z {p.Z:0.00}  ·  {state.Rotation:0.#}°  ·  {state.Scale:0.##}×"));
            if (!compact) { ImGui.SameLine(); Label(HavenText.Small, TextTertiary, state.FollowCursor ? "Click to place · Q/E rotate · F hold" : "Enter to place · F follow cursor"); }
            ImGui.SameLine();
            HavenKit.IconButton("##hud-keys", HavenIcon.Info, "", size: ControlHeight);
            if (ImGui.IsItemHovered())
                HavenKit.Tooltip("Move: arrows, Page Up / Page Down · Rotate: Q / E · Scale: + / −\nDrag the X / Y / Z handles to move along one axis\nShift: fine · Ctrl: coarse · F: hold or follow the cursor\nEnter, click, right-click or Done: set it down · Esc: cancel\nFree camera (toolbar): WASD or middle-drag pans · Space / C up and down · wheel zooms · right-drag turns");
            ImGui.SameLine(); if (HavenKit.Button("Cancel##hud", ButtonKind.Ghost)) action = EditorAction.Cancel;
            ImGui.SameLine(); ImGui.BeginDisabled(!state.HasTarget);
            if (HavenKit.Button("Place##hud", ButtonKind.Primary)) action = EditorAction.Place;
            ImGui.EndDisabled();
        }
        ImGui.End(); ImGui.PopStyleColor(); ImGui.PopStyleVar(3); HavenKit.PopTheme();
        return action;
    }
}
