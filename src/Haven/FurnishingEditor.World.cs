using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;

namespace Haven;

internal sealed partial class FurnishingEditor
{
    private Guid? worldHome, snapMoving;
    private string? snapImported;
    private House? snapHouse;
    private PlacementBounds[] snapTargets = [];
    // Each snap target's turn (null for imported objects), to square a piece up to the neighbour it meets.
    private float?[] snapTurns = [];
    private Vector3 magnetOffset;
    private AxisDrag? axisDrag;
    private readonly AxisHandle[] axisHandles = new AxisHandle[3];
    private int hoveredAxis = -1;
    // A mouse press the game received (Haven did not own the mouse when it went down). Haven must not take the mouse
    // until it is released: a double-click that began a move used to take it mid-press, the game never saw the
    // release, and its camera drag stayed stuck on. The physical button reads as up a moment before the game has
    // handled its release message, so the mouse stays the game's for a short while after the release too.
    private bool gameHoldsMouse, capturingMouse;
    private double gameReleasedAt = double.NegativeInfinity;
    private const double ReleaseSettleSeconds = .15;
    private (bool Delete, bool Duplicate, bool Link, bool Unlink, bool Clear) previousKeys;
    private int[] magnetTargets = [];
    private static readonly int[] BoxAxes = [1, 2, 4];
    private static readonly (Vector3 Axis, uint Color, string Label)[] GuideAxes = [(Vector3.UnitX, 0xFF8888EEu, "X"), (Vector3.UnitY, 0xFF99CC99u, "Y"), (Vector3.UnitZ, 0xFFEEBB88u, "Z")];

