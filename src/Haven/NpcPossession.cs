using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Haven.Core;
using HavenNative;

namespace Haven;

/// <summary>
/// NPCs: every NPC saved in the active home and not away is shown on this PC once the scene is shown, as
/// its own client-side character in its saved look (its Glamourer look when there is one). The player can possess one:
/// their own character keeps where it is (seated, emoting, whatever it was doing), the movement keys stop reaching it
/// and the camera follows the NPC instead, which Haven walks and jumps. In a gathering a possession is claimed over the
/// relay (game "npc", first claim wins) and the possessor's PC sends the NPC's state while it moves; the others drive
/// their copy from it. A let-go NPC stays where (and in the emote) it was left for the visit; "Save spot" stores both.
/// A possessed NPC takes pose balls as the player does (HousingController.NpcPoses); while its set plays, every PC puts
/// its copy on the ball, and it stays there when let go.
/// Character objects are pooled for the visit and never deleted before it ends (see NativeEngine.Npcs).
/// </summary>
internal sealed class NpcPossession(HousingController housing, NativeEngine engine, IObjectTable objects, IPluginLog log)
{
    // Close to the game's own running and walking speeds, in metres a second; turning in radians a second.
    private const float RunSpeed = 6.3f, WalkSpeed = 2.4f, TurnSpeed = 14f;
    private const ushort RunClip = 22, WalkClip = 13;   // the ground run and walk loops (LocomotionData)
    private const ushort JumpClip = 31, FallClip = 32, LandClip = 33;   // the ground jump's start, fall and landing
    // A jump of about a metre: take-off speed and gravity in metres a second (squared); drops past LedgeDrop are falls.
    // The way down pulls harder than the way up (FallGravity), as a player's jump does: an even arc looked floaty.
    private const float JumpSpeed = 5.2f, Gravity = 14f, FallGravity = 21f, LedgeDrop = .45f;
    // States go out at most this often while the NPC changes, and are repeated this often while held (late joiners,
    // and the sign that the holder is still there: a hold nobody repeats for HoldTimeout is dropped).
    private const double StateInterval = .1, RepeatInterval = 2, HoldTimeout = 6;
    // Glamourer is asked a moment after the NPC is drawn, and again a few times if it isn't ready.
    private const double DressDelay = .25, DressRetry = 1;
    private const int DressTries = 6;
    // How long the game is given to draw a look's ordinary colours before Haven reads them back (Settle).
    private const double SettleDelay = .8;
    // Looks already cleaned this session: their advanced colours are wanted, so they are applied as they are.
    private readonly HashSet<string> settled = [];

    private sealed class Spawned(HomeNpc saved, uint slot, string gameName, Vector3 position, float yaw)
    {
        public HomeNpc Saved = saved;
        public readonly uint Slot = slot;
        public string GameName = gameName;  // the character's own name (HomeNpcs.GameNames), which Glamourer knows it by
        public bool Drawn, Glamoured, Dressed;
        public bool Pinned;     // the look it wears sets advanced colours (Glamourer keeps them until reverted)
        public bool BodyDone;   // its Customize+ profile (or none) was put on this draw of it
        public bool BodyShown;  // and it had one
        public int DressTried;
        public DateTime DressAt;
        public DateTime? UndrawnAt;
        // A look being cleaned of advanced colours nobody changed (Settle), and when to read the plain colours back.
        public string? Settling;
        public DateTime SettleAt;   // when a redraw undrew it (a hide left on longer than a second is lifted)
        public Vector3 Position = position, Target = position;
        public float Yaw = yaw, TargetYaw = yaw;
        public ushort Clip, TargetClip;
        public ushort Applied;          // the clip last started on this copy (Haven's own record; see Move)
        public ushort Rest;             // the held emote's loop it rests in when nobody moves it (0: standing)
        public ushort? RestEmote;       // that emote, for Save spot
        public ulong Holder;            // the character possessing it on another PC (0: nobody else)
        public bool HolderSpeaks;       // and speaking as it (their typing bubble shows over the NPC)
        public DateTime HeardAt, ClaimedAt;
        // Let go here somewhere other than its saved spot: this PC repeats where it was left, for late joiners.
        public bool LeftHere;
        public DateTime RepeatAt;
        // On a playing pose ball: where it stood before the animation took it there (it goes back on standing up),
        // and the ball's restart count last played.
        public Vector3? StandAt;
        public float StandYaw;
        public long BallRestart;
    }

    private readonly Dictionary<Guid, Spawned> spawned = [];
    private readonly List<uint> parked = [];            // objects of NPCs away or removed, kept for reuse
    private readonly HashSet<uint> made = [];           // every object made this visit, deleted when it ends
    private readonly ConcurrentQueue<GameMessage> inbox = new();
    private Guid? possessed;
    private bool airborne, jumpHeld;
    private float rise, fallFloor;
    private Vector3 lastGround;
    private DateTime lastSent, lastRepeat;
    private (Vector3 Position, float Yaw, ushort Clip, int Held) sentState;
    private DateTime possessedAt;
    private const double HintSeconds = 8;
    private ushort heldEmote;   // a looping emote the possessed NPC holds until it moves
    private ushort? heldEmoteId;
    private uint? retarget;     // an NPC renamed while targeted, targeted again next frame
    private Guid? visitHome;    // the home whose NPCs are shown, once its scene was first shown
    private DateTime createAfter;   // after the game refused an object, wait before asking again
    private string[] players = [];  // the names of players in the world here, which no NPC's game name may take
    private const double LeftRepeat = 5, CreateBackoff = 5;

    /// <summary>Set by the plugin: Glamourer, which dresses each NPC in its saved state once drawn.</summary>
    public NpcGlamour? Glamour { get; set; }
    /// <summary>Set by the plugin: visiting NPCs (NpcVisitors), drawn beside the home's own, possessed as their bringer chose.</summary>
    public NpcVisitors? Visitors { get; set; }
    /// <summary>Set by the plugin: Penumbra, which draws each NPC with the skeletons of the player's collection (nothing else of it).</summary>
    public NpcCollections? Collections { get; set; }
    /// <summary>Set by the plugin: Customize+, which shapes each NPC with its saved profile once dressed.</summary>
    public NpcBody? Body { get; set; }
    /// <summary>Set by the plugin: an NPC's name changed, so the game's nameplates are drawn again (NpcNameplates).</summary>
    public Action? NameChanged { get; set; }

