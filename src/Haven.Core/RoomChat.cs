using System.Text;
using System.Text.Json;

namespace Haven.Core;

/// <summary>
/// A room chat line. An emote's log line (channel 28) also names its Emote row and the Target's name, so each viewer
/// renders it from the game's own text, which says "you" to the target; Text is the line as a bystander reads it.
/// A /random roll (channel 74, the game's own "Random!" lines) carries its upper limit in Roll (RoomRolls), and the
/// relay, never the roller, picks the number (Rolled) and words Text.
/// </summary>
public sealed record RoomChatMessage(int Channel, string Text, string Name = "", int Emote = 0, string Target = "", int Roll = 0, int Rolled = -1);

public static class RoomChat
{
    public const int MaxTextBytes = 1000;
    public static bool IsChannel(int channel) => channel is 10 or 11 or 14 or 28 or 30 or RoomRolls.Channel;
    public static byte[] Encode(RoomChatMessage message)
    {
        Validate(message);
        return JsonSerializer.SerializeToUtf8Bytes(message);
    }
    public static RoomChatMessage Decode(byte[] data)
    {
        if (data.Length > 8192) throw new InvalidDataException("Chat frame too large.");
        var message = JsonSerializer.Deserialize<RoomChatMessage>(data) ?? throw new InvalidDataException("Empty chat frame.");
        Validate(message);
        return message;
    }
    private static void Validate(RoomChatMessage message)
    {
        if (!IsChannel(message.Channel) || string.IsNullOrWhiteSpace(message.Text)
            || Encoding.UTF8.GetByteCount(message.Text) > MaxTextBytes || message.Text.Any(char.IsControl)
            || message.Name == null || message.Name.Length > 80 || message.Name.Any(char.IsControl)
            || message.Emote is < 0 or > ushort.MaxValue || message.Emote != 0 && message.Channel != 28
            || message.Target == null || message.Target.Length > 80 || message.Target.Any(char.IsControl)
            || (message.Channel == RoomRolls.Channel
                ? !RoomRolls.ValidLimit(message.Roll) || message.Rolled != -1 && !RoomRolls.InRange(message.Roll, message.Rolled)
                : message.Roll != 0 || message.Rolled != -1))
            throw new InvalidDataException("Use plain room chat up to 1000 UTF-8 bytes.");
    }
}

/// <summary>
/// The chat a participant missed while reconnecting: the relay keeps a room's last lines in memory and replays them
/// on resume; the client skips lines it already showed (same channel, sender and text within SeenMs).
/// </summary>
public static class ChatReplay
{
    public const long WindowMs = 30_000, SeenMs = 90_000;
    public const int MaxLines = 40;
}

/// <summary>Chat bursts cannot fill the movement queue. Uses a monotonic clock in seconds.</summary>
public sealed class RoomChatRateLimit(double burst = 8, double perSecond = 2)
{
    private double tokens = burst, previous;
    public bool Allow(double now)
    {
        tokens = Math.Min(burst, tokens + Math.Max(0, now - previous) * perSecond);
        previous = Math.Max(previous, now);
        if (tokens < 1) return false;
        tokens--;
        return true;
    }
}

public static class RoomChatCommand
{
    public static int Channel(string command) => command.ToLowerInvariant() switch
    {
        "/s" or "/say" => 10,
        "/sh" or "/shout" => 11,
        "/y" or "/yell" => 30,
        "/em" or "/emote" => 28,
        _ => 0,
    };
    public static (string Command, string Text) Split(string value)
    {
        value = value.Trim();
        var index = value.IndexOfAny([' ', '\t', '\r', '\n']);
        return index < 0 ? (value.ToLowerInvariant(), "") : (value[..index].ToLowerInvariant(), value[(index + 1)..].Trim());
    }
}

/// <summary>
/// /random and /dice in a home: the game's own roll, made by the relay so nobody can choose their number. Roll is the
/// upper limit as typed (2-999: 1 to it), or DefaultLimit for a bare /random (0 to 999, as the game rolls).
/// </summary>
public static class RoomRolls
{
    public const int Channel = 74, DefaultLimit = 1000;
    public static bool ValidLimit(int limit) => limit is >= 2 and <= 999 or DefaultLimit;
    public static bool InRange(int limit, int rolled) => limit == DefaultLimit ? rolled is >= 0 and <= 999 : rolled >= 1 && rolled <= limit;

    /// <summary>The limit typed after /random or /dice (a chat channel name before it is ignored: rolls go to the room),
    /// DefaultLimit for none, or null when the number is out of range.</summary>
    public static int? Limit(string body)
    {
        var last = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (last == null || !last.All(char.IsAsciiDigit)) return DefaultLimit;
        return int.TryParse(last, out var limit) && limit is >= 2 and <= 999 ? limit : null;
    }

    public static int Roll(int limit) => limit == DefaultLimit
        ? System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1000)
        : System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, limit + 1);

    /// <summary>The game's wording: "Random! You roll a 512." or "Random! Name rolls an 8 (out of 20)."</summary>
    public static string Text(string? name, int rolled, int limit)
    {
        var who = name == null ? "You roll" : name + " rolls";
        var article = rolled is 8 or 11 or 18 or >= 80 and <= 89 or >= 800 and <= 899 ? "an" : "a";
        return $"Random! {who} {article} {rolled}" + (limit == DefaultLimit ? "." : $" (out of {limit}).");
    }
}
