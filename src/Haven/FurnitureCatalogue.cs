using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;
using Haven.Core;
using Stagehand.Definitions;
using Stagehand.Definitions.Objects;

namespace Haven;

// EffectList: the same effects as Effects' stage, for Haven's own effects (FurnitureEffects) when they're on.
internal sealed record AmbientScene(string? Effects, IReadOnlyDictionary<string, string> Sounds)
{
    public IReadOnlyList<AmbientEmitter> EffectList { get; init; } = [];
    // SoundList: the same sounds as Sounds' stages (budgeted, keyed alike), for Haven's own sounds (FurnitureSounds).
    public IReadOnlyList<AmbientEmitter> SoundList { get; init; } = [];
}

internal sealed class FurnitureCatalogue
{
    private readonly FurniturePrefabReader prefabs;
    private readonly Dictionary<uint, FurnitureEntry> byItem;
    private readonly Dictionary<ushort, FurnitureEntry> byModel;
    private readonly Dictionary<uint, DyeEntry> byDye;
    // Every dye's colour, including ones housing does not offer, which some furnishings use as their default.
    private readonly Dictionary<uint, uint> stainColors;
    private readonly HashSet<uint> orchestrionIds;
    private readonly HashSet<uint> mainHallTerritories;
    private readonly Dictionary<uint, string> interiorShellCodes, interiorPlaceNames;
    private readonly Task<BuildingCatalogue> buildings;
    private readonly Func<string, bool> exists;
    private readonly Dictionary<uint, string> finishMaterials;
    private readonly Dictionary<(string, uint), FurniturePrefab> finishedParts = [];
    private readonly Func<string, ScdFile?> readSound;
    private readonly Func<string, MdlFile?> readModel;
    private readonly Func<string, MtrlFile?> readMaterial;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, uint?> originalStains = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> soundLoops = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long?> soundLengths = new(StringComparer.Ordinal);
    public bool BuildingsReady => buildings.IsCompletedSuccessfully;
    public string BuildingStatus => buildings.IsFaulted ? "Building catalogue unavailable: " + buildings.Exception!.GetBaseException().Message : "Reading installed housing building parts…";
    public FurnitureEntry[] BuildingItems => buildings.GetAwaiter().GetResult().Items;
    public string[] BuildingCategories => buildings.GetAwaiter().GetResult().Categories;
    /// <summary>A preset home's shell pieces for remodelling (empty until the building catalogue is read, or for other territories).</summary>
    public IReadOnlyList<ShellPieceInfo> ShellPieces(uint territory) => BuildingsReady ? buildings.Result.ShellPieces(territory) : [];
    public IReadOnlyList<uint> CompanyDoors(uint territory) => BuildingsReady ? buildings.Result.CompanyDoors(territory) : [];
    /// <summary>Whether a shell piece can be moved (become a building piece of its own).</summary>
    public bool CanBuild(string path) => BuildingsReady && buildings.Result.CanBuild(path);
    public FurnitureEntry[] Items { get; }
    public FixtureEntry[] Fixtures { get; }
    public PictureEntry[] Pictures { get; }
    private readonly Dictionary<uint, PictureEntry> byPicture;
    private readonly Dictionary<(uint, uint, uint, string?), FurniturePrefab> displayed = [];
    // The material of a custom picture once it is built and redirected (CustomPictures.Material); null keeps a frame
    // blank meanwhile.
    public Func<Furnishing, string?>? CustomPictureMaterial { get; set; }
    // Custom furniture: the base item's model paths a furnishing's package replaces, once its files are served
    // (CustomFurnitureLibrary.Models); null draws the base item.
    public Func<Furnishing, IReadOnlyDictionary<string, string>?>? CustomFurnitureModels { get; set; }
    private readonly Dictionary<(uint, string), FurniturePrefab> furnished = [];
    public DyeEntry[] Dyes { get; }
    public string[] Categories { get; }

