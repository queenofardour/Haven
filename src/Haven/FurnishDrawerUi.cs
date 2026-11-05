using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

internal enum DrawerTab { Catalogue, Room, Yours }
// Thumbnail: a world piece's path, whose drawn thumbnail stands in for an icon.
internal readonly record struct DrawerItem(int Index, string Name, ushort Icon, bool Favourite, bool Selected, string? Thumbnail = null);
internal readonly record struct DrawerRoomRow(int Index, string Name, string Detail, bool Selected);
// One of the player's custom furnishings: its name, the item it is built on, that item's icon.
internal readonly record struct DrawerCustom(int Index, string Name, string Detail, ushort Icon, bool Selected);
// One of the player's saved groups (SavedGroups): its name and how many pieces.
internal readonly record struct DrawerGroup(int Index, string Name, string Detail);
// A mod group shown when importing: one choice (Single) or any number of add-ons (Multi), with the preselected value.
internal sealed record DrawerModGroup(int Index, string Name, bool Single, IReadOnlyList<string> Options, ulong Preselected);
internal sealed record FurnishDrawerModel(IReadOnlyList<string> Filters, IReadOnlyList<DrawerItem> Items, IReadOnlyList<DrawerRoomRow> Room,
    bool CanPlace, bool CustomHome, string Notice, Func<ushort, ImTextureID?> Icon)
{
    public IReadOnlyList<DrawerCustom> Yours { get; init; } = [];
    public IReadOnlyList<DrawerGroup> Groups { get; init; } = [];
    /// <summary>A mod being read or imported, and a mod read and waiting for the player's choice of item and name.</summary>
    public string? ImportStatus { get; init; }
    public (string ModName, IReadOnlyList<string> Items)? ImportChoice { get; init; }
    /// <summary>The mod's groups that change furniture: name, single choice or add-ons, option names, defaults.</summary>
    public IReadOnlyList<DrawerModGroup> ImportGroups { get; init; } = [];
    /// <summary>The content agreement is needed before the owner's own furniture can go to guests.</summary>
    public bool NeedsAgreement { get; init; }
    /// <summary>Which of the import's items are minion props, and whether importing one warns first.</summary>
    public IReadOnlyList<bool> ImportProps { get; init; } = [];
    public bool WarnProps { get; init; }
    /// <summary>The World source's zones ("All zones" first) and its thumbnails, by piece path (null while drawn or for none).</summary>
    public IReadOnlyList<string> Zones { get; init; } = [];
    public Func<string, ImTextureID?>? Thumbnail { get; init; }
}
internal enum DrawerAction { None, Pick, Favourite, SelectRoom, Close, PickCustom, ImportFile, ImportFolder, Import, CancelImport, AgreeAndPickCustom, RenameCustom, DeleteCustom,
    // A saved group: Index is the group; RenameGroup's Text the new name.
    PickGroup, RenameGroup, DeleteGroup }
internal readonly record struct DrawerCommand(DrawerAction Action, int Index = -1, string Text = "")
{
    /// <summary>The options chosen for an import, by group index (Single: the option; Multi: a bit mask).</summary>
    public IReadOnlyDictionary<int, ulong>? Options { get; init; }
    /// <summary>The player ticked "Don't show this again" on the minion prop warning.</summary>
    public bool HideWarning { get; init; }
}

// What the drawer shows: its tab, search texts and filter. The editor filters with it before each draw.
internal sealed class FurnishDrawerState
{
    public DrawerTab Tab;
    public string Search = "", RoomSearch = "";
    public int Filter;
    public bool Building;
    // The World source: every model the game places in its world, filtered by zone too.
    public bool World;
    public int Zone;
    public int ImportItem;          // the item chosen in the import dialog
    public Dictionary<int, ulong> ImportOptions = [];
    public string ImportName = "";
    public bool ImportShown;
    // The minion prop warning (its own dialog, opened from the import dialog's Import), its checkbox, and returning
    // from it to the import dialog with the choices kept.
    public bool PropWarning, WarningShown, HideWarning, BackToImport;
    public int AgreementFor = -1;   // the piece to place once the agreement is accepted
    public bool AgreementShown;
    // A piece being renamed or deleted from its right-click menu, and the dialog for it.
    public int Managing = -1;
    public bool Deleting, ManageShown;
    public string ManageName = "";
    // A saved group being renamed or deleted.
    public int ManagingGroup = -1;
    public bool DeletingGroup, GroupShown;
    public string GroupName = "";
}

