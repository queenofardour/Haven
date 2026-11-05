using System.Numerics;
using Haven.Core;

namespace Haven;

// NPCs on pose balls. A possessed NPC takes a ball as the player would, and stays on it
// when let go. Its entry goes out as an NPC message ("npc" / "pose"), not to the relay's pose table: every PC keeps the
// NPCs' entries beside the people's (PoseEntries merges them; a person keeps a ball an NPC also claimed). Whoever last
// set an NPC's entry repeats it for late joiners; if they leave, everyone who knows it does. Each PC puts its own copy
// of the NPC on the ball once the set plays (NpcPossession asks NpcOnBall), at the scale and tilt the entry says.
internal sealed partial class HousingController
{
    /// <summary>Set by the plugin: the NPC this PC possesses, or null.</summary>
    public Func<Guid?>? PossessedNpc { get; set; }
    /// <summary>Set by the plugin: where an NPC stands on this PC, or null when it isn't here.</summary>
    public Func<Guid, Vector3?>? NpcAt { get; set; }

    private sealed class NpcPoseState
    {
        public PoseEntry Entry;
        public ulong Keeper;            // who last set it (repeats it)
        public DateTime RepeatAt;
        public bool Unsent;             // changed here, not yet sent (a drag goes at most four times a second)
        public DateTime SentAt;
        public long Restart;            // the animation started again here (RestartPoseAnimation for an NPC)
        // Rest on surface, on the PC that set the entry: the NPC is raised while its animation settles in, then the
        // lift becomes part of its move (as a player's does). A fit (saved, or set by hand) says where it rests already.
        public float Lift;
        public long LiftUntil;
        public bool Fitted, WasPlaying;
    }
    private readonly Dictionary<Guid, NpcPoseState> npcPoses = [];
    // NPCs whose saved ball (HomeNpc.Seat) was hidden or left this visit: it doesn't come back until the next visit.
    private readonly HashSet<Guid> dismissedSeats = [];
    private Guid? seatsHome;
    private const double NpcPoseRepeat = 5, NpcPoseOrphanRepeat = 8, NpcPoseSendGap = .25;

    /// <summary>The NPC the pose controls act for: the possessed one (it takes balls, and the HUD fits it), else none.</summary>
    private Guid? PoseNpc => PossessedNpc?.Invoke() is { } id && NpcHere(id) ? id : null;

    /// <summary>Whom the pose HUD is for: the possessed NPC while it has a ball, else the player.</summary>
    public ulong PoseSubject => PoseNpc is { } npc && EntryOf(PoseNpcs.Key(npc)) is { Holds: true } ? PoseNpcs.Key(npc) : Me;

    /// <summary>How far Haven is raising an NPC while it measures its rest on the surface (on the PC that does).</summary>
    private float NpcLift(Guid npc) => npcPoses.TryGetValue(npc, out var state) ? state.Lift : 0;

    /// <summary>
    /// The NPCs' entries in this home: those set during the visit, and for the rest their saved balls (every PC reads
    /// those from the home, so they need no message), unless hidden or left this visit.
    /// </summary>
    private IEnumerable<PoseEntry> NpcEntries
    {
        get
        {
            foreach (var state in npcPoses.Values) if (!state.Entry.Empty) yield return state.Entry;
            foreach (var npc in active?.Npcs ?? [])
                if (npc is { Away: false, Seat: { } seat } && !npcPoses.ContainsKey(npc.Id) && !dismissedSeats.Contains(npc.Id)
                    && FindSet(seat.Furnishing, seat.Set) is { } found && seat.Role < found.Set.Roles.Length)
                    yield return seat.Entry(PoseNpcs.Key(npc.Id), found.Set.Roles[seat.Role]);
        }
    }

    private Guid? NpcOf(ulong key) => PoseNpcs.IsNpc(key) ? NpcByKey(key)?.Id : null;

    // Sets an NPC's entry here and sends it (a drag at most four times a second; UpdateNpcPoses sends what's left).
    private void SetNpcEntry(Guid npc, PoseEntry entry)
    {
        entry = entry.Empty ? PoseEntry.None(PoseNpcs.Key(npc)) : entry with { Character = PoseNpcs.Key(npc) };
        if (!npcPoses.TryGetValue(npc, out var state)) npcPoses[npc] = state = new NpcPoseState();
        if (state.Entry == entry && state.Keeper == Me && !state.Unsent) return;
        state.Entry = entry; state.Keeper = Me; state.Unsent = true;
        SendNpcPose(npc, state);
        if (entry.Empty && !state.Unsent) npcPoses.Remove(npc);
    }

