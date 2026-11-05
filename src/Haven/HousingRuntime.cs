using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Haven.Core;
using HavenNative;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using Dalamud.Plugin.Services;

namespace Haven;

internal sealed unsafe class HousingRuntime : IDisposable
{
    // WhenOn: solid only while the item's switch is on that side (a closed container door).
    private readonly record struct LiveCollider(nint Pointer, int Door, bool? WhenOn = null);
    private sealed record LiveFurniture(Furnishing Definition, List<LiveCollider> Colliders);
    private readonly NativeEngine engine;
    private readonly FurnitureCatalogue catalogue;
    private readonly HousingLightingGuard lightingGuard;
    private readonly InteriorLighting lighting = new();
    private readonly OrchestrionDurations musicDurations;
    private readonly IDataManager data;
    private readonly MusicPlaybackClock musicClock = new();
    public bool MusicReady { get; private set; }
    public string MusicStatus { get; private set; } = "";
    public MusicSlot? MusicState(MusicOptions options)
    {
        MusicReady = musicDurations.TryResolve(options.Playlist, out var durations, out var status);
        MusicStatus = status;
        return MusicReady ? musicClock.Locate(options, MusicNow, durations) : null;
    }
    private readonly Dictionary<Guid, LiveFurniture> furniture = [];
    private readonly List<Vector3> doorOccupants = [];
    public FurnitureDoorScene Doors { get; }
    public long ClockSeconds => engine.HousingEorzeaTime;
    public long AnimationMilliseconds => MusicNow;
    private readonly Dictionary<string, nint> paths = [];
    private nint boundLayout, boundScene;
    private uint territory;
    private Matrix4x4 houseMatrix;
    private readonly InteriorPreview interior = new();
    private readonly InteriorWatch interiorWatch = new();
    private Furnishing[]? colliderFurnishings;
    private Guid? colliderMoving;
    private readonly IPluginLog log;
    // The home the watch belongs to, and when (Environment.TickCount64) this scene's interior was first asked for.
    private Guid watchedHouse;
    private long interiorAskedAt = -1;
    private MusicPlaybackKey? musicKey;
    private bool musicOwned;
    private readonly long musicWallAnchor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long musicTickAnchor = Environment.TickCount64;
    private long MusicNow => musicWallAnchor + Environment.TickCount64 - musicTickAnchor;
    private long nextMusicRecovery;
    private long? musicLostSince;
    private Vector3? standingPosition;
    private Furnishing? seatedFurniture;
    private nint starterCollider;
    private readonly CanvasArrival canvasArrival = new();
    private bool canvasExitLogged;
    public bool CustomReady => canvasArrival.Ready;
    // An inn room has no finishes to wait for (0.58.3 waited for them, so the loading screen held for its whole minute).
    public bool SceneReady => boundScene != 0 && !lighting.Pending
        && (canvasArrival.Ready || innRoom || interior.Requested != null && !interior.Pending);
    private bool innRoom;
    public int ColliderCount => furniture.Values.Sum(x => x.Colliders.Count) + (starterCollider == 0 ? 0 : 1);
    public string FixtureStatus { get; private set; } = "";
    public void RefreshLighting() => lighting.Invalidate();

    public HousingRuntime(NativeEngine engine, FurnitureCatalogue catalogue, IDataManager data, IPluginLog log)
    { this.engine = engine; this.catalogue = catalogue; this.data = data; this.log = log; Doors = new(catalogue); musicDurations = new(data); lightingGuard = engine.CreateHousingLightingGuard(); engine.HousingSceneChanging += Clear; engine.HousingSceneChanging += engine.HousingReleaseAtmosphere; }

