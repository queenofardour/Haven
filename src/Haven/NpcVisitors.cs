using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Plugin.Services;
using Haven.Core;
using HavenNative;

namespace Haven;

/// <summary>
/// The player's saved NPCs (NpcRoster) and visiting NPCs. A guest brings
/// one of theirs into someone else's gathering: its definition goes to everyone in pieces (VisitingNpcs), the home's
/// owner lets it in (Welcome) or turns it away (Away), and every PC then draws it as one of the home's NPCs would be
/// drawn (NpcPossession merges Shown with the home's own). It is never part of the home. It goes when its bringer
/// takes it back, leaves, or the owner sends it away. Late joiners get the definition from the bringer and the
/// welcome from the owner, 20 s after they appear (as the owner's NPC mod state does).
/// </summary>
internal sealed class NpcVisitors(HousingController housing, NativeEngine engine, NpcRoster roster, IPluginLog log)
{
    private sealed class Visitor(VisitingNpc npc, ulong bringer, int version)
    {
        public VisitingNpc Npc = npc;
        public readonly ulong Bringer = bringer;
        public int Version = version;
        public bool Welcomed;
        public DateTime Since = DateTime.UtcNow, AskedAt, MissingSince;
    }

    private readonly Dictionary<Guid, Visitor> visitors = [];
    private readonly HashSet<Guid> welcomes = [];       // welcomes heard before the definition was whole
    private readonly HashSet<Guid> turnedAway = [];     // on the owner's PC: sent away this visit (asked again, still away)
    private readonly VisitingNpcs.Assembler assembler = new();
    private readonly ConcurrentQueue<GameMessage> inbox = new();
    private readonly Queue<GameMessage> outbox = new();
    private readonly Dictionary<ulong, DateTime> seenAt = [];
    private readonly HashSet<ulong> statedTo = [];
    private readonly Dictionary<(ulong, Guid), DateTime> answeredAt = [];
    private readonly Dictionary<Guid, DateTime> askedAt = [];
    private Guid? visitHome;
    private DateTime nextSend;
    // A few messages a second: the relay lets each player send 12 a second in all, and a possessed NPC's states use most.
    private const int MaxTurnedAway = 64;
    private const double SendGap = .25, StateDelay = 20, AskAfter = 5, AskGap = 10, AnswerGap = 10, BringerGone = 10, NoAnswer = 15;

    /// <summary>What the last bring, refusal or send-away said, for the panel.</summary>
    public string Note { get; private set; } = "";

    private ulong Me => engine.HousingCharacterId;
    private ulong HomeOwner => engine.HousingRelay.Editing?.Owner ?? 0;
    private string Key => HomeOwners.Key(Me);
    private bool InVisit => housing.IsSharedVisit && housing.IsReady && !housing.EndingVisit && housing.ActiveHouse != null;

    public IReadOnlyList<RosterNpc> Roster => roster.Of(Key);

    /// <summary>The visitors every PC draws now (welcomed), as the NPC code draws the home's, in a fixed order.</summary>
    public HomeNpc[] Shown => [.. visitors.Values.Where(v => v.Welcomed).OrderBy(v => v.Npc.Id).Select(v => v.Npc.AsHomeNpc())];
    public bool IsVisitor(Guid id) => visitors.TryGetValue(id, out var v) && v.Welcomed;
    public bool Mine(Guid id) => visitors.TryGetValue(id, out var v) && v.Bringer == Me;
    public VisitRights? RightsOf(Guid id) => visitors.TryGetValue(id, out var v) ? v.Npc.Rights : null;

    /// <summary>The visitors here for the panel: welcomed ones and this player's own still waiting.</summary>
    public IEnumerable<(VisitingNpc Npc, ulong Bringer, string BroughtBy, bool Welcomed)> Listed =>
        visitors.Values.Where(v => v.Welcomed || v.Bringer == Me).OrderBy(v => v.Since).Select(v => (v.Npc, v.Bringer, v.Bringer == Me ? "you" : NameOf(v.Bringer), v.Welcomed));