    private void SendNpcPose(Guid npc, NpcPoseState state)
    {
        var now = DateTime.UtcNow;
        if (!IsSharedVisit || !engine.HousingRelay.HousingSceneReady) { state.Unsent = false; return; }
        if ((now - state.SentAt).TotalSeconds < NpcPoseSendGap) return;
        state.Unsent = false; state.SentAt = now; state.RepeatAt = now.AddSeconds(NpcPoseRepeat);
        _ = engine.HousingRelay.SendMiniGame(new GameMessage(Me, GameMessage.Everyone, "npc", "pose", npc, PoseNpcs.Encode(state.Entry)));
    }

    /// <summary>An NPC's entry from another PC (NpcPossession passes "pose" messages on).</summary>
    public void ReceiveNpcPose(ulong sender, Guid npc, int[]? values)
    {
        if (sender == Me || PoseNpcs.Decode(npc, values) is not { } entry) return;
        if (entry.Empty) { npcPoses.Remove(npc); return; }
        if (!npcPoses.TryGetValue(npc, out var state)) npcPoses[npc] = state = new NpcPoseState();
        state.Entry = entry; state.Keeper = sender; state.Unsent = false;
        state.RepeatAt = DateTime.UtcNow.AddSeconds(NpcPoseOrphanRepeat + Random.Shared.NextDouble() * 4);
    }

    // Each frame (from UpdatePoses): drops entries of NPCs or sets the home no longer has, sends what's waiting, and
    // repeats entries this PC keeps (or that nobody here keeps any more) for late joiners.
    private void UpdateNpcPoses()
    {
        var now = DateTime.UtcNow;
        if (seatsHome != active?.Id) { seatsHome = active?.Id; dismissedSeats.Clear(); }
        var members = engine.HousingRelay.HousingMembers.Values.Select(m => m.ContentId).ToHashSet();
        foreach (var (npc, state) in npcPoses.ToArray())
        {
            // Waiting to go out (a stand-up included, which is then forgotten).
            if (state.Unsent) { SendNpcPose(npc, state); if (state.Entry.Empty && !state.Unsent) npcPoses.Remove(npc); continue; }
            if (state.Entry.Empty || !NpcHere(npc) || FindSet(state.Entry.Furnishing, state.Entry.Set) is not { } found
                || state.Entry.Holds && state.Entry.Role >= found.Set.Roles.Length)
            { npcPoses.Remove(npc); continue; }
            if (!IsSharedVisit || now < state.RepeatAt) continue;
            var orphaned = state.Keeper != Me && !members.Contains(state.Keeper);
            if (state.Keeper == Me || orphaned)
            {
                state.SentAt = DateTime.MinValue; state.Unsent = true;
                SendNpcPose(npc, state);
                if (orphaned) state.RepeatAt = now.AddSeconds(NpcPoseOrphanRepeat + Random.Shared.NextDouble() * 4);
            }
        }
        // A person took a ball this PC's NPC held (both claimed it): the NPC gives way, for everyone.
        var merged = PoseEntries.Where(e => PoseNpcs.IsNpc(e.Character)).ToDictionary(e => e.Character);
        foreach (var (npc, state) in npcPoses.ToArray())
            if (state.Keeper == Me && state.Entry.Holds && merged.TryGetValue(state.Entry.Character, out var shown) && !shown.Holds)
            {
                SetNpcEntry(npc, shown);
                if (npc == PoseNpc) Failed?.Invoke("Someone else took that place first.");
            }
        // Rest on surface for the NPCs this PC put on balls.
        foreach (var (npc, state) in npcPoses.ToArray())
        {
            if (state.Keeper != Me || !state.Entry.Holds) { state.WasPlaying = false; continue; }
            var playing = NpcOnBall(npc) != null;
            if (playing && !state.WasPlaying && PoseRestOnSurface && !state.Fitted) { state.Lift = 0; state.LiftUntil = Environment.TickCount64 + LiftMilliseconds; }
            state.WasPlaying = playing;
            if (state.LiftUntil == 0) continue;
            if (!playing) { state.Lift = 0; state.LiftUntil = 0; continue; }
            if (Environment.TickCount64 < state.LiftUntil) { MeasureNpcLift(npc, state); continue; }
            // Measured: the lift becomes part of its move, for everyone.
            var lifted = state.Entry with { Move = PoseWire.Limit(state.Entry.Move + Vector3.UnitY * state.Lift, far: true) };
            state.Lift = 0; state.LiftUntil = 0; state.Fitted = true;
            SetNpcEntry(npc, lifted);
        }
        // NPCs on a playing set are drawn at the scale, tilt and lean their entries say (with the people, in UpdatePoses).
    }

