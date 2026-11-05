using System.Text.Json;
using Haven.Core;
using HavenNative;
using Stagehand.Definitions;

namespace Haven;

// Saved layouts: the furnishings and finishes of a real house (saved from inside it with the Haven menu) or of a
// MakePlace file, kept in a local library. A layout starts a new home on its shell, or replaces the furnishings and
// finishes of a home on the same shell (one undo step). Layouts are in the interior's own coordinates, which are a
// preset home's (its transform is the identity), so every piece keeps its place. What Haven cannot carry is counted
// and named when the layout is saved.
internal sealed partial class HousingController
{
    private LayoutStore? layoutStore;
    private LayoutStore LayoutLibrary => layoutStore ??= new(Path.Combine(Path.GetDirectoryName(Store.DirectoryPath)!, "layouts"));
    public string LayoutsFolder => LayoutLibrary.DirectoryPath;
    private IReadOnlyList<SavedLayout>? savedLayouts;
    public IReadOnlyList<SavedLayout> SavedLayouts => savedLayouts ??= LayoutLibrary.List();
    private void RefreshLayouts() => savedLayouts = null;

    // The real house or apartment the player is in, by place name, while no Haven visit is running; null elsewhere.
    public string? RealInteriorName => engine.HousingRealInterior is uint territory ? Catalogue.ShellName(territory) : null;
    public string ShellName(uint territory) => Catalogue.ShellName(territory);

    // Name: what the player called it, else the place.
    public LayoutResult SaveRealHouseLayout(string? name = null)
    {
        var capture = engine.HousingCaptureRealInterior();
        name = UniqueLayoutName(string.IsNullOrWhiteSpace(name) ? Catalogue.ShellName(capture.Territory) : name);
        var result = SaveLayout(capture.Layout, name, $"Saved in {Catalogue.ShellName(capture.Territory)} on {DateTimeOffset.Now:d MMM yyyy}", capture.Territory);
        result = result with { Unknown = result.Unknown + capture.Unknown };
        Status = result.Summary($"Saved the layout \"{name}\"");
        return result;
    }

    // Shell: the launcher's preset for the file's size and district, or the shell chosen on the New home page when
    // the file does not say.
    public LayoutResult ImportMakePlace(string path, Func<HomeLayout, uint?> shell)
    {
        var info = new FileInfo(path);
        if (info.Length > MakePlaceLayout.MaxBytes) throw new InvalidDataException("This MakePlace layout is larger than 16 MiB.");
        var layout = MakePlaceLayout.Read(File.ReadAllBytes(path));
        var territory = shell(layout) ?? throw new InvalidDataException("This layout does not say which house it is for; choose a shell on the New home page first.");
        var name = UniqueLayoutName(Path.GetFileNameWithoutExtension(path));
        var result = SaveLayout(layout, name, $"MakePlace file {Path.GetFileName(path)}", territory);
        Status = result.Summary($"Imported the layout \"{name}\"");
        return result;
    }

    public House CreateFromLayout(Guid layoutId, string name)
    {
        var layout = SavedLayouts.FirstOrDefault(x => x.Id == layoutId) ?? throw new InvalidOperationException("That layout is no longer saved.");
        RequireHousing(layout.TerritoryId);
        var definition = new StageDefinition { FormatVersion = StageDefinition.CurrentFormatVersion, Info = new StageInfo { Name = name, IntendedTerritoryType = (int)layout.TerritoryId } };
        var house = new House
        {
            Name = name, TerritoryId = layout.TerritoryId,
            Stage = JsonSerializer.SerializeToElement(definition, StageDefinition.DefinitionStringSerializerOptions),
            Furnishings = LayoutFormat.Copy(layout), Interior = layout.Interior,
        };
        HouseFormat.Validate(house); Catalogue.Validate(house);
        SaveNewHome(house);
        Status = $"Home created from the layout \"{layout.Name}\". Enter it to see it.";
        return house;
    }