    /// <summary>Set by the plugin: an emote's main timeline, and whether it loops (held emotes); null if it has none.</summary>
    public Func<ushort, (ushort Timeline, bool Loop)?>? EmoteTimeline { get; set; }
    /// <summary>Set by the plugin: prints a line in /say as spoken by the named NPC.</summary>
    public Action<string, string>? PrintSay { get; set; }
    /// <summary>While possessing: /say lines are spoken by the NPC (the bar's toggle).</summary>
    public bool SpeakAsNpc { get; set; } = true;

    /// <summary>A typed emote while possessing plays on the NPC: true when it did.</summary>
    public bool PlayEmote(ushort emote)
    {
        if (possessed is not { } id || !spawned.TryGetValue(id, out var npc) || EmoteTimeline?.Invoke(emote) is not { } found) return false;
        if (found.Loop) { heldEmote = found.Timeline; heldEmoteId = emote; }
        else { heldEmote = 0; heldEmoteId = null; engine.HousingNpcPlay(npc.Slot, found.Timeline); Send("act", id, [found.Timeline]); }
        return true;
    }

    /// <summary>A /say line while possessing (and speaking as the NPC): printed here and on every PC as the NPC's.</summary>
    public bool Say(string text)
    {
        if (possessed is not { } id || !SpeakAsNpc || !spawned.TryGetValue(id, out var npc)) return false;
        text = text.Trim();
        if (text.Length == 0) return false;
        if (text.Length > GameMessage.MaxText) text = text[..GameMessage.MaxText];
        PrintSay?.Invoke(npc.Saved.Name, text);
        if (housing.IsSharedVisit)
            _ = engine.HousingRelay.SendMiniGame(new GameMessage(engine.HousingCharacterId, GameMessage.Everyone, "npc", "say", id, null, text));
        return true;
    }

    public bool Possessing => possessed != null;
    public Guid? PossessedId => possessed;
    /// <summary>Where an NPC is drawn now on this PC (on its ball, if it's on one), or null when it isn't here.</summary>
    public Vector3? PositionOf(Guid id) => spawned.TryGetValue(id, out var npc) && npc.Drawn ? npc.Position : null;
    public string? PossessedName => possessed is { } id && spawned.TryGetValue(id, out var npc) ? npc.Saved.Name : null;
    public bool IsPossessed(Guid id) => possessed == id;
    public bool HeldByOther(Guid id) => spawned.TryGetValue(id, out var npc) && npc.Holder != 0;
    /// <summary>Whether an NPC is here and drawn (not away, not still appearing).</summary>
    public bool Present(Guid id) => spawned.TryGetValue(id, out var npc) && npc.Drawn;
    /// <summary>Where an NPC stands now on this PC and the emote it rests in (for "Save spot").</summary>
    public (Vector3 Position, float Yaw, ushort? Emote)? Place(Guid id) => spawned.TryGetValue(id, out var npc)
        ? npc.StandAt is { } stand ? (stand, npc.StandYaw, npc.RestEmote) : (npc.Position, npc.Yaw, possessed == id ? heldEmoteId : npc.RestEmote) : null;

    /// <summary>The drawn NPCs: id, name, slot, where they stand and how tall they are drawn.</summary>
    public IEnumerable<(Guid Id, string Name, uint Slot, Vector3 Position, float Height)> Shown =>
        spawned.Values.Where(x => x.Drawn).Select(x => (x.Saved.Id, x.Saved.Name, x.Slot, x.Position, engine.HousingNpcHeight(x.Slot) ?? 1.7f));
    /// <summary>The object index of the NPC a character (by content id) speaks as: possessed with "Say as NPC" on.</summary>
    public int? SpeakerFor(ulong contentId)
    {
        if (contentId == engine.HousingCharacterId)
            return possessed is { } id && SpeakAsNpc && spawned.TryGetValue(id, out var mine) && mine.Drawn ? NativeEngine.HousingNpcObjectIndex(mine.Slot) : null;
        var theirs = spawned.Values.FirstOrDefault(x => x.Drawn && x.Holder == contentId && x.HolderSpeaks);
        return theirs == null ? null : NativeEngine.HousingNpcObjectIndex(theirs.Slot);
    }
    /// <summary>The home's name for the NPC drawn at an object index (its nameplate shows it), or null.</summary>
    public string? NameAt(int objectIndex) =>
        spawned.Values.FirstOrDefault(x => NativeEngine.HousingNpcObjectIndex(x.Slot) == objectIndex)?.Saved.Name;
    /// <summary>Whether a home's NPC stands at an object index.</summary>
    public bool IsNpc(int objectIndex) => spawned.Values.Any(x => NativeEngine.HousingNpcObjectIndex(x.Slot) == objectIndex);
    /// <summary>The names the NPCs' characters carry on this PC (HomeNpcs.GameNames), in home order.</summary>
    public string[] GameNames(IReadOnlyList<HomeNpc> npcs) => HomeNpcs.GameNames(npcs, players);

    // Every player character in the world part of the object table (yourself, friends in a gathering, anyone nearby):
    // an NPC carrying one of their names would share their Glamourer and Penumbra identity.
    // Read twice a second, and only in a home with NPCs.
    private long playersReadAt;
    private void ReadPlayers()
    {
        if (Environment.TickCount64 < playersReadAt) return;
        playersReadAt = Environment.TickCount64 + 500;
        var names = new List<string>();
        for (var i = 0; i < 200; i++)
            if (objects[i] is { } player && (byte)player.ObjectKind == 1 && player.Name.TextValue is { Length: > 0 } name)   // 1: a player character
                names.Add(name);
        if (!names.SequenceEqual(players)) players = [.. names];
    }
    public void Target(uint slot) => engine.HousingTargetNpc(slot);
    public bool Targeted(uint slot) => engine.HousingNpcTargeted(slot);
    public bool HasGameNameplate(uint slot) => engine.HousingNpcHasNameplate(slot);

    // A visiting NPC's own messages (its definition, welcome…) are NpcVisitors'.
    public void Receive(GameMessage message) { if (message.Game == "npc" && !VisitingNpcs.IsVisit(message)) inbox.Enqueue(message); }

    // Who may possess an NPC: the home's own, its owner and decorators (0.61.0); a visitor, as its bringer chose.
    private bool MayControl(Guid id, ulong character) =>
        Visitors?.IsVisitor(id) == true ? Visitors.MayPossess(id, character) : housing.MayDecorate(character);
    /// <summary>Whether this player may possess an NPC here.</summary>
    public bool MayPossess(Guid id) => Visitors?.IsVisitor(id) == true ? Visitors.MayPossess(id, engine.HousingCharacterId) : housing.CanFurnish;
    // The NPCs this PC draws: the home's own, then the visitors.
    private HomeNpc[] Here(House house) => Visitors?.Shown is { Length: > 0 } visiting
        ? [.. house.Npcs ?? [], .. visiting.Where(v => house.Npcs?.Any(n => n.Id == v.Id) != true)] : house.Npcs ?? [];