    private void DrawPlacementWorld()
    {
        RefreshRoom(); var house = housing.ActiveHouse;
        // The inspector previews the selected furnishing's balls while decorating (for as long as it keeps saying so),
        // and the set being edited.
        if (poseEdit is { } poseEditing && (!editing || house?.Furnishings.All(f => f.Id != poseEditing.Furnishing) != false)) poseEdit = null;
        housing.PoseDraft = poseEdit is { } draft ? (draft.Furnishing, draft.Draft) : null;
        if (worldHome != house?.Id)
        { worldHome = house?.Id; EndEditing(); Select(null, null); snapHouse = null; }
        var focused = WorldEditorKeys.Focused;
        if (house != null && housing.IsReady) DrawStagedGuides(house);
        // Markers draw from the home's data each frame: a piece that won't place (a malformed one) is skipped, not fatal.
        if (house != null)
            try { DrawAmbientMarkers(house); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or NullReferenceException or ArgumentException or InvalidOperationException) { }
        if (editing && !housing.CanFurnish) EndEditing();
        var toolbar = house != null && housing.IsReady;
        if (toolbar)
        {
            SharedDraftToolbar? save = housing.CanFurnish && housing.IsSharedVisit && housing.OwnsActiveHouse
                ? housing.EditProposals.Count > 0 && !housing.HasSharedDraft
                    ? new SharedDraftToolbar($"Approve all · {housing.EditProposals.Count}", housing.CanReview, EditorAction.ApproveAll)
                    // Changes publish by themselves; the toolbar only says so while a batch is on its way, or offers
                    // Publish when a conflict holds them back.
                    : housing.AutoPublishing ? housing.EditBusy ? new SharedDraftToolbar("Saving…", false) : housing.DraftConflict != null ? new SharedDraftToolbar(housing.SaveLabel, housing.CanSaveShared) : null
                    : editing || housing.HasSharedDraft || housing.EditBusy ? new SharedDraftToolbar(housing.SaveLabel, housing.CanSaveShared) : null
                : null;
            var posing = housing.Bound is { Playing: true } && Cinematic != null;
            switch (PlacementUi.OwnerBar(new RoomBarState(housing.CanFurnish, editing, housing.CanUndo, housing.CanRedo, housing.CanFurnish, ShownPanel, VisitLabel, save,
                posing, posing && !engine.HousingCinematicSeen, housing.CanRemodel, freeCamera), ref snap, ref grid, ref drawGrid, ref magnet))
            {
                case EditorAction.FreeCamera: ToggleFreeCamera(); break;
                case EditorAction.Cinematic:
                    engine.HousingCinematicSeen = true;
                    Cinematic?.Request(true);
                    break;
                case EditorAction.ToggleEditing:
                    if (editing) EndEditing(keep: true);
                    else { StartEditing(true); if (!panelVisible) { panel = RoomPanel.Furnish; panelVisible = true; } }
                    break;
                case EditorAction.Save: housing.Run(housing.SaveSharedChanges); break;
                case EditorAction.ApproveAll: housing.Run(() => housing.ReviewAllProposals()); break;
                case EditorAction.Catalogue: TogglePanel(RoomPanel.Furnish); break;
                case EditorAction.Room: TogglePanel(RoomPanel.Room); break;
                case EditorAction.Music: TogglePanel(RoomPanel.Music); break;
                case EditorAction.Npcs: TogglePanel(RoomPanel.Npcs); break;
                case EditorAction.Remodel: TogglePanel(RoomPanel.Remodel); break;
                case EditorAction.Visit: TogglePanel(RoomPanel.Visit); break;
                case EditorAction.Undo: housing.Run(housing.Undo); break;
                case EditorAction.Redo: housing.Run(housing.Redo); break;
            }
        }
        else PlacementUi.ClearBounds();
        var drawerBounds = toolbar ? DrawPanels() : default;
        if (!toolbar) SidePanelUi.Clear();
        if (editing) DrawFloatingInspector(drawerBounds);
        if (house != null && housing.IsReady) { DrawPictureConsent(); DrawDownloadFailures(); DrawNpcModsPrompts(); }
        if (!focused) clicks.Reset();
        var ready = house != null && housing.IsReady;
        var io = ImGui.GetIO();
        // Dalamud only feeds mouse buttons to ImGui while WantCaptureMouse is
        // true, and clears MouseDown over the game viewport. World tools must
        // read physical buttons; current panel bounds still block UI presses.
        var buttons = focused ? WorldEditorKeys.Read() : default;
        var placing = editing && housing.IsPlacing;
        if (placing && !housing.IsFineEditing) DrawPlacementHud(drawerBounds);
        var underUi = EditorUiInput.BlocksWorld(editing ? inspectorWindow.Bounds : default,
            toolbar ? PlacementUi.OwnerBounds : default, placing && !housing.IsFineEditing ? PlacementUi.HudBounds : default, drawerBounds);
        if (!focused || !placing || !ready || housing.IsFineEditing) axisDrag = null;
        UpdateAxisHandles(placing && !housing.IsFineEditing ? house : null);
        hoveredAxis = focused && !underUi ? PlacementGizmoUi.HitTest(axisHandles, ImGui.GetMousePos(), HavenUi.S) : -1;
        if (hoveredAxis >= 0) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        Vector3 origin = default, direction = default, target = default; Vector3? normal = null;
        var ray = focused && ready && editing && (!underUi || axisDrag != null) && !housing.IsFineEditing
            && (placing || buttons.Left || buttons.Right) && TryRay(out origin, out direction);
        var gizmoOwnsMouse = axisDrag != null;
        var hasTarget = placing && !followCursor && housing.PreviewHasTarget;
        if (placing && followCursor && focused && !underUi && !housing.IsFineEditing && !gizmoOwnsMouse && hoveredAxis < 0)
        {
            // The collision ray also gives the surface's normal, so a piece set on a floor or table rests on it.
            if (ray && SurfaceHit(origin, direction) is {} hit) { target = hit.Point; normal = hit.Normal; hasTarget = true; }
            else hasTarget = gameGui.ScreenToWorld(ImGui.GetMousePos(), out target, 100);
            if (!hasTarget && ray && engine.HousingPlayerPosition is Vector3 player && PlacementMath.RayPlane(origin, direction, player.Y) is Vector3 floor)
            { target = floor; hasTarget = true; placementStatus = "Floor plane at your feet"; }
            else placementStatus = hasTarget ? "Cursor follows the surface" : "Aim at a surface, or press F for precision controls";
        }
        var commands = input.Update(buttons, focused, underUi, io.WantTextInput, placing, ready, hasTarget, editing, rightTurnsCamera: freeCamera);
        UpdateFreeCamera(ready ? house : null, focused, underUi, placing && !housing.IsFineEditing, io.WantTextInput);
        if (axisDrag == null && ray && hoveredAxis >= 0 && input.LeftPressed && !underUi)
        {
            axisDrag = AxisDrag.Begin(hoveredAxis, housing.PreviewPosition, FurnitureCatalogue.HouseMatrix(house!), origin, direction);
            if (axisDrag != null) { followCursor = false; movementInput.Reset(); gizmoOwnsMouse = true; }
        }
        if (gizmoOwnsMouse || hoveredAxis >= 0) commands = commands with { Place = false };
        var movement = movementInput.Update(focused ? WorldEditorKeys.ReadPlacement() : default, ImGui.GetTime(),
            !focused || !ready || !placing || io.WantTextInput || housing.IsFineEditing || gizmoOwnsMouse || ImGui.IsAnyItemActive());
        if (placing && commands.Cancel) { axisDrag = null; housing.Run(housing.CancelPlacement); return; }
        // A right-click sets the piece down where it stands (also mid-drag on a handle).
        if (placing && (commands.Keep || gizmoOwnsMouse && input.RightPressed)) { axisDrag = null; housing.Run(housing.KeepPlacement); return; }
        housing.HighlightedFurnishing = null;
        var overBall = ready && house != null && DrawPoses(house, focused && !underUi && !io.WantTextInput, focused);
        if (!editing && ready && house != null && !placing && !overBall) DrawInteractions(house, focused && !underUi && !io.WantTextInput);
        if (buttons.Left || buttons.Right)
        {
            if (!capturingMouse && (input.LeftPressed || input.RightPressed)) gameHoldsMouse = true;
            if (gameHoldsMouse) gameReleasedAt = double.PositiveInfinity;
        }
        else if (gameHoldsMouse)
        {
            if (double.IsPositiveInfinity(gameReleasedAt)) gameReleasedAt = ImGui.GetTime();
            if (ImGui.GetTime() - gameReleasedAt >= ReleaseSettleSeconds) gameHoldsMouse = false;
        }
        capturingMouse = false;
        if (!ready || !editing || house == null) { clicks.Reset(); engine.HousingOutlineShellPiece(0); engine.HousingGhostShellPiece(0); return; }
        SettleRemodelMove(placing);
        // Remodelling picks the house's own pieces instead of furnishings.
        if (ShownPanel == RoomPanel.Remodel && !placing) { RemodelPointer(focused, underUi, commands.Select && !OverGameWindow()); DrawPlacementGuides(house); return; }
        engine.HousingOutlineShellPiece(0); engine.HousingGhostShellPiece(0);
        if (!placing) HoverAndShortcuts(house, focused, underUi, buttons, io.WantTextInput);
        magnetOffset = Vector3.Zero;
        if (placing && !housing.IsFineEditing)
        {
            if (focused)
            {
                if (!gameHoldsMouse)
                {
                    io.WantCaptureMouse = true;
                    ImGui.SetNextFrameWantCaptureMouse(true);
                    capturingMouse = true;
                }
                io.WantCaptureKeyboard = true;
                ImGui.SetNextFrameWantCaptureKeyboard(true);
            }
            if (axisDrag is {} drag)
            {
                if (ray && drag.Update(origin, direction, snap ? grid * (buttons.Shift ? .1f : 1) : 0, buttons.Shift) is {} position)
                {
                    var world = Vector3.Transform(position, FurnitureCatalogue.HouseMatrix(house));
                    housing.Run(() => housing.MovePreview(world));
                }
                if (!buttons.Left) axisDrag = null;
            }
            if (movement.ToggleFollow) followCursor = !followCursor;
            if (movement.Delta != Vector3.Zero) { followCursor = false; housing.Run(() => housing.NudgePreview(movement.Delta)); }
            var turn = movement.Turn + commands.Rotation;
            if (turn != 0) housing.Run(() => housing.RotatePreview(turn));
            if (movement.ScaleFactor != 1) housing.Run(() => housing.ScalePreview(movement.ScaleFactor));
            // While a right-drag turns the free camera, the piece holds still rather than chase the pointer.
            if (followCursor && hasTarget && focused && !underUi && !gizmoOwnsMouse && hoveredAxis < 0 && !(freeCamera && buttons.Right))
            {
                var matrix = FurnitureCatalogue.HouseMatrix(house);
                var point = PlacementTools.SnapInHouse(target, matrix, snap ? grid : 0);
                if (housing.Placement is {} piece && ItemBounds(piece) is {} rest && Matrix4x4.Invert(matrix, out var toHouse))
                {
                    var lift = PlacementTools.Rest(rest.Minimum.Y - housing.PreviewPosition.Y, PlacementTools.FacesUp(normal is {} n ? Vector3.TransformNormal(n, toHouse) : null));
                    point = Vector3.Transform(Vector3.Transform(point, toHouse) + new Vector3(0, lift, 0), matrix);
                }
                magnetTargets = [];
                // Alt holds off the magnet for a moment, to set a piece down freely beside another.
                if (magnet && !WorldEditorKeys.Alt && Matrix4x4.Invert(matrix, out var inverse) && PreviewBounds() is {} box)
                {
                    RefreshSnapTargets(house);
                    var local = Vector3.Transform(point, inverse);
                    var delta = local - housing.PreviewPosition;
                    var snapped = PlacementTools.Snap(new(box.Minimum + delta, box.Maximum + delta), snapTargets);
                    magnetOffset = snapped.Offset; magnetTargets = snapped.Targets;
                    point = Vector3.Transform(local + magnetOffset, matrix);
                    // Nearly square to the neighbour it meets (or a quarter turn from it): square it up, so their faces
                    // meet flush rather than at a slight angle.
                    if (housing.Placement is {} turning)
                        foreach (var index in magnetTargets)
                            if (index < snapTurns.Length && snapTurns[index] is float neighbour)
                            {
                                var off = ((neighbour - turning.Rotation) % 90 + 135) % 90 - 45;
                                if (Math.Abs(off) is > .01f and <= 8) housing.Run(() => housing.RotatePreview(off));
                                break;
                            }
                }
                housing.Run(() => housing.MovePreview(point));
            }
            if (!gizmoOwnsMouse && (commands.Place || movement.Confirm) && (hasTarget || housing.PreviewHasTarget)) housing.Run(housing.Place);
        }
        else if (!placing && ray)
        {
            if (commands.Select && !OverGameWindow())
            {
                var picked = Pick(house, origin, direction);
                var key = picked?.Proposal?.ToString() ?? picked?.Id?.ToString() ?? picked?.ImportedKey;
                var twice = clicks.Click(key, ImGui.GetTime(), ImGui.GetMousePos(), io.MouseDoubleClickTime);
                // Empty space does not dismiss an inspector or discard its selection.
                if (picked is {} choice)
                {
                    if (choice.Id is Guid id && choice.Proposal == null)
                    {
                        // A click selects a piece with its linked group; Alt-click just the piece; Ctrl- or Shift-click
                        // adds or removes.
                        var members = WorldEditorKeys.Alt ? [id] : FurnishingGroups.Members(house, id);
                        if ((WorldEditorKeys.Ctrl || buttons.Shift) && !twice) ToggleSelection(members, id);
                        else if (twice && selection.Contains(id)) selected = id; // Double-click any selected piece: move them all.
                        else SelectPieces(members, id);
                    }
                    else { Select(choice.Id, choice.ImportedKey); SelectProposal(choice.Proposal); }
                    if (selected != null || selectedImported != null || selectedProposal != null) OpenInspector();
                    if (twice && selectedProposal == null && (selected != null || selectedImported != null)) BeginMove();
                }
            }
            if (commands.Context && Pick(house, origin, direction) is {} contextItem)
            { Select(contextItem.Id, contextItem.ImportedKey); SelectProposal(contextItem.Proposal); OpenInspector(); }
        }
        DrawPlacementGuides(house);
        if (placing && !housing.IsFineEditing) PlacementGizmoUi.Draw(axisHandles, hoveredAxis, axisDrag?.Axis ?? -1);
    }

