using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Haven.Core;
using HavenNative;
using Stagehand.Api;
using Stagehand.Definitions;
using Stagehand.Definitions.Objects;
using System.Collections.Concurrent;
using System.Numerics;

namespace Haven;

internal sealed partial class HousingController : IDisposable
{
    private const string LiveStage = "Haven.ActiveHouse";
    private const string GhostStage = "Haven.PlacementGhost";
    private const string AmbientStage = "Haven.Ambience";
    private string? liveEffects;
    private readonly Dictionary<string, string> liveSounds = new(StringComparer.Ordinal);
    private readonly IStagehandApiConsumer stagehand;
    private readonly NativeEngine engine;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly HousingRuntime runtime;
    private readonly FurnitureVisuals visuals;
    // Furnishing lights Haven draws itself when HavenLights is on; Stagehand draws them otherwise.
    private readonly FurnitureLights lights;
    /// <summary>
    /// Whether Haven draws furnishing lights and the blank canvas's fill light itself instead of Stagehand (the default
    /// since 0.66.1; Settings → Advanced can give them back to Stagehand). World groups' lights show only then.
    /// </summary>
    public bool HavenLights
    {
        get => engine.HousingHavenLights;
        set { if (engine.HousingHavenLights == value) return; engine.HousingHavenLights = value; if (!value) lights.Clear(); needsRender = true; }
    }
    /// <summary>How many lights Haven draws itself now, and how many belong to world pieces.</summary>
    public (int All, int World) HavenLightCount => lights.Count;
    // Furnishing effects Haven draws itself when HavenEffects is on; Stagehand otherwise.
    private readonly FurnitureEffects effects;
    // A world effect being placed or moved, drawn where the pointer puts it (its own, apart from the home's).
    private readonly FurnitureEffects previewEffect;
    /// <summary>Whether Haven draws furnishing effects itself instead of Stagehand (the default since 0.66.4; Settings → Advanced can give them back).</summary>
    public bool HavenEffects
    {
        get => engine.HousingHavenEffects;
        set { if (engine.HousingHavenEffects == value) return; engine.HousingHavenEffects = value; if (!value) effects.Clear(); needsRender = true; }
    }
    /// <summary>How many effects Haven draws itself now.</summary>
    public int HavenEffectCount => effects.Count;
    // Furnishing sounds Haven plays itself when HavenSounds is on; Stagehand otherwise.
    private readonly FurnitureSounds sounds;
    /// <summary>Whether Haven plays furnishing sounds itself instead of Stagehand (the default since 0.66.7; Settings → Advanced can give them back).</summary>
    public bool HavenSounds
    {
        get => engine.HousingHavenSounds;
        set { if (engine.HousingHavenSounds == value) return; StopSwitchCues(); engine.HousingHavenSounds = value; if (!value) sounds.Clear(); needsRender = true; }
    }
    /// <summary>How many furnishing sounds Haven plays itself now.</summary>
    public int HavenSoundCount => sounds.Count;
    private readonly ConcurrentQueue<Action> actions = new();
    private readonly Stack<House> undo = new(), redo = new();
    private bool needsRender;
    private bool movingExisting;
    /// <summary>A piece already in the home (or an imported object) is being moved, rather than a new one placed.</summary>
    private bool MovingExisting => movingExisting || importedPlacement != null;
    private DateTimeOffset ghostUpdated;
    private Furnishing? placement;
    private ImportedObject? importedPlacement;
    private StageDefinition? importedGhost;
    private ObjectDefinition? importedGhostTarget;
    public FurnitureCatalogue Catalogue { get; }
    public ImportedScene Imported { get; }
    public House? ActiveHouse => active;
    /// <summary>A Gold Saucer arcade game holds the player at its machine (movement keys and turning).</summary>
    public bool ArcadeHolding { get; set; }
    /// <summary>The player possesses an NPC: their own character holds still (movement keys and turning).</summary>
    public bool NpcHolding { get; set; }
    /// <summary>The free building camera is on: the player's character holds still (movement keys and turning). Only
    /// ever inside a home.</summary>
    public bool CameraHolding { get => cameraHolding && active != null; set => cameraHolding = value; }
    private bool cameraHolding;
    /// <summary>Raised as a visit is left or ended, before the trip back begins (characters Haven made go first).</summary>
    public event Action? Leaving;
    public Furnishing? Placement => placement;
    public bool IsPlacing => placement != null || importedPlacement != null;
    public bool IsFineEditing { get; private set; }
    public bool PreviewHasTarget { get; private set; }
    public string PreviewName => placement != null ? NameOf(placement) : importedPlacement?.Name ?? "Furnishing";
    public bool BuildingFinishesAvailable => visuals.BuildingFinishesAvailable;
    public string? MovingImportedKey => importedPlacement?.Definition.Key;
    public Matrix4x4 PreviewMatrix => placement != null ? FurnitureCatalogue.ItemMatrix(placement) : importedPlacement != null && importedGhostTarget != null
        ? StageObjects.Matrix(importedGhostTarget.Position, importedGhostTarget.RotationPitchYawRollDegrees, importedGhostTarget.Scale) * importedPlacement.Definition.ParentTransform : Matrix4x4.Identity;
    public Vector3 PreviewPosition => Vector3.Transform(Vector3.Zero, PreviewMatrix);
    public float PreviewScale => placement?.Scale ?? importedGhostTarget?.Scale.X ?? 1;
    public float PreviewRotation => placement?.Rotation ?? importedGhostTarget?.RotationPitchYawRollDegrees.Y ?? 0;
    public Guid? MovingId => movingExisting ? placement?.Id : null;
    public bool IsReady => active != null && engine.HousingZoneReady(active.TerritoryId) && (!active.CustomLayout || runtime.CustomReady);
    /// <summary>The home is in place and drawn, furnishings included (the loading screen lifts on this).</summary>
    public bool SceneShown => IsReady && rendered && runtime.SceneReady && visuals.SceneReady;
    /// <summary>Furnishing models drawn so far, of those created.</summary>
    public (int Ready, int Count) FurnishingsReady => rendered ? visuals.Readiness : (0, 0);
    public bool OwnsActiveHouse => active != null && (engine.HousingRelay.Editing is {} access
        ? access.Owner == engine.HousingRelay.LocalContentId : ownedVisit == active.Id);
    public bool IsSharedVisit => publishedScene != null;
    public bool CanStartVisit => !engine.HousingSessionActive && !engine.HousingRelay.IsConnecting && pendingHost == null && !EndingVisit && !engine.HousingReturnPending;
    public bool CanStopVisit => engine.HousingSessionActive || engine.HousingRelay.IsConnecting || pendingHost != null || active != null;
    public bool CanRetryEntry => active != null && engine.HousingRelay.RoomJoinedAcknowledged && engine.HousingRelay.IsHost
        && !engine.HousingRelay.IsRecovering && !IsReady && !EndingVisit && !engine.HousingReturnPending;
    public bool EndingVisit { get; private set; }
    public bool CanControlMusic => IsReady && !EndingVisit && OwnsActiveHouse && (engine.HousingRelay.SoloMode
        || engine.HousingRelay.IsConnected && engine.HousingRelay.HousingSceneReady);
    public bool CanFurnish => IsReady && !EndingVisit && (IsSharedVisit
        ? (engine.HousingRelay.IsRecovering || engine.HousingRelay.IsConnected && engine.HousingRelay.HousingSceneReady)
            && engine.HousingRelay.Editing?.CanEdit(engine.HousingRelay.LocalContentId) == true
        : OwnsActiveHouse && engine.HousingRelay.SoloMode);
    /// <summary>Whether another character may decorate this home now: its owner, or a decorator while guests may decorate
    /// (the gathering's editing state). Only they possess its NPCs.</summary>
    public bool MayDecorate(ulong character) => IsSharedVisit && engine.HousingRelay.Editing?.CanEdit(character) == true;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public Matrix4x4 FurnitureModelTransform(Furnishing item, PrefabModel model)
        => model.Door is {} motion ? DoorMovement.Transform(Catalogue.Prefab(item).Doors[motion.Door].Action,
            motion.Panel, motion.Local, motion.Parent, runtime.Doors.Progress(item.Id, motion.Door)) : model.Clock is {} clock
                ? ClockMovement.Transform(clock.Hour, clock.Local, clock.Parent, runtime.ClockSeconds) : model.Loop is {} loop
                    ? LoopMovement.Transform(loop, runtime.AnimationMilliseconds, Catalogue.Prefab(item).Switch == null || SwitchOn(item), SwitchAge(item),
                        FurnitureCatalogue.MotionSeed(item)) : model.Transform;
    public int ColliderCount => runtime.ColliderCount;
    public string FixtureStatus => runtime.FixtureStatus;
    public MusicSlot? MusicSlot => active == null ? null : runtime.MusicState(active.Music);
    public bool MusicReady => runtime.MusicReady;
    public string MusicStatus => runtime.MusicStatus;
    private byte[]? receivedSnapshot;
    private House? active, pendingHost, visitorHouse;
    private PublishedHouseScene? publishedScene;
    private Guid? ownedVisit;
    private bool rendered;
    private bool needsPublish;
    private DateTimeOffset nextPublish;
    private DateTimeOffset nextReady;
    private DateTimeOffset connectDeadline;
    private int locationChanged;
    private string? lastError;
    private DateTimeOffset lastErrorAt;
    public HouseStore Store { get; }
    // Every saved home on this PC; Houses is the logged-in character's, plus any still unsorted (HomeOwners).
    private IReadOnlyList<House> allHouses = [];
    private IReadOnlyList<House>? visibleHouses;
    private string visibleFor = "";
    private HomeOwners owners = null!;
    private string Character => HomeOwners.Key(engine.HousingCharacterId);
    public IReadOnlyList<House> Houses
    {
        get
        {
            var character = Character;
            if (visibleHouses == null || visibleFor != character)
            { visibleHouses = allHouses.Where(h => owners.Visible(h.Id, character)).ToArray(); visibleFor = character; }
            return visibleHouses;
        }
    }
    // A home saved before homes belonged to characters, shown to every character until one keeps it.
    public bool IsUnsorted(Guid id) => owners.Unsorted(id);
    public void KeepForThisCharacter(Guid id)
    {
        if (Character.Length == 0) throw new InvalidOperationException("Log in to the character who should keep this home.");
        if (!owners.Unsorted(id)) return;
        owners.Assign(id, Character); visibleHouses = null;
        Status = "Home kept for this character. Your other characters no longer see it.";
    }
    public void KeepAllForThisCharacter()
    {
        if (Character.Length == 0) throw new InvalidOperationException("Log in to the character who should keep these homes.");
        var unsorted = allHouses.Where(h => owners.Unsorted(h.Id)).ToArray();
        foreach (var home in unsorted) owners.Assign(home.Id, Character);
        visibleHouses = null;
        Status = $"Kept {unsorted.Length} home{(unsorted.Length == 1 ? "" : "s")} for this character.";
    }
    // A home made now belongs to the character who made it.
    /// <summary>A copy of a home took new NPC ids (old to new): their kept mods are copied across (set by the plugin).</summary>
    public Action<IReadOnlyDictionary<Guid, Guid>>? NpcsCopied { get; set; }