    // An NPC's key and drawn object for the pose code (scale, tilt, redraws).
    private void Register(Spawned npc) => engine.HousingPoseNpc(PoseNpcs.Key(npc.Saved.Id), npc.Slot);

    /// <summary>Removes every NPC and deletes their objects (the visit is ending or the home changed).</summary>
    public void RemoveAll()
    {
        Release();
        foreach (var npc in spawned.Values)
        {
            if (npc.BodyShown) Body?.Clear(NativeEngine.HousingNpcObjectIndex(npc.Slot));
            Glamour?.Unlock(NativeEngine.HousingNpcObjectIndex(npc.Slot));
        }
        engine.HousingNpcDeleteAll(made);
        Collections?.Clear();
        engine.HousingPoseNpcsClear();
        spawned.Clear(); parked.Clear(); made.Clear();
        visitHome = null; jumpsTraced = 0;
    }

    /// <summary>After a real zone change the game has rebuilt its characters: the old handles are never used again.</summary>
    public void Forget()
    {
        // Evidence for the review's open question: if these survive, they are orphaned.
        if (spawned.Count > 0)
        {
            var left = engine.HousingNpcSurvivors(spawned.Values.Select(n => (n.Slot, n.GameName)));
            log.Information("[NPC] zone changed: {Left} of {Count} NPC objects still hold their names.", left, spawned.Count);
        }
        if (possessed != null) { possessed = null; housing.NpcHolding = false; engine.HousingCameraOnNpc(null); }
        Collections?.Clear();
        engine.HousingPoseNpcsClear();
        spawned.Clear(); parked.Clear(); made.Clear();
        visitHome = null;
    }

    /// <summary>Adds an NPC in the player's look to the home, a step in front of them and facing them.</summary>
    public string Add()
    {
        if (!housing.CanFurnish) return "NPCs can be added only by someone who may decorate a ready home.";
        if (InFront() is not { } spot) return "Your character isn't ready.";
        try { housing.AddNpc(spot.Position, spot.Yaw); return housing.Status; }
        catch (InvalidOperationException e) { return e.Message; }
    }

    /// <summary>A spot a step in front of the player, on the floor there, facing the player.</summary>
    public (Vector3 Position, float Yaw)? InFront()
    {
        if (objects.LocalPlayer is not { } player) return null;
        var ahead = new Vector3(MathF.Sin(player.Rotation), 0, MathF.Cos(player.Rotation));
        var spot = NativeEngine.HousingSurfaceBelow(player.Position + ahead * 1.5f + new Vector3(0, 1, 0), 3) ?? player.Position;
        return (spot, Wrap(player.Rotation + MathF.PI));
    }

    /// <summary>Possesses the NPC nearest the player, or lets go of the one possessed.</summary>
    public string TogglePossess()
    {
        if (possessed != null) { Release(); return "You let go of the NPC."; }
        if (objects.LocalPlayer is not { } player) return "Your character isn't ready.";
        var nearest = spawned.Values.Where(x => x.Drawn && x.Holder == 0 && MayPossess(x.Saved.Id)).MinBy(x => Vector3.DistanceSquared(x.Position, player.Position));
        if (nearest == null && !housing.CanFurnish && spawned.Values.All(x => !MayPossess(x.Saved.Id))) return NpcPanelUi.NotYours;
        if (nearest == null) return spawned.Count == 0 ? "There's no NPC here. Add one from the NPCs tab." : "The NPCs are still appearing, or someone else is controlling them.";
        return Possess(nearest.Saved.Id);
    }

    public string Possess(Guid id)
    {
        if (!spawned.TryGetValue(id, out var npc) || !npc.Drawn) return "That NPC isn't here yet. Try again in a moment.";
        if (npc.Holder != 0) return "Someone else is controlling that NPC.";
        if (!MayPossess(id)) return Visitors?.IsVisitor(id) == true ? NpcPanelUi.NotYourVisitor : NpcPanelUi.NotYours;
        Release();
        npc.LeftHere = false;
        possessed = id; possessedAt = DateTime.UtcNow; airborne = false; rise = 0; lastGround = npc.Position; fallFloor = npc.Position.Y;
        // It keeps the emote it rests in until it moves.
        heldEmote = npc.Rest; heldEmoteId = npc.RestEmote;
        housing.NpcHolding = true;
        engine.HousingCameraOnNpc(npc.Slot);
        Send("claim", id);
        SendState(npc, force: true);
        return $"You're controlling {npc.Saved.Name}: move with WASD (hold Ctrl to walk, Space jumps). Let go from the bar.";
    }

    public void Release()
    {
        if (possessed is not { } id) return;
        possessed = null;
        housing.NpcHolding = false;
        engine.HousingCameraOnNpc(null);
        if (spawned.TryGetValue(id, out var npc) && npc.StandAt != null)
        {
            // On a pose ball it stays there; it stands, when it leaves the ball, where it was before.
            npc.Target = npc.StandAt.Value; npc.TargetYaw = npc.StandYaw; npc.LeftHere = true; npc.RepeatAt = DateTime.UtcNow.AddSeconds(LeftRepeat);
            SendState(npc, force: true);
        }
        else if (npc != null)
        {
            if (airborne) npc.Position = lastGround;
            // Left in the emote it holds (or standing), for the visit.
            npc.Rest = airborne ? (ushort)0 : heldEmote; npc.RestEmote = npc.Rest == 0 ? null : heldEmoteId;
            npc.Clip = npc.TargetClip = npc.Rest; npc.Target = npc.Position; npc.TargetYaw = npc.Yaw;
            npc.LeftHere = true; npc.RepeatAt = DateTime.UtcNow.AddSeconds(LeftRepeat);
            Move(npc, npc.Position, npc.Yaw, npc.Clip);
            SendState(npc, force: true);
        }
        heldEmote = 0; heldEmoteId = null;
        airborne = false; rise = 0;
        Send("release", id);
    }

