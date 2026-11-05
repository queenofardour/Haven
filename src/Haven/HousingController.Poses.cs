using System.Numerics;
using System.Text.Json;
using Haven.Core;
using Haven.Core.Animation;
using Haven.Core.Furniture;

namespace Haven;

/// <summary>The player's own ball: where, which role, whether its animation plays, and their fit on it.</summary>
internal sealed record PoseBinding(Guid Furnishing, Guid Set, int Role)
{
    public bool Playing { get; init; }
    /// <summary>The emote it plays (a carrier for a custom animation), once it plays.</summary>
    public ushort Emote { get; init; }
    /// <summary>The player's fit: a move in the ball's own space (x right, y up, z forward), a turn, tilt and lean in degrees, and a scale.</summary>
    public Vector3 Move { get; init; }
    public float Turn { get; init; }
    public float Pitch { get; init; }
    public float Roll { get; init; }
    public float Scale { get; init; } = 1;
    /// <summary>
    /// How far Haven is raising the player so their body rests on the surface under it, while it measures (the first
    /// seconds of the animation); then it becomes part of the move.
    /// </summary>
    public float Lift { get; init; }
    /// <summary>The move with the lift in it: where the player is, as the HUD shows it and a saved fit keeps it.</summary>
    public Vector3 Placed => Move + Vector3.UnitY * Lift;
    /// <summary>A saved fit (or a partner) placed the player: it already says where they rest, so nothing is measured.</summary>
    public bool Fitted { get; init; }
    /// <summary>Until when (TickCount64) the lift is still being measured after the animation starts.</summary>
    public long LiftUntil { get; init; }
    /// <summary>The others on the set's balls when its animation started (PoseFits.PartnersKey), for the fit kept with them.</summary>
    public string Partners { get; init; } = "";
    /// <summary>Why it is not playing yet, for the HUD.</summary>
    public string Status { get; init; } = "";
    /// <summary>Where the player stood before the animation took them to the ball; standing up returns them there.</summary>
    public Vector3? StandAt { get; init; }
    /// <summary>The ball's emote can't play on this PC; it isn't tried again until the player takes a ball afresh.</summary>
    public bool Broken { get; init; }
    /// <summary>The owner placing the ball itself: it plays at once, alone, and Done moves the ball to where they are.</summary>
    public bool Positioning { get; init; }
    /// <summary>When the emote was last started again after something ended it (the player stays on the ball until Stand up).</summary>
    public long Replayed { get; init; }
}

/// <summary>A player's fit on their ball as the HUD shows it: the move (lift included), turn, tilt and lean in degrees, and scale.</summary>
internal readonly record struct PoseFitValues(Vector3 Move, float Turn, float Pitch, float Roll, float Scale);

/// <summary>A prop the player is asked about: its package, the animations' name, the item that shows it and its slot, and the NPC that would wear it (null: the player).</summary>
internal sealed record PropQuestion(string Package, string Animations, string Item, string Slot, string? Npc = null);

/// <summary>A ball as it is drawn: its set, role, place, colour, and who holds it (0 for free).</summary>
internal readonly record struct PoseBallView(Guid Furnishing, PoseSet Set, int Role, Vector3 Position, float Yaw, uint Color, ulong Holder, bool Preview);

// Pose balls. A furnishing offers animation sets; anyone in the home opens one from
// the furnishing and takes a ball. Each PC holds its own player at their ball and plays the role's emote once every
// ball is taken; the engine's sync shows position, facing and emote to the others, and the pose entries (relay 0x7D)
// say who has which balls open or holds which, and the scale, tilt and lean each chose.
internal sealed partial class HousingController
{
    /// <summary>This PC's custom animations.</summary>
    public PoseAnimationLibrary Animations { get; private set; } = null!;
    /// <summary>Props worn as gear while an animation plays.</summary>
    public PoseGear Gear { get; private set; } = null!;
    /// <summary>A prop the player is asked about (wear it while the animation plays?), or null.</summary>
    public PropQuestion? PropAsked { get; private set; }
    private readonly Dictionary<ulong, PoseEntry> poseEntries = [];
    private PoseEntry myPose;
    private PoseEntry? sentPose;
    private DateTimeOffset nextPoseSend;
    private readonly HashSet<ulong> poseScaled = [];
    private string poseFitsPath = "";
    private List<PoseFit> poseFits = [];
    // Locker work for the home's animation packages, as for its custom furniture.
    private string animationRoom = "";
    private readonly HashSet<string> animationUploaded = [], animationWorking = [];
    private readonly Dictionary<string, (int Attempts, DateTimeOffset Next)> animationRetries = [];
    private long poseReplayAt;
    private readonly HashSet<Guid> silenceChecked = [];
    // Fits this player asked partners to take: shown until their own entries catch up, and sent a few times a second.
    private readonly Dictionary<ulong, (PoseFitValues Fit, long Until)> partnerFits = [];
    private readonly Dictionary<ulong, PoseFitValues> partnerFitsOut = [];
    private readonly Dictionary<ulong, long> partnerFitSent = [];

    /// <summary>The ball the player holds, or null.</summary>
    public PoseBinding? Bound { get; private set; }
    /// <summary>The furnishing and set whose balls the inspector shows while the owner edits them (for everyone else nothing changes).</summary>
    public (Guid Furnishing, Guid? Set)? PosePreview { get => Environment.TickCount64 - posePreviewAt < 200 ? posePreview : null; set { posePreview = value; posePreviewAt = Environment.TickCount64; } }
    private (Guid Furnishing, Guid? Set)? posePreview;
    private long posePreviewAt;
    /// <summary>A set being edited, drawn in place of its saved balls (or as a new set) while the editor is open.</summary>
    public (Guid Furnishing, PoseSet Set)? PoseDraft { get; set; }

    /// <summary>Whether custom animations play their own sounds (music for a dance), on this PC.</summary>
    public bool PoseSounds { get => engine.HousingPoseSounds; set => engine.HousingPoseSounds = value; }
    /// <summary>Whether Haven raises a player whose animated body would sink into the surface under them, on this PC.</summary>
    public bool PoseRestOnSurface { get => engine.HousingPoseRestOnSurface; set { engine.HousingPoseRestOnSurface = value; if (Bound is { } b) Bound = b with { Move = b.Placed, Lift = 0, LiftUntil = value && b.Playing ? Environment.TickCount64 + LiftMilliseconds : 0 }; } }
    /// <summary>Whether partners on the same set may move, turn and size this player, on this PC.</summary>
    public bool PoseLetPartnersAdjust { get => engine.HousingPoseLetPartnersAdjust; set => engine.HousingPoseLetPartnersAdjust = value; }
    private const long LiftMilliseconds = 2500;

    private void InitialisePoses(Dalamud.Plugin.IDalamudPluginInterface pi, Dalamud.Plugin.Services.IDataManager data)
    {
        Animations = new PoseAnimationLibrary(pi, data, log) { KeepUsage = pictures.KeepUsage };
        Gear = new PoseGear(pi, log);
        poseFitsPath = Path.Combine(pi.ConfigDirectory.FullName, "pose-fits.json");
        try { if (File.Exists(poseFitsPath)) poseFits = (JsonSerializer.Deserialize<List<PoseFit>>(File.ReadAllText(poseFitsPath)) ?? []).Where(PoseFits.Valid).ToList(); }
        catch (Exception ex) when (ex is IOException or JsonException) { log.Warning(ex, "[Haven] Saved pose fits could not be read."); }
        engine.HousingRelay.OnPoseEntries += OnPoseEntries;
        engine.HousingRelay.OnPoseAdjust += OnPoseAdjust;
        engine.HousingRelay.OnPoseClose += OnPoseClose;
        engine.HousingRelay.OnHousingSceneReady += OnPoseSceneReady;
        engine.HousingRelay.OnMiniGame += OnMachineMessage;
    }

    private void DisposePoses()
    {
        engine.HousingRelay.OnPoseEntries -= OnPoseEntries;
        engine.HousingRelay.OnPoseAdjust -= OnPoseAdjust;
        engine.HousingRelay.OnPoseClose -= OnPoseClose;
        engine.HousingRelay.OnHousingSceneReady -= OnPoseSceneReady;
        engine.HousingRelay.OnMiniGame -= OnMachineMessage;
        Gear?.Dispose();
        Animations?.Dispose();
    }

