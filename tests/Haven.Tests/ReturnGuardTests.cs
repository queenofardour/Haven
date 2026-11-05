using System.Numerics;
using Haven.Core;

internal static class ReturnGuardTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Return guard round-trips the origin and recovers only an immediate same-process reload", () =>
        {
            var directory = Directory.CreateTempSubdirectory("haven-guard-");
            try
            {
                var path = Path.Combine(directory.FullName, ReturnGuard.FileName);
                Assert(ReturnGuard.Load(path) == null);
                var saved = ReturnGuard.Create(628, new Vector3(12.5f, 4.25f, -80), 1.5f, 4242, 638_000_000_000_000_000);
                ReturnGuard.Save(path, saved);
                var loaded = ReturnGuard.Load(path)!;
                Assert(loaded.Zone == 628 && loaded.Position == new Vector3(12.5f, 4.25f, -80) && loaded.Rotation == 1.5f);
                Assert(loaded.SameProcess(4242, 638_000_000_000_000_000));
                Assert(!loaded.SameProcess(4242, 638_000_000_000_000_001) && !loaded.SameProcess(4243, 638_000_000_000_000_000));
                Assert(!File.Exists(path + ".tmp"));
                // Recovery applies only to an immediate same-process reload after an unload mid-visit.
                var now = DateTimeOffset.UtcNow;
                Assert(!loaded.Recoverable(4242, 638_000_000_000_000_000, now));
                var unloaded = loaded with { UnloadedAt = now };
                ReturnGuard.Save(path, unloaded);
                Assert(ReturnGuard.Load(path)!.UnloadedAt == now);
                Assert(unloaded.Recoverable(4242, 638_000_000_000_000_000, now.AddSeconds(2)));
                Assert(!unloaded.Recoverable(4242, 638_000_000_000_000_000, now + ReturnGuard.RecoveryWindow + TimeSpan.FromSeconds(1)));
                Assert(!unloaded.Recoverable(4242, 638_000_000_000_000_000, now.AddSeconds(-5)));
                Assert(!unloaded.Recoverable(4243, 638_000_000_000_000_000, now.AddSeconds(2)));
                ReturnGuard.Clear(path); ReturnGuard.Clear(path);
                Assert(ReturnGuard.Load(path) == null);
            }
            finally { directory.Delete(true); }
        });
        await check("Return guard refuses corrupt, implausible or identity-less records", () =>
        {
            var directory = Directory.CreateTempSubdirectory("haven-guard-");
            try
            {
                var path = Path.Combine(directory.FullName, ReturnGuard.FileName);
                Reject(() => ReturnGuard.Create(0, Vector3.Zero, 0, 1, 1));
                Reject(() => ReturnGuard.Create(628, new Vector3(float.NaN, 0, 0), 0, 1, 1));
                Reject(() => ReturnGuard.Create(628, new Vector3(0, 0, 20_000), 0, 1, 1));
                Reject(() => ReturnGuard.Create(628, Vector3.Zero, 0, 0, 1));
                File.WriteAllText(path, "{\"Zone\":628,\"X\":1e9,\"Y\":0,\"Z\":0,\"Rotation\":0,\"ProcessId\":1,\"ProcessStartTicks\":1,\"SavedAt\":\"2026-10-25T00:00:00Z\"}");
                Reject(() => ReturnGuard.Load(path));
                File.WriteAllText(path, "not json");
                Reject(() => ReturnGuard.Load(path));
            }
            finally { directory.Delete(true); }
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Return guard assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; } catch (System.Text.Json.JsonException) { return; }
        throw new Exception("An unsafe return guard was accepted.");
    }
}