    /// <summary>Each frame: keeps the shown NPCs in step with the home, draws and dresses them, and moves them.</summary>
    public void Update(float seconds)
    {
        // NPC objects are deleted only when the visit ends or the home changes. The scene is "not shown" for a frame
        // whenever a furnishing model is still loading (a piece placed, a lamp clicked); that only holds back the
        // first NPCs of a visit, and must never remove them (0.51.1 and earlier removed and remade every NPC then).
        if (housing.ActiveHouse is not { } house || !housing.IsReady || housing.EndingVisit)
        {
            if (made.Count > 0 || visitHome != null) RemoveAll();
            inbox.Clear();
            return;
        }
        if (visitHome != house.Id)
        {
            if (made.Count > 0) RemoveAll();
            if (!housing.SceneShown) { inbox.Clear(); return; }
            visitHome = house.Id;
        }
        if (house.Npcs is { Length: > 0 } || made.Count > 0 || Visitors?.Shown.Length > 0) ReadPlayers();
        if (retarget is { } again) { retarget = null; engine.HousingTargetNpc(again); }
        Sync();
        // A Penumbra redraw draws parked objects again; they stay put away.
        foreach (var slot in parked) engine.HousingNpcPark(slot);
        while (inbox.TryDequeue(out var message)) Handle(message);
        // The owner stopped this player decorating: they let go (a gathering between editing states keeps it).
        // A visitor's bringer chose who may possess it; they may change their mind.
        if (possessed is { } held && housing.IsSharedVisit && (Visitors?.IsVisitor(held) == true ? !Visitors.MayPossess(held, engine.HousingCharacterId)
                : engine.HousingRelay.Editing is { } editing && !editing.CanEdit(engine.HousingRelay.LocalContentId)))
            Release();
        seconds = Math.Clamp(seconds, 0, .1f);
        var now = DateTime.UtcNow;
        foreach (var npc in spawned.Values)
        {
            // A redraw (Glamourer's, or anyone's through Penumbra) rebuilds the model; Glamourer dresses the new one, so
            // Haven doesn't dress it again (that would undo unsaved edits made in Glamourer).
            if (npc.Drawn && engine.HousingNpcRedrawAsked(npc.Slot)) { npc.Drawn = false; npc.UndrawnAt = now; }
            if (!npc.Drawn)
            {
                if (npc.Drawn = engine.HousingNpcDrawWhenReady(npc.Slot)) { npc.DressAt = now.AddSeconds(DressDelay); npc.UndrawnAt = null; }
                else if (npc.UndrawnAt is { } since && (now - since).TotalSeconds > 1) { engine.HousingNpcShow(npc.Slot); npc.UndrawnAt = null; }
                continue;
            }
            if (!npc.Dressed && now >= npc.DressAt) Dress(npc, now);
            if (npc.Dressed && !npc.BodyDone)
            {
                npc.BodyDone = true;
                // Without a body this takes away any profile left on the name (another NPC carried it before).
                Body?.Apply(NativeEngine.HousingNpcObjectIndex(npc.Slot), npc.Saved.Name, npc.Saved.Look.Body);
                npc.BodyShown = npc.Saved.Look.Body != null;
            }
            if (npc.Settling != null && now >= npc.SettleAt) Settle(npc);
            // On a ball whose set plays, every PC holds its copy there in the ball's animation.
            if (housing.NpcOnBall(npc.Saved.Id) is { } ball)
            {
                OnBall(npc, ball);
                if (npc.Saved.Id == possessed) SendState(npc);
                continue;
            }
            if (npc.StandAt != null) LeaveBall(npc);
            if (npc.Saved.Id == possessed) { Drive(npc, seconds); continue; }
            // The holder went quiet (left, or lost the connection), or may no longer decorate.
            if (npc.Holder != 0 && ((now - npc.HeardAt).TotalSeconds > HoldTimeout || !MayControl(npc.Saved.Id, npc.Holder)))
            {
                // Perhaps mid-jump: it settles where it is.
                npc.Holder = 0; npc.HolderSpeaks = false; npc.TargetClip = Settled(npc, npc.TargetClip);
                if (NativeEngine.HousingSurfaceBelow(npc.Target + new Vector3(0, 1, 0), 30) is { } floor) npc.Target = floor;
            }
            if (npc.LeftHere && npc.Holder == 0 && now >= npc.RepeatAt) RepeatLeft(npc, now);
            // Others' NPCs (and let-go ones) ease towards their latest state.
            if (Vector3.DistanceSquared(npc.Position, npc.Target) > 25) npc.Position = npc.Target;
            else npc.Position = Vector3.Lerp(npc.Position, npc.Target, 1 - MathF.Exp(-12 * seconds));
            npc.Yaw = TurnTowards(npc.Yaw, npc.TargetYaw, TurnSpeed * seconds);
            if (npc.TargetClip == LandClip) { Move(npc, npc.Position, npc.Yaw, 0); engine.HousingNpcPlay(npc.Slot, LandClip); npc.TargetClip = 0; }
            npc.Clip = npc.TargetClip;
            Move(npc, npc.Position, npc.Yaw, npc.Clip);
        }
    }