    public void Update(House house, Guid? moving, bool platformReady = false, InteriorOptions? previewInterior = null, Func<Furnishing, bool>? switchOn = null)
    {
        if (!engine.HousingZoneReady(house.TerritoryId)) { Clear(); engine.HousingReleaseAtmosphere(); return; }
        if (house.Id != watchedHouse) { watchedHouse = house.Id; interiorWatch.Reset(); }
        if (standingPosition != null && !engine.HousingIsSeated) { standingPosition = null; seatedFurniture = null; }
        if (seatedFurniture is {} seated && (seated.Id == moving || !house.Furnishings.Any(x => x.Id == seated.Id && Same(x, seated)))) Stand();
        var world = LayoutWorld.Instance(); var layout = world->ActiveLayout;
        var scene = Scene();
        var matrix = FurnitureCatalogue.HouseMatrix(house);
        if (boundLayout != (nint)layout || boundScene != (nint)(scene == null ? null : scene->Scene) || houseMatrix != matrix)
        {
            // Collision-scene or transform changes must not recapture a temporary
            // lighting override as the shell's original brightness.
            var original = boundLayout == (nint)layout ? lighting.OriginalLevel : layout->IndoorAreaData != null ? layout->IndoorAreaData->LightLevel : 1;
            Clear(); boundLayout = (nint)layout; boundScene = (nint)(scene == null ? null : scene->Scene);
            territory = house.TerritoryId; houseMatrix = matrix;
            lighting.Bind(original, engine.HousingIsGPosing);
        }
        var decor = previewInterior ?? house.Interior;
        // The home's weather and time of day (moods), held once a second; a dragged time follows at once.
        engine.HousingHoldAtmosphere(house.TerritoryId, decor.Weather, decor.TimeOfDay, previewInterior != null);
        engine.HousingHoldOutside(house.TerritoryId, decor.Outside, decor.TimeOfDay);
        // An inn room has no housing finishes or brightness: its own lighting is left alone, and nothing is asked of it.
        var inn = innRoom = engine.IsInnTerritory(house.TerritoryId);
        var gposeChanged = lighting.Observe(decor.Brightness, engine.HousingIsGPosing);
        // (The room counts as lit at once, or SceneReady, which waits for brightness, never would be.)
        if (inn) { lightingGuard.Clear(); if (lighting.Pending) lighting.Applied(); }
        else
        {
            lightingGuard.Bind(boundLayout, territory, lighting.Level);
            if (gposeChanged) lightingGuard.ReportTransition(engine.HousingIsGPosing);
        }
        // A consumed preview buffer does not retain baseline fixture IDs. Reloading the local
        // shell restores its original assets; the next update replays remaining overrides.
        if (!house.CustomLayout && !inn && InteriorPlan.NeedsShellReset(interior.Requested, decor))
        { engine.HousingReloadShell(); return; }
        Doors.Bind(house, moving);
        if (Doors.HasDoors)
        { engine.HousingOccupantPositions(doorOccupants); Doors.Update(doorOccupants, Environment.TickCount64, switchOn); }
        // Colliders follow the furnishings: compared only when the home's list (a new array on every edit) or the piece
        // being moved changed, not every frame.
        if (!ReferenceEquals(house.Furnishings, colliderFurnishings) || moving != colliderMoving)
        {
            colliderFurnishings = house.Furnishings; colliderMoving = moving;
            var live = house.Furnishings.Where(x => x.Id != moving && x.Collision).ToDictionary(x => x.Id);
            foreach (var id in furniture.Keys.Where(id => !live.ContainsKey(id) || !Same(furniture[id].Definition, live[id])).ToArray()) Remove(id);
            foreach (var item in live.Values)
                if (!furniture.ContainsKey(item.Id)) furniture[item.Id] = new(item, Add(item));
        }
        foreach (var item in furniture.Values)
        foreach (var collider in item.Colliders)
            if (collider.Door >= 0 || collider.WhenOn != null)
            {
                var pointer = (Collider*)collider.Pointer;
                var blocks = collider.Door >= 0 ? Doors.Blocks(item.Definition.Id, collider.Door)
                    : (switchOn?.Invoke(item.Definition) ?? catalogue.Prefab(item.Definition).Switch?.DefaultOn) == collider.WhenOn;
                if (blocks) pointer->VisibilityFlags |= 1;
                else pointer->VisibilityFlags &= 0xFE;
            }
        if (house.CustomLayout)
        {
            UpdateCanvas(platformReady);
            if (lighting.Pending)
            {
                if (HousingLightingGuard.Submit(layout, lighting.Level) != 0) throw new InvalidOperationException("Custom lighting is waiting for its private scene.");
                lighting.Applied();
            }
        }
        else
        {
            UpdateRemodel(house);
            if (!inn && ApplyInterior(decor)) return;
        }
        ApplyMusic(house.Music);
    }