    // Saves a copy of a home (Duplicate, Import, a visitor's copy) as a new one, with new NPC ids (HomeNpcs.Renumber).
    private House SaveCopy(House house)
    {
        house = HomeNpcs.Renumber(house, out var renamed);
        SaveNewHome(house);
        if (renamed.Count > 0)
            try { NpcsCopied?.Invoke(renamed); }
            catch (Exception ex) { Failed?.Invoke($"The copy's NPCs start without their mods ({ex.Message})."); }
        return house;
    }

    private void SaveNewHome(House house)
    {
        Store.Save(house);
        if (Character.Length > 0) owners.Assign(house.Id, Character);
        Refresh();
    }
    public House? VisitorHouse => visitorHouse;
    public string Status { get => status; private set { status = value; StatusVersion++; } }
    private string status = "Create a home or select a saved layout.";
    /// <summary>Counts every new Status, so a window can show only the ones its own actions brought.</summary>
    public int StatusVersion { get; private set; }
    public string ConfigDirectory { get; }
    public string VisitCode { get; set; }
    public bool CanEdit => visitorHouse == null;

    public HousingController(IDalamudPluginInterface pi, NativeEngine engine, IPluginLog log, IFramework framework, IDataManager data, ISigScanner scanner)
    {
        this.engine = engine;
        VisitCode = engine.HousingHostCode;
        this.log = log;
        this.framework = framework;
        Catalogue = new FurnitureCatalogue(data);
        Imported = new ImportedScene(data, Catalogue);
        runtime = new HousingRuntime(engine, Catalogue, data, log);
        engine.RegisterHousingEmotes(CanUseNativeFurniture, UseNativeFurniture);
        visuals = new FurnitureVisuals(Catalogue, log, new BuildingMaterials(scanner)) { MachineAction = MachineAction };
        lights = new FurnitureLights(Catalogue, data, log);
        effects = new FurnitureEffects(data, log);
        previewEffect = new FurnitureEffects(data, log);
        sounds = new FurnitureSounds(data, log);
        engine.HousingSceneChanging += OnSceneChanging;
        // A guest whose game lacks the home's zone (GameDataPaths) leaves the gathering before anything loads.
        engine.LogInstalledExpansions();
        engine.HousingZoneUnavailable += reason => Run(() => { Leave(); Status = reason; });
        // The host learns why such a guest left at once (the relay passes the reason on).
        engine.HousingRelay.OnGuestMissingExpansion += (guest, expansion) => Run(() =>
        {
            var why = GameDataPaths.GuestMissing(guest, expansion);
            log.Warning("[DATA] {Why}", why);
            Failed?.Invoke(why);
        });
        engine.RegisterHousingExit(() => Run(Leave));
        ConfigDirectory = pi.ConfigDirectory.FullName;
        // Everything in the game: read from this PC's game data the first time it's wanted.
        World = new WorldCatalogue(data, ConfigDirectory, log, () => Catalogue.ReadyBuildings);
        Catalogue.World = World;
        Ambient = new AmbientCatalogue(data, log);
        Catalogue.Ambient = Ambient;
        World.EffectColour = path => Ambient.Find(path)?.Colour;
        Store = new HouseStore(Path.Combine(pi.ConfigDirectory.FullName, "houses"));
        owners = new HomeOwners(Path.Combine(pi.ConfigDirectory.FullName, "home-owners.json"));
        switchesPath = Path.Combine(pi.ConfigDirectory.FullName, "housing-switches.json");
        LoadSwitches();
        InitialiseCustomPictures(pi, data);
        InitialiseCustomMusic(pi, data);
        InitialiseCustomFurniture(pi, data);
        InitialisePoses(pi, data);
        InitialiseNotices();
        draftStore = new HouseStore(Path.Combine(pi.ConfigDirectory.FullName, "editing-drafts"));
        draftBaseStore = new HouseStore(Path.Combine(pi.ConfigDirectory.FullName, "editing-drafts", "base"));
        engine.HousingRelay.OnRecovered += OnEditingRecovered;
        engine.HousingRelay.OnEditing += OnEditing;
        engine.HousingRelay.OnSwitchStates += OnSwitchStates;
        engine.HousingRelay.OnHousingSceneReady += OnSwitchSceneReady;
        engine.HousingRelay.OnEditProposal += OnProposal;
        engine.HousingRelay.OnEditResult += OnEditResult;
        engine.HousingRelay.OnEditApplied += OnEditApplied;
        stagehand = StagehandApi.CreateIpcClient(pi);
        stagehand.LocationChanged += OnLocationChanged;
        engine.HousingRelay.OnHousingSnapshot += OnSnapshot;
        Refresh();
    }