    // Shows NPCs the home gained or brought back (one a frame, on a parked object when there is one), parks those it
    // lost or sent away, and changes the rest in place: a new name renames the character, a new plain look redraws it,
    // a new Glamourer state or name dresses it again, a new saved spot or resting emote moves an idle one.
    private void Sync()
    {
        var saved = housing.ActiveHouse is { } house ? Here(house) : [];
        var names = GameNames(saved);
        foreach (var gone in spawned.Values.Where(x => saved.FirstOrDefault(s => s.Id == x.Saved.Id) is not { Away: false }).ToList()) Park(gone);
        var setUp = false;
        for (var i = 0; i < saved.Length; i++)
        {
            var npc = saved[i];
            if (npc.Away) continue;
            if (spawned.TryGetValue(npc.Id, out var current))
            {
                if (current.Saved == npc && current.GameName == names[i]) continue;
                if (current.GameName != names[i])
                {
                    // Glamourer and Customize+ know the character by its name: let go of the old one before it changes.
                    // Renamed because a player with its name is near: their name mustn't keep the NPC's look or mods.
                    // Glamourer keeps a state per name (reverted here, at the cost of a redraw), and Penumbra a name's
                    // collection until it is deleted (made again for the new name below).
                    Glamour?.Revert(NativeEngine.HousingNpcObjectIndex(current.Slot));
                    Glamour?.Unlock(NativeEngine.HousingNpcObjectIndex(current.Slot));
                    Collections?.Forget(npc.Id);
                    if (current.BodyShown) { Body?.Clear(NativeEngine.HousingNpcObjectIndex(current.Slot)); current.BodyShown = false; }
                    // The game's nameplate keeps a targeted character's old name: target it afresh next frame.
                    if (engine.HousingNpcUntarget(current.Slot)) retarget = current.Slot;
                    engine.HousingNpcRename(current.Slot, names[i]);
                    Glamour?.Reidentify(NativeEngine.HousingNpcObjectIndex(current.Slot));
                    Collections?.Assign(NativeEngine.HousingNpcObjectIndex(current.Slot), names[i], npc.Id);
                    NameChanged?.Invoke();
                    current.GameName = names[i]; current.Glamoured = false; Redress(current);
                }
                if (current.Saved.Level != npc.Level) { engine.HousingNpcLevel(current.Slot, npc.Level ?? 0); NameChanged?.Invoke(); }
                if (!current.Saved.Look.SameAs(npc.Look with { Design = current.Saved.Look.Design }))
                { engine.HousingNpcRestyle(current.Slot, npc.Look); current.Drawn = false; current.Applied = 0; Redress(current); }
                else if (current.Saved.Look.Design != npc.Look.Design) Redress(current);
                if (current.Saved.Look.Body != npc.Look.Body) current.BodyDone = false;
                // A new saved spot or resting emote (Save spot, Bring here, undo, someone else's edit) moves an idle NPC.
                if (npc.Id != possessed && current.Holder == 0 && (!SameSpot(current.Saved, npc) || current.Saved.Emote != npc.Emote))
                {
                    current.Target = Spot(npc); current.TargetYaw = npc.Yaw;
                    current.Rest = RestClip(npc.Emote); current.RestEmote = current.Rest == 0 ? null : npc.Emote;
                    current.TargetClip = current.Rest; current.LeftHere = false;
                }
                current.Saved = npc;
                continue;
            }
            if (setUp) continue;
            setUp = true;
            uint slot; var fresh = parked.Count == 0;
            if (!fresh) { slot = parked[^1]; parked.RemoveAt(parked.Count - 1); }
            else if (DateTime.UtcNow < createAfter) continue;
            else if (engine.HousingNpcCreate() is { } created) { slot = created; made.Add(slot); }
            else { createAfter = DateTime.UtcNow.AddSeconds(CreateBackoff); log.Warning("[NPC] no room to make {Name}; trying again shortly.", npc.Name); continue; }
            // An object that couldn't be set up stays out of use (deleted when the visit ends).
            if (!engine.HousingNpcSetup(slot, fresh, Spot(npc), npc.Yaw, names[i], npc.Look, npc.Level ?? 0))
            { createAfter = DateTime.UtcNow.AddSeconds(CreateBackoff); log.Warning("[NPC] couldn't set up {Name}.", npc.Name); continue; }
            Glamour?.Reidentify(NativeEngine.HousingNpcObjectIndex(slot));
            // Before it is drawn: the collection decides the skeleton its model loads, which its animations must fit.
            Collections?.Assign(NativeEngine.HousingNpcObjectIndex(slot), names[i], npc.Id);
            NameChanged?.Invoke();
            var rest = RestClip(npc.Emote);
            spawned[npc.Id] = new Spawned(npc, slot, names[i], Spot(npc), npc.Yaw)
                { Rest = rest, RestEmote = rest == 0 ? null : npc.Emote, TargetClip = rest };
            Register(spawned[npc.Id]);
        }
    }

    /// <summary>
    /// An NPC's mods on this PC changed (NpcModShare): it gets its new collection and is drawn again with it. One that
    /// isn't shown here picks them up when it is.
    /// </summary>
    public void Remod(Guid id)
    {
        Collections?.Forget(id);
        if (!spawned.TryGetValue(id, out var npc)) return;
        Collections?.Assign(NativeEngine.HousingNpcObjectIndex(npc.Slot), npc.GameName, id);
        engine.HousingNpcRestyle(npc.Slot, npc.Saved.Look); npc.Drawn = false; npc.Applied = 0; Redress(npc);
    }

    /// <summary>The object-table index this PC draws an NPC at, or null when it isn't shown.</summary>
    public int? ObjectIndexOf(Guid id) => spawned.TryGetValue(id, out var npc) ? NativeEngine.HousingNpcObjectIndex(npc.Slot) : null;

    // Puts an NPC's object away for reuse (its NPC was removed or sent away).
    private void Park(Spawned npc)
    {
        if (npc.Saved.Id == possessed) Release();
        if (npc.BodyShown) Body?.Clear(NativeEngine.HousingNpcObjectIndex(npc.Slot));
        Glamour?.Unlock(NativeEngine.HousingNpcObjectIndex(npc.Slot));
        engine.HousingPoseNpc(PoseNpcs.Key(npc.Saved.Id), null);
        engine.HousingNpcPark(npc.Slot);
        Glamour?.Reidentify(NativeEngine.HousingNpcObjectIndex(npc.Slot));
        spawned.Remove(npc.Saved.Id);
        parked.Add(npc.Slot);
        log.Information("[NPC] {Name} put away (slot {Slot}).", npc.Saved.Name, npc.Slot);
    }

    private static void Redress(Spawned npc) { npc.BodyDone = false; npc.Dressed = false; npc.DressTried = 0; npc.DressAt = DateTime.UtcNow.AddSeconds(DressDelay); }

    // The saved Glamourer state dresses this PC's copy; without one, any state Glamourer still keeps for its name is
    // reverted. Without Glamourer it keeps its plain saved look. Retried a few times (Glamourer may still be loading).
    private void Dress(Spawned npc, DateTime now)
    {
        if (Glamour == null) { npc.Dressed = true; return; }
        var index = NativeEngine.HousingNpcObjectIndex(npc.Slot);
        Glamour.Reidentify(index);
        if (npc.Saved.Look.Design is not { } design)
        {
            Glamour.Revert(index);
            npc.Glamoured = false; npc.Dressed = true;
            return;
        }
        // A look that pins advanced colours (every look Glamourer exports does, changed or not) is cleaned first: they
        // override the ordinary Skin, Hair, Eye and Lip colours, which then couldn't be changed in Glamourer.
        if (!settled.Contains(design) && GlamourerStates.PinsParameters(design) && GlamourerStates.WithoutParameters(design) is { } plain)
        {
            Glamour.Revert(index);
            if (Glamour.Apply(index, plain, quiet: true))
            {
                npc.Settling = design; npc.SettleAt = now.AddSeconds(SettleDelay);
                npc.Glamoured = npc.Dressed = true;
                return;
            }
        }
        // Advanced colours an earlier look set stay in Glamourer's state through an apply that leaves them out: revert first.
        var pins = GlamourerStates.PinsParameters(design);
        if (npc.Pinned && !pins) Glamour.Revert(index);
        if (Glamour.Apply(index, design, quiet: npc.DressTried + 1 < DressTries))
        {
            npc.Glamoured = npc.Dressed = true; npc.Pinned = pins;
            log.Information("[NPC] {Name} ({GameName}) wears its Glamourer look.", npc.Saved.Name, npc.GameName);
            return;
        }
        if (++npc.DressTried >= DressTries) { npc.Dressed = true; log.Warning("[NPC] {Name} ({GameName}): Glamourer didn't take its look; it shows its plain look.", npc.Saved.Name, npc.GameName); }
        else npc.DressAt = now.AddSeconds(DressRetry);
    }

