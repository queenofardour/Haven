using Haven.Core;

internal static class InteriorTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Brightness waits for the layout's first housing pass, then refreshes on its own once the interior settled", () =>
        {
            var preview = new InteriorPreview();
            var options = new InteriorOptions { Brightness = 2, Floors = [new() { Wallpaper = 66068 }, new(), new()] };
            List<FixtureChange> writes = []; var lights = 0;
            // Bound while the layout is still busy: nothing is handed over, not even the light.
            Assert(!preview.Update(options, true, false, writes.Add, () => lights++));
            Assert(writes.Count == 0 && lights == 0 && preview.Pending && !preview.Settled);
            preview.Update(options, true, true, writes.Add, () => lights++);
            preview.Update(options, false, true, writes.Add, () => lights++);
            Assert(writes.Count == 2 && !preview.Pending && preview.Settled);
            // After settling a GPose refresh may go in while the layout is busy.
            var before = lights;
            Assert(preview.Update(options, true, false, writes.Add, () => lights++) && lights == before + 1);
            preview.Clear();
            Assert(!preview.Settled);
            // An interior with no fixtures settles as soon as the layout is ready, light included.
            var plain = new InteriorPreview(); lights = 0;
            Assert(!plain.Update(new() { Brightness = 3 }, true, false, writes.Add, () => lights++) && lights == 0);
            Assert(plain.Update(new() { Brightness = 3 }, true, true, writes.Add, () => lights++) && lights == 1 && plain.Settled);
        });
        await check("A settled interior sends every finish again without becoming pending, and a new choice replaces the resend", () =>
        {
            var preview = new InteriorPreview();
            var options = new InteriorOptions { Floors = [new() { Wallpaper = 1, Flooring = 2 }, new(), new() { CeilingLight = 3 }] };
            List<FixtureChange> writes = []; var lights = 0;
            for (var i = 0; i < 6; i++) preview.Update(options, false, true, writes.Add, () => lights++);
            Assert(writes.Count == 6 && preview.Settled && !preview.Pending);
            writes.Clear();
            preview.Refresh();
            Assert(preview.Refreshing && !preview.Pending && preview.Settled);
            // Busy layout: nothing goes in, the resend waits.
            Assert(!preview.Update(options, false, false, writes.Add, () => lights++) && writes.Count == 0);
            for (var i = 0; i < 6; i++) { preview.Update(options, false, true, writes.Add, () => lights++); Assert(!preview.Pending); }
            Assert(!preview.Refreshing && writes.Count == 6 && writes.Select(x => x.FixtureId).Distinct().Order().SequenceEqual(new uint[] { 1, 2, 3 }));
            // A change made meanwhile wins: the resend stops and only the new choice goes in.
            preview.Refresh(); writes.Clear();
            var changed = options with { Floors = [new() { Wallpaper = 9, Flooring = 2 }, new(), new() { CeilingLight = 3 }] };
            preview.Update(changed, false, true, writes.Add, () => lights++);
            Assert(!preview.Refreshing && writes is [{ FixtureId: 9 }]);
        });
        await check("A stuck interior is reported after ten seconds and reloads the shell once per home", () =>
        {
            var watch = new InteriorWatch();
            Assert(watch.Observe(0, true) == InteriorWatchStep.None);
            Assert(watch.Observe(9_999, true) == InteriorWatchStep.None);
            Assert(watch.Observe(10_000, true) == InteriorWatchStep.ReloadShell && watch.Reloads == 1);
            Assert(watch.Observe(10_100, true) == InteriorWatchStep.None);          // once per stall
            watch.Rebind();                                                          // the shell loaded again
            Assert(watch.Observe(20_000, true) == InteriorWatchStep.None);
            Assert(watch.Observe(30_000, true) == InteriorWatchStep.Report);        // no second reload
            watch.Rebind();
            Assert(watch.Observe(31_000, false) == InteriorWatchStep.None && watch.WaitedMilliseconds(31_000) == 0);
            Assert(watch.Observe(32_000, true) == InteriorWatchStep.None && watch.Observe(41_999, true) == InteriorWatchStep.None);
            Assert(watch.Observe(42_000, true) == InteriorWatchStep.Report);
            watch.Reset();                                                           // settled, or another home
            Assert(watch.Observe(50_000, true) == InteriorWatchStep.None && watch.Observe(60_000, true) == InteriorWatchStep.ReloadShell);
            // A short wait between ready frames never counts.
            var calm = new InteriorWatch();
            for (long t = 0; t < 60_000; t += 500) Assert(calm.Observe(t, t % 2000 != 0) == InteriorWatchStep.None);
        });
        await check("Interiors compare by their finishes, not by array identity, and describe themselves for the log", () =>
        {
            var a = new InteriorOptions { Brightness = 2, Floors = [new() { Wallpaper = 1 }, new(), new()] };
            var b = new InteriorOptions { Brightness = 2, Floors = [new() { Wallpaper = 1 }, new(), new()] };
            Assert(a != b && InteriorPlan.Same(a, b) && InteriorPlan.Same(null, null) && !InteriorPlan.Same(a, null));
            Assert(!InteriorPlan.Same(a, b with { Brightness = 3 }) && !InteriorPlan.Same(a, b with { MainHallCeilingLight = 5 }));
            Assert(!InteriorPlan.Same(a, b with { Floors = [new() { Wallpaper = 1 }, new() { Flooring = 2 }, new()] }));
            Assert(InteriorPlan.Describe(a with { MainHallCeilingLight = 7 }) == "brightness 2, floors [1,0,0] [0,0,0] [0,0,0], hall 7");
            Assert(InteriorPlan.Describe(new()) == "brightness shell, floors [0,0,0] [0,0,0] [0,0,0]");
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Interior assertion failed."); }
}
