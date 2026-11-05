using Haven.Core;
using Haven.Core.Furniture;

namespace Haven;

// Custom furniture: importing a furniture mod's changes to one
// vanilla item as a new furnishing, placing it, and drawing it from its package. A custom furnishing keeps its base
// item's behaviour; anyone without its package (or who declined the home's custom content) sees the base item.
internal sealed partial class HousingController
{
    private Dalamud.Plugin.Services.IDataManager furnitureData = null!;
    /// <summary>This PC's custom furniture.</summary>
    public CustomFurnitureLibrary Furniture { get; private set; } = null!;
    // The active home's custom furniture packages.
    private House? furnitureHouse;
    private string[] furnitureHashes = [];
    // Locker work for the home's packages: uploads done (this gathering, or this session through a content session),
    // work in flight, and when to try a failed transfer again.
    private string furnitureRoom = "";
    private readonly HashSet<string> furnitureUploaded = [], furnitureWorking = [];
    private readonly Dictionary<string, (int Attempts, DateTimeOffset Next)> furnitureRetries = [];

    /// <summary>A furniture mod opened for import: its name, and the catalogue items it changes.</summary>
    /// <summary>What can be imported from a mod: a vanilla furnishing it changes (BaseItem), or a minion model it changes (PropModel).</summary>
    public sealed record FurnitureImportTarget(string Label, uint BaseItem, string? PropModel, int MaterialSet = 1);
    public sealed record FurnitureImportChoice(string Path, string ModName, IReadOnlyList<FurnitureImportTarget> Items)
    {
        /// <summary>The mod's groups with an option that changes indoor furniture, by their index in the mod.</summary>
        public IReadOnlyList<(int Index, ModGroup Group)> Groups { get; init; } = [];
    }
    /// <summary>A furniture mod being read or imported, or null.</summary>
    public string? FurnitureImportStatus { get; private set; }

    private void InitialiseCustomFurniture(Dalamud.Plugin.IDalamudPluginInterface pi, Dalamud.Plugin.Services.IDataManager data)
    {
        furnitureData = data;
        Furniture = new CustomFurnitureLibrary(pi, data, log) { KeepUsage = pictures.KeepUsage };
        Catalogue.CustomFurnitureModels = ShownFurniture;
        Catalogue.CustomPropPrefab = item => item.CustomFurniture is { } hash && (active == null || OwnsActiveHouse || CustomPicturesLoaded || Furniture.Item(hash) != null) ? Furniture.Prop(hash) : null;

        PickMeshes = new FurniturePickMeshes(path => path.StartsWith(Haven.Core.Furniture.FurniturePackages.Root, StringComparison.Ordinal) ? Furniture.ModelFile(path) : data.GetFile<Lumina.Data.Files.MdlFile>(path),
            (ex, path) => log.Warning(ex, "[PICK] {Path} couldn't be read for picking; its bounding box is used.", path));
    }
    /// <summary>The triangles furnishings are picked by (FurniturePickMeshes).</summary>
    public FurniturePickMeshes PickMeshes { get; private set; } = null!;

    // A custom furnishing draws from its package where this PC may show the home's custom content.
    private IReadOnlyDictionary<string, string>? ShownFurniture(Furnishing item) =>
        item.CustomFurniture is { } hash && (active == null || CustomPicturesLoaded || Furniture.Item(hash) != null) ? Furniture.Models(hash) : null;

    /// <summary>The active home's custom furniture packages.</summary>
    public IReadOnlyList<string> HomeFurniture => furnitureHashes;