    // The game's own chat box (or any game text field) has focus: Delete there edits text, not the room.
    internal static unsafe bool GameTextInput()
    {
        var module = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule.Instance();
        return module != null && module->AtkModule.IsTextInputActive();
    }
    private void HoverAndShortcuts(House house, bool focused, bool underUi, EditorButtons buttons, bool typing)
    {
        // The piece a click would select is outlined, as the game outlines what it targets.
        if (focused && !underUi && !housing.IsFineEditing && !OverGameWindow() && TryRay(out var origin, out var direction)
            && Pick(house, origin, direction)?.Id is Guid id) housing.HighlightedFurnishing = id;
        var keys = focused && !typing && !ImGui.IsAnyItemActive() && !GameTextInput() ? WorldEditorKeys.ReadSelectionKeys() : default;
        bool Pressed(bool now, bool before) => now && !before;
        var pieces = SelectedPieces;
        if (housing.CanFurnish && pieces.Length > 0)
        {
            if (Pressed(keys.Delete, previousKeys.Delete)) { housing.Run(() => housing.RemoveMany(pieces)); Select(null, null); inspectorOpen = false; }
            else if (Pressed(keys.Duplicate, previousKeys.Duplicate) && selected is Guid leader)
            {
                // The copy stands on the original until it's dragged out.
                followCursor = false; axisDrag = null; movementInput.Reset();
                if (pieces.Length > 1) housing.Run(() => housing.BeginGroupDuplicate(pieces, leader));
                else if (house.Furnishings.FirstOrDefault(f => f.Id == leader) is {} item) housing.Run(() => { housing.BeginDuplicate(item with { Group = null }); Select(housing.Placement!.Id, null); });
            }
            else if (Pressed(keys.Link, previousKeys.Link) && pieces.Length > 1) housing.Run(() => housing.LinkGroup(pieces));
            else if (Pressed(keys.Unlink, previousKeys.Unlink)) housing.Run(() => housing.UnlinkGroup(pieces));
        }
        if (Pressed(keys.Clear, previousKeys.Clear) && selected != null) { Select(null, null); inspectorOpen = false; }
        // The game must not also act on a shortcut Haven used.
        if (keys.Delete || keys.Duplicate || keys.Link || keys.Unlink) keyState.ClearAll();
        previousKeys = keys;
    }

