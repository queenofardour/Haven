using Haven.Core;

internal static class WeatherTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Housing weather restores only the empty weather GPose leaves behind, never a photo tool's pick", () =>
        {
            var weather = new HousingWeather();
            Assert(weather.Observe(2, false, 7) == null && weather.Remembered == 2);      // the home's working weather
            Assert(weather.Observe(0, true, 7) == 2);                                     // GPose fell back to 0: restore
            Assert(weather.Observe(0, false, 7) == 2);                                    // and after leaving GPose
            Assert(weather.Observe(4, true, 7) == null && weather.Remembered == 2);      // a photo tool's pick stays, not adopted
            Assert(weather.Observe(5, false, 7) == null && weather.Remembered == 5);      // a real change outside GPose is kept
            weather.Clear();
            Assert(weather.Observe(0, false, 7) == 7);                                    // nothing seen yet: the shell's natural weather
            Assert(new HousingWeather().Observe(0, false, 0) == null);                    // nothing valid to restore
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Housing weather assertion failed."); }
}