    private ulong Me => engine.HousingCharacterId;
    /// <summary>Everyone's pose entries in this home, the player's own included.</summary>
    public IEnumerable<PoseEntry> PoseEntries => PoseNpcs.Merge(
        myPose.Empty ? poseEntries.Values : [.. poseEntries.Values.Where(e => e.Character != myPose.Character), myPose], NpcEntries);

    private void OnPoseEntries(PoseEntry[] entries) => Run(() =>
    {
        foreach (var entry in entries)
        {
            if (entry.Character == Me) { OwnEcho(entry); continue; }
            // A player's id with the NPC mark would stand in for a home NPC (PoseNpcs.Key); real ids never carry it.
            if (PoseNpcs.IsNpc(entry.Character)) continue;
            if (entry.Empty) poseEntries.Remove(entry.Character); else poseEntries[entry.Character] = entry;
        }
    });

    // The relay answers each entry this PC sends with what stands, in order. An answer equal to an entry sent (the
    // oldest one it matches, which also drops any the relay never answered) is an acknowledgement. Anything else
    // answers the oldest entry still waiting: the relay changed it, refusing a ball someone else holds (it comes back
    // as a viewer's) or a set the gathering's copy of the home doesn't have yet (it comes back empty). Only a refusal
    // of the entry this player stands by now releases their ball; an old answer to an earlier entry changes nothing.
    private readonly List<PoseEntry> posesSent = [];
    private void OwnEcho(PoseEntry echo)
    {
        var acknowledged = posesSent.IndexOf(echo);
        if (acknowledged >= 0) { posesSent.RemoveRange(0, acknowledged + 1); return; }
        if (posesSent.Count == 0) return;
        var refused = posesSent[0];
        posesSent.RemoveAt(0);
        if (refused != sentPose) return;
        sentPose = echo;
        myPose = echo.Empty ? PoseEntry.None(Me) : echo;
        if (Bound is { } bound && refused.Holds && refused.Furnishing == bound.Furnishing && refused.Set == bound.Set && refused.Role == bound.Role)
        {
            ReleaseBall();
            Failed?.Invoke(echo.Empty ? "That animation set hasn't reached the gathering yet. Try again in a moment." : "Someone else took that place first.");
        }
    }
    // A joined or resumed gathering: the relay sends its table afresh (anyone who left meanwhile is gone from it), so
    // the old one is dropped; this player's own entry is sent again.
    private void OnPoseSceneReady() => Run(() => { sentPose = null; posesSent.Clear(); poseEntries.Clear(); });

    /// <summary>Opens a set's balls for everyone (from a click on its furnishing).</summary>
    public void OpenPoseSet(Guid furnishing, Guid set)
    {
        if (!IsReady || FindSet(furnishing, set) == null) return;
        if (PoseNpc is { } npc) { OpenNpcPoseSet(npc, furnishing, set); return; }
        if (Bound != null) LeavePoseBall();
        myPose = new(Me, furnishing, set, PoseEntry.Viewing, 1);
    }
    /// <summary>
    /// Hides a furnishing's balls for everyone: every set of it that's open, with anyone on its balls stood up. In a
    /// gathering the relay drops the sets' entries and tells everyone (PoseClose); alone, it's this player's own.
    /// </summary>
    public void ClosePoseSets(Guid furnishing)
    {
        var sets = PoseEntries.Where(e => !e.Empty && e.Furnishing == furnishing).Select(e => e.Set).Distinct().Where(s => MayClosePoseSet(furnishing, s)).ToArray();
        foreach (var set in sets)
        {
            CloseSet(furnishing, set);
            if (IsSharedVisit && engine.HousingRelay.HousingSceneReady) _ = engine.HousingRelay.SendPoseClose(new(furnishing, set));
        }
    }

    /// <summary>
    /// Whether this player may hide a set's balls for everyone: the owner or a decorator, or someone holding one of its
    /// balls or viewing it (the relay checks the same).
    /// </summary>
    public bool MayClosePoseSet(Guid furnishing, Guid set) =>
        !IsSharedVisit || CanFurnish || !myPose.Empty && myPose.Furnishing == furnishing && myPose.Set == set;

    // A set closed for everyone: this player stands up if they were on one of its balls, and its entries go.
    private void CloseSet(Guid furnishing, Guid set)
    {
        if (!myPose.Empty && myPose.Furnishing == furnishing && myPose.Set == set)
        {
            if (Bound != null) ReleaseBall();
            myPose = PoseEntry.None(Me);
            // The relay dropped this player's entry itself: nothing to send.
            sentPose = myPose; posesSent.Clear();
        }
        foreach (var gone in poseEntries.Values.Where(e => e.Furnishing == furnishing && e.Set == set).Select(e => e.Character).ToArray()) poseEntries.Remove(gone);
        CloseNpcSet(furnishing, set);
    }
    private void OnPoseClose(PoseClose close) => Run(() => CloseSet(close.Furnishing, close.Set));

    /// <summary>Takes a ball: the player is held at it and plays its role once every ball of the set is taken.</summary>
    public void TakePoseBall(Guid furnishing, Guid set, int role)
    {
        // While possessing an NPC, the NPC takes it.
        if (PoseNpc is { } npc) TakeNpcBall(npc, furnishing, set, role);
        else TakeBall(furnishing, set, role, false);
    }

    /// <summary>
    /// The owner (or a decorator) places a ball by standing on it: its animation plays at once, alone, and the HUD
    /// moves the player; Done moves the ball to where they are (FinishPositioning).
    /// </summary>
    public void BeginPositioning(Guid furnishing, Guid set, int role)
    {
        if (!CanFurnish) throw new InvalidOperationException("Only decorators can move pose balls.");
        if (FindSet(furnishing, set) == null) throw new InvalidOperationException("Save the set first, then try its balls.");
        TakeBall(furnishing, set, role, true);
    }

    private void TakeBall(Guid furnishing, Guid set, int role, bool positioning)
    {
        if (!IsReady || FindSet(furnishing, set) is not { } found || role < 0 || role >= found.Set.Roles.Length) return;
        if (PoseSessions.Holders(PoseEntries.Where(e => e.Character != Me), furnishing, found.Set)[role] != 0) throw new InvalidOperationException("Someone is already there.");
        if (Bound != null) LeavePoseBall();
        var fit = positioning ? null : PoseFits.Find(poseFits, HomeOwners.Key(Me), set, role, "");
        Bound = new(furnishing, set, role)
        {
            Move = PoseWire.Limit(fit?.Move ?? Vector3.Zero, far: true), Turn = fit?.Turn ?? 0, Pitch = fit?.Pitch ?? 0, Roll = fit?.Roll ?? 0, Scale = fit?.Scale ?? 1,
            Fitted = fit != null, Positioning = positioning, Status = positioning ? "Placing the ball" : "Waiting for everyone to take a place…",
        };
        SyncMyPose(found.Set);
    }

    /// <summary>Ends placing a ball: keep moves the ball (its place, facing, tilt and lean) to where the player is now; either way they stand up.</summary>
    public void FinishPositioning(bool keep)
    {
        if (Bound is not { Positioning: true } bound) return;
        if (keep && FindSet(bound.Furnishing, bound.Set) is { } found && active != null && Matrix4x4.Invert(FurnitureCatalogue.HouseMatrix(active), out var toHouse))
        {
            var role = found.Set.Roles[bound.Role];
            var (world, _) = BallPlace(found.Item, role, bound with { Lift = 0 });
            var offset = PoseSets.OffsetFor(found.Item, Vector3.Transform(world, toHouse));
            var moved = role with
            {
                Position = [MathF.Round(offset.X, 3), MathF.Round(offset.Y, 3), MathF.Round(offset.Z, 3)],
                Rotation = PoseTilt.Wrap(role.Rotation + bound.Turn), Pitch = PoseTilt.Wrap(role.Pitch + bound.Pitch), Roll = PoseTilt.Wrap(role.Roll + bound.Roll),
            };
            LeavePoseBall();
            SavePoseSet(found.Item.Id, found.Set with { Roles = [.. found.Set.Roles.Select((r, i) => i == bound.Role ? moved : r)] });
            return;
        }
        LeavePoseBall();
    }

