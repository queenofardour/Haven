using System.Text.Json;
using System.Text.RegularExpressions;

namespace Haven.Core;

/// <summary>
/// A notice from the relay's operator to everyone connected (relay → client, frame 0x80): an announcement, an update
/// ("Haven 0.45.0 is out"), or relay maintenance with a countdown. Kind is "info", "update", "maintenance" or "clear"
/// (withdraws the notice with that Id). EndsInMs, when set, is how long until the moment it counts down to (the
/// maintenance), measured when the relay sent it, so clocks needn't agree. Version, for an update, is the version it
/// announces: a client already on it shows nothing.
/// </summary>
public sealed record RelayNotice(string Id, string Kind, string Text, long EndsInMs = 0, string? Version = null);

public static class NoticeWire
{
    public const int Protocol = 1;
    public const int MaxText = 500;
    public const long MaxCountdownMs = 24L * 60 * 60 * 1000;
    public static readonly string[] Kinds = ["info", "update", "maintenance", "clear"];
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,40}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Whether a notice is well formed: a short id, a known kind, text within bounds (none for clear), a countdown within a day.</summary>
    public static bool Valid(RelayNotice? notice) => notice is { Id: not null, Kind: not null, Text: not null }
        && IdPattern.IsMatch(notice.Id) && Kinds.Contains(notice.Kind)
        && (notice.Kind == "clear" || notice.Text.Trim().Length is > 0 and <= MaxText) && notice.Text.All(c => c >= ' ' || c == '\n')
        && notice.EndsInMs is >= 0 and <= MaxCountdownMs
        && (notice.Version == null || notice.Kind == "update" && VersionPattern.IsMatch(notice.Version));

    public static byte[] Encode(RelayNotice notice) => Valid(notice) ? JsonSerializer.SerializeToUtf8Bytes(notice, Json) : throw new InvalidDataException("A relay notice is not valid.");

    public static RelayNotice Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 4096) throw new InvalidDataException("A relay notice is too large.");
        var notice = JsonSerializer.Deserialize<RelayNotice>(payload, Json);
        return Valid(notice) ? notice! : throw new InvalidDataException("A relay notice is not valid.");
    }

    /// <summary>Whether a client on this version should show the notice: an update notice only to clients before its version.</summary>
    public static bool Concerns(RelayNotice notice, Version client) =>
        notice.Kind != "update" || notice.Version == null || !Version.TryParse(notice.Version, out var announced) || client < announced;

    /// <summary>A countdown as the player reads it: "4:05", "1:02:30", or "now".</summary>
    public static string Countdown(TimeSpan left) => left <= TimeSpan.Zero ? "now"
        : left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{left.Minutes}:{left.Seconds:00}";
}