    // Remodelling: the home's removed shell pieces are hidden twice a second (a shell reload brings
    // them back), and while any is gone without its collision, someone who falls well below the lowest floor or wanders
    // far off is put back where they last stood on something.
    private long remodelAt, groundAt;
    private string remodelLogged = "";
    private bool remodelReshownLogged;
    private Vector3? lastGround;
    private uint lowestFor; private float lowestFloor; private Vector3 roomMiddle;
    private void UpdateRemodel(House house)
    {
        var now = Environment.TickCount64;
        var hidden = house.Interior.HiddenPieces ?? [];
        if (now >= remodelAt)
        {
            remodelAt = now + 500;
            // The free company door is never part of a Haven home (the game shows it when entered from real housing).
            var hide = hidden.ToDictionary(x => x.Instance, x => x.KeepCollision);
            foreach (var door in catalogue.CompanyDoors(house.TerritoryId)) hide.TryAdd(door, false);
            // An inn piece's lights and collision boxes go with it.
            var attached = catalogue.ShellPieces(house.TerritoryId).Where(x => x.Attached != null).ToDictionary(x => x.Instance, x => x.Attached!);
            var found = engine.HousingShellRemodel(hide, attached);
            var summary = $"{found} of {hide.Count}";
            if (summary != remodelLogged && (hidden.Length > 0 || remodelLogged.Length > 0)) { remodelLogged = summary; log.Information($"[REMODEL] {found} of {hide.Count} removed shell pieces hidden (the company door included)."); }
        }
        // Some parts the game shows again by itself (a window's light shaft), so they're held hidden every frame.
        if (hidden.Length > 0 && engine.HousingHoldShellRemodel() is var reshown and > 0 && !remodelReshownLogged)
        { remodelReshownLogged = true; log.Information($"[REMODEL] the game showed {reshown} removed part(s) again; holding them hidden every frame."); }
        if (!hidden.Any(x => !x.KeepCollision) || engine.HousingPlayerPosition is not Vector3 player) { lastGround = null; return; }
        if (lowestFor != house.TerritoryId)
        {
            var pieces = catalogue.ShellPieces(house.TerritoryId);
            var floors = pieces.Where(x => x.Category == "Floors").ToArray();
            // An inn's pieces are all "Inn room" (its floors among them), and the room needn't sit at the middle of its
            // map: below the lowest of them, or far from their middle. Without this, inns had no fall recovery.
            var inn = floors.Length == 0;
            if (inn) floors = [.. pieces.Where(x => x.Category == BuildingCatalogue.InnRoom)];
            if (floors.Length == 0) return;
            lowestFor = house.TerritoryId;
            lowestFloor = floors.Min(x => Vector3.Transform(x.Minimum, x.World).Y);
            roomMiddle = inn ? (floors.Select(x => Vector3.Transform((x.Minimum + x.Maximum) / 2, x.World)).Aggregate(Vector3.Zero, (a, b) => a + b) / floors.Length) with { Y = 0 } : Vector3.Zero;
        }
        if (ShellRemodel.NeedsRecovery(player - roomMiddle, lowestFloor))
        {
            if (lastGround is Vector3 safe) { engine.HousingStandUpAt(safe); log.Information($"[REMODEL] fell through a removed piece; brought back to {safe}."); }
            return;
        }
        // Somewhere to come back to: where the player last stood on something, checked once a second.
        if (now >= groundAt && NativeEngine.HousingSurfaceBelow(player + new Vector3(0, .5f, 0), 1) is Vector3 ground && player.Y - ground.Y < .3f)
        { lastGround = ground + new Vector3(0, .05f, 0); groundAt = now + 1000; }
    }

    private void UpdateCanvas(bool platformReady)
    {
        var scene = Scene();
        if (scene == null || scene->Scene == null) { canvasArrival.Clear(); FixtureStatus = "Waiting for the room to load…"; return; }
        if (starterCollider == 0)
        {
            // The audited loft spans X ±4, Z approximately ±4 with its top at Y=0.
            // A synchronous box supports arrival while any furniture meshes load.
            var center = new Vector3(0, -.25f, 0); var rotation = Vector3.Zero; var half = new Vector3(4, .25f, 4);
            starterCollider = (nint)scene->AddColliderBox(1, &center, &rotation, &half);
            if (starterCollider == 0) throw new InvalidOperationException("The starter platform collision could not be created. Leave and retry the custom home.");
            ((Collider*)starterCollider)->LayoutObjectId = 0; // Owned floor, outside native-shell suppression.
        }
        var isolated = platformReady && engine.HousingHideCustomShell();
        if (canvasArrival.Update(isolated, engine.HousingPlayerPosition)) engine.HousingStandUpAt(CustomCanvas.Spawn);
        // Logged once a visit: if the game kept putting it back, this would move it every frame.
        if (isolated && engine.HousingPlaceCanvasExit(CustomCanvas.Exit) is > 0 and var moved && !canvasExitLogged)
        { canvasExitLogged = true; log.Information($"[CANVAS] stood {moved} Exit marker(s) on the starter platform."); }
        FixtureStatus = CustomReady ? "If you fall off or wander far from the canvas, you're brought back to the starter platform."
            : "Preparing the starter platform and private blank canvas…";
    }

