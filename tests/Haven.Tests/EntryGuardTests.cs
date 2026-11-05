using Haven.Core;

internal static class EntryGuardTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Entry guard refuses duties, PvP, queues, combat, events, transitions and death with a reason", () =>
        {
            Assert(EntryGuard.Reason(default) == null);
            foreach (var state in new EntryState[]
            {
                new(true, false, false, false, false, false, false), new(false, true, false, false, false, false, false),
                new(false, false, true, false, false, false, false), new(false, false, false, true, false, false, false),
                new(false, false, false, false, true, false, false), new(false, false, false, false, false, true, false),
                new(false, false, false, false, false, false, true),
            })
                Assert(!string.IsNullOrWhiteSpace(EntryGuard.Reason(state)));
            // A zone change in progress is reported first: its other flags are not yet trustworthy.
            Assert(EntryGuard.Reason(new(true, true, true, true, true, true, true))!.Contains("changing areas"));
            Assert(EntryGuard.Reason(new(true, true, false, true, false, false, false))!.Contains("duty", StringComparison.OrdinalIgnoreCase));
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Entry guard assertion failed."); }
}