    private void DrawFloatingInspector(UiBounds avoid)
    {
        if (selected == null && selectedImported == null && selectedProposal == null) inspectorOpen = false;
        inspectorWindow.Draw(ref inspectorOpen, () => { ImGui.PushID("floating-inspector"); DrawSelection(); ImGui.PopID(); },
            avoid);
    }

    private void DrawStagedGuides(House house)
    {
        var matrix = FurnitureCatalogue.HouseMatrix(house); var draw = ImGui.GetBackgroundDrawList();
        var labels = 0;
        foreach (var pending in housing.StagedFurniture)
        {
            if (ItemBounds(pending.Item) is not {} box) continue;
            Draw(box, pending.Proposal, pending.Author, pending.Deleted);
        }
        foreach (var pending in housing.StagedImported)
            Draw(PlacementBounds.Transform(pending.Item.Minimum, pending.Item.Maximum, pending.Item.Definition.Transform), pending.Proposal, pending.Author, pending.Deleted);
        void Draw(PlacementBounds box, Guid proposal, string author, bool deleted)
        {
            var center = Vector3.Transform((box.Minimum + box.Maximum) / 2, matrix);
            if (engine.HousingPlayerPosition is {} player && Vector3.DistanceSquared(player, center) > 40 * 40) return;
            var color = deleted ? 0xFF9292EFu : 0xFFFFBE65u;
            Vector3 Corner(int i) => new((i & 1) == 0 ? box.Minimum.X : box.Maximum.X, (i & 2) == 0 ? box.Minimum.Y : box.Maximum.Y, (i & 4) == 0 ? box.Minimum.Z : box.Maximum.Z);
            for (var i = 0; i < 8; i++) foreach (var bit in BoxAxes) if ((i & bit) == 0
                && gameGui.WorldToScreen(Vector3.Transform(Corner(i), matrix), out var a)
                && gameGui.WorldToScreen(Vector3.Transform(Corner(i | bit), matrix), out var b))
                draw.AddLine(a, b, color, 1.5f * HavenUi.S);
            if ((labels < 12 || selectedProposal == proposal) && gameGui.WorldToScreen(Vector3.Transform(new Vector3(box.Minimum.X, box.Maximum.Y, box.Minimum.Z), matrix), out var label))
            {
                labels++;
                var text = (deleted ? "Remove · " : "Pending · ") + author;
                var size = ImGui.CalcTextSize(text); var pad = new Vector2(7, 4) * HavenUi.S;
                draw.AddRectFilled(label - pad, label + size + pad, 0xE8202428u, 5 * HavenUi.S);
                draw.AddText(label, color, text);
            }
        }
    }