    /// <summary>Whether a character may possess a visitor here, as its bringer chose.</summary>
    public bool MayPossess(Guid id, ulong character) => visitors.TryGetValue(id, out var v) && v.Welcomed
        && VisitingNpcs.MayPossess(v.Npc.Rights, character, v.Bringer, HomeOwner, character == Me || IsMember(character));

    /// <summary>Why this player can't bring a visitor here now, or null when they can.</summary>
    public string? CannotBring => !InVisit ? "Join a gathering in someone else's Haven to bring an NPC along."
        : housing.OwnsActiveHouse ? "This is your home: add the NPC to it instead."
        : HomeOwner == 0 ? "The home's owner isn't here to let NPCs in."
        : visitors.Values.Count(v => v.Bringer == Me) >= VisitingNpcs.PerGuest ? $"You can bring up to {VisitingNpcs.PerGuest} NPCs at once."
        : null;

    /// <summary>Whether the player turns visiting NPCs away from their homes.</summary>
    public bool Refusing
    {
        get => roster.Refuses(Key);
        set
        {
            roster.SetRefusing(Key, value);
            if (!value || !InVisit || !housing.OwnsActiveHouse) return;
            foreach (var visitor in visitors.Values.Where(v => v.Bringer != Me).ToList()) SendAway(visitor.Npc.Id);
        }
    }

    // The roster -------------------------------------------------------------

    /// <summary>Set by the plugin: one of this player's visitors was let in (its mod state goes to everyone).</summary>
    public Action<Guid>? Welcomed { get; set; }

    /// <summary>Saves a home's NPC to Your NPCs, with a copy of the mods this PC keeps for it (kept under the new entry's id).</summary>
    public void SaveToRoster(HomeNpc npc)
    {
        var saved = roster.Add(Key, npc);
        Note = $"{saved.Name} is saved in Your NPCs.";
    }
    public void Rename(Guid id, string name) => roster.Rename(Key, id, name);
    /// <summary>Removes a saved NPC and the mods this PC keeps under its id (homes it was added to keep their own copies).</summary>
    public void Remove(Guid id)
    {
        if (visitors.TryGetValue(id, out var visiting) && visiting.Bringer == Me) throw new InvalidOperationException("It's visiting this gathering now. Take it back first.");
        roster.Remove(Key, id);
    }

    /// <summary>Who brought a visitor here (its mods are theirs to share), or null when it isn't one.</summary>
    public ulong? BringerOf(Guid id) => visitors.TryGetValue(id, out var v) && (v.Welcomed || v.Bringer == Me) ? v.Bringer : null;
    /// <summary>A visitor drawn here now, as the NPC code draws it.</summary>
    public HomeNpc? Find(Guid id) => visitors.TryGetValue(id, out var v) && v.Welcomed ? v.Npc.AsHomeNpc() : null;

    // Visiting ---------------------------------------------------------------

    /// <summary>Brings one of the player's NPCs into this gathering, standing where given: the owner is asked to let it in.</summary>
    public void Bring(Guid rosterId, Vector3 position, float yaw)
    {
        if (CannotBring is { } why) throw new InvalidOperationException(why);
        if (roster.Find(Key, rosterId) is not { } saved) throw new InvalidOperationException("That NPC isn't in Your NPCs any more.");
        if (visitors.ContainsKey(rosterId)) throw new InvalidOperationException($"{saved.Name} is here already.");
        if (housing.Npcs.Any(n => n.Id == rosterId)) throw new InvalidOperationException($"This home has {saved.Name} already.");
        var npc = VisitingNpc.From(saved, [position.X, position.Y, position.Z], SafeAngles.Wrap(yaw), VisitRights.OnlyMe);
        var visitor = visitors[npc.Id] = new Visitor(npc, Me, NewVersion());
        SendDefinition(visitor, GameMessage.Everyone);
        Note = $"Asking the home's owner to let {npc.Name} in…";
        log.Information("[NPC-VISIT] bringing {Name} ({Id}).", npc.Name, npc.Id);
    }