    private static bool Same(Furnishing a, Furnishing b) => a.ItemId == b.ItemId && a.BuildingPart == b.BuildingPart && a.Rotation == b.Rotation
        && a.Pitch == b.Pitch && a.Roll == b.Roll && a.Portrait == b.Portrait && a.Scale == b.Scale && a.Collision == b.Collision && a.Position.SequenceEqual(b.Position);
    private static SceneWrapper* Scene()
    {
        var framework = CSFramework.Instance(); var module = framework == null ? null : framework->BGCollisionModule;
        return module == null || module->SceneManager == null ? null : module->SceneManager->FirstScene;
    }
    private List<LiveCollider> Add(Furnishing item)
    {
        List<LiveCollider> result = []; var scene = Scene(); if (scene == null) return result;
        foreach (var part in catalogue.Prefab(item).Colliders)
        {
            var matrix = part.Transform * FurnitureCatalogue.ItemMatrix(item) * houseMatrix;
            Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position);
            // The game's layout angle order (X, then Y, then Z), which the collision scene takes: tilted pieces were
            // confirmed solid in the right places in game on 4 November 2026 (0.63.0).
            var angles = LayoutRotation.Angles(rotation);
            Collider* collider;
            if (part.Path != null)
            {
                if (!paths.TryGetValue(part.Path, out var address)) paths[part.Path] = address = Marshal.StringToCoTaskMemUTF8(part.Path);
                collider = (Collider*)scene->AddColliderMesh(1, (byte*)address, false, &position, &angles, &scale);
            }
            else
            {
                var center = Vector3.Transform((part.Minimum + part.Maximum) / 2, matrix);
                var half = Vector3.Abs((part.Maximum - part.Minimum) * scale / 2);
                if (half.X < 0.001f || half.Y < 0.001f || half.Z < 0.001f) continue;
                collider = (Collider*)scene->AddColliderBox(1, &center, &angles, &half);
            }
            if (collider != null) { collider->LayoutObjectId = 0; result.Add(new((nint)collider, part.Door, part.WhenOn)); }
        }
        return result;
    }
    private void Remove(Guid id)
    {
        var item = furniture[id]; var scene = Scene();
        if (scene != null && (nint)scene->Scene == boundScene)
            foreach (var collider in item.Colliders) scene->RemoveCollider((Collider*)collider.Pointer);
        furniture.Remove(id);
    }
    // Yaw-pitch-roll angles (Z, X, then Y); only the seat's turn (Y) is read, and seats are always level.
    private static Vector3 Euler(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        return new(MathF.Asin(Math.Clamp(-m.M32, -1, 1)), MathF.Atan2(m.M31, m.M33), MathF.Atan2(m.M12, m.M22));
    }

    /// <returns>True when the shell is being loaded again (a stuck interior), so the rest of this update must wait.</returns>
    private bool ApplyInterior(InteriorOptions options)
    {
        var now = Environment.TickCount64;
        // Once settled, every finish is sent again twice over (2.5 s, then 10 s): a friend's finishes went back to the
        // shell's after starting the game, though Haven had asked for them and setting them again fixed it, so a cold
        // start can lose one still loading while the next is taken.
        if (refreshAt >= 0 && now >= refreshAt && interior.Settled && !interior.Pending && interior.Requested != null)
        {
            refreshes++;
            refreshAt = refreshes < RefreshDelays.Length ? interiorSettledAt + RefreshDelays[refreshes] : -1;
            if (InteriorPlan.Changes(null, interior.Requested).Any())
            {
                interior.Refresh();
                log.Information($"[INTERIOR] sending the finishes again ({refreshes} of {RefreshDelays.Length}, {now - interiorSettledAt} ms after they were applied).");
            }
        }
        if (!lighting.Pending && !interior.Pending && !interior.Refreshing && interior.Requested != null && InteriorPlan.Same(options, interior.Requested)) { interiorWatch.Rebind(); return false; }
        var layout = (LayoutManager*)boundLayout;
        if (interiorAskedAt < 0)
        {
            interiorAskedAt = now;
            log.Information($"[INTERIOR] asking for {InteriorPlan.Describe(options)} (territory {territory}; {LayoutState(layout)}).");
        }
        var nativeReady = layout != null && layout->HousingType != 0 && !layout->HousingLayoutDataUpdatePending && layout->OutdoorAreaDataUpdated;
        switch (interiorWatch.Observe(now, !nativeReady))
        {
            case InteriorWatchStep.ReloadShell:
                log.Warning($"[INTERIOR] the game has not taken the interior for {InteriorWatch.StuckMilliseconds / 1000} s ({InteriorPlan.Describe(options)}; {LayoutState(layout)}, {(interior.Pending ? "fixtures" : "brightness")} waiting). Loading the shell again.");
                engine.HousingReloadShell();
                return true;
            case InteriorWatchStep.Report:
                log.Warning($"[INTERIOR] the game still has not taken the interior after a shell reload ({InteriorPlan.Describe(options)}; {LayoutState(layout)}). Leaving and entering again should restore it.");
                FixtureStatus = "The game has not applied this home's finishes. Leave and enter again to restore them.";
                interiorStuck = true;
                break;
        }
        if (layout == null || layout->HousingType == 0)
        { if (!interiorStuck) FixtureStatus = "Waiting for the room to load…"; return false; }
        var lit = false;
        var changed = interior.Update(options, lighting.Pending, nativeReady, fixture =>
        {
            if (!HousingLightingGuard.PrepareFixtureCommand(layout)) throw new InvalidOperationException("Interior preview is waiting for its private layout.");
            // SetInteriorFixture allocates its own transient IndoorAreaData when it is null.
            // Never require that buffer to exist before calling the function that creates it.
            var error = LayoutWorld.SetInteriorFixture(fixture.Floor, fixture.Part, -1, (int)fixture.FixtureId);
            if (error != 0) { FixtureStatus = $"The {(fixture.Floor == 3 ? "main hall" : $"floor {fixture.Floor + 1}")} finish couldn't be applied (code {error}). Haven tries again."; throw new InvalidOperationException(FixtureStatus); }
        }, () =>
        {
            var error = HousingLightingGuard.Submit(layout, lighting.Level);
            if (error != 0) throw new InvalidOperationException($"The native lighting preview could not be submitted (code {error}).");
            lit = true;
        });
        // Brightness counts as applied only once it was handed to the layout; before the interior settles it waits for the
        // layout's own pass instead (InteriorPreview), and must not be dropped meanwhile.
        if (lit || !lighting.Pending) lighting.Applied();
        if (!interior.Pending && interior.Settled && !lighting.Pending && interiorAskedAt >= 0 && settledLogged != interiorAskedAt)
        {
            settledLogged = interiorAskedAt;
            log.Information($"[INTERIOR] applied in {now - interiorAskedAt} ms ({InteriorPlan.Describe(options)}, light level {lighting.Level:0.##}{(interiorWatch.Reloads > 0 ? ", after a shell reload" : "")}).");
            interiorWatch.Reset(); interiorStuck = false;
            interiorSettledAt = now; refreshes = 0; refreshAt = now + RefreshDelays[0];
        }
        // The Room panel shows this: only work in progress or a problem, never the routine "done".
        if (!interiorStuck) FixtureStatus = interior.Pending ? "Applying finishes…" : "";
        return false;
    }
    private long settledLogged = -1;
    private bool interiorStuck;
    private static readonly long[] RefreshDelays = [2500, 10000];
    private long interiorSettledAt, refreshAt = -1;
    private int refreshes;

    private static string LayoutState(LayoutManager* layout) => layout == null ? "no layout"
        : $"layout type {layout->HousingType}, init {layout->InitState}, update pending {layout->HousingLayoutDataUpdatePending}, area data updated {layout->OutdoorAreaDataUpdated}, indoor data {(layout->IndoorAreaData == null ? "none" : "present")}";

    /// <summary>Custom music (CustomMusic): prepares the songs a playlist needs, and says whether one can play now.</summary>
    public Action<IEnumerable<string>>? PrepareSongs { get; set; }
    public Func<string, bool>? SongPlayable { get; set; }
    /// <summary>False for a custom song this PC will not get (a guest who declined the home's custom content): its turn is silent.</summary>
    public Func<string, bool>? SongWanted { get; set; }
    // The music options last applied, and when the track playing now began on this PC (local clock), so an edit to the
    // playlist can let that track carry on instead of restarting it.
    private MusicOptions? appliedMusic;
    private long? trackOrigin;

    /// <summary>
    /// The owner's edit to a playing playlist, re-timed so the track playing now carries on (MusicSchedule.Rebase).
    /// Playing again from the start (a new start time) or stopping is left as it is.
    /// </summary>
    // How far into its track the shared schedule is now (null between tracks or with a length not known). An edit keeps
    // this point; Play from the start does not, so that restarts the track as asked.
    private long? SharedPoint(MusicOptions options, long now)
    {
        var lengths = options.Playlist.Select(musicDurations.Known).ToArray();
        if (lengths.Any(l => l is not > 0)) return null;
        var durations = lengths.Select(l => l!.Value).ToArray();
        return MusicSchedule.Locate(options, now, durations) is { Silent: false } slot ? durations[slot.PlaylistIndex] - (long)Math.Round(slot.RemainingSeconds * 1000) : null;
    }

    public MusicOptions KeepPlaying(MusicOptions old, MusicOptions updated)
    {
        if (!old.Playing || !updated.Playing || old.StartedAtUnixMs != updated.StartedAtUnixMs) return updated;
        if (!musicDurations.TryResolve(old.Playlist, out var resolved, out _)) return updated;
        var oldDurations = resolved.ToArray();
        return MusicSchedule.Rebase(old, updated, MusicNow, oldDurations, i => musicDurations.Known(updated.Playlist[i])) is { } started
            ? updated with { StartedAtUnixMs = started } : updated;
    }

    /// <summary>A game file played instead of the home's music while set (a minigame's music); null resumes the home's.</summary>
    public string? MusicOverride { get; set; }
    private string? overridePlaying;
    private string? overrideAsked;
    private long heldSince = -1;
    private long overrideStarted;
    private readonly Dictionary<string, long?> runsOutAfter = [];

    // The game's orchestrion player only plays looping files (all 886 orchestrion tracks loop). A file without a loop that
    // reaches its end in it froze the game and exited with c0500 ("could not locate data files"): World IX's map theme in
    // the Odyssey, BGM 20042, is a 33 s event track without a loop (a friend, twice, 1-2 November 2026; custom songs
    // without a loop jammed the player the same way before 0.35.1). Such a file is played again from its start just before
    // it ends, so it repeats instead. Null: the file loops, or its length can't be read.
    private long? RunsOutAfter(string file)
    {
        if (runsOutAfter.TryGetValue(file, out var known)) return known;
        long? ends = null;
        try
        {
            if (data.GetFile<Lumina.Data.Files.ScdFile>(file) is { } scd && ScdDurationReader.Read(scd) is { Loops: false, Milliseconds: > 0 } track)
            {
                ends = Math.Max(1000, track.Milliseconds - 500);
                log.Information("[MUSIC] {Path} doesn't loop ({Seconds:F1} s); it will be played again before it ends.", file, track.Milliseconds / 1000.0);
            }
        }
        catch (Exception ex) { log.Warning("[MUSIC] couldn't read the length of {Path}: {Error}", file, ex.Message); }
        return runsOutAfter[file] = ends;
    }

    // A minigame's change of music waits while Haven's stream is still reading its file, and changes once it has loaded,
    // a moment later: cancelling a stream mid-load is a risky step in the game's sound engine (0.58.6, first suspected for
    // the Odyssey's c0500 exits, whose likelier cause is a file without a loop; see RunsOutAfter). After 5 s it changes anyway.
    private bool HoldWhileLoading()
    {
        if (!engine.HousingMusicLoading) { heldSince = -1; return false; }
        var now = Environment.TickCount64;
        if (heldSince < 0) heldSince = now;
        if (now - heldSince < 5000) return true;
        // A file the game can't finish reading in 5 s is most likely one it can't read at all (a damaged install: see
        // OdysseyContent.MapMusic); it isn't asked for again this session.
        if (overridePlaying != null && unreadable.Add(overridePlaying))
            log.Warning("[MUSIC] {Path} was still loading after 5 s; changing the music anyway, and not playing that file again this session. If the game exits with c0500, its data files may need repairing.", overridePlaying);
        else log.Warning("[MUSIC] the track was still loading after 5 s; changing it anyway.");
        heldSince = -1;
        return false;
    }
    private readonly HashSet<string> unreadable = [];

    private void ApplyMusic(MusicOptions options)
    {
        if (MusicOverride != overridePlaying && HoldWhileLoading()) return;
        if (MusicOverride is { } song)
        {
            if (overridePlaying == song && RunsOutAfter(song) is { } ends && Environment.TickCount64 - overrideStarted >= ends) overridePlaying = null;
            // A file this install doesn't have (a free trial gets no data for the expansions it lacks, so Dawntrail's
            // music is absent) is never asked for: the game closes with c0500 when its player can't find one.
            if (overridePlaying != song && !unreadable.Contains(song) && !data.FileExists(song) && unreadable.Add(song))
                log.Warning("[MUSIC] {Path} isn't in this game's data files (an expansion the account doesn't own?); it won't be played.", song);
            if (overridePlaying != song && unreadable.Contains(song))
            {
                // Silence instead (the home's music stays off until the minigame's next track).
                if (overrideAsked != song) { log.Information("[MUSIC] minigame track {Path} skipped: it didn't load earlier.", song); engine.HousingMusicSilence(); }
                overrideAsked = song; overridePlaying = null; musicOwned = true; musicKey = null;
                return;
            }
            if (overridePlaying != song)
            {
                var played = engine.HousingPlayFile(song);
                if (overrideAsked != song) log.Information("[MUSIC] minigame track {Path}{Result}.", song, played ? "" : " (the game didn't start it)");
                overrideAsked = song;
                if (played) { overridePlaying = song; musicOwned = true; overrideStarted = Environment.TickCount64; }
            }
            musicKey = null;
            return;
        }
        if (overridePlaying != null) log.Information("[MUSIC] minigame music ended; the home's music resumes.");
        overridePlaying = overrideAsked = null;
        var now = MusicNow;
        var stream = engine.HousingMusicState;
        musicClock.ObservePlayback(now, stream == MusicStreamState.Playing);
        var slot = MusicState(options);
        if (!options.Playing)
        {
            if (options.Playlist.Length > 0)
            {
                if (!musicOwned || musicKey != null) engine.HousingMusicSilence();
                musicOwned = true;
            }
            else { if (musicOwned) engine.HousingMusicRelease(); musicOwned = false; }
            musicKey = null; musicLostSince = null; return;
        }
        // Custom songs are built and handed to Penumbra before they are due (CustomMusic).
        var customSongs = options.Playlist.Where(t => t.IsCustom).Select(t => t.CustomMusic!).ToArray();
        if (customSongs.Length > 0) PrepareSongs?.Invoke(customSongs);
        if (!MusicReady || !musicDurations.TryResolve(options.Playlist, out var durations, out _)) return;
        // An edit (or a fresh copy of the same options from the relay) while a track plays: when that track is still the
        // one due, it carries on, and the local clock takes on the new schedule from where it is.
        var edited = appliedMusic != null && !ReferenceEquals(appliedMusic, options);
        var previousMusic = appliedMusic;
        appliedMusic = options;
        var loops = options.Playlist.Length == 1 && musicDurations.Loops(options.Playlist[0].OrchestrionId);
        if (edited && musicKey is { Silent: false } playing && trackOrigin is { } origin && stream == MusicStreamState.Playing
            && MusicSchedule.Locate(options, now, durations) is { Silent: false } due
            && MusicPlaybackPolicy.TrackKey(options.Playlist[due.PlaylistIndex]) == playing.Roll
            && SharedPoint(previousMusic!, now) is { } before && Math.Abs(before - (durations[due.PlaylistIndex] - (long)Math.Round(due.RemainingSeconds * 1000))) < 2000)
        {
            musicClock.Continue(options, now, durations, now - origin);
            musicKey = MusicPlaybackPolicy.Key(options, musicClock.Locate(options, now, durations, beginPlayback: true), loops);
        }
        slot = musicClock.Locate(options, now, durations, beginPlayback: true);
        var key = MusicPlaybackPolicy.Key(options, slot, loops);
        // Where the playing track began, by the local clock, for the next edit.
        if (key == musicKey && slot is { Silent: false } current && stream == MusicStreamState.Playing)
            trackOrigin = now - (durations[current.PlaylistIndex] - (long)Math.Round(current.RemainingSeconds * 1000));
        if (MusicPlaybackPolicy.NeedsRecovery(stream)) musicLostSince ??= now; else musicLostSince = null;
        var recover = key == musicKey && slot is { Silent: false } && musicLostSince is {} lost && now - lost >= 500;
        if (key != musicKey || recover)
        {
            if (now < nextMusicRecovery) return;
            musicOwned = true;
            if (slot is {} active && !active.Silent)
            {
                var track = options.Playlist[active.PlaylistIndex];
                if (track.IsCustom && SongWanted?.Invoke(track.CustomMusic!) == false)
                {
                    // Declined: silent for the song's length, on schedule with everyone else.
                    engine.HousingMusicSilence();
                    MusicStatus = $"{track.Title} is a custom song. Load custom content in the Visit panel to hear it.";
                    musicOwned = true; musicKey = key; musicLostSince = null;
                    return;
                }
                if (track.IsCustom && SongPlayable?.Invoke(track.CustomMusic!) != true)
                {
                    // Not here yet (still arriving or being prepared): wait, and keep the schedule.
                    musicClock.AwaitPlayback(now);
                    nextMusicRecovery = now + 1000;
                    MusicStatus = $"Getting {track.Title}…";
                    return;
                }
                if (!(track.IsCustom ? engine.HousingPlayFile(CustomMusic.GamePath(track.CustomMusic!)) : engine.HousingPlayRoll(track.OrchestrionId)))
                {
                    musicClock.AwaitPlayback(now);
                    nextMusicRecovery = now + 2000;
                    MusicStatus = "Waiting for the game's orchestrion player. Retrying…";
                    return;
                }
                // The request may be retried, or a frame may cross the scheduled boundary.
                // Every successful native start gets a complete local track duration.
                musicClock.RestartTrack(active, durations);
                musicClock.AwaitPlayback(now);
                nextMusicRecovery = now + 500;
            }
            else engine.HousingMusicSilence();
            musicOwned = true; musicKey = key; musicLostSince = null;
        }
    }

    public void UseSeat(House house, Furnishing item, int seatIndex = 0)
    {
        if (item.Tilted) throw new InvalidOperationException("A tilted piece can't be sat on. Set its Tilt and Lean back to 0 first.");
        var seats = catalogue.Prefab(item).Seats;
        if (seats.Length == 0) throw new InvalidOperationException("This furnishing has no chair or bed marker.");
        var seat = seats[Math.Clamp(seatIndex, 0, seats.Length - 1)];
        var matrix = seat.Transform * FurnitureCatalogue.ItemMatrix(item) * FurnitureCatalogue.HouseMatrix(house);
        Matrix4x4.Decompose(matrix, out _, out var orientation, out var position);
        var stand = engine.HousingPlayerPosition;
        engine.HousingUseSeat(position, Euler(orientation).Y, seat.Bed);
        standingPosition = stand;
        seatedFurniture = item;
    }
    public void Stand()
    {
        if (standingPosition is Vector3 stand && engine.HousingIsSeated) engine.HousingStandUpAt(stand);
        standingPosition = null; seatedFurniture = null;
    }
    private FurnitureAnchor? Nearby(House house, bool bed)
    {
        if (engine.HousingPlayerPosition is not {} player) return null;
        IEnumerable<FurnitureAnchor> Anchors()
        {
            var parent = FurnitureCatalogue.HouseMatrix(house);
            foreach (var item in house.Furnishings)
            {
                // A tilted seat's sitting pose would float or sink: the game sits only level.
                if (item.Tilted) continue;
                var seats = catalogue.Prefab(item).Seats;
                var matrix = FurnitureCatalogue.ItemMatrix(item) * parent;
                for (var i = 0; i < seats.Length; i++)
                    yield return new(item.Id, i, Vector3.Transform(Vector3.Zero, seats[i].Transform * matrix), seats[i].Bed);
            }
        }
        return FurnitureInteractions.Nearest(Anchors(), player, bed);
    }
    public bool CanUseNativeEmote(House house, ushort emote)
    {
        if (!engine.HousingCanUseFurniture || FurnitureInteractions.WantsBed(emote) is not bool bed) return false;
        return emote == FurnitureInteractions.Sit && standingPosition != null && engine.HousingIsSeated || Nearby(house, bed) != null;
    }
    public bool UseNativeEmote(House house, ushort emote)
    {
        if (!engine.HousingCanUseFurniture || FurnitureInteractions.WantsBed(emote) is not bool bed) return false;
        if (emote == FurnitureInteractions.Sit && standingPosition != null && engine.HousingIsSeated) { Stand(); return true; }
        if (Nearby(house, bed) is not {} anchor) return false;
        UseSeat(house, house.Furnishings.First(x => x.Id == anchor.FurnitureId), anchor.Marker);
        return true;
    }
    public void Clear()
    {
        Doors.Clear(); doorOccupants.Clear();
        lightingGuard.Clear(); lighting.Clear();
        canvasArrival.Clear(); canvasExitLogged = false;
        engine.HousingClearShellRemodel(); remodelLogged = ""; lastGround = null; remodelAt = 0;
        Stand();
        var scene = Scene();
        if (starterCollider != 0 && scene != null && (nint)scene->Scene == boundScene) scene->RemoveCollider((Collider*)starterCollider);
        starterCollider = 0;
        foreach (var id in furniture.Keys.ToArray()) Remove(id);
        colliderFurnishings = null; colliderMoving = null;
        if (musicOwned) engine.HousingMusicRelease();
        boundLayout = boundScene = 0; territory = 0; innRoom = false;
        musicOwned = false; musicClock.Reset(); interior.Clear(); interiorWatch.Rebind(); interiorAskedAt = -1; refreshAt = -1; refreshes = 0; interiorStuck = false; musicKey = null; nextMusicRecovery = 0; musicLostSince = null; appliedMusic = null; trackOrigin = null;
        // A minigame's music ends with the home it played in.
        MusicOverride = null; overridePlaying = overrideAsked = null; heldSince = -1;
    }
    public void Dispose()
    {
        engine.HousingSceneChanging -= Clear;
        Clear();
        lightingGuard.Dispose();
        musicDurations.Dispose();
        foreach (var pointer in paths.Values) Marshal.FreeCoTaskMem(pointer);
        paths.Clear();
    }
}
