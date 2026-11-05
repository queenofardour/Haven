using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Action = System.Action;

namespace HavenNative;

// Housing-facing surface. Every call that touches game state runs on the framework thread.
public sealed partial class NativeEngine
{
    internal HousingLightingGuard CreateHousingLightingGuard() => new(sigScanner, hooks, log, HousingZoneReady);
    public bool HousingIsGPosing => clientState.IsGPosing;
    public unsafe long HousingEorzeaTime
    {
        get
        {
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            return framework == null ? 0 : framework->ClientTime.EorzeaTime;
        }
    }
    public void RegisterHousingEmotes(Func<ushort, bool>? canUse, Func<ushort, bool>? execute)
    { emoteUnlock.HousingCanUse = canUse; emoteUnlock.HousingExecute = execute; }
    public unsafe bool HousingCanUseFurniture
    {
        get
        {
            if (!packetFilter.IsActive || !zoneLoad.IsZoneLoaded || objectTable.LocalPlayer == null) return false;
            var c = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)objectTable.LocalPlayer.Address;
            return c->Mode is FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.Normal
                or FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.EmoteLoop
                or FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.InPositionLoop;
        }
    }
    public event Action HousingSceneChanging
    {
        add => zoneLoad.ZoneWillChange += value;
        remove => zoneLoad.ZoneWillChange -= value;
    }
    public void RegisterHousingExit(Action? action)
    {
        packetFilter.BlockedInteraction = action == null ? null : target => HousingObjectInteraction(target, action);
    }
    private unsafe void HousingObjectInteraction(nint target, Action action)
    {
        if (!packetFilter.IsActive || !zoneLoad.IsZoneLoaded || target == 0) return;
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target;
        // The shell's own "Exit" markers (planmap.lgb LVD_entrance_* and the apartments' LVD_gimmick_*) leave too; the
        // "entrance to additional chambers" markers (other EObj rows) never do.
        if (obj->ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.EventObj)
        {
            if (Haven.Core.ShellRemodel.ExitMarkers.Contains(obj->BaseId)) RunOnMainThread(action);
            return;
        }
        if (obj->ObjectKind != FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.HousingEventObject) return;
        var sg = obj->SharedGroupLayoutInstance;
        // Original furniture is suppressed separately. The remaining native housing target is the shell exit.
        if (sg != null && Haven.Core.HousingAssetPolicy.IsPlacedFurniture(sg->GetPrimaryPath().ToString())) return;
        RunOnMainThread(action);
    }
    public System.Numerics.Vector3? HousingPlayerPosition => objectTable.LocalPlayer?.Position;
    public float? HousingPlayerRotation => objectTable.LocalPlayer?.Rotation;
    public unsafe void HousingOccupantPositions(List<System.Numerics.Vector3> positions)
    {
        positions.Clear();
        if (!packetFilter.IsActive || !zoneLoad.IsZoneLoaded) return;
        if (objectTable.LocalPlayer is {} local) positions.Add(local.Position);
        foreach (var (peer, actor) in stateApply.Peers)
        {
            // Read only the registered participants' real, interpolated actors. Nearby
            // players from the original game zone must never operate a Haven door.
            if (!relay.HousingMembers.ContainsKey(peer) || actor.ObjectIndex is not {} index) continue;
            if (objectTable[(int)index] is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player
                && ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address)->ContentId == actor.ContentId) positions.Add(player.Position);
        }
    }
    /// <summary>Hides a preset home's removed shell pieces (key → keep collision) and shows the rest; how many were found.</summary>
    public int HousingShellRemodel(IReadOnlyDictionary<uint, bool> hide, IReadOnlyDictionary<uint, IReadOnlyList<uint>>? attached = null)
        => packetFilter.IsActive && zoneLoad.IsZoneLoaded && !zoneLoad.IsTransitioning ? zoneLoad.ApplyShellRemodel(hide, attached) : 0;
    public void HousingClearShellRemodel() { if (zoneLoad.IsZoneLoaded) zoneLoad.ClearShellRemodel(); }
    /// <summary>Keeps removed shell pieces hidden this frame; how many draw slots the game had shown again.</summary>
    public int HousingHoldShellRemodel() => packetFilter.IsActive && zoneLoad.IsZoneLoaded && !zoneLoad.IsTransitioning ? zoneLoad.HoldShellRemodel() : 0;
    /// <summary>Outlines the shell piece the remodel mode points at (0: none).</summary>
    public void HousingOutlineShellPiece(uint instance) { if (packetFilter.IsActive && zoneLoad.IsZoneLoaded) zoneLoad.OutlineShellPiece(instance); }
    /// <summary>Shows a removed shell piece as a see-through ghost while it's pointed at (0: none).</summary>
    public void HousingGhostShellPiece(uint instance) { if (packetFilter.IsActive && zoneLoad.IsZoneLoaded) zoneLoad.GhostShellPiece(instance); }
    public bool HousingHideCustomShell() => packetFilter.IsActive && HousingZoneReady(Haven.Core.CustomCanvas.TerritoryId)
        && zoneLoad.HideCustomHousingShell(Haven.Core.CustomCanvas.TerritoryId);
    /// <summary>
    /// Stands the blank canvas's Exit markers (the hidden Topmast apartment's door, 3.4 m past the starter platform's
    /// edge, out of reach) at a spot on the platform. They're the zone's own client-side EObjs, put back by the reload on
    /// leaving. How many were moved this call.
    /// </summary>
    public unsafe int HousingPlaceCanvasExit(System.Numerics.Vector3 at)
    {
        if (!packetFilter.IsActive || !zoneLoad.IsZoneLoaded || zoneLoad.IsTransitioning || zoneLoad.CurrentLoadedZone != Haven.Core.CustomCanvas.TerritoryId) return 0;
        var moved = 0;
        foreach (var o in objectTable)
        {
            if (o == null || o.Address == 0) continue;
            var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)o.Address;
            if (obj->ObjectKind != FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.EventObj
                || !Haven.Core.ShellRemodel.ExitMarkers.Contains(obj->BaseId)) continue;
            var p = obj->Position;
            if (System.Numerics.Vector3.DistanceSquared(new(p.X, p.Y, p.Z), at) < 1e-4f) continue;
            obj->SetPosition(at.X, at.Y, at.Z);
            moved++;
        }
        return moved;
    }
    public unsafe void HousingStandUp()
    {
        SanitiseLocalPosture("housing interaction", evictBaseLane: true);
        if (objectTable.LocalPlayer == null) return;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)objectTable.LocalPlayer.Address;
        character->EmoteController.CurrentPoseType = EmoteController.PoseType.Idle;
        character->EmoteController.CPoseState = 0;
        stateCapture.RequestFullResend();
    }
    public unsafe bool HousingIsSeated
    {
        get
        {
            if (objectTable.LocalPlayer == null) return false;
            var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)objectTable.LocalPlayer.Address;
            return character->Mode == FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.InPositionLoop;
        }
    }
    public unsafe void HousingStandUpAt(System.Numerics.Vector3 position)
    {
        if (!packetFilter.IsActive || objectTable.LocalPlayer == null) return;
        HousingStandUp();
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)objectTable.LocalPlayer.Address;
        obj->SetPosition(position.X, position.Y, position.Z);
    }

    public unsafe void HousingUseSeat(System.Numerics.Vector3 position, float yaw, bool bed)
    {
        if (!packetFilter.IsActive || !zoneLoad.IsZoneLoaded || objectTable.LocalPlayer == null) throw new InvalidOperationException("Enter a private home first.");
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)objectTable.LocalPlayer.Address;
        if (System.Numerics.Vector3.Distance(objectTable.LocalPlayer.Position, position) > 3.5f) throw new InvalidOperationException("Move closer to the furnishing.");
        ushort emoteId = bed ? (ushort)88 : (ushort)50;
        var row = dataManager.GetExcelSheet<Emote>().GetRow(emoteId);
        var mode = row.EmoteMode.Value;
        SanitiseLocalPosture("housing seat", evictBaseLane: true);
        var p = new FFXIVClientStructs.FFXIV.Common.Math.Vector3(position.X, position.Y, position.Z);
        character->GameObject.SetPosition(p.X, p.Y, p.Z);
        // The local player's facing is kept by the game outside GameObject.Rotation: a plain field write was lost, so one
        // who walked up facing a chair sat facing it, in front of the seat, and dozed across a bed. SetRotation is the
        // call the engine's home restore and spawn facing use. The marker's facing is the seated or lying direction.
        character->GameObject.SetRotation(yaw);
        character->EmoteController.EmoteId = emoteId;
        character->SetMode((FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes)mode.ConditionMode, (byte)row.EmoteMode.RowId);
        character->EmoteController.CurrentPoseType = bed ? EmoteController.PoseType.Doze : EmoteController.PoseType.Sit;
        character->EmoteController.CPoseState = 0;
        var loop = (ushort)row.ActionTimeline[0].RowId;
        var intro = (ushort)row.ActionTimeline[1].RowId;
        character->Timeline.PlayActionTimeline(intro, loop);
        stateCapture.RequestFullResend();
    }

    public bool HousingPlayRoll(uint roll) => mapSettings.PlayHousingRoll(roll);
    public bool HousingPlayFile(string path) => mapSettings.PlayHousingFile(path);
    public Haven.Core.MusicStreamState HousingMusicState => mapSettings.HousingMusicState;
    public bool HousingMusicLoading => mapSettings.HousingMusicLoading;
    public void HousingMusicSilence() => mapSettings.SilenceHousingMusic();
    public void HousingMusicRelease() => mapSettings.ReleaseHousingMusic();
    public void HousingReloadShell()
    {
        if (!packetFilter.IsActive || !zoneLoad.IsZoneLoaded || !IsHousingTerritory(zoneLoad.CurrentLoadedZone))
            throw new InvalidOperationException("Enter a private home before restoring fixtures.");
        var player = objectTable.LocalPlayer;
        zoneLoad.LoadZone(zoneLoad.CurrentLoadedZone, stateApply.GetPeerObjectIndices(), player?.Position, player?.Rotation);
        actorVisibility.Refresh();
    }
    public List<(uint sel, string title, string category, int catOrder, int order)> HousingTracks => mapSettings.GetOrchestrionChoices();
    public Sync.RelaySyncService HousingRelay => relay;
    public bool HousingSessionActive => relay.IsSessionActive;
    /// <summary>Raised when the player chose a friend's Estate Teleportation inside a home (it didn't run).</summary>
    public event Action<ulong>? HousingEstateTeleportAsked;
    /// <summary>Runs a friend's Estate Teleportation, once outside a home.</summary>
    public bool HousingTeleportToEstate(ulong character) => socialRequests?.TeleportToEstate(character) == true;
    public bool HousingReturnPending => zoneLoad.HomeRestoreArmed || zoneLoad.IsTransitioning || retOriginFrames > 0;
    public bool HousingCanLoad => packetFilter.CanEnable && zoneLoad.CanLoad && privateRoomChat.Available;
    public uint HousingTerritory => zoneLoad.CurrentLoadedZone;
    public string HousingRelayUrl => config.RelayUrl;
    public string HousingHostCode => config.HousingHostCode;
    public string HousingGuestCode => config.HousingGuestCode;
    // Checked before a visit starts and again before the sandbox zone loads (a guest may queue after joining).
    public string? HousingEntryBlocked
    {
        get
        {
            bool Any(params Dalamud.Game.ClientState.Conditions.ConditionFlag[] flags) => flags.Any(f => condition[f]);
            if (!clientState.IsLoggedIn) return "Log in to a character before entering a home.";
            return Haven.Core.EntryGuard.Reason(new(
                Duty: Any(ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95, ConditionFlag.InDeepDungeon, ConditionFlag.DutyRecorderPlayback),
                DutyQueue: Any(ConditionFlag.InDutyQueue, ConditionFlag.WaitingForDuty, ConditionFlag.WaitingForDutyFinder),
                Pvp: clientState.IsPvP,
                Combat: Any(ConditionFlag.InCombat),
                Cutscene: Any(ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.OccupiedInQuestEvent, ConditionFlag.OccupiedInEvent),
                Transition: Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.LoggingOut),
                Dead: Any(ConditionFlag.Unconscious)));
        }
    }
    public void ShowHousingError(string message) => toast.ShowError(message);

    // GPose rebuilds the environment from the game's weather state, which the blocked server never filled for a
    // private home: weather falls to 0, ambient light to zero, and the room stays dark after GPose. Keep the
    // home's own weather (seen outside GPose in this territory) or the shell's first natural weather instead.
    private readonly Haven.Core.HousingWeather housingWeather = new();
    private uint housingWeatherTerritory;
    private long housingWeatherLogged;
    public unsafe void HousingKeepWeather(uint territory)
    {
        if (territory != housingWeatherTerritory || !HousingZoneReady(territory)) { housingWeather.Clear(); housingWeatherTerritory = territory; }
        if (!HousingZoneReady(territory)) return;
        var env = FFXIVClientStructs.FFXIV.Client.Graphics.Environment.EnvManager.Instance();
        if (env == null) return;
        // The environment scene's weather table starts at +0x30; its first entry is always backed by this shell.
        var natural = env->EnvScene == null ? (byte)0 : *((byte*)env->EnvScene + 0x30);
        if (housingWeather.Observe(env->ActiveWeather, clientState.IsGPosing, natural) is not {} restore) return;
        env->ActiveWeather = restore;
        if (Environment.TickCount64 - housingWeatherLogged > 10_000)
        {
            housingWeatherLogged = Environment.TickCount64;
            log.Information($"[Haven lighting] Restored weather {restore} after it fell to 0 (territory {territory}, GPose {clientState.IsGPosing}).");
        }
    }
    /// <summary>Room chat preferences (Settings → General), applied immediately.</summary>
    public Haven.ChatPreferences HousingChat => new(config.RoomChatTag, config.RoomEmoteMessages, config.TypingShareOwn, config.TypingShowOthers,
        Haven.Core.WorldChannels.All & ~(Haven.Core.WorldChannels)config.WorldChatOff);
    public void SetHousingChat(Haven.ChatPreferences chat)
    {
        config.RoomChatTag = chat.Tag; config.RoomEmoteMessages = chat.EmoteMessages;
        config.TypingShareOwn = chat.ShareTyping; config.TypingShowOthers = chat.ShowTyping;
        config.WorldChatOff = (int)(Haven.Core.WorldChannels.All & ~chat.World);
        config.Save();
    }
    /// <summary>Whether importing a minion prop shows the experimental warning first (Settings → Custom content → Furniture).</summary>
    public bool HousingWarnMinionProps
    {
        get => config.WarnMinionProps;
        set { config.WarnMinionProps = value; config.Save(); }
    }
    /// <summary>Whether tilting a chair or bed says it can't be sat on any more (Settings → Custom content → Furniture).</summary>
    public bool HousingWarnTiltedSeats
    {
        get => config.WarnTiltedSeats;
        set { config.WarnTiltedSeats = value; config.Save(); }
    }
    public bool HousingPoseSounds { get => config.PoseSounds; set { config.PoseSounds = value; config.Save(); } }
    public bool HousingPoseRestOnSurface { get => config.PoseRestOnSurface; set { config.PoseRestOnSurface = value; config.Save(); } }
    public bool HousingCinematicChatTwoHintSeen { get => config.CinematicChatTwoHintSeen; set { config.CinematicChatTwoHintSeen = value; config.Save(); } }
    public string HousingChangelogSeen { get => config.ChangelogSeen; set { if (config.ChangelogSeen == value) return; config.ChangelogSeen = value; config.Save(); } }
    public bool HousingCinematicSeen { get => config.CinematicSeen; set { if (config.CinematicSeen == value) return; config.CinematicSeen = value; config.Save(); } }
    public bool HousingPoseLetPartnersAdjust { get => config.PoseLetPartnersAdjust; set { config.PoseLetPartnersAdjust = value; config.Save(); } }
    public bool HousingAmbientEffects => config.HousingAmbientEffects;
    public bool HousingAmbientSounds => config.HousingAmbientSounds;
    public void SetHousingAmbience(bool effects, bool sounds)
    {
        config.HousingAmbientEffects = effects;
        config.HousingAmbientSounds = sounds;
        config.Save();
    }

    public void SetHousingVisitCodes(string hostCode, string guestCode, bool save = true)
    {
        config.HousingHostCode = hostCode.Trim();
        config.HousingGuestCode = guestCode.Trim();
        if (save) config.Save();
    }

    // The logged-in character's content ID, or 0; homes belong to characters by it (HomeOwners).
    public ulong HousingCharacterId => LocalContentId();
    /// <summary>The logged-in character's name (framework thread), or empty.</summary>
    public string HousingCharacterName => objectTable.LocalPlayer?.Name.TextValue ?? "";
    /// <summary>Gives the relay connection each character's content secret (see ContentLocker.OwnerKey).</summary>
    public Func<ulong, string>? HousingContentSecret { set => relay.ContentSecretFor = value; }
    public Action<System.Net.WebSockets.ClientWebSocketOptions, ulong, string>? HousingPrepareConnection { set => relay.PrepareConnection = value; }

    // The real house or apartment the player is standing in, outside any Haven visit; null elsewhere. Read only.
    public unsafe uint? HousingRealInterior
    {
        get
        {
            if (packetFilter.IsActive || !clientState.IsLoggedIn) return null;
            var manager = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
            if (manager == null || manager->IndoorTerritory == null || !manager->IsInside() || !manager->IndoorTerritory->IsLoaded()) return null;
            var territory = (uint)clientState.TerritoryType;
            return IsHousingTerritory(territory) ? territory : null;
        }
    }

    /// <summary>
    /// The furnishings, finishes and brightness of the real house the player stands in, read from the client's own
    /// housing state (FFXIVClientStructs): IndoorTerritory's furniture list (each piece's HousingFurniture row as
    /// 0x30000 | (Id &amp; 0xFFFF), its position and rotation in the interior's coordinates, and its stain), the interior layout's
    /// wall, floor and light per floor (parts 0, 3 and 4, HousingInterior rows) with a mansion hall's light, and the
    /// light level. Only reads; nothing in the real house is touched.
    /// </summary>
    public unsafe HomeLayoutCapture HousingCaptureRealInterior()
    {
        if (HousingRealInterior is not uint territoryId) throw new InvalidOperationException("Stand inside a real house or apartment to copy it.");
        var indoor = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance()->IndoorTerritory;
        var rows = dataManager.GetExcelSheet<Lumina.Excel.Sheets.HousingFurniture>();
        var furniture = indoor->FurnitureManager.FurnitureVector;
        List<Haven.Core.LayoutPiece> pieces = [];
        var unknown = 0;
        var displays = PrepareRealDisplays(indoor);
        for (var i = 0L; i < furniture.LongCount && pieces.Count < Haven.Core.HomeLayout.MaxPieces; i++)
        {
            var piece = furniture[i].Value;
            if (piece == null) continue;
            // Every HousingFurniture row is 0x30000 plus its index. Most pieces store their ID with 0x10000 set (so
            // FFXIVClientStructs' 0x20000 | Id works for them), but table food such as Hand-poured Coffee does not,
            // and was left out. The low 16 bits are the index either way.
            var row = rows.GetRowOrDefault(0x30000 | (piece->Id & 0xFFFF));
            if (row is not { } housing || !housing.Item.IsValid)
            {
                unknown++;
                log.Warning($"[Haven] Layout save: furnishing {i} has ID 0x{piece->Id:X}, which matches no catalogue row; it was left out.");
                continue;
            }
            var name = housing.Item.Value.Name.ToString();
            var (shown, fish) = ReadRealDisplay(displays, indoor, piece, i, housing, name);
            pieces.Add(new(housing.Item.RowId, name, new(piece->Position.X, piece->Position.Y, piece->Position.Z),
                Haven.Core.HomeLayouts.Degrees(piece->Rotation), piece->Stain, Material: shown, Fish: fish));
        }
        log.Information($"[LAYOUT] Displays read: {displays.Paintings} painting(s), {displays.Wallpapers} partition wallpaper(s), {displays.Prints} fish print(s), "
            + $"{displays.Vases} vase(s) of flowers, {displays.Tanks} aquarium(s) with {displays.Fish} fish; {displays.Blank} empty, {displays.Unread} without a housing object.");
        foreach (var note in displays.Notes) log.Information($"[LAYOUT] {note}");
        var layout = indoor->IndoorAreaLayoutData;
        List<Haven.Core.LayoutFixture> fixtures = [];
        void Floor(IndoorFloorLayoutData floor, string level)
        {
            foreach (var (type, id) in new[] { ("Wall", floor.Part0), ("Floor", floor.Part3), ("Light", floor.Part4) })
                if (id > 0) fixtures.Add(new(type, level, "", FixtureId: (uint)id));
        }
        Floor(layout.Floor0, "Ground Floor"); Floor(layout.Floor1, "Upper Floor"); Floor(layout.Floor2, "Basement");
        if (layout.Exterior.Part4 > 0) fixtures.Add(new("Light", "Main", "", FixtureId: (uint)layout.Exterior.Part4));
        var brightness = float.IsFinite(layout.LightLevel) ? Math.Clamp(layout.LightLevel * 5, 0, 5) : (float?)null;
        var place = dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ToString();
        return new(territoryId, string.IsNullOrWhiteSpace(place) ? "this house" : place,
            new(null, null, pieces.ToArray(), fixtures.ToArray(), Brightness: brightness), unknown);
    }

    public bool IsHousingTerritory(uint id)
    {
        var territory = dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(id);
        return territory != null && territory.Value.TerritoryIntendedUse.RowId == 14
            && !string.IsNullOrEmpty(territory.Value.Bg.ToString());
    }

    // The inn rooms (TerritoryIntendedUse 2: Mizzenmast, the Hourglass, the Roost, Cloud Nine, Bokairo, the Pendants,
    // the Andron, the For'ard Cabins), read once.
    private HashSet<uint>? innTerritories;
    /// <summary>An inn room, offered as a Haven shell (no housing finishes or brightness; its own furniture remodels).</summary>
    public bool IsInnTerritory(uint id) => (innTerritories ??= dataManager.GetExcelSheet<TerritoryType>()
        .Where(x => x.TerritoryIntendedUse.RowId == 2 && !string.IsNullOrEmpty(x.Bg.ToString())).Select(x => x.RowId).ToHashSet()).Contains(id);
    /// <summary>A territory a Haven home can be: a housing interior or an inn room.</summary>
    public bool IsHavenTerritory(uint id) => IsHousingTerritory(id) || IsInnTerritory(id);

    // Whether this install has a game file (GameDataPaths: a trial or an account without an expansion lacks its data,
    // and the game closes when asked for it). Each path is looked up once; the install doesn't change while it runs.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> installed = new(StringComparer.Ordinal);
    public bool HasGameFile(string path) => installed.GetOrAdd(path, p => { try { return dataManager.FileExists(p); } catch (Exception) { return false; } });
    /// <summary>The newest expansion whose data some of these files need and this install lacks, or null when it has them all.</summary>
    public int? GameDataNeeds(IEnumerable<string> paths) => Haven.Core.GameDataPaths.Needs(paths, HasGameFile);

    /// <summary>The expansion a territory's zone needs and this install lacks (Andron and The For'ard Cabins on a free trial), or null.</summary>
    public int? HousingTerritoryNeeds(uint id) =>
        dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(id) is { } territory && territory.Bg.ToString() is { Length: > 0 } bg
            ? GameDataNeeds([Haven.Core.GameDataPaths.TerritoryFile(bg)]) : null;

    /// <summary>Why this PC can't enter a territory (its data isn't installed), for the player; null when it can.</summary>
    public string? HousingTerritoryUnavailable(uint id) => HousingTerritoryNeeds(id) is { } needs
        ? Haven.Core.GameDataPaths.Missing(TerritoryPlace(id), needs) : null;
    private string TerritoryPlace(uint id) =>
        dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(id)?.PlaceName.ValueNullable?.Name.ToString() is { Length: > 0 } name ? name : "This home";

    /// <summary>
    /// Logs once which expansions' data this install has (each by its first zone's level file), so a friend's log shows a
    /// free trial or a missing expansion at a glance.
    /// </summary>
    public void LogInstalledExpansions()
    {
        var probes = dataManager.GetExcelSheet<TerritoryType>().Select(t => t.Bg.ToString())
            .Where(bg => bg.Length > 0 && Haven.Core.GameDataPaths.Expansion("bg/" + bg) > 0)
            .GroupBy(bg => Haven.Core.GameDataPaths.Expansion("bg/" + bg)).OrderBy(g => g.Key)
            .Select(g => (Expansion: g.Key, Has: HasGameFile(Haven.Core.GameDataPaths.TerritoryFile(g.First())))).ToArray();
        var missing = probes.Where(p => !p.Has).Select(p => Haven.Core.GameDataPaths.ExpansionName(p.Expansion)).ToArray();
        log.Information("[DATA] this game has {Has}; {Missing}.",
            string.Join(", ", probes.Where(p => p.Has).Select(p => Haven.Core.GameDataPaths.ExpansionName(p.Expansion)).Prepend("the base game")),
            missing.Length == 0 ? "every expansion's data" : $"no data for {string.Join(" or ", missing)} (a free trial, or an account without them): homes and music from them are refused");
    }

    /// <summary>A guest's game was asked to load a home whose zone it doesn't have; the visit is left (the reason given).</summary>
    public event Action<string>? HousingZoneUnavailable;

    public unsafe bool HousingZoneReady(uint territory)
    {
        if (!zoneLoad.IsZoneLoaded || zoneLoad.IsTransitioning || zoneLoad.CurrentLoadedZone != territory || !packetFilter.IsActive)
            return false;
        var world = LayoutWorld.Instance();
        return world != null && world->ActiveLayout != null && world->ActiveLayout->InitState == 7
            && world->ActiveLayout->TerritoryTypeId == territory;
    }

    public void SetHousingRelay(string endpoint)
    {
        if (relay.IsSessionActive || relay.IsConnecting) throw new InvalidOperationException("Leave the current visit before changing relay.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter a ws:// or wss:// Haven relay endpoint without credentials or query parameters.");
        config.RelayServices = [new RelayService { Name = "Haven relay", Url = uri.AbsoluteUri }];
        config.SelectedRelayService = 0;
        config.SyncSelectedRelayUrl();
        config.Save();
    }

    public void VisitHousingSolo(uint territory)
    {
        ValidateHousingEntry(territory);
        DoQuickLoad(territory);
    }

    public void HostHousing(string code)
    {
        if (relay.IsSessionActive || relay.IsConnecting) throw new InvalidOperationException("Leave the current visit before hosting.");
        if (!HousingCanLoad) throw new InvalidOperationException("Native zone or packet hooks are unavailable for this game build.");
        if (code.Length is < 8 or > 128) throw new ArgumentException("Use an 8–128 character visit code.");
        DoStart(code);
    }

    public void JoinHousing(string code)
    {
        if (relay.IsSessionActive || relay.IsConnecting) throw new InvalidOperationException("Leave the current visit before joining.");
        if (!HousingCanLoad) throw new InvalidOperationException("Native zone or packet hooks are unavailable for this game build.");
        if (code.Length is < 8 or > 128) throw new ArgumentException("Enter an 8–128 character visit code.");
        DoJoin(code);
    }

    public void LoadHostedHousing(uint territory)
    {
        ValidateHousingEntry(territory);
        if (!relay.IsHost || !relay.RoomJoinedAcknowledged) throw new InvalidOperationException("Wait for relay admission.");
        DoLoad(territory);
    }

    public void LeaveHousing() => DoLeaveInternal(silent: false);
    public void EndHousingVisit() => DoStop();
    private void ValidateHousingEntry(uint territory)
    {
        if (!HousingCanLoad) throw new InvalidOperationException("Native zone or packet hooks are unavailable for this game build.");
        if (!IsHavenTerritory(territory)) throw new InvalidOperationException("Choose an available housing interior or inn room.");
        if (HousingTerritoryUnavailable(territory) is { } missing) { toast.ShowError(missing); throw new InvalidOperationException(missing); }
    }
}

// A real house read for copying: its interior territory, its place name and its layout. Unknown counts furniture
// entries whose catalogue row the game data does not know.
public sealed record HomeLayoutCapture(uint Territory, string Place, Haven.Core.HomeLayout Layout, int Unknown);