    // As MeasureLift for the player: the NPC's lowest joint against the surface straight below it, raised (never lowered).
    private void MeasureNpcLift(Guid npc, NpcPoseState state)
    {
        if (NpcOnBall(npc) is not { } ball || engine.HousingPoseLowestJoint(PoseNpcs.Key(npc)) is not { } body) return;
        var root = ball.Position - Vector3.UnitY * state.Lift;
        if (HavenNative.NativeEngine.HousingSurfaceBelow(new(body.Joint.X, root.Y + 1, body.Joint.Z), 3) is not { } surface) return;
        var depth = body.Joint.Y - body.Root.Y;
        var needed = Math.Clamp(surface.Y + .03f - (root.Y + depth), 0, 1.5f);
        if (needed > state.Lift + .005f)
        {
            state.Lift = needed;
            log.Information("[POSE] raised an NPC {Lift:0.000} m to rest on the surface.", needed);
        }
    }

    // A set closed for everyone: NPCs on its balls stand up, here and (from each PC's own close) everywhere.
    private void CloseNpcSet(Guid furnishing, Guid set)
    {
        foreach (var npc in active?.Npcs ?? []) if (npc.Seat is { } seat && seat.Furnishing == furnishing && seat.Set == set) dismissedSeats.Add(npc.Id);
        foreach (var npc in npcPoses.Where(p => p.Value.Entry.Furnishing == furnishing && p.Value.Entry.Set == set).Select(p => p.Key).ToArray()) npcPoses.Remove(npc);
    }

    /// <summary>The possessed NPC opens a set's balls (from a click on its furnishing), leaving any ball it had.</summary>
    private void OpenNpcPoseSet(Guid npc, Guid furnishing, Guid set) => SetNpcEntry(npc, new(PoseNpcs.Key(npc), furnishing, set, PoseEntry.Viewing, 1));

    /// <summary>The possessed NPC takes a ball: with a fit kept for it there, else at the ball.</summary>
    private void TakeNpcBall(Guid npc, Guid furnishing, Guid set, int role)
    {
        if (!IsReady || FindSet(furnishing, set) is not { } found || role < 0 || role >= found.Set.Roles.Length) return;
        var key = PoseNpcs.Key(npc);
        if (PoseSessions.Holders(PoseEntries.Where(e => e.Character != key), furnishing, found.Set)[role] != 0) throw new InvalidOperationException("Someone is already there.");
        var fit = PoseFits.Find(poseFits, HomeOwners.Key(key), set, role, "");
        var r = found.Set.Roles[role];
        SetNpcEntry(npc, new(key, furnishing, set, (byte)role, fit?.Scale ?? 1, PoseTilt.Wrap(r.Pitch + (fit?.Pitch ?? 0)), PoseTilt.Wrap(r.Roll + (fit?.Roll ?? 0)),
            PoseWire.Limit(fit?.Move ?? Vector3.Zero, far: true), PoseTilt.Wrap(fit?.Turn ?? 0)));
        if (npcPoses.TryGetValue(npc, out var state)) { state.Fitted = fit != null; state.WasPlaying = false; state.Lift = 0; state.LiftUntil = 0; }
    }

    /// <summary>The possessed NPC leaves its ball (it stands where it was before the animation took it there).</summary>
    public void LeaveNpcBall()
    {
        if (PoseNpc is not { } npc || EntryOf(PoseNpcs.Key(npc)) is not { Holds: true } entry) return;
        // A saved ball stays saved (Save its spot again, off the ball, to forget it); this visit it's left.
        dismissedSeats.Add(npc);
        SetNpcEntry(npc, entry with { Role = PoseEntry.Viewing, Scale = 1, Pitch = 0, Roll = 0, Move = default, Turn = 0 });
    }

    /// <summary>Keeps the possessed NPC's fit on its ball for next time (as a player's, under the NPC's own key).</summary>
    private void SaveNpcFit(ulong key)
    {
        if (FitOf(key) is not { } fit || PoseEntries.FirstOrDefault(e => e.Character == key) is not { Holds: true } entry) return;
        poseFits = PoseFits.Keep(poseFits, new PoseFit { Character = HomeOwners.Key(key), Set = entry.Set, Role = entry.Role, Offset = [fit.Move.X, fit.Move.Y, fit.Move.Z],
            Turn = fit.Turn, Pitch = fit.Pitch, Roll = fit.Roll, Scale = fit.Scale });
        WritePoseFits();
    }

