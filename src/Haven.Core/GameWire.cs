using System.Text.Json;

namespace Haven.Core;

/// <summary>
/// Two players' minigame over the relay (frame <see cref="HousingWire.MiniGame"/>): one player's message to one other
/// in the same gathering, or (target 0) to everyone else there. The relay stamps the sender's character. Furnishing
/// one-shots ("machine": play, with the timeline's SubId, 0 for rest; Match is the furnishing) and an arcade player's
/// stance and swings ("arcade": pose, act, end, with an ActionTimeline) go to everyone. Triple Triad
/// (game "triad") sends: invite (the rules), accept (the guest's deck), decline, start (seed, who goes first, both
/// decks), move (move number, hand slot, cell), rematch, leave. Both PCs play the same game from the same seed and the
/// same moves, so nothing else needs to travel. NPCs (game "npc", Match = the NPC): claim, release, state (position
/// and facing ×1000, clip, held), act (a one-shot timeline), say (Text: a line spoken as the NPC) and pose (its
/// pose ball entry, PoseNpcs; from 0.53.0).
/// </summary>
public sealed record GameMessage(ulong Sender, ulong Target, string Game, string Kind, Guid Match, int[]? Values = null, string? Text = null)
{
    public const int Protocol = 3;
    public const int MaxBytes = 2048, MaxValues = 24, MaxText = 500;
    private static readonly HashSet<string> Games = ["triad", "machine", "arcade", "npc"];
    private static readonly HashSet<string> Kinds = ["invite", "accept", "decline", "start", "move", "rematch", "leave", "play", "pose", "act", "end", "claim", "release", "state", "say"];
    /// <summary>The target that means everyone else in the room.</summary>
    public const ulong Everyone = 0;

    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static GameMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > MaxBytes) throw new InvalidDataException("A minigame message has the wrong size.");
        GameMessage? message;
        try { message = JsonSerializer.Deserialize<GameMessage>(payload); }
        catch (JsonException e) { throw new InvalidDataException("A minigame message is not valid.", e); }
        if (message == null || !Games.Contains(message.Game) || !Kinds.Contains(message.Kind) || message.Match == Guid.Empty
            || message.Values is { Length: > MaxValues }
            || message.Text != null && (message.Kind != "say" || message.Text.Length is 0 or > MaxText || message.Text.Any(char.IsControl)))
            throw new InvalidDataException("A minigame message is not one Haven plays.");
        return message;
    }
}

/// <summary>A Triple Triad match between two players: the inviter's deck plays blue, the guest's red.</summary>
public sealed record TriadDeal(int Seed, TriadPlayer First, uint[] HostDeck, uint[] GuestDeck)
{
    public int[] Encode() => [Seed, (int)First, .. HostDeck.Select(c => (int)c), .. GuestDeck.Select(c => (int)c)];

    public static TriadDeal? Decode(int[]? values) => values is { Length: 12 } v && v[1] is 0 or 1 && v[2..].All(c => c > 0)
        ? new(v[0], (TriadPlayer)v[1], [.. v[2..7].Select(c => (uint)c)], [.. v[7..12].Select(c => (uint)c)]) : null;
}