    public FurnitureCatalogue(IDataManager data)
    {
        exists = data.FileExists;
        readSound = path => data.GetFile<ScdFile>(path);
        readModel = path => data.GetFile<MdlFile>(path);
        readMaterial = path => data.GetFile<MtrlFile>(path);
        var categories = data.GetExcelSheet<FurnitureCatalogCategory>().ToDictionary(x => x.RowId, x => x.Category.ToString());
        var itemCategories = data.GetExcelSheet<FurnitureCatalogItemList>().Where(x => x.Item.IsValid && x.Category.IsValid)
            .GroupBy(x => x.Item.RowId).ToDictionary(g => g.Key, g => categories.GetValueOrDefault(g.First().Category.RowId, "Other"));
        // Usage 12 displays: parameter 2 frames show a painting, parameter 3 partitions a wallpaper.
        Items = data.GetExcelSheet<HousingFurniture>().Where(x => x.Item.IsValid && !x.Item.Value.Name.IsEmpty)
            .Select(x => new FurnitureEntry(x.Item.RowId, x.RowId, x.ModelKey, x.Item.Value.Icon, x.Item.Value.Name.ToString(),
                itemCategories.GetValueOrDefault(x.Item.RowId, x.Item.Value.ItemUICategory.Value.Name.ToString()), x.Item.Value.Description.ToString(), x.UsageType, x.Item.Value.DyeCount,
                FinishPart: x.UsageType == DisplayUsage && x.UsageParameter == 3 ? 0u : null, CustomTalk: x.CustomTalk.RowId)
            { TakesPicture = x.UsageType == DisplayUsage && x.UsageParameter == 2,
              TurnsUpright = x.UsageType == DisplayUsage && x.UsageParameter == 2 && x.Item.Value.ItemUICategory.RowId == WallMounted,
              DisplayKind = x.UsageType == DisplayUsage && x.UsageParameter is 1 or (>= 4 and <= 7) ? x.UsageParameter : x.UsageType == FlowerpotUsage ? 1u : 0,
              Flowerpot = x.UsageType == FlowerpotUsage,
              AquariumTier = x.UsageType == AquariumUsage && x.UsageParameter is >= 1 and <= 4 ? (int)x.UsageParameter : 0 })
            .OrderBy(x => x.Name).ToArray();
        // Paintings (item category 95) name a Picture row; PaintingTextures gives the texture that draws it.
        var pictureRows = data.GetExcelSheet<Picture>();
        Pictures = data.GetExcelSheet<Item>().Where(x => x.ItemUICategory.RowId == 95 && !x.Name.IsEmpty && pictureRows.HasRow(x.AdditionalData.RowId))
            .Select(x => (Item: x, Row: pictureRows.GetRow(x.AdditionalData.RowId)))
            .Select(x => new PictureEntry(x.Row.RowId, x.Item.Name.ToString(), (uint)Math.Max(0, x.Row.Image),
                PaintingTextures.Texture(x.Row.RowId) is uint texture ? PaintingTextures.Material(texture) : ""))
            .Where(x => x.Material.Length > 0 && data.FileExists(x.Material)).GroupBy(x => x.Id).Select(g => g.First()).OrderBy(x => x.Name).ToArray();
        byPicture = Pictures.ToDictionary(x => x.Id);
        paintingItems = data.GetExcelSheet<Item>().Where(x => x.ItemUICategory.RowId == 95 && byPicture.ContainsKey(x.AdditionalData.RowId))
            .GroupBy(x => x.RowId).ToDictionary(g => g.Key, g => g.First().AdditionalData.RowId);
        // What vases and canvases show (VaseFlower rows grouped by Vase row: 1 flowers, 4-7 fish prints by canvas size),
        // and the fish aquariums hold.
        var vases = data.GetExcelSheet<Vase>();
        var displays = new List<DisplayEntry>();
        foreach (var row in data.GetExcelSheet<VaseFlower>())
        {
            if (!row.Item.IsValid || row.Item.Value.Name.IsEmpty) continue;
            var kind = vases.FirstOrDefault(v => row.RowId >= v.VaseFlowerStart && row.RowId <= v.VaseFlowerEnd).RowId;
            if (kind is 1 or (>= 4 and <= 7)) displays.Add(new(row.RowId, kind, row.Item.RowId, row.Item.Value.Name.ToString(), (ushort)row.Item.Value.Icon, row.Unknown0, row.Unknown2));
        }
        Displays = [.. displays.OrderBy(x => x.Name)];
        byDisplay = Displays.ToDictionary(x => x.Row);
        Fish = [.. data.GetExcelSheet<AquariumFish>().Where(x => x.Item.IsValid && !x.Item.Value.Name.IsEmpty && x.Size is >= 1 and <= 4)
            .Select(x => new AquariumFishEntry(x.RowId, x.Item.RowId, x.Item.Value.Name.ToString(), (ushort)x.Item.Value.Icon, x.Unknown0, x.AquariumWater.RowId, x.Size))
            .OrderBy(x => x.Name)];
        byFish = Fish.ToDictionary(x => x.Row);
        byItem = Items.GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.First());
        byModel = Items.GroupBy(x => x.ModelKey).ToDictionary(g => g.Key, g => g.First());
        Categories = Items.Select(x => x.Category).Distinct().Order().ToArray();
        Fixtures = data.GetExcelSheet<Item>().Where(x => x.ItemUICategory.RowId is 73 or 74 or 75 && x.AdditionalData.RowId != 0)
            .Where(x => data.GetExcelSheet<HousingInterior>().HasRow(x.AdditionalData.RowId))
            .Select(x => new FixtureEntry(x.RowId, x.AdditionalData.RowId, x.Icon, x.Name.ToString(), x.ItemUICategory.RowId switch { 73 => 0u, 74 => 3u, _ => 4u }))
            .OrderBy(x => x.Name).ToArray();
        Dyes = data.GetExcelSheet<Stain>().Where(x => x.RowId == 0 || x.IsHousingApplicable)
            .Select(x => new DyeEntry(x.RowId, x.RowId == 0 ? "Original colour" : x.Name.ToString(), x.Color)).ToArray();
        byDye = Dyes.ToDictionary(x => x.Id);
        stainColors = data.GetExcelSheet<Stain>().ToDictionary(x => x.RowId, x => x.Color);
        orchestrionIds = data.GetExcelSheet<OrchestrionPath>().Where(x => !x.File.IsEmpty).Select(x => x.RowId).ToHashSet();
        // Installed housing shell codes end i1/i2/i3 for cottage/house/mansion.
        // All seven i3 shells have a separate doubled-size central chandelier.
        mainHallTerritories = data.GetExcelSheet<TerritoryType>().Where(x => x.TerritoryIntendedUse.RowId == 14
            && x.Bg.ToString().EndsWith("i3", StringComparison.Ordinal)).Select(x => x.RowId).ToHashSet();
        interiorShellCodes = data.GetExcelSheet<TerritoryType>().Where(x => x.TerritoryIntendedUse.RowId == 14)
            .ToDictionary(x => x.RowId, x => x.Name.ToString());
        // Housing interiors and inn rooms.
        interiorPlaceNames = data.GetExcelSheet<TerritoryType>().Where(x => x.TerritoryIntendedUse.RowId is 14 or 2 && !x.Bg.IsEmpty && !x.PlaceName.Value.Name.IsEmpty)
            .ToDictionary(x => x.RowId, x => x.PlaceName.Value.Name.ToString().Replace(" - ", " · "));
        prefabs = new FurniturePrefabReader(path => data.GetFile<SgbFile>(path), path => data.GetFile<MdlFile>(path), data.FileExists);
        finishMaterials = data.GetExcelSheet<HousingInterior>().Where(x => x.Unknown2 is 9 or 10)
            .ToDictionary(x => x.RowId, x => $"bgcommon/hou/dyna/mat/{(x.Unknown2 == 9 ? "wl" : "fl")}/{x.Unknown1:0000}/material/rom_{(x.Unknown2 == 9 ? "wl" : "fl")}_2{x.Unknown1:0000}a.mtrl");
        var shells = data.GetExcelSheet<TerritoryType>().Where(x => x.TerritoryIntendedUse.RowId == 14 && !x.PlaceName.Value.Name.IsEmpty)
            .Where(x => new[] { "i1", "i2", "i3", "i4_2" }.Any(end => x.Name.ToString().EndsWith(end, StringComparison.Ordinal)))
            .Select(x => new BuildingShell(x.RowId, x.Name.ToString(), x.PlaceName.Value.Name.ToString(), x.Bg.ToString()))
            .Concat(data.GetExcelSheet<TerritoryType>().Where(x => x.TerritoryIntendedUse.RowId == 2 && !x.Bg.IsEmpty && !x.PlaceName.Value.Name.IsEmpty)
                .Select(x => new BuildingShell(x.RowId, x.Name.ToString(), x.PlaceName.Value.Name.ToString(), x.Bg.ToString(), Inn: true))).ToArray();
        var ceilingParts = Fixtures.Where(x => x.Part == 4).Select(x => new BuildingFixture(x.Name, x.Icon,
            $"bgcommon/hou/dyna/lmp/lp/{data.GetExcelSheet<HousingInterior>().GetRow(x.FixtureId).Unknown1:0000}/asset/lmp_s0_m{data.GetExcelSheet<HousingInterior>().GetRow(x.FixtureId).Unknown1:0000}.sgb")).ToArray();
        var modules = BuildingCatalogue.ModularPieces.Where(p => byItem.ContainsKey(p.ItemId)).Select(p => { var item = Get(p.ItemId); return new BuildingFixture(item.Name, item.Icon,
            $"bgcommon/hou/indoor/general/{item.ModelKey:0000}/asset/fun_b0_m{item.ModelKey:0000}.sgb", p.Category, "Modular housing furnishing", item.DyeSlots); }).ToArray();
        var eventObjects = data.GetExcelSheet<EObj>();
        buildings = Task.Run(() => new BuildingCatalogue(shells, data.GetFile<LgbFile>, data.GetFile<SgbFile>, data.GetFile<MdlFile>, data.FileExists, ceilingParts.Concat(modules),
            row => eventObjects.GetRowOrDefault(row)?.SgbPath.ValueNullable?.SgbPath.ToString()));
    }

    /// <summary>A catalogue item by its id, or null when this game build has no such furnishing.</summary>
    public FurnitureEntry? Find(uint itemId) => byItem.GetValueOrDefault(itemId);
    public FurnitureEntry Get(uint itemId) => itemId == 0 ? PropEntry : byItem.GetValueOrDefault(itemId) ?? throw new InvalidDataException("Furniture item is unavailable in this game build.");
    /// <summary>A custom prop made from a minion model: a furnishing with no base item.</summary>
    public static readonly FurnitureEntry PropEntry = new(0, 0, 0, 0, "Custom prop", "Custom furniture", "A prop made from a minion model", 0, 0);
    // Custom props: a prop's prefab once its package is served (its model and a box collider); null until then.
    public Func<Furnishing, FurniturePrefab?>? CustomPropPrefab { get; set; }
    private static readonly FurniturePrefab NothingYet = new([], [], [], []);
    public FurnitureEntry? FromModelPath(string? path)
    {
        const string prefix = "bgcommon/hou/indoor/general/";
        if (path == null || !path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var end = path.IndexOf('/', prefix.Length);
        return end > prefix.Length && ushort.TryParse(path.AsSpan(prefix.Length, end - prefix.Length), out var key) ? byModel.GetValueOrDefault(key) : null;
    }
    public FurniturePrefab Prefab(uint itemId) => prefabs.Get(Get(itemId).ModelKey);
    // The placed piece's model bounds in the home's coordinates.
    public PlacementBounds? Bounds(Furnishing item)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var model in Prefab(item).Models)
        { var box = PlacementBounds.Transform(model.Minimum, model.Maximum, model.Transform * ItemMatrix(item)); min = Vector3.Min(min, box.Minimum); max = Vector3.Max(max, box.Maximum); }
        return min.X <= max.X ? new(min, max) : null;
    }
    public FurnitureEntry Get(Furnishing item) => item.BuildingPart == null ? Get(item.ItemId) : Part(item.BuildingPart).Entry;
    /// <summary>The game's world as building pieces; null until the plugin sets it up.</summary>
    public WorldCatalogue? World { get; set; }
    /// <summary>The world's effects and ambient sounds as pieces; null until the plugin sets it up.</summary>
    public AmbientCatalogue? Ambient { get; set; }
    // A building part: the housing and inn parts read up front, else a world piece (built when first wanted; a
    // placeholder drawing nothing meanwhile, or when this PC can't draw it).
    private BuildingPart Part(string path)
    {
        if (Ambient != null && BuildingParts.IsAmbient(path)) return Ambient.Part(path);
        var catalogue = buildings.GetAwaiter().GetResult();
        return World != null && !catalogue.CanBuild(path) && BuildingParts.IsWorldAsset(path) ? World.Part(path) : catalogue.Get(path);
    }
    /// <summary>The building catalogue once it's read (the world catalogue builds its parts with it).</summary>
    public BuildingCatalogue? ReadyBuildings => BuildingsReady ? buildings.Result : null;
    private const byte DisplayUsage = 12;
    private const uint WallMounted = 79;
    public PictureEntry? Picture(uint id) => byPicture.GetValueOrDefault(id);
    private readonly Dictionary<uint, uint> paintingItems = [];
    /// <summary>The painting (Picture row) a painting item shows, or null for any other item.</summary>
    public PictureEntry? PictureOfItem(uint itemId) => paintingItems.TryGetValue(itemId, out var row) ? Picture(row) : null;
    public DisplayEntry[] Displays { get; } = [];
    private readonly Dictionary<uint, DisplayEntry> byDisplay = [];
    public DisplayEntry? Display(uint row) => byDisplay.GetValueOrDefault(row);
    public AquariumFishEntry[] Fish { get; } = [];
    private readonly Dictionary<uint, AquariumFishEntry> byFish = [];
    public AquariumFishEntry? FishOf(uint row) => byFish.GetValueOrDefault(row);
    private const byte AquariumUsage = 13;
    private const byte FlowerpotUsage = 11;
    private static string PrintSize(uint kind) => kind switch { 4 => "sm", 5 => "mi", 6 => "la", _ => "ll" };
    /// <summary>Whether an aquarium setup fits its tank: the tier's size and slots, one water, known fish, a decoration 0-6.</summary>
    public bool AquariumFits(FurnitureEntry entry, AquariumSetup tank) => entry.AquariumTier > 0 && tank.Decor <= Aquariums.MaxDecor
        && tank.Fish.All(f => byFish.ContainsKey(f)) && Aquariums.Fits(entry.AquariumTier, [.. tank.Fish.Select(f => (byFish[f].Size, byFish[f].Water))]);
    // The same placed piece is asked for its prefab several times a frame (visuals, picking, switches, effects), and a
    // frame's painting, flowers or fish build their cache key each time. On the framework thread the answer is kept
    // until the next frame (NextFrame), so a picture or package that becomes ready shows a frame later.
    private readonly Dictionary<Furnishing, FurniturePrefab> thisFrame = new(ReferenceEqualityComparer.Instance);
    private int frameThread = -1;
    /// <summary>Starts a frame: what Prefab answered in the last one is forgotten. Called on the framework thread.</summary>
    public void NextFrame() { thisFrame.Clear(); frameThread = Environment.CurrentManagedThreadId; }
    public FurniturePrefab Prefab(Furnishing item)
    {
        var memo = Environment.CurrentManagedThreadId == frameThread;
        if (memo && thisFrame.TryGetValue(item, out var known)) return known;
        var prefab = Resolve(item);
        if (memo) thisFrame[item] = prefab;
        return prefab;
    }
    private FurniturePrefab Resolve(Furnishing item)
    {
        if (item.BuildingPart == null && item.ItemId == 0) return CustomPropPrefab?.Invoke(item) ?? NothingYet;
        if (item.BuildingPart == null) return Displayed(item);
        var part = Part(item.BuildingPart);
        if (item.SurfaceFinish == 0) return part.Prefab;
        var key = (item.BuildingPart, item.SurfaceFinish);
        if (finishedParts.TryGetValue(key, out var prefab)) return prefab;
        if (part.Entry.FinishPart is not {} kind || !Fixtures.Any(x => x.FixtureId == item.SurfaceFinish && x.Part == kind)
            || !finishMaterials.TryGetValue(item.SurfaceFinish, out var material) || !exists(material))
            throw new InvalidDataException("This finish is not available for the selected building surface.");
        // These native wall/floor models expose one replaceable surface material.
        prefab = buildings.GetAwaiter().GetResult().WithFinish(item.BuildingPart, kind, material);
        finishedParts[key] = prefab;
        return prefab;
    }
    // A frame's chosen painting or a partition's wallpaper goes in the model's native replaceable material, the slot
    // its placeholder (pic/ta/0000 or dyna/mat/wl/0000) names; the game fills the same slot.
    private FurniturePrefab Displayed(Furnishing item)
    {
        if (item.Display != 0 || item.Aquarium is { Fish.Length: > 0 } or { Decor: > 0 }) return Filled(item);
        var prefab = Furnished(item);
        if (item.Picture == 0 && item.SurfaceFinish == 0 && item.CustomPicture == null) return prefab;
        var custom = item.CustomPicture != null ? CustomPictureMaterial?.Invoke(item) : null;
        if (item.CustomPicture != null && custom == null) return prefab;
        var key = (item.ItemId, item.Picture, item.SurfaceFinish, custom + "|" + item.CustomFurniture);
        if (displayed.TryGetValue(key, out var cached)) return cached;
        var entry = Get(item.ItemId);
        string material, slot;
        if (custom != null)
        {
            if (!entry.TakesPicture) throw new InvalidDataException("This furnishing cannot show a picture.");
            (material, slot) = (custom, "/pic/ta/");
        }
        else if (item.Picture != 0)
        {
            if (!entry.TakesPicture || Picture(item.Picture) is not {} picture) throw new InvalidDataException("This painting cannot be shown on this furnishing.");
            (material, slot) = (picture.Material, "/pic/ta/");
        }
        else if (entry.FinishPart == 0 && Fixtures.Any(x => x.FixtureId == item.SurfaceFinish && x.Part == 0) && finishMaterials.TryGetValue(item.SurfaceFinish, out var wall) && exists(wall))
            (material, slot) = (wall, "/dyna/mat/wl/");
        else throw new InvalidDataException("This wallpaper cannot be shown on this furnishing.");
        cached = prefab with { Models = prefab.Models.Select(x => x.ChangeSlot?.Contains(slot, StringComparison.Ordinal) == true ? x with { Material = material } : x).ToArray() };
        displayed[key] = cached;
        return cached;
    }
    // A vase's flowers, a canvas's fish print, or an aquarium's fish, water and decoration. The game fills the same places:
    // a vase's flower spot and a tank's fish, water and decoration spots are empty groups (…/0000/asset/…_m0000.sgb)
    // swapped for the chosen one's group; a canvas's print is its model's replaceable material (gyo/<size>/0000).
    private FurniturePrefab Filled(Furnishing item)
    {
        var entry = Get(item.ItemId);
        if (entry.Canvas)
        {
            if (Display(item.Display) is not { } print || print.Kind != entry.DisplayKind) throw new InvalidDataException("This print can't be shown on this canvas.");
            var key = (item.ItemId, item.Display, 0u, "print");
            if (displayed.TryGetValue(key, out var shown)) return shown;
            var size = PrintSize(print.Kind);
            var material = $"bgcommon/hou/indoor/gyo/{size}/{print.Index:D4}/material/gyo_{size}_2{print.Index:D4}a.mtrl";
            if (!exists(material)) throw new InvalidDataException("This print's texture isn't in the game data.");
            var plain = Furnished(item);
            return displayed[key] = plain with { Models = plain.Models.Select(x => x.ChangeSlot?.Contains("/indoor/gyo/", StringComparison.Ordinal) == true ? x with { Material = material } : x).ToArray() };
        }
        if (entry.Vase)
        {
            if (Display(item.Display) is not { Kind: 1 } flowers) throw new InvalidDataException("These flowers can't be shown in this vase.");
            // A vase's flower spot is an empty hana group swapped for the cut flowers (han_f4); a flowerpot's is an empty
            // farm plot (outdoor/frm/0000) swapped for the plant at its last growth stage, in bloom (frm_f4; f1-f3 grow,
            // f5 has wilted). Every flower a vase takes has one (244 rows, all colours, checked 29 October 2026).
            var (slot, stage) = entry.Flowerpot ? ("bgcommon/hou/outdoor/frm/0000/", "frm") : ("bgcommon/hou/outdoor/hana/0000/", "han");
            var swap = entry.Flowerpot ? $"bgcommon/hou/outdoor/frm/{flowers.Index:D4}/asset/frm_f4_m{flowers.Index:D4}.sgb"
                : $"bgcommon/hou/outdoor/hana/{flowers.Index:D4}/asset/han_f4_m{flowers.Index:D4}.sgb";
            var arranged = prefabs.Get(entry.ModelKey, "flowers" + flowers.Index, (path, _) =>
                path.StartsWith(slot, StringComparison.Ordinal) && path.EndsWith("_m0000.sgb", StringComparison.Ordinal) ? swap : null);
            // The flowers' colour (VaseFlower column 2: 0-8) is its own model beside the first colour's, numbered colour ×
            // 1000 + the flower (a red oldrose han_f4_m0101, blue han_f4_m1101), as a real vase shows. Missing, the first.
            if (flowers.Colour == 0) return arranged;
            var key = (item.ItemId, item.Display, (uint)flowers.Colour, "flowers");
            if (displayed.TryGetValue(key, out var coloured)) return coloured;
            var first = $"/frm/{flowers.Index:D4}/bgparts/{stage}_f4_m{flowers.Index:D4}.mdl";
            var colour = $"/frm/{flowers.Index:D4}/bgparts/{stage}_f4_m{flowers.Colour * 1000 + flowers.Index:D4}.mdl";
            return displayed[key] = arranged with { Models = arranged.Models.Select(m => m.Path.EndsWith(first, StringComparison.Ordinal)
                && m.Path[..^first.Length] + colour is var path && exists(path) ? m with { Path = path } : m).ToArray() };
        }
        if (entry.AquariumTier > 0 && item.Aquarium is { } tank)
        {
            if (!AquariumFits(entry, tank)) throw new InvalidDataException("These fish don't fit this aquarium.");
            var fish = tank.Fish.Select(f => byFish[f]).ToArray();
            var water = fish.Length > 0 ? fish[0].Water : 0;
            return prefabs.Get(entry.ModelKey, $"tank{string.Join(",", tank.Fish)}|{tank.Decor}", (path, occurrence) =>
            {
                if (!path.EndsWith("_m0000.sgb", StringComparison.Ordinal) || !path.Contains("/0000/asset/", StringComparison.Ordinal)) return null;
                if (path.Contains("/indoor/gyo/", StringComparison.Ordinal))
                    return occurrence < fish.Length ? $"bgcommon/hou/indoor/gyo/{fish[occurrence].SizeCode}/{fish[occurrence].Model:D4}/asset/fsh_{fish[occurrence].SizeCode}_m{fish[occurrence].Model:D4}.sgb" : null;
                if (path.Contains("/wat/", StringComparison.Ordinal) && water is 1 or 2) return path.Replace("/0000/asset/", $"/{water:D4}/asset/").Replace("_m0000.sgb", $"_m{water:D4}.sgb");
                if (path.Contains("/aqa/", StringComparison.Ordinal) && tank.Decor > 0) return path.Replace("/0000/asset/", $"/{tank.Decor:D4}/asset/").Replace("_m0000.sgb", $"_m{tank.Decor:D4}.sgb");
                return null;
            });
        }
        return Furnished(item);
    }

    // The base item's prefab drawn with a custom furnishing's models, once its package is served; everything else
    // (collision, seats, lights, effects, motion) stays the base item's.
    private FurniturePrefab Furnished(Furnishing item)
    {
        var prefab = Prefab(item.ItemId);
        if (item.CustomFurniture is not { } hash || CustomFurnitureModels?.Invoke(item) is not { } models) return prefab;
        if (furnished.TryGetValue((item.ItemId, hash), out var cached)) return cached;
        cached = prefab with { Models = [.. prefab.Models.Select(m => models.TryGetValue(m.Path.ToLowerInvariant(), out var path) ? m with { Path = path } : m)] };
        furnished[(item.ItemId, hash)] = cached;
        return cached;
    }

    /// <summary>The catalogue items a mod might change: those whose model folder holds any of the mod's files.</summary>
    public IReadOnlyList<FurnitureEntry> ItemsTouching(IEnumerable<string> gamePaths)
    {
        const string prefix = "bgcommon/hou/indoor/general/";
        var keys = gamePaths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => { var end = p.IndexOf('/', prefix.Length); return end > prefix.Length && ushort.TryParse(p.AsSpan(prefix.Length, end - prefix.Length), out var key) ? key : (ushort?)null; })
            .OfType<ushort>().ToHashSet();
        return [.. Items.Where(i => keys.Contains(i.ModelKey))];
    }

    public bool HasMainHallChandelier(uint territoryId) => mainHallTerritories.Contains(territoryId);
    public string? InteriorShellCode(uint territoryId) => interiorShellCodes.GetValueOrDefault(territoryId);
    public string ShellName(uint territoryId) => interiorPlaceNames.GetValueOrDefault(territoryId, "this house");
    public Vector4 Dye(uint stainId)
    {
        if (stainId == 0) return Vector4.One;
        var dye = byDye.GetValueOrDefault(stainId) ?? throw new InvalidDataException("Unknown housing dye.");
        return FurnitureDyes.Linear(dye.Color);
    }
    // The colour an undyed model shows: its first colour-change material's g_DiffuseColor (CRC 0x2C2A34DD). That
    // shader colours the model from the dye buffer alone, so undyed it must hold this. Null without such a material.
    public uint? OriginalStain(string modelPath) => originalStains.GetOrAdd(modelPath, path =>
    {
        try
        {
            if (readModel(path) is not {} mdl) return null;
            foreach (var offset in mdl.MaterialNameOffsets)
            {
                var start = (int)offset; var end = Array.IndexOf(mdl.Strings, (byte)0, start);
                if (end < 0 || readMaterial(System.Text.Encoding.ASCII.GetString(mdl.Strings, start, end - start)) is not {} material) continue;
                var shader = material.FileHeader.ShaderPackageNameOffset; var shaderEnd = Array.IndexOf(material.Strings, (byte)0, shader);
                if (shaderEnd < 0 || System.Text.Encoding.ASCII.GetString(material.Strings, shader, shaderEnd - shader) != "bgcolorchange.shpk") continue;
                var diffuse = material.Constants.FirstOrDefault(c => c.ConstantId == 0x2C2A34DD);
                var values = material.ShaderValues;
                if (diffuse.ValueSize < 12 || diffuse.ValueOffset % 4 != 0 || diffuse.ValueOffset / 4 + 3 > values.Length) continue;
                var at = diffuse.ValueOffset / 4;
                return FurnitureDyes.Rgba(FurnitureDyes.FromLinear(values[at], values[at + 1], values[at + 2]), true);
            }
            return null;
        }
        catch (Exception) { return null; }
    });
    public uint StainRgba(uint stainId) => FurnitureDyes.Rgba(stainId == 0 || !byDye.TryGetValue(stainId, out var dye) ? 0 : dye.Color, stainId != 0 && byDye.ContainsKey(stainId));
    // The colour an undyed furnishing's dye buffer holds: its prefab's default dye, as the game writes it (a probe of a
    // real house read exactly these values), else its first colour-change material's default for pieces with no
    // furniture prefab. Null when there is nothing to write.
    public uint? UndyedRgba(Furnishing item, string modelPath) =>
        DefaultDyeColor(item) is uint colour ? FurnitureDyes.Rgba(colour, true) : OriginalStain(modelPath);
    // The 0xRRGGBB of a furnishing's default dye, for its "Original colour" swatch; null for pieces without one.
    public uint? DefaultDyeColor(Furnishing item) => Prefab(item).DefaultDye is ushort dye && stainColors.TryGetValue(dye, out var colour) ? colour : null;
    public void Validate(House house)
    {
        var starter = house.CustomLayout ? Prefab(CustomCanvas.PlatformItemId) : null;
        int parts = starter == null ? 0 : starter.Models.Length + 1, colliders = starter == null ? 0 : 1;
        foreach (var item in house.Furnishings)
        {
            var entry = Get(item);
            if (item.SurfaceFinish != 0 && entry.FinishPart == null) throw new InvalidDataException("This piece has no replaceable surface finish.");
            if (item.Picture != 0 && (!entry.TakesPicture || Picture(item.Picture) == null)) throw new InvalidDataException("Unknown painting, or a furnishing that cannot show one.");
            if (item.Picture != 0 && item.SurfaceFinish != 0) throw new InvalidDataException("A furnishing shows a painting or a wallpaper, not both.");
            if (item.CustomPicture != null && !entry.TakesPicture) throw new InvalidDataException("Only picture frames can show a custom picture.");
            if (item.Portrait && !entry.TurnsUpright) throw new InvalidDataException("Only wall picture frames can stand upright.");
            if (item.Display != 0 && Display(item.Display)?.Kind != entry.DisplayKind) throw new InvalidDataException("Unknown flowers or print, or a furnishing that can't show them.");
            if (item.Aquarium != null && !AquariumFits(entry, item.Aquarium)) throw new InvalidDataException("These fish don't fit this aquarium.");
            if (item.BuildingPart != null && item.StainId != 0 && entry.DyeSlots == 0) throw new InvalidDataException("This building piece is not dyeable.");
            if (!byDye.ContainsKey(item.StainId)) throw new InvalidDataException("Unknown furnishing dye.");
            var prefab = Prefab(item);
            parts += 1 + prefab.Models.Length + prefab.Lights.Length;
            colliders += item.Collision ? prefab.Colliders.Length : 0;
            // A world piece not built yet is an empty placeholder: count what it will cost (a model from the index, a
            // group by building it now), or a home could pass here and exceed the budget once its pieces are built.
            if (item.BuildingPart != null && World?.Unbuilt(item.BuildingPart) is { } unbuilt)
            {
                var (objects, solid) = World.UnbuiltCost(unbuilt);
                parts += objects; colliders += item.Collision ? solid : 0;
            }
        }
        if (parts > HouseFormat.MaxObjects || colliders > HouseFormat.MaxObjects) throw new InvalidDataException("This home exceeds the furniture scene budget.");
        if (house.Music.Playlist.Any(x => !x.IsCustom && !orchestrionIds.Contains(x.OrchestrionId))) throw new InvalidDataException("This playlist contains an unavailable orchestrion track.");
        foreach (var floor in house.Interior.Floors)
            foreach (var (id, part) in new[] { (floor.Wallpaper, 0u), (floor.Flooring, 3u), (floor.CeilingLight, 4u) })
                if (id != 0 && !Fixtures.Any(x => x.FixtureId == id && x.Part == part)) throw new InvalidDataException("Unknown or mismatched interior fixture.");
        if (house.Interior.MainHallCeilingLight != 0 && (!HasMainHallChandelier(house.TerritoryId)
            || !Fixtures.Any(x => x.FixtureId == house.Interior.MainHallCeilingLight && x.Part == 4)))
            throw new InvalidDataException("The main hall chandelier requires a mansion shell and a ceiling-light fixture.");
    }

    public static Matrix4x4 ItemMatrix(Furnishing item) => PlacementMath.ItemMatrix(item);
    // Varies random motion (a wind chime's swing) between copies, the same on every client: it comes from the item's ID.
    public static int MotionSeed(Furnishing item) => BitConverter.ToInt32(item.Id.ToByteArray(), 0);
    public static Matrix4x4 HouseMatrix(House house) => Matrix4x4.CreateScale(house.Transform.Scale) * Matrix4x4.CreateFromQuaternion(house.Transform.Orientation) * Matrix4x4.CreateTranslation(house.Transform.Position);

    /// <param name="havenLights">Haven draws the furnishing lights and the canvas fill itself (FurnitureLights), so the
    /// stage leaves them out; an imported stage's own lights stay on it either way.</param>
    public StageDefinition BuildStage(House house, Furnishing? ghost = null, string[]? excludeImported = null, bool includeFurnitureModels = true,
        Func<Furnishing, bool>? switchOn = null, bool havenLights = false)
    {
        if (!StageDefinition.TryParseDefinitionString(house.Stage.GetRawText(), out var stage)) throw new InvalidDataException("Invalid legacy furnishing stage.");
        if (excludeImported != null) ImportedScene.Remove(stage, excludeImported);
        var lights = 0;
        LimitImportedLights(stage.Objects, ref lights);
        if (house.CustomLayout && !havenLights)
            stage.Objects["haven-canvas-fill"] = new LightDefinition { DisplayName = "Canvas fill light", Position = new(0, 6, 0),
                Color = Vector3.One, Intensity = 1.4f * (house.Interior.Brightness ?? 5) / 5, Range = 24 };
        foreach (var item in house.Furnishings) Add(stage, item, false, includeFurnitureModels, switchOn, havenLights);
        if (ghost != null) Add(stage, ghost, true, includeFurnitureModels, null);
        if (Count(stage.Objects.Values) + (house.CustomLayout ? 1 : 0) > HouseFormat.MaxObjects) throw new InvalidDataException("The furnishing scene exceeds its rendering budget.");
        return stage;
    }
    // Identity-transform, world-space stages outside the item groups. Effects share one stage.
    // Each sound gets its own create-once stage: Stagehand 0.5.5's in-place sound update (also run
    // by a repeated create/visible call) silenced furnishing loops in game, while its editor's
    // create-only path played the same file. Values are definition strings keyed by stage suffix.
    // animation: the shared animation clock, for loop-driven effects showing in a window of each lap.
    public AmbientScene BuildAmbience(House house, bool effects, bool sounds, Func<Furnishing, bool>? switchOn = null, long animation = 0,
        Func<Furnishing, (int SubId, long Since, long Started)?>? action = null)
    {
        var parent = HouseMatrix(house);
        List<AmbientEmitter> fx = [], audio = [];
        foreach (var item in house.Furnishings.Where(x => x.BuildingPart == null))
        {
            var prefab = Prefab(item); var world = ItemMatrix(item) * parent; var id = item.Id.ToString("N");
            var on = prefab.Switch == null || (switchOn?.Invoke(item) ?? prefab.Switch.DefaultOn);
            if (effects) for (var i = 0; i < prefab.Effects.Length; i++) if (PrefabState.Shown(prefab.Effects[i].Switch, on)) fx.Add(new($"fx-{id}-{i}", prefab.Effects[i].Path, prefab.Effects[i].Transform * world, prefab.Effects[i].Color));
            // Each lap's window gets its own key, so the effect starts afresh every lap as the native loop restarts it.
            if (effects) for (var i = 0; i < prefab.LoopEffects.Length; i++)
                if (prefab.LoopEffects[i] is var lapped && lapped.Loop.ShowingLap(animation, lapped.AutoPlay) is long lap)
                    fx.Add(new($"lf-{id}-{i}-{lap}", lapped.Path, lapped.Transform * world, lapped.Color));
            // A one-shot timeline's effects while their window lasts; each play gets its own key, so each starts afresh.
            if (effects && prefab.ActionEffects.Length > 0 && action?.Invoke(item) is { } playing)
                for (var i = 0; i < prefab.ActionEffects.Length; i++)
                    if (prefab.ActionEffects[i] is var shown && shown.SubId == playing.SubId && shown.Showing(playing.Since))
                        fx.Add(new($"af-{id}-{i}-{playing.Started}", shown.Path, shown.Transform * world, shown.Color));
            if (sounds) for (var i = 0; i < prefab.Sounds.Length; i++) audio.Add(new($"se-{id}-{i}", prefab.Sounds[i].Path, prefab.Sounds[i].Transform * world, Vector4.One, item.SoundVolume ?? 1));
            // A switch side's own loop (a shower, a Carbuncle Lamp's hum) plays for as long as the item is on that side.
            if (sounds && prefab.Switch != null) for (var i = 0; i < prefab.SwitchSounds.Length; i++)
                if (prefab.SwitchSounds[i] is var held && held.On == on && Held(held))
                    audio.Add(new($"sw-{id}-{i}", held.Path, held.Transform * world, Vector4.One, item.SoundVolume ?? 1));
        }
        var stage = new StageDefinition { FormatVersion = StageDefinition.CurrentFormatVersion };
        var drawn = AmbientBudget.Effects(fx);
        // World groups' always-playing effects: only for Haven's own effects (EffectList), never on Stagehand's stage, after
        // the furnishings' and within the same budget.
        List<AmbientEmitter> worldFx = [];
        if (effects)
            foreach (var item in house.Furnishings.Where(x => x.BuildingPart != null))
            {
                var prefab = Prefab(item);
                if (prefab.WorldEffects.Length == 0) continue;
                var world = ItemMatrix(item) * parent; var id = item.Id.ToString("N");
                for (var i = 0; i < prefab.WorldEffects.Length; i++) worldFx.Add(new($"wf-{id}-{i}", prefab.WorldEffects[i].Path, prefab.WorldEffects[i].Transform * world, prefab.WorldEffects[i].Color));
            }
        var withWorld = worldFx.Count == 0 ? drawn
            : [.. drawn, .. worldFx.Take(Math.Min(AmbientBudget.MaxWorldEffects, AmbientBudget.MaxEffects - drawn.Length))];
        foreach (var e in drawn)
        {
            Matrix4x4.Decompose(e.World, out var scale, out var rotation, out var position);
            var definition = new VfxObjectDefinition { DisplayName = "Furnishing effect", VfxGamePath = e.Path, Position = position, Scale = scale, Color = e.Color };
            definition.RotationQuaternion = rotation; stage.Objects[e.Key] = definition;
        }
        var soundStages = new Dictionary<string, string>(StringComparer.Ordinal);
        var heard = AmbientBudget.Sounds(audio);
        // World groups' always-playing sounds: only for Haven's own sounds (SoundList), never on Stagehand's stages. They
        // share the budget with the furnishings' (merged and capped together), at the piece's own volume (since 0.67).
        List<AmbientEmitter> worldAudio = [];
        if (sounds)
            foreach (var item in house.Furnishings.Where(x => x.BuildingPart != null))
            {
                var prefab = Prefab(item);
                if (prefab.WorldSounds.Length == 0) continue;
                var world = ItemMatrix(item) * parent; var id = item.Id.ToString("N");
                for (var i = 0; i < prefab.WorldSounds.Length; i++) worldAudio.Add(new($"ws-{id}-{i}", prefab.WorldSounds[i].Path, prefab.WorldSounds[i].Transform * world, Vector4.One, item.SoundVolume ?? 1));
            }
        var heardAll = worldAudio.Count == 0 ? heard : AmbientBudget.Sounds([.. audio, .. worldAudio]);
        foreach (var s in heard)
        {
            var single = new StageDefinition { FormatVersion = StageDefinition.CurrentFormatVersion };
            // A changed volume changes the definition, so the sound is recreated rather than updated in place.
            single.Objects["sound"] = new SoundObjectDefinition { DisplayName = "Furnishing sound", SoundGamePath = s.Path, Position = s.World.Translation, IsPositional = true, Volume = s.Volume };
            soundStages[s.Key] = single.ToDefinitionString();
        }
        return new(stage.Objects.Count == 0 ? null : stage.ToDefinitionString(), soundStages) { EffectList = withWorld, SoundList = heardAll };
    }
    // A looping sound its side never stops is held with the switch; everything else is a one-off cue on the change.
    public bool Held(PrefabSwitchSound sound) => sound.StopFrame == null && soundLoops.GetOrAdd(sound.Path, path =>
    {
        try { return readSound(path) is {} scd && Enumerable.Range(0, scd.AudioDataCount).Any(i => scd.GetAudio(i).AudioBasicDesc is var a && a.LoopEnd > a.LoopStart); }
        catch (Exception) { return false; }
    });
    // A sound's natural length, read from its installed SCD; null when its format or arrangement is not readable.
    public long? SoundMilliseconds(string path) => soundLengths.GetOrAdd(path, p =>
    {
        try { return readSound(p) is {} scd ? ScdDurationReader.Read(scd).Milliseconds : null; }
        catch (Exception) { return null; }
    });
    // One create-once stage per cue, like the ambient sounds: Stagehand's in-place sound update silences them.
    public static string CueStage(string path, Vector3 position, float volume)
    {
        var stage = new StageDefinition { FormatVersion = StageDefinition.CurrentFormatVersion };
        stage.Objects["sound"] = new SoundObjectDefinition { DisplayName = "Furnishing sound cue", SoundGamePath = path,
            Position = position, IsPositional = true, Volume = volume };
        return stage.ToDefinitionString();
    }
    // Stagehand's light carries the render light's own fields, so each takes what the client derives from the prefab
    // (FurnitureLighting.Render): shape, colour at intensity 1, curve, factor, reach, cone, projected texture and the
    // authored specular and shadow switches. A line light, which Stagehand lacks, shines as a point.
    private static LightDefinition LightOf(string name, LayoutLight authored, Vector3 position, Quaternion rotation)
    {
        var rendered = FurnitureLighting.Render(authored);
        var definition = new LightDefinition
        {
            DisplayName = name + " light", Position = position,
            Shape = authored.Type switch { 3 => LightShape.Spot, 4 => LightShape.Flat, _ => LightShape.Point },
            Color = rendered.Color, Intensity = 1,
            FalloffFunction = rendered.Curve switch { LightCurve.Linear => LightFalloffFunction.Linear, LightCurve.Quadratic => LightFalloffFunction.Quadratic, _ => LightFalloffFunction.Cubic },
            FalloffFactor = rendered.FalloffFactor, Range = rendered.Range,
            SpotLightAngleDegrees = authored.ConeDegrees, AngularFalloffDegrees = authored.ConeFalloffDegrees,
            FlatLightSkewAngleDegrees = authored.PlaneRotation, ProjectedTextureGamePath = authored.Texture ?? "",
            EnableSpecularHighlights = authored.Specular, EnableDynamicShadows = false,
            EnableObjectShadows = authored.ObjectShadows, EnableCharacterShadows = authored.CharacterShadows,
            ShadowPlaneNear = rendered.ShadowNear, ShadowPlaneFar = MathF.Max(rendered.Range, .1f), CharacterShadowRange = rendered.Range,
        };
        definition.RotationQuaternion = rotation;
        return definition;
    }
    // An imported stage's lights, bounded as it is drawn (SceneLights): beyond the first MaxImported they are left out.
    private static void LimitImportedLights(Dictionary<string, ObjectDefinition> objects, ref int count)
    {
        foreach (var (key, value) in objects.ToArray())
        {
            if (value is GroupDefinition group) LimitImportedLights(group.Objects, ref count);
            else if (value is LightDefinition light)
            {
                if (++count > SceneLights.MaxImported) { objects.Remove(key); continue; }
                light.Range = SceneLights.Range(light.Range);
                light.Intensity = SceneLights.Intensity(light.Intensity);
                light.ShadowPlaneFar = SceneLights.ShadowRange(light.ShadowPlaneFar);
                light.CharacterShadowRange = SceneLights.ShadowRange(light.CharacterShadowRange);
            }
        }
    }
    private static int Count(IEnumerable<ObjectDefinition> objects) => objects.Sum(x => 1 + (x is GroupDefinition g ? Count(g.Objects.Values) : 0));
    private void Add(StageDefinition stage, Furnishing item, bool ghost, bool includeModels, Func<Furnishing, bool>? switchOn, bool havenLights = false)
    {
        var entry = Get(item); var prefab = Prefab(item);
        var on = prefab.Switch == null || (switchOn?.Invoke(item) ?? prefab.Switch.DefaultOn);
        var group = new GroupDefinition { DisplayName = entry.Name, Position = item.Translation, RotationPitchYawRollDegrees = new(0, item.Rotation, 0), Scale = new(item.Scale) };
        // A tilted (or portrait) piece's lights turn with it: the group takes the whole rotation.
        if (item.Tilted || item.Portrait)
        {
            Matrix4x4.Decompose(ItemMatrix(item), out _, out var turned, out _);
            group.RotationQuaternion = turned;
        }
        var dye = Dye(item.StainId);
        for (var i = 0; includeModels && i < prefab.Models.Length; i++)
        {
            var model = prefab.Models[i];
            if (!PrefabState.Shown(model.Switch, on)) continue;
            Matrix4x4.Decompose(model.Transform, out var scale, out var rotation, out var position);
            var definition = new BgObjectDefinition { DisplayName = entry.Name, ModelGamePath = model.Path, Position = position, Scale = scale,
                DyeColor = dye, Opacity = ghost ? 0.55f : 1 };
            definition.RotationQuaternion = rotation;
            group.Objects["part-" + i] = definition;
        }
        if (!ghost && !havenLights)
            for (var i = 0; i < prefab.Lights.Length; i++)
            {
                var light = prefab.Lights[i];
                if (!PrefabState.Shown(light.Switch, on)) continue;
                Matrix4x4.Decompose(light.Transform, out _, out var rotation, out var position);
                group.Objects["light-" + i] = LightOf(entry.Name, light.Light, position, rotation);
            }
        stage.Objects[(ghost ? "haven-ghost-" : "haven-item-") + item.Id.ToString("N")] = group;
    }
}
