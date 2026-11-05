using System.Numerics;
using static Haven.HavenTokens;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Haven.Core;
using HavenNative;
using HavenNative.Services;

namespace Haven;

internal sealed partial class FurnishingEditor(HousingController housing, NativeEngine engine, ITextureProvider textures, IGameGui gameGui, IKeyState keyState)
{
    public System.Action? CloseWorkspace { get; set; }
    private bool inspectorOpen;
    private readonly FurnitureInspectorWindow inspectorWindow = new();
    private int floor;
    private Guid? selected;
    // Every selected piece (the inspector's subject, selected, is one of them): a whole linked group, or several
    // pieces picked with Ctrl-click.
    private readonly HashSet<Guid> selection = [];
    private Guid? selectedProposal;
    private ulong selectedProposalAuthor;
    private string[]? selectedProposalPath;
    private bool editing, snap = true, drawGrid = true, magnet = true, followCursor = true, consumingKeys;
    private float grid = 0.1f;
    private string? filterKey;
    private FurnitureEntry[] filtered = [];
    private DrawerItem[] drawerItems = [];
    private DrawerRoomRow[] drawerRoom = [];
    private readonly FurnishDrawerState drawer = new();
    private bool drawerOpen;
    private FurnishLists? lists;
    private int listsVersion;
    private readonly WorldEditorInput input = new();
    private readonly PlacementKeyInput movementInput = new();
    private readonly ObjectClickInput clicks = new();
    private string placementStatus = "Point at the room floor to position the preview.";
    private readonly Dictionary<uint, float> floatEdits = [];
    private readonly Dictionary<uint, Vector3> positionEdits = [];
    // The volume slider's value while dragging; committed on release, since each change restarts the sound.
    private (Guid Item, float Value)? liveSoundVolume;
    private sealed record TrackRow(uint Selection, string Title, string Label);
    private List<(uint sel, string title, string category, int catOrder, int order)>? trackSource;
    private TrackRow[] trackRows = [];
    private readonly Dictionary<uint, string> trackTitles = [];
    private FurnitureCatalogue Catalogue => housing.Catalogue;

    private sealed record RoomRow(Guid? Id, string? ImportedKey, string Name, string Detail);
    private House? roomHouse;
    private RoomRow[] roomRows = [];
    private ImportedObject[] importedObjects = [];
    private string? selectedImported;

    private void RefreshRoom()
    {
        var house = housing.ActiveHouse;
        if (selectedProposal != null && !housing.EditProposals.Any(p => p.Batch.Id == selectedProposal))
            selectedProposal = housing.EditProposals.FirstOrDefault(p => p.Author == selectedProposalAuthor && selectedProposalPath != null
                && p.Batch.Changes.Any(c => HostedEditing.Overlaps(c.Path, selectedProposalPath)))?.Batch.Id;
        if (selected is Guid selection && housing.Placement?.Id != selection && house?.Furnishings.Any(x => x.Id == selection) != true) Select(null, null);
        if (ReferenceEquals(roomHouse, house)) return;
        roomHouse = house;
        this.selection.RemoveWhere(id => house?.Furnishings.Any(x => x.Id == id) != true);
        importedObjects = house == null ? [] : housing.Imported.Objects(house.Stage);
        List<RoomRow> rows = [];
        var moved = housing.MovedShellCopies;
        if (house != null)
            foreach (var item in house.Furnishings)
            {
                if (moved.Contains(item.Id)) continue;
                var entry = Catalogue.Get(item);
                var category = housing.ShowsCustom(item) ? "Custom furniture" : entry.Category;
                rows.Add(new(item.Id, null, housing.NameOf(item), item.Group == null ? category : category + " · linked"));
            }
        foreach (var item in importedObjects)
            rows.Add(new(null, item.Definition.Key, item.Name, "Imported from Stagehand"));
        roomRows = rows.ToArray();
    }

    private void Select(Guid? id, string? imported)
    {
        selectedProposal = null; selectedProposalPath = null; selected = id; selectedImported = imported;
        selection.Clear(); if (id is Guid piece) selection.Add(piece);
    }
    private void SelectPieces(IEnumerable<Guid> ids, Guid primary)
    { Select(primary, null); selection.UnionWith(ids); }
    // Ctrl-click: adds a piece (with its group) to the selection, or takes it out when it is already in.
    private void ToggleSelection(Guid[] ids, Guid primary)
    {
        if (selectedImported != null || selectedProposal != null) { SelectPieces(ids, primary); return; }
        if (ids.All(selection.Contains))
        {
            selection.ExceptWith(ids);
            selected = selection.Count == 0 ? null : selected is Guid current && selection.Contains(current) ? current : selection.First();
        }
        else { selection.UnionWith(ids); selected = primary; }
    }
    private Guid[] SelectedPieces => selected == null ? [] : [.. selection];
    private void SelectProposal(Guid? id)
    {
        selectedProposal = id;
        if (housing.EditProposals.FirstOrDefault(p => p.Batch.Id == id) is not {} proposal) return;
        selectedProposalAuthor = proposal.Author; selectedProposalPath = proposal.Batch.Changes.FirstOrDefault()?.Path;
    }
    public void InspectProposal(Guid id)
    {
        if (!housing.EditProposals.Any(s => s.Batch.Id == id)) return;
        StartEditing(); Select(null, null); SelectProposal(id); OpenInspector();
    }