// The Furnish drawer: an in-room panel with the catalogue as an icon grid, filters
// (All, Recent, Favourites, each category), search and a star per item, plus the objects already in this room.
// It stays open while placing. Grids draw only their visible rows, so the full catalogue costs a screenful.
internal static class FurnishDrawerUi
{
    public const string AllFilter = "All furnishings", AllWorldFilter = "All pieces", RecentFilter = "Recent", FavouritesFilter = "Favourites";
    public static UiBounds Bounds => SidePanelUi.Bounds;
    // For the standalone preview's click checks.
    public static (Vector2 Min, Vector2 Max) FirstTileBounds { get; private set; }
    public static (Vector2 Min, Vector2 Max) FirstStarBounds { get; private set; }

    public static DrawerCommand Draw(FurnishDrawerState state, FurnishDrawerModel model, ref bool open, float top)
    {
        var command = default(DrawerCommand);
        if (SidePanelUi.Begin("Furnish", "Haven.FurnishDrawer", ref open, top))
        {
            var room = model.Room.Count;
            var tab = (int)state.Tab;
            if (HavenKit.Segmented("drawer-tab", ["Catalogue", $"In this room · {room}", "Yours"], ref tab)) state.Tab = (DrawerTab)tab;
            ImGui.Dummy(Px(0, Space1));
            command = state.Tab switch { DrawerTab.Catalogue => Catalogue(state, model), DrawerTab.Room => Room(state, model), _ => Yours(state, model) };
            if (ImportDialog(state, model) is { Action: not DrawerAction.None } import) command = import;
        }
        SidePanelUi.End(open);
        return command;
    }