    private void UpdateAxisHandles(House? house)
    {
        Array.Clear(axisHandles);
        if (house == null || !housing.PreviewHasTarget) return;
        var matrix = FurnitureCatalogue.HouseMatrix(house); var position = housing.PreviewPosition;
        var world = Vector3.Transform(position, matrix);
        if (!gameGui.WorldToScreen(world, out var start)) return;
        for (var i = 0; i < GuideAxes.Length; i++)
        {
            var axis = GuideAxes[i].Axis;
            if (!gameGui.WorldToScreen(Vector3.Transform(position + axis, matrix), out var unit)) continue;
            var pixels = Vector2.Distance(start, unit);
            if (!float.IsFinite(pixels) || pixels < 2) continue;
            var length = Math.Clamp(88 * HavenUi.S / pixels, .1f, 10);
            if (!gameGui.WorldToScreen(Vector3.Transform(position + axis * length, matrix), out var end)) continue;
            var screenAxis = end - start;
            if (screenAxis.LengthSquared() < 28 * 28 * HavenUi.S * HavenUi.S) continue;
            axisHandles[i] = new(i, start + Vector2.Normalize(screenAxis) * 18 * HavenUi.S, end, true);
        }
    }

    private PlacementBounds? ItemBounds(Furnishing item) => Catalogue.Bounds(item);
    private PlacementBounds? PreviewBounds()
    {
        if (housing.Placement is {} item) return ItemBounds(item);
        if (housing.MovingImportedKey is {} key && importedObjects.FirstOrDefault(x => x.Definition.Key == key) is {} imported)
            return PlacementBounds.Transform(imported.Minimum, imported.Maximum, housing.PreviewMatrix);
        return null;
    }
    private void RefreshSnapTargets(House house)
    {
        if (ReferenceEquals(snapHouse, house) && snapMoving == housing.MovingId && snapImported == housing.MovingImportedKey) return;
        snapHouse = house; snapMoving = housing.MovingId; snapImported = housing.MovingImportedKey;
        List<PlacementBounds> boxes = []; List<float?> turns = [];
        var moving = housing.FollowerPreviews.Select(f => f.Id).ToHashSet();
        foreach (var item in house.Furnishings)
            if (item.Id != snapMoving && !moving.Contains(item.Id) && ItemBounds(item) is {} box) { boxes.Add(box); turns.Add(item.Rotation); }
        foreach (var item in importedObjects)
        {
            var definition = item.Definition;
            if (definition.Disabled || definition.Type != "BgObject" || snapImported != null && (definition.Key == snapImported || definition.Key.StartsWith(snapImported + "/", StringComparison.Ordinal))) continue;
            boxes.Add(PlacementBounds.Transform(item.Minimum, item.Maximum, definition.Transform)); turns.Add(null);
        }
        snapTargets = boxes.ToArray(); snapTurns = turns.ToArray();
    }