    public StagehandApiAvailability StagehandStatus => stagehand.CheckApiAvailability();
    // A failed operation's message, at most once per 5 s for the same message: shown as a notification, so
    // it is seen even with every Haven window closed.
    public event Action<string>? Failed;
    public void Run(Action action)
    {
        if (!framework.IsInFrameworkUpdateThread) { actions.Enqueue(action); return; }
        try { action(); }
        catch (Exception ex)
        {
            Status = ex.Message;
            if (lastError != ex.Message || DateTimeOffset.UtcNow - lastErrorAt > TimeSpan.FromSeconds(5))
            {
                log.Warning(ex, "Haven housing operation failed.");
                Failed?.Invoke(ex.Message);
                lastError = ex.Message;
                lastErrorAt = DateTimeOffset.UtcNow;
            }
        }
    }
    /// <summary>Keeps the in-memory library current after saving one home, without re-reading every home from disk.</summary>
    private void Remember(House house)
    {
        allHouses = allHouses.Where(x => x.Id != house.Id).Append(house).OrderBy(x => x.Name).ToArray();
        visibleHouses = null;
    }
    public void Refresh()
    {
        allHouses = Store.LoadAll((path, ex) => log.Warning(ex, "Skipped unreadable house {File}", Path.GetFileName(path)));
        visibleHouses = null;
    }

    public bool CanDeleteHome(Guid id) => active?.Id != id && pendingHost?.Id != id && Houses.Any(x => x.Id == id);
    public void DeleteHome(Guid id)
    {
        if (!CanDeleteHome(id)) throw new InvalidOperationException("Leave this home before deleting it, and check that it is still in your library.");
        Store.Delete(id);
        owners.Forget(id);
        Refresh();
        Status = "Home deleted. A recovery copy is saved in houses/deleted; import it to restore the home.";
    }

    // Homes in use (entered or about to be hosted) keep their name until you leave.
    public void RenameHome(Guid id, string name)
    {
        if (!CanDeleteHome(id)) throw new InvalidOperationException("Leave this home before renaming it.");
        name = name.Trim();
        if (name.Length is 0 or > 120) throw new InvalidOperationException("Home names need 1 to 120 characters.");
        var house = Houses.First(x => x.Id == id) with { Name = name, SavedAt = DateTimeOffset.UtcNow };
        HouseFormat.Validate(house);
        Store.Save(house);
        Refresh();
        Status = "Home renamed.";
    }

    // A copy is a new home with its own identity; any Stagehand link stays with the original.
    public House DuplicateHome(Guid id)
    {
        var source = Houses.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Check that the home is still in your library.");
        var name = source.Name.Length > 113 ? source.Name[..113] : source.Name;
        var copy = SaveCopy(source with { Id = Guid.NewGuid(), Name = name + " (copy)", SavedAt = DateTimeOffset.UtcNow });
        Status = "Home duplicated.";
        return copy;
    }

    public House Create(string name, uint territory, bool custom = false)
    {
        RequireHousing(territory);
        var definition = new StageDefinition { FormatVersion = StageDefinition.CurrentFormatVersion, Info = new StageInfo { Name = name, IntendedTerritoryType = (int)territory } };
        var house = new House { Name = name, TerritoryId = territory, CustomLayout = custom,
            Stage = JsonSerializer.SerializeToElement(definition, StageDefinition.DefinitionStringSerializerOptions) };
        HouseFormat.Validate(house);
        Catalogue.Validate(house);
        SaveNewHome(house);
        Status = "Home created. Enter it, then press the wrench and Furnish to make it yours.";
        return house;
    }

    // Capturing homes from Stagehand stages, and recapturing them when Stagehand saved, was removed on 3 November 2026
    // (no tester used it, and nobody should be able to); homes captured before are ordinary homes.

    public void VisitSolo(House house)
    {
        RequireStagehand();
        Catalogue.Validate(house); Catalogue.BuildStage(house);
        RequireNoVisit();
        RequireHousing(house.TerritoryId); RequireInstalled(house.TerritoryId);
        Hide();
        ClearSharedEditing();
        active = house;
        publishedScene = null;
        EndingVisit = false;
        needsPublish = false;
        Interlocked.Exchange(ref receivedSnapshot, null);
        ownedVisit = house.Id;
        visitorHouse = null;
        undo.Clear(); redo.Clear();
        BeginSwitches(house, true);
        engine.VisitHousingSolo(house.TerritoryId);
        Status = "Loading home. Furnishings appear when the interior is ready.";
    }

    public void Host(House house)
    {
        RequireStagehand();
        Catalogue.Validate(house); Catalogue.BuildStage(house);
        RequireHousing(house.TerritoryId); RequireInstalled(house.TerritoryId);
        RequireNoVisit();
        RequireAccount();
        RequireAmbientShareable(house);
        // Detach arrays and JSON from the saved library before admission can begin.
        house = HouseFormat.Parse(HouseFormat.Serialize(house with { FormatVersion = 2 }));
        var scene = new PublishedHouseScene(house);
        CancelPlacement();
        Hide();
        engine.HostHousing(VisitCode);
        ClearSharedEditing(); sharedDraft = new(house);
        publishedScene = scene;
        nextPublish = nextReady = default;
        EndingVisit = false;
        needsPublish = false;
        Interlocked.Exchange(ref receivedSnapshot, null);
        pendingHost = house;
        ownedVisit = house.Id;
        connectDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
        active = house;
        visitorHouse = null;
        undo.Clear(); redo.Clear();
        BeginSwitches(house, true);
        Status = "Opening a gathering. Saved edits can be published without ending the visit. Friends must gather in rendering range before joining.";
    }

    public void Join()
    {
        RequireStagehand();
        RequireNoVisit();
        RequireAccount();
        Hide();
        ClearSharedEditing();
        active = pendingHost = visitorHouse = null;
        publishedScene = null;
        EndingVisit = false;
        needsPublish = false;
        Interlocked.Exchange(ref receivedSnapshot, null);
        ownedVisit = null;
        undo.Clear(); redo.Clear();
        switchStates.Clear(); persistSwitches = false;
        engine.JoinHousing(VisitCode);
        Status = "Joining the home. Waiting for host admission and the housing snapshot.";
    }