    // Each frame: failures are reported once; the home's packages this PC has are unpacked and served; the owner puts
    // each package on the locker (as soon as it is placed, through a content session, and again whenever it hosts),
    // and a guest who agreed to the home's custom content fetches them.
    private void UpdateCustomFurniture()
    {
        while (Furniture.Failures.TryDequeue(out var message)) Failed?.Invoke(message);
        if (!ReferenceEquals(active, furnitureHouse))
        {
            if (active?.Id != furnitureHouse?.Id) furnitureRetries.Clear();
            furnitureHouse = active;
            furnitureHashes = active?.Furnishings.Select(f => f.CustomFurniture).OfType<string>().Distinct().ToArray() ?? [];
        }
        // The piece being placed is served too, so its preview draws as itself before it is set down (it is always the
        // player's own: only their furniture can be placed).
        string[] shown = active != null && (OwnsActiveHouse || CustomPicturesLoaded) ? furnitureHashes : [];
        if (placement?.CustomFurniture is { } placing && Furniture.Item(placing) != null && !shown.Contains(placing)) shown = [.. shown, placing];
        if (shown.Length > 0) Furniture.Prepare(shown);
        if (furnitureHashes.Length == 0 || active == null) return;
        var relay = engine.HousingRelay;
        var gathering = IsSharedVisit && relay.HousingSceneReady;
        if (gathering && relay.RoomId != furnitureRoom) { furnitureRoom = relay.RoomId; furnitureUploaded.Clear(); furnitureRetries.Clear(); }
        var owner = OwnsActiveHouse;
        if (owner && !AgreementAccepted) return;
        foreach (var hash in furnitureHashes)
        {
            if (furnitureWorking.Contains(hash) || furnitureRetries.TryGetValue(hash, out var retry) && retry.Next > DateTimeOffset.UtcNow) continue;
            if (owner ? furnitureUploaded.Contains(hash) || !gathering && Furniture.Item(hash)?.Stored != null || !Furniture.Has(hash)
                : !gathering || !CustomPicturesLoaded || Furniture.Has(hash)) continue;
            furnitureWorking.Add(hash);
            var origin = relay.ContentOrigin; var relayUrl = engine.HousingRelayUrl; var character = engine.HousingCharacterId;
            _ = Task.Run(async () =>
            {
                try
                {
                    // A few transfers at a time: a home can list dozens of packages, each held whole while it's checked.
                    await TransferSlots.WaitAsync();
                    if (!owner) await Furniture.Download(relay, hash);
                    else if (gathering) await Furniture.Upload(relay.RequestContentTicket, origin ?? throw new InvalidOperationException("The relay's address is unknown."), hash, CancellationToken.None);
                    else
                    {
                        // Alone: through a content session, so guests can see it whenever they visit.
                        await using var session = await ContentSessionClient.Open(relayUrl, character, pictures.SecretFor(character), CancellationToken.None, o => Account.Apply(o, character, ""));
                        await Furniture.Upload(session.Request, session.Origin, hash, CancellationToken.None);
                    }
                    Run(() => { furnitureWorking.Remove(hash); furnitureRetries.Remove(hash); contentBroken.Remove(hash); if (owner) furnitureUploaded.Add(hash); });
                }
                catch (Exception ex)
                {
                    var quiet = ex is PictureNotReadyException;
                    if (!quiet) log.Warning(ex, "Haven could not {Action} custom furnishing {Hash}.", owner ? "upload" : "download", hash[..12]);
                    Run(() =>
                    {
                        furnitureWorking.Remove(hash);
                        if (!quiet) contentBroken.Add(hash);
                        var attempts = furnitureRetries.GetValueOrDefault(hash).Attempts + 1;
                        // A guest's download that failed outright stops there: the player is told and chooses to try again or give up.
                        var stopped = !quiet && !owner;
                        furnitureRetries[hash] = (attempts, stopped ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddSeconds(quiet ? 3 : Math.Min(120, 3 * Math.Pow(2, attempts))));
                        if (stopped) DownloadFailed(hash, ContentKind.Furniture, ex.Message);
                        else if (!quiet && attempts == 1) Failed?.Invoke("A custom furnishing could not be uploaded: " + ex.Message);
                    });
                }
                finally { TransferSlots.Release(); }
            });
        }
    }

    /// <summary>
    /// Whether a furnishing shows as custom furniture on this PC: the player's own, or one whose package is served and
    /// whose home's custom content this PC loads. Otherwise it is its base item, by name and look.
    /// </summary>
    public bool ShowsCustom(Furnishing item) => item.CustomFurniture is { } hash && (Furniture.Item(hash) != null || ShownFurniture(item) != null || item.ItemId == 0 && Furniture.Prop(hash) != null);
    /// <summary>A furnishing's name as this PC shows it: a custom piece's own name, otherwise its catalogue name.</summary>
    public string NameOf(Furnishing item) => ShowsCustom(item) && Furniture.Name(item.CustomFurniture!) is { } name ? name : Catalogue.Get(item).Name;

