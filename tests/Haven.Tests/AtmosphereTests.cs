using System.Numerics;
using Haven.Core;

// Moods, weather and time of day; /random rolls; rows, circles, mirrors and saved groups (0.64.0).
internal static class AtmosphereTests
{
    public static async Task Run(Func<string, Action, Task> check, Func<House> sample)
    {
        await check("Moods keep the room's atmosphere, switch it back in one go, travel in the home and are validated", () =>
        {
            var house = sample() with
            {
                Interior = new InteriorOptions { Brightness = 2, Weather = 7, TimeOfDay = 18 * 60 + 30, Outside = "rain" },
                Music = new MusicOptions { Playing = true, Playlist = [new MusicTrack { OrchestrionId = 12 }] },
            };
            var evening = HomeMoods.Capture(house, "  Evening ", music: true);
            Assert(evening.Name == "Evening" && evening.Brightness == 2 && evening.Weather == 7 && evening.TimeOfDay == 1110 && evening.Outside == "rain");
            Assert(evening.Music is { Playing: true, StartedAtUnixMs: 0 } && HomeMoods.Showing(house, evening));
            var bright = house with { Interior = house.Interior with { Brightness = 5, Weather = null, TimeOfDay = null, Outside = null } };
            Assert(!HomeMoods.Showing(bright, evening));
            var applied = HomeMoods.Apply(bright.Interior, evening);
            Assert(applied.Brightness == 2 && applied.Weather == 7 && applied.TimeOfDay == 1110 && applied.Outside == "rain");
            // Saved in the home and read back; nothing extra written for a home without them.
            var saved = house with { Interior = house.Interior with { Moods = [evening, HomeMoods.Capture(house, "Quiet", false)] } };
            var back = HouseFormat.Parse(HouseFormat.Serialize(saved));
            Assert(back.Interior.Moods is { Length: 2 } moods && moods[0].Name == "Evening" && moods[1].Music == null && moods[0].Music!.Playlist.Length == 1);
            var plain = System.Text.Encoding.UTF8.GetString(HouseFormat.Serialize(sample()));
            Assert(!plain.Contains("moods", StringComparison.OrdinalIgnoreCase) && !plain.Contains("timeOfDay") && !plain.Contains("outside"));
            // Names: unique within the home, case aside, and trimmed to length.
            Assert(HomeMoods.UniqueName([evening], "evening") == "evening (2)" && HomeMoods.UniqueName([evening], "Evening", evening.Id) == "Evening");
            Assert(HomeMoods.UniqueName([], new string('x', 80)).Length == HomeMoods.MaxName);
            Assert(HomeMoods.Clock(0) == "12:00 am" && HomeMoods.Clock(18 * 60 + 30) == "6:30 pm" && HomeMoods.Clock(12 * 60) == "12:00 pm");
            // Refused: a time past midnight, an unknown sound, a duplicate name, too many moods, a bad weather row.
            foreach (var bad in new[]
            {
                house.Interior with { TimeOfDay = 1440 }, house.Interior with { TimeOfDay = -1 }, house.Interior with { Outside = "lava" },
                house.Interior with { Weather = 0 }, house.Interior with { Weather = 300 },
                house.Interior with { Moods = [evening, evening with { Id = Guid.NewGuid(), Name = "EVENING" }] },
                house.Interior with { Moods = [.. Enumerable.Range(0, HomeMoods.MaxMoods + 1).Select(i => evening with { Id = Guid.NewGuid(), Name = "M" + i })] },
                house.Interior with { Moods = [evening with { TimeOfDay = 2000 }] },
                house.Interior with { Moods = [evening with { Name = " padded" }] },
                house.Interior with { Moods = [evening with { Name = null! }] },
            })
                Reject(() => HouseFormat.Serialize(house with { Interior = bad }));
            // Every outside sound is a day and a night loop of the game's base data, day from 6 am.
            Assert(HomeAmbience.All.All(s => s.Day.StartsWith("bgcommon/sound/") && s.Night.StartsWith("bgcommon/sound/") && s.Day.EndsWith(".scd")));
            var rain = HomeAmbience.Find("rain")!;
            Assert(HomeAmbience.PathFor(rain, 6 * 60) == rain.Day && HomeAmbience.PathFor(rain, 5 * 60 + 59) == rain.Night && HomeAmbience.PathFor(rain, 18 * 60) == rain.Night);
        });

        await check("/random and /dice: the limit as the game reads it, the relay's roll in range, worded like the game's", () =>
        {
            Assert(RoomRolls.Limit("") == RoomRolls.DefaultLimit && RoomRolls.Limit("party") == RoomRolls.DefaultLimit);
            Assert(RoomRolls.Limit("20") == 20 && RoomRolls.Limit("party 6") == 6 && RoomRolls.Limit("999") == 999);
            Assert(RoomRolls.Limit("1") == null && RoomRolls.Limit("1000") == null && RoomRolls.Limit("99999999999") == null);
            for (var i = 0; i < 500; i++)
            {
                var d = RoomRolls.Roll(6); Assert(d is >= 1 and <= 6);
                var r = RoomRolls.Roll(RoomRolls.DefaultLimit); Assert(r is >= 0 and <= 999);
            }
            Assert(RoomRolls.Text(null, 512, RoomRolls.DefaultLimit) == "Random! You roll a 512.");
            Assert(RoomRolls.Text("Aria Venn", 8, 20) == "Random! Aria Venn rolls an 8 (out of 20).");
            Assert(RoomRolls.Text("A", 18, 20).Contains(" an 18") && RoomRolls.Text("A", 85, 99).Contains(" an 85") && RoomRolls.Text("A", 7, 9).Contains(" a 7"));
            // On the wire: a roll needs a valid limit and, once made, a number in range; other lines carry neither.
            var asked = new RoomChatMessage(RoomRolls.Channel, "/random", Roll: 20);
            Assert(RoomChat.Decode(RoomChat.Encode(asked)) == asked);
            var made = asked with { Rolled = 20, Text = RoomRolls.Text("A", 20, 20) };
            Assert(RoomChat.Decode(RoomChat.Encode(made)) == made);
            Reject(() => RoomChat.Encode(asked with { Rolled = 21 }));
            Reject(() => RoomChat.Encode(asked with { Roll = 0 }));
            Reject(() => RoomChat.Encode(asked with { Roll = 1 }));
            Reject(() => RoomChat.Encode(new RoomChatMessage(10, "hello", Roll: 20)));
            Reject(() => RoomChat.Encode(new RoomChatMessage(10, "hello", Rolled: 3)));
        });

        await check("Copies in a row, in a circle and mirrored: placed, turned and regrouped as their originals", () =>
        {
            Guid group = Guid.NewGuid();
            var a = new Furnishing { ItemId = 1, Position = [1, 0, 2], Rotation = 30, Group = group };
            var b = new Furnishing { ItemId = 2, Position = [2, 0, 2], Rotation = 90, Group = group, Roll = 10, Pitch = 5 };
            var row = FurnishingGroups.Row([a, b], 3, new Vector3(0, 0, 1.5f));
            Assert(row.Length == 6 && row[0] == a && row[1] == b && row.Select(f => f.Id).Distinct().Count() == 6);
            Assert(Near(row[2].Translation, new(1, 0, 3.5f)) && Near(row[5].Translation, new(2, 0, 5)) && row[3].Rotation == 90);
            // Each copied set is a group of its own.
            Assert(row[2].Group == row[3].Group && row[2].Group != group && row[4].Group == row[5].Group && row[4].Group != row[2].Group);

            // Chairs round a table: a chair facing +Z (turn 0), 2 m from the table's middle.
            var chair = new Furnishing { ItemId = 3, Position = [0, 0, -2], Rotation = 0 };
            var circle = FurnishingGroups.Circle([chair], chair, 4, 2);
            Assert(circle.Length == 4 && circle[0] == chair);
            var spots = circle.Skip(1).Select(f => f.Translation).ToArray();
            Assert(spots.All(p => MathF.Abs(p.Length() - 2) < 1e-3f) && spots.Any(p => Near(p, new(0, 0, 2))));
            // Every chair still faces the middle.
            Assert(circle.All(f => Vector3.Dot(FurnishingGroups.Front(f.Rotation), Vector3.Normalize(-f.Translation)) > .999f));

            // Mirrored across X at 3: positions reflected, turns and leans mirrored, the originals kept.
            var mirror = FurnishingGroups.Mirror([a, b], 0, 3, keep: true);
            Assert(mirror.Length == 4 && mirror[0] == a && Near(mirror[2].Translation, new(5, 0, 2)) && Near(mirror[3].Translation, new(4, 0, 2)));
            Assert(MathF.Abs(mirror[2].Rotation - 330) < 1e-3f && mirror[3].Roll == -10 && mirror[3].Pitch == 5);
            // Across Z: front and back swap, so a piece facing +Z faces −Z.
            // Copies of one piece of a linked group alone aren't linked to nothing: no group of one.
            var lone = FurnishingGroups.Row([a], 3, new Vector3(1, 0, 0));
            Assert(lone.Length == 3 && lone[0].Group == group && lone.Skip(1).All(f => f.Group == null));
            var flipped = FurnishingGroups.Mirror([chair], 2, 0, keep: false);
            Assert(flipped.Length == 1 && flipped[0].Id == chair.Id && Near(flipped[0].Translation, new(0, 0, 2)) && MathF.Abs(flipped[0].Rotation - 180) < 1e-3f);
            // A mirrored piece's whole drawn placement is the original's mirror image when it is the same left and right:
            // mirror(original) = mirrored placement × the model's own left-right flip.
            foreach (var (axis, piece) in new[] { (0, b), (2, b), (0, a), (2, a) })
            {
                var copy = FurnishingGroups.Mirror([piece], axis, 0, keep: false)[0];
                var reflect = axis == 0 ? Matrix4x4.CreateScale(-1, 1, 1) : Matrix4x4.CreateScale(1, 1, -1);
                var expected = Matrix4x4.CreateScale(-1, 1, 1) * PlacementMath.ItemMatrix(piece) * reflect;
                Assert(Close(PlacementMath.ItemMatrix(copy), expected));
            }
        });

        await check("Saved groups keep pieces about their middle, give each placement fresh ids and one group, and refuse bad files", () =>
        {
            var link = Guid.NewGuid();
            var pieces = new[]
            {
                new Furnishing { ItemId = 1, Position = [10, 1, 20], Group = link, StainId = 3 },
                new Furnishing { ItemId = 2, Position = [12, 2, 20], Group = link },
                new Furnishing { ItemId = 3, Position = [11, 1.5f, 23] },
            };
            var saved = SavedGroups.Capture("  Bar corner ", pieces);
            Assert(saved.Name == "Bar corner" && saved.Pieces.Length == 3 && saved.Pieces.All(f => f.Group == null));
            Assert(Near(saved.Pieces[0].Translation, new(-1, 0, -1)) && Near(saved.Pieces[2].Translation, new(0, .5f, 2)) && saved.Pieces[0].StainId == 3);
            var back = SavedGroups.Parse(SavedGroups.Serialize(saved));
            Assert(back.Name == saved.Name && back.Pieces.Length == 3);
            var (leader, rest) = SavedGroups.Copies(saved);
            var all = rest.Append(leader).ToArray();
            Assert(rest.Length == 2 && all.Select(f => f.Id).Distinct().Count() == 3 && all.All(f => f.Group == leader.Group && f.Group != null));
            Assert(all.All(f => saved.Pieces.All(p => p.Id != f.Id)));
            Assert(SavedGroups.Copies(saved).Leader.Id != leader.Id);
            // One piece: no group.
            Assert(SavedGroups.Copies(SavedGroups.Capture("One", [pieces[0]])).Leader.Group == null);
            Reject(() => SavedGroups.Capture("None", []));
            Reject(() => SavedGroups.Parse(System.Text.Encoding.UTF8.GetBytes("{\"id\":\"00000000-0000-0000-0000-000000000000\",\"name\":\"x\",\"pieces\":[]}")));
            Reject(() => SavedGroups.Serialize(saved with { Pieces = [saved.Pieces[0] with { Scale = 50 }] }));
            Assert(SavedGroups.Clean("\u0001  ") == "Group" && SavedGroups.Clean(new string('y', 99)).Length == SavedGroups.MaxName);
            // The store: saved, listed by name, deleted into deleted/.
            var folder = Path.Combine(Path.GetTempPath(), "haven-groups-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new SavedGroupStore(folder);
                store.Save(saved); store.Save(SavedGroups.Capture("Alcove", [pieces[1]]));
                Assert(store.List().Select(g => g.Name).SequenceEqual(["Alcove", "Bar corner"]));
                store.Delete(saved.Id);
                Assert(store.List().Count == 1 && Directory.GetFiles(Path.Combine(folder, "deleted")).Length == 1);
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        });
    }

    public static async Task District(Func<string, Action, Task> check)
    {
        await check("A district's fair-weather loops have a version for each weather heard outside, from the district's own set", () =>
        {
            const string sea = "bgcommon/sound/sea/", est = "bgcommon/sound/est/", wil = "bgcommon/sound/wil/", roc = "bgcommon/sound/roc/";
            // Shirogane's coast (sound_e3f1_coast.essb), the Goblet's calm (sound_wil_calm.essb), the Empyreum's (sound_r1h1.essb).
            string[] coast = [sea + "sea_bg_sea_d_fine_coast_loop.scd", est + "est_wether_coast_d_fine_spot.scd", sea + "sea_bg_sea_n_fine_coast_loop.scd",
                sea + "sea_bg_sea_d_mist_coast_loop.scd", sea + "sea_bg_sea_d_wind_coast_loop.scd", est + "est_wether_coast_d_wind_spot.scd",
                sea + "sea_bg_sea_d_rain_coast_loop.scd", sea + "sea_bg_sea_n_rain_coast_loop.scd", est + "est_wether_coast_d_rain_spot.scd",
                est + "est_wether_coast_d_thunder_spot.scd", est + "est_wether_town_n_mist_spot.scd"];
            string[] calm = [wil + "wil_bg_wil_d_fine_calm_loop.scd", wil + "wil_bg_wil_d_fine_calm_spot.scd", wil + "wil_bg_wil_dn_rain_loop.scd",
                wil + "wil_bg_wil_n_rain_loop.scd", wil + "wil_bg_wil_d_rainstorm_calm_loop.scd", "bgcommon/sound/fst/fst_bg_fst_d_thndstorm_narrow_spot.scd"];
            string[] empyreum = [roc + "roc_bg_roc_d_fine_loop.scd", roc + "roc_bg_roc_d_snow_loop.scd", roc + "roc_bg_roc_n_snow_loop.scd"];
            Assert(DistrictWeather.FairWeather(coast[0]) && DistrictWeather.FairWeather(coast[1]) && !DistrictWeather.FairWeather(coast[3])
                && !DistrictWeather.FairWeather("bgcommon/sound/hou/fine_clock.scd") && !DistrictWeather.FairWeather(coast[6]));
            Assert(DistrictWeather.Variant(coast[0], null, coast) == coast[0]);
            Assert(DistrictWeather.Variant(coast[0], "rain", coast) == coast[6] && DistrictWeather.Variant(coast[2], "drizzle", coast) == coast[7]);
            Assert(DistrictWeather.Variant(coast[1], "rain", coast) == coast[8] && DistrictWeather.Variant(coast[1], "thunderstorm", coast) == coast[9]);
            Assert(DistrictWeather.Variant(coast[0], "thunderstorm", coast) == coast[6] && DistrictWeather.Variant(coast[0], "wind", coast) == coast[4]);
            Assert(DistrictWeather.Variant(coast[1], "wind", coast) == coast[5] && DistrictWeather.Variant(coast[0], "snow", coast) == null);
            // Another set's naming: one rain loop for day and night; a storm of its own; a thunder spot from another district.
            Assert(DistrictWeather.Variant(calm[0], "rain", calm) == calm[2] && DistrictWeather.Variant(calm[0], "rainstorm", calm) == calm[4]);
            Assert(DistrictWeather.Variant(calm[1], "thunderstorm", calm) == calm[5] && DistrictWeather.Variant(calm[0], "wind", calm) == null);
            Assert(DistrictWeather.Variant(empyreum[0], "blizzard", empyreum) == empyreum[1] && DistrictWeather.Variant(empyreum[0], "rain", empyreum) == null);
            Assert(DistrictWeather.Slot(coast[0]) == DistrictWeather.Slot(coast[2]) && DistrictWeather.Slot(coast[0]) != DistrictWeather.Slot(coast[1]));
            // The set a level names, and the sounds a set names.
            var lvb = System.Text.Encoding.ASCII.GetBytes("\0\0bgcommon/env/sound/est_e3/e3_fld/sound_e3f1_coast.essb\0bg/x/level/bg.lgb\0");
            Assert(DistrictWeather.SetOf(lvb) == "bgcommon/env/sound/est_e3/e3_fld/sound_e3f1_coast.essb");
            var essb = System.Text.Encoding.ASCII.GetBytes("ESSB\u0001\0" + string.Join("\0\u0003", coast) + "\0" + coast[0] + "\0");
            Assert(DistrictWeather.SoundsIn(essb).SequenceEqual(coast));
        });
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
    private static bool Close(Matrix4x4 a, Matrix4x4 b)
    {
        for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++) if (MathF.Abs(a[r, c] - b[r, c]) > 1e-3f) return false;
        return true;
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Atmosphere/copies assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected the value to be refused.");
    }
}
