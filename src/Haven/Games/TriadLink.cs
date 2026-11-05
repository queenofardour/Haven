using System.Collections.Concurrent;
using Haven.Core;

namespace Haven.Games;

/// <summary>An invitation to play Triple Triad from another player in the gathering.</summary>
internal sealed record TriadInvitation(ulong Sender, string Name, Guid Match, IReadOnlyList<TriadRule> Rules, long Expires);

/// <summary>
/// Triple Triad between two players in a gathering, over the relay (GameMessage): invitations and their answers, the
/// deal (the inviter deals: a seed, who goes first, both decks), each move, rematches and leaving. Messages arrive on
/// the relay's thread and are handled on the framework thread (Update).
/// </summary>
internal sealed class TriadLink
{
    private const long InvitationMilliseconds = 60_000;
    private readonly Func<GameMessage, Task> send;
    private readonly Func<ulong> me;
    private readonly Func<IReadOnlyList<(ulong Id, string Name)>> members;
    private readonly Func<IReadOnlyList<TriadCard>> cards;
    private readonly ConcurrentQueue<GameMessage> inbox = new();
    private readonly Random random = new();

    // Our invitation waiting for an answer: its match, the player asked, the rules and our deck.
    private (Guid Match, ulong Target, string Name, TriadRule[] Rules, uint[] Deck, long Expires)? asking;
    // An invitation we accepted, waiting for the inviter's deal.
    private (Guid Match, ulong Host, string Name, TriadRule[] Rules, long Expires)? accepted;
    // The match being played.
    private (Guid Match, ulong Other, TriadRemote Remote)? playing;
    private bool rematchAsked;
    private readonly Dictionary<ulong, long> declinedAt = [];

    public TriadLink(Func<GameMessage, Task> send, Func<ulong> me, Func<IReadOnlyList<(ulong Id, string Name)>> members, Func<IReadOnlyList<TriadCard>> cards)
    {
        this.send = send; this.me = me; this.members = members; this.cards = cards;
    }

    /// <summary>Whether this PC is busy with a game (an incoming invitation is then declined).</summary>
    public Func<bool>? Busy { get; set; }
    /// <summary>Invitations from others, newest last; the invite window answers them.</summary>
    public List<TriadInvitation> Invitations { get; } = [];
    /// <summary>A match starts: the other player's name, the rules and the link to them.</summary>
    public Action<string, IReadOnlyList<TriadRule>, TriadRemote>? Started { get; set; }
    /// <summary>What became of our invitation, for the picker: waiting, declined, gone.</summary>
    public string? AskingStatus { get; private set; }
    public bool Waiting => asking != null;

    public IReadOnlyList<(ulong Id, string Name)> Players() => [.. members().Where(m => m.Id != 0 && m.Id != me())];

    /// <summary>The relay delivered a message (any thread).</summary>
    public void Receive(GameMessage message) { if (message.Game == "triad") inbox.Enqueue(message); }

    public void Invite(ulong target, string name, IReadOnlyList<TriadRule> rules, IReadOnlyList<TriadCard> deck)
    {
        var match = Guid.NewGuid();
        asking = (match, target, name, [.. rules], [.. deck.Select(c => c.Id)], Environment.TickCount64 + InvitationMilliseconds);
        AskingStatus = $"Waiting for {name} to answer…";
        Send(target, "invite", match, [.. rules.Select(r => (int)r)]);
    }

    public void CancelInvite()
    {
        if (asking is { } a) Send(a.Target, "leave", a.Match);
        asking = null; AskingStatus = null;
    }

    public void Accept(TriadInvitation invitation, IReadOnlyList<TriadCard> deck)
    {
        Invitations.Remove(invitation);
        accepted = (invitation.Match, invitation.Sender, invitation.Name, [.. invitation.Rules], Environment.TickCount64 + 15_000);
        Send(invitation.Sender, "accept", invitation.Match, [.. deck.Select(c => (int)c.Id)]);
    }

    public void Decline(TriadInvitation invitation)
    {
        Invitations.Remove(invitation);
        Send(invitation.Sender, "decline", invitation.Match);
    }

    /// <summary>Handles what arrived and lets stale invitations go; call every framework update.</summary>
    public void Update(bool inGathering, bool matchOpen)
    {
        var now = Environment.TickCount64;
        if (!inGathering) { Clear(); return; }
        // The match window closed (finished, quit, the home left): the match is over here.
        if (playing != null && !matchOpen) playing = null;
        Invitations.RemoveAll(i => i.Expires < now);
        if (asking is { } a && a.Expires < now) { asking = null; AskingStatus = $"{a.Name} didn't answer."; }
        if (accepted is { } b && b.Expires < now) accepted = null;
        // Someone who leaves the gathering leaves the match.
        if (playing is { } p && Players().All(m => m.Id != p.Other)) p.Remote.Left = true;
        while (inbox.TryDequeue(out var message)) Handle(message);
    }