    /// <summary>Opens a furniture mod (a .pmp or a mod folder) off the framework thread and finds the items it changes.</summary>
    public Task<FurnitureImportChoice> ReadFurnitureMod(string path)
    {
        FurnitureImportStatus = "Reading the mod…";
        return Task.Run(() =>
        {
            try
            {
                using var mod = PenumbraMod.Open(path);
                // Every option at once, so a furnishing only one option changes is found too; the player picks options next.
                mod.SelectAll();
                var items = Catalogue.ItemsTouching(mod.Files.Keys)
                    .Where(item => FurniturePackages.Changes(mod, BaseModels(item.ItemId), Furniture.GameFile))
                    .Select(item => new FurnitureImportTarget(item.Name, item.ItemId, null))
                    // Minion models: props of their own, as most gpose prop mods are.
                    .Concat(FurniturePackages.PropModels(mod).SelectMany(model => PropTargets(mod, model))).ToArray();
                if (items.Length == 0) throw new InvalidOperationException("This mod doesn't change any indoor furnishing or minion model Haven can use.");
                var groups = mod.Groups.Select((group, index) => (index, group))
                    .Where(g => g.group.Options.Count > 0 && g.group.Options.Any(o => o.Files.Keys.Any(k => k.StartsWith("bgcommon/hou/indoor/", StringComparison.Ordinal)))).ToArray();
                return new FurnitureImportChoice(path, mod.Name.Length > 0 ? mod.Name : Path.GetFileNameWithoutExtension(path), items) { Groups = groups };
            }
            finally { Run(() => FurnitureImportStatus = null); }
        });
    }

    // One prop per material set the minions using this model draw with ("A prop from the Black Chocobo Chick
    // minion"): several minions can share a model and differ only in materials. Each minion's variant gives its
    // material set through the model's .imc file (the mod's, if it changes it). A model no minion uses: set 1.
    private IEnumerable<FurnitureImportTarget> PropTargets(PenumbraMod mod, string model)
    {
        var match = System.Text.RegularExpressions.Regex.Match(model, @"^(chara/monster/m(\d{4})/obj/body/b(\d{4}))/");
        if (!match.Success) return [new("A prop from a minion model", 0, model)];
        int number = int.Parse(match.Groups[2].Value), body = int.Parse(match.Groups[3].Value);
        var imcPath = $"{match.Groups[1].Value}/b{body:0000}.imc";
        var imc = mod.Files.TryGetValue(imcPath, out var file) && file.Entry != null ? mod.Read(file.Entry) : Furniture.GameFile(imcPath);
        var title = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
        var minions = furnitureData.GetExcelSheet<Lumina.Excel.Sheets.Companion>()
            .Where(c => c.Model.IsValid && c.Model.Value.Type == 3 && c.Model.Value.Model == number && c.Model.Value.Base == body && !c.Singular.IsEmpty)
            .Select(c => (Name: title.ToTitleCase(c.Singular.ToString()), Set: imc is { } bytes ? FurnitureFiles.ImcMaterialSet(bytes, c.Model.Value.Variant) ?? 1 : 1))
            .GroupBy(m => m.Set).OrderBy(g => g.Key).ToArray();
        if (minions.Length == 0) return [new($"A prop from minion model {number:0000}", 0, model)];
        // Only the looks the mod changes the materials of (the Spooky Pumpkin Set's Big Jack-o-Lantern is for the Black
        // Chocobo Chick; the Wayward Hatchling's material is the game's own); all of them when it changes none.
        var changed = FurniturePackages.ChangedMaterialSets(mod, model, Furniture.GameFile, [.. minions.Select(g => g.Key)]);
        minions = [.. minions.Where(g => changed.Contains(g.Key))];
        return minions.Select(g => new FurnitureImportTarget($"A prop from the {string.Join(" / ", g.Select(m => m.Name).Distinct())} minion", 0, model, g.Key));
    }

    private string[] BaseModels(uint itemId) => [.. Catalogue.Prefab(itemId).Models.Select(m => m.Path).Distinct()];