    /// <param name="keep">Done: a piece being placed or moved is set down where it is. Otherwise (another home, no
    /// decorating access any more) it's dropped.</param>
    public void EndEditing(bool keep = false)
    {
        editing = false; inspectorOpen = false; axisDrag = null;
        housing.Run(keep ? housing.KeepPlacement : housing.CancelPlacement); clicks.Reset(); movementInput.Reset();
    }
    private void StartEditing(bool closeWorkspace = false)
    { if (!housing.CanFurnish) return; editing = true; clicks.Reset(); if (closeWorkspace) CloseWorkspace?.Invoke(); }
    private void OpenInspector()
    { inspectorOpen = true; CloseWorkspace?.Invoke(); }
    // Estate Teleportation chosen inside a home: asked here first (it would take the player out); after they leave and
    // are back where they entered, the game's own teleport runs.
    private ulong? estateAsked, estateAfterReturn;
    private long estateReadyAt;
    private void OnEstateTeleportAsked(ulong character) => estateAsked = character;
    // Relay maintenance announced by the operator: a countdown under the room's toolbar while in a home.
    private void DrawMaintenance()
    {
        if (housing.MaintenanceBanner is not { } banner) return;
        HavenKit.PushTheme();
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2, Px(18 + ToolbarHeight + Space4)), ImGuiCond.Always, new Vector2(.5f, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(0, 0)); ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
        if (ImGui.Begin("###Haven.Maintenance", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground))
        { HavenKit.KeepOnTop(); HavenKit.Banner(BannerKind.Warning, banner.Title, banner.Text, Px(460)); }
        ImGui.End();
        ImGui.PopStyleColor(); ImGui.PopStyleVar();
        HavenKit.PopTheme();
    }

    // A ball whose animation uses a prop: wear it while the animation plays? Asked once per animation, or remembered.
    private bool propRemember = true;
    private void DrawPropQuestion()
    {
        if (housing.PropAsked is not { } asked) return;
        HavenKit.PushTheme();
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size / 2, ImGuiCond.Always, new Vector2(.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space5)); ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space2, Space3));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow)); ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .97f });
        if (ImGui.Begin("Animation prop###Haven.PoseProp", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar))
        {
            HavenKit.KeepOnTop();
            using (HavenFonts.Use(HavenText.Title)) ImGui.TextUnformatted("This animation uses a prop");
            HavenKit.Caption(asked.Npc is { } npc
                ? $"{asked.Animations} needs a piece of gear to look right: {asked.Item}. While {npc} plays it, Haven puts it in {npc}'s {asked.Slot.ToLowerInvariant()} slot "
                    + "with Glamourer and switches on its mod in Penumbra (in a \"Haven Animation Props\" folder) on this PC; when it stands up, its own item comes back. "
                    + "Each PC in the gathering asks this for its own copy of the NPC. Your answer counts for you on this animation too."
                : $"{asked.Animations} needs a piece of gear to look right: {asked.Item}. While you play it, Haven puts it in your {asked.Slot.ToLowerInvariant()} slot "
                    + "with Glamourer and switches on its mod in Penumbra (in a \"Haven Animation Props\" folder) just for you; when you stand up, your own item comes back. "
                    + "Sync plugins show it to the friends you're paired with.", Px(420));
            HavenKit.Toggle("Remember my choice for this animation##prop-remember", ref propRemember);
            ImGui.Dummy(Px(0, Space1));
            HavenKit.AlignRight(HavenKit.ButtonWidth("Not now") + HavenKit.ButtonWidth("Put it on") + Px(Space2));
            if (HavenKit.Button("Not now##prop", ButtonKind.Ghost)) housing.AnswerProp(false, propRemember);
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Put it on##prop", ButtonKind.Primary)) housing.AnswerProp(true, propRemember);
        }
        ImGui.End();
        ImGui.PopStyleColor(); ImGui.PopStyleVar(3);
        HavenKit.PopTheme();
    }

    private void DrawEstateTeleport()
    {
        if (estateAsked is not { } character) return;
        HavenKit.PushTheme();
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size / 2, ImGuiCond.Always, new Vector2(.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space5)); ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space2, Space3));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow)); ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .97f });
        if (ImGui.Begin("Estate Teleportation###Haven.EstateLeave", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar))
        {
            HavenKit.KeepOnTop();
            using (HavenFonts.Use(HavenText.Title)) ImGui.TextUnformatted("Estate Teleportation");
            var hosting = engine.HousingRelay.IsHost && !engine.HousingRelay.SoloMode;
            HavenKit.Caption("Teleporting to this friend's estate takes you out of your Haven home" + (hosting ? " and ends your gathering for everyone" : "") + ". Leave and teleport?", Px(380));
            ImGui.Dummy(Px(0, Space1));
            HavenKit.AlignRight(HavenKit.ButtonWidth("Stay") + HavenKit.ButtonWidth("Leave and teleport") + Px(Space2));
            if (HavenKit.Button("Stay##estate", ButtonKind.Ghost)) estateAsked = null;
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Leave and teleport##estate", ButtonKind.Primary))
            {
                estateAsked = null; estateAfterReturn = character; estateReadyAt = 0;
                housing.Run(housing.StopVisit);
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(); ImGui.PopStyleVar(3);
        HavenKit.PopTheme();
    }
    // Once the return has finished (no session, nothing pending) and a moment has passed, the teleport runs.
    private void UpdateEstateTeleport()
    {
        if (estateAfterReturn is not { } character) return;
        if (engine.HousingSessionActive || engine.HousingReturnPending) { estateReadyAt = 0; return; }
        if (estateReadyAt == 0) { estateReadyAt = Environment.TickCount64 + 2000; return; }
        if (Environment.TickCount64 < estateReadyAt) return;
        estateAfterReturn = null;
        if (!engine.HousingTeleportToEstate(character)) housing.Run(() => throw new InvalidOperationException("Estate Teleportation couldn't start. Open it again from your friend list."));
    }

    public void ConsumeGameInput()
    {
        UpdateEstateTeleport();
        HoldPoseKeys();
        if (!WorldEditorKeys.Focused) { consumingKeys = false; return; }
        if (editing && housing.IsPlacing && !housing.IsFineEditing)
        { consumingKeys = true; keyState.ClearAll(); }
        else if (consumingKeys)
        { if (WorldEditorKeys.AnyMovementKeyDown) keyState.ClearAll(); else consumingKeys = false; }
    }
    private void BeginMove()
    {
        followCursor = false; axisDrag = null; movementInput.Reset();
        if (selected is Guid leader && selection.Count > 1) { var pieces = SelectedPieces; housing.Run(() => housing.BeginGroupPlacement(pieces, leader)); }
        else if (selected is Guid id && housing.ActiveHouse?.Furnishings.FirstOrDefault(x => x.Id == id) is {} item) housing.Run(() => housing.BeginPlacement(item, id));
        else if (selectedImported is {} key) housing.Run(() => housing.BeginImportedMove(key));
    }

    private ImportedObject? ImportedSelection()
    {
        if (selectedImported == null) return null;
        for (var i = 0; i < importedObjects.Length; i++)
            if (importedObjects[i].Definition.Key == selectedImported) return importedObjects[i];
        return null;
    }

    // The launcher's Decorate: start editing with the Furnish drawer open.
    public void StartDecorating() { StartEditing(); ShowPanel(RoomPanel.Furnish); }

    private void DrawSelection()
    {
        RefreshRoom();
        if (selectedProposal is {} proposalId && housing.EditProposals.FirstOrDefault(s => s.Batch.Id == proposalId) is {} proposal)
        {
            var pending = housing.StagedFurniture.FirstOrDefault(s => s.Proposal == proposalId);
            var importedPending = housing.StagedImported.FirstOrDefault(s => s.Proposal == proposalId);
            var deleted = pending?.Deleted == true || importedPending?.Deleted == true;
            var review = FurnitureInspectorUi.Review(pending != null ? housing.NameOf(pending.Item) : importedPending?.Item.Name ?? "Interior / scene change", proposal.Name, deleted, housing.CanReview);
            if (review == InspectAction.Approve) housing.Run(() => housing.ReviewProposal(proposalId, true));
            if (review == InspectAction.Reject) housing.Run(() => housing.ReviewProposal(proposalId, false));
            return;
        }
        if (ImportedSelection() is {} imported) { DrawImportedSelection(imported); return; }
        // Copies being shaped (in a row, a circle, mirrored) take the inspector until they're set down or cancelled.
        if (arranging != null) { if (housing.Arranging) { DrawArrangement(arranging); return; } arranging = null; }
        if (selection.Count > 1 && selected is Guid leader) { DrawGroupSelection(leader); return; }
        var item = housing.Placement?.Id == selected ? housing.Placement : housing.ActiveHouse?.Furnishings.FirstOrDefault(x => x.Id == selected);
        if (item == null) return;
        var entry = Catalogue.Get(item);
        ImGui.PushID(item.Id.ToString());
        var custom = housing.ShowsCustom(item);
        var detail = custom ? item.ItemId == 0 ? "Custom furniture · a prop" : "Custom furniture · built on " + entry.Name : item.BuildingPart != null ? entry.Category : entry.DyeSlots > 0 ? entry.Category + " · dyeable" : entry.Category;
        var action = FurnitureInspectorUi.Body(new(housing.NameOf(item), false, item.Translation, item.Rotation, new Vector3(item.Scale), editing && housing.CanFurnish, housing.IsPlacing,
                entry.Usage == 10, item.BuildingPart != null, detail, entry.Icon == 0 || custom ? null : IconTexture(entry.Icon), custom),
            () => DrawLiveTransforms(item.Translation, item.Rotation, new Vector3(item.Scale), false,
                (p, yaw, scale) => housing.PreviewFineFurniture(item with { Position = [p.X, p.Y, p.Z], Rotation = yaw, Scale = scale.X }),
                () => DrawTilt(item)),
            () => DrawAppearance(item, entry));
        switch (action)
        {
            case InspectAction.Move: BeginMove(); break;
            case InspectAction.Delete: housing.Run(() => housing.Remove(item.Id)); Select(null, null); inspectorOpen = false; break;
            case InspectAction.Duplicate: followCursor = false; axisDrag = null; movementInput.Reset(); housing.Run(() => { housing.BeginDuplicate(item with { Group = null }); Select(housing.Placement!.Id, null); }); break;
            case InspectAction.Music: ShowPanel(RoomPanel.Music); break;
        }
        DrawCopies([item.Id], item.Id);
        // Several pieces have no button of their own, so say how to get there.
        if (item.Group == null && editing && housing.CanFurnish && !housing.IsPlacing)
        {
            ImGui.Dummy(HavenTokens.Px(0, HavenTokens.Space1));
            HavenKit.Caption("Ctrl- or Shift-click other pieces to select them too, then move, align or link them as a group.");
        }
        // A piece picked out of its group with Alt-click.
        if (item.Group != null && editing && housing.CanFurnish && !housing.IsPlacing && housing.ActiveHouse is {} home)
        {
            ImGui.Dummy(HavenTokens.Px(0, HavenTokens.Space1));
            HavenKit.SectionTitle("Group", "one piece of a linked group");
            var half = (ImGui.GetContentRegionAvail().X - HavenTokens.Px(HavenTokens.Space2)) / 2;
            if (HavenKit.Button("Select the group", size: new(half, 0))) SelectPieces(FurnishingGroups.Members(home, item.Id), item.Id);
            ImGui.SameLine(0, HavenTokens.Px(HavenTokens.Space2));
            if (HavenKit.Button("Take out of group", size: new(half, 0))) { var one = new[] { item.Id }; housing.Run(() => housing.UnlinkGroup(one)); }
        }
        ImGui.PopID();
    }
    private void DrawGroupSelection(Guid leader)
    {
        var house = housing.ActiveHouse; if (house == null) return;
        var pieces = SelectedPieces;
        var groups = house.Furnishings.Where(f => selection.Contains(f.Id)).Select(f => f.Group).Distinct().ToArray();
        var linked = groups.Length == 1 && groups[0] != null && FurnishingGroups.Members(house, leader).Length == pieces.Length;
        var command = FurnitureInspectorUi.Group(new(pieces.Length, linked, editing && housing.CanFurnish, housing.IsPlacing));
        switch (command.Action)
        {
            case InspectAction.Move: BeginMove(); break;
            case InspectAction.Duplicate: followCursor = false; axisDrag = null; movementInput.Reset(); housing.Run(() => housing.BeginGroupDuplicate(pieces, leader)); break;
            case InspectAction.Delete: housing.Run(() => housing.RemoveMany(pieces)); Select(null, null); inspectorOpen = false; break;
            case InspectAction.Link: housing.Run(() => housing.LinkGroup(pieces)); break;
            case InspectAction.Unlink: housing.Run(() => housing.UnlinkGroup(pieces)); break;
            case InspectAction.Turn: housing.Run(() => housing.TurnSelection(pieces, command.Degrees)); break;
            case InspectAction.Align: housing.Run(() => housing.AlignSelection(pieces, command.Edge)); break;
            case InspectAction.Distribute: housing.Run(() => housing.DistributeSelection(pieces, command.Axis)); break;
        }
        DrawCopies(pieces, leader);
    }
    private void DrawAppearance(Furnishing item, FurnitureEntry entry)
    {
        if (item.BuildingPart != null)
        {
            if (entry.FinishPart is {} part)
            {
                var finishes = Catalogue.Fixtures.Where(x => x.Part == part).ToArray();
                var names = finishes.Select(x => x.Name).Prepend("Original surface").ToArray();
                var index = Array.FindIndex(finishes, x => x.FixtureId == item.SurfaceFinish) + 1;
                ImGui.BeginDisabled(!housing.BuildingFinishesAvailable);
                if (HavenKit.Select("##surface-finish", part == 0 ? "Wallpaper" : "Flooring", names, ref index))
                    housing.Run(() => housing.ChangeFurniture(item with { SurfaceFinish = index == 0 ? 0 : finishes[index - 1].FixtureId }));
                ImGui.EndDisabled();
                if (!housing.BuildingFinishesAvailable) HavenKit.Caption("Surface finishes are unavailable in this game build.");
            }
            // A world piece says where the game uses it, or why it isn't drawn here.
            else if (BuildingParts.IsWorldAsset(item.BuildingPart) && !Catalogue.CanBuild(item.BuildingPart))
                HavenKit.Caption(Catalogue.Prefab(item).Models.Length == 0
                    ? "This piece of the game's world isn't drawn here: it's still being read, or this game doesn't have it (a free trial has no Endwalker or Dawntrail pieces)."
                    : $"A piece of the game's world, from {entry.Description}, in its own materials.");
            else HavenKit.Caption("This piece uses its original housing materials.");
            DrawCollision(item);
            if (Catalogue.Prefab(item).Colliders.Length == 0) HavenKit.Caption("This asset has no solid collision.");
            if (entry.DyeSlots == 0) return;
        }
        else { DrawDisplay(item, entry); DrawCollision(item); DrawSeatSwitch(item); DrawDoorSwitch(item); DrawSoundVolume(item); }
        if (entry.DyeSlots == 0) HavenKit.Caption("Not dyeable");
        else DrawDye(item);
        DrawPoseSets(item);
    }
    // A seat's switch (a Broken Heart Chair's beating heart), and a lamp vanilla gives no click script, are chosen
    // here. It is the same switch a click flips, so it is shared with the gathering and remembered like one.
    private void DrawSeatSwitch(Furnishing item)
    {
        var lamp = housing.InspectorLamp(item);
        if (!(Catalogue.Prefab(item).Switch is { InspectorOnly: true } && housing.CanInteract(item) || lamp && housing.IsReady)) return;
        var on = housing.SwitchOn(item);
        if (HavenKit.Toggle((lamp ? "Lit" : "Switched on") + "##seat-switch", ref on)) housing.Run(() => housing.ToggleSwitch(item.Id));
    }
    // A hidden door in a furnishing (the Thick Bookshelf Partition) can be turned off: it stays shut and solid.
    private void DrawDoorSwitch(Furnishing item)
    {
        if (!housing.DoorToggle(item) || !housing.IsReady) return;
        var on = housing.SwitchOn(item);
        if (HavenKit.Toggle("Door opens##door-switch", ref on)) housing.Run(() => housing.ToggleSwitch(item.Id));
        HavenKit.Caption(on ? "It opens as someone walks up to it." : "It stays shut and solid.");
    }
    private void DrawSoundVolume(Furnishing item)
    {
        var prefab = Catalogue.Prefab(item);
        if (prefab.Sounds.Length == 0 && prefab.LoopSounds.Length == 0 && (prefab.Switch == null || prefab.SwitchSounds.Length == 0)) return;
        var value = liveSoundVolume is { } live && live.Item == item.Id ? live.Value : item.SoundVolume ?? 1;
        var drag = HavenKit.Slide("##sound-volume", "Sound volume", ref value, 0, 1, value <= 0 ? "Off" : $"{MathF.Round(value * 100):0}%");
        liveSoundVolume = drag.Active ? (item.Id, value) : null;
        if (!drag.Committed) return;
        var volume = MathF.Round(value * 100) / 100;
        housing.Run(() => housing.ChangeFurniture(item with { SoundVolume = volume >= 1 ? null : volume }));
    }
    // The current colour as a swatch and name; the full palette opens as a swatch grid.
    // A frame's painting or a partition's wallpaper, chosen like a dye: the current choice opens a picker.
    private string pictureSearch = "";
    private void DrawDisplay(Furnishing item, FurnitureEntry entry)
    {
        if (entry.Vase || entry.Canvas) { DrawFlowersOrPrint(item, entry); return; }
        if (entry.AquariumTier > 0) { DrawAquarium(item, entry); return; }
        if (entry.FinishPart == 0)
        {
            var walls = Catalogue.Fixtures.Where(x => x.Part == 0).ToArray();
            var names = walls.Select(x => x.Name).Prepend("Blank").ToArray();
            var index = Array.FindIndex(walls, x => x.FixtureId == item.SurfaceFinish) + 1;
            ImGui.BeginDisabled(!housing.BuildingFinishesAvailable);
            if (HavenKit.Select("##partition-wallpaper", "Wallpaper", names, ref index))
                housing.Run(() => housing.ChangeFurniture(item with { SurfaceFinish = index == 0 ? 0 : walls[index - 1].FixtureId }));
            ImGui.EndDisabled();
            return;
        }
        if (!entry.TakesPicture) return;
        var current = item.Picture == 0 ? null : Catalogue.Picture(item.Picture);
        ImGui.BeginDisabled(!housing.BuildingFinishesAvailable);
        HavenKit.Caption("Painting");
        var label = item.CustomPicture != null ? "Custom picture" : current?.Name ?? "Blank";
        if (HavenKit.Button(label + "##painting", ButtonKind.Secondary)) { pictureSearch = ""; RefreshCustomShown(item); ImGui.OpenPopup("##painting-picker"); }
        ImGui.EndDisabled();
        if (!housing.BuildingFinishesAvailable) HavenKit.Caption("Paintings are unavailable in this game build.");
        else DrawCustomPictureControls(item, entry);
        if (!HavenKit.BeginMenu("##painting-picker")) return;
        if (pictureSearch.Length == 0) DrawCustomPictureTiles(item);
        HavenKit.Search("##painting-search", ref pictureSearch, "Search paintings", HavenTokens.Px(PictureColumns * (PictureTile.X + 6)));
        var shown = Catalogue.Pictures.Where(x => pictureSearch.Length == 0 || x.Name.Contains(pictureSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        ImGui.BeginChild("##painting-grid", HavenTokens.Px(PictureColumns * (PictureTile.X + 6), 330));
        // Blank first, then every painting with its image; the game accepts any painting in any frame.
        for (var i = -1; i < shown.Length; i++)
        {
            var picture = i < 0 ? null : shown[i];
            if ((i + 1) % PictureColumns != 0) ImGui.SameLine(0, HavenTokens.Px(6));
            var size = HavenTokens.Px(PictureTile.X, PictureTile.Y);
            if (HavenKit.Card("##painting-" + (picture?.Id ?? 0), size, (picture?.Id ?? 0) == item.Picture && item.CustomPicture == null, out var min))
            {
                var chosen = picture?.Id ?? 0;
                housing.Run(() => housing.ChangeFurniture(item with { Picture = chosen, CustomPicture = null }));
                ImGui.CloseCurrentPopup();
            }
            var draw = ImGui.GetWindowDrawList(); var inset = HavenTokens.Px(4, 4);
            if (picture != null && textures.GetFromGameIcon(picture.Image).TryGetWrap(out var wrap, out _))
                draw.AddImageRounded(wrap!.Handle, min + inset, min + size - inset, Vector2.Zero, Vector2.One, HavenUi.U(Vector4.One), HavenTokens.Px(HavenTokens.RadiusField));
            else if (picture == null)
                draw.AddText(min + (size - ImGui.CalcTextSize("Blank")) / 2, HavenUi.U(HavenTokens.TextSecondary), "Blank");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(picture?.Name ?? "Blank frame");
        }
        if (shown.Length == 0) HavenKit.Caption("No painting matches.");
        ImGui.EndChild();
        ImGui.EndPopup();
    }
    private const int PictureColumns = 4;
    private static readonly Vector2 PictureTile = new(104, 72);

    private void DrawDye(Furnishing item)
    {
        var current = Catalogue.Dyes.FirstOrDefault(x => x.Id == item.StainId) ?? Catalogue.Dyes[0];
        // "Original colour" shows the furnishing's own default dye, which is what an undyed piece looks like.
        var original = Catalogue.DefaultDyeColor(item);
        Vector4 Colour(DyeEntry dye) => dye.Id == 0 && original is uint rgb ? DyeColor(dye with { Color = rgb }, true) : DyeColor(dye);
        HavenKit.Caption("Dye");
        // Centre the swatch on the name button's row.
        var top = ImGui.GetCursorPosY(); ImGui.SetCursorPosY(top + HavenTokens.Px((HavenTokens.ControlHeight - 22) / 2));
        if (HavenKit.Swatch("##dye-current", Colour(current), false, "Change dye")) ImGui.OpenPopup("##dye-palette");
        ImGui.SameLine(0, HavenTokens.Px(HavenTokens.Space2)); ImGui.SetCursorPosY(top);
        var label = item.StainId == 0 ? "Original colour" : current.Name;
        if (HavenKit.Button(label + "##dye-name", ButtonKind.Ghost)) ImGui.OpenPopup("##dye-palette");
        if (!HavenKit.BeginMenu("##dye-palette")) return;
        for (var i = 0; i < Catalogue.Dyes.Length; i++)
        {
            var dye = Catalogue.Dyes[i];
            if (i % 10 != 0) ImGui.SameLine(0, HavenTokens.Px(4));
            if (HavenKit.Swatch("##dye-" + dye.Id, Colour(dye), dye.Id == item.StainId, dye.Name))
            { housing.Run(() => housing.ChangeFurniture(item with { StainId = dye.Id })); ImGui.CloseCurrentPopup(); }
        }
        ImGui.EndPopup();
    }
    private static Vector4 DyeColor(DyeEntry dye, bool coloured = false) => dye.Id == 0 && !coloured ? HavenTokens.IconWell
        : new Vector4(((dye.Color >> 16) & 255) / 255f, ((dye.Color >> 8) & 255) / 255f, (dye.Color & 255) / 255f, 1);
    private void DrawCollision(Furnishing item)
    {
        var collision = item.Collision;
        if (HavenKit.Toggle("Solid##collision", ref collision)) housing.Run(() => housing.ChangeFurniture(item with { Collision = collision }));
    }
    private void DrawImportedSelection(ImportedObject item)
    {
        var definition = item.Definition;
        ImGui.PushID(definition.Key);
        var preview = housing.ImportedPreview(definition.Key);
        var position = preview?.Position ?? definition.Position; var rotation = preview?.Rotation ?? definition.Rotation; var scale = preview?.Scale ?? definition.Scale;
        var action = FurnitureInspectorUi.Body(new(item.Name, true, position, rotation.Y, scale, housing.CanFurnish && editing, housing.IsPlacing),
            () => DrawLiveTransforms(position, rotation.Y, scale, true,
                (p, yaw, s) => housing.PreviewFineImported(definition.Key, p, rotation with { Y = yaw }, s)), null);
        switch (action)
        {
            case InspectAction.Move: BeginMove(); break;
            case InspectAction.Delete: housing.Run(() => housing.RemoveImported(definition.Key)); Select(null, null); inspectorOpen = false; break;
        }
        if (definition.Path.Length > 1 && HavenKit.Button("Select group", ButtonKind.Secondary, new(-1, 0))) Select(null, definition.Key[..definition.Key.LastIndexOf('/')]);
        ImGui.PopID();
    }
    /// <summary>Cinematic mode (the toolbar's camera button while posing).</summary>
    public CinematicMode? Cinematic { get; set; }
    private bool estateWatched;
    public void DrawWorld()
    {
        if (!estateWatched) { estateWatched = true; engine.HousingEstateTeleportAsked += OnEstateTeleportAsked; }
        DrawEstateTeleport();
        DrawPropQuestion();
        DrawMaintenance();
        DrawPlacementWorld();
        DrawNpcNames();
    }

    private readonly record struct PickedObject(Guid? Id, string? ImportedKey, Guid? Proposal = null);
    // The hover outline picks every frame: while the pointer and camera hold still, the last answer stands for a moment.
    private (Vector3 Origin, Vector3 Direction, Furnishing[]? Furnishings, long At, PickedObject? Result) lastPick;
    private PickedObject? Pick(House house, Vector3 origin, Vector3 direction)
    {
        var now = Environment.TickCount64;
        if (origin == lastPick.Origin && direction == lastPick.Direction && ReferenceEquals(house.Furnishings, lastPick.Furnishings) && now - lastPick.At < 150)
            return lastPick.Result;
        var result = PickNow(house, origin, direction);
        lastPick = (origin, direction, house.Furnishings, now, result);
        return result;
    }
    private PickedObject? PickNow(House house, Vector3 origin, Vector3 direction)
    {
        var distance = float.MaxValue; PickedObject? result = null;
        // Moved pieces of the house are chosen in Remodel, not here.
        var moved = housing.MovedShellCopies;
        foreach (var item in house.Furnishings)
        {
            if (moved.Contains(item.Id)) continue;
            var prefab = Catalogue.Prefab(item);
            var on = prefab.Switch == null || housing.SwitchOn(item);
            foreach (var model in prefab.Models)
            {
                if (!PrefabState.Shown(model.Switch, on)) continue;
                var hit = ModelHit(model, housing.FurnitureModelTransform(item, model) * FurnitureCatalogue.ItemMatrix(item) * FurnitureCatalogue.HouseMatrix(house),
                    origin, direction, item.BuildingPart != null, distance);
                if (hit is float d && d < distance)
                { distance = d; result = housing.StagedFurniture.FirstOrDefault(s => s.Item.Id == item.Id) is {} pending ? new(null, null, pending.Proposal) : new(item.Id, null); }
            }
        }
        foreach (var pending in housing.StagedFurniture)
        foreach (var model in Catalogue.Prefab(pending.Item).Models)
        {
            var hit = ModelHit(model, model.Transform * FurnitureCatalogue.ItemMatrix(pending.Item) * FurnitureCatalogue.HouseMatrix(house),
                origin, direction, pending.Item.BuildingPart != null, distance);
            if (hit is float d && d < distance) { distance = d; result = new(null, null, pending.Proposal); }
        }
        foreach (var item in importedObjects)
        {
            if (housing.StagedImported.Any(s => s.Item.Definition.Key == item.Definition.Key)) continue;
            if (item.Definition.Disabled || item.Definition.Type == "Group") continue;
            var hit = PlacementMath.RayBox(origin, direction, item.Minimum, item.Maximum, item.Definition.Transform * FurnitureCatalogue.HouseMatrix(house));
            if (hit is float d && d < distance) { distance = d; result = new(null, item.Definition.Key); }
        }
        foreach (var pending in housing.StagedImported)
        {
            var item = pending.Item;
            var hit = PlacementMath.RayBox(origin, direction, item.Minimum, item.Maximum, item.Definition.Transform * FurnitureCatalogue.HouseMatrix(house));
            if (hit is float d && d < distance) { distance = d; result = new(null, null, pending.Proposal); }
        }
        return result;
    }
    /// <summary>
    /// How far along the ray a model's drawn triangles are, or null when the ray misses them (FurniturePickMeshes). The
    /// box around them is checked first, so models the ray can't reach (or only reaches beyond <paramref name="nearest"/>)
    /// are never tested triangle by triangle; until a model's triangles are read, its declared box stands in for them.
    /// </summary>
    private float? ModelHit(PrefabModel model, Matrix4x4 world, Vector3 origin, Vector3 direction, bool building, float nearest)
    {
        if (housing.PickMeshes.Get(model.Path) is not {} mesh)
            return PlacementMath.RayBox(origin, direction, model.Minimum, model.Maximum, world, exitWhenInside: building);
        if (mesh.Empty) return null;
        if (PlacementMath.RayBox(origin, direction, mesh.Minimum, mesh.Maximum, world) is not float near || near >= nearest) return null;
        return PlacementMath.RayTriangles(origin, direction, mesh.Positions, mesh.Triangles, world);
    }
    // The world's collision under the cursor ray, with the surface normal Dalamud's ScreenToWorld leaves out.
    private static (Vector3 Point, Vector3 Normal)? SurfaceHit(Vector3 origin, Vector3 direction)
    {
        if (!FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule.RaycastMaterialFilter(origin, direction, out var hit, 100)) return null;
        Vector3 point = hit.Point, normal = hit.Normal;
        return float.IsFinite(point.LengthSquared()) && float.IsFinite(normal.LengthSquared()) ? (point, normal) : null;
    }
    /// <summary>Where the camera looks: the ray through the middle of the screen.</summary>
    public static unsafe Vector3? CameraForward()
    {
        var manager = CameraManager.Instance(); if (manager == null) return null;
        var camera = manager->CurrentCamera; if (camera == null) return null;
        var middle = ImGui.GetMainViewport().Size / 2;
        var ray = camera->ScreenPointToRay(new FFXIVClientStructs.FFXIV.Common.Math.Vector2(middle.X, middle.Y));
        var direction = new Vector3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z);
        return direction.LengthSquared() > 0.000001f && float.IsFinite(direction.LengthSquared()) ? Vector3.Normalize(direction) : null;
    }

    private static unsafe bool TryRay(out Vector3 origin, out Vector3 direction)
    {
        origin = direction = default;
        var manager = CameraManager.Instance(); if (manager == null) return false;
        var camera = manager->CurrentCamera; if (camera == null) return false;
        var cursor = ImGui.GetMousePos() - ImGui.GetMainViewport().Pos;
        var ray = camera->ScreenPointToRay(new FFXIVClientStructs.FFXIV.Common.Math.Vector2(cursor.X, cursor.Y));
        origin = new(ray.Origin.X, ray.Origin.Y, ray.Origin.Z); direction = new(ray.Direction.X, ray.Direction.Y, ray.Direction.Z);
        if (direction.LengthSquared() < 0.000001f || !float.IsFinite(direction.LengthSquared())) return false;
        direction = Vector3.Normalize(direction);
        return true;
    }
}