    // Reads back the colours the game drew for the look's ordinary settings, then applies the look keeping only the
    // advanced colours that differ (as Glamourer's own copy does), and saves it when this PC may decorate.
    private void Settle(Spawned npc)
    {
        var wanted = npc.Settling!; npc.Settling = null;
        if (Glamour == null) return;
        var index = NativeEngine.HousingNpcObjectIndex(npc.Slot);
        Glamour.Reidentify(index);
        var cleaned = Glamour.State(index) is { } shown ? GlamourerStates.KeepChangedParameters(wanted, shown) ?? wanted : wanted;
        settled.Add(cleaned);
        Glamour.Apply(index, cleaned);
        npc.Pinned = GlamourerStates.PinsParameters(cleaned);
        // The ones kept differ from the ordinary colours: set on purpose, or left from an older look (Reset advanced colours).
        var kept = GlamourerStates.Pinned(cleaned);
        log.Information("[NPC] {Name} ({GameName}): advanced colours nobody changed let go; kept: {Kept}.", npc.Saved.Name, npc.GameName, kept.Length == 0 ? "none" : string.Join(", ", kept));
        if (cleaned == wanted || npc.Saved.Look.Design != wanted || !housing.CanFurnish || Visitors?.IsVisitor(npc.Saved.Id) == true) return;
        try { housing.SetNpcLook(npc.Saved.Id, npc.Saved.Look with { Design = cleaned }); }
        catch (InvalidOperationException e) { log.Information("[NPC] couldn't save {Name}'s tidied look: {Message}", npc.Saved.Name, e.Message); }
    }

    /// <summary>
    /// Dresses an NPC in one of the player's Glamourer designs: the state as dressed, with the plain look read back for
    /// PCs without Glamourer, or an explanation of why not.
    /// </summary>
    public (NpcLook? Look, string? Problem) DressIn(Guid id, Guid design)
    {
        if (Glamour == null) return (null, "Glamourer isn't available.");
        if (!spawned.TryGetValue(id, out var npc) || !npc.Drawn) return (null, "That NPC isn't here right now. Bring it back first.");
        var index = NativeEngine.HousingNpcObjectIndex(npc.Slot);
        Glamour.Reidentify(index);
        if (Glamour.Dress(index, design) is not { } state) return (null, "Glamourer couldn't dress that NPC. Check Glamourer is enabled and up to date.");
        npc.Glamoured = npc.Dressed = true;
        var plain = engine.HousingNpcLook(npc.Slot) ?? npc.Saved.Look;
        return (plain with { Design = state }, null);
    }

    /// <summary>
    /// An NPC's look as Glamourer has it now (after the player edited it in Glamourer's window), with the plain look
    /// read back for PCs without Glamourer, or an explanation of why not.
    /// </summary>
    public (NpcLook? Look, string? Problem) ReadGlamour(Guid id)
    {
        if (Glamour == null) return (null, "Glamourer isn't available.");
        if (!spawned.TryGetValue(id, out var npc) || !npc.Drawn) return (null, "That NPC isn't here right now. Bring it back first.");
        Glamour.Reidentify(NativeEngine.HousingNpcObjectIndex(npc.Slot));
        if (Glamour.State(NativeEngine.HousingNpcObjectIndex(npc.Slot)) is not { } state)
            return (null, $"Glamourer has no look for {npc.GameName}. Edit it in Glamourer's Actors tab first.");
        npc.Glamoured = npc.Dressed = true;
        var plain = engine.HousingNpcLook(npc.Slot) ?? npc.Saved.Look;
        return (plain with { Design = state }, null);
    }

    private ushort RestClip(ushort? emote) => emote is { } id && EmoteTimeline?.Invoke(id) is { Loop: true } found ? found.Timeline : (ushort)0;
    // A clip an NPC settles into once nobody moves it: movement and jumps end in its resting emote (or standing).
    private static ushort Settled(Spawned npc, ushort clip) => clip is RunClip or WalkClip or JumpClip or FallClip ? npc.Rest : clip;
    private static bool SameSpot(HomeNpc a, HomeNpc b) => a.Position.SequenceEqual(b.Position) && a.Yaw == b.Yaw;
    private static Vector3 Spot(HomeNpc npc) => new(npc.Position[0], npc.Position[1], npc.Position[2]);

    // Moves a copy and holds its clip, starting the clip only when it differs from the one Haven last started.
    private void Move(Spawned npc, Vector3 position, float yaw, ushort clip)
    {
        // The take-off is a short one-shot: it plays once over the fall loop, which is held as the base from the start
        // and simply carries on at the top of the jump. (0.51.1 held the take-off itself as the base for the whole
        // rise; the game kept restarting or freezing it, which looked like a twitch.)
        var hold = clip == JumpClip ? FallClip : clip;
        var start = npc.Applied != clip && !(clip == FallClip && npc.Applied == JumpClip);
        engine.HousingMoveNpc(npc.Slot, position, yaw, hold, start ? clip : (ushort)0);
        npc.Applied = clip;
        Trace(npc, clip);
    }

    // The first jumps of a visit are traced to the log, frame by frame, so a jump that looks wrong in game can be
    // read afterwards ([NPC-JUMP]).
    private int jumpsTraced, traceFrames;
    private void Trace(Spawned npc, ushort clip)
    {
        if (traceFrames == 0 && clip == JumpClip && jumpsTraced < 3) { jumpsTraced++; traceFrames = 90; }
        if (traceFrames == 0) return;
        traceFrames--;
        log.Information("[NPC-JUMP] clip {Clip}: {State}", clip, engine.HousingNpcTimelines(npc.Slot));
    }