    /// <summary>Imports one item's changes from a mod as a new furnishing under the given name, off the framework thread.</summary>
    public Task<string> ImportFurniture(string path, FurnitureImportTarget target, string name, ModSelection? options = null)
    {
        FurnitureImportStatus = $"Importing {name}…";
        var models = target.PropModel == null ? BaseModels(target.BaseItem) : [];
        return Task.Run(() =>
        {
            try
            {
                using var mod = PenumbraMod.Open(path);
                mod.Select(options ?? ModSelection.Default);
                var hash = target.PropModel is { } prop ? Furniture.ImportProp(mod, prop, name, target.MaterialSet) : Furniture.Import(mod, target.BaseItem, models, name);
                Run(() => Status = $"Added {Furniture.Item(hash)?.Name ?? name} to your furniture.");
                return hash;
            }
            finally { Run(() => FurnitureImportStatus = null); }
        });
    }

    /// <summary>Starts placing one of the player's custom furnishings, like any catalogue item.</summary>
    public void BeginCustomFurniture(string hash)
    {
        if (OwnsActiveHouse) RequireAgreement();
        if (Furniture.Item(hash) is not { } item) throw new InvalidOperationException("That furnishing is no longer in your furniture.");
        BeginPlacement(new Furnishing { ItemId = item.BaseItem, CustomFurniture = hash }, faceCamera: true);
        // A prop draws only once its package is unpacked and served.
        Furniture.Prepare([hash]);
    }

    public void RenameFurniture(string hash, string name) => Furniture.Rename(hash, name);

    /// <summary>Whether importing a minion prop shows the experimental warning first.</summary>
    public bool WarnMinionProps { get => engine.HousingWarnMinionProps; set => engine.HousingWarnMinionProps = value; }
    public bool WarnTiltedSeats { get => engine.HousingWarnTiltedSeats; set => engine.HousingWarnTiltedSeats = value; }

    private string[] HomesFurnishedWith(string hash) => allHouses.Where(h => h.Id != active?.Id).Append(active).OfType<House>()
        .Where(h => h.Furnishings.Any(f => f.CustomFurniture == hash)).Select(h => h.Name).Distinct().ToArray();
    public string[] FurnitureHomes(string hash) => HomesFurnishedWith(hash);

    /// <summary>
    /// Deletes a custom furnishing from this PC and removes every copy of it from every home here: the one open now as
    /// one undoable change, and every saved home. A placement of it in progress is cancelled first, so nothing can be
    /// set down without its files. A package this PC uploaded is deleted from Haven's server too, giving its bytes back
    /// to the allowance; if that fails, the server copy expires after 30 days unused.
    /// </summary>
    public void DeleteFurniture(string hash)
    {
        if (placement?.CustomFurniture == hash) CancelPlacement();
        var uploaded = Furniture.Item(hash) is { Stored: not null };
        var removed = 0;
        if (active?.Furnishings.Any(f => f.CustomFurniture == hash) == true)
        {
            removed += active.Furnishings.Count(f => f.CustomFurniture == hash);
            Commit(active with { Furnishings = [.. active.Furnishings.Where(f => f.CustomFurniture != hash)] });
        }
        var others = allHouses.Where(h => h.Id != active?.Id && h.Furnishings.Any(f => f.CustomFurniture == hash)).ToArray();
        foreach (var house in others)
        {
            removed += house.Furnishings.Count(f => f.CustomFurniture == hash);
            Store.Save(house with { Furnishings = [.. house.Furnishings.Where(f => f.CustomFurniture != hash)] });
        }
        if (others.Length > 0) Refresh();
        Furniture.Delete(hash);
        Status = removed == 0 ? "Furnishing deleted." : $"Furnishing deleted, and {removed} placed {(removed == 1 ? "copy" : "copies")} removed from your homes.";
        if (!uploaded) return;
        var relayUrl = engine.HousingRelayUrl; var character = engine.HousingCharacterId;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var session = await ContentSessionClient.Open(relayUrl, character, pictures.SecretFor(character), CancellationToken.None, o => Account.Apply(o, character, ""));
                await Furniture.DeleteFromServer(session.Request, session.Origin, hash, CancellationToken.None);
                Run(() => Status = "Furnishing deleted from this PC and Haven's server.");
            }
            catch (Exception ex) when (Retryable(ex))
            {
                // The piece is already gone from this PC and its homes; its server copy is deleted later.
                Run(() => { QueueServerDelete(ServerContent.Furniture, hash, character, ex); Status = "Furnishing deleted. Haven deletes its copy on the server as soon as it can."; });
            }
            catch (NotUploaderException ex) { Run(() => throw new InvalidOperationException("The furnishing was deleted from this PC. " + ex.Message, ex)); }
        });
    }
}