    /// <summary>Its bringer takes a visitor back; the home's owner sends it away.</summary>
    public void SendAway(Guid id)
    {
        if (!visitors.TryGetValue(id, out var visitor)) return;
        if (visitor.Bringer == Me) Post(GameMessage.Everyone, id, VisitingNpcs.Gone);
        else if (InVisit && housing.OwnsActiveHouse) { turnedAway.Add(id); Post(GameMessage.Everyone, id, VisitingNpcs.Away); }
        else return;
        Drop(id);
        Note = $"{visitor.Npc.Name} left.";
    }

    /// <summary>Who may possess one of the player's visitors: sent to everyone as a new version of its definition.</summary>
    public void SetRights(Guid id, VisitRights rights)
    {
        if (!visitors.TryGetValue(id, out var visitor) || visitor.Bringer != Me || visitor.Npc.Rights == rights || !Enum.IsDefined(rights)) return;
        visitor.Npc = visitor.Npc with { Rights = rights };
        visitor.Version = NewVersion();
        SendDefinition(visitor, GameMessage.Everyone);
    }

    public void Receive(GameMessage message) { if (VisitingNpcs.IsVisit(message)) inbox.Enqueue(message); }

    /// <summary>Forgets every visitor (the visit ended): they belonged to that gathering.</summary>
    public void Clear()
    {
        if (visitors.Count > 0) log.Information("[NPC-VISIT] the gathering ended; {Count} visiting NPC(s) gone.", visitors.Count);
        visitors.Clear(); welcomes.Clear(); turnedAway.Clear(); assembler.Clear(); outbox.Clear(); inbox.Clear();
        seenAt.Clear(); statedTo.Clear(); answeredAt.Clear(); askedAt.Clear();
        visitHome = null;
    }

    public void Update()
    {
        if (!InVisit || visitHome != null && visitHome != housing.ActiveHouse!.Id)
        {
            if (visitHome != null || visitors.Count > 0) Clear();
            inbox.Clear();
            return;
        }
        visitHome = housing.ActiveHouse!.Id;
        var now = DateTime.UtcNow;
        while (inbox.TryDequeue(out var message)) Handle(message, now);
        // Visitors whose bringer left the gathering go with them.
        foreach (var visitor in visitors.Values.Where(v => v.Bringer != Me).ToList())
        {
            if (IsMember(visitor.Bringer)) { visitor.MissingSince = default; continue; }
            if (visitor.MissingSince == default) visitor.MissingSince = now;
            else if ((now - visitor.MissingSince).TotalSeconds > BringerGone) { log.Information("[NPC-VISIT] {Name} left with {Bringer}.", visitor.Npc.Name, visitor.Bringer); Drop(visitor.Npc.Id); }
        }
        // A home NPC now has a visitor's id (an edit arrived): the home's own wins.
        foreach (var clash in visitors.Keys.Where(id => housing.Npcs.Any(n => n.Id == id)).ToList()) Drop(clash);
        LateJoiners(now);
        AskAgain(now);
        if (outbox.Count > 0 && now >= nextSend) { nextSend = now.AddSeconds(SendGap); _ = engine.HousingRelay.SendMiniGame(outbox.Dequeue()); }
    }