    private void DrawPlacementHud(UiBounds drawerBounds)
    {
        switch (PlacementUi.Hud(new(housing.PreviewName, followCursor, housing.PreviewPosition, housing.PreviewRotation, housing.PreviewScale, housing.PreviewHasTarget, placementStatus), inspectorWindow.Bounds, drawerBounds))
        {
            case EditorAction.Place: housing.Run(housing.Place); break;
            case EditorAction.Cancel: housing.Run(housing.CancelPlacement); break;
        }
    }

    private void DrawPlacementGuides(House house)
    {
        var matrix = FurnitureCatalogue.HouseMatrix(house); var draw = ImGui.GetBackgroundDrawList();
        void Line(Vector3 a, Vector3 b, uint color, float width = 1)
        { if (gameGui.WorldToScreen(Vector3.Transform(a, matrix), out var x) && gameGui.WorldToScreen(Vector3.Transform(b, matrix), out var y)) draw.AddLine(x, y, color, width); }
        PlacementBounds? bounds = housing.IsPlacing ? PreviewBounds() : selected is Guid id && house.Furnishings.FirstOrDefault(x => x.Id == id) is {} item ? ItemBounds(item)
            : ImportedSelection() is {} imported ? PlacementBounds.Transform(imported.Minimum, imported.Maximum, imported.Definition.Transform) : null;
        var soft = HavenUi.U(HavenTokens.Accent with { W = .55f });
        void Box(PlacementBounds b, uint colour, float width)
        {
            Vector3 C(int i) => new((i & 1) == 0 ? b.Minimum.X : b.Maximum.X, (i & 2) == 0 ? b.Minimum.Y : b.Maximum.Y, (i & 4) == 0 ? b.Minimum.Z : b.Maximum.Z);
            for (var i = 0; i < 8; i++) foreach (var bit in BoxAxes) if ((i & bit) == 0) Line(C(i), C(i | bit), colour, width);
        }
        if (housing.IsPlacing) { foreach (var follower in housing.FollowerPreviews) if (ItemBounds(follower) is {} f) Box(f, soft, 1.2f); }
        else if (selection.Count > 1)
            foreach (var piece in house.Furnishings) if (piece.Id != selected && selection.Contains(piece.Id) && ItemBounds(piece) is {} s) Box(s, soft, 1.2f);
        if (housing.IsPlacing && magnetOffset != Vector3.Zero)
            foreach (var index in magnetTargets) if (index < snapTargets.Length) Box(snapTargets[index], HavenUi.U(HavenTokens.Success with { W = .7f }), 1.2f);
        if (bounds is not {} box) return;
        if (housing.IsPlacing && drawGrid)
        {
            var spacing = Math.Max(grid, 0.05f); var center = PlacementMath.Snap(housing.PreviewPosition, spacing);
            var y = box.Minimum.Y + 0.003f;
            for (var n = -10; n <= 10; n++)
            {
                var offset = n * spacing; var extent = 10 * spacing;
                var color = n == 0 ? 0xB07BB7D6u : 0x407BB7D6u;
                Line(new(center.X + offset, y, center.Z - extent), new(center.X + offset, y, center.Z + extent), color);
                Line(new(center.X - extent, y, center.Z + offset), new(center.X + extent, y, center.Z + offset), color);
            }
        }
        var wire = magnetOffset != Vector3.Zero ? HavenUi.U(HavenTokens.Success) : HavenUi.U(HavenTokens.Accent);
        Vector3 Corner(int i) => new((i & 1) == 0 ? box.Minimum.X : box.Maximum.X, (i & 2) == 0 ? box.Minimum.Y : box.Maximum.Y, (i & 4) == 0 ? box.Minimum.Z : box.Maximum.Z);
        for (var i = 0; i < 8; i++) foreach (var bit in BoxAxes) if ((i & bit) == 0) Line(Corner(i), Corner(i | bit), wire, 1.5f);
        var origin = housing.IsPlacing ? housing.PreviewPosition : (box.Minimum + box.Maximum) / 2;
        if (!housing.IsPlacing) foreach (var (axis, color, label) in GuideAxes)
        {
            var end = origin + axis * 0.5f; Line(origin, end, color, 2);
            if (gameGui.WorldToScreen(Vector3.Transform(end, matrix), out var screen)) draw.AddText(screen, color, label);
        }
    }
}