    /// <summary>Leaves the ball and frees it: the player stands up where they stood before the animation took them there.</summary>
    public void LeavePoseBall()
    {
        if (Bound is not { } bound) return;
        ReleaseBall();
        myPose = new(Me, bound.Furnishing, bound.Set, PoseEntry.Viewing, 1);
    }

    private void ReleaseBall()
    {
        if (Bound is not { } bound) return;
        Bound = null;
        if (bound.Playing)
        {
            if (bound.StandAt is { } spot) engine.HousingStandUpAt(spot);
            else engine.HousingLeavePose();
        }
        engine.HousingPoseScale(Me, null);
        engine.HousingPoseTilt(Me, 0, 0, upright: true);
    }

    // The player's entry follows their ball: the role, scale, and the tilt and lean in all (the ball's plus theirs).
    private void SyncMyPose(PoseSet set)
    {
        if (Bound is not { } bound) return;
        var role = set.Roles[bound.Role];
        myPose = new(Me, bound.Furnishing, bound.Set, (byte)bound.Role, bound.Scale, PoseTilt.Wrap(role.Pitch + bound.Pitch), PoseTilt.Wrap(role.Roll + bound.Roll),
            PoseWire.Limit(bound.Placed, far: true), PoseTilt.Wrap(bound.Turn));
    }