    public void Leave()
    {
        StopPreview();
        Leaving?.Invoke();
        CancelPlacement();
        runtime.Clear();
        Hide();
        ClearSharedEditing();
        active = pendingHost = visitorHouse = null;
        publishedScene = null;
        EndingVisit = false;
        ownedVisit = null;
        Interlocked.Exchange(ref receivedSnapshot, null);
        needsPublish = false;
        engine.LeaveHousing();
        Status = "Visit ended. Returning to your original location.";
    }

    public void StopVisit()
    {
        Leaving?.Invoke();
        if (engine.HousingRelay.IsConnected && engine.HousingRelay.IsHost && engine.HousingRelay.RoomJoinedAcknowledged)
        {
            if (EndingVisit) return;
            EndingVisit = true;
            engine.EndHousingVisit();
            Status = "Ending the gathering and returning everyone home…";
        }
        else Leave(); // Also cancels a pending connection, even before admission or house loading.
    }

    public void RetryHomeEntry()
    {
        if (!CanRetryEntry || active == null) throw new InvalidOperationException("Reconnect to the gathering before retrying house entry.");
        engine.LoadHostedHousing(active.TerritoryId);
        needsPublish = true;
        Status = "Retrying house entry in the existing gathering…";
    }

    public void EndVisitForEditing()
    {
        if (engine.HousingRelay.IsRecovering || !engine.HousingRelay.IsConnected)
            throw new InvalidOperationException("Reconnect before ending the gathering for everyone. Leave home remains available to return locally.");
        if (!IsSharedVisit || !OwnsActiveHouse || !engine.HousingRelay.IsHost || EndingVisit)
            throw new InvalidOperationException("Only the owner hosting this gathering can end it for editing.");
        CancelPlacement();
        EndingVisit = true;
        engine.EndHousingVisit();
        Status = "Ending the gathering for everyone. Once returned, enter your home privately to decorate.";
    }

    public void Export(House house, string path)
    {
        // The file dialog has already confirmed the destination, including replacing an existing file.
        using var stream = new FileStream(path, FileMode.Create);
        stream.Write(HouseFormat.Serialize(house));
        Status = "House exported.";
    }
    public void Import(string path)
    {
        var house = HouseStore.Read(path) with { Id = Guid.NewGuid(), SavedAt = DateTimeOffset.UtcNow };
        RequireHousing(house.TerritoryId);
        Catalogue.Validate(house);
        SaveCopy(house);
        Status = "House imported as a new saved home.";
    }
    public void SaveVisitorCopy()
    {
        var house = visitorHouse ?? throw new InvalidOperationException("No visitor snapshot available.");
        SaveCopy(house with { Id = Guid.NewGuid(), SavedAt = DateTimeOffset.UtcNow });
        Status = "Saved a local copy of this shared home.";
    }

    private void OnSnapshot(byte[] bytes) => Interlocked.Exchange(ref receivedSnapshot, bytes);
    private void OnLocationChanged(StageLocation _) => Interlocked.Exchange(ref locationChanged, 1);
    private void OnSceneChanging() { visuals.Clear(); lights.Clear(); effects.Clear(); previewEffect.Clear(); sounds.Clear(); Interlocked.Exchange(ref locationChanged, 1); }

    private ulong namedCharacter;
    public void Update()
    {
        Catalogue.NextFrame();
        if (engine.HousingCharacterId is var me && me != namedCharacter && engine.HousingCharacterName is { Length: > 0 } myName) { namedCharacter = me; Account.Note(me, myName); }
        for (var i = 0; i < 64 && actions.TryDequeue(out var action); i++) Run(action);
        Run(UpdateCustomPictures);
        Run(UpdateCustomMusic);
        Run(UpdateCustomFurniture);
        Run(UpdatePoses);
        Run(UpdateContentCache);
        Run(UpdatePreview);
        Run(UpdateAmbientReady);
        Run(UpdateServerDeletes);
        Run(() =>
        {
            if (Interlocked.Exchange(ref locationChanged, 0) != 0) { visuals.Clear(); lights.Clear(); rendered = false; HideAmbience(); }
            var received = Interlocked.Exchange(ref receivedSnapshot, null);
            if (received != null && engine.HousingRelay.RoomJoinedAcknowledged)
                ReceiveSharedHouse(HouseFormat.Parse(received));
            if (editBusy && DateTimeOffset.UtcNow > editDeadline)
            { editBusy = false; EditStatus = "No acknowledgement yet. Your draft is preserved; reconnect or retry saving."; }
            if (pendingHost != null && engine.HousingRelay.RoomJoinedAcknowledged && engine.HousingRelay.IsHost)
            {
                var house = pendingHost;
                pendingHost = null; // A failed load must not retry/throw every frame.
                Status = "The gathering is open. If house entry fails, use Retry house entry or Stop hosting under Visits.";
                engine.LoadHostedHousing(house.TerritoryId);
                needsPublish = true;
            }
            if (pendingHost != null && !engine.HousingRelay.RoomJoinedAcknowledged && DateTimeOffset.UtcNow > connectDeadline)
            {
                Leave();
                Status = "The hosting connection did not complete. It has been cancelled; you can try again.";
                return;
            }
            if (!engine.HousingSessionActive)
            {
                if (IsPlacing) CancelPlacement();
                if (rendered) Hide();
                visuals.Clear();
                lights.Clear();
                runtime.Clear();
                if (pendingHost == null)
                {
                    ClearSharedEditing();
                    publishedScene = null; EndingVisit = false;
                    active = visitorHouse = null; ownedVisit = null;
                    needsPublish = false;
                }
                else if (DateTimeOffset.UtcNow > connectDeadline)
                {
                    active = pendingHost = null;
                    publishedScene = null;
                    ownedVisit = null;
                    Status = "The hosting connection did not complete. Check the relay address and in-game chat.";
                }
                return;
            }
            if (active == null) engine.HousingReleaseAtmosphere();
            if (active != null)
            {
                UpdateAutoStaging();
                UpdateAutoPublishing();
                // Relay host succession keeps the visit alive; it does not transfer home ownership.
                var ready = engine.HousingZoneReady(active.TerritoryId);
                if (ready && (!rendered || needsRender)) { Show(active); needsRender = false; Status = "Visiting " + active.Name; }
                else if (!ready && rendered) Hide();
                engine.HousingKeepWeather(active.TerritoryId);
                // A time of day being dragged in the Room panel shows at once.
                var previewInterior = TimePreview is int previewTime ? (stagedScene?.Interior ?? active.Interior) with { TimeOfDay = previewTime } : stagedScene?.Interior;
                if (engine.HousingZoneReady(active.TerritoryId)) runtime.Update(active, MovingId, visuals.PlatformReady, previewInterior, SwitchOn);
                else { runtime.Clear(); engine.HousingReleaseAtmosphere(); }
                if (ready) visuals.Update(active, MovingId, placement, stagedFurniture, runtime.Doors, runtime.ClockSeconds, runtime.AnimationMilliseconds, SwitchOn, SwitchAge, FollowerPreviews); else visuals.Clear();
                // The lights Stagehand would otherwise draw: those of the home as drawn (Display), once it's shown.
                if (ready && rendered && HavenLights) lights.Update(Display(active), SwitchOn); else lights.Clear();
                UpdatePlacementEffect(ready && rendered);
                // Haven's own ambient sounds: one that finished plays again, as Stagehand's do.
                if (ready && rendered && HavenSounds) sounds.Update();
                if (rendered) { UpdateLapCues(runtime.AnimationMilliseconds); UpdateSwitchCues(); UpdateLapEffects(); }
                UpdateGhost();
                if (needsPublish && engine.HousingRelay.IsHost && engine.HousingRelay.IsConnected && engine.HousingRelay.RoomJoinedAcknowledged && DateTimeOffset.UtcNow >= nextPublish)
                {
                    needsPublish = false;
                    nextPublish = DateTimeOffset.UtcNow.AddSeconds(2);
                    _ = engine.HousingRelay.SendHousingSnapshot(HouseFormat.Serialize(sharedDraft?.Published ?? active));
                }
                if (publishedScene != null && IsReady && rendered && runtime.SceneReady && visuals.SceneReady
                    && !needsPublish && !engine.HousingRelay.HousingSceneReady && DateTimeOffset.UtcNow >= nextReady)
                {
                    nextReady = DateTimeOffset.UtcNow.AddSeconds(2);
                    _ = engine.HousingRelay.SendHousingReady(publishedScene.Hash);
                    Status = "House loaded. Joining the shared scene…";
                }
                if (engine.HousingRelay.HousingSceneReady && Status == "House loaded. Joining the shared scene…")
                    Status = "Shared home ready · " + active.Name;
            }
        });
    }

