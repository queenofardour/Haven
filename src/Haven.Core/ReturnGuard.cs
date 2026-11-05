using System.Numerics;
using System.Text.Json;

namespace Haven.Core;

/// <summary>
/// The server-known position a sandbox visit must return to before outbound traffic resumes. Persisted on entry so a
/// plugin instance loaded immediately afterwards in the same game process (an update/reload mid-visit) can re-arm the
/// firewall and finish the return. A different process means the game restarted, and an old unload means the player
/// may have legitimately moved since: in both cases the server's own position is authoritative and nothing is done.
/// </summary>
public sealed record ReturnGuard(uint Zone, float X, float Y, float Z, float Rotation, int ProcessId, long ProcessStartTicks, DateTimeOffset SavedAt,
    DateTimeOffset? UnloadedAt = null)
{
    public const string FileName = "return-guard.json";
    // A plugin update reloads within about a second; anything slower is not a stranded hand-over.
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromSeconds(60);
    // Plausible world coordinates only; a corrupt record must never drive SetPosition.
    private const float MaxCoordinate = 10_000;

    public Vector3 Position => new(X, Y, Z);

    public bool SameProcess(int processId, long processStartTicks) => ProcessId == processId && ProcessStartTicks == processStartTicks;

    public bool Recoverable(int processId, long processStartTicks, DateTimeOffset now) => SameProcess(processId, processStartTicks)
        && UnloadedAt is {} unloaded && now >= unloaded && now - unloaded <= RecoveryWindow;

    public static ReturnGuard Create(uint zone, Vector3 position, float rotation, int processId, long processStartTicks)
    {
        var guard = new ReturnGuard(zone, position.X, position.Y, position.Z, rotation, processId, processStartTicks, DateTimeOffset.UtcNow);
        Validate(guard);
        return guard;
    }

    public static void Save(string path, ReturnGuard guard)
    {
        Validate(guard);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(guard));
        File.Move(temporary, path, true);
    }

    /// <summary>Returns null when absent; a corrupt or implausible record is reported as such and never used.</summary>
    public static ReturnGuard? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 4096) throw new InvalidDataException("Return guard is too large.");
        var guard = JsonSerializer.Deserialize<ReturnGuard>(bytes) ?? throw new InvalidDataException("Empty return guard.");
        Validate(guard);
        return guard;
    }

    public static void Clear(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Validate(ReturnGuard guard)
    {
        if (guard.Zone == 0 || guard.ProcessId <= 0 || guard.ProcessStartTicks <= 0)
            throw new InvalidDataException("Return guard is missing its zone or process identity.");
        foreach (var value in new[] { guard.X, guard.Y, guard.Z, guard.Rotation })
            if (!float.IsFinite(value) || Math.Abs(value) > MaxCoordinate) throw new InvalidDataException("Return guard has an implausible position.");
    }
}
