using Haven.Core;

internal static class DisplayTests
{
    public static async Task Run(Func<string, Action, Task> check, House sample)
    {
        await check("Vase flowers, fish prints and aquariums travel in the home format, and tanks hold fish as the game's do", () =>
        {
            // Tier N: N slots, sizes adding to at most N, one water.
            Assert(Aquariums.Fits(1, [(1, 1u)]) && !Aquariums.Fits(1, [(2, 1u)]) && Aquariums.Fits(4, [(2, 2u), (1, 2u), (1, 2u)]));
            Assert(!Aquariums.Fits(4, [(1, 1u), (1, 2u)]) && !Aquariums.Fits(3, [(1, 1u), (1, 1u), (1, 1u), (1, 1u)]) && Aquariums.Fits(4, [(4, 2u)]) && Aquariums.Fits(2, []));
            var home = sample with { Furnishings = [
                new() { ItemId = 14008, Display = 1 },
                new() { ItemId = 15000, Display = 3040 },
                new() { ItemId = 16000, Aquarium = new() { Fish = [12, 30], Decor = 3 } },
            ] };
            var read = HouseFormat.Parse(HouseFormat.Serialize(home));
            Assert(read.Furnishings[0].Display == 1 && read.Furnishings[1].Display == 3040 && read.Furnishings[2].Aquarium is { Decor: 3 } tank && tank.Fish.SequenceEqual([12u, 30u]));
            // Nothing written when unused.
            Assert(!System.Text.Encoding.UTF8.GetString(HouseFormat.Serialize(sample with { Furnishings = [new() { ItemId = 1 }] })).Contains("display", StringComparison.OrdinalIgnoreCase));
            void Refused(Furnishing bad) { try { HouseFormat.Validate(sample with { Furnishings = [bad] }); throw new Exception("A bad display was accepted."); } catch (InvalidDataException) { } }
            Refused(new() { ItemId = 1, Display = 5, Picture = 3 });
            Refused(new() { ItemId = 1, Aquarium = new() { Fish = [1, 2, 3, 4, 5] } });
            Refused(new() { ItemId = 1, Aquarium = new() { Decor = 7 } });
            Refused(new() { ItemId = 1, Display = 2, Aquarium = new() { Fish = [1] } });
        });
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
}