    // The player's custom furniture, made from furniture mods: import one, then click a piece to place it.
    private static DrawerCommand Yours(FurnishDrawerState state, FurnishDrawerModel model)
    {
        var command = default(DrawerCommand);
        var width = ImGui.GetContentRegionAvail().X; var half = (width - Px(Space2)) / 2;
        ImGui.BeginDisabled(model.ImportStatus != null || !model.CanPlace);
        if (HavenKit.Button("Import a .pmp…", ButtonKind.Primary, new(half, 0))) command = new(DrawerAction.ImportFile);
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("From a mod folder…", ButtonKind.Secondary, new(half, 0))) command = new(DrawerAction.ImportFolder);
        ImGui.EndDisabled();
        HavenKit.Caption(model.ImportStatus ?? "A Penumbra furniture or minion prop mod becomes new furniture of your own, beside the original. A furniture mod's piece behaves like the item it replaced (seats, lights, collision); a minion prop is solid but has no seat.");
        ImGui.Dummy(Px(0, Space1));
        HavenKit.SectionTitle("Your furniture", $"{model.Yours.Count}");
        if (ImGui.BeginChild("##drawer-yours", Vector2.Zero, false))
        {
            if (model.Yours.Count == 0) { ImGui.Dummy(Px(0, Space4)); HavenKit.EmptyState(HavenIcon.Plus, "No furniture yet", "Import a furniture mod to place it in your homes."); }
            ImGui.BeginDisabled(!model.CanPlace);
            foreach (var item in model.Yours)
            {
                if (HavenKit.ListRow("##custom-" + item.Index, item.Name, item.Detail, item.Selected, HavenIcon.Custom))
                {
                    if (model.NeedsAgreement) state.AgreementFor = item.Index;
                    else command = new(DrawerAction.PickCustom, item.Index);
                }
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.OpenPopup("##custom-menu-" + item.Index);
                if (HavenKit.BeginMenu("##custom-menu-" + item.Index))
                {
                    if (HavenKit.MenuItem("Rename…")) { state.Managing = item.Index; state.Deleting = false; state.ManageName = item.Name; }
                    if (HavenKit.MenuItem("Delete…", danger: true)) { state.Managing = item.Index; state.Deleting = true; }
                    ImGui.EndPopup();
                }
            }
            ImGui.EndDisabled();
            if (model.Yours.Count > 0) { ImGui.Dummy(Px(0, Space1)); HavenKit.Caption("Right-click a piece to rename or delete it."); }
            ImGui.Dummy(Px(0, Space3));
            HavenKit.SectionTitle("Your groups", $"{model.Groups.Count}");
            if (model.Groups.Count == 0) HavenKit.Caption("Select a few pieces (a dressed table, a bar corner) and choose Keep in Your groups in the inspector, to drop them into any home.");
            ImGui.BeginDisabled(!model.CanPlace);
            foreach (var group in model.Groups)
            {
                if (HavenKit.ListRow("##group-" + group.Index, group.Name, group.Detail, false, HavenIcon.Grid)) command = new(DrawerAction.PickGroup, group.Index);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.OpenPopup("##group-menu-" + group.Index);
                if (HavenKit.BeginMenu("##group-menu-" + group.Index))
                {
                    if (HavenKit.MenuItem("Rename…")) { state.ManagingGroup = group.Index; state.DeletingGroup = false; state.GroupName = group.Name; }
                    if (HavenKit.MenuItem("Delete…", danger: true)) { state.ManagingGroup = group.Index; state.DeletingGroup = true; }
                    ImGui.EndPopup();
                }
            }
            ImGui.EndDisabled();
            if (model.Groups.Count > 0) { ImGui.Dummy(Px(0, Space1)); HavenKit.Caption("Click a group to place it; right-click to rename or delete it."); }
        }
        ImGui.EndChild();
        if (ManageDialog(state, model) is { Action: not DrawerAction.None } managed) command = managed;
        if (GroupDialog(state, model) is { Action: not DrawerAction.None } managedGroup) command = managedGroup;
        // Opened here, outside the list, so the dialog and its opening share an id.
        if (state.AgreementFor >= 0 && !state.AgreementShown) { ImGui.OpenPopup("##furniture-agreement"); state.AgreementShown = true; }
        if (!HavenKit.BeginDialog("##furniture-agreement", "Before you place your own furniture", 520))
        {
            // Closed by a click outside it: nothing is placed.
            if (state.AgreementShown) { state.AgreementShown = false; state.AgreementFor = -1; }
        }
        else
        {
            HavenKit.Caption("Haven keeps the pictures, music and furniture you upload and shares them with everyone who visits your homes and agrees to load custom content. You are responsible for what you upload.");
            HavenKit.Caption("•  Upload only what you have the right to share; credit the mod's makers where they ask.");
            HavenKit.Caption("•  Never upload anything involving minors, or anything illegal.");
            HavenKit.Caption("•  Haven may remove anything uploaded, and stop uploads from anyone who breaks these rules.");
            HavenKit.Caption("What your homes stop using is deleted after 30 days.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("I agree") + Px(Space2));
            if (HavenKit.Button("Cancel##furniture-agreement", ButtonKind.Ghost)) { state.AgreementFor = -1; ImGui.CloseCurrentPopup(); }
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("I agree##furniture-agreement", ButtonKind.Primary)) { command = new(DrawerAction.AgreeAndPickCustom, state.AgreementFor); state.AgreementFor = -1; ImGui.CloseCurrentPopup(); }
            HavenKit.EndDialog();
        }
        return command;
    }

    // Renaming or deleting one of the player's saved groups.
    private static DrawerCommand GroupDialog(FurnishDrawerState state, FurnishDrawerModel model)
    {
        var command = default(DrawerCommand);
        if (state.ManagingGroup < 0 || state.ManagingGroup >= model.Groups.Count) { state.ManagingGroup = -1; state.GroupShown = false; return command; }
        var group = model.Groups[state.ManagingGroup];
        if (!state.GroupShown) { ImGui.OpenPopup("##group-manage"); state.GroupShown = true; }
        if (!HavenKit.BeginDialog("##group-manage", state.DeletingGroup ? "Delete this group?" : "Rename this group", 440)) { state.ManagingGroup = -1; state.GroupShown = false; return command; }
        if (state.DeletingGroup)
        {
            HavenKit.Caption($"{group.Name} leaves Your groups. Pieces already placed in your homes stay.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Delete") + Px(Space2));
            if (HavenKit.Button("Keep##group-manage", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Delete##group-manage", ButtonKind.Danger)) { command = new(DrawerAction.DeleteGroup, group.Index); ImGui.CloseCurrentPopup(); }
        }
        else
        {
            var submit = HavenKit.SubmitField("##group-manage-name", "Name", ref state.GroupName, "Group name", maxLength: Haven.Core.SavedGroups.MaxName);
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere(-1);
            var valid = !string.IsNullOrWhiteSpace(state.GroupName);
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Save") + Px(Space2));
            if (HavenKit.Button("Cancel##group-manage", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2)); ImGui.BeginDisabled(!valid);
            if ((HavenKit.Button("Save##group-manage", ButtonKind.Primary) || submit) && valid)
            { command = new(DrawerAction.RenameGroup, group.Index, state.GroupName.Trim()); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
        }
        HavenKit.EndDialog();
        return command;
    }

    // Renaming or deleting one of the player's pieces, from its right-click menu.
    private static DrawerCommand ManageDialog(FurnishDrawerState state, FurnishDrawerModel model)
    {
        var command = default(DrawerCommand);
        if (state.Managing < 0 || state.Managing >= model.Yours.Count) { state.Managing = -1; state.ManageShown = false; return command; }
        var item = model.Yours[state.Managing];
        if (!state.ManageShown) { ImGui.OpenPopup("##custom-manage"); state.ManageShown = true; }
        if (!HavenKit.BeginDialog("##custom-manage", state.Deleting ? "Delete this furnishing?" : "Rename this furnishing", 440)) { state.Managing = -1; state.ManageShown = false; return command; }
        if (state.Deleting)
        {
            HavenKit.Caption($"{item.Name} is deleted from this PC and from Haven's server, and every copy placed in your homes is removed too. The mod it came from is not touched.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Delete") + Px(Space2));
            if (HavenKit.Button("Keep##custom-manage", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Delete##custom-manage", ButtonKind.Danger)) { command = new(DrawerAction.DeleteCustom, item.Index); ImGui.CloseCurrentPopup(); }
        }
        else
        {
            var name = state.ManageName;
            var submitted = HavenKit.SubmitField("##custom-manage-name", "Name", ref name, "Furnishing name", maxLength: 60);
            state.ManageName = name;
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Rename") + Px(Space2));
            if (HavenKit.Button("Cancel##custom-manage", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2));
            if ((HavenKit.Button("Rename##custom-manage", ButtonKind.Primary) || submitted) && name.Trim().Length > 0)
            { command = new(DrawerAction.RenameCustom, item.Index, name.Trim()); ImGui.CloseCurrentPopup(); }
        }
        HavenKit.EndDialog();
        return command;
    }

    // After a mod is read: which item it changes to import (when it changes several), and the new furnishing's name.
    private static DrawerCommand ImportDialog(FurnishDrawerState state, FurnishDrawerModel model)
    {
        var command = default(DrawerCommand);
        if (model.ImportChoice is not var (modName, items)) { state.ImportShown = false; state.PropWarning = state.WarningShown = false; return command; }
        if (!state.ImportShown)
        {
            ImGui.OpenPopup("##furniture-import"); state.ImportShown = true; state.ImportItem = 0; state.ImportName = modName;
            state.ImportOptions = model.ImportGroups.ToDictionary(g => g.Index, g => g.Preselected);
            state.PropWarning = state.WarningShown = state.HideWarning = state.BackToImport = false;
        }
        if (state.PropWarning) return PropWarning(state, items);
        if (state.BackToImport) { ImGui.OpenPopup("##furniture-import"); state.BackToImport = false; }
        if (!HavenKit.BeginDialog("##furniture-import", "Import furniture", 460)) { state.ImportShown = false; return new(DrawerAction.CancelImport); }
        HavenKit.Caption(items.Count == 1 ? $"From {modName}: {items[0]}. It becomes a new furnishing of your own; the original stays as it was."
            : $"{modName} gives {items.Count} pieces. Choose the one to import; you can import the others afterwards.");
        if (model.ImportGroups.Count > 0) HavenKit.Caption("Choose the mod's options for this piece. Import again with other options to make another piece.");
        if (items.Count > 1)
        {
            var chosen = Math.Clamp(state.ImportItem, 0, items.Count - 1);
            if (HavenKit.Select("##furniture-import-item", "Built on", items, ref chosen)) state.ImportItem = chosen;
        }
        // The mod's options: one choice per Single group, toggles for a Multi group's add-ons.
        foreach (var group in model.ImportGroups)
        {
            ImGui.PushID("import-group-" + group.Index);
            var value = state.ImportOptions.GetValueOrDefault(group.Index, group.Preselected);
            var label = group.Name.Length > 0 ? group.Name : "Options";
            if (group.Single)
            {
                var chosen = (int)Math.Min(value, (ulong)Math.Max(0, group.Options.Count - 1));
                if (HavenKit.Select("##option", label, group.Options, ref chosen)) state.ImportOptions[group.Index] = (ulong)chosen;
            }
            else
            {
                HavenKit.Caption(label);
                for (var i = 0; i < group.Options.Count && i < 64; i++)
                {
                    var on = (value >> i & 1) != 0;
                    if (HavenKit.Toggle(group.Options[i].Length > 0 ? group.Options[i] + "##option-" + i : $"Option {i + 1}##option-{i}", ref on))
                        state.ImportOptions[group.Index] = on ? value | 1UL << i : value & ~(1UL << i);
                }
            }
            ImGui.PopID();
        }
        var name = state.ImportName;
        var submitted = HavenKit.SubmitField("##furniture-import-name", "Name", ref name, "Furnishing name", maxLength: 60);
        state.ImportName = name;
        HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Import") + Px(Space2));
        if (HavenKit.Button("Cancel##furniture-import", ButtonKind.Ghost)) { command = new(DrawerAction.CancelImport); ImGui.CloseCurrentPopup(); }
        ImGui.SameLine(0, Px(Space2));
        if ((HavenKit.Button("Import##furniture-import", ButtonKind.Primary) || submitted) && name.Trim().Length > 0)
        {
            var item = Math.Clamp(state.ImportItem, 0, items.Count - 1);
            // A minion prop warns first, in this same dialog, unless the player turned the warning off.
            if (model.WarnProps && item < model.ImportProps.Count && model.ImportProps[item]) state.PropWarning = true;
            else command = new(DrawerAction.Import, item, name.Trim()) { Options = new Dictionary<int, ulong>(state.ImportOptions) };
            ImGui.CloseCurrentPopup();
        }
        HavenKit.EndDialog();
        return command;
    }

    // The warning before a minion prop is imported: the conversion is experimental. A dialog of its own, so it sizes to
    // its text; Import anyway goes ahead with the choices made in the import dialog, Back returns to them, and a click
    // outside cancels the import.
    private static DrawerCommand PropWarning(FurnishDrawerState state, IReadOnlyList<string> items)
    {
        var command = default(DrawerCommand);
        if (!state.WarningShown) { ImGui.OpenPopup("##prop-warning"); state.WarningShown = true; }
        if (!HavenKit.BeginDialog("##prop-warning", "Minion props are experimental", 460))
        {
            state.PropWarning = state.WarningShown = false;
            if (state.BackToImport) return command;
            state.ImportShown = false;
            return new(DrawerAction.CancelImport);
        }
        HavenKit.Caption("This piece comes from a mod that replaces a minion. Haven turns the minion's model into furniture, and that conversion is still experimental: the result may look wrong, behave oddly, or in rare cases crash the game.");
        HavenKit.Caption("We're working to improve it. If a converted piece comes out malformed, or causes a crash or other problems, please let Haven's developer know, with screenshots and a copy of the mod. Thank you!");
        ImGui.Dummy(Px(0, Space1));
        HavenKit.Toggle("Don't show this warning again", ref state.HideWarning);
        if (state.HideWarning) HavenKit.Caption("You can turn it back on in Settings → Custom content → Furniture.");
        HavenKit.AlignRight(HavenKit.ButtonWidth("Back") + HavenKit.ButtonWidth("Import anyway") + Px(Space2));
        if (HavenKit.Button("Back##prop-warning", ButtonKind.Ghost)) { state.BackToImport = true; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("Import anyway##prop-warning", ButtonKind.Primary))
        {
            command = new(DrawerAction.Import, Math.Clamp(state.ImportItem, 0, items.Count - 1), state.ImportName.Trim())
            { Options = new Dictionary<int, ulong>(state.ImportOptions), HideWarning = state.HideWarning };
            state.PropWarning = state.WarningShown = false;
            ImGui.CloseCurrentPopup();
        }
        HavenKit.EndDialog();
        return command;
    }

    private static DrawerCommand Catalogue(FurnishDrawerState state, FurnishDrawerModel model)
    {
        var command = default(DrawerCommand);
        if (model.CustomHome)
        {
            var source = state.World ? 2 : state.Building ? 1 : 0;
            if (HavenKit.Segmented("drawer-source", ["Furnishings", "Building", "World"], ref source)) { state.Building = source == 1; state.World = source == 2; state.Filter = 0; state.Zone = 0; }
        }
        HavenKit.Search("##drawer-search", ref state.Search, state.World ? "Search the world: a name, a zone or a file" : state.Building ? "Search building pieces or district" : "Search furnishings");
        var filter = Math.Clamp(state.Filter, 0, Math.Max(0, model.Filters.Count - 1));
        if (state.World && model.Zones.Count > 0)
        {
            // Two pickers side by side: what kind of piece, and which zone placed it.
            var half = (ImGui.GetContentRegionAvail().X - Px(Space2)) / 2;
            if (HavenKit.Select("##drawer-filter", "", model.Filters, ref filter, half)) state.Filter = filter;
            ImGui.SameLine(0, Px(Space2));
            var zone = Math.Clamp(state.Zone, 0, model.Zones.Count - 1);
            if (HavenKit.Select("##drawer-zone", "", model.Zones, ref zone, half)) state.Zone = zone;
            state.Zone = zone;
        }
        else if (HavenKit.Select("##drawer-filter", "", model.Filters, ref filter)) state.Filter = filter;
        state.Filter = filter;
        if (model.Notice.Length > 0) HavenKit.Caption(model.Notice);
        HavenKit.SectionTitle(model.Filters.Count > 0 ? model.Filters[filter] : "", model.Items.Count == 1 ? "1 item" : $"{model.Items.Count} items");

        if (ImGui.BeginChild("##drawer-grid", Vector2.Zero, false))
        {
            if (model.Items.Count == 0)
            {
                ImGui.Dummy(Px(0, Space5));
                var favourites = model.Filters.Count > filter && model.Filters[filter] == FavouritesFilter;
                HavenKit.EmptyState(favourites ? HavenIcon.Star : HavenIcon.Search, favourites ? "No favourites yet" : "Nothing found",
                    favourites ? "Star an item to keep it here." : "Try another name or category.");
            }
            else command = Grid(model);
        }
        ImGui.EndChild();
        return command;
    }

    private static DrawerCommand Grid(FurnishDrawerModel model)
    {
        var command = default(DrawerCommand);
        var columns = 3; var gap = Px(Space2); var width = ImGui.GetContentRegionAvail().X;
        // World pieces have no game icon, only their drawn thumbnail, which needs more room to be read.
        var side = model.Thumbnail != null ? 84 : 52;
        var tile = new Vector2((width - gap * (columns - 1)) / columns, Px(Space2 + side + 6 + 34 + Space2));
        var rowHeight = tile.Y + gap; var rows = (model.Items.Count + columns - 1) / columns;
        var first = Math.Max(0, (int)(ImGui.GetScrollY() / rowHeight)); var last = Math.Min(rows, first + (int)(ImGui.GetWindowHeight() / rowHeight) + 2);
        var origin = ImGui.GetCursorScreenPos() - new Vector2(0, ImGui.GetScrollY()); var top = ImGui.GetCursorPos();
        ImGui.BeginDisabled(!model.CanPlace);
        for (var row = first; row < last; row++)
        for (var column = 0; column < columns; column++)
        {
            var i = row * columns + column; if (i >= model.Items.Count) break;
            var item = model.Items[i];
            ImGui.SetCursorPos(top + new Vector2(column * (tile.X + gap), row * rowHeight));
            ImGui.PushID(item.Index);
            if (HavenKit.Card("##tile", tile, item.Selected, out var min)) command = new(DrawerAction.Pick, item.Index);
            ImGui.SetItemAllowOverlap();
            var hovered = ImGui.IsItemHovered();
            if (hovered) HavenKit.Tooltip(item.Name);
            if (i == 0) FirstTileBounds = (min, min + tile);
            var draw = ImGui.GetWindowDrawList(); var icon = new Vector2(Px(side)); var at = min + new Vector2((tile.X - icon.X) / 2, Px(Space2));
            if (item.Thumbnail != null)
            {
                // A world piece: its thumbnail on the plain well (transparent around the piece); the well alone while
                // it's drawn, or when the piece draws nothing.
                draw.AddRectFilled(at, at + icon, HavenUi.U(IconWell), Px(RadiusField));
                if (model.Thumbnail?.Invoke(item.Thumbnail) is { } thumbnail) draw.AddImageRounded(thumbnail, at, at + icon, Vector2.Zero, Vector2.One, HavenUi.U(Vector4.One), Px(RadiusField));
            }
            else if (model.Icon(item.Icon) is { } texture) draw.AddImageRounded(texture, at, at + icon, Vector2.Zero, Vector2.One, HavenUi.U(Vector4.One), Px(RadiusField));
            else draw.AddRectFilled(at, at + icon, HavenUi.U(IconWell), Px(RadiusField));
            using (HavenFonts.Use(HavenText.Caption))
            {
                var text = new Vector2(min.X + Px(6), at.Y + icon.Y + Px(6)); var wrap = tile.X - Px(12);
                draw.PushClipRect(text, new Vector2(min.X + tile.X - Px(6), text.Y + ImGui.GetTextLineHeight() * 2), true);
                draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), text, HavenUi.U(TextOnRaised), item.Name, wrap);
                draw.PopClipRect();
            }
            // The star sits over the tile's corner; a favourite always shows it, others on hover.
            var star = new Vector2(Px(24));
            ImGui.SetCursorScreenPos(min + new Vector2(tile.X - star.X - Px(4), Px(4)));
            if (HavenUi.Surface("##star", star)) command = new(DrawerAction.Favourite, item.Index);
            var starHovered = ImGui.IsItemHovered();
            if (i == 0) FirstStarBounds = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            if (item.Favourite || hovered || starHovered)
                HavenUi.DrawStar(draw, min + new Vector2(tile.X - star.X / 2 - Px(4), Px(4) + star.Y / 2), Px(15), item.Favourite ? Warning : starHovered ? Text : TextTertiary, item.Favourite);
            if (starHovered) HavenKit.Tooltip(item.Favourite ? "Remove from Favourites" : "Add to Favourites");
            ImGui.PopID();
        }
        ImGui.EndDisabled();
        // Reserve the full grid height so the scrollbar spans every row, drawn or not.
        ImGui.SetCursorPos(top + new Vector2(0, rows * rowHeight)); ImGui.Dummy(Vector2.Zero);
        return command;
    }

    private static DrawerCommand Room(FurnishDrawerState state, FurnishDrawerModel model)
    {
        var command = default(DrawerCommand);
        HavenKit.Search("##drawer-room-search", ref state.RoomSearch, "Find an object in this room");
        var rows = model.Room.Where(x => state.RoomSearch.Length == 0 || x.Name.Contains(state.RoomSearch, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        if (ImGui.BeginChild("##drawer-room", Vector2.Zero, false))
        {
            if (rows.Length == 0)
            {
                ImGui.Dummy(Px(0, Space5));
                HavenKit.EmptyState(HavenIcon.Home, model.Room.Count == 0 ? "Nothing here yet" : "Nothing found",
                    model.Room.Count == 0 ? "Pick something from the catalogue to place it." : "Try another name.");
            }
            else
            {
                var height = Px(58) + ImGui.GetStyle().ItemSpacing.Y;
                var first = Math.Max(0, (int)(ImGui.GetScrollY() / height)); var last = Math.Min(rows.Length, first + (int)(ImGui.GetWindowHeight() / height) + 2);
                var top = ImGui.GetCursorPos();
                for (var i = first; i < last; i++)
                {
                    ImGui.SetCursorPos(top + new Vector2(0, i * height));
                    if (HavenKit.ListRow("##room-" + rows[i].Index, rows[i].Name, rows[i].Detail, rows[i].Selected)) command = new(DrawerAction.SelectRoom, rows[i].Index);
                }
                ImGui.SetCursorPos(top + new Vector2(0, rows.Length * height)); ImGui.Dummy(Vector2.Zero);
            }
        }
        ImGui.EndChild();
        return command;
    }
}
