using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using Lumina.Data.Parsing.Layer;

namespace Haven;

// Inn: an inn room, whose own pieces remodel: removed, or moved as building pieces of their own (never listed to place).
internal sealed record BuildingShell(uint Territory, string Code, string Name, string Background, bool Inn = false);
internal sealed record BuildingFixture(string Name, ushort Icon, string Path, string Category = "Lighting", string Source = "Housing lighting", byte DyeSlots = 0);
internal sealed record BuildingPart(FurnitureEntry Entry, FurniturePrefab Prefab, string Source, string[] Materials);
/// <summary>
/// A piece of a preset home's own shell: its bg.lgb layout instance, its building part, where it
/// stands (World) and its bounds in its own space. Label names it ("Pillar or beam"); Category as the building catalogue.
/// </summary>
internal sealed record ShellPieceInfo(uint Instance, string Path, string Label, string Category, Matrix4x4 World, Vector3 Minimum, Vector3 Maximum, bool Collision)
{
    /// <summary>An inn piece's models in its own space (a group's members), picked by their triangles; null for housing pieces.</summary>
    public IReadOnlyList<(string Path, Matrix4x4 Local)>? Models { get; init; }
    /// <summary>
    /// The layout lights and collision boxes that go with an inn piece (a lamp's light, a bed's box): darkened and made
    /// passable with it. Inn rooms keep them as instances of their own, beside the piece.
    /// </summary>
    public IReadOnlyList<uint>? Attached { get; init; }
}

// Only assets actually referenced by installed residential layouts enter this catalogue.
// Environment/light-mask geometry and event logic are deliberately not building parts.
internal sealed class BuildingCatalogue
{
    // Supplement the native room assemblies with existing small construction modules.
    internal static readonly (uint ItemId, string Category)[] ModularPieces = [
        (21106, "Modular walls"), (21107, "Modular walls"), (21108, "Modular walls"), (21109, "Modular walls"), (48134, "Modular walls"),
        (24514, "Modular walls"), (27281, "Modular walls"), (37366, "Modular walls"), (17974, "Modular walls"),
        (24511, "Modular floors"), (35562, "Modular floors"), (37360, "Modular floors"), (24512, "Modular steps"), (27273, "Arches") ];
    private readonly Func<string, MdlFile?> readModel;
    private readonly FurniturePrefabReader prefabs;
    private readonly Func<string, bool> exists;
    public BuildingPart[] Parts { get; }
    public FurnitureEntry[] Items { get; }
    public string[] Categories { get; }
    public int ModelFilesRead { get; }
    private readonly Dictionary<string, BuildingPart> byPath;
    private readonly Dictionary<uint, ShellPieceInfo[]> shellPieces = [];
    /// <summary>The pieces of a preset home's shell that remodelling may hide or copy; empty for any other territory.</summary>
    public IReadOnlyList<ShellPieceInfo> ShellPieces(uint territory) => shellPieces.GetValueOrDefault(territory) ?? [];
    private readonly Dictionary<uint, List<uint>> companyDoors = [];
    /// <summary>A preset home's free company door to the additional chambers, by instance: never part of a Haven home.</summary>
    public IReadOnlyList<uint> CompanyDoors(uint territory) => companyDoors.GetValueOrDefault(territory) ?? [];

