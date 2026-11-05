using System.Text.Json.Serialization;

namespace Haven.Core;

// No plaintext invite codes, chat, or actor action/event history in durable recovery data.
public sealed record RecoveryMember(string Id, [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong ContentId, string Name, byte[] TokenHash, long ResumeUntil);
public sealed record RecoveryRoom(string Id, byte[] PasswordHash, string HostId, RecoveryMember[] Members,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong[] Banned, byte[]? House, byte[]? Zone, long ExpiresAt, [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong Owner = 0, bool EditingEnabled = false, [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong[]? Editors = null, long LayoutRevision = 0, string? PublishedHash = null);
public sealed record RecoveryCheckpoint(long Revision, string RoomId, bool Removed, RecoveryRoom? State);
public sealed record PeerConnection(string PeerId, bool Online, long ResumeUntil);

public static class RecoveryPolicy
{
    /// <summary>
    /// How long an open connection may stay silent before it is treated as dead. Pings go every 5 s and every part
    /// of an incoming frame counts, so 20 s is about three missed pongs; a slow link earns more, up to a minute.
    /// (Connecting and re-admission keep the longer SilenceTimeout: a restarting relay restores rooms first.)
    /// </summary>
    public static TimeSpan WatchdogTimeout(long roundTripMs) => TimeSpan.FromMilliseconds(Math.Clamp(20000 + Math.Clamp(roundTripMs, 0, 30000) * 4, 20000, 60000));
    /// <summary>A round-trip estimate that one slow pong cannot swing: an exponential average of the samples.</summary>
    public static long SmoothRoundTrip(long previous, long sample) => previous <= 0 ? Math.Max(0, sample) : (long)Math.Round(previous * 0.8 + Math.Max(0, sample) * 0.2);
    public static TimeSpan SilenceTimeout(long roundTripMs) => TimeSpan.FromMilliseconds(Math.Clamp(30000 + Math.Clamp(roundTripMs, 0, 30000) * 6, 30000, 120000));
    public static readonly TimeSpan Reservation = TimeSpan.FromMinutes(30);
    public static TimeSpan RetryDelay(int attempt, double jitter) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Clamp(attempt - 1, 0, 5))) * (.8 + .4 * Math.Clamp(jitter, 0, 1)));
}