    private void Handle(GameMessage message)
    {
        // Pose balls are the home's business (they may arrive before this PC shows the NPC).
        if (message.Kind == "pose") { housing.ReceiveNpcPose(message.Sender, message.Match, message.Values); return; }
        if (message.Sender == engine.HousingCharacterId || !spawned.TryGetValue(message.Match, out var npc)) return;
        // Only the owner and decorators possess the home's NPCs (checked on every PC): a guest's claim or state is
        // ignored, so nobody else can take one or keep it by repeating its position. A visitor: whom its bringer allows.
        if (!MayControl(npc.Saved.Id, message.Sender)) return;
        switch (message.Kind)
        {
            case "claim":
                // Both claimed at once: the lower character id keeps it.
                if (possessed == npc.Saved.Id && engine.HousingCharacterId < message.Sender) { SendState(npc, force: true); return; }
                // Held on another PC: it stays theirs, unless two claimed at once and this one has the lower id (as on
                // the claimants' own PCs). Possess refuses a held NPC, so only a modified client claims over a holder.
                if (!FreeFor(npc, message.Sender) && !(message.Sender < npc.Holder && (DateTime.UtcNow - npc.ClaimedAt).TotalSeconds < 2)) return;
                if (possessed == npc.Saved.Id) Release();
                npc.Holder = message.Sender; npc.HeardAt = npc.ClaimedAt = DateTime.UtcNow; npc.LeftHere = false;
                break;
            // Only the player holding the NPC acts or speaks as it (anyone else's would be someone putting words in its mouth).
            // Timelines from others name a row of the game's sheet, as players' do (WireSanity): an unknown one can crash.
            case "act" when npc.Holder == message.Sender && message.Values is [var timeline and > 0 and <= ushort.MaxValue]
                            && HavenNative.Wire.WireSanity.Timeline((ushort)timeline) != 0:
                engine.HousingNpcPlay(npc.Slot, (ushort)timeline);
                break;
            case "say" when npc.Holder == message.Sender && message.Text is { Length: > 0 } text:
                PrintSay?.Invoke(npc.Saved.Name, text);
                break;
            case "release":
                // The releasing PC's last state carries the emote it was left in.
                if (npc.Holder == message.Sender)
                {
                    npc.Holder = 0; npc.HolderSpeaks = false;
                    if (npc.TargetClip != LandClip) { npc.TargetClip = Settled(npc, npc.TargetClip); npc.Rest = npc.TargetClip; }
                }
                break;
            // Values: x, y, z and yaw (×1000), clip, held, and (from 0.52.0) the emote it rests in (0: none).
            // Only from its holder, or anyone while nobody holds it: someone else's state would take the NPC from its
            // holder, and with it the right to act and speak as it.
            case "state" when message.Values is { Length: >= 6 } v && possessed != npc.Saved.Id && FreeFor(npc, message.Sender):
                var target = new Vector3(v[0], v[1], v[2]) / 1000f;
                if (target.Length() > 10000) return;
                npc.Target = target; npc.TargetYaw = v[3] / 1000f; npc.TargetClip = HavenNative.Wire.WireSanity.Timeline((ushort)Math.Clamp(v[4], 0, ushort.MaxValue));
                // Held 1: possessed; 2: possessed and spoken as (the holder's typing bubble shows over the NPC).
                if (v[5] != 0) { npc.Holder = message.Sender; npc.HolderSpeaks = v[5] == 2; npc.HeardAt = DateTime.UtcNow; npc.LeftHere = false; }
                else
                {
                    if (npc.Holder == message.Sender) { npc.Holder = 0; npc.HolderSpeaks = false; }
                    // Let go (or repeated by whoever let it go): it rests as it was left.
                    if (npc.Holder == 0)
                    {
                        npc.TargetClip = npc.Rest = Settled(npc, npc.TargetClip);
                        npc.RestEmote = npc.Rest == 0 ? null : v.Length >= 7 && v[6] is > 0 and <= ushort.MaxValue ? (ushort)v[6] : npc.RestEmote;
                        npc.LeftHere = false;
                    }
                }
                break;
        }
    }

    // Nobody else holds it: no holder, the sender is the holder, or the holder went quiet.
    private static bool FreeFor(Spawned npc, ulong sender) =>
        npc.Holder == 0 || npc.Holder == sender || (DateTime.UtcNow - npc.HeardAt).TotalSeconds > HoldTimeout;

    private void Send(string kind, Guid id, int[]? values = null)
    {
        if (!housing.IsSharedVisit) return;
        _ = engine.HousingRelay.SendMiniGame(new GameMessage(engine.HousingCharacterId, GameMessage.Everyone, "npc", kind, id, values));
    }

    // While it changes, at most every StateInterval; while still, only the RepeatInterval reminder.
    private void SendState(Spawned npc, bool force = false, ushort? clip = null)
    {
        var now = DateTime.UtcNow;
        var held = possessed == npc.Saved.Id ? SpeakAsNpc ? 2 : 1 : 0;
        // On a ball, the others hold their own copy there; the state says where it stands when it leaves.
        var state = (Position: npc.StandAt ?? npc.Position, Yaw: npc.StandAt != null ? npc.StandYaw : npc.Yaw, Clip: clip ?? npc.Clip, Held: held);
        var changed = Vector3.DistanceSquared(state.Position, sentState.Position) > 1e-6f || state.Yaw != sentState.Yaw || state.Clip != sentState.Clip
            || state.Held != sentState.Held;
        if (!force && clip != LandClip && (changed ? (now - lastSent).TotalSeconds < StateInterval : (now - lastRepeat).TotalSeconds < RepeatInterval)) return;
        lastSent = lastRepeat = now;
        sentState = state;
        var emote = possessed == npc.Saved.Id ? heldEmoteId : npc.RestEmote;
        Send("state", npc.Saved.Id, [(int)(state.Position.X * 1000), (int)(state.Position.Y * 1000), (int)(state.Position.Z * 1000),
            (int)(state.Yaw * 1000), state.Clip, held, emote ?? 0]);
    }

    // An NPC let go here away from its saved spot: where it is and how it rests, every few seconds (late joiners), until
    // someone takes it, it is saved there, or it is back at its saved spot.
    private void RepeatLeft(Spawned npc, DateTime now)
    {
        npc.RepeatAt = now.AddSeconds(LeftRepeat);
        var (at, yaw) = npc.StandAt is { } stand ? (stand, npc.StandYaw) : (npc.Position, npc.Yaw);
        if (Vector3.DistanceSquared(at, Spot(npc.Saved)) < .0025f && npc.RestEmote == npc.Saved.Emote) { npc.LeftHere = false; return; }
        Send("state", npc.Saved.Id, [(int)(at.X * 1000), (int)(at.Y * 1000), (int)(at.Z * 1000), (int)(yaw * 1000), npc.Rest, 0, npc.RestEmote ?? 0]);
    }