    private void Handle(GameMessage message)
    {
        var from = message.Sender;
        var name = Players().FirstOrDefault(m => m.Id == from).Name ?? "Someone";
        switch (message.Kind)
        {
            case "invite":
                var rules = (message.Values ?? []).Where(v => Enum.IsDefined(typeof(TriadRule), v)).Select(v => (TriadRule)v).Distinct().Take(4).ToArray();
                if (Busy?.Invoke() == true || playing != null || Invitations.Any(i => i.Sender == from))
                {
                    // Declined at most once a sender every 30 s: each reply spends this player's own relay allowance, which
                    // their moves and NPC messages share, so a stream of invitations mustn't make it send a stream back.
                    var now = Environment.TickCount64;
                    if (!declinedAt.TryGetValue(from, out var last) || now - last >= 30_000) { declinedAt[from] = now; Send(from, "decline", message.Match); }
                    return;
                }
                Invitations.Add(new(from, name, message.Match, rules, Environment.TickCount64 + InvitationMilliseconds));
                return;
            case "decline":
                if (asking is { } declined && declined.Match == message.Match && declined.Target == from) { asking = null; AskingStatus = $"{name} declined."; }
                return;
            case "accept":
                if (asking is not { } invite || invite.Match != message.Match || invite.Target != from) return;
                asking = null; AskingStatus = null;
                if (Deck(message.Values) is not { } guestDeck) { Send(from, "leave", message.Match); AskingStatus = $"{name}'s deck can't be played."; return; }
                var deal = new TriadDeal(random.Next(), random.Next(2) == 0 ? TriadPlayer.Blue : TriadPlayer.Red, invite.Deck, guestDeck);
                Send(from, "start", message.Match, deal.Encode());
                Begin(message.Match, from, invite.Name, invite.Rules, true, deal);
                return;
            case "start":
                if (accepted is not { } waiting || waiting.Match != message.Match || waiting.Host != from) return;
                accepted = null;
                if (TriadDeal.Decode(message.Values) is not { } dealt || Deck([.. dealt.HostDeck.Select(c => (int)c)]) == null || Deck([.. dealt.GuestDeck.Select(c => (int)c)]) == null) return;
                Begin(message.Match, from, waiting.Name, waiting.Rules, false, dealt);
                return;
            case "move":
                if (playing is { } p && p.Match == message.Match && p.Other == from && message.Values is { Length: 3 } v) p.Remote.Moves.Enqueue((v[0], v[1], v[2]));
                return;
            case "rematch":
                if (playing is not { } r || r.Match != message.Match || r.Other != from) return;
                if (r.Remote.Host && message.Values == null) { r.Remote.WantsRematch = true; if (rematchAsked) DealRematch(); }
                // The inviter's new deal: only once this player asked, and with the same two decks (a rematch reshuffles
                // and picks who starts; the decks were checked when the match began).
                else if (!r.Remote.Host && TriadDeal.Decode(message.Values) is { } next)
                {
                    if (rematchAsked && next.HostDeck.SequenceEqual(r.Remote.Deal.HostDeck) && next.GuestDeck.SequenceEqual(r.Remote.Deal.GuestDeck))
                    { rematchAsked = false; r.Remote.Rematch(next); }
                }
                else r.Remote.WantsRematch = true;
                return;
            case "leave":
                if (playing is { } gone && gone.Match == message.Match && gone.Other == from) gone.Remote.Left = true;
                if (asking is { } cancelled && cancelled.Match == message.Match && cancelled.Target == from) { asking = null; AskingStatus = $"{name} can't play now."; }
                Invitations.RemoveAll(i => i.Match == message.Match && i.Sender == from);
                if (accepted is { } withdrawn && withdrawn.Match == message.Match) accepted = null;
                return;
        }
    }

    private void Begin(Guid match, ulong other, string name, IReadOnlyList<TriadRule> rules, bool host, TriadDeal deal)
    {
        rematchAsked = false;
        TriadRemote? remote = null;
        remote = new TriadRemote(host, deal,
            (number, hand, cell) => Send(other, "move", match, [number, hand, cell]),
            () => AskRematch(match),
            () => { if (playing is { } p && p.Remote == remote) playing = null; Send(other, "leave", match); });
        playing = (match, other, remote);
        Started?.Invoke(name, rules, remote);
    }

    // We ask for a rematch: the guest asks the inviter, who deals once both have asked.
    private void AskRematch(Guid match)
    {
        if (playing is not { } p || p.Match != match) return;
        rematchAsked = true;
        if (!p.Remote.Host) { Send(p.Other, "rematch", match); return; }
        if (p.Remote.WantsRematch) DealRematch();
        else Send(p.Other, "rematch", match);
    }

    private void DealRematch()
    {
        if (playing is not { } p) return;
        rematchAsked = false;
        var previous = p.Remote.Deal;
        var next = previous with { Seed = random.Next(), First = random.Next(2) == 0 ? TriadPlayer.Blue : TriadPlayer.Red };
        Send(p.Other, "rematch", p.Match, next.Encode());
        p.Remote.Rematch(next);
    }

    // Five cards the game has, allowed together by the deck rules.
    private uint[]? Deck(int[]? values)
    {
        if (values is not { Length: 5 }) return null;
        var byId = cards().ToDictionary(c => c.Id);
        var deck = new List<TriadCard>();
        foreach (var id in values)
        {
            if (id <= 0 || !byId.TryGetValue((uint)id, out var card) || !TriadGame.DeckAllows(deck, card)) return null;
            deck.Add(card);
        }
        return [.. deck.Select(c => c.Id)];
    }

    private void Send(ulong target, string kind, Guid match, int[]? values = null) =>
        _ = send(new GameMessage(me(), target, "triad", kind, match, values));

    private void Clear()
    {
        if (playing is { } p) p.Remote.Left = true;
        asking = null; accepted = null; playing = null; Invitations.Clear(); AskingStatus = null;
        while (inbox.TryDequeue(out _)) { }
    }
}
