using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haven.Core;

/// <summary>
/// The owner's guest list: with InviteOnly, the relay admits only these
/// characters (and the owner) as new members. People already in the gathering resume as before.
/// </summary>
public sealed record GuestList(bool InviteOnly, ulong[] Invited)
{
    public const int Protocol = 1, MaxInvited = 64;
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static GuestList Decode(byte[] payload)
    {
        if (payload.Length > 4096) throw new InvalidDataException("Oversized guest list.");
        var list = JsonSerializer.Deserialize<GuestList>(payload) ?? throw new InvalidDataException("Empty guest list.");
        if (list.Invited is null || list.Invited.Length > MaxInvited || list.Invited.Any(id => id == 0 || PoseNpcs.IsNpc(id))) throw new InvalidDataException("Invalid guest list.");
        return list with { Invited = [.. list.Invited.Distinct()] };
    }
}

/// <summary>An invitation to a friend's gathering, as the Worker keeps it (infra/cloudflare/src/invites.ts).</summary>
public sealed record GatheringInvite(
    [property: JsonPropertyName("from")] string From, [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("home")] string Home, [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("gathering")] string Gathering, [property: JsonPropertyName("expires")] long Expires)
{
    public ulong Host => ulong.TryParse(From, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0;
    public bool Valid => Host != 0 && Code.Length is >= 8 and <= 128 && !Code.Any(char.IsControl) && Gathering.Length is >= 16 and <= 32 && Name.Length <= 40 && Home.Length <= 60;

    /// <summary>The Worker's answer to a poll: valid invitations only, at most a handful.</summary>
    public static GatheringInvite[] Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("invites", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return [.. list.EnumerateArray().Take(16).Select(e => { try { return e.Deserialize<GatheringInvite>(); } catch (JsonException) { return null; } })
            .OfType<GatheringInvite>().Where(i => i.Valid).Select(i => i with { Name = SafeText.Clean(i.Name, 40), Home = SafeText.Clean(i.Home, 60) })];
    }
}

/// <summary>
/// The host's side: which nearby friends to invite now. Automatic invitations go to friends as they arrive nearby;
/// chosen ones (Invite) go whether or not automatic invitations are on. Each is sent again before it expires, while
/// the friend is still nearby or was chosen. Withdrawn ones are never sent again unless chosen again.
/// </summary>
public sealed class InviteHostPlan
{
    public const long RefreshMs = 10 * 60_000, SendEveryMs = 6_000;
    public const int MaxPerSend = 16, MaxOut = 32;
    private readonly Dictionary<ulong, long> sent = [];
    private readonly HashSet<ulong> chosen = [], withdrawn = [];
    private long nextSend;
    public string Gathering { get; private set; } = NewGathering();
    public IReadOnlyCollection<ulong> Invited => sent.Keys;
    public bool IsInvited(ulong friend) => sent.ContainsKey(friend) || chosen.Contains(friend);
    /// <summary>Everyone invited, sent or only chosen so far: the relay's guest list (a chosen friend may use the code).</summary>
    public IEnumerable<ulong> Allowed => sent.Keys.Union(chosen);
    public static string NewGathering() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>A new gathering: nothing sent yet, under a new identity (invitees' declines were for the old one).</summary>
    public void Reset() { sent.Clear(); chosen.Clear(); withdrawn.Clear(); nextSend = 0; Gathering = NewGathering(); }
    public void Choose(ulong friend) { if (friend == 0) return; chosen.Add(friend); withdrawn.Remove(friend); nextSend = 0; }
    /// <summary>Takes an invitation back; returns whether one was out (for the Worker to withdraw).</summary>
    public bool Withdraw(ulong friend)
    {
        chosen.Remove(friend); withdrawn.Add(friend);
        return sent.Remove(friend);
    }
    /// <summary>Automatic invitations turned off: those not chosen are taken back (returned, for the Worker).</summary>
    public ulong[] WithdrawAutomatic()
    {
        var automatic = sent.Keys.Where(f => !chosen.Contains(f)).ToArray();
        foreach (var friend in automatic) { sent.Remove(friend); withdrawn.Add(friend); }
        return automatic;
    }

    /// <summary>Who to invite now (at most MaxPerSend; none until SendEveryMs after the last send).</summary>
    public ulong[] Due(IEnumerable<ulong> nearbyFriends, bool automatic, long now)
    {
        if (now < nextSend) return [];
        var nearby = nearbyFriends.ToHashSet();
        var candidates = chosen.Concat(automatic ? nearby.Where(f => !withdrawn.Contains(f)) : []).Distinct()
            .Where(f => !sent.TryGetValue(f, out var at) || now - at >= RefreshMs && (nearby.Contains(f) || chosen.Contains(f)));
        return [.. candidates.Where(f => sent.ContainsKey(f) || sent.Count < MaxOut).Take(MaxPerSend)];
    }
    public void Sent(IEnumerable<ulong> friends, long now) { foreach (var f in friends) sent[f] = now; nextSend = now + SendEveryMs; }
    /// <summary>A send failed: try again later (a minute, or what the Worker asked for).</summary>
    public void Failed(long now, long retryMs = 60_000) => nextSend = now + Math.Max(SendEveryMs, retryMs);
}

/// <summary>
/// The invitee's side: when to ask the Worker, and which invitation (if any) to show. Haven asks only while signed in,
/// outside any visit, with a friend nearby and nothing (a duty, combat, a cutscene) that keeps a visit from starting.
/// </summary>
public sealed class InviteInbox
{
    public const long PollMs = 20_000, FailMs = 60_000, BusyMs = 10 * 60_000;
    private long nextPoll;
    private GatheringInvite[] invites = [];
    private readonly HashSet<string> answered = [];

    public bool ShouldPoll(bool signedIn, bool inVisit, bool friendNearby, bool blocked, long now) => signedIn && !inVisit && friendNearby && !blocked && now >= nextPoll;
    public void Polling(long now) => nextPoll = now + PollMs;
    public void Polled(IEnumerable<GatheringInvite> got) => invites = [.. got];
    /// <summary>The Worker couldn't answer: back off (longer when it said it is busy for the day).</summary>
    public void Failed(long now, bool busy) => nextPoll = now + (busy ? BusyMs : FailMs);

    /// <summary>The invitation to ask about: unanswered, unexpired, and from a friend standing nearby (so joining can work).</summary>
    public GatheringInvite? Showing(IReadOnlySet<ulong> nearbyFriends, long unixNow) =>
        invites.FirstOrDefault(i => i.Expires > unixNow && nearbyFriends.Contains(i.Host) && !answered.Contains(Key(i)));
    /// <summary>Joined or declined: this gathering isn't asked about again (a new gathering from the same host is).</summary>
    public void Answer(GatheringInvite invite) => answered.Add(Key(invite));
    private static string Key(GatheringInvite invite) => invite.From + "/" + invite.Gathering;
}