    private void Handle(GameMessage message, DateTime now)
    {
        if (message.Sender == Me || message.Values is not [var mark, ..]) return;
        var id = message.Match;
        switch (mark)
        {
            case VisitingNpcs.Part:
                if (assembler.Add(message.Sender, id, message.Values, message.Text, now) is { } npc) Arrived(npc, message.Sender, message.Values[1]);
                break;
            case VisitingNpcs.Welcome when message.Sender == HomeOwner && HomeOwner != 0:
                if (visitors.TryGetValue(id, out var welcomed))
                {
                    if (!welcomed.Welcomed)
                    {
                        log.Information("[NPC-VISIT] {Name} was let in.", welcomed.Npc.Name);
                        welcomed.Welcomed = true;
                        if (welcomed.Bringer == Me) Welcomed?.Invoke(id);
                    }
                    if (welcomed.Bringer == Me && Note.StartsWith("Asking", StringComparison.Ordinal)) Note = $"{welcomed.Npc.Name} is here.";
                }
                else welcomes.Add(id);
                break;
            case VisitingNpcs.Away when message.Sender == HomeOwner && HomeOwner != 0:
                if (visitors.TryGetValue(id, out var refused) && refused.Bringer == Me)
                    Note = refused.Welcomed ? $"{NameOf(HomeOwner)} sent {refused.Npc.Name} away." : $"{NameOf(HomeOwner)}'s home didn't let {refused.Npc.Name} in.";
                welcomes.Remove(id);
                Drop(id);
                break;
            case VisitingNpcs.Gone when visitors.TryGetValue(id, out var gone) && gone.Bringer == message.Sender:
                Drop(id);
                break;
            case VisitingNpcs.Ask when visitors.TryGetValue(id, out var asked):
                // Answered at most every AnswerGap per asker and visitor.
                if (answeredAt.TryGetValue((message.Sender, id), out var last) && (now - last).TotalSeconds < AnswerGap) break;
                answeredAt[(message.Sender, id)] = now;
                if (asked.Bringer == Me) SendDefinition(asked, message.Sender);
                if (housing.OwnsActiveHouse && asked.Welcomed) Post(message.Sender, id, VisitingNpcs.Welcome);
                break;
            case VisitingNpcs.Ask when housing.OwnsActiveHouse && turnedAway.Contains(id):
                if (answeredAt.TryGetValue((message.Sender, id), out var lastAway) && (now - lastAway).TotalSeconds < AnswerGap) break;
                answeredAt[(message.Sender, id)] = now;
                Post(message.Sender, id, VisitingNpcs.Away);
                break;
        }
    }

    // A whole definition from its bringer: the owner's PC lets it in or turns it away; the others keep it, shown once welcomed.
    private void Arrived(VisitingNpc npc, ulong bringer, int version)
    {
        if (visitors.TryGetValue(npc.Id, out var known))
        {
            if (known.Bringer != bringer) return;
            known.Npc = npc; known.Version = version;
            return;
        }
        if (housing.Npcs.Any(n => n.Id == npc.Id)) return;
        if (housing.OwnsActiveHouse)
        {
            var refusal = turnedAway.Contains(npc.Id) ? "It was sent away."
                : VisitingNpcs.Refusal(npc, bringer, Me, visitors.Values.Where(v => v.Welcomed).ToDictionary(v => v.Npc.Id, v => v.Bringer), housing.Npcs.Select(n => n.Id), Refusing);
            if (refusal != null)
            {
                // Said once per id (an Ask is answered below), and not at all past a few dozen: a guest sending endless
                // ids would otherwise grow this list and fill the outbox with refusals.
                if (turnedAway.Count < MaxTurnedAway && turnedAway.Add(npc.Id)) Post(GameMessage.Everyone, npc.Id, VisitingNpcs.Away);
                Note = $"{NameOf(bringer)}'s {npc.Name} wasn't let in: {refusal}";
                log.Information("[NPC-VISIT] turned {Name} away: {Why}", npc.Name, refusal);
                return;
            }
            visitors[npc.Id] = new Visitor(npc, bringer, version) { Welcomed = true };
            Post(GameMessage.Everyone, npc.Id, VisitingNpcs.Welcome);
            Note = $"{NameOf(bringer)} brought {npc.Name}.";
            log.Information("[NPC-VISIT] let {Name} in, brought by {Bringer}.", npc.Name, NameOf(bringer));
            return;
        }
        // Someone else's visitor: only while its bringer is in the gathering, and never the owner's own.
        if (bringer == HomeOwner || !IsMember(bringer) || VisitingNpcs.Crowded(bringer, visitors.Values.Select(v => v.Bringer))) return;
        visitors[npc.Id] = new Visitor(npc, bringer, version) { Welcomed = welcomes.Remove(npc.Id) };
    }

