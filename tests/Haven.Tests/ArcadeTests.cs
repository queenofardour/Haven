using Haven.Core;

internal static class ArcadeTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("The six arcade machines map to their GoldSaucerArcadeMachine rows, windows and per-result data", () =>
        {
            Assert(ArcadeMachines.All.Count == 6 && ArcadeMachines.All.Select(m => m.Type).OrderBy(t => t).SequenceEqual([0, 1, 2, 3, 4, 5]));
            Assert(ArcadeMachines.All.Select(m => m.Row).Distinct().Count() == 6 && ArcadeMachines.All.All(m => m.Row is >= 2359297 and <= 2359302));
            Assert(ArcadeMachines.All.All(m => m.Actions.Length == 4 && m.MachineTimelines.Length == 4 && m.Images.Length == 4 && m.Timeout > 0 && m.EndDelay > m.BalloonDelay));
            Assert(ArcadeMachines.ByModel(706) is { Type: 1, Idle: 4072 } && ArcadeMachines.ByModel(647) is { Type: 2 } && ArcadeMachines.ByModel(705) is { Rounds: true });
            Assert(ArcadeMachines.ByModel(707) is { Gathering: true, Type: 5 } && ArcadeMachines.ByModel(708) is { Gathering: true, Type: 4 } && ArcadeMachines.ByModel(1) == null);
            // Names are the Addon rows in game-type order (Monster Toss 9987 … The Moogle's Paw 9990; 0.46.0 had Cuff-a-Cur and the
            // claw swapped), and every machine answers at a play spot of its own.
            Assert(ArcadeMachines.All.Where(m => m.Type <= 3).All(m => m.NameText == 9987 + m.Type) && ArcadeMachines.ByModel(708) is { NameText: 9993 } && ArcadeMachines.ByModel(707) is { NameText: 9994 });
            Assert(ArcadeMachines.All.All(m => m.Parts.Length > 0) && ArcadeMachines.ByModel(706) is { Parts: ["w1eb_m1_glgm1"] });
        });

        await check("Machine reactions: result glows fade from full, parts show and hide on cue, and colour overrides write the client's bytes", () =>
        {
            GlowFade[] lamps = [new(false, 0, 1), new(true, 33, 1)];
            Assert(SgbTimelines.GlowLevel(lamps, 0) == 0 && SgbTimelines.GlowLevel(lamps, 1000) == 0 && SgbTimelines.GlowLevel(lamps, 1200) == 1);
            Assert(SgbTimelines.GlowLevel([new(false, 0, 30)], 500) is > .4f and < .6f);
            (int, bool)[] ball = [(39, true), (50, false)];
            Assert(!SgbTimelines.ShownAt(ball, 1000, false) && SgbTimelines.ShownAt(ball, 1400, false) && !SgbTimelines.ShownAt(ball, 2000, false));
            (int, int, System.Numerics.Vector4)[] flash = [(29, 1, new(.8f, 0, 0, 1)), (59, 1, new(1, 1, 1, 1))];
            Assert(SgbTimelines.ColourAt(flash, 0) == System.Numerics.Vector4.Zero && SgbTimelines.ColourAt(flash, 1500) == new System.Numerics.Vector4(.8f, 0, 0, 1));
            Assert(SgbTimelines.ColourBytes(new(.8f, 0, 0, 1)) == (1, 204, 0, 0, 255) && SgbTimelines.ColourBytes(new(2, 1, 0, .5f)) == (2, 255, 128, 0, 128));
            Assert(SgbTimelines.ColourBytes(System.Numerics.Vector4.Zero) == (1, 0, 0, 0, 0));
        });

        await check("Personal bests are kept per character and machine, beaten only by a higher score, and survive a reload", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "haven-bests-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var bests = new ArcadeBests(path);
                Assert(bests.Best("a", "striker") == null && !bests.Record("a", "monstertoss", 0));
                Assert(bests.Record("a", "striker", ArcadeBests.Score("striker", 2, 0, 0)) && bests.Best("a", "striker") == 3);
                Assert(!bests.Record("a", "striker", 3) && !bests.Record("a", "striker", 1) && bests.Record("a", "striker", 4));
                Assert(bests.Record("b", "striker", 1) && bests.Best("a", "striker") == 4);
                Assert(bests.Record("a", "finerminer", ArcadeBests.Score("finerminer", 0, 0, 1425)));
                Assert(new ArcadeBests(path) is var again && again.Best("a", "striker") == 4 && again.Best("b", "striker") == 1 && again.Best("a", "finerminer") == 1425);
                Assert(ArcadeBests.Describe("striker", 4) == "PULVERIZING!!!" && ArcadeBests.Describe("cuffacur", 1) == "WEAK..." && ArcadeBests.Describe("monstertoss", 1) == "1 basket");
                Assert(ArcadeBests.Score("mooglespaw", 1, 0, 0) == 2 && ArcadeBests.Score("mooglespaw", 3, 0, 0) == 1 && ArcadeBests.Score("mooglespaw", 0, 0, 0) == 0);
                Assert(ArcadeBests.Describe("outonalimb", 1050) == "1,050 points" || ArcadeBests.Describe("outonalimb", 1050).StartsWith("1"));
            }
            finally { File.Delete(path); }
        });

        await check("Out on a Limb and The Finer Miner grade swings by the machine's distance bands and run Double Down rounds", () =>
        {
            var miner = new GatheringArcade(true, new Random(1));
            Assert(miner.Level(0) == 3 && miner.Level(1) == 3 && miner.Level(2) == 2 && miner.Level(7) == 2 && miner.Level(8) == 1 && miner.Level(14) == 1 && miner.Level(15) == 0 && miner.Level(100) == 0);
            var limb = new GatheringArcade(false, new Random(1));
            Assert(limb.Level(2) == 3 && limb.Level(3) == 2 && limb.Level(10) == 2 && limb.Level(11) == 1 && limb.Level(20) == 1 && limb.Level(21) == 0);
            // Difficulty sets the swings: the Finer Miner 6/5/4, Out on a Limb always 10.
            miner.Choose(2); Assert(miner.Attempts == 4 && miner.Round == 0 && miner.Gauge == GatheringArcade.GaugeMax);
            limb.Choose(0); Assert(limb.Attempts == 10 && limb.Speed == 1500);
            // A swing on the spot empties the gauge: the round is won and Double Down is offered.
            Assert(miner.Swing(miner.Spot) == 3 && miner.RoundWon && miner.CanDoubleDown && miner.Payout == 60 && miner.NextPayout == 200);
            miner.Next(); Assert(miner.Round == 1 && !miner.RoundWon && miner.SwingsLeft == 4);
            // Far swings cut nothing; a round lost runs out of swings.
            var far = miner.Spot > 50 ? 0 : 100;
            for (var i = 0; i < 4; i++) Assert(miner.Swing(far) == 0);
            Assert(miner.SwingsLeft == 0 && !miner.RoundWon);
            Assert(new GatheringArcade(true, new Random()).LevelMessage(3) == 4788 && new GatheringArcade(false, new Random()).LevelMessage(0) == 4790);
            // The sixth round won offers no more Double Down.
            var long_ = new GatheringArcade(false, new Random(5)); long_.Choose(1);
            for (var round = 0; round < GatheringArcade.MaxRounds; round++) { Assert(long_.Swing(long_.Spot) == 3); if (round < GatheringArcade.MaxRounds - 1) { Assert(long_.CanDoubleDown); long_.Next(); } }
            Assert(long_.RoundWon && !long_.CanDoubleDown && long_.Payout == 750);
        });
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
}