    /// <summary>
    /// Where an NPC is on its ball, and the emote it plays there (its loop, and the intro played once when it starts),
    /// while its set plays; null when it isn't on a playing ball (or the animation isn't ready on this PC yet).
    /// </summary>
    public (Vector3 Position, float Yaw, ushort Loop, ushort Intro, long Restart)? NpcOnBall(Guid npc)
    {
        if (active == null || !IsReady) return null;
        npcPoses.TryGetValue(npc, out var state);
        var key = PoseNpcs.Key(npc);
        var entries = PoseEntries.ToArray();
        if (entries.FirstOrDefault(e => e.Character == key) is not { Holds: true } entry || FindSet(entry.Furnishing, entry.Set) is not { } found
            || entry.Role >= found.Set.Roles.Length || !PoseSessions.Playing(entries, entry.Furnishing, found.Set)) return null;
        var role = found.Set.Roles[entry.Role];
        ushort emote;
        if (role.Custom)
        {
            // A home whose animations this PC doesn't show plays the carrier's stand-in; one still preparing waits.
            if (ShowsAnimations(found.Set.Package!) && !Animations.Serving(found.Set.Package!, role.Animation, role.Carrier, role.Silent)) return null;
            emote = PoseCarriers.Get(role.Carrier)!.Value.Emote;
        }
        else emote = (ushort)role.Emote;
        if (furnitureData.GetExcelSheet<Lumina.Excel.Sheets.Emote>().GetRowOrDefault(emote) is not { } row || row.ActionTimeline[0].RowId is 0 or > ushort.MaxValue) return null;
        var matrix = FurnitureCatalogue.HouseMatrix(active);
        var (position, degrees) = PoseSets.Place(found.Item, role);
        var yaw = WorldYaw(matrix, degrees);
        var world = Vector3.Transform(entry.Move, BallFrame(Vector3.Transform(position, matrix), yaw)) + Vector3.UnitY * (state?.Lift ?? 0);
        var intro = row.ActionTimeline[1].RowId is > 0 and <= ushort.MaxValue ? (ushort)row.ActionTimeline[1].RowId : (ushort)0;
        return (world, yaw + entry.Turn * MathF.PI / 180, (ushort)row.ActionTimeline[0].RowId, intro, state?.Restart ?? 0);
    }

    /// <summary>
    /// The HUD's subject on its ball (PoseSubject: the player, or the possessed NPC): where, whether it plays, why not,
    /// and its fit; null when it has no ball.
    /// </summary>
    public PoseSeat? SeatOf(ulong subject)
    {
        if (!PoseNpcs.IsNpc(subject))
            return Bound is { } bound ? new(bound.Furnishing, bound.Set, bound.Role, bound.Playing, bound.Positioning, bound.Status,
                new(bound.Placed, bound.Turn, bound.Pitch, bound.Roll, bound.Scale)) : null;
        if (NpcOf(subject) is not { } npc || EntryOf(subject) is not { Holds: true } entry || FindSet(entry.Furnishing, entry.Set) is not { } found) return null;
        var playing = NpcOnBall(npc) != null;
        var status = playing ? ""
            : !PoseSessions.Playing(PoseEntries, entry.Furnishing, found.Set) ? "Waiting for everyone to take a place…"
            : found.Set.Roles[entry.Role] is { Custom: true } role && ShowsAnimations(found.Set.Package!) && !Animations.Serving(found.Set.Package!, role.Animation, role.Carrier, role.Silent)
                ? Animations.Has(found.Set.Package!) ? "Preparing the animation…" : "Getting the animation…"
                : "This ball can't play here.";
        return new(entry.Furnishing, entry.Set, entry.Role, playing, false, status, FitOf(subject) ?? new(Vector3.Zero, 0, 0, 0, 1));
    }

    /// <summary>The HUD's subject leaves its ball: the possessed NPC, or the player.</summary>
    public void LeaveSubjectBall()
    {
        if (PoseNpcs.IsNpc(PoseSubject)) LeaveNpcBall(); else LeavePoseBall();
    }
}

/// <summary>Someone on a ball as the pose HUD shows it: the ball, whether it plays (else why not), and their fit.</summary>
internal sealed record PoseSeat(Guid Furnishing, Guid Set, int Role, bool Playing, bool Positioning, string Status, PoseFitValues Fit);