    private void Show(House house)
    {
        RequireStagehand();
        var t = house.Transform;
        var display = Display(house);
        if (stagedScene != null) t = stagedScene.Transform;
        var stage = Catalogue.BuildStage(display, excludeImported: importedPlacement?.Definition.Path, includeFurnitureModels: false, switchOn: SwitchOn,
            havenLights: HavenLights).ToDefinitionString();
        if (!stagehand.TryCreateOrUpdateTemporaryStageWithTransform(stage, LiveStage, t.Position, t.Orientation, t.Scale, house.Name)
            || !stagehand.TrySetTemporaryStageVisible(LiveStage, true)) throw new InvalidOperationException("Stagehand could not display the housing stage.");
        rendered = true;
        ShowAmbience(display);
    }
    // The home as drawn: without the piece being moved, and in a staged scene's stage and transform.
    private House Display(House house)
    {
        var display = MovingId == null ? house : house with { Furnishings = house.Furnishings.Where(x => x.Id != MovingId && !IsFollower(x.Id)).ToArray() };
        return stagedScene != null ? display with { Stage = stagedScene.Stage, Transform = stagedScene.Transform } : display;
    }
    // Loop-driven effects open and close windows each lap; the ambience is rebuilt only when one does.
    // Which lap of each looping effect shows, folded into one number (FNV-1a), so nothing is built each frame.
    private const ulong LapSeed = 14695981039346656037, LapPrime = 1099511628211;
    private ulong lapEffects = LapSeed;
    private void UpdateLapEffects()
    {
        // Also when only sounds are on: a world piece built after the home was shown brings its sounds too.
        if (active == null || !engine.HousingAmbientEffects && !(HavenSounds && engine.HousingAmbientSounds)) return;
        var clock = runtime.AnimationMilliseconds;
        var signature = LapSeed;
        foreach (var item in active.Furnishings)
        {
            // A world piece built since (its part is read in the background) brings its effects: count them in.
            if (item.BuildingPart != null)
            {
                var part = Catalogue.Prefab(item);
                if (HavenEffects) signature = (signature ^ (ulong)part.WorldEffects.Length) * LapPrime;
                if (HavenSounds) signature = (signature ^ (ulong)part.WorldSounds.Length << 20) * LapPrime;
                continue;
            }
            var prefab = Catalogue.Prefab(item);
            var laps = prefab.LoopEffects;
            for (var i = 0; i < laps.Length; i++) signature = (signature ^ (ulong)(laps[i].Loop.ShowingLap(clock, laps[i].AutoPlay) ?? long.MinValue)) * LapPrime;
            // A machine's result effects open and close their windows the same way.
            if (PlayingAction(item) is { } playing)
                foreach (var shown in prefab.ActionEffects)
                    if (shown.SubId == playing.SubId) signature = (signature ^ (ulong)(shown.Showing(playing.Since) ? playing.Started : 0)) * LapPrime;
        }
        if (signature == lapEffects) return;
        lapEffects = signature;
        ShowAmbience(Display(active));
    }
    // Local-only presentation: a failed or disabled ambience never blocks the furnished scene.
    // Only changed stages are sent. A sound is never updated in place: it is destroyed and
    // recreated when its furnishing moves or changes. Creation alone makes a stage visible.
    private void ShowAmbience(House display)
    {
        AmbientScene scene;
        try { scene = Catalogue.BuildAmbience(display, engine.HousingAmbientEffects, engine.HousingAmbientSounds, SwitchOn, runtime.AnimationMilliseconds, PlayingAction); }
        catch (InvalidDataException e) { log.Warning(e, "Furnishing effects are unavailable."); return; }
        // Haven's own effects, or Stagehand's ambient stage: never both.
        if (HavenEffects)
        {
            if (liveEffects != null) { stagehand.TryDestroyTemporaryStage(AmbientStage); liveEffects = null; }
            effects.Show(scene.EffectList);
        }
        else if (scene.Effects != liveEffects)
        {
            effects.Clear();
            if (scene.Effects == null) stagehand.TryDestroyTemporaryStage(AmbientStage);
            else if (!stagehand.TryCreateOrUpdateTemporaryStageWithTransform(scene.Effects, AmbientStage, Vector3.Zero, Quaternion.Identity, 1, display.Name + " effects"))
                log.Warning("Stagehand could not display furnishing effects.");
            liveEffects = scene.Effects;
        }
        // Haven's own sounds, or Stagehand's one stage per sound: never both.
        if (HavenSounds)
        {
            foreach (var key in liveSounds.Keys) stagehand.TryDestroyTemporaryStage(AmbientStage + "." + key);
            liveSounds.Clear();
            sounds.Show(scene.SoundList);
            return;
        }
        sounds.Show([]);
        foreach (var (key, definition) in liveSounds.ToArray())
            if (scene.Sounds.GetValueOrDefault(key) != definition) { stagehand.TryDestroyTemporaryStage(AmbientStage + "." + key); liveSounds.Remove(key); }
        foreach (var (key, definition) in scene.Sounds)
        {
            if (liveSounds.ContainsKey(key)) continue;
            if (stagehand.TryCreateOrUpdateTemporaryStageWithTransform(definition, AmbientStage + "." + key, Vector3.Zero, Quaternion.Identity, 1, display.Name + " sound")) liveSounds[key] = definition;
            else log.Warning("Stagehand could not play a furnishing sound.");
        }
    }
    private void HideAmbience()
    {
        if (StagehandStatus == StagehandApiAvailability.Available)
        {
            if (liveEffects != null) stagehand.TryDestroyTemporaryStage(AmbientStage);
            foreach (var key in liveSounds.Keys) stagehand.TryDestroyTemporaryStage(AmbientStage + "." + key);
        }
        StopSwitchCues();
        effects.Clear();
        previewEffect.Clear();
        sounds.Clear();
        liveEffects = null; liveSounds.Clear();
    }
    public void SetAmbience(bool effects, bool sounds)
    {
        engine.SetHousingAmbience(effects, sounds);
        needsRender = true;
    }
    private void Hide()
    {
        visuals.Clear();
        lights.Clear();
        if (StagehandStatus == StagehandApiAvailability.Available) stagehand.TryDestroyTemporaryStage(LiveStage);
        HideAmbience();
        rendered = false;
    }