    // The possessed NPC: WASD relative to the camera, Space jumps, rays for the floor and walls.
    private void Drive(Spawned npc, float seconds)
    {
        // Typing anywhere (the game's chat box, Chat 2 or any ImGui field) never walks the NPC.
        var typing = FurnishingEditor.GameTextInput() || ImGui.GetIO().WantTextInput;
        var (forward, strafe, walk, jump) = WorldEditorKeys.Focused && !typing ? WorldEditorKeys.Walking : (0, 0, false, false);
        var position = npc.Position; var yaw = npc.Yaw;
        var ahead = engine.HousingCameraForward() ?? new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        var right = new Vector3(-ahead.Z, 0, ahead.X);
        var move = ahead * forward + right * strafe;
        // Space jumps from the ground (held, it doesn't repeat until pressed again).
        if (jump && !jumpHeld && !airborne) { airborne = true; rise = JumpSpeed; }
        jumpHeld = jump;
        var moving = move.LengthSquared() > 1e-4f;
        if (moving)
        {
            move = Vector3.Normalize(move);
            yaw = TurnTowards(yaw, MathF.Atan2(move.X, move.Z), TurnSpeed * seconds);
            var step = move * (walk ? WalkSpeed : RunSpeed) * seconds;
            // Blocked straight on, slide along whichever axis is free.
            foreach (var attempt in new[] { step, new Vector3(step.X, 0, 0), new Vector3(0, 0, step.Z) })
            {
                if (attempt.LengthSquared() < 1e-8f || Blocked(position, attempt)) continue;
                if (airborne) { position += attempt; break; }
                if (NativeEngine.HousingSurfaceBelow(position + attempt + new Vector3(0, 1, 0), 3) is not { } ground) continue;
                // Off a ledge: fall rather than snap down.
                if (ground.Y < position.Y - LedgeDrop) { position += attempt; airborne = true; rise = 0; }
                else position = ground;
                break;
            }
        }
        if (moving || airborne) heldEmote = 0;
        ushort clip = moving ? (walk ? WalkClip : RunClip) : heldEmote;
        var landed = false;
        if (airborne)
        {
            rise -= (rise > 0 ? Gravity : FallGravity) * seconds;
            var next = position + new Vector3(0, rise * seconds, 0);
            var ground = NativeEngine.HousingSurfaceBelow(position + new Vector3(0, 1, 0), 4);
            if (rise <= 0 && ground is { } floor && next.Y <= floor.Y)
            {
                position = floor; airborne = false; rise = 0; landed = true;
            }
            else if (rise < -30 || ground == null && position.Y < fallFloor - 20) { airborne = false; rise = 0; position = lastGround; }
            else position = next;
            // The take-off held while rising (started once; see Move), the fall loop on the way down.
            if (airborne) clip = rise > 0 ? JumpClip : FallClip;
        }
        if (!airborne) { lastGround = position; fallFloor = position.Y; }
        npc.Position = npc.Target = position; npc.Yaw = npc.TargetYaw = yaw; npc.Clip = clip;
        // A landing plays once over the standing base (as for peers): the base is let go first.
        if (landed) { Move(npc, position, yaw, 0); engine.HousingNpcPlay(npc.Slot, LandClip); }
        Move(npc, position, yaw, clip);
        SendState(npc, clip: landed ? LandClip : null);
    }

    // Holds a copy on its ball: the ball's place and facing, the emote's loop as the base and its intro once when it
    // starts (or the player asks to start again). It remembers where it stood.
    private void OnBall(Spawned npc, (Vector3 Position, float Yaw, ushort Loop, ushort Intro, long Restart) ball)
    {
        if (npc.StandAt == null)
        {
            npc.StandAt = npc.Saved.Id == possessed && airborne ? lastGround : npc.Position; npc.StandYaw = npc.Yaw;
            if (npc.Saved.Id == possessed) { airborne = false; rise = 0; heldEmote = 0; heldEmoteId = null; }
            log.Information("[POSE] {Name} plays its ball (timeline {Loop}).", npc.Saved.Name, ball.Loop);
        }
        var start = npc.Applied != ball.Loop || npc.BallRestart != ball.Restart;
        npc.BallRestart = ball.Restart;
        npc.Position = ball.Position; npc.Yaw = ball.Yaw;
        engine.HousingMoveNpc(npc.Slot, ball.Position, ball.Yaw, ball.Loop, start ? ball.Intro != 0 ? ball.Intro : ball.Loop : (ushort)0);
        npc.Applied = ball.Loop;
    }

    // Off its ball (stood up, a partner left, the balls hidden): back where it stood, standing.
    private void LeaveBall(Spawned npc)
    {
        var stand = npc.StandAt!.Value;
        npc.StandAt = null;
        npc.Position = stand; npc.Yaw = npc.StandYaw;
        if (npc.Saved.Id == possessed || npc.Holder == 0 && !npc.LeftHere) { npc.Target = stand; npc.TargetYaw = npc.StandYaw; }
        if (npc.Saved.Id == possessed) { lastGround = stand; fallFloor = stand.Y; }
        npc.Clip = npc.TargetClip = npc.Rest = 0; npc.RestEmote = null;
        Move(npc, stand, npc.Yaw, 0);
        log.Information("[POSE] {Name} left its ball.", npc.Saved.Name);
    }

    // Something in the way at knee or chest height, within the step and a body's half-width.
    private static bool Blocked(Vector3 from, Vector3 step)
    {
        var length = step.Length(); var direction = step / length;
        foreach (var height in new[] { .45f, 1.1f })
            if (FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule.RaycastMaterialFilter(
                    from + new Vector3(0, height, 0), direction, out _, length + .3f))
                return true;
        return false;
    }

    private static float TurnTowards(float from, float to, float most)
    {
        var delta = Wrap(to - from);
        return Wrap(from + Math.Clamp(delta, -most, most));
    }
    private static float Wrap(float angle) => MathF.IEEERemainder(angle, MathF.Tau);

    /// <summary>
    /// A slim bar under the toolbar while possessing: the NPC's name, who /say speaks as (one click switches), and let
    /// go. The keys are shown underneath only for the first moments of each possession.
    /// </summary>
    public void Draw()
    {
        if (possessed == null || CinematicMode.Hiding || PossessedName is not { } name) return;
        switch (NpcBarUi.Draw(name, SpeakAsNpc, (DateTime.UtcNow - possessedAt).TotalSeconds < HintSeconds))
        {
            case NpcBarAction.SwitchSpeech: SpeakAsNpc = !SpeakAsNpc; break;
            case NpcBarAction.LetGo: Release(); break;
        }
    }
}