    // Each member who appears gets, StateDelay later (a guest's Haven drops NPC messages until its home is ready), the
    // definitions of this player's visitors and, from the owner, the welcomes.
    private void LateJoiners(DateTime now)
    {
        foreach (var member in engine.HousingRelay.HousingMembers.Values)
        {
            if (member.ContentId == 0 || member.ContentId == Me || statedTo.Contains(member.ContentId)) continue;
            if (!seenAt.TryGetValue(member.ContentId, out var seen)) { seenAt[member.ContentId] = now; continue; }
            if ((now - seen).TotalSeconds < StateDelay) continue;
            statedTo.Add(member.ContentId);
            foreach (var visitor in visitors.Values)
            {
                if (visitor.Bringer == Me) SendDefinition(visitor, member.ContentId);
                if (housing.OwnsActiveHouse && visitor.Welcomed && visitor.Bringer != member.ContentId) Post(member.ContentId, visitor.Npc.Id, VisitingNpcs.Welcome);
            }
        }
        foreach (var gone in seenAt.Keys.Where(c => !IsMember(c)).ToList()) { seenAt.Remove(gone); statedTo.Remove(gone); }
    }

    // A definition still missing pieces asks its bringer again; a whole one nobody let in asks the owner.
    private void AskAgain(DateTime now)
    {
        foreach (var (sender, id, version) in assembler.Waiting(now, AskAfter))
            if (Due(id, now)) Post(sender, id, VisitingNpcs.Ask, version);
        foreach (var visitor in visitors.Values.Where(v => !v.Welcomed && (now - v.Since).TotalSeconds > AskAfter))
        {
            if (HomeOwner == 0 || !Due(visitor.Npc.Id, now)) continue;
            Post(HomeOwner, visitor.Npc.Id, VisitingNpcs.Ask, visitor.Version);
            if (visitor.Bringer == Me && (now - visitor.Since).TotalSeconds > NoAnswer)
                Note = $"The home's owner hasn't let {visitor.Npc.Name} in yet. Their Haven may need updating.";
        }
    }

    private bool Due(Guid id, DateTime now)
    {
        if (askedAt.TryGetValue(id, out var last) && (now - last).TotalSeconds < AskGap) return false;
        askedAt[id] = now;
        return true;
    }

    private void SendDefinition(Visitor visitor, ulong target)
    {
        // Pieces already waiting for this target are replaced (a newer version, or the same one again).
        var waiting = outbox.Where(m => !(m.Match == visitor.Npc.Id && m.Target == target && m.Values is [VisitingNpcs.Part, ..])).ToList();
        outbox.Clear();
        foreach (var message in waiting) outbox.Enqueue(message);
        foreach (var (values, text) in VisitingNpcs.Encode(visitor.Npc, visitor.Version))
            outbox.Enqueue(new GameMessage(Me, target, "npc", VisitingNpcs.Kind, visitor.Npc.Id, values, text));
    }

    private void Post(ulong target, Guid id, int mark, int value = 0) =>
        outbox.Enqueue(new GameMessage(Me, target, "npc", VisitingNpcs.Kind, id, value == 0 ? [mark] : [mark, value]));

    private void Drop(Guid id)
    {
        if (visitors.Remove(id, out var visitor)) log.Information("[NPC-VISIT] {Name} is gone.", visitor.Npc.Name);
        assembler.Forget(id);
        askedAt.Remove(id);
    }

    private bool IsMember(ulong character) => engine.HousingRelay.HousingMembers.Values.Any(m => m.ContentId == character);
    private string NameOf(ulong character) =>
        engine.HousingRelay.HousingMembers.Values.FirstOrDefault(m => m.ContentId == character)?.Name is { Length: > 0 } name ? name : "Someone";
    private static int NewVersion() => Random.Shared.Next(1, int.MaxValue);
}