    private void Commit(House house, bool visual = true, bool history = true, bool musicOnly = false)
    {
        if (!(musicOnly ? CanControlMusic : CanFurnish) || active == null)
            throw new InvalidOperationException("You need decorating access in a ready home before editing it.");
        house = HomeNpcs.DropLostSeats(house with { FormatVersion = 2, SavedAt = DateTimeOffset.UtcNow });
        HouseFormat.Validate(house); Catalogue.Validate(house);
        RequireShareableChange(house);
        if (visual) Catalogue.BuildStage(house);
        if (IsSharedVisit)
        {
            if (musicOnly) { _ = engine.HousingRelay.SetSharedMusic(house.Music); return; }
            sharedDraft ??= new(active);
            if (history) { PushHistory(undo, active); redo.Clear(); }
            house = house with { Music = sharedDraft.Published.Music };
            sharedDraft.Change(house); active = house; needsRender |= visual;
            ArchiveDraft();
            EditStatus = AutoStaging ? "Changes will stage automatically for the owner's approval." : AutoPublishing ? "Changes publish automatically." : "Private draft · save when all changes are ready.";
            RefreshStagedFurniture();
            if (AutoStaging && nextStage < DateTimeOffset.UtcNow) nextStage = DateTimeOffset.UtcNow.AddMilliseconds(200);
            if (AutoPublishing && nextAutoPublish < DateTimeOffset.UtcNow) nextAutoPublish = DateTimeOffset.UtcNow.AddMilliseconds(300);
            return;
        }
        Store.Save(house);
        if (history) { PushHistory(undo, active); redo.Clear(); }
        active = house; needsRender |= visual;
        Remember(house); Status = "Home saved.";
    }
    public void BeginPlacement(uint itemId, Guid? move = null)
        => BeginPlacement(new Furnishing { ItemId = itemId }, move, faceCamera: move == null);
    public void BeginBuilding(string path)
    {
        if (BuildingParts.IsAmbient(path) && IsSharedVisit && !AmbientShareable)
            throw new InvalidOperationException("World effects and sounds can be placed in Just me visits for now: gatherings can carry them once Haven's server is updated.");
        // A world piece is read now (once), so the preview shows it and rests it on the floor from the first frame; one
        // this PC can't draw isn't placed.
        if (BuildingParts.IsWorldAsset(path) && !Catalogue.CanBuild(path))
        {
            if (!World.Contains(path)) throw new InvalidOperationException("This piece isn't in this game's world.");
            if (World.Part(path, wait: true).Prefab.Models.Length == 0) throw new InvalidOperationException("This piece can't be drawn in this game.");
        }
        BeginPlacement(new Furnishing { BuildingPart = path, Scale = BuildingParts.IsAmbient(path) ? Ambient.StartingScale(path) : 1 }, faceCamera: true);
    }
    /// <summary>Every model and group the game places in its world, to place as building pieces.</summary>
    public WorldCatalogue World { get; }
    /// <summary>The world's effects and ambient sounds as pieces.</summary>
    public AmbientCatalogue Ambient { get; }
    /// <summary>Where the camera looks (world), for turning a new piece toward it; null when unknown.</summary>
    public Func<Vector3?>? CameraForward { get; set; }
    /// <param name="inPlace">A copy: it starts exactly where the template stands, to be dragged out from there.</param>
    public void BeginPlacement(Furnishing template, Guid? move = null, bool faceCamera = false, bool inPlace = false)
    {
        if (!CanFurnish || active == null) throw new InvalidOperationException("Visit a home you can furnish first.");
        // A move under way is kept, not lost, when another piece is picked up (a new piece's preview is just replaced).
        if (MovingExisting && (placement == null || placement.Id != move)) KeepPlacement(); else CancelPlacement();
        Catalogue.Prefab(template);
        placement = move == null ? template with { Id = Guid.NewGuid() } : active.Furnishings.First(x => x.Id == move);
        // A piece fresh from the catalogue turns its front toward the camera, a quarter turn at a time (copies keep theirs).
        if (move == null && faceCamera && CameraForward?.Invoke() is { } forward)
            placement = placement with { Rotation = PlacementMath.FacingCamera(forward, FurnitureCatalogue.HouseMatrix(active)) };
        if (move == null && !inPlace && engine.HousingPlayerPosition is {} player && Matrix4x4.Invert(FurnitureCatalogue.HouseMatrix(active), out var inverse))
        {
            // Set down at the feet, resting on the floor: a wall piece would otherwise sink up to its anchor.
            var local = Vector3.Transform(player, inverse); placement = placement with { Position = [local.X, local.Y, local.Z] };
            if (Catalogue.Bounds(placement) is {} box) placement = placement with { Position = [local.X, local.Y + PlacementTools.Rest(box.Minimum.Y - local.Y, true), local.Z] };
        }
        PreviewHasTarget = move != null || inPlace;
        movingExisting = move != null; needsRender = movingExisting; ghostUpdated = DateTimeOffset.MinValue;
        Status = inPlace ? "The copy stands on the original: drag a handle or use the arrow keys (F follows the cursor); click, right-click or Done sets it down, Escape cancels."
            : "Move the preview with your cursor; click, right-click or Done sets it down, R rotates, Escape cancels.";
    }
    public void MovePreview(Vector3 world, float rotationDelta = 0)
    {
        if (active == null) return;
        if (importedPlacement != null && importedGhostTarget != null)
        {
            if (!Matrix4x4.Invert(importedPlacement.Definition.ParentTransform * FurnitureCatalogue.HouseMatrix(active), out var parentInverse))
                throw new InvalidOperationException("This imported group has a non-invertible scale.");
            importedGhostTarget.Position = Vector3.Transform(world, parentInverse);
            PreviewHasTarget = true;
            return;
        }
        if (placement == null) return;
        Matrix4x4.Invert(FurnitureCatalogue.HouseMatrix(active), out var inverse);
        var local = Vector3.Transform(world, inverse);
        placement = placement with { Position = [local.X, local.Y, local.Z], Rotation = (placement.Rotation + rotationDelta) % 360 };
        PreviewHasTarget = true;
    }
    public void RotatePreview(float degrees)
    {
        if (placement != null) placement = placement with { Rotation = (placement.Rotation + degrees) % 360 };
        if (importedGhostTarget != null) importedGhostTarget.RotationPitchYawRollDegrees += new Vector3(0, degrees, 0);
    }
    public void Place()
    {
        if (!PreviewHasTarget) return;
        // A new custom piece whose files are gone (deleted meanwhile) is never set down invisible.
        if (placement is { CustomFurniture: { } hash } && !movingExisting && !Furniture.Has(hash))
        { CancelPlacement(); throw new InvalidOperationException("That custom furnishing was deleted, so it can't be placed."); }
        if (active != null && importedPlacement != null && importedGhostTarget != null)
        {
            var target = importedGhostTarget;
            CommitImported(StageObjects.Transform(active.Stage, importedPlacement.Definition.Path, target.Position, target.RotationPitchYawRollDegrees, target.Scale));
            CancelPlacement(); return;
        }
        if (placement == null || active == null) return;
        var item = placement;
        Commit(WithPlacement(active, item));
        CancelPlacement();
    }
    /// <summary>
    /// Ends a placement keeping it where it is (a right-click, Done, picking up another piece): only Escape and Cancel
    /// throw a move away. A new piece that has nowhere to stand yet is dropped.
    /// </summary>
    public void KeepPlacement()
    {
        if (!IsPlacing) return;
        if (PreviewHasTarget) Place(); else CancelPlacement();
    }
    public void CancelPlacement()
    {
        if (placement != null && movingExisting || importedPlacement != null) needsRender = true;
        if (followers.Length > 0 && movingExisting) needsRender = true;
        placement = null; movingExisting = false; ClearFollowers();
        IsFineEditing = false; PreviewHasTarget = false;
        importedPlacement = null; importedGhost = null; importedGhostTarget = null;
        if (StagehandStatus == StagehandApiAvailability.Available) stagehand.TryDestroyTemporaryStage(GhostStage);
    }
    private void UpdateGhost()
    {
        if (!IsPlacing || active == null || !IsReady || importedGhost == null) return;
        if (DateTimeOffset.UtcNow - ghostUpdated < TimeSpan.FromMilliseconds(50)) return;
        ghostUpdated = DateTimeOffset.UtcNow;
        string definition;
        definition = importedGhost.ToDefinitionString();
        var t = active.Transform;
        stagehand.TryCreateOrUpdateTemporaryStageWithTransform(definition, GhostStage, t.Position, t.Orientation, t.Scale, "Placement preview");
        stagehand.TrySetTemporaryStageVisible(GhostStage, true);
    }
    public void Remove(Guid id)
    {
        if (active == null) return;
        CancelPlacement();
        if (active.Furnishings.Any(x => x.Id == id)) Commit(active with { Furnishings = active.Furnishings.Where(x => x.Id != id).ToArray() });
    }
    public void ChangeFurniture(Furnishing item)
    {
        if (active == null) return;
        if (!CanFurnish) throw new InvalidOperationException("Only the host can furnish this home.");
        if (Catalogue.Get(item).DyeSlots == 0) item = item with { StainId = 0 };
        if ((item.SurfaceFinish != 0 || item.Picture != 0) && !BuildingFinishesAvailable) throw new InvalidOperationException("Surface finishes and paintings are unavailable in this game build.");
        if (placement?.Id == item.Id) { placement = item; return; }
        CancelPlacement(); Commit(active with { Furnishings = active.Furnishings.Select(x => x.Id == item.Id ? item : x).ToArray() });
    }
    public void PreviewFineFurniture(Furnishing item)
    {
        if (!CanFurnish || active == null) throw new InvalidOperationException("Only the host can furnish this home.");
        HouseFormat.Validate(active with { Furnishings = [item] });
        if (placement?.Id != item.Id) { BeginPlacement(item, item.Id); IsFineEditing = true; }
        placement = item; PreviewHasTarget = true;
    }
    public void PreviewFineImported(string key, Vector3 position, Vector3 rotation, Vector3 scale)
    {
        if (!CanFurnish) throw new InvalidOperationException("Only the host can edit imported furnishings.");
        if (!float.IsFinite(position.LengthSquared()) || !float.IsFinite(rotation.LengthSquared()) || !float.IsFinite(scale.LengthSquared())) throw new InvalidDataException("Invalid imported transform.");
        if (MovingImportedKey != key) { BeginImportedMove(key); IsFineEditing = true; }
        importedGhostTarget!.Position = position; importedGhostTarget.RotationPitchYawRollDegrees = rotation; importedGhostTarget.Scale = scale;
    }
    public (Vector3 Position, Vector3 Rotation, Vector3 Scale)? ImportedPreview(string key) => MovingImportedKey == key && importedGhostTarget != null
        ? (importedGhostTarget.Position, importedGhostTarget.RotationPitchYawRollDegrees, importedGhostTarget.Scale) : null;
    public void NudgePreview(Vector3 delta)
    {
        if (placement != null) { var p = placement.Translation + delta; placement = placement with { Position = [p.X, p.Y, p.Z] }; }
        else if (importedPlacement != null && importedGhostTarget != null && Matrix4x4.Invert(importedPlacement.Definition.ParentTransform, out var inverse))
            importedGhostTarget.Position += Vector3.TransformNormal(delta, inverse);
        PreviewHasTarget = true;
    }
    public void ScalePreview(float factor)
    {
        if (placement != null) placement = placement with { Scale = Math.Clamp(placement.Scale * factor, 0.05f, 10) };
        if (importedGhostTarget != null) importedGhostTarget.Scale = PlacementTools.ScaleProportionally(importedGhostTarget.Scale, factor);
    }
    /// <summary>A copy of a piece, standing exactly where it does (same position, turn and size) until it's moved.</summary>
    public void BeginDuplicate(Furnishing item) => BeginPlacement(item, inPlace: true);
    public void BeginImportedMove(string key)
    {
        if (!CanFurnish || active == null) throw new InvalidOperationException("Only the host can edit imported furnishings.");
        CancelPlacement();
        importedPlacement = Imported.Objects(active.Stage).First(x => x.Definition.Key == key);
        (importedGhost, importedGhostTarget) = ImportedScene.Ghost(active.Stage, importedPlacement.Definition.Path);
        PreviewHasTarget = true;
        needsRender = true; ghostUpdated = DateTimeOffset.MinValue;
        Status = "Moving imported furnishing. Click, right-click or Done sets it down; R rotates; Escape cancels.";
    }
    public void RemoveImported(string key)
    {
        if (active == null) return;
        var target = Imported.Objects(active.Stage).First(x => x.Definition.Key == key);
        CancelPlacement(); CommitImported(StageObjects.Remove(active.Stage, target.Definition.Path));
    }
    public void ChangeImported(string key, Vector3 position, Vector3 rotation, Vector3 scale)
    {
        if (active == null) return;
        var target = Imported.Objects(active.Stage).First(x => x.Definition.Key == key);
        CancelPlacement();
        CommitImported(StageObjects.Transform(active.Stage, target.Definition.Path, position, rotation, scale));
    }
    private void CommitImported(JsonElement stage)
    {
        if (active == null) return;
        Commit(active with { Stage = stage });
    }
    public void SetInterior(InteriorOptions options)
    {
        if (active == null) return;
        Commit(active with { Interior = options }, active.CustomLayout);
        runtime.RefreshLighting();
    }
    public void ReturnToPlatform()
    {
        if (active?.CustomLayout != true || !IsReady) throw new InvalidOperationException("Wait for the custom platform to finish loading.");
        engine.HousingStandUpAt(CustomCanvas.Spawn);
    }
    // An edit to a playing playlist is re-timed so the track playing now carries on (HousingRuntime.KeepPlaying).
    // The one-shot timeline each furnishing is playing (an arcade machine's reaction), by SubId, and when it began.
    private readonly Dictionary<Guid, (int SubId, long At)> machineActions = [];
    private (int SubId, long Since)? MachineAction(Furnishing item) =>
        machineActions.TryGetValue(item.Id, out var action) ? (action.SubId, Environment.TickCount64 - action.At) : null;
    private (int SubId, long Since, long Started)? PlayingAction(Furnishing item) =>
        machineActions.TryGetValue(item.Id, out var action) ? (action.SubId, Environment.TickCount64 - action.At, action.At) : null;
    /// <summary>Plays one of a furnishing's own timelines by its index, as the game numbers them (0: back at rest).</summary>
    public void PlayMachineTimeline(Guid item, byte index)
    {
        var furnishing = active?.Furnishings.FirstOrDefault(f => f.Id == item);
        var table = furnishing == null ? [] : Catalogue.Prefab(furnishing).ActionTable;
        if (furnishing == null) return;
        PlayAction(furnishing, index == 0 || index >= table.Length ? 0 : table[index], share: true);
    }