    // Replaces this home's furnishings and finishes with the layout's; Undo brings them back.
    public void ApplyLayout(Guid layoutId)
    {
        var layout = SavedLayouts.FirstOrDefault(x => x.Id == layoutId) ?? throw new InvalidOperationException("That layout is no longer saved.");
        if (active == null || active.CustomLayout || active.TerritoryId != layout.TerritoryId)
            throw new InvalidOperationException($"\"{layout.Name}\" was made for {Catalogue.ShellName(layout.TerritoryId)}; enter a home on that shell to apply it.");
        // The home keeps its moods, weather and time of day (a layout carries finishes and brightness only).
        Commit(active with { Furnishings = LayoutFormat.Copy(layout),
            Interior = layout.Interior with { Moods = active.Interior.Moods, Weather = active.Interior.Weather, TimeOfDay = active.Interior.TimeOfDay, Outside = active.Interior.Outside } });
        runtime.RefreshLighting();
        Status = $"Applied the layout \"{layout.Name}\". Undo brings your furnishings back.";
    }

    public void RenameLayout(Guid layoutId, string name)
    {
        var layout = SavedLayouts.FirstOrDefault(x => x.Id == layoutId) ?? throw new InvalidOperationException("That layout is no longer saved.");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A layout needs a name.");
        var renamed = layout with { Name = UniqueLayoutName(name, layoutId) };
        LayoutLibrary.Save(renamed);
        RefreshLayouts();
        Status = $"Layout renamed to \"{renamed.Name}\".";
    }

    public void DeleteLayout(Guid layoutId)
    {
        LayoutLibrary.Delete(layoutId);
        RefreshLayouts();
        Status = "Layout deleted. A recovery copy is kept in the layouts folder.";
    }

