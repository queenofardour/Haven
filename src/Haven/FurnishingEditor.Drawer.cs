using Dalamud.Bindings.ImGui;
using Haven.Core;
using Haven.Core.Furniture;

namespace Haven;

// The Furnish drawer's data: filtering, Favourites and Recent, the room list, and its commands.
internal sealed partial class FurnishingEditor
{
    private string ListsPath => Path.Combine(housing.ConfigDirectory, "furnish-lists.json");
    private FurnishLists Lists => lists ??= FurnishLists.Load(ListsPath);
    private void SaveLists()
    {
        listsVersion++;
        try { Lists.Save(ListsPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private ImTextureID? IconTexture(ushort icon) =>
        icon != 0 && textures.GetFromGameIcon((uint)icon).TryGetWrap(out var wrap, out _) ? wrap!.Handle : null;

    private void DrawDrawer()
    {
        if (!editing || housing.ActiveHouse is not {} house) return;
        RefreshRoom();
        // Building pieces in every home: custom layouts are built from them, and remodelling replaces shell pieces with them.
        var custom = true;
        // The World source: the game's whole world, read from this PC's game data the first time it's opened.
        var world = housing.World;
        if (drawer.World) world.Start();
        var buildingReady = drawer.World ? world.Ready && Catalogue.BuildingsReady : !drawer.Building || Catalogue.BuildingsReady;
        var source = drawer.World ? buildingReady ? world.Entries : [] : drawer.Building ? buildingReady ? Catalogue.BuildingItems : [] : Catalogue.Items;
        string[] filters = [drawer.World ? FurnishDrawerUi.AllWorldFilter : FurnishDrawerUi.AllFilter, FurnishDrawerUi.RecentFilter, FurnishDrawerUi.FavouritesFilter,
            .. drawer.World ? buildingReady ? world.Categories : [] : drawer.Building ? buildingReady ? Catalogue.BuildingCategories : [] : Catalogue.Categories];
        drawer.Filter = Math.Clamp(drawer.Filter, 0, filters.Length - 1);
        if (drawer.World && worldZones.Length == 0 && buildingReady) worldZones = [WorldZonesAll, .. world.Zones];
        drawer.Zone = Math.Clamp(drawer.Zone, 0, Math.Max(0, worldZones.Length - 1));
        var placing = housing.Placement is {} p ? p.BuildingPart ?? p.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        var key = $"{drawer.Search}\n{drawer.Filter}\n{drawer.Building}\n{drawer.World}\n{drawer.Zone}\n{buildingReady}\n{listsVersion}\n{placing}";
        if (key != filterKey)
        {
            filterKey = key;
            var filter = filters[drawer.Filter];
            IEnumerable<FurnitureEntry> matches = filter switch
            {
                FurnishDrawerUi.RecentFilter => Lists.Recent.Select(k => source.FirstOrDefault(x => x.WidgetId == k)).OfType<FurnitureEntry>(),
                FurnishDrawerUi.FavouritesFilter => source.Where(x => Lists.IsFavourite(x.WidgetId)),
                FurnishDrawerUi.AllFilter or FurnishDrawerUi.AllWorldFilter => source,
                _ => source.Where(x => x.Category == filter),
            };
            if (drawer.World)
            {
                // A world piece answers to its name, its zone (the entry's description) or its file.
                var zone = drawer.Zone > 0 && drawer.Zone < worldZones.Length ? worldZones[drawer.Zone] : null;
                filtered = matches.Where(x => (zone == null || x.Description == zone) && (x.Name.Contains(drawer.Search, StringComparison.CurrentCultureIgnoreCase)
                    || x.Description.Contains(drawer.Search, StringComparison.CurrentCultureIgnoreCase) || x.BuildingPart!.Contains(drawer.Search, StringComparison.OrdinalIgnoreCase))).ToArray();
            }
            else filtered = matches.Where(x => x.Name.Contains(drawer.Search, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            drawerItems = filtered.Select((x, i) => new DrawerItem(i, x.Name, x.Icon, Lists.IsFavourite(x.WidgetId), x.WidgetId == placing, drawer.World ? x.BuildingPart : null)).ToArray();
        }
        if (drawerRoom.Length != roomRows.Length || drawerRoom.Any(r => r.Selected != IsSelected(roomRows[r.Index])))
            drawerRoom = roomRows.Select((r, i) => new DrawerRoomRow(i, r.Name, r.Detail, IsSelected(r))).ToArray();
        var notice = !housing.CanFurnish ? "Decorating access is required to place furnishings."
            : drawer.World && !buildingReady ? world.Status.Length > 0 ? world.Status : Catalogue.BuildingStatus
            : drawer.World ? "Pieces of the game's own world, from this game's data. Many are large and few are solid to walk on; guests see them when their game has them too."
            : drawer.Building && !buildingReady ? Catalogue.BuildingStatus : "";
        UpdateFurnitureImport();
        var yours = drawer.Tab == DrawerTab.Yours ? housing.Furniture.Library : [];
        var savedGroups = drawer.Tab == DrawerTab.Yours ? housing.SavedGroups : [];
        var model = new FurnishDrawerModel(filters, drawerItems, drawerRoom, housing.CanFurnish, custom, notice, IconTexture)
        {
            Yours = [.. yours.Select((f, i) => new DrawerCustom(i, f.Item.Name, f.Item.BaseItem == 0 ? "A prop made from a minion model" : "Built on " + (Catalogue.Find(f.Item.BaseItem)?.Name ?? "an unknown item"),
                Catalogue.Find(f.Item.BaseItem)?.Icon ?? 0, housing.Placement?.CustomFurniture == f.Hash))],
            Groups = [.. savedGroups.Select((g, i) => new DrawerGroup(i, g.Name, g.Pieces.Length == 1 ? "1 piece" : $"{g.Pieces.Length} pieces"))],
            ImportStatus = housing.FurnitureImportStatus,
            NeedsAgreement = housing.OwnsActiveHouse && !housing.AgreementAccepted,
            ImportChoice = furnitureChoice is { } choice ? (choice.ModName, [.. choice.Items.Select(i => i.Label)]) : null,
            ImportProps = furnitureChoice is { } props ? [.. props.Items.Select(i => i.PropModel != null)] : [],
            WarnProps = housing.WarnMinionProps,
            Zones = drawer.World ? worldZones : [],
            Thumbnail = drawer.World ? WorldThumbnail : null,
            ImportGroups = furnitureChoice is { } groups ? [.. groups.Groups.Select(g => new DrawerModGroup(g.Index, g.Group.Name, g.Group.Single,
                [.. g.Group.Options.Select(o => o.Name)], g.Group.Single ? g.Group.DefaultSettings : g.Group.Options.Count >= 64 ? ulong.MaxValue : (1UL << g.Group.Options.Count) - 1))] : [],
        };
        var command = FurnishDrawerUi.Draw(drawer, model, ref drawerOpen, SidePanelUi.Top);
        switch (command.Action)
        {
            case DrawerAction.PickCustom or DrawerAction.AgreeAndPickCustom when command.Index >= 0 && command.Index < yours.Length:
                var hash = yours[command.Index].Hash; var agree = command.Action == DrawerAction.AgreeAndPickCustom;
                followCursor = true; movementInput.Reset();
                housing.Run(() => { if (agree) housing.AcceptAgreement(); housing.BeginCustomFurniture(hash); Select(housing.Placement!.Id, null); });
                break;
            case DrawerAction.PickGroup when command.Index >= 0 && command.Index < savedGroups.Count:
                var groupId = savedGroups[command.Index].Id;
                followCursor = true; movementInput.Reset();
                housing.Run(() => { housing.BeginSavedGroup(groupId); Select(housing.Placement!.Id, null); });
                break;
            case DrawerAction.RenameGroup when command.Index < savedGroups.Count:
                var (groupRenamed, groupName) = (savedGroups[command.Index].Id, command.Text);
                housing.Run(() => housing.RenameSavedGroup(groupRenamed, groupName));
                break;
            case DrawerAction.DeleteGroup when command.Index < savedGroups.Count:
                var groupDeleted = savedGroups[command.Index].Id;
                housing.Run(() => housing.DeleteSavedGroup(groupDeleted));
                break;
            case DrawerAction.RenameCustom when command.Index < yours.Length:
                var (renamed, newName) = (yours[command.Index].Hash, command.Text);
                housing.Run(() => housing.RenameFurniture(renamed, newName));
                break;
            case DrawerAction.DeleteCustom when command.Index < yours.Length:
                var deleted = yours[command.Index].Hash;
                housing.Run(() => housing.DeleteFurniture(deleted));
                break;
            case DrawerAction.ImportFile: PickFurnitureMod?.Invoke(false, ReadMod); break;
            case DrawerAction.ImportFolder: PickFurnitureMod?.Invoke(true, ReadMod); break;
            case DrawerAction.CancelImport: furnitureChoice = null; break;
            case DrawerAction.Import when furnitureChoice is { } chosen && command.Index < chosen.Items.Count:
                var (modPath, target, furnitureName) = (chosen.Path, chosen.Items[command.Index], command.Text);
                var options = command.Options is { } picked ? new ModSelection(picked) : null;
                var hideWarning = command.HideWarning;
                furnitureChoice = null;
                housing.Run(() => { if (hideWarning) housing.WarnMinionProps = false; furnitureImport = housing.ImportFurniture(modPath, target, furnitureName, options); });
                break;
            case DrawerAction.Pick when command.Index < filtered.Length:
                var item = filtered[command.Index];
                followCursor = true; movementInput.Reset();
                housing.Run(() => { if (item.BuildingPart != null) housing.BeginBuilding(item.BuildingPart); else housing.BeginPlacement(item.ItemId); Select(housing.Placement!.Id, null); });
                Lists.Used(item.WidgetId); SaveLists();
                break;
            case DrawerAction.Favourite when command.Index < filtered.Length:
                Lists.ToggleFavourite(filtered[command.Index].WidgetId); SaveLists();
                break;
            case DrawerAction.SelectRoom when command.Index < roomRows.Length:
                // As in the room: a row selects its piece with its group, Alt just the piece, Ctrl adds or removes.
                var row = roomRows[command.Index];
                if (row.Id is Guid id)
                {
                    var io = ImGui.GetIO();
                    var members = io.KeyAlt ? [id] : FurnishingGroups.Members(house, id);
                    if (io.KeyCtrl || io.KeyShift) ToggleSelection(members, id); else SelectPieces(members, id);
                }
                else Select(null, row.ImportedKey);
                OpenInspector();
                break;
        }
    }
    // Custom furniture imports: the file picker (Settings' dialog; true for a folder), the mod being read, the item
    // and name waiting for the player, and the import running.
    public Action<bool, Action<string>>? PickFurnitureMod { get; set; }
    private Task<HousingController.FurnitureImportChoice>? furnitureRead;
    private HousingController.FurnitureImportChoice? furnitureChoice;
    private Task<string>? furnitureImport;
    private void ReadMod(string path) => housing.Run(() => furnitureRead = housing.ReadFurnitureMod(path));
    private void UpdateFurnitureImport()
    {
        if (furnitureRead is { IsCompleted: true } read)
        {
            furnitureRead = null;
            if (read.IsCompletedSuccessfully) furnitureChoice = read.Result;
            else housing.Run(() => throw new InvalidOperationException("That mod couldn't be read: " + read.Exception!.GetBaseException().Message, read.Exception));
        }
        if (furnitureImport is { IsCompleted: true } import)
        {
            furnitureImport = null;
            if (!import.IsCompletedSuccessfully) housing.Run(() => throw new InvalidOperationException("That furnishing couldn't be imported: " + import.Exception!.GetBaseException().Message, import.Exception));
        }
    }

    private const string WorldZonesAll = "All zones";
    private string[] worldZones = [];
    // A world piece's thumbnail as a texture once it's drawn and kept (Dalamud loads the PNG and lets it go when unused).
    private ImTextureID? WorldThumbnail(string path) =>
        housing.World.Thumbnail(path) is { } file && textures.GetFromFile(file).TryGetWrap(out var wrap, out _) ? wrap!.Handle : null;

    private bool IsSelected(RoomRow row) => row.Id is Guid id ? selection.Contains(id) : selectedImported == row.ImportedKey;
}