    /// <param name="eventObjectModel">An EObj row's own group (ExportedSG), for an inn object whose model it brings (The
    /// Unending Journey's book); null when it has none.</param>
    public BuildingCatalogue(IEnumerable<BuildingShell> shells, Func<string, LgbFile?> readLayout,
        Func<string, SgbFile?> readGroup, Func<string, MdlFile?> readModel, Func<string, bool> exists, IEnumerable<BuildingFixture>? fixtures = null,
        Func<uint, string?>? eventObjectModel = null)
    {
        Dictionary<string, MdlFile?> modelCache = new(StringComparer.Ordinal);
        MdlFile? ReadCached(string path)
        {
            if (!modelCache.TryGetValue(path, out var model)) modelCache[path] = model = readModel(path);
            return model;
        }
        this.readModel = ReadCached; this.exists = exists;
        prefabs = new(readGroup, ReadCached, exists);
        // World pieces are read after the catalogue is built, one at a time and never cached here (there are a hundred
        // thousand of them), so through the plain readers.
        worldModel = readModel;
        worldPrefabs = new(() => new FurniturePrefabReader(readGroup, readModel, exists));
        Dictionary<string, (string Category, string Label, string Source, LayerCommon.BGInstanceObject? Bg)> found = [];
        List<(uint Territory, uint Instance, string Path, Matrix4x4 World, bool Collision, string? Category, string? Label)> placed = [];
        Dictionary<string, (Vector3 Min, Vector3 Max)> rawBounds = new(StringComparer.Ordinal);
        Dictionary<string, List<(string Path, Matrix4x4 Local)>> innModels = new(StringComparer.Ordinal);
        Dictionary<(uint Territory, uint Instance), List<uint>> innAttached = [];
        Dictionary<string, (string Label, string Source, LayerCommon.BGInstanceObject? Bg)> innSources = new(StringComparer.Ordinal);
        foreach (var shell in shells.OrderBy(x => x.Territory))
        {
            if (shell.Inn) { ReadInn(shell, readLayout, readGroup, ReadCached, eventObjectModel, placed, rawBounds, innModels, innAttached, innSources); continue; }
            var path = "bg/" + shell.Background[..(shell.Background.LastIndexOf('/') + 1)] + "bg.lgb";
            var layout = readLayout(path) ?? throw new InvalidDataException("Housing building layout unavailable: " + shell.Name);
            foreach (var layer in layout.Layers)
            foreach (var obj in layer.InstanceObjects)
            {
                var t = obj.Transform;
                var world = Haven.Core.LayoutRotation.Transform(new(t.Translation.X, t.Translation.Y, t.Translation.Z),
                    new(t.Rotation.X, t.Rotation.Y, t.Rotation.Z), new(t.Scale.X, t.Scale.Y, t.Scale.Z));
                if (obj.Object is LayerCommon.BGInstanceObject bg && IsStructure(bg.AssetPath))
                {
                    var category = Category(bg.AssetPath);
                    found.TryAdd(bg.AssetPath, (category, Label(bg.AssetPath, category), shell.Name, bg));
                    placed.Add((shell.Territory, obj.InstanceId, bg.AssetPath, world, bg.CollisionType != ModelCollisionType.None, null, null));
                }
                // The light guard: the brown hull around the whole house. Not a building part, but the remodel may remove it.
                else if (obj.Object is LayerCommon.BGInstanceObject hull && IsLightGuard(hull.AssetPath) && ReadCached(hull.AssetPath) is { } hullModel)
                {
                    var bb = hullModel.ModelBoundingBoxes;
                    rawBounds[hull.AssetPath] = (new(bb.Min[0], bb.Min[1], bb.Min[2]), new(bb.Max[0], bb.Max[1], bb.Max[2]));
                    placed.Add((shell.Territory, obj.InstanceId, hull.AssetPath, world, false, OuterShell, null));
                }
                else if (obj.Object is LayerCommon.SharedGroupInstanceObject group && IsBuildingGroup(group.AssetPath))
                {
                    var category = Category(group.AssetPath);
                    found.TryAdd(group.AssetPath, (category, Label(group.AssetPath, category), shell.Name, null));
                    // A free company's door to the additional chambers: the game shows it only in a company's house (and
                    // when a Haven home is entered from real housing), so a Haven home always hides it, and it isn't there
                    // to remodel (it stays a building part to place).
                    if (!IsCompanyDoor(group.AssetPath)) placed.Add((shell.Territory, obj.InstanceId, group.AssetPath, world, false, null, null));
                    else (companyDoors.TryGetValue(shell.Territory, out var doors) ? doors : companyDoors[shell.Territory] = []).Add(obj.InstanceId);
                }
            }
        }
        var fixtureIcons = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var fixtureDyes = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var fixture in fixtures ?? [])
        {
            // A fixture missing after a game patch is left out; it must never cost the rest of the catalogue.
            if (!exists(fixture.Path)) { partFailures.Add($"{fixture.Path}: housing light fixture unavailable ({fixture.Name})"); continue; }
            found[fixture.Path] = (fixture.Category, fixture.Name, fixture.Source, null);
            fixtureIcons[fixture.Path] = fixture.Icon;
            fixtureDyes[fixture.Path] = fixture.DyeSlots;
        }
        List<BuildingPart> result = [];
        foreach (var (path, source) in found.OrderBy(x => x.Key, StringComparer.Ordinal))
        try
        {
            var prefab = source.Bg is {} bg ? Model(bg) : prefabs.GetBuildingGroup(path);
            if (Extent(prefab) is { } extent) rawBounds[path] = extent;
            var materials = prefab.Models.SelectMany(m => MaterialPaths(ReadCached(m.Path)!)).Distinct().ToArray();
            prefab = prefab with { Models = prefab.Models.Select(m => m with { FinishPart = SurfacePart(ReadCached(m.Path)!) }).ToArray() };
            var wall = prefab.Models.Any(x => x.FinishPart == 0);
            var floor = prefab.Models.Any(x => x.FinishPart == 3);
            // The native background buffer supports one replaceable material per draw object.
            uint? finishPart = wall != floor ? wall ? 0u : 3u : null;
            var entry = new FurnitureEntry(0, 0, 0, fixtureIcons.GetValueOrDefault(path), source.Label, source.Category,
                source.Source + " · Housing building piece", 0, fixtureDyes.GetValueOrDefault(path), path, finishPart);
            result.Add(new(entry, Normalize(prefab), source.Source, materials));
        }
        // A part whose files don't read after a game patch is left out (homes using it are refused as before); one bad
        // part must never cost the whole catalogue, which every home with building pieces needs.
        catch (Exception ex) when (ex is not OutOfMemoryException) { partFailures.Add($"{path}: {ex.Message}"); }
        Parts = result.OrderBy(x => x.Entry.Name).ToArray();
        Items = Parts.Select(x => x.Entry).ToArray(); Categories = Items.Select(x => x.Category).Distinct().Order().ToArray();
        byPath = Parts.ToDictionary(x => x.Entry.BuildingPart!, StringComparer.Ordinal);
        // An inn room's pieces become building parts too, so one can be moved like a house's (a moved piece is a building
        // piece standing in for it), but they aren't offered to place. A piece whose files don't read stays remove-only.
        foreach (var (path, source) in innSources)
        {
            if (byPath.ContainsKey(path) || !Haven.Core.BuildingParts.IsHousingAsset(path)) continue;
            try
            {
                var prefab = source.Bg is {} bg ? Model(bg) : prefabs.GetBuildingGroup(path, innRoom: true);
                if (Extent(prefab) is not { } extent) continue;
                rawBounds[path] = extent;
                var entry = new FurnitureEntry(0, 0, 0, 0, source.Label, InnRoom, source.Source + " · Inn room piece", 0, 0, path, null);
                byPath[path] = new(entry, Normalize(prefab), source.Source, []);
            }
            // Any failure keeps that piece remove-only; it must never cost the rest of the catalogue.
            catch (Exception ex) { innFailures.Add($"{path}: {ex.Message}"); }
        }
        foreach (var shell in placed.GroupBy(x => x.Territory))
            shellPieces[shell.Key] = [.. shell.Where(x => rawBounds.ContainsKey(x.Path) && (x.Category != null || byPath.ContainsKey(x.Path))).Select(x =>
            {
                var category = x.Category ?? byPath[x.Path].Entry.Category;
                return new ShellPieceInfo(x.Instance, x.Path, x.Label ?? PieceName(category), category, x.World, rawBounds[x.Path].Min, rawBounds[x.Path].Max, x.Collision)
                    { Models = innModels.GetValueOrDefault(x.Path), Attached = innAttached.GetValueOrDefault((x.Territory, x.Instance)) };
            })];
        ModelFilesRead = modelCache.Count;
        modelCache.Clear(); // Keep paths/bounds/transforms, not copied model-file payloads.
    }

    public BuildingPart Get(string path) => byPath.GetValueOrDefault(path)
        ?? throw new InvalidDataException("This building part is not available in the installed housing layouts.");
    private readonly List<string> innFailures = [], partFailures = [];
    /// <summary>The housing parts and fixtures left out because their files didn't read, with why.</summary>
    public IReadOnlyList<string> PartFailures => partFailures;
    /// <summary>The inn pieces whose files didn't read as building parts (so they can only be removed), with why.</summary>
    public IReadOnlyList<string> InnFailures => innFailures;
    /// <summary>Whether a shell piece can become a building piece (be moved); an inn piece whose files didn't read can't.</summary>
    public bool CanBuild(string path) => byPath.ContainsKey(path);

    private readonly Func<string, MdlFile?> worldModel;
    private readonly Lazy<FurniturePrefabReader> worldPrefabs;
    /// <summary>The files a world piece needs, by expansion: a piece of Endwalker or Dawntrail isn't drawn by a game
    /// without them (a free trial), since a model the game can't find closes it.</summary>
    private const int CheckedExpansion = 1;
    /// <summary>The most models one world piece may draw (a home's whole budget is 4,096 objects).</summary>
    public const int MaxWorldModels = 512;

    /// <summary>
    /// A world piece as a building part: a model with the collision its placements in the world
    /// give it (its box, or its collision mesh; none if it has none: nothing is invented), or a zone group with its
    /// members, as an inn group is read. Throws when its files don't read, or when this game lacks any file it draws
    /// (models, materials and textures outside the base game are each looked up first through <paramref name="read"/>).
    /// </summary>
    public BuildingPart World(Haven.Core.World.WorldPiece piece, Haven.Core.World.WorldName name, string zone, Func<string, byte[]?> read)
    {
        FurniturePrefab prefab;
        if (piece.Group) prefab = worldPrefabs.Value.GetWorldGroup(piece.Path, MaxWorldModels);
        else
        {
            if (!exists(piece.Path)) throw new InvalidDataException("this game doesn't have the model.");
            var mdl = worldModel(piece.Path) ?? throw new InvalidDataException("the model doesn't read.");
            var (min, max) = FurniturePrefabReader.WorldBounds(mdl);
            PrefabCollider[] colliders = piece.Collision switch
            {
                Haven.Core.World.WorldCollision.Box => [new(null, Matrix4x4.Identity, min, max)],
                Haven.Core.World.WorldCollision.Mesh when piece.CollisionPath is { } collision && exists(collision) => [new(collision, Matrix4x4.Identity, min, max)],
                _ => [],
            };
            prefab = new([new(piece.Path, Matrix4x4.Identity, min, max)], colliders, [], []);
        }
        if (prefab.Models.Length == 0) throw new InvalidDataException("it draws nothing.");
        // A zone group can hold a whole street; one piece may take only part of a home's scene budget.
        if (prefab.Models.Length > MaxWorldModels) throw new InvalidDataException($"it's {prefab.Models.Length} models, more than one piece may be ({MaxWorldModels}).");
        // Every file outside the base game must be here before the game is ever asked for it.
        var materials = new List<string>();
        foreach (var model in prefab.Models.Select(m => m.Path).Distinct())
        {
            if (!exists(model)) throw new InvalidDataException("this game doesn't have " + model + ".");
            if (worldModel(model) is { } file) materials.AddRange(MaterialPaths(file));
        }
        foreach (var material in materials.Distinct())
        {
            if (Haven.Core.GameDataPaths.Expansion(material) < CheckedExpansion) continue;
            if (read(material) is not { } mtrl) throw new InvalidDataException($"it needs {Haven.Core.GameDataPaths.ExpansionName(Haven.Core.GameDataPaths.Expansion(material))}'s game data, which this game doesn't have.");
            foreach (var texture in Haven.Core.Furniture.FurnitureFiles.MaterialTextures(mtrl))
                if (Haven.Core.GameDataPaths.Expansion(texture) >= CheckedExpansion && !exists(texture))
                    throw new InvalidDataException($"it needs {Haven.Core.GameDataPaths.ExpansionName(Haven.Core.GameDataPaths.Expansion(texture))}'s game data, which this game doesn't have.");
        }
        var entry = new FurnitureEntry(0, 0, 0, 0, name.Name, name.Category, zone, 0, 0, piece.Path);
        // A group's lights aren't drawn through Stagehand (new features don't lean on it): they're kept apart, for Haven's
        // own lights when those are on, each normalised with the group.
        var normal = Normalize(prefab);
        // Its effects likewise (Haven's own effects only); one outside the base game is kept only when every texture it
        // names is installed (an effect file this game has can still name another expansion's texture).
        bool Installed(PrefabEffect effect)
        {
            if (Haven.Core.GameDataPaths.Expansion(effect.Path) < CheckedExpansion) return true;
            try { return read(effect.Path) is { } avfx && Haven.Core.Animation.AvfxFiles.Textures(avfx).All(t => Haven.Core.GameDataPaths.Expansion(t) < CheckedExpansion || exists(t)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }   // an effect that doesn't read is left out, not the piece
        }
        var effects = normal.Effects.Where(Installed).ToArray();
        // Its sounds likewise (each one's file was checked to exist when read).
        return new(entry, normal with { Lights = [], WorldLights = normal.Lights, Effects = [], WorldEffects = effects, Sounds = [], WorldSounds = normal.Sounds }, zone, [.. materials.Distinct()]);
    }

    public FurniturePrefab WithFinish(string path, uint kind, string material)
    {
        var part = Get(path);
        var prefix = kind == 0 ? "bgcommon/hou/dyna/mat/wl/" : kind == 3 ? "bgcommon/hou/dyna/mat/fl/" : "";
        if (part.Entry.FinishPart != kind || prefix.Length == 0 || !material.StartsWith(prefix, StringComparison.Ordinal) || !exists(material))
            throw new InvalidDataException("The surface finish does not match this building piece.");
        return part.Prefab with { Models = part.Prefab.Models.Select(x => x with { Material = x.FinishPart == kind ? material : null }).ToArray() };
    }

    private FurniturePrefab Model(LayerCommon.BGInstanceObject bg)
    {
        var mdl = readModel(bg.AssetPath) ?? throw new InvalidDataException("Building model unavailable: " + bg.AssetPath);
        var bb = mdl.ModelBoundingBoxes;
        var min = new Vector3(bb.Min[0], bb.Min[1], bb.Min[2]); var max = new Vector3(bb.Max[0], bb.Max[1], bb.Max[2]);
        PrefabCollider[] colliders = bg.CollisionType switch
        {
            ModelCollisionType.Box => [new(null, Matrix4x4.Identity, min, max)],
            ModelCollisionType.Replace when !string.IsNullOrEmpty(bg.CollisionAssetPath) && exists(bg.CollisionAssetPath)
                => [new(bg.CollisionAssetPath, Matrix4x4.Identity, min, max)],
            _ => []
        };
        return new([new(bg.AssetPath, Matrix4x4.Identity, min, max)], colliders, [], []);
    }

    // A prefab's bounds in its own space (before Normalize), or null without geometry.
    private static (Vector3 Min, Vector3 Max)? Extent(FurniturePrefab prefab)
    {
        var corners = prefab.Models.SelectMany(m => new[] {
            new Vector3(m.Minimum.X,m.Minimum.Y,m.Minimum.Z), new(m.Maximum.X,m.Minimum.Y,m.Minimum.Z), new(m.Minimum.X,m.Maximum.Y,m.Minimum.Z),
            new(m.Minimum.X,m.Minimum.Y,m.Maximum.Z), new(m.Maximum.X,m.Maximum.Y,m.Minimum.Z), new(m.Maximum.X,m.Minimum.Y,m.Maximum.Z),
            new(m.Minimum.X,m.Maximum.Y,m.Maximum.Z), new(m.Maximum.X,m.Maximum.Y,m.Maximum.Z)
        }.Select(v => Vector3.Transform(v, m.Transform))).ToArray();
        return corners.Length == 0 ? null : (corners.Aggregate(Vector3.Min), corners.Aggregate(Vector3.Max));
    }

    // A shell piece's name in the remodel panel, from its building category.
    private static string PieceName(string category) => category switch
    {
        "Pillars" => "Pillar or beam", "Walls" => "Walls (this whole floor)", "Floors" => "Floor (this whole floor)",
        "Ceilings & room sections" => "Ceiling and structure (this whole floor)", "Stairs" => "Stairs", "Doors" => "Door",
        "Windows" => "Window", "Lighting" => "Ceiling light", OuterShell => "Outer shell (around the whole house)", _ => "Terrace or trim",
    };

    private static FurniturePrefab Normalize(FurniturePrefab prefab)
    {
        var bounds = prefab.Models.SelectMany(m => new[] {
            new Vector3(m.Minimum.X,m.Minimum.Y,m.Minimum.Z), new(m.Maximum.X,m.Minimum.Y,m.Minimum.Z),
            new(m.Minimum.X,m.Maximum.Y,m.Minimum.Z), new(m.Minimum.X,m.Minimum.Y,m.Maximum.Z),
            new(m.Maximum.X,m.Maximum.Y,m.Minimum.Z), new(m.Maximum.X,m.Minimum.Y,m.Maximum.Z),
            new(m.Minimum.X,m.Maximum.Y,m.Maximum.Z), new(m.Maximum.X,m.Maximum.Y,m.Maximum.Z)
        }.Select(v => Vector3.Transform(v, m.Transform))).ToArray();
        if (bounds.Length == 0) throw new InvalidDataException("Building part has no visible geometry.");
        var min = bounds.Aggregate(Vector3.Min); var max = bounds.Aggregate(Vector3.Max);
        var offset = Matrix4x4.CreateTranslation(-(min.X + max.X) / 2, -min.Y, -(min.Z + max.Z) / 2);
        return prefab with { Models = prefab.Models.Select(x => x with { Transform = x.Transform * offset,
                Door = x.Door is {} door ? door with { Parent = door.Parent * offset } : null,
                Clock = x.Clock is {} clock ? clock with { Parent = clock.Parent * offset } : null,
                Loop = x.Loop is {} loop ? [new Haven.Core.LoopLevel(offset, null), .. loop] : null }).ToArray(),
            Colliders = prefab.Colliders.Select(x => x with { Transform = x.Transform * offset }).ToArray(),
            Doors = prefab.Doors.Select(x => x with { Trigger = x.Trigger with { Transform = x.Trigger.Transform * offset } }).ToArray(),
            Lights = prefab.Lights.Select(x => x with { Transform = x.Transform * offset }).ToArray(),
            Effects = prefab.Effects.Select(x => x with { Transform = x.Transform * offset }).ToArray(),
            Sounds = prefab.Sounds.Select(x => x with { Transform = x.Transform * offset }).ToArray(), Seats = [] };
    }

    internal static string[] MaterialPaths(MdlFile mdl) => mdl.MaterialNameOffsets.Select(offset =>
    {
        var start = checked((int)offset); var end = Array.IndexOf(mdl.Strings, (byte)0, start);
        return System.Text.Encoding.UTF8.GetString(mdl.Strings, start, (end < 0 ? mdl.Strings.Length : end) - start);
    }).ToArray();

    private static uint? SurfacePart(MdlFile mdl)
    {
        var materials = MaterialPaths(mdl); var index = mdl.ModelHeader.BGChangeMaterialIndex;
        if (index >= materials.Length) return null;
        var material = materials[index];
        return material.StartsWith("bgcommon/hou/dyna/mat/wl/", StringComparison.Ordinal) ? 0u
            : material.StartsWith("bgcommon/hou/dyna/mat/fl/", StringComparison.Ordinal) ? 3u : null;
    }

    /// <summary>The category of a shell's light guard, the hull around the whole house (a shell piece, never a building part).</summary>
    public const string OuterShell = "Outer shell";
    /// <summary>The category of an inn room's own pieces (shell pieces to remove or move; never offered to place).</summary>
    public const string InnRoom = "Inn room";
    // How far from the middle of an inn room's event objects and arrival points its pieces reach. The For'ard Cabins'
    // layout also holds the whole town outside its windows; every room's own pieces lie within about 16 m.
    private const float InnReach = 20;

    // An inn room's pieces: every model and group in its bg.lgb, the groups in its planmap.lgb (the armoire's, the
    // dresser's) and the models its event objects bring (The Unending Journey's book), within reach of the room. Each is
    // named from its file ("Chair"), or from the event object it holds ("Bed"); bounds come from its models. The room's
    // lights and collision boxes go with the piece they belong to: a light within reach of a lamp, a box inside a piece.
    // A piece whose files can't be read is left out.
    private static void ReadInn(BuildingShell shell, Func<string, LgbFile?> readLayout, Func<string, SgbFile?> readGroup, Func<string, MdlFile?> readCached,
        Func<uint, string?>? eventObjectModel,
        List<(uint Territory, uint Instance, string Path, Matrix4x4 World, bool Collision, string? Category, string? Label)> placed,
        Dictionary<string, (Vector3 Min, Vector3 Max)> rawBounds, Dictionary<string, List<(string Path, Matrix4x4 Local)>> innModels,
        Dictionary<(uint Territory, uint Instance), List<uint>> attached,
        Dictionary<string, (string Label, string Source, LayerCommon.BGInstanceObject? Bg)> sources)
    {
        var level = "bg/" + shell.Background[..(shell.Background.LastIndexOf('/') + 1)];
        static Vector3 At(LayerCommon.InstanceObject o) => new(o.Transform.Translation.X, o.Transform.Translation.Y, o.Transform.Translation.Z);
        static Matrix4x4 Place(LayerCommon.InstanceObject o) => Haven.Core.LayoutRotation.Transform(At(o),
            new(o.Transform.Rotation.X, o.Transform.Rotation.Y, o.Transform.Rotation.Z), new(o.Transform.Scale.X, o.Transform.Scale.Y, o.Transform.Scale.Z));
        var plans = new[] { "planevent.lgb", "planmap.lgb" }.Select(f => readLayout(level + f)).OfType<LgbFile>()
            .SelectMany(l => l.Layers).SelectMany(l => l.InstanceObjects).ToArray();
        var marks = plans.Where(o => o.AssetType is LayerEntryType.PopRange or LayerEntryType.EventObject).Select(At).ToArray();
        if (marks.Length == 0) return;
        var middle = marks.Aggregate(Vector3.Zero, (a, b) => a + b) / marks.Length;
        bool Near(LayerCommon.InstanceObject o) => Vector3.Distance(At(o), middle) <= InnReach;

        // The models a path draws, read once: a model, or a group's members.
        bool Read(string path)
        {
            if (rawBounds.ContainsKey(path)) return true;
            try
            {
                List<(string Path, Matrix4x4 Local)> models = path.EndsWith(".mdl", StringComparison.Ordinal) ? [(path, Matrix4x4.Identity)] : [];
                if (models.Count == 0) GroupModels(path, Matrix4x4.Identity, readGroup, models, 0);
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                foreach (var (model, local) in models)
                {
                    if (readCached(model) is not { } modelFile) continue;
                    var bb = modelFile.ModelBoundingBoxes;
                    var box = Haven.Core.PlacementBounds.Transform(new(bb.Min[0], bb.Min[1], bb.Min[2]), new(bb.Max[0], bb.Max[1], bb.Max[2]), local);
                    min = Vector3.Min(min, box.Minimum); max = Vector3.Max(max, box.Maximum);
                }
                if (min.X > max.X) return false;
                rawBounds[path] = (min, max);
                innModels[path] = models;
                return true;
            }
            catch (Exception) { return false; }
        }

        var pieces = new List<(uint Instance, string Path, Matrix4x4 World, bool Collision, string Label, Haven.Core.PlacementBounds Box)>();
        var bgModels = new Dictionary<string, LayerCommon.BGInstanceObject>(StringComparer.Ordinal);
        void Add(uint instance, string path, Matrix4x4 world, bool collision, string? label = null, LayerCommon.BGInstanceObject? bg = null)
        {
            if (!Read(path)) return;
            var (min, max) = rawBounds[path];
            pieces.Add((instance, path, world, collision, label ?? InnPieceName(path, max - min), Haven.Core.PlacementBounds.Transform(min, max, world)));
            if (bg is { } model) bgModels.TryAdd(path, model);
        }
        var lights = new List<(uint Instance, Vector3 At)>();
        var boxes = new List<(uint Instance, Vector3 At)>();
        foreach (var file in new[] { "bg.lgb", "planmap.lgb" })
        {
            if (readLayout(level + file) is not { } layout) continue;
            foreach (var obj in layout.Layers.SelectMany(l => l.InstanceObjects))
            {
                if (!Near(obj)) continue;
                if (obj.Object is LayerCommon.BGInstanceObject bg && file == "bg.lgb") Add(obj.InstanceId, bg.AssetPath, Place(obj), bg.CollisionType != ModelCollisionType.None, bg: bg);
                else if (obj.Object is LayerCommon.SharedGroupInstanceObject group) Add(obj.InstanceId, group.AssetPath, Place(obj), true);
                else if (obj.AssetType == LayerEntryType.LayLight) lights.Add((obj.InstanceId, At(obj)));
                else if (obj.AssetType == LayerEntryType.CollisionBox) boxes.Add((obj.InstanceId, At(obj)));
            }
        }
        // Event objects: the model one brings is a piece of its own (keyed by the object's layout id), and the piece
        // holding one is named after it.
        foreach (var obj in plans)
        {
            if (obj.Object is not LayerCommon.EventInstanceObject eobj || !Near(obj)) continue;
            var name = InnObjectName(eobj.ParentData.BaseId);
            if (eventObjectModel?.Invoke(eobj.ParentData.BaseId) is { Length: > 0 } model) { Add(obj.InstanceId, model, Place(obj), false, name ?? "Furnishing"); continue; }
            if (name == null) continue;
            var at = At(obj);
            var holder = pieces.Select((p, index) => (p, index)).Where(x => Inside(x.p.Box, at, .3f) && Size(x.p.Box) <= 4.5f)
                .OrderBy(x => Size(x.p.Box)).FirstOrDefault();
            if (holder.p.Path != null) pieces[holder.index] = holder.p with { Label = name };
        }
        // A light goes with the nearest lamp within reach of it; a collision box with the smallest piece around it.
        foreach (var (light, at) in lights)
        {
            var lamp = pieces.Where(p => p.Label == "Lamp" && Distance(p.Box, at) <= 1.2f).OrderBy(p => Distance(p.Box, at)).FirstOrDefault();
            if (lamp.Path != null) (attached.TryGetValue((shell.Territory, lamp.Instance), out var list) ? list : attached[(shell.Territory, lamp.Instance)] = []).Add(light);
        }
        foreach (var (box, at) in boxes)
        {
            var holder = pieces.Where(p => Inside(p.Box, at, .5f)).OrderBy(p => Size(p.Box)).FirstOrDefault();
            if (holder.Path != null) (attached.TryGetValue((shell.Territory, holder.Instance), out var list) ? list : attached[(shell.Territory, holder.Instance)] = []).Add(box);
        }
        foreach (var piece in pieces)
        {
            placed.Add((shell.Territory, piece.Instance, piece.Path, piece.World, piece.Collision, InnRoom, piece.Label));
            // A moved piece's building part, named as the piece (the first room's name for a file several rooms use).
            sources.TryAdd(piece.Path, (piece.Label, shell.Name, bgModels.TryGetValue(piece.Path, out var model) ? model : null));
        }
    }

    private static bool Inside(Haven.Core.PlacementBounds box, Vector3 at, float margin) =>
        at.X >= box.Minimum.X - margin && at.Y >= box.Minimum.Y - margin && at.Z >= box.Minimum.Z - margin
        && at.X <= box.Maximum.X + margin && at.Y <= box.Maximum.Y + margin && at.Z <= box.Maximum.Z + margin;
    private static float Distance(Haven.Core.PlacementBounds box, Vector3 at) => Vector3.Distance(at, Vector3.Clamp(at, box.Minimum, box.Maximum));
    private static float Size(Haven.Core.PlacementBounds box) { var d = box.Maximum - box.Minimum; return Math.Max(d.X, Math.Max(d.Y, d.Z)); }

    // The inn's event objects by EObj row (ShellRemodel.ExitMarkers and InnFixtures): what the piece holding one is.
    private static string? InnObjectName(uint row) => row switch
    {
        2001012 or 2001270 or 2000088 or 2005629 or 2010433 => "Bed",
        2007708 => "Tatami bed",
        2001405 or 2001406 or 2001407 or 2005630 or 2007709 => "Armoire",
        2000403 or 2010284 => "Summoning bell",
        2004358 => "Crystal bell",
        2006999 => "Orchestrion",
        2009439 => "Glamour dresser",
        2004359 => "Toy chest",
        2001901 or 2009274 or 2010156 => "The Unending Journey",
        2001010 or 2001011 or 2000087 or 2005628 or 2007707 or 2009759 or 2011853 or 2014367 => "Door",
        _ => null,
    };

    // A group's models (and its nested groups', a few levels down), each with its place in the group's space.
    private static void GroupModels(string path, Matrix4x4 parent, Func<string, SgbFile?> readGroup, List<(string Path, Matrix4x4 Local)> models, int depth)
    {
        if (depth > 3 || readGroup(path) is not { } group) return;
        foreach (var layer in group.LayerGroups.SelectMany(g => g.Layers))
        foreach (var obj in layer.InstanceObjects)
        {
            var t = obj.Transform;
            var local = Haven.Core.LayoutRotation.Transform(new(t.Translation.X, t.Translation.Y, t.Translation.Z),
                new(t.Rotation.X, t.Rotation.Y, t.Rotation.Z), new(t.Scale.X, t.Scale.Y, t.Scale.Z)) * parent;
            if (obj.Object is LayerCommon.BGInstanceObject bg) models.Add((bg.AssetPath, local));
            else if (obj.Object is LayerCommon.SharedGroupInstanceObject nested) GroupModels(nested.AssetPath, local, readGroup, models, depth + 1);
        }
    }

    // An inn piece's name from its file: the letters of its last part ("s1ti_ia_chr2": chr, "Chair"). A part whose code
    // says little ("int", "obj", a world item) is named by its size: big ones are the room's own structure.
    internal static string InnPieceName(string path, Vector3 size)
    {
        var big = Math.Max(size.X, Math.Max(size.Y, size.Z)) >= 2.5f;
        var name = InnCodeName(path);
        return name is "Small item" or "Room piece" ? big ? "Room structure" : "Small item" : name;
    }

    private static string InnCodeName(string path)
    {
        if (path.StartsWith("bgcommon/hou/indoor/general/", StringComparison.Ordinal)) return "Furnishing";
        var file = Path.GetFileNameWithoutExtension(path);
        if (file.StartsWith("w_itm_", StringComparison.Ordinal)) return "Small item";
        var last = file[(file.LastIndexOf('_') + 1)..];
        var code = new string(last.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
        return code switch
        {
            "wal" or "wall" or "wl" => "Wall", "flr" or "floor" or "fl" or "yuka" => "Floor", "win" or "wid" or "mado" => "Window",
            "dor" or "door" => "Door", "lmp" or "lamp" or "lgt" or "light" or "cnd" or "candle" => "Lamp", "chr" or "chair" or "isu" => "Chair",
            "tbl" or "table" => "Table", "dsk" or "desk" => "Desk", "slf" or "shf" or "shelf" or "tana" => "Shelf", "plr" or "pil" or "pillar" or "hasira" => "Pillar",
            "bed" => "Bed", "pot" or "vase" => "Pot", "plt" or "plant" or "flw" => "Plant", "box" or "chest" => "Box", "rug" or "mat" or "carpet" => "Rug",
            "cur" or "ctn" or "curtain" => "Curtain", "sof" or "sofa" => "Sofa", "cab" or "cbt" => "Cabinet", "bok" or "book" => "Books",
            "frm" or "frame" or "pic" => "Picture", "rof" or "roof" or "cel" or "ceil" => "Ceiling", "stp" or "str" or "slp" or "stair" => "Stairs",
            "dec" or "deco" => "Decoration", "mir" or "mirror" => "Mirror", "clk" or "clock" => "Clock", "bsk" => "Basket",
            "rom" or "room" or "hotl" => "Room", "fen" or "fence" => "Railing", "kag" or "kagu" => "Furniture",
            "itm" or "obj" or "int" or "idr" => "Room piece", "lig" or "lit" or "blb" or "rou" => "Lamp", "tab" or "tabl" => "Table", "char" or "sit" => "Seat",
            "bot" or "botl" or "bin" => "Bottle", "dish" or "dsh" or "cup" => "Dishes", "oran" or "appl" or "frut" or "fod" or "bre" => "Food",
            "carp" or "cpt" => "Rug", "fut" => "Futon", "sel" => "Shelf", "hako" or "hak" or "boxs" or "tru" => "Box", "taru" => "Barrel",
            "uek" or "pla" or "plan" or "flow" or "bon" or "pfl" => "Plant", "tre" or "sak" => "Tree", "ston" => "Stone", "yane" => "Roof",
            "shog" => "Screen", "kake" or "kaks" or "gaku" => "Hanging scroll", "yroi" => "Armour", "cov" => "Cover", "ppr" or "let" => "Papers",
            "bag" => "Bag", "kit" => "Kitchen", "sto" => "Stove", "mac" => "Machine", "div" => "Partition", "cry" => "Crystal",
            "wind" => "Window", "gat" => "Gate", "bal" => "Balcony", "sld" => "Shield", "hari" => "Beam",
            _ => "Room piece",
        };
    }
    private static bool IsLightGuard(string path) => path.StartsWith("bg/ffxiv/", StringComparison.Ordinal)
        && path.Contains("/ind/common/bgparts/", StringComparison.Ordinal) && path.EndsWith("_lightgard.mdl", StringComparison.Ordinal);
    private static bool IsCompanyDoor(string path) => path.Contains("/shared/for_", StringComparison.Ordinal) && path.Contains("/sgho_", StringComparison.Ordinal);
    private static bool IsStructure(string path) => path.StartsWith("bg/ffxiv/", StringComparison.Ordinal)
        && path.Contains("/ind/common/bgparts/", StringComparison.Ordinal) && path.EndsWith(".mdl", StringComparison.Ordinal)
        && !path.Contains("lightgard", StringComparison.Ordinal) && !path.Contains("lightguard", StringComparison.Ordinal);
    private static bool IsBuildingGroup(string path) => path.StartsWith("bgcommon/hou/dyna/", StringComparison.Ordinal)
        && (path.Contains("/dor/", StringComparison.Ordinal) || path.Contains("/wid/", StringComparison.Ordinal) || path.Contains("/lmp/", StringComparison.Ordinal))
        && path.EndsWith(".sgb", StringComparison.Ordinal)
        || path.StartsWith("bg/ffxiv/", StringComparison.Ordinal) && path.Contains("/shared/for_hou/", StringComparison.Ordinal)
        && path.Contains("door", StringComparison.Ordinal) && path.EndsWith(".sgb", StringComparison.Ordinal);

    private static string Category(string path) => path.Contains("/dor/") || path.Contains("door") || path.Contains("_dor") ? "Doors"
        : path.Contains("/wid/") || path.Contains("_wid") ? "Windows" : path.Contains("/lmp/") ? "Lighting"
        : path.Contains("_wl_") ? "Walls" : path.Contains("_fl_") ? "Floors"
        : path.Contains("_slp") || path.Contains("_str") || path.Contains("_stp") ? "Stairs"
        : path.Contains("_pil") ? "Pillars" : path.Contains("_rom") ? "Ceilings & room sections" : "Railings & trim";

    private static string Label(string path, string category)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        var district = path.Contains("sea_s1") || path.Contains("/s1h0/") ? "Mist"
            : path.Contains("fst_f1") || path.Contains("/f1h0/") ? "Lavender Beds"
            : path.Contains("wil_w1") || path.Contains("/w1h0/") ? "Goblet"
            : path.Contains("est_e1") || path.Contains("/e1h0/") ? "Shirogane"
            : path.Contains("roc_r1") || path.Contains("/r1h0/") ? "Empyreum" : "Housing";
        var size = System.Text.RegularExpressions.Regex.Match(file, @"i([1-4])_").Groups[1].Value switch
            { "1" => "Cottage ", "2" => "House ", "3" => "Mansion ", "4" => "Apartment ", _ => "" };
        var level = file.Contains("_b1_") ? "basement " : file.Contains("_f2_") ? "upper " : file.Contains("_f1_") ? "ground " : "";
        var variant = System.Text.RegularExpressions.Regex.Match(file, @"(\d+)$").Value.TrimStart('0');
        if (variant.Length == 0) variant = "1";
        return $"{district} · {size}{level}{category.ToLowerInvariant()} {variant}";
    }
}