    // Another layout's name gets " (2)", " (3)"...; except: the layout being renamed, which may keep its own.
    private string UniqueLayoutName(string name, Guid except = default)
    {
        name = name.Trim().Length == 0 ? "Layout" : name.Trim();
        if (name.Length > 100) name = name[..100];
        var taken = SavedLayouts.Where(x => x.Id != except).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++) if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    private LayoutResult SaveLayout(HomeLayout layout, string name, string source, uint territory)
    {
        RequireHousing(territory);
        var byName = Catalogue.Items.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var dyes = Catalogue.Dyes.Where(x => x.Id != 0).Select(x => (x.Id, x.Color)).ToArray();
        List<Furnishing> furnishings = []; List<string> missing = []; var fixturesSkipped = 0; var materialsSkipped = 0;
        foreach (var piece in layout.Pieces)
        {
            // An ID first (a real house is exact), then the English name (MakePlace looks items up the same way).
            var entry = Catalogue.Items.FirstOrDefault(x => x.ItemId == piece.ItemId && piece.ItemId != 0)
                ?? (piece.Name.Length > 0 ? byName.GetValueOrDefault(piece.Name) : null);
            if (entry == null) { missing.Add(piece.Name.Length > 0 ? piece.Name : $"item {piece.ItemId}"); continue; }
            var stain = entry.DyeSlots == 0 ? 0
                : piece.Stain != 0 && Catalogue.Dyes.Any(x => x.Id == piece.Stain) ? piece.Stain
                : piece.Color is uint color ? HomeLayouts.NearestDye(color, dyes) : 0;
            // What it shows (MakePlace's material): a frame's painting, a partition's wallpaper, a vase's flower or a canvas's print.
            uint picture = 0, finish = 0, display = 0;
            if (piece.Material != 0)
            {
                if (entry.TakesPicture && Catalogue.PictureOfItem(piece.Material) is { } painting) picture = painting.Id;
                else if (entry.FinishPart == 0 && Catalogue.Fixtures.FirstOrDefault(x => x.ItemId == piece.Material && x.Part == 0) is { } wall) finish = wall.FixtureId;
                else if (entry.DisplayKind != 0 && Catalogue.Displays.FirstOrDefault(x => x.ItemId == piece.Material && x.Kind == entry.DisplayKind) is { } shown) display = shown.Row;
                else materialsSkipped++;
            }
            // A real aquarium's fish, kept only when they fit this tank as Haven checks it.
            AquariumSetup? tank = null;
            if (piece.Fish is { Length: > 0 } fish && entry.AquariumTier > 0)
            {
                var setup = new AquariumSetup { Fish = fish.Take(entry.AquariumTier).ToArray() };
                if (Catalogue.AquariumFits(entry, setup)) tank = setup; else materialsSkipped++;
            }
            furnishings.Add(new Furnishing { ItemId = entry.ItemId, Position = [piece.Position.X, piece.Position.Y, piece.Position.Z], Rotation = piece.Yaw, StainId = stain,
                Picture = picture, SurfaceFinish = finish, Display = display, Aquarium = tank });
        }
        var floors = new[] { new InteriorFloor(), new InteriorFloor(), new InteriorFloor() };
        uint hall = 0;
        foreach (var fixture in layout.Fixtures)
        {
            uint? part = fixture.Type.ToLowerInvariant() switch { "wall" => 0, "floor" => 3, "light" => 4, _ => null };
            var match = part is not uint p ? null : Catalogue.Fixtures.FirstOrDefault(x => x.Part == p && (fixture.FixtureId != 0 ? x.FixtureId == fixture.FixtureId
                : fixture.ItemId != 0 && x.ItemId == fixture.ItemId || fixture.Name.Length > 0 && x.Name.Equals(fixture.Name, StringComparison.OrdinalIgnoreCase)));
            if (match == null) { fixturesSkipped++; continue; }
            if (fixture.Level.Equals("Main", StringComparison.OrdinalIgnoreCase))
            {
                if (part == 4 && Catalogue.HasMainHallChandelier(territory)) hall = match.FixtureId; else fixturesSkipped++;
                continue;
            }
            var level = Array.FindIndex(HomeLayouts.Levels, x => x.Equals(fixture.Level, StringComparison.OrdinalIgnoreCase));
            if (level < 0) { fixturesSkipped++; continue; }
            floors[level] = part switch { 0 => floors[level] with { Wallpaper = match.FixtureId }, 3 => floors[level] with { Flooring = match.FixtureId }, _ => floors[level] with { CeilingLight = match.FixtureId } };
        }
        var saved = new SavedLayout
        {
            Name = name, Source = source, TerritoryId = territory, Furnishings = furnishings.ToArray(),
            Interior = new InteriorOptions { Floors = floors, MainHallCeilingLight = hall, Brightness = layout.Brightness },
        };
        // The scene budget a home on this shell would have to meet.
        Catalogue.Validate(new House { Name = name, TerritoryId = territory, Furnishings = saved.Furnishings, Interior = saved.Interior });
        LayoutLibrary.Save(saved);
        RefreshLayouts();
        return new(saved.Id, furnishings.Count, missing.Distinct().ToArray(), missing.Count, layout.OutdoorPieces, fixturesSkipped, 0) { MaterialsSkipped = materialsSkipped };
    }
}

/// <summary>What became of a layout: the saved layout, how many furnishings it holds, and what was left out.</summary>
internal sealed record LayoutResult(Guid Layout, int Placed, string[] MissingNames, int Missing, int Outdoor, int FixturesSkipped, int Unknown)
{
    public int MaterialsSkipped { get; init; }
    public string Summary(string verb)
    {
        var text = $"{verb} with {Placed} furnishing{(Placed == 1 ? "" : "s")}.";
        List<string> left = [];
        if (Missing > 0)
            left.Add($"{Missing} not in Haven's catalogue ({string.Join(", ", MissingNames.Take(3))}{(MissingNames.Length > 3 ? ", …" : "")})");
        // A piece the game data cannot name at all is listed by its ID in the Dalamud log.
        if (Unknown > 0) left.Add($"{Unknown} unrecognised piece{(Unknown == 1 ? "" : "s")} (see the Dalamud log)");
        if (Outdoor > 0) left.Add($"{Outdoor} yard item{(Outdoor == 1 ? "" : "s")}");
        if (FixturesSkipped > 0) left.Add($"{FixturesSkipped} window, door or unknown finish{(FixturesSkipped == 1 ? "" : "es")}");
        if (MaterialsSkipped > 0) left.Add($"{MaterialsSkipped} painting, wallpaper, flowers or fish Haven can't show (the piece is placed plain)");
        return left.Count == 0 ? text : text + " Left out: " + string.Join("; ", left) + ".";
    }
}