    // A furnishing's one-shot timeline by SubId (0: back to rest), with its sounds; shared, it plays for everyone in the
    // gathering too (a machine's result, the Ale Tap's pour), and the timeline itself runs on each PC.
    private void PlayAction(Furnishing item, int subId, bool share)
    {
        if (subId == 0) machineActions.Remove(item.Id);
        else { machineActions[item.Id] = (subId, Environment.TickCount64); PlayActionSounds(item, subId); }
        if (share && engine.HousingRelay.IsConnected)
            _ = engine.HousingRelay.SendMiniGame(new GameMessage(Me, GameMessage.Everyone, "machine", "play", item.Id, [subId]));
    }

    // Another participant's machine result or furnishing use: the same timeline here.
    private void OnMachineMessage(GameMessage message)
    {
        if (message.Game != "machine" || message.Kind != "play" || message.Values is not [var subId]) return;
        Run(() =>
        {
            if (active?.Furnishings.FirstOrDefault(f => f.Id == message.Match) is not { } item) return;
            var prefab = Catalogue.Prefab(item);
            if (subId < 0 || subId != 0 && prefab.UseTimeline != subId && (subId > byte.MaxValue || !prefab.ActionTable.Contains((byte)subId))) return;
            PlayAction(item, subId, share: false);
        });
    }
    /// <summary>Plays a game music file instead of the home's (a minigame's music); null resumes the home's.</summary>
    public void OverrideMusic(string? file) => runtime.MusicOverride = file;
    public void SetMusic(MusicOptions options) { if (active != null) Commit(active with { Music = runtime.KeepPlaying(active.Music, options) }, false, musicOnly: true); }
    public void Undo()
    {
        if (active == null || undo.Count == 0) return;
        CancelPlacement();
        var previous = undo.Peek(); var current = active;
        Commit(previous, history: false); undo.Pop(); PushHistory(redo, current);
    }
    public void Redo()
    {
        if (active == null || redo.Count == 0) return;
        CancelPlacement();
        var next = redo.Peek(); var current = active;
        Commit(next, history: false); redo.Pop(); PushHistory(undo, current);
    }
    private static void PushHistory(Stack<House> stack, House house)
    {
        stack.Push(house);
        if (stack.Count <= 64) return;
        var kept = stack.Take(64).Reverse().ToArray();
        stack.Clear();
        foreach (var entry in kept) stack.Push(entry);
    }
    public void UseFurniture(Guid id, int seat = 0)
    {
        if (active == null || !IsReady) return;
        var item = active.Furnishings.First(x => x.Id == id);
        runtime.UseSeat(active, item, seat);
    }
    public void Stand() => runtime.Stand();
    private bool CanUseNativeFurniture(ushort emote) => FurnitureInteractions.WantsBed(emote) != null && active != null && !IsPlacing && IsReady && runtime.CanUseNativeEmote(active, emote);
    private bool UseNativeFurniture(ushort emote) => FurnitureInteractions.WantsBed(emote) != null && active != null && !IsPlacing && IsReady && runtime.UseNativeEmote(active, emote);
    private void RequireStagehand()
    { if (StagehandStatus != StagehandApiAvailability.Available) throw new InvalidOperationException("Install or enable Stagehand with compatible IPC API 1.2."); }
    // Every entry point (solo, host, join) passes here; the engine re-checks before the sandbox zone loads.
    private void RequireAccount()
    {
        if (!Account.SignedIn && AccountSignIn.Required(engine.HousingRelayUrl))
            throw new InvalidOperationException("Gatherings need a Haven account. Sign in with Discord in Settings → Connection, then try again.");
    }