    /// <summary>Moves the player's fit on their ball: a step in the ball's own space (x right, y up, z forward) and a turn in degrees.</summary>
    /// <summary>
    /// Sets the player's fit: where they are from the ball (the lift Haven measured included, as the HUD shows it),
    /// kept within reach of the ball, and their turn, tilt and lean. Haven stops measuring the surface: the player chose.
    /// </summary>
    public void SetPoseFit(Vector3 move, float turn, float pitch, float roll, bool far = false)
    {
        if (Bound is not { } bound) return;
        // Within 2 m of the ball, unless Ctrl is held or they're already past it (then until they're back within).
        Bound = bound with { Move = PoseWire.Limit(move, far || PoseWire.Beyond(bound.Placed)), Lift = 0, LiftUntil = 0, Turn = PoseTilt.Wrap(turn), Pitch = PoseTilt.Wrap(pitch), Roll = PoseTilt.Wrap(roll) };
    }
    public void SetPoseScale(float scale)
    {
        if (Bound is not { } bound) return;
        Bound = bound with { Scale = Math.Clamp(scale, PoseWire.MinScale, PoseWire.MaxScale) };
    }
    public void ResetPoseFit()
    {
        if (PoseSubject is var subject && PoseNpcs.IsNpc(subject))
        {
            SetFitFor(subject, new(Vector3.Zero, 0, 0, 0, 1));
            // Back at the ball, it rests on the surface afresh (as the player does).
            if (NpcOf(subject) is { } npc && npcPoses.TryGetValue(npc, out var reset)) { reset.Fitted = false; reset.WasPlaying = false; }
            return;
        }
        if (Bound is not { } bound) return;
        Bound = bound with { Move = Vector3.Zero, Turn = 0, Pitch = 0, Roll = 0, Scale = 1, Lift = 0, Fitted = false,
            LiftUntil = bound.Playing && PoseRestOnSurface ? Environment.TickCount64 + LiftMilliseconds : 0 };
    }
    /// <summary>Keeps the player's fit on this ball, with the others there now, for next time.</summary>
    public void SavePoseFit()
    {
        if (PoseSubject is var subject && PoseNpcs.IsNpc(subject)) { SaveNpcFit(subject); return; }
        if (Bound is not { Positioning: false } bound) return;
        var fit = new PoseFit { Character = HomeOwners.Key(Me), Set = bound.Set, Role = bound.Role, Partners = bound.Partners, Offset = [bound.Placed.X, bound.Placed.Y, bound.Placed.Z],
            Turn = bound.Turn, Pitch = bound.Pitch, Roll = bound.Roll, Scale = bound.Scale };
        poseFits = PoseFits.Keep(poseFits, fit);
        // Also as the fit for this ball with anyone, so it is the starting point before partners arrive.
        if (bound.Partners.Length > 0) poseFits = PoseFits.Keep(poseFits, fit with { Partners = "" });
        WritePoseFits();
    }
    private void WritePoseFits()
    {
        try
        {
            var temporary = poseFitsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(poseFits));
            File.Move(temporary, poseFitsPath, overwrite: true);
        }
        catch (IOException ex) { throw new InvalidOperationException("Your fit could not be saved: " + ex.Message, ex); }
    }
    /// <summary>Starts the animation again for everyone on the set's balls who sees it here (their own PCs restart theirs), and reloads it.</summary>
    public void RestartPoseAnimation()
    {
        if (PoseNpc is { } npc && PoseSubject != Me)
        {
            // A saved ball has no state of its own yet: taking it over here makes one.
            if (!npcPoses.TryGetValue(npc, out var state) && EntryOf(PoseNpcs.Key(npc)) is { } entry) { SetNpcEntry(npc, entry); npcPoses.TryGetValue(npc, out state); }
            if (state != null) state.Restart++;
            return;
        }
        if (Bound is not { Playing: true } bound) return;
        engine.HousingRedrawPoseCharacters();
        Bound = bound with { Playing = false, Status = "Starting again…" };
        poseReplayAt = Environment.TickCount64 + 1200;
    }

    /// <summary>A set's name and the furnishing offering it, or null when the home no longer has it.</summary>
    public (Furnishing Item, PoseSet Set)? FindSet(Guid furnishing, Guid set) =>
        active?.Furnishings.FirstOrDefault(f => f.Id == furnishing) is { Poses: { } sets } item && sets.FirstOrDefault(s => s.Id == set) is { } found ? (item, found) : null;

    // Someone's entry as everyone's merged entries have it (a person's, or an NPC's).
    private PoseEntry? EntryOf(ulong character) => PoseEntries.FirstOrDefault(e => e.Character == character) is { Empty: false } entry ? entry : null;

    /// <summary>A character's name as this PC knows it: the player's own, an NPC's, or a gathering participant's.</summary>
    public string PoseName(ulong contentId) => contentId == 0 ? "" : contentId == Me ? "You"
        : PoseNpcs.IsNpc(contentId) ? NpcByKey(contentId)?.Name ?? "An NPC"
        : engine.HousingRelay.HousingMembers.Values.FirstOrDefault(m => m.ContentId == contentId)?.Name ?? "Someone";

    /// <summary>The balls to draw: every open set's, and the set the owner is editing (as a preview).</summary>
    public List<PoseBallView> PoseBalls()
    {
        List<PoseBallView> balls = [];
        if (active == null || !IsReady) return balls;
        var entries = PoseEntries.ToArray();
        var matrix = FurnitureCatalogue.HouseMatrix(active);
        foreach (var item in active.Furnishings)
        {
            var sets = item.Poses ?? [];
            if (PoseDraft is { } draft && draft.Furnishing == item.Id)
                sets = sets.Any(s => s.Id == draft.Set.Id) ? sets.Select(s => s.Id == draft.Set.Id ? draft.Set : s).ToArray() : [.. sets, draft.Set];
            foreach (var set in sets)
            {
                var preview = PosePreview is { } p && p.Furnishing == item.Id && (p.Set == null || p.Set == set.Id) || PoseDraft is { } d && d.Furnishing == item.Id && d.Set.Id == set.Id;
                if (!preview && !PoseSessions.Open(entries, item.Id, set.Id)) continue;
                var holders = PoseSessions.Holders(entries, item.Id, set);
                for (var r = 0; r < set.Roles.Length; r++)
                {
                    var (position, degrees) = PoseSets.Place(item, set.Roles[r]);
                    balls.Add(new(item.Id, set, r, Vector3.Transform(position, matrix), WorldYaw(matrix, degrees), set.Roles[r].Color, holders[r], preview));
                }
            }
        }
        return balls;
    }

    // A yaw in the home's space, in degrees, as a yaw in the world, in radians (the home may be turned).
    private static float WorldYaw(Matrix4x4 house, float degrees)
    {
        var radians = degrees * MathF.PI / 180;
        var forward = Vector3.TransformNormal(new Vector3(MathF.Sin(radians), 0, MathF.Cos(radians)), house);
        return MathF.Atan2(forward.X, forward.Z);
    }

    /// <summary>
    /// The player's ball in the world: its place and its own axes (x right, y up, z forward, from the ball's facing),
    /// as a matrix whose translation is the ball. The player's move is in this space; the HUD's handles drag along it.
    /// </summary>
    public Matrix4x4? BallFrame()
    {
        if (Bound is not { } bound || active == null || FindSet(bound.Furnishing, bound.Set) is not { } found) return null;
        var matrix = FurnitureCatalogue.HouseMatrix(active);
        var (position, degrees) = PoseSets.Place(found.Item, found.Set.Roles[bound.Role]);
        return BallFrame(Vector3.Transform(position, matrix), WorldYaw(matrix, degrees));
    }
    private static Matrix4x4 BallFrame(Vector3 world, float yaw)
    {
        // The game's yaw 0 faces +Z (south) and grows towards +X (east), a turn to the left; facing south, the right hand points west.
        var right = new Vector3(-MathF.Cos(yaw), 0, MathF.Sin(yaw));
        var forward = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        return new(right.X, right.Y, right.Z, 0, 0, 1, 0, 0, forward.X, forward.Y, forward.Z, 0, world.X, world.Y, world.Z, 1);
    }

    // Where the player stands on their ball: the ball, plus their fit in the ball's own space, raised by the lift.
    private (Vector3 Position, float Yaw) BallPlace(Furnishing item, PoseRole role, PoseBinding bound)
    {
        var matrix = FurnitureCatalogue.HouseMatrix(active!);
        var (position, degrees) = PoseSets.Place(item, role);
        var yaw = WorldYaw(matrix, degrees);
        var world = Vector3.Transform(bound.Move, BallFrame(Vector3.Transform(position, matrix), yaw)) + Vector3.UnitY * bound.Lift;
        return (world, yaw + bound.Turn * MathF.PI / 180);
    }

    private void HoldBall(Furnishing item, PoseSet set)
    {
        if (Bound is not { } bound) return;
        var role = set.Roles[bound.Role];
        var (position, yaw) = BallPlace(item, role, bound);
        engine.HousingHoldPose(position, yaw);
        engine.HousingPoseScale(Me, bound.Scale);
        engine.HousingPoseTilt(Me, PoseTilt.Wrap(role.Pitch + bound.Pitch), PoseTilt.Wrap(role.Roll + bound.Roll));
    }

    // While the animation settles in (its first seconds), the body's lowest joint is compared with the surface straight
    // below it, and the player raised so it rests just above (never lowered: legs over a chair's edge stay as they
    // are). The ray starts a metre above the ball, so a body sunk into the floor still finds the floor's top.
    private void MeasureLift(Furnishing item, PoseSet set)
    {
        if (Bound is not { Playing: true } bound || Environment.TickCount64 >= bound.LiftUntil) return;
        if (engine.HousingPoseLowestJoint(Me) is not { } body) return;
        var (root, _) = BallPlace(item, set.Roles[bound.Role], bound with { Lift = 0 });
        if (HavenNative.NativeEngine.HousingSurfaceBelow(new(body.Joint.X, root.Y + 1, body.Joint.Z), 3) is not { } surface) return;
        // The joint's depth below the body's root, read in the same frame (the root may not have reached the last
        // raise yet); a sole is about 3 cm below its lowest joint.
        var depth = body.Joint.Y - body.Root.Y;
        var needed = Math.Clamp(surface.Y + .03f - (root.Y + depth), 0, 1.5f);
        if (needed > bound.Lift + .005f)
        {
            Bound = bound with { Lift = needed };
            log.Information("[POSE] raised {Lift:0.000} m: lowest joint {Depth:0.000} below the body, surface at {Surface:0.000}, ball at {Root:0.000}.", needed, -depth, surface.Y, root.Y);
        }
    }

    /// <summary>Whether this PC may play a home's custom animations: the owner's, the player's own, or a home whose custom content they agreed to.</summary>
    private bool ShowsAnimations(string package) => active != null && (OwnsActiveHouse || CustomPicturesLoaded || Animations.Item(package) != null);

    // Each frame: the home's custom animations are served; the owner shares its packages and guests who agreed fetch
    // them; the player's ball holds them and starts or stops its animation; others' scales and tilts are applied; and
    // the player's entry goes to the gathering when it changes.
    private void UpdatePoses()
    {
        engine.HousingPoseTiltFrame();
        // Held on a playing ball, the game doesn't turn the player with the camera (or the turn keys, or a target).
        engine.HousingHoldFacing(ArcadeHolding || NpcHolding || CameraHolding || active != null && Bound is { Playing: true });
        while (Animations.Failures.TryDequeue(out var message)) Failed?.Invoke(message);
        while (Gear.Failures.TryDequeue(out var message)) Failed?.Invoke(message);
        if (active == null || !IsReady)
        {
            PropAsked = null;
            Gear.Update(null, engine.HousingPlayerPosition != null);
            Gear.UpdateNpcs(NoProps);
            if (Bound != null) { Bound = null; engine.HousingPoseScale(Me, null); engine.HousingPoseTilt(Me, 0, 0, upright: true); }
            foreach (var character in poseScaled) { engine.HousingPoseScale(character, null); engine.HousingPoseTilt(character, 0, 0, upright: true); }
            poseScaled.Clear(); poseEntries.Clear(); npcPoses.Clear(); myPose = default; sentPose = null; PosePreview = null; PoseDraft = null;
            if (active == null) Animations.Clear();
            return;
        }
        var all = PoseSets.Animations(active).ToArray();
        var animations = all.Where(a => ShowsAnimations(a.Package)).ToArray();
        var standIns = all.Where(a => !ShowsAnimations(a.Package)).Select(a => a.Carrier).ToArray();
        // The owner's sets saved before silent balls get them (and carriers of their own) once, so music plays once.
        if (OwnsActiveHouse && CanFurnish && !silenceChecked.Contains(active.Id))
        {
            silenceChecked.Add(active.Id);
            foreach (var item in active.Furnishings.Where(f => f.Poses != null))
                foreach (var set in item.Poses!.Where(PoseSets.NeedsSilence).ToArray())
                    try { SavePoseSet(item.Id, set); log.Information("[POSE] {Set}: one ball now plays its music, the others silent.", set.Name); }
                    catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { log.Warning(ex, "[POSE] {Set} kept its carriers.", set.Name); }
        }
        if (Animations.Prepare(animations, standIns, PoseSounds, PoseSets.SilentCarriers(active).ToArray()).Length > 0)
        {
            // A carrier now plays something else than it did earlier in this game session: characters keep what they bound.
            engine.HousingRedrawPoseCharacters();
            if (Bound is { Playing: true } replay) { Bound = replay with { Playing = false, Status = "Loading the animation…" }; poseReplayAt = Environment.TickCount64 + 1200; }
        }
        UpdateAnimationTransfers();
        // Entries for sets the home no longer offers.
        foreach (var gone in poseEntries.Values.Where(e => FindSet(e.Furnishing, e.Set) == null).Select(e => e.Character).ToArray()) poseEntries.Remove(gone);
        if (!myPose.Empty && FindSet(myPose.Furnishing, myPose.Set) == null) { ReleaseBall(); myPose = PoseEntry.None(Me); }
        if (myPose.Character != Me && !myPose.Empty) myPose = myPose with { Character = Me };
        UpdateNpcPoses();
        UpdateBall();
        UpdateProp();
        // Everyone else on a ball of a set that plays (NPCs too) is drawn at the scale, tilt and lean they chose.
        var holding = new HashSet<ulong>();
        var entries = PoseEntries.ToArray();
        foreach (var entry in entries.Where(e => e.Holds && e.Character != Me))
        {
            if (FindSet(entry.Furnishing, entry.Set) is not { } found || !PoseSessions.Playing(entries, entry.Furnishing, found.Set)) continue;
            holding.Add(entry.Character);
            engine.HousingPoseScale(entry.Character, entry.Scale);
            engine.HousingPoseTilt(entry.Character, entry.Pitch, entry.Roll);
        }
        // Someone whose animation stopped (they stood up, or a partner left their set) is stood up on this PC too: the
        // game's sync doesn't always carry the end of a looping emote, and they were seen still animating.
        foreach (var character in poseScaled.Where(c => !holding.Contains(c)).ToArray())
        {
            engine.HousingPoseScale(character, null); engine.HousingPoseTilt(character, 0, 0, upright: true);
            if (!PoseNpcs.IsNpc(character) && engine.HousingStandPeer(character)) log.Information("[POSE] stood up a partner whose animation ended.");
        }
        poseScaled.Clear(); poseScaled.UnionWith(holding);
        SendPartnerFits();
        // The player's entry: sent when it changes, a drag at most 2.5 times a second (the relay allows 3).
        if (IsSharedVisit && engine.HousingRelay.HousingSceneReady && myPose.Character == Me && sentPose != (myPose.Empty ? PoseEntry.None(Me) : myPose)
            && DateTimeOffset.UtcNow >= nextPoseSend && (!myPose.Empty || sentPose != null))
        {
            var entry = myPose.Empty ? PoseEntry.None(Me) : myPose;
            sentPose = entry; nextPoseSend = DateTimeOffset.UtcNow.AddMilliseconds(400);
            posesSent.Add(entry);
            if (posesSent.Count > 16) posesSent.RemoveAt(0);
            _ = engine.HousingRelay.SendPose(entry);
        }
    }

    private void UpdateBall()
    {
        if (Bound is not { } bound) return;
        if (FindSet(bound.Furnishing, bound.Set) is not { } found || bound.Role >= found.Set.Roles.Length) { LeavePoseBall(); return; }
        var (item, set) = found;
        var role = set.Roles[bound.Role];
        SyncMyPose(set);
        var holders = PoseSessions.Holders(PoseEntries, item.Id, set);
        // Every ball held, this one by this player (another player's accepted claim may be ahead of this one's
        // refusal). The owner placing a ball plays it alone.
        var everyone = bound.Positioning ? holders[bound.Role] == Me : holders.All(h => h != 0) && holders[bound.Role] == Me;
        if (bound.Playing)
        {
            if (!everyone)
            {
                // A partner left: the animation stops, and this player waits where they were for someone to take the place.
                if (bound.StandAt is { } spot) engine.HousingStandUpAt(spot); else engine.HousingLeavePose();
                engine.HousingPoseTilt(Me, 0, 0, upright: true);
                Bound = bound with { Playing = false, Status = "Waiting for everyone to take a place…" };
                engine.HousingPoseScale(Me, bound.Scale);
                return;
            }
            // Something ended the emote (the game's own reaction to a key Haven didn't hold back): the player stays on
            // the ball until they choose Stand up, so it starts again.
            if (!engine.HousingPosePlaying(bound.Emote) && Environment.TickCount64 - bound.Replayed > 500)
            {
                var (at, facing) = BallPlace(item, role, bound);
                try { engine.HousingPlayPose(at, facing, bound.Emote); }
                catch (InvalidOperationException) { }
                Bound = bound = bound with { Replayed = Environment.TickCount64 };
            }
        }
        else if (everyone && Environment.TickCount64 >= poseReplayAt && !bound.Broken)
        {
            ushort emote;
            if (role.Custom)
            {
                if (!ShowsAnimations(set.Package!)) { Bound = bound with { Status = "Load this home's custom content (Visit panel) to play this animation." }; return; }
                if (!Animations.Serving(set.Package!, role.Animation, role.Carrier, role.Silent))
                { Bound = bound with { Status = Animations.Has(set.Package!) ? "Preparing the animation…" : "Getting the animation…" }; return; }
                emote = PoseCarriers.Get(role.Carrier)!.Value.Emote;
            }
            else emote = (ushort)role.Emote;
            var partners = PoseFits.PartnersKey(holders.Where(h => h != Me));
            if (!bound.Positioning && partners != bound.Partners && PoseFits.Find(poseFits, HomeOwners.Key(Me), set.Id, bound.Role, partners) is { } fit && fit.Partners == partners)
                bound = bound with { Move = PoseWire.Limit(fit.Move, far: true), Turn = fit.Turn, Pitch = fit.Pitch, Roll = fit.Roll, Scale = fit.Scale, Fitted = true };
            // A saved fit already says where the player rests (its height included); without one, the surface is measured
            // (not while placing a ball: what the owner sees is where the ball goes).
            bound = bound with { Partners = partners, Move = bound.Placed, Lift = 0, LiftUntil = PoseRestOnSurface && !bound.Fitted && !bound.Positioning ? Environment.TickCount64 + LiftMilliseconds : 0 };
            var (position, yaw) = BallPlace(item, role, bound);
            var stand = bound.StandAt ?? engine.HousingPlayerPosition;
            try { engine.HousingPlayPose(position, yaw, emote); }
            catch (InvalidOperationException ex)
            {
                // A ball whose emote this game can't play (an unknown or one-off emote in someone's home): say so once.
                Bound = bound with { Broken = true, Status = "This ball can't play here: " + ex.Message };
                Failed?.Invoke(Bound.Status);
                return;
            }
            // An animation a mod made only for some bodies (women's, say) has nothing for this one: it stands still.
            var status = role.Custom && engine.HousingBodyCode is { } body && !Animations.Fits(set.Package!, role.Animation, role.Carrier, body)
                ? "This animation has no version for your body type, so you stand still." : "";
            Bound = bound = bound with { Playing = true, Emote = emote, Status = status, StandAt = stand };
            log.Information("[POSE] playing emote {Emote} on {Set} · {Role} at ({X:0.000}, {Y:0.000}, {Z:0.000}).", emote, set.Name, bound.Role, position.X, position.Y, position.Z);
        }
        // Held at the ball only while the animation plays; a player waiting for partners stays where they are.
        if (bound.Playing)
        {
            MeasureLift(item, set);
            // Measured: the lift becomes part of the move, so the HUD, a saved fit and partners all see where the player is.
            if (Bound is { LiftUntil: > 0 } settled && Environment.TickCount64 >= settled.LiftUntil)
                Bound = settled with { Move = PoseWire.Limit(settled.Placed, far: true), Lift = 0, LiftUntil = 0 };
            HoldBall(item, set);
        }
        else engine.HousingPoseScale(Me, bound.Scale);
    }

    // Adjusting partners --------------------------------------------------------------------------------------------
    // Anyone on a set's ball may move, turn, tilt, lean and size the others on it (a partner who'd rather not fiddle can
    // leave it to the other), one at a time or everyone together. The fit is sent to the partner's PC (PoseAdjust),
    // which applies it as its own; their entry then shows it to everyone.

    /// <summary>The others on the HUD's subject's set (the player's, or the possessed NPC's) while its animation plays, in ball order.</summary>
    public ulong[] PosePartners()
    {
        var subject = PoseSubject;
        if (PoseNpcs.IsNpc(subject))
        {
            var entries = PoseEntries.ToArray();
            if (entries.FirstOrDefault(e => e.Character == subject) is not { Holds: true } mine || FindSet(mine.Furnishing, mine.Set) is not { } set
                || !PoseSessions.Playing(entries, mine.Furnishing, set.Set)) return [];
            return [.. PoseSessions.Holders(entries, mine.Furnishing, set.Set).Where(h => h != 0 && h != subject)];
        }
        if (Bound is not { Playing: true, Positioning: false } bound || FindSet(bound.Furnishing, bound.Set) is not { } found) return [];
        return [.. PoseSessions.Holders(PoseEntries, bound.Furnishing, found.Set).Where(h => h != 0 && h != Me)];
    }

    /// <summary>A player's fit on their ball, as the HUD shows it: the player's own, or a partner's (as asked for, until their PC catches up).</summary>
    public PoseFitValues? FitOf(ulong character)
    {
        if (character == Me || character == 0) return Bound is { } bound ? new(bound.Placed, bound.Turn, bound.Pitch, bound.Roll, bound.Scale) : null;
        if (partnerFits.TryGetValue(character, out var asked) && Environment.TickCount64 < asked.Until) return asked.Fit;
        if (EntryOf(character) is not { Holds: true } entry || FindSet(entry.Furnishing, entry.Set) is not { } found || entry.Role >= found.Set.Roles.Length) return null;
        var role = found.Set.Roles[entry.Role];
        // An NPC being raised onto its surface here shows where it is.
        var lift = NpcOf(character) is { } lifted ? NpcLift(lifted) : 0;
        return new(entry.Move + Vector3.UnitY * lift, entry.Turn, PoseTilt.Wrap(entry.Pitch - role.Pitch), PoseTilt.Wrap(entry.Roll - role.Roll), entry.Scale);
    }

    /// <summary>A player's ball in the world, as BallFrame(): theirs or a partner's.</summary>
    public Matrix4x4? BallFrameOf(ulong character)
    {
        if (character == Me || character == 0) return BallFrame();
        if (active == null || EntryOf(character) is not { Holds: true } entry || FindSet(entry.Furnishing, entry.Set) is not { } found || entry.Role >= found.Set.Roles.Length) return null;
        var matrix = FurnitureCatalogue.HouseMatrix(active);
        var (position, degrees) = PoseSets.Place(found.Item, found.Set.Roles[entry.Role]);
        return BallFrame(Vector3.Transform(position, matrix), WorldYaw(matrix, degrees));
    }

    /// <summary>Sets one player's fit: the player's own at once, a partner's through their PC.</summary>
    public void SetFitFor(ulong character, PoseFitValues fit, bool far = false)
    {
        // Within 2 m of the ball unless Ctrl is held, or the player is already past it (until they're back within).
        far = far || FitOf(character) is { } now && PoseWire.Beyond(now.Move);
        fit = fit with { Move = PoseWire.Limit(fit.Move, far), Turn = PoseTilt.Wrap(fit.Turn), Pitch = PoseTilt.Wrap(fit.Pitch), Roll = PoseTilt.Wrap(fit.Roll),
            Scale = Math.Clamp(fit.Scale, PoseWire.MinScale, PoseWire.MaxScale) };
        if (character == Me || character == 0)
        {
            SetPoseFit(fit.Move, fit.Turn, fit.Pitch, fit.Roll, far);
            SetPoseScale(fit.Scale);
            return;
        }
        // An NPC (the possessed one, or a partner on the set): any PC on the set may fit it, and its entry says so to everyone.
        if (PoseNpcs.IsNpc(character))
        {
            if (NpcOf(character) is not { } npc || EntryOf(character) is not { Holds: true } held || FindSet(held.Furnishing, held.Set) is not { } set || held.Role >= set.Set.Roles.Length) return;
            if (character != PoseSubject && !PosePartners().Contains(character)) return;
            var role = set.Set.Roles[held.Role];
            SetNpcEntry(npc, held with { Move = fit.Move, Turn = fit.Turn, Pitch = PoseTilt.Wrap(role.Pitch + fit.Pitch), Roll = PoseTilt.Wrap(role.Roll + fit.Roll), Scale = fit.Scale });
            // Set by hand: Haven stops measuring the surface (the move shown already had the lift in it).
            if (npcPoses.TryGetValue(npc, out var fitted)) { fitted.Lift = 0; fitted.LiftUntil = 0; fitted.Fitted = true; }
            return;
        }
        if (!PosePartners().Contains(character)) return;
        partnerFits[character] = (fit, Environment.TickCount64 + 2000);
        partnerFitsOut[character] = fit;
    }

    /// <summary>
    /// Moves everyone on the set together, from a change to the player's own fit: the same move in the world for each,
    /// a turn of the whole group about its middle, and the same change of tilt, lean and size.
    /// </summary>
    public void SetFitForEveryone(PoseFitValues mine, bool far = false)
    {
        var subject = PoseSubject;
        if (FitOf(subject) is not { } current || BallFrameOf(subject) is not { } myFrame) return;
        var partners = PosePartners();
        var turn = PoseTilt.Wrap(mine.Turn - current.Turn) * MathF.PI / 180;
        var shift = Vector3.TransformNormal(mine.Move - current.Move, myFrame);
        var ratio = current.Scale > 0 ? mine.Scale / current.Scale : 1;
        List<(ulong Character, PoseFitValues Fit, Matrix4x4 Frame)> group = [(subject, current, myFrame)];
        foreach (var partner in partners) if (FitOf(partner) is { } fit && BallFrameOf(partner) is { } frame) group.Add((partner, fit, frame));
        var places = group.Select(g => Vector3.Transform(g.Fit.Move, g.Frame)).ToArray();
        var middle = places.Aggregate(Vector3.Zero, (a, b) => a + b) / places.Length;
        // A turn of the game's yaw: facing (sin, cos) turns towards +X.
        var rotate = Matrix4x4.CreateRotationY(turn);
        for (var i = 0; i < group.Count; i++)
        {
            var (character, fit, frame) = group[i];
            var world = middle + Vector3.Transform(places[i] - middle, rotate) + shift;
            var move = Matrix4x4.Invert(frame, out var inverse) ? Vector3.Transform(world, inverse) : fit.Move;
            SetFitFor(character, new(move, fit.Turn + turn * 180 / MathF.PI, fit.Pitch + (mine.Pitch - current.Pitch), fit.Roll + (mine.Roll - current.Roll), fit.Scale * ratio), far);
        }
    }

    // A partner's asked-for fit goes at most four times a second per partner; the relay forwards it only while both
    // hold balls of the set.
    private void SendPartnerFits()
    {
        if (partnerFitsOut.Count == 0) return;
        if (Bound is not { Playing: true } bound || !IsSharedVisit || !engine.HousingRelay.HousingSceneReady) { partnerFitsOut.Clear(); return; }
        foreach (var (character, fit) in partnerFitsOut.ToArray())
        {
            if (partnerFitSent.TryGetValue(character, out var at) && Environment.TickCount64 - at < 250) continue;
            partnerFitsOut.Remove(character);
            if (PoseNpcs.IsNpc(character) || !poseEntries.TryGetValue(character, out var entry) || !entry.Holds || entry.Furnishing != bound.Furnishing || entry.Set != bound.Set) continue;
            partnerFitSent[character] = Environment.TickCount64;
            _ = engine.HousingRelay.SendPoseAdjust(new(Me, character, entry.Furnishing, entry.Set, entry.Role, fit.Move, fit.Turn, fit.Pitch, fit.Roll, fit.Scale));
        }
    }

    // A partner on the same set moved this player: taken as the player's own fit, unless they turned that off.
    private void OnPoseAdjust(PoseAdjust adjust) => Run(() =>
    {
        if (adjust.Target != Me || !PoseLetPartnersAdjust || Bound is not { Playing: true, Positioning: false } bound
            || bound.Furnishing != adjust.Furnishing || bound.Set != adjust.Set || bound.Role != adjust.Role) return;
        if (!poseEntries.TryGetValue(adjust.Sender, out var sender) || !sender.Holds || sender.Furnishing != bound.Furnishing || sender.Set != bound.Set) return;
        Bound = bound with { Move = PoseWire.Limit(adjust.Move, far: true), Lift = 0, LiftUntil = 0, Fitted = true, Turn = PoseTilt.Wrap(adjust.Turn), Pitch = PoseTilt.Wrap(adjust.Pitch),
            Roll = PoseTilt.Wrap(adjust.Roll), Scale = Math.Clamp(adjust.Scale, PoseWire.MinScale, PoseWire.MaxScale) };
    });

    // The home's animation packages on the locker: the owner uploads, guests who agreed download (as custom furniture).
    private void UpdateAnimationTransfers()
    {
        var hashes = PoseSets.Packages(active!).ToArray();
        if (hashes.Length == 0) return;
        var relay = engine.HousingRelay;
        var gathering = IsSharedVisit && relay.HousingSceneReady;
        if (gathering && relay.RoomId != animationRoom) { animationRoom = relay.RoomId; animationUploaded.Clear(); animationRetries.Clear(); }
        var owner = OwnsActiveHouse;
        if (owner && !AgreementAccepted) return;
        foreach (var hash in hashes)
        {
            if (animationWorking.Contains(hash) || animationRetries.TryGetValue(hash, out var retry) && retry.Next > DateTimeOffset.UtcNow) continue;
            if (owner ? animationUploaded.Contains(hash) || !gathering && Animations.Item(hash)?.Stored != null || !Animations.Has(hash)
                : !gathering || !CustomPicturesLoaded || Animations.Has(hash)) continue;
            animationWorking.Add(hash);
            var origin = relay.ContentOrigin; var relayUrl = engine.HousingRelayUrl; var character = engine.HousingCharacterId;
            _ = Task.Run(async () =>
            {
                try
                {
                    // A few transfers at a time: a home can list dozens of packages, each held whole while it's checked.
                    await TransferSlots.WaitAsync();
                    if (!owner) await Animations.Download(relay, hash);
                    else if (gathering) await Animations.Upload(relay.RequestContentTicket, origin ?? throw new InvalidOperationException("The relay's address is unknown."), hash, CancellationToken.None);
                    else
                    {
                        await using var session = await ContentSessionClient.Open(relayUrl, character, pictures.SecretFor(character), CancellationToken.None, o => Account.Apply(o, character, ""));
                        await Animations.Upload(session.Request, session.Origin, hash, CancellationToken.None);
                    }
                    Run(() => { animationWorking.Remove(hash); animationRetries.Remove(hash); contentBroken.Remove(hash); if (owner) animationUploaded.Add(hash); });
                }
                catch (Exception ex)
                {
                    var quiet = ex is PictureNotReadyException;
                    if (!quiet) log.Warning(ex, "Haven could not {Action} custom animations {Hash}.", owner ? "upload" : "download", hash[..12]);
                    Run(() =>
                    {
                        animationWorking.Remove(hash);
                        if (!quiet) contentBroken.Add(hash);
                        var attempts = animationRetries.GetValueOrDefault(hash).Attempts + 1;
                        // A guest's download that failed outright stops there: the player is told and chooses to try again or give up.
                        var stopped = !quiet && !owner;
                        animationRetries[hash] = (attempts, stopped ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddSeconds(quiet ? 3 : Math.Min(120, 3 * Math.Pow(2, attempts))));
                        if (stopped) DownloadFailed(hash, ContentKind.Animations, ex.Message);
                        else if (!quiet && attempts == 1) Failed?.Invoke("Custom animations could not be uploaded: " + ex.Message);
                    });
                }
                finally { TransferSlots.Release(); }
            });
        }
    }

    // Setting up sets (owner and decorators) --------------------------------------------------------------------

    /// <summary>Adds or replaces a set on a furnishing, giving its custom animations their carriers in this home.</summary>
    public void SavePoseSet(Guid furnishing, PoseSet set)
    {
        if (active?.Furnishings.FirstOrDefault(f => f.Id == furnishing) is not { } item) return;
        if (item.BuildingPart != null) throw new InvalidOperationException("Building pieces can't hold pose balls.");
        if (set.Roles.FirstOrDefault(r => !r.Custom && PoseEmotes.All(e => e.Id != r.Emote)) is { } bad)
            throw new InvalidOperationException($"{(bad.Name.Length > 0 ? bad.Name : "A ball")} needs a looping or seated emote.");
        // Each custom animation goes to the free carrier whose bodies would draw it on bones it was made for (a mod
        // made for women's bodies only fits carriers where women read their own copies).
        var assigned = PoseSets.AssignCarriers(active, set, Animations.CarrierFit);
        var sets = item.Poses ?? [];
        sets = sets.Any(s => s.Id == set.Id) ? sets.Select(s => s.Id == set.Id ? assigned : s).ToArray() : [.. sets, assigned];
        if (sets.Length > PoseSets.MaxSetsPerFurnishing) throw new InvalidOperationException($"A furnishing holds at most {PoseSets.MaxSetsPerFurnishing} animation sets.");
        ChangeFurniture(item with { Poses = sets });
    }

    public void RemovePoseSet(Guid furnishing, Guid set)
    {
        if (active?.Furnishings.FirstOrDefault(f => f.Id == furnishing) is not { Poses: { } sets } item) return;
        var left = sets.Where(s => s.Id != set).ToArray();
        ChangeFurniture(item with { Poses = left.Length == 0 ? null : left });
    }

    /// <summary>The default balls for a new set on a furnishing: across the top of it, facing its front.</summary>
    public float[][] DefaultPoseBalls(Furnishing item, int count)
    {
        var prefab = Catalogue.Prefab(item);
        if (prefab.Models.Length == 0) return PoseSets.DefaultPositions(count, Vector3.Zero, Vector3.Zero);
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var model in prefab.Models)
        {
            var box = PlacementBounds.Transform(model.Minimum, model.Maximum, model.Transform);
            min = Vector3.Min(min, box.Minimum); max = Vector3.Max(max, box.Maximum);
        }
        return PoseSets.DefaultPositions(count, min, max);
    }

    /// <summary>A ball's offset in its furnishing for where the player stands now, and the way they face (relative to the furnishing).</summary>
    public (float[] Position, float Rotation)? PoseBallHere(Furnishing item)
    {
        if (active == null || engine.HousingPlayerPosition is not { } world || !Matrix4x4.Invert(FurnitureCatalogue.HouseMatrix(active), out var toHouse)) return null;
        var offset = PoseSets.OffsetFor(item, Vector3.Transform(world, toHouse));
        if (Math.Abs(offset.X) > PoseSets.MaxOffset || Math.Abs(offset.Y) > PoseSets.MaxOffset || Math.Abs(offset.Z) > PoseSets.MaxOffset) return null;
        var degrees = engine.HousingPlayerRotation is float yaw ? yaw * 180 / MathF.PI : item.Rotation;
        var relative = ((degrees - item.Rotation) % 360 + 540) % 360 - 180;
        return ([MathF.Round(offset.X, 3), MathF.Round(offset.Y, 3), MathF.Round(offset.Z, 3)], MathF.Round(relative, 1));
    }

    // Props worn as gear ---------------------------------------------------------------------------------------

    // The prop the player's playing animation uses, worn once they said yes; asked first when they haven't said.
    private void UpdateProp()
    {
        PropWanted? want = null;
        PropQuestion? ask = null;
        if (Bound is { } bound && FindSet(bound.Furnishing, bound.Set) is { } found && bound.Role < found.Set.Roles.Length && found.Set.Roles[bound.Role].Custom
            && found.Set.Package is { } package && ShowsAnimations(package) && Animations.Manifest(package)?.Props is [var prop, ..] && PropItem(prop.Slot, prop.Set) is { } item)
        {
            var name = $"{item.Name} ({Animations.Name(package) ?? "animation"})";
            switch (Gear.Choice(package))
            {
                case null: ask = new(package, Animations.Name(package) ?? "This animation", item.Name, PoseProps.Label(prop.Slot)); break;
                case true when bound.Playing: want = new(package, 0, prop, name, item.Id, file => Animations.UnpackedFile(package, file)); break;
            }
        }
        // NPCs on playing balls wear their animation's prop too (each PC dresses its own copy), once this PC said yes to it.
        var npcWants = new Dictionary<int, PropWanted>();
        foreach (var npc in (active?.Npcs ?? []).Concat(Visitors?.Invoke() ?? []))
        {
            var key = PoseNpcs.Key(npc.Id);
            if (NpcOnBall(npc.Id) == null || EntryOf(key) is not { Holds: true } entry || FindSet(entry.Furnishing, entry.Set) is not { } held || entry.Role >= held.Set.Roles.Length
                || !held.Set.Roles[entry.Role].Custom || held.Set.Package is not { } npcPackage || !ShowsAnimations(npcPackage)
                || Animations.Manifest(npcPackage)?.Props is not [var npcProp, ..] || PropItem(npcProp.Slot, npcProp.Set) is not { } npcItem || engine.HousingPoseNpcIndex(key) is not { } index) continue;
            switch (Gear.Choice(npcPackage))
            {
                case null: ask ??= new(npcPackage, Animations.Name(npcPackage) ?? "This animation", npcItem.Name, PoseProps.Label(npcProp.Slot), npc.Name); break;
                case true: npcWants[index] = new(npcPackage, 0, npcProp, $"{npcItem.Name} ({Animations.Name(npcPackage) ?? "animation"})", npcItem.Id, file => Animations.UnpackedFile(npcPackage, file)); break;
            }
        }
        PropAsked = ask;
        Gear.Update(want, engine.HousingPlayerPosition != null);
        Gear.UpdateNpcs(npcWants);
    }
    private static readonly Dictionary<int, PropWanted> NoProps = [];

    /// <summary>Removes the props Haven installed in Penumbra (they install again when next worn).</summary>
    public void RemoveAnimationProps()
    {
        var removed = Gear.RemoveInstalled();
        Status = removed == 1 ? "Removed 1 animation prop from Penumbra." : $"Removed {removed} animation props from Penumbra.";
    }

    /// <summary>The player's answer to the prop question: wear it (now and, remembered, next time) or not.</summary>
    public void AnswerProp(bool wear, bool remember)
    {
        if (PropAsked is not { } asked) return;
        Gear.Choose(asked.Package, wear, remember);
        PropAsked = null;
    }

    // The item that shows a piece of gear: the first item for that slot whose model is that set (lowest variant).
    private readonly Dictionary<(string, int), (ulong Id, string Name)?> propItems = [];
    public (ulong Id, string Name)? PropItem(string slot, int set)
    {
        lock (propItems) if (propItems.TryGetValue((slot, set), out var known)) return known;
        (ulong Id, string Name)? found = null;
        var best = int.MaxValue;
        foreach (var item in furnitureData.GetExcelSheet<Lumina.Excel.Sheets.Item>())
        {
            if ((int)(item.ModelMain & 0xFFFF) != set || item.Name.IsEmpty || item.EquipSlotCategory.ValueNullable is not { } category) continue;
            var fits = slot switch
            {
                "Head" => category.Head > 0, "Body" => category.Body > 0, "Hands" => category.Gloves > 0, "Legs" => category.Legs > 0, "Feet" => category.Feet > 0,
                "Ears" => category.Ears > 0, "Neck" => category.Neck > 0, "Wrists" => category.Wrists > 0, "RFinger" or "LFinger" => category.FingerR > 0 || category.FingerL > 0,
                _ => false,
            };
            var variant = (int)(item.ModelMain >> 16 & 0xFFFF);
            if (fits && variant < best) { best = variant; found = (item.RowId, item.Name.ToString()); }
        }
        lock (propItems) propItems[(slot, set)] = found;
        return found;
    }

    // Importing animations ------------------------------------------------------------------------------------

    /// <summary>An animation mod opened for import: its name, the animations it changes, and its option groups.</summary>
    public sealed record AnimationImportChoice(string Path, string ModName, IReadOnlyList<PoseAnimationCandidate> Candidates)
    {
        /// <summary>Gear the mod changes that its animations may use as a prop, each with the item that shows it.</summary>
        public IReadOnlyList<(PosePropCandidate Prop, string Item)> Props { get; init; } = [];
    }
    public string? AnimationImportStatus { get; private set; }

    /// <summary>Opens an animation mod (a .pmp or a mod folder) off the framework thread and lists the animations it changes.</summary>
    public Task<AnimationImportChoice> ReadAnimationMod(string path)
    {
        AnimationImportStatus = "Reading the mod…";
        return Task.Run(() =>
        {
            try
            {
                using var mod = PenumbraMod.Open(path);
                mod.SelectAll();
                var candidates = PoseAnimations.Candidates(mod, AnimationLabel);
                if (candidates.Count == 0) throw new InvalidOperationException(PoseAnimations.MovementOnly(mod)
                    ? "This mod replaces how characters walk, run or stand. Pose balls play an emote's looping animation, so they can't use it."
                    : "This mod changes no character animations Haven can use.");
                var props = PoseProps.Detect(mod).Select(p => (Prop: p, Item: PropItem(p.Slot, p.Set)?.Name)).Where(p => p.Item != null).Select(p => (p.Prop, p.Item!)).ToArray();
                return new AnimationImportChoice(path, mod.Name.Length > 0 ? mod.Name : System.IO.Path.GetFileNameWithoutExtension(path), candidates) { Props = props };
            }
            finally { Run(() => AnimationImportStatus = null); }
        });
    }

    /// <summary>Imports chosen animations of a mod as one package, under a name.</summary>
    public Task<string> ImportAnimations(AnimationImportChoice choice, string name, IReadOnlyList<(PoseAnimationCandidate Candidate, string Name)> chosen, PosePropCandidate? prop = null)
    {
        AnimationImportStatus = "Importing…";
        return Task.Run(() =>
        {
            try
            {
                using var mod = PenumbraMod.Open(choice.Path);
                mod.SelectAll();
                return Animations.Import(mod, name, chosen, prop);
            }
            finally { Run(() => AnimationImportStatus = null); }
        });
    }

    /// <summary>The homes on this PC whose pose balls play a package.</summary>
    public string[] AnimationHomes(string hash) => allHouses.Where(h => h.Id != active?.Id).Append(active).OfType<House>()
        .Where(h => PoseSets.Packages(h).Contains(hash)).Select(h => h.Name).Distinct().ToArray();

    /// <summary>
    /// Deletes one of the player's animation packages from this PC (refused while a home of theirs plays it), and from
    /// Haven's server when this PC uploaded it, giving its bytes back to the allowance.
    /// </summary>
    public void DeleteAnimations(string hash)
    {
        if (AnimationHomes(hash) is { Length: > 0 } homes) throw new InvalidOperationException($"These animations play in {string.Join(", ", homes)}. Remove those pose balls first.");
        var uploaded = Animations.Item(hash) is { Stored: not null };
        Animations.Delete(hash);
        Status = "Animations deleted.";
        if (!uploaded) return;
        var relayUrl = engine.HousingRelayUrl; var character = engine.HousingCharacterId;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var session = await ContentSessionClient.Open(relayUrl, character, pictures.SecretFor(character), CancellationToken.None, o => Account.Apply(o, character, ""));
                await Animations.DeleteFromServer(session.Request, session.Origin, hash, CancellationToken.None);
                Run(() => Status = "Animations deleted from this PC and Haven's server.");
            }
            catch (Exception ex) when (Retryable(ex))
            {
                Run(() => { QueueServerDelete(ServerContent.Animations, hash, character, ex); Status = "Animations deleted. Haven deletes their copy on the server as soon as it can."; });
            }
            catch (NotUploaderException ex) { Run(() => throw new InvalidOperationException("The animations were deleted from this PC. " + ex.Message, ex)); }
        });
    }

    // What an emote animation (a timeline key) replaces, for the player: its emote's name and command when one plays it.
    private Dictionary<string, string>? animationLabels;
    private string? AnimationLabel(string key)
    {
        if (animationLabels == null)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var emote in furnitureData.GetExcelSheet<Lumina.Excel.Sheets.Emote>())
            {
                if (emote.Name.IsEmpty) continue;
                var command = emote.TextCommand.ValueNullable?.Command.ToString();
                var label = emote.Name.ToString() + (string.IsNullOrEmpty(command) ? "" : $" ({command})");
                for (var i = 0; i < emote.ActionTimeline.Count; i++)
                    if (emote.ActionTimeline[i].ValueNullable?.Key.ToString() is { Length: > 0 } timeline) labels.TryAdd(timeline, label);
            }
            animationLabels = labels;
        }
        return animationLabels.TryGetValue(key, out var found) ? found : null;
    }

    /// <summary>The looping and seated emotes a ball may play, with their names, for the set editor.</summary>
    public IReadOnlyList<(uint Id, string Name)> PoseEmotes => poseEmotes ??= furnitureData.GetExcelSheet<Lumina.Excel.Sheets.Emote>()
        .Where(e => !e.Name.IsEmpty && e.TextCommand.RowId != 0 && e.EmoteMode.RowId != 0 && e.EmoteMode.ValueNullable?.ConditionMode is > 0 && e.ActionTimeline[0].RowId != 0
            && !e.DrawsWeapon && !PoseCarriers.IsCarrierEmote(e.RowId))
        .Select(e => (e.RowId, e.Name.ToString())).OrderBy(e => e.Item2, StringComparer.OrdinalIgnoreCase).ToArray();
    private (uint Id, string Name)[]? poseEmotes;
}