    private void RequireNoVisit()
    {
        if (!CanStartVisit) throw new InvalidOperationException("Leave the current visit and wait for your return to finish before entering another home.");
        if (engine.HousingEntryBlocked is {} blocked) { engine.ShowHousingError(blocked); throw new InvalidOperationException(blocked); }
    }
    private void RequireHousing(uint territory)
    { if (!engine.IsHavenTerritory(territory)) throw new InvalidOperationException("Select a housing interior or an inn room from the territory picker."); }
    // Entering or hosting a home whose zone this install lacks (an Endwalker or Dawntrail inn on a free trial) would
    // close the game: refused with the reason, as a red error. Making, importing and keeping such a home are allowed.
    private void RequireInstalled(uint territory)
    {
        if (engine.HousingTerritoryUnavailable(territory) is not { } missing) return;
        engine.ShowHousingError(missing);
        throw new InvalidOperationException(missing);
    }
    public void Dispose()
    {
        engine.RegisterHousingExit(null);
        engine.RegisterHousingEmotes(null, null);
        engine.HousingSceneChanging -= OnSceneChanging;
        // Each step on its own: one that fails mustn't leave the others' Penumbra mods, Stagehand stages or worn props
        // behind in the real world (the custom animations' carrier emotes would keep playing them).
        void Step(string what, Action step)
        {
            try { step(); }
            catch (Exception ex) { log.Warning(ex, "[Haven] Shutting down {What} failed.", what); }
        }
        Step("the world catalogue", World.Dispose);
        Step("the home's runtime", runtime.Dispose);
        Step("the furnishings", visuals.Dispose);
        Step("the furnishing lights", lights.Dispose);
        Step("the furnishing effects", effects.Dispose);
        Step("the placement's effect", previewEffect.Dispose);
        Step("the furnishing sounds", sounds.Dispose);
        Step("the sound preview", StopPreview);
        Step("the account", () => Account?.Dispose());
        Step("custom songs", () => Songs?.Dispose());
        Step("custom furniture", () => Furniture?.Dispose());
        Step("pose balls", DisposePoses);
        Step("notices", DisposeNotices);
        Step("custom pictures", pictures.Dispose);
        engine.HousingRelay.OnHousingSnapshot -= OnSnapshot;
        engine.HousingRelay.OnRecovered -= OnEditingRecovered;
        engine.HousingRelay.OnEditing -= OnEditing;
        engine.HousingRelay.OnSwitchStates -= OnSwitchStates;
        engine.HousingRelay.OnHousingSceneReady -= OnSwitchSceneReady;
        engine.HousingRelay.OnEditProposal -= OnProposal;
        engine.HousingRelay.OnEditResult -= OnEditResult;
        engine.HousingRelay.OnEditApplied -= OnEditApplied;
        stagehand.LocationChanged -= OnLocationChanged;
        Step("the stages", Hide);
        Step("the ghost stage", () => { if (StagehandStatus == StagehandApiAvailability.Available) stagehand.TryDestroyTemporaryStage(GhostStage); });
        stagehand.Dispose();
    }
}
