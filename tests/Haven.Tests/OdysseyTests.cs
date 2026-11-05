using Haven.Core;
using Haven.Core.Odyssey;

internal static class OdysseyTests
{
    /// <summary>A brain that plays the engine's own one-ply chooser (the Odyssey's real AI lives in OdysseyAi).</summary>
    internal sealed class PlainBrain : ITriadBrain
    {
        public (int Hand, int Cell) Choose(TriadGame game, Random random) => game.Choose(random);
    }
    private static readonly Func<int, IReadOnlyList<TriadCard>, ITriadBrain> Brains = (_, _) => new PlainBrain();

    private static TriadCard Card(uint id, int top, int right, int bottom, int left, int stars = 1, int type = 0, TriadPerk perk = TriadPerk.None) =>
        new(id, "Card " + id, top, right, bottom, left, stars, type) { Perk = perk };
    private static TriadCard[] Five(uint first, int n, int stars = 1, int type = 0) => [.. Enumerable.Range(0, 5).Select(i => Card(first + (uint)i, n, n, n, n, stars, type))];

    /// <summary>A card set shaped like the game's (ids 1-475, stars rising with the sides), so content ids resolve and power bands are reachable.</summary>
    public static IReadOnlyList<TriadCard> Synthetic()
    {
        var list = new List<TriadCard>();
        for (uint id = 1; id <= 475; id++)
        {
            var stars = (int)(id % 7 == 0 ? 5 : id % 5 == 0 ? 4 : id % 3 == 0 ? 3 : id % 2 == 0 ? 2 : 1);
            int Side(uint salt) => (int)Math.Clamp(1 + (stars - 1) * 2 + (id * salt % 4), 1, 10);
            list.Add(new TriadCard(id, "Card " + id, Side(7), Side(11), Side(13), Side(17), stars, (int)(id % 9 == 0 ? id % 4 + 1 : 0)));
        }
        return list;
    }

    // A game on a 3 × 3 board with two decks of flat cards; blue places first unless said otherwise.
    private static TriadGame Game(TriadCard[] blue, TriadCard[] red, TriadEdge? blueEdge = null, TriadEdge? redEdge = null, TriadRule[]? rules = null, TriadPlayer first = TriadPlayer.Blue) =>
        new(blue, red, first, rules, new Random(1), blueEdge, redEdge);

    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Odyssey engine hooks: each TriadEdge field changes play as documented (corner, side, centre, type, stars, floor, attack, defend, first, last, behind, momentum)", () =>
        {
            // Corner +1: a 5 in a corner takes a 5 beside it; without the edge it doesn't.
            var g = Game(Five(1, 5), Five(11, 5), new TriadEdge { Corner = 1 }, first: TriadPlayer.Red);
            g.Place(0, 1); Assert(g.Place(0, 0).SequenceEqual([1]));
            g = Game(Five(1, 5), Five(11, 5), first: TriadPlayer.Red); g.Place(0, 1); Assert(g.Place(0, 0).Length == 0);
            // Side +1 on an edge middle, Centre +2 in the centre.
            g = Game(Five(1, 5), Five(11, 5), new TriadEdge { Side = 1 }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            g = Game(Five(1, 5), Five(11, 6), new TriadEdge { Centre = 2 }, first: TriadPlayer.Red); g.Place(0, 1); Assert(g.Place(0, 4).SequenceEqual([1]));
            g = Game(Five(1, 5), Five(11, 6), new TriadEdge { Centre = 2 }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            // Type[1] +2 for Primals only; Common +1 for 1-2★, Rare +1 for 4-5★.
            g = Game(Five(1, 5, type: 1), Five(11, 6), new TriadEdge { Type = { [1] = 2 } }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            g = Game(Five(1, 5, type: 2), Five(11, 6), new TriadEdge { Type = { [1] = 2 } }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            g = Game(Five(1, 5, stars: 2), Five(11, 5), new TriadEdge { Common = 1 }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            g = Game(Five(1, 5, stars: 3), Five(11, 5), new TriadEdge { Common = 1, Rare = 1 }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            g = Game(Five(1, 5, stars: 5), Five(11, 5), new TriadEdge { Rare = 1 }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            // Floor 3: a 1 counts as 3 and takes a 2.
            g = Game(Five(1, 1), Five(11, 2), new TriadEdge { Floor = 3 }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            Assert(g.Number(g.Board[1]!, 0, TriadPlayer.Blue, 1, false) == 3);
            // Attack +1 takes an equal; Defend +1 holds against one higher.
            g = Game(Five(1, 5), Five(11, 5), new TriadEdge { Attack = 1 }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            g = Game(Five(1, 5), Five(11, 6), new TriadEdge { Defend = 1 }); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0 && g.Owner[0] == TriadPlayer.Blue);
            // First +2 stays on the first card only; Last +2 when two or fewer cells are empty.
            g = Game(Five(1, 5), Five(11, 6), new TriadEdge { First = 2 }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]) && g.Lasting(1) == 2);
            g.Place(0, 8); Assert(g.Place(0, 7).Length == 0);
            g = Game(Five(1, 5), Five(11, 6), new TriadEdge { Last = 2 });
            g.Place(0, 0); g.Place(0, 1); g.Place(0, 2); g.Place(0, 3); g.Place(0, 4); g.Place(0, 5); g.Place(0, 6);   // blue 0 2 4 6; red 1 3 5
            Assert(g.Turn == TriadPlayer.Red); g.Place(0, 7);
            Assert(g.Place(0, 8).SequenceEqual([5, 7]));
            // Behind +2: only while the score is lower.
            g = Game(Five(1, 5), Five(11, 6), new TriadEdge { Behind = 2 }, first: TriadPlayer.Red);
            g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);                       // equal scores: no bonus
            g.Place(0, 2); Assert(g.Owner[1] == TriadPlayer.Red);                    // red takes blue's card: blue is behind
            Assert(g.Place(0, 4).SequenceEqual([1]));                                // 5 + 2 beats 6
            // Momentum +1: after a placement that takes two, the next card gets +1.
            g = Game(Five(1, 6), Five(11, 5), new TriadEdge { Momentum = 1 });
            g.Place(0, 0); g.Place(0, 1); g.Place(0, 8); g.Place(0, 3);
            Assert(g.Place(0, 4).SequenceEqual([1, 3]));
            g.Place(0, 2);
            var six = g.Hand(TriadPlayer.Blue)[0];
            Assert(g.Takes(six, 5, TriadPlayer.Blue).SequenceEqual([2]) && g.Number(six, 0, TriadPlayer.Blue, 5, true) == 6);
            Assert(g.Place(0, 5).SequenceEqual([2]) && g.Lasting(5) == 1);
        });

        await check("Odyssey engine hooks: Same and Plus by Materia, the walls counting as A, Reverse ignored, Free Will under Order, Ascend under Descension, Corner Guard, Frost, Bond, ties winning, Reveal and Reflect", () =>
        {
            // Same by edge, with no Same rule: a 5 beside two 5s takes both.
            var g = Game(Five(1, 5), Five(11, 5), new TriadEdge { Same = true }, first: TriadPlayer.Red);
            g.Place(0, 0); g.Place(0, 8); g.Place(0, 2); Assert(g.Place(0, 1).OrderBy(x => x).SequenceEqual([0, 2]));
            // Same Wall: the wall is an A; a card with A on top and 5 right beside a 5 takes it through Same.
            TriadCard[] walls = [Card(1, 10, 5, 5, 5), Card(2, 10, 5, 5, 5), Card(3, 10, 5, 5, 5), Card(4, 10, 5, 5, 5), Card(5, 10, 5, 5, 5)];
            g = Game(walls, Five(11, 5), new TriadEdge { SameWall = true, Same = true }, first: TriadPlayer.Red); g.Place(0, 1); Assert(g.Place(0, 0).SequenceEqual([1]));
            g = Game(walls, Five(11, 5), new TriadEdge { Same = true }, first: TriadPlayer.Red); g.Place(0, 1); Assert(g.Place(0, 0).Length == 0);
            // Plus Wall: wall A + top 3 = 13; right 6 + their 7 = 13.
            TriadCard[] plus = [Card(1, 3, 6, 3, 3), Card(2, 3, 6, 3, 3), Card(3, 3, 6, 3, 3), Card(4, 3, 6, 3, 3), Card(5, 3, 6, 3, 3)];
            g = Game(plus, Five(11, 7), new TriadEdge { PlusWall = true, Plus = true }, first: TriadPlayer.Red); g.Place(0, 1); Assert(g.Place(0, 0).SequenceEqual([1]));
            // Ignore Reverse: under Reverse a 6 takes a 5 only with the edge.
            g = Game(Five(1, 6), Five(11, 5), new TriadEdge { IgnoreReverse = true }, rules: [TriadRule.Reverse], first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            g = Game(Five(1, 6), Five(11, 5), rules: [TriadRule.Reverse], first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            // Free Will under Order: any hand card; without it, only the first.
            g = Game(Five(1, 5), Five(11, 5), new TriadEdge { FreeChoice = true }, rules: [TriadRule.Order]); Assert(g.Playable(3));
            g = Game(Five(1, 5), Five(11, 5), rules: [TriadRule.Order]); Assert(g.Playable(0) && !g.Playable(3));
            // Ascend under Descension: blue's Primal gains with each Primal on the board, red's loses.
            g = Game(Five(1, 5, type: 1), Five(11, 5, type: 1), new TriadEdge { Ascend = true }, rules: [TriadRule.Descension]);
            g.Place(0, 0); g.Place(0, 8);
            Assert(g.Modifier(g.Blue[0], TriadPlayer.Blue) == 2 && g.Modifier(g.Red[0], TriadPlayer.Red) == -2);
            // Corner Guard: a corner card isn't taken; an edge card is.
            g = Game(Five(1, 5), Five(11, 6), new TriadEdge { CornerGuard = true }); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            g = Game(Five(1, 5), Five(11, 6), new TriadEdge { CornerGuard = true }); g.Place(0, 1); Assert(g.Place(0, 0).SequenceEqual([1]));
            // Frost: an enemy card beside the placed card loses 1 for good.
            g = Game(Five(1, 5), Five(11, 6), new TriadEdge { Frost = true }, first: TriadPlayer.Red); g.Place(0, 0); g.Place(0, 1);
            Assert(g.Lasting(0) == -1 && g.Number(0, 1) == 5);
            // Bond: a card beside its own type gives both +1.
            g = Game(Five(1, 5, type: 2), Five(11, 6), new TriadEdge { Bond = true }, first: TriadPlayer.Red); g.Place(0, 8); g.Place(0, 0); g.Place(0, 7); g.Place(0, 1);
            Assert(g.Lasting(0) == 1 && g.Lasting(1) == 1 && g.Number(1, 1) == 6);
            // Ties win: a 5 takes a 5.
            g = Game(Five(1, 5), Five(11, 5), new TriadEdge { TiesWin = true }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            // Reveal: the other hand is open without an Open rule; only one way round.
            g = Game(Five(1, 5), Five(11, 5), new TriadEdge { Reveal = true });
            Assert(g.Red.All(g.Open.Contains) && !g.Blue.Any(g.Open.Contains));
            // Reflect 1: the first capture of blue's cards is undone, the second stands.
            g = Game(Five(1, 5), Five(11, 6), new TriadEdge { Reflect = 1 });
            g.Place(0, 0); Assert(g.Place(0, 1).Length == 0 && g.Owner[0] == TriadPlayer.Blue && g.Reflects(TriadPlayer.Blue) == 0);
            g.Place(0, 8); Assert(g.Place(0, 7).SequenceEqual([8]));
        });

        await check("Odyssey engine hooks: every perk (Steadfast, Aegis, Ace Killer, Avalanche, Reckless, Guardian, Cornerstone, Crown, Wild) and vanilla play unchanged without edges", () =>
        {
            TriadCard[] With(TriadPerk perk, int n = 5, int type = 0) => [.. Enumerable.Range(0, 5).Select(i => Card(1 + (uint)i, n, n, n, n, 1, type, perk))];
            // Steadfast: not taken by comparison; Aegis: not by Same, but by comparison.
            var g = Game(With(TriadPerk.Steadfast), Five(11, 6)); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            g = Game(With(TriadPerk.Aegis), Five(11, 6)); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            g = Game(With(TriadPerk.Aegis), Five(11, 5), rules: [TriadRule.Same]); g.Place(0, 0); g.Place(0, 8); g.Place(0, 2); Assert(g.Place(0, 1).Length == 0);
            g = Game(Five(1, 5), Five(11, 5), rules: [TriadRule.Same]); g.Place(0, 0); g.Place(0, 8); g.Place(0, 2); Assert(g.Place(0, 1).Length == 2);
            // Ace Killer: its 1 takes an A.
            g = Game(With(TriadPerk.AceKiller, 1), Five(11, 10), first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            g = Game(Five(1, 1), Five(11, 10), first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            // Avalanche: a card taken by comparison goes on to take its weaker neighbour.
            TriadCard[] avalanche = [Card(1, 7, 7, 7, 7, perk: TriadPerk.Avalanche), Card(2, 7, 7, 7, 7), Card(3, 7, 7, 7, 7), Card(4, 7, 7, 7, 7), Card(5, 7, 7, 7, 7)];
            TriadCard[] chain = [Card(11, 6, 6, 6, 6), Card(12, 5, 5, 5, 5), Card(13, 5, 5, 5, 5), Card(14, 5, 5, 5, 5), Card(15, 5, 5, 5, 5)];
            g = Game(avalanche, chain, first: TriadPlayer.Red); g.Place(0, 1); g.Place(1, 8); g.Place(0, 2);
            var placement = g.PlaceCard(0, 0);
            Assert(placement.Taken.SequenceEqual([1]) && placement.Combo.SequenceEqual([2]) && g.Owner[2] == TriadPlayer.Blue);
            g = Game([.. avalanche.Select(c => c with { Perk = TriadPerk.None })], chain, first: TriadPlayer.Red); g.Place(0, 1); g.Place(1, 8); g.Place(0, 2);
            Assert(g.Place(0, 0).SequenceEqual([1]) && g.Owner[2] == TriadPlayer.Red);
            // A card a combo goes on from strikes: a taken Reckless 5 takes a 6 (7 against 6), a taken Guardian 5 doesn't
            // take a 5 (4 against 5).
            g = Game(avalanche, [Card(11, 5, 5, 5, 5, perk: TriadPerk.Reckless), Card(12, 6, 6, 6, 6), .. chain[2..]], first: TriadPlayer.Red);
            g.Place(0, 1); g.Place(1, 8); g.Place(0, 2);
            placement = g.PlaceCard(0, 0);
            Assert(placement.Taken.SequenceEqual([1]) && placement.Combo.SequenceEqual([2]));
            TriadCard[] strong = [.. avalanche.Select(c => c with { Top = 10, Right = 10, Bottom = 10, Left = 10 })];
            g = Game(strong, [Card(11, 5, 5, 5, 5, perk: TriadPerk.Guardian), .. chain[1..]], first: TriadPlayer.Red);
            g.Place(0, 1); g.Place(1, 8); g.Place(0, 2);
            placement = g.PlaceCard(0, 0);
            Assert(placement.Taken.SequenceEqual([1]) && placement.Combo.Length == 0 && g.Owner[2] == TriadPlayer.Red);
            // Reckless: +2 attacking, -1 defending; Guardian the other way.
            g = Game(With(TriadPerk.Reckless), Five(11, 6), first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            g = Game(With(TriadPerk.Reckless), Five(11, 5)); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            g = Game(With(TriadPerk.Guardian), Five(11, 6)); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            g = Game(With(TriadPerk.Guardian), Five(11, 4), first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            // Cornerstone: +2 in a corner, nothing elsewhere.
            g = Game(With(TriadPerk.Cornerstone), Five(11, 6), first: TriadPlayer.Red); g.Place(0, 1); Assert(g.Place(0, 0).SequenceEqual([1]));
            g = Game(With(TriadPerk.Cornerstone), Five(11, 6), first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            // Crown counts twice in its owner's score (for whoever holds it).
            g = Game(With(TriadPerk.Crown), Five(11, 6)); g.Place(0, 0); Assert(g.Score(TriadPlayer.Blue) == 6);
            g.Place(0, 1); Assert(g.Score(TriadPlayer.Red) == 7 && g.Score(TriadPlayer.Blue) == 4);
            // Wild: takes the best type bonus and counts as every type under Ascension.
            g = Game(With(TriadPerk.Wild), Five(11, 6), new TriadEdge { Type = { [3] = 2 } }, first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            g = Game(With(TriadPerk.Wild), Five(11, 5, type: 4), rules: [TriadRule.Ascension]); g.Place(0, 0); g.Place(0, 8);
            Assert(g.Modifier(g.Blue[0]) == 2 && g.Modifier(g.Red[0]) == 2);
            // Vanilla: with no edges and no perks, every number is the card's own.
            g = Game(Five(1, 5), Five(11, 6)); g.Place(0, 4);
            Assert(g.Number(g.Board[4]!, 0, TriadPlayer.Blue, 4, false) == 5 && g.Number(g.Red[0], 2, TriadPlayer.Red, -1, true) == 6 && g.BlueEdge == null && g.RedEdge == null);
        });

        await check("Odyssey engine hooks: the newer perks (Vanguard, Desperate, Keystone, Frostbite, Rally, Drain, Contrary, Masked, Mimic) work without Materia, and two-trait scrolls carry both", () =>
        {
            TriadCard[] With(TriadPerk perk, int n = 5) => [.. Enumerable.Range(0, 5).Select(i => Card(1 + (uint)i, n, n, n, n, 1, 0, perk))];
            // Vanguard: +2 on its owner's first card only, kept on the cell.
            var g = Game(With(TriadPerk.Vanguard), Five(11, 6), first: TriadPlayer.Red);
            g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]) && g.Lasting(1) == 2);
            g.Place(0, 8); Assert(g.Place(0, 7).Length == 0 && g.Lasting(7) == 0);
            // Desperate: +2 only when played behind.
            g = Game(With(TriadPerk.Desperate), Five(11, 6), first: TriadPlayer.Red);
            g.Place(0, 0); Assert(g.Place(0, 1).Length == 0 && g.Lasting(1) == 0);   // level: no bonus
            g.Place(0, 2); Assert(g.Owner[1] == TriadPlayer.Red);                     // now behind
            Assert(g.Place(0, 4).SequenceEqual([1]) && g.Lasting(4) == 2);
            // Keystone: +2 in the centre, nothing on an edge.
            g = Game(With(TriadPerk.Keystone), Five(11, 6), first: TriadPlayer.Red); g.Place(0, 1); Assert(g.Place(0, 4).SequenceEqual([1]));
            g = Game(With(TriadPerk.Keystone), Five(11, 6), first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            // Frostbite: the enemy card left beside it loses 1 for good; the opponent's own perk needs no edge either.
            g = Game(With(TriadPerk.Frostbite), Five(11, 6), first: TriadPlayer.Red); g.Place(0, 0); g.Place(0, 1);
            Assert(g.Owner[0] == TriadPlayer.Red && g.Lasting(0) == -1 && g.Number(0, 1) == 5 && g.BlueEdge == null);
            g = Game(Five(1, 6), With(TriadPerk.Frostbite), first: TriadPlayer.Blue); g.Place(0, 0); g.Place(0, 1); Assert(g.Lasting(0) == -1);
            // Rally: its owner's cards beside it gain 1, a card it just took included.
            TriadCard[] rally = [Card(1, 5, 5, 5, 5), Card(2, 5, 5, 5, 5, perk: TriadPerk.Rally), Card(3, 5, 5, 5, 5), Card(4, 5, 5, 5, 5), Card(5, 5, 5, 5, 5)];
            g = Game(rally, Five(11, 4)); g.Place(0, 0); g.Place(0, 2);
            Assert(g.Place(0, 1).SequenceEqual([2]) && g.Lasting(0) == 1 && g.Lasting(2) == 1 && g.Lasting(1) == 0);
            // Drain: +1 for each card the placement took.
            g = Game(With(TriadPerk.Drain, 6), Five(11, 5), first: TriadPlayer.Red); g.Place(0, 0); g.Place(0, 8); g.Place(0, 2);
            Assert(g.Place(0, 1).Length == 2 && g.Lasting(1) == 2 && g.Number(1, 2) == 8 && g.Lasting(8) == 0);
            // Contrary: a 2 takes a 6, but guards as a 2; under Reverse it takes with higher numbers.
            g = Game(With(TriadPerk.Contrary, 2), Five(11, 6), first: TriadPlayer.Red); g.Place(0, 0);
            Assert(g.Place(0, 1).SequenceEqual([0]) && g.Place(0, 2).SequenceEqual([1]));
            g = Game(With(TriadPerk.Contrary, 6), Five(11, 5), rules: [TriadRule.Reverse], first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            // Masked: -1 on every side, and a star lower for the Odyssey's deck limits.
            g = Game(With(TriadPerk.Masked), Five(11, 5)); g.Place(0, 4); Assert(g.Number(4, 0) == 4 && g.Number(g.Blue[0], 1, TriadPlayer.Blue, -1, true) == 4);
            var five = Card(90, 9, 9, 9, 9, 5); var masked = Card(91, 9, 9, 9, 9, 5, perk: TriadPerk.Masked);
            Assert(!OdysseyBalance.DeckAllows([five], five with { Id = 92 }, false) && OdysseyBalance.DeckAllows([five], masked, false) && OdysseyBalance.LimitStars(masked) == 4
                && !OdysseyBalance.DeckAllows([five, Card(93, 8, 8, 8, 8, 4)], masked, false));
            // Mimic: a side facing a higher number copies it (no capture on a tie); a lower one isn't copied; the copy stays and travels with copies of the game.
            g = Game(With(TriadPerk.Mimic, 3), Five(11, 7), first: TriadPlayer.Red); g.Place(0, 0);
            Assert(g.Place(0, 1).Length == 0 && g.Number(1, 3) == 7 && g.Number(1, 1) == 3 && g.Copy().Number(1, 3) == 7);
            g = Game(With(TriadPerk.Mimic, 5), Five(11, 2), first: TriadPlayer.Red); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]) && g.Number(1, 3) == 5);
            // With Odin's ties it takes what it copies; with Same both copied sides take.
            g = Game(With(TriadPerk.Mimic, 3), Five(11, 7), new TriadEdge { TiesWin = true }, first: TriadPlayer.Red); g.Place(0, 0);
            Assert(g.Takes(g.Blue[0], 1, TriadPlayer.Blue).SequenceEqual([0]) && g.Place(0, 1).SequenceEqual([0]));
            g = Game(With(TriadPerk.Mimic, 3), Five(11, 7), rules: [TriadRule.Same], first: TriadPlayer.Red); g.Place(0, 0); g.Place(0, 8); g.Place(0, 2);
            Assert(g.Place(0, 1).OrderBy(x => x).SequenceEqual([0, 2]));
            // A copy under Descension still reads exactly the number it copied (measured before the clamp).
            g = Game([.. With(TriadPerk.Mimic, 1).Select(c => c with { Type = 1 })], [.. Five(11, 7).Select(c => c with { Type = 1 })], rules: [TriadRule.Descension], first: TriadPlayer.Red); g.Place(0, 0);
            g.Place(0, 1); Assert(g.Number(1, 3) == g.Number(0, 1) && g.Number(0, 1) == 5);
            // Two traits from one scroll: Adamant (Steadfast and Aegis) is never taken; Bastion (Aegis and Cornerstone) is +2 in a corner.
            var adamant = With(TriadPerk.Steadfast | TriadPerk.Aegis);
            g = Game(adamant, Five(11, 6)); g.Place(0, 0); Assert(g.Place(0, 1).Length == 0);
            g = Game(adamant, Five(11, 5), rules: [TriadRule.Same]); g.Place(0, 0); g.Place(0, 8); g.Place(0, 2); Assert(g.Place(0, 1).Length == 0);
            g = Game(With(TriadPerk.Aegis | TriadPerk.Cornerstone), Five(11, 6), first: TriadPlayer.Red); g.Place(0, 1); Assert(g.Place(0, 0).SequenceEqual([1]));
            // Tonic edges: Shielded stops Same but not a higher number; Concealed hides a hand from All Open and a Reveal; First -2 on their opening card.
            g = Game(Five(1, 5), Five(11, 5), new TriadEdge { Shielded = true }, rules: [TriadRule.Same]); g.Place(0, 0); g.Place(0, 8); g.Place(0, 2); Assert(g.Place(0, 1).Length == 0);
            g = Game(Five(1, 5), Five(11, 6), new TriadEdge { Shielded = true }); g.Place(0, 0); Assert(g.Place(0, 1).SequenceEqual([0]));
            g = Game(Five(1, 5), Five(11, 5), new TriadEdge { Concealed = true }, new TriadEdge { Reveal = true }, rules: [TriadRule.AllOpen]);
            Assert(!g.Blue.Any(g.Open.Contains) && g.Red.All(g.Open.Contains));
            g = Game(Five(1, 5), Five(11, 6), redEdge: new TriadEdge { First = -2 }, first: TriadPlayer.Red); g.Place(0, 0);
            Assert(g.Lasting(0) == -2 && g.Place(0, 1).SequenceEqual([0]));
        });

        await check("Odyssey content: every world, Materia, Enhancement, Item and event is complete and consistent; Materia keys map to edges; the verdict lines", () =>
        {
            Assert(OdysseyContent.Worlds.Count == 14 && OdysseyContent.Worlds.Select((w, i) => w.Number == i + 1).All(x => x));
            foreach (var w in OdysseyContent.Worlds)
            {
                Assert(w.Title.Length > 0 && w.Subtitle.Length > 0 && w.Flavour.Length > 20 && w.ThemedCards.Length >= 8 && w.Duelists.Length >= 6 && w.Elites.Length >= 2);
                Assert(w.Boss.Fixed.Length == 4 && w.Boss.Fixed.All(w.ThemedCards.Contains) && w.Boss.Rules.Length >= 1 && w.Boss.Name.Length > 0 && w.DuelMusic > 0 && w.EliteMusic > 0 && w.BossMusic > 0 && w.MapMusic > 0 && w.MapMusic != w.DuelMusic);
                Assert(w.Elites.All(e => w.ThemedCards.Contains(e.SignatureCard)) && w.Boss.Materia.All(k => OdysseyContent.MateriaOf(k)?.InDuel == true));
                Assert(w.Duelists.Select(d => d.Name).Distinct().Count() == w.Duelists.Length);
            }
            Assert(OdysseyContent.Worlds[7].Boss.Materia.Contains("same-wall") && OdysseyContent.Worlds[5].Boss.Materia.Contains("shiva") && OdysseyContent.Worlds[8].Boss.Materia.Contains("carbuncle"));
            Assert(OdysseyContent.Worlds[13].Boss.Materia.Length == 2 && OdysseyContent.Worlds[13].Boss.SecondForm.Length == 4);
            Assert(OdysseyContent.AllMateria.Count == 40 && OdysseyContent.AllMateria.Select(m => m.Key).Distinct().Count() == 40 && OdysseyContent.AllMateria.All(m => m.Description.Length > 0));
            Assert(OdysseyContent.AllMateria.Count(m => m.Effect != RunEffect.None) == 7 && OdysseyContent.AllMateria.Where(m => m.Effect == RunEffect.None).All(m => m.Apply != null));
            Assert(OdysseyContent.Enhancements.Count == 27 && OdysseyContent.Items.Count == 24 && OdysseyContent.Events.Count == 9 && OdysseyContent.GuestStars.Count == 4);
            // Every perk scroll is its own (21 among them, three of two traits); keys and names are unique (the simulator maps names back to keys).
            Assert(OdysseyContent.Enhancements.Where(e => e.Kind == EnhancementKind.Perk).Select(e => e.Perk).Distinct().Count() == 21
                && OdysseyContent.Enhancements.Count(e => e.Kind == EnhancementKind.Perk && (e.Perk & (e.Perk - 1)) != 0) == 3);
            Assert(OdysseyContent.Enhancements.Select(e => e.Key).Concat(OdysseyContent.Items.Select(i => i.Key)).Distinct().Count() == 51
                && OdysseyContent.Enhancements.Select(e => e.Name).Distinct().Count() == 27 && OdysseyContent.Items.Select(i => i.Name).Distinct().Count() == 24);
            Assert(OdysseyContent.Enhancements.All(e => e.Description.Length > 0 && e.Icon.Length > 0) && OdysseyContent.Items.All(i => i.Description.Length > 0 && i.Icon.Length > 0));
            Assert(OdysseyContent.Items.Where(i => i.Kind == ItemKind.Tonic).All(i => i.Mine != null || i.Theirs != null) && OdysseyContent.Items.Where(i => i.Kind != ItemKind.Tonic).All(i => i.Mine == null && i.Theirs == null));
            // By rarity: Enhancements 7 / 10 / 7 / 3, Items 7 / 8 / 6 / 3.
            Assert(Enum.GetValues<Rarity>().Select(r => OdysseyContent.Enhancements.Count(e => e.Rarity == r)).SequenceEqual([7, 10, 7, 3])
                && Enum.GetValues<Rarity>().Select(r => OdysseyContent.Items.Count(i => i.Rarity == r)).SequenceEqual([7, 8, 6, 3]));
            Assert(OdysseyBalance.KindWeights.SequenceEqual([36.0, 34, 30]) && OdysseyBalance.OpponentPerkPool.Contains(TriadPerk.Keystone) && OdysseyBalance.OpponentPerkPool.Contains(TriadPerk.Drain)
                && !OdysseyBalance.OpponentPerkPool.Any(p => p is TriadPerk.Contrary or TriadPerk.Masked or TriadPerk.Mimic));
            // Every Materia key makes the edge the design says.
            var edge = OdysseyContent.Edge(["protect", "gravity", "fortify", "toughness", "bahamut", "final-attack", "same-wall", "odin", "wall", "carbuncle", "royal-charter"]);
            Assert(edge.Corner == 1 && edge.Centre == 2 && edge.Floor == 4 && edge.Last == 5 && edge.SameWall && edge.TiesWin && edge.Reflect == 3 && edge.All == 0);
            Assert(OdysseyContent.Edge(["libra", "eikon-attunement", "knights-of-the-round"]) is { Reveal: true, All: 1 } typed && typed.Type[1] == 2);
            Assert(OdysseyContent.Verdict(.2, 3, false).Contains("outmatched") && OdysseyContent.Verdict(0, 1, false).StartsWith("So close") && OdysseyContent.Verdict(0, 1, true).Contains("weren't with you")
                && OdysseyContent.Verdict(0, 3, true).StartsWith("A fair fight"));
            // Balance dials behave: power rises and caps, AI levels clamp, rarity weights sum to 100, prices discount and mark up.
            // The tuned curve: regulars .434 at I-1, a step at each act, elites +.05, bosses +.10 and their own cap; Reverse out of the random pool.
            Assert(OdysseyBalance.DeckPower(1, 1, false, false) is > .43 and < .44 && OdysseyBalance.DeckPower(14, 6, false, true) == OdysseyBalance.BossPowerCap && OdysseyBalance.DeckPower(14, 5, false, false) == OdysseyBalance.PowerCap);
            Assert(OdysseyBalance.DeckPower(5, 1, false, false) - OdysseyBalance.DeckPower(4, 5, false, false) > OdysseyBalance.PowerPerWorld && Math.Abs(OdysseyBalance.DeckPower(3, 2, true, false) - OdysseyBalance.DeckPower(3, 2, false, false) - OdysseyBalance.ElitePower) < 1e-9);
            Assert(OdysseyBalance.BossPower(1) < OdysseyBalance.BossPower(5) && Math.Abs(OdysseyBalance.DeckPower(1, 6, false, true) - OdysseyBalance.DeckPower(1, 6, false, false) - OdysseyBalance.BossPowerEarly) < 1e-9);
            Assert(!OdysseyBalance.RulePool(8).Contains(TriadRule.Reverse) && OdysseyBalance.RulePool(6).Contains(TriadRule.Chaos) && OdysseyBalance.RulePool(9).Contains(TriadRule.Swap) && !OdysseyBalance.RulePool(5).Contains(TriadRule.Chaos));
            Assert(OdysseyBalance.OpponentLimits(9, true) == (2, 3) && OdysseyBalance.OpponentLimits(9, false) == (1, 2) && OdysseyBalance.OpponentLimits(11, false) == (2, 3) && OdysseyBalance.OpponentLimits(13, true) == (2, 4));
            Assert(OdysseyBalance.AiLevel(1, false, false, 0) == 1 && OdysseyBalance.AiLevel(14, true, true, 4) == 12 && OdysseyBalance.AiLevel(8, false, false, 0) == 6 && OdysseyBalance.AiLevel(13, false, false, 0) == 10);
            Assert(Math.Abs(OdysseyBalance.RarityWeights(1).Sum() - 100) < 1e-9 && Math.Abs(OdysseyBalance.RarityWeights(14).Sum() - 100) < 1e-9 && OdysseyBalance.RarityWeights(14)[3] == 1 && OdysseyBalance.RarityWeights(1)[3] == .5);
            Assert(OdysseyBalance.BossLegendaryChance is > 0 and < .1);
            Assert(OdysseyBalance.Price(100, true, 0) == 75 && OdysseyBalance.Price(100, false, 7) == 125 && OdysseyBalance.RemovalPrice(2, false, 0) == 80);
            Assert(OdysseyBalance.Mgp(3, 2, false, false, false) == 24 && OdysseyBalance.Mgp(3, 2, true, false, false) == 36 && OdysseyBalance.Mgp(3, 2, false, true, true) == 72);
            Assert(OdysseyBalance.RerollPrice(0) == 20 && OdysseyBalance.RerollPrice(1) == 40 && OdysseyBalance.RerollPrice(2) == 80);
            Assert(OdysseyBalance.OpponentMateria(2, false, 0) == 0 && OdysseyBalance.OpponentMateria(5, false, 0) == 1 && OdysseyBalance.OpponentMateria(8, true, 0) == 3 && OdysseyBalance.OpponentMateria(13, true, 1) == 6);
        });

        await check("Odyssey content integrity against the real card list (skipped without HAVEN_TRIAD_CARDS): every id exists, every world builds its decks near the power targets", () =>
        {
            var path = Environment.GetEnvironmentVariable("HAVEN_TRIAD_CARDS");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Console.WriteLine("  (no real card list supplied; skipped)"); return; }
            var cards = File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => l.Split('\t'))
                .Select(f => new TriadCard(uint.Parse(f[0]), f[1], int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4]), int.Parse(f[5]), int.Parse(f[6]), int.Parse(f[7]))).ToList();
            var ids = cards.Select(c => c.Id).ToHashSet();
            foreach (var id in OdysseyContent.AllThemedIds) Assert(ids.Contains(id));
            var odyssey = new Odyssey(cards, Brains);
            odyssey.NewRun(1);
            for (var w = 1; w <= 14; w++)
            {
                var boss = odyssey.BossPlan(w);
                Assert(boss.Deck.Count == 5 && boss.Deck.Select(c => c.Id).Distinct().Count() == 5);
                Assert(Math.Abs(boss.Power - OdysseyBalance.DeckPower(w, 6, false, true)) < .06);
            }
            Assert(OdysseyContent.Worlds.All(w => w.Elites.All(e => cards.First(c => c.Id == e.SignatureCard).Stars is >= 3 or 5)));
        });

        await check("Odyssey runs are deterministic: the same seed and commands give the same run, and saving and loading mid-run changes nothing; JSON round-trips exactly", () =>
        {
            var cards = Synthetic();
            string Play(int seed, bool reload)
            {
                var o = new Odyssey(cards, Brains);
                o.NewRun(seed, 0, new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc));
                var log = new List<string>();
                for (var step = 0; step < 60 && o.Screen != OdysseyScreen.Title; step++)
                {
                    if (reload && step % 7 == 3) { var json = OdysseyJson.Save(o.Run!); o = new Odyssey(cards, Brains, o.Records, OdysseyJson.Load(json)); Assert(OdysseyJson.Save(o.Run!) == json); }
                    Drive(o, step, win: step % 4 != 1);
                    log.Add(o.Screen + ":" + OdysseyJson.Save(o.Run!));
                }
                return string.Join("\n", log);
            }
            Assert(Play(7, false) == Play(7, false) && Play(7, false) == Play(7, true) && Play(7, false) != Play(8, false));
            // The records round-trip too, and a wrong version is refused.
            var records = new OdysseyRecords { Runs = 3, BestRankCleared = 1, Recent = [new RunRecord { Won = true, World = 14, Materia = ["odin"], Verdict = "x" }] };
            var back = OdysseyJson.LoadRecords(OdysseyJson.Save(records));
            Assert(back.Runs == 3 && back.UnlockedRank == 2 && back.Recent[0].Materia[0] == "odin" && OdysseyJson.Save(back) == OdysseyJson.Save(records));
            Reject(() => OdysseyJson.Load(OdysseyJson.Save(new OdysseyRun()).Replace("\"Version\":1", "\"Version\":9")));
        });

        await check("Odyssey starting gift: three distinct Common Enhancements, no reroll, then world I (taken or skipped)", () =>
        {
            for (var seed = 0; seed < 20; seed++)
            {
                var o = new Odyssey(Synthetic(), Brains);
                o.NewRun(seed); o.Begin();
                var gift = o.Reward();
                Assert(o.Screen == OdysseyScreen.Reward && gift.Starting && gift.RerollPrice == null && gift.Choices.Count == OdysseyBalance.StartingEnhancements);
                Assert(gift.Choices.All(c => c.Kind == RewardKind.Enhancement && c.Rarity == Rarity.Common) && gift.Choices.Select(c => c.Name).Distinct().Count() == gift.Choices.Count);
                if (seed % 2 == 0) { o.SkipReward(); Assert(o.Screen == OdysseyScreen.WorldTitle && o.Run!.World == 1); continue; }
                var pick = gift.Choices.First(c => c.Enabled);
                o.PickReward(pick.Index);
                Assert(o.Screen == OdysseyScreen.Upgrade);
                var applied = false;
                foreach (var card in o.Upgrade().Binder)
                {
                    for (var choice = 0; choice < 5 && !applied; choice++)
                        if (o.UpgradePreview(card.Uid, choice).Refusal == null) { o.ApplyUpgrade(card.Uid, choice); applied = true; }
                    if (applied) break;
                }
                Assert(applied && o.Screen == OdysseyScreen.WorldTitle && o.Run!.World == 1 && o.Run.Reward == null);
                o.EnterPath(); Assert(o.Screen == OdysseyScreen.Path);
            }
        });

        await check("Odyssey deck limits: one 5★ and two of 4★+, no duplicate ids, Royal Charter one more of each; a binder that can't make a deck is refused", () =>
        {
            var cards = Synthetic();
            var o = new Odyssey(cards, Brains);
            o.NewRun(3);
            o.Begin(); o.SkipReward(); o.EnterPath(); o.ChoosePath(0);
            var run = o.Run!;
            run.Binder.Clear();
            foreach (var id in new uint[] { 7, 14, 21, 5, 10, 15, 3, 3, 1 }) run.Binder.Add(new BinderCard { Uid = run.NextUid++, Id = id });   // three 5★ (7, 14, 21), three 4★ (5, 10, 15), two of card 3 (3★), a 1★
            var uids = run.Binder.Select(b => b.Uid).ToList();
            o.ToggleDeckCard(uids[0]);
            Assert(o.DeckRefusal(uids[1])!.Contains("one five-star") && o.DeckRefusal(uids[3]) == null);
            o.ToggleDeckCard(uids[3]);
            Assert(o.DeckRefusal(uids[4])!.Contains("two cards of four stars") && o.DeckRefusal(uids[6]) == null);
            o.ToggleDeckCard(uids[6]);
            Assert(o.DeckRefusal(uids[7])!.Contains("two of one card"));
            Reject(() => o.ToggleDeckCard(uids[1]));
            // The Royal Charter: a second 5★ and a third of 4★+ (the two 5★ count), never a fourth.
            run.Materia.Add("royal-charter");
            Assert(o.DeckRefusal(uids[1]) == null && o.DeckRefusal(uids[4]) == null);
            o.ToggleDeckCard(uids[1]);
            Assert(o.DeckRefusal(uids[4])!.Contains("three cards of four stars") && o.DeckRefusal(uids[2])!.Contains("two five-star"));
            o.ToggleDeckCard(uids[8]);
            Assert(o.Deck().Deck.Count == 5 && o.Deck().CanStart && o.DeckRefusal(uids[7])!.Contains("full"));
            Assert(o.CanFormDeck(run.Binder) && !o.CanFormDeck(run.Binder.Take(4)));
            run.Materia.Clear();
            Assert(!o.CanFormDeck(run.Binder.Where(b => b.Id is 7 or 14 or 21 or 5 or 10)) && !o.DeckLegalNow);
        });

        await check("Odyssey rewards: the offer's shape by opponent, reroll prices and the Card Sharp's free one, pity, items refused when full, the boss's two stages, Treasure Hunter", () =>
        {
            var cards = Synthetic();
            var o = new Odyssey(cards, Brains);
            o.NewRun(11); o.Begin(); o.SkipReward(); o.EnterPath(); o.ChoosePath(0);
            Deck(o); o.StartDuel();
            o.Resolve(new DuelReport(DuelOutcome.Won, 3, new Dictionary<uint, int> { [1] = 2 }, 2, 1));
            var reward = o.Reward();
            Assert(reward.Choices.Count == 3 && reward.Choices[0].Kind == RewardKind.ClaimCard && reward.Claimable.Count == 5 && reward.RerollPrice == 20 && o.Run!.Mgp == 24);
            o.Run.Mgp = 200;
            var mgp = o.Run.Mgp;
            o.Reroll(); Assert(o.Run.Mgp == mgp - 20 && o.Reward().RerollPrice == 40 && o.Reward().Choices.Count == 3);
            o.Run.Materia.Add("card-sharp");
            Assert(o.Reward().RerollPrice == 0); o.Reroll(); Assert(o.Run.Mgp == mgp - 20 && o.Run.CardSharpUsed && o.Reward().RerollPrice == 80);
            o.Run.Materia.Clear();
            // Items: a full inventory refuses an Item reward (and never rolls one).
            o.Run.Items.AddRange(["potion", "ether", "elixir"]);
            o.Reroll(); Assert(o.Reward().Choices.All(c => c.Kind != RewardKind.Item));
            // Pity rises on screens without a Rare and resets on one.
            var pity = o.Run.Pity;
            Assert(pity == 0 || pity == OdysseyBalance.PityStep);
            o.Run.Pity = 40;
            Assert(o.RarityWeights(false)[2] > 40);
            // Claiming: the sub-choice then the card in the binder, enhanced as the opponent played it.
            o.PickReward(0); Assert(o.Reward().Claiming);
            o.CancelClaim(); Assert(!o.Reward().Claiming);
            o.PickReward(0);
            var before = o.Run.Binder.Count;
            var claimed = reward.Claimable[2].Card.Id;
            o.ClaimCard(2);
            Assert(o.Run.Binder.Count == before + 1 && o.Run.Binder[^1].Id == claimed && o.Run.Stats.Claimed.SequenceEqual([claimed]) && o.Screen is OdysseyScreen.Path or OdysseyScreen.Event);
            // An Enhancement no binder card can take is refused where it is offered (every card already Crowned, every side at A), so the Upgrade screen never sticks.
            Assert(o.CanApply("scroll-crown") && o.CanApply("dark-matter-1"));
            foreach (var b in o.Run.Binder) b.Perk = TriadPerk.Crown;
            Assert(!o.CanApply("scroll-crown") && o.CanApply("scroll-aegis"));
            foreach (var b in o.Run.Binder) b.Raised = [9, 9, 9, 9];
            Assert(!o.CanApply("dark-matter-1") && !o.CanApply("dark-matter-superior") && o.CanApply("glamour-prism"));
            foreach (var b in o.Run.Binder) { b.Perk = TriadPerk.None; b.Raised = [0, 0, 0, 0]; }
            // An elite's offer has four choices with a Rare or better; a Treasure Hunter adds one more.
            var elite = new Odyssey(cards, Brains);
            elite.NewRun(5); elite.Begin(); elite.SkipReward(); elite.EnterPath();
            elite.Run!.Path[0].Kind = OpponentKind.Elite;
            elite.Run.Materia.Add("treasure-hunter");
            elite.ChoosePath(0); Deck(elite); elite.StartDuel();
            elite.Resolve(new DuelReport(DuelOutcome.Won, 1, new Dictionary<uint, int>(), 0, 0));
            Assert(elite.Reward().Choices.Count == 5 && elite.Reward().Choices.Any(c => c.Rarity >= Rarity.Rare) && elite.Run.Pity == 0);
            // A boss: claim, then three Materia of Rare or better and a Materia Slot (below six slots), then the shop.
            var boss = new Odyssey(cards, Brains);
            boss.NewRun(5); boss.Begin(); boss.SkipReward(); boss.EnterPath();
            boss.Run!.Duel = 6; boss.Run.Path.Clear(); boss.Run.Path.Add(boss.BossPlan(1));
            boss.ChoosePath(0); Deck(boss); boss.StartDuel();
            boss.Resolve(new DuelReport(DuelOutcome.Won, 1, new Dictionary<uint, int>(), 0, 0));
            Assert(boss.Reward().Choices.Count == 1 && boss.Reward().Stage == 0);
            boss.PickReward(0); boss.ClaimCard(0);
            Assert(boss.Reward().Stage == 1 && boss.Reward().Choices.Count is 3 or 4 && boss.Reward().Choices.Count(c => c.Kind == RewardKind.MateriaSlot) == 1
                && boss.Reward().Choices.All(c => c.Rarity >= Rarity.Rare && c.Kind is RewardKind.Materia or RewardKind.MateriaSlot));
            boss.PickReward(0);
            Assert(boss.Screen == OdysseyScreen.Shop && (boss.Run.Materia.Count == 1 || boss.Run.Slots == 4));
            var shop = boss.Shop();
            Assert(shop.Wares.Count == 8 && shop.Pack.Count == 3 && shop.Wares.Count(w => w.Kind == WareKind.Enhancement) == 2 && shop.Wares.Count(w => w.Kind == WareKind.Item) == 3
                && shop.RemovalPrice == 40 && shop.RerollPrice == 20);
            boss.LeaveShop(); Assert(boss.Screen == OdysseyScreen.WorldTitle && boss.Run.World == 2);
        });

        await check("Odyssey losses: a Phoenix Down (then a Phoenix Pinion, once a world) deals the duel again, a draw always does, and a plain loss ends the run with the Moogle's Verdict", () =>
        {
            var cards = Synthetic();
            var o = new Odyssey(cards, Brains);
            o.NewRun(21); o.Begin(); o.SkipReward(); o.EnterPath(); o.ChoosePath(0); Deck(o);
            o.Run!.Items.Add("phoenix-down"); o.Run.Materia.Add("phoenix-pinion");
            var plan = o.StartDuel();
            Assert(plan.Blue.Count == 5 && plan.Red.Count == 5 && plan.Rules.Contains(TriadRule.SuddenDeath) && plan.LossNote.Contains("Phoenix Down"));
            o.Resolve(new DuelReport(DuelOutcome.Drawn, 0, new Dictionary<uint, int>(), 0, 0));
            Assert(o.Screen == OdysseyScreen.Duel && o.Run.Dealt!.Attempt == 2 && o.Plan().Seed != plan.Seed);
            o.Resolve(new DuelReport(DuelOutcome.Lost, -3, new Dictionary<uint, int>(), 0, 0));
            Assert(o.Screen == OdysseyScreen.Duel && !o.Run.Items.Contains("phoenix-down") && o.Run.Stats.Revives == 1 && o.Plan().LossNote.Contains("Pinion"));
            o.Resolve(new DuelReport(DuelOutcome.Lost, -1, new Dictionary<uint, int>(), 0, 0));
            Assert(o.Screen == OdysseyScreen.Duel && o.Run.PinionUsed && o.Run.Stats.Revives == 2 && o.Plan().LossNote.Contains("ends here"));
            o.Resolve(new DuelReport(DuelOutcome.Lost, -1, new Dictionary<uint, int>(), 0, 0));
            Assert(o.Screen == OdysseyScreen.GameOver && o.Run.Over && !o.Run.Won && o.Records.Runs == 1 && o.Records.BestWorld == 1);
            var summary = o.Summary();
            Assert(summary.Verdict.Length > 0 && summary.DuelsPlayed == 4 && summary.Duels.Count == 4 && summary.Duels[1].Revived == "Phoenix Down" && !summary.Won);
            o.CloseSummary(); Assert(o.Screen == OdysseyScreen.Title && !o.CanContinue && o.Title().UnlockedRank == 0);
            // A forfeit ends the run at once, Phoenix Down or not.
            var f = new Odyssey(cards, Brains);
            f.NewRun(22); f.Begin(); f.SkipReward(); f.EnterPath(); f.ChoosePath(0); Deck(f); f.Run!.Items.Add("phoenix-down"); f.StartDuel(); f.Forfeit();
            Assert(f.Screen == OdysseyScreen.GameOver && f.Run.Items.Contains("phoenix-down"));
        });

        await check("Odyssey safety nets: a binder that can't make a deck is given low-star cards; a Smoke Bomb waits until no other item is used; a replaced run is recorded", () =>
        {
            var cards = Synthetic();
            // Five 5★ cards break the limits whatever deck is chosen: facing a duel adds the weakest cards it lacks.
            var o = new Odyssey(cards, Brains);
            o.NewRun(41); o.Begin(); o.SkipReward(); o.EnterPath();
            o.Run!.Binder = [.. new uint[] { 7, 14, 21, 28, 35 }.Select((id, i) => new BinderCard { Uid = 100 + i, Id = id, Raised = [0, 0, 0, 0], Type = -1 })];
            Assert(!o.CanFormDeck(o.Run.Binder));
            o.ChoosePath(0);
            Assert(o.Screen == OdysseyScreen.Deck && o.CanFormDeck(o.Run.Binder) && o.Run.Binder.Count > 5 && o.Run.Binder.Skip(5).All(b => cards.First(c => c.Id == b.Id).Stars == 1));
            // A saved run already stuck on the Deck screen is mended when it's taken up.
            o.Run.Binder.RemoveAll(b => cards.First(c => c.Id == b.Id).Stars < 5);
            var reloaded = new Odyssey(cards, Brains, o.Records, OdysseyJson.Load(OdysseyJson.Save(o.Run)));
            reloaded.Resume();
            Assert(reloaded.CanFormDeck(reloaded.Run!.Binder));
            // A Smoke Bomb after another item would lose it with the duel.
            var m = new Odyssey(cards, Brains);
            m.NewRun(42); m.Begin(); m.SkipReward(); m.EnterPath(); m.ChoosePath(0); Deck(m);
            m.Run!.Opponent!.Kind = OpponentKind.Regular;
            m.Run.Items.AddRange(["ether", "smoke-bomb"]);
            Assert(m.ItemRefusal(1) == null);
            m.UseItem(0);
            Assert(m.ItemRefusal(0) != null);
            // Starting anew over a run under way records it as abandoned (one still on its starting binder doesn't count).
            var runs = m.Records.Runs;
            m.NewRun(43);
            Assert(m.Records.Runs == runs + 1 && m.Records.Recent[0].Verdict == "Abandoned.");
            m.NewRun(44);
            Assert(m.Records.Runs == runs + 1);
        });

        await check("Odyssey items on the Deck screen: Potion, Hi-Potion, Elixir, Megalixir, Ether, Gysahl Greens, Remedy, Echo Screen and Smoke Bomb change the deal as described", () =>
        {
            var cards = Synthetic();
            var o = new Odyssey(cards, Brains);
            o.NewRun(31); o.Begin(); o.SkipReward(); o.EnterPath(); o.ChoosePath(0); Deck(o);
            var run = o.Run!;
            run.Opponent!.Rules = [TriadRule.AllOpen, TriadRule.Same, TriadRule.SuddenDeath]; run.Opponent.Materia = ["protect"];
            run.Items.AddRange(["potion", "elixir", "ether"]);
            var target = run.Prep.Deck[0];
            Assert(o.ItemRefusal(0) != null && o.ItemRefusal(0, target) == null);
            o.UseItem(0, target); o.UseItem(0); o.UseItem(0);
            run.Items.AddRange(["gysahl-greens", "remedy", "echo-screen"]);
            Assert(o.ItemRefusal(2, rule: TriadRule.SuddenDeath) != null && o.ItemRefusal(2, rule: TriadRule.Plus) != null && o.ItemRefusal(2, rule: TriadRule.Same) == null);
            o.UseItem(0); o.UseItem(0); o.UseItem(0, rule: TriadRule.Same);
            Assert(run.Items.Count == 0 && o.Deck().Buffs.Count == 6 && o.Deck().TheirMateria.Count == 0 && !o.Deck().RulesInPlay.Contains(TriadRule.Same));
            var plan = o.StartDuel();
            var base0 = o.Card(run.Binder.First(b => b.Uid == target));
            Assert(plan.Blue[0].Top == Math.Min(10, base0.Top + 3) && plan.Blue[1].Top == Math.Min(10, o.Card(run.Binder.First(b => b.Uid == run.Prep.Deck[1])).Top + 2));
            Assert(plan.First == TriadPlayer.Blue && plan.BlueEdge.Reveal && plan.RedEdge == null && !plan.Rules.Contains(TriadRule.Same) && plan.Rules.Contains(TriadRule.SuddenDeath));
            Assert(plan.Unseen.Count == run.Binder.Count && plan.NewGame().Open.Count == 10);
            // A Megalixir undoes the first capture; a Smoke Bomb skips a regular duel for nothing.
            var m = new Odyssey(cards, Brains);
            m.NewRun(32); m.Begin(); m.SkipReward(); m.EnterPath(); m.ChoosePath(0); Deck(m);
            m.Run!.Items.AddRange(["megalixir", "smoke-bomb"]);
            m.UseItem(0); Assert(m.StartDuel().BlueEdge.Reflect == 1);
            m.Resolve(new DuelReport(DuelOutcome.Won, 2, new Dictionary<uint, int>(), 0, 0)); m.SkipReward();
            while (m.Screen == OdysseyScreen.Event) m.ChooseEvent(m.EventChoices().Count - 1, m.Run.Binder[0].Uid, 1);
            m.Run.Path[0].Kind = OpponentKind.Regular;
            m.ChoosePath(0);
            var mgp = m.Run.Mgp; var duel = m.Run.Duel;
            m.UseItem(0);
            Assert(m.Screen == OdysseyScreen.Path && m.Run.Mgp == mgp && m.Run.Duel == duel + 1 && m.Run.Items.Count == 0);
        });

        await check("Odyssey newer items: tonics on either side (kept through a Remedy and a save), Treasure Maps, Prism Powder, Queen of Cards' Favour, Ribbon and Pandora's Box", () =>
        {
            var cards = Synthetic();
            Odyssey Start(int seed, List<TriadRule> rules, List<string> materia, params string[] items)
            {
                var o = new Odyssey(cards, Brains);
                o.NewRun(seed); o.Begin(); o.SkipReward(); o.EnterPath(); o.ChoosePath(0); Deck(o);
                o.Run!.Opponent!.Rules = rules; o.Run.Opponent.Materia = materia; o.Run.Opponent.Kind = OpponentKind.Regular;
                o.Run.Items.AddRange(items);
                return o;
            }
            // Tonics: refused where they can't help or twice; Mine on blue's edge, Theirs on red's, made for them without Materia and kept through a Remedy.
            var o = Start(51, [TriadRule.AllOpen, TriadRule.Same, TriadRule.SuddenDeath], ["protect"], "holy-water", "arctic-wind", "bomb-fragment");
            Assert(o.ItemRefusal(0)!.Contains("Reverse") && o.ItemRefusal(1) == null);
            o.UseItem(1); o.UseItem(1);
            Assert(o.Run!.Prep.Tonics.SequenceEqual(["arctic-wind", "bomb-fragment"]) && o.Deck().Buffs.Any(b => b.StartsWith("Arctic Wind:")));
            var plan = o.StartDuel();
            Assert(plan.BlueEdge.Frost && plan.RedEdge is { First: -2, Corner: 1 } && o.Run.Dealt!.BlueTonics.SequenceEqual(["arctic-wind"]) && o.Run.Dealt.RedTonics.SequenceEqual(["bomb-fragment"]));
            // A save replays the tonics: the loaded run's plan has the same edges.
            var loaded = new Odyssey(cards, Brains, o.Records, OdysseyJson.Load(OdysseyJson.Save(o.Run)));
            Assert(loaded.Plan().BlueEdge.Frost && loaded.Plan().RedEdge!.First == -2);
            o = Start(52, [TriadRule.AllOpen, TriadRule.Reverse, TriadRule.SuddenDeath], ["protect"], "remedy", "bomb-fragment", "hero-drink", "hero-drink");
            Assert(o.ItemRefusal(0) == null); o.UseItem(0); o.UseItem(0); o.UseItem(0);
            Assert(o.ItemRefusal(0)!.Contains("already"));
            plan = o.StartDuel();
            Assert(plan.RedEdge is { First: -2, Corner: 0 } && plan.BlueEdge.Behind == 3);
            o = Start(53, [TriadRule.AllOpen, TriadRule.SuddenDeath], [], "bomb-fragment", "holy-water");
            Assert(o.ItemRefusal(1) != null); o.UseItem(0);
            Assert(o.StartDuel().RedEdge is { First: -2 });
            // A Treasure Map: one more choice after the win, and no Smoke Bomb while it is in use; an Ancient one makes every rolled choice Rare or better.
            o = Start(54, [TriadRule.AllOpen, TriadRule.SuddenDeath], [], "treasure-map", "smoke-bomb");
            o.UseItem(0); Assert(o.Run!.Prep.Map == Rarity.Common && o.ItemRefusal(0)!.Contains("Treasure Map"));
            o.StartDuel(); o.Resolve(new DuelReport(DuelOutcome.Won, 2, new Dictionary<uint, int>(), 0, 0));
            Assert(o.Reward().Choices.Count == 1 + OdysseyBalance.RegularRolled + 1);
            o = Start(55, [TriadRule.AllOpen, TriadRule.SuddenDeath], [], "ancient-map");
            o.UseItem(0); o.StartDuel(); o.Resolve(new DuelReport(DuelOutcome.Won, 2, new Dictionary<uint, int>(), 0, 0));
            Assert(o.Reward().Choices.Count == 4 && o.Reward().Choices.Skip(1).All(c => c.Rarity >= Rarity.Rare));
            o.Run!.Mgp = 500; o.Reroll(); Assert(o.Reward().Choices.Skip(1).All(c => c.Rarity >= Rarity.Rare));
            // On a boss the crystal offers one more Materia.
            var boss = new Odyssey(cards, Brains);
            boss.NewRun(56); boss.Begin(); boss.SkipReward(); boss.EnterPath();
            boss.Run!.Duel = 6; boss.Run.Path.Clear(); boss.Run.Path.Add(boss.BossPlan(1));
            boss.Run.Slots = OdysseyBalance.MaxSlots;   // no Materia Slot among the choices
            boss.ChoosePath(0); Deck(boss); boss.Run.Items.Add("treasure-map"); boss.UseItem(0); boss.StartDuel();
            boss.Resolve(new DuelReport(DuelOutcome.Won, 1, new Dictionary<uint, int>(), 0, 0));
            boss.PickReward(0); boss.ClaimCard(0);
            Assert(boss.Reward().Choices.Count == OdysseyBalance.BossMateriaChoices + 1 && boss.Reward().Choices.All(c => c.Kind == RewardKind.Materia));
            // Prism Powder: refused without an Open rule; hides the hand from All Open.
            o = Start(57, [TriadRule.Same, TriadRule.SuddenDeath], [], "prism-powder");
            Assert(o.ItemRefusal(0) != null);
            o = Start(57, [TriadRule.AllOpen, TriadRule.SuddenDeath], [], "prism-powder");
            o.UseItem(0); plan = o.StartDuel();
            var concealed = plan.NewGame();   // only their hand shows, including their copy of a card you also hold
            Assert(plan.BlueEdge.Concealed && o.Run!.Dealt!.Hidden && concealed.Open.SetEquals(concealed.Red));
            // Queen of Cards' Favour: only a rule of its pool that isn't in play and fits with theirs; the duel then plays it.
            o = Start(58, [TriadRule.AllOpen, TriadRule.Ascension, TriadRule.SuddenDeath], [], "queens-favour");
            Assert(o.AddableRules().SequenceEqual([TriadRule.Same, TriadRule.Plus, TriadRule.FallenAce, TriadRule.Reverse, TriadRule.Order]));
            Assert(o.ItemRefusal(0) != null && o.ItemRefusal(0, rule: TriadRule.Ascension)!.Contains("in play") && o.ItemRefusal(0, rule: TriadRule.Descension)!.Contains("fit")
                && o.ItemRefusal(0, rule: TriadRule.Chaos) != null && o.ItemRefusal(0, rule: TriadRule.Reverse) == null && o.Deck().Items[0].CanUse);
            o.UseItem(0, rule: TriadRule.Reverse);
            Assert(o.Deck().RulesInPlay.SequenceEqual([TriadRule.AllOpen, TriadRule.Ascension, TriadRule.Reverse, TriadRule.SuddenDeath]) && o.StartDuel().Rules.Contains(TriadRule.Reverse));
            // A Ribbon: their rules down to the Open rule and Sudden Death, their Materia gone; refused when there's nothing to cancel.
            o = Start(59, [TriadRule.ThreeOpen, TriadRule.Same, TriadRule.Plus, TriadRule.SuddenDeath], ["protect", "shell"], "ribbon", "remedy");
            o.UseItem(0);
            Assert(o.ItemRefusal(0)!.Contains("already") && o.Deck().TheirMateria.Count == 0);
            plan = o.StartDuel();
            Assert(plan.Rules.SequenceEqual([TriadRule.ThreeOpen, TriadRule.SuddenDeath]) && plan.RedEdge == null);
            o = Start(59, [TriadRule.AllOpen, TriadRule.SuddenDeath], [], "ribbon");
            Assert(o.ItemRefusal(0) != null);
            // Pandora's Box: a Legendary Materia not held, rolled when opened and played in the duel; refused when every one is held.
            o = Start(60, [TriadRule.AllOpen, TriadRule.SuddenDeath], [], "pandoras-box");
            o.Run!.Materia.AddRange(["odin", "alexander", "wall"]);
            o.UseItem(0);
            Assert(o.Run.Prep.Pandora == "knights-of-the-round" && o.Deck().Buffs.Any(b => b.Contains("Knights of the Round")) && o.StartDuel().BlueEdge.All == 1 && o.Run.Dealt!.BlueMateria.Contains("knights-of-the-round"));
            o = Start(60, [TriadRule.AllOpen, TriadRule.SuddenDeath], [], "pandoras-box");
            o.Run!.Materia.AddRange(["odin", "alexander", "wall", "knights-of-the-round"]);
            Assert(o.ItemRefusal(0) != null);
        });

        await check("Odyssey newer enhancements: a Mythril Whetstone shifts a side, Fantasia swaps a corner, two-trait scrolls save as both flags, and a mask lets a second 5-star into the deck", () =>
        {
            var cards = Synthetic();
            var o = new Odyssey(cards, Brains);
            o.NewRun(61); o.Begin(); o.SkipReward(); o.EnterPath(); o.ChoosePath(0);
            var run = o.Run!;
            var b = run.Binder[0]; var uid = b.Uid;
            var printed = cards.First(c => c.Id == b.Id);
            void Pending(string key) { run.Upgrade = new UpgradeState { Enhancement = key, ReturnTo = OdysseyScreen.Shop }; run.Screen = OdysseyScreen.Upgrade; }
            // Whetstone: top 5 → 7, bottom 4 → 3; at 9 it stops at A; an opposite 1 stays 1; an A refuses.
            b.Raised = [5 - printed.Top, 0, 4 - printed.Bottom, 0];
            Pending("whetstone");
            var (before, after, _, refusal) = o.UpgradePreview(uid, 0);
            Assert(refusal == null && after.Top == 7 && after.Bottom == 3 && after.Right == before.Right && after.Left == before.Left);
            b.Raised = [9 - printed.Top, 0, 1 - printed.Bottom, 0];
            after = o.UpgradePreview(uid, 0).After;
            Assert(after.Top == 10 && after.Bottom == 1);
            o.ApplyUpgrade(uid, 0);
            Assert(o.Card(b).Top == 10 && b.Raised[2] == 1 - printed.Bottom && o.Screen == OdysseyScreen.Shop);
            Pending("whetstone"); Assert(o.UpgradePreview(uid, 0).Refusal != null && o.UpgradePreview(uid, 2).Refusal == null);
            // Fantasia: the top-right corner's numbers change places; equal numbers refuse.
            b.Raised = [8 - printed.Top, 2 - printed.Right, 0, 0];
            Pending("fantasia");
            after = o.UpgradePreview(uid, 0).After;
            Assert(after.Top == 2 && after.Right == 8);
            o.ApplyUpgrade(uid, 0); Assert(o.Card(b).Top == 2 && o.Card(b).Right == 8);
            b.Raised = [6 - printed.Top, 6 - printed.Right, 0, 0];
            Pending("fantasia"); Assert(o.UpgradePreview(uid, 0).Refusal != null);
            // A two-trait scroll: both flags, saved by name.
            Pending("scroll-bastion"); o.ApplyUpgrade(uid);
            Assert(b.Perk == (TriadPerk.Aegis | TriadPerk.Cornerstone) && OdysseyJson.Save(run).Contains("Aegis, Cornerstone"));
            var back = OdysseyJson.Load(OdysseyJson.Save(run));
            Assert(back.Binder[0].Perk == (TriadPerk.Aegis | TriadPerk.Cornerstone));
            // A masked five-star joins beside another (synthetic 7 and 14 are five-star).
            run.Screen = OdysseyScreen.Deck;
            run.Prep = new DuelPrep();
            run.Binder.Clear();
            foreach (var id in new uint[] { 7, 14, 1, 2, 4 }) run.Binder.Add(new BinderCard { Uid = run.NextUid++, Id = id });
            o.ToggleDeckCard(run.Binder[0].Uid);
            Assert(o.DeckRefusal(run.Binder[1].Uid)!.Contains("five-star"));
            run.Binder[1].Perk = TriadPerk.Masked;
            Assert(o.DeckRefusal(run.Binder[1].Uid) == null && o.CanFormDeck(run.Binder));
        });

        await check("Odyssey upgrades: sides, corners, all sides, a type and perks apply with previews and refusals; a Materia beyond the slots asks which to drop", () =>
        {
            var cards = Synthetic();
            var o = new Odyssey(cards, Brains);
            o.NewRun(41); o.Begin(); o.SkipReward(); o.EnterPath(); o.ChoosePath(0); Deck(o); o.StartDuel();
            o.Resolve(new DuelReport(DuelOutcome.Won, 1, new Dictionary<uint, int>(), 0, 0));
            var run = o.Run!;
            run.Reward!.Choices[1] = new RewardChoice { Kind = RewardKind.Enhancement, Key = "dark-matter-5", Rarity = Rarity.Uncommon };
            o.PickReward(1);
            Assert(o.Screen == OdysseyScreen.Upgrade && o.Upgrade().Enhancement.Key == "dark-matter-5" && o.Upgrade().NeedsChoice);
            var uid = run.Binder[0].Uid;
            var (before, after, replaces, refusal) = o.UpgradePreview(uid, 0);
            Assert(refusal == null && !replaces && after.Top == Math.Min(10, before.Top + 1) && after.Right == Math.Min(10, before.Right + 1) && after.Bottom == before.Bottom);
            Reject(() => o.ApplyUpgrade(uid, 9));
            o.ApplyUpgrade(uid, 0);
            Assert(run.Binder[0].Raised[0] == 1 && run.Binder[0].Raised[1] == 1 && o.Screen != OdysseyScreen.Upgrade);
            // A perk replaces the old one (flagged), a side at A refuses, a Glamour Prism sets the type, all sides raise what can rise.
            run.Binder[0].Perk = TriadPerk.Guardian; run.Binder[0].Raised = [9, 9, 9, 9];
            run.Upgrade = new UpgradeState { Enhancement = "scroll-fury", ReturnTo = OdysseyScreen.Shop }; run.Screen = OdysseyScreen.Upgrade;
            Assert(o.UpgradePreview(uid).ReplacesPerk);
            o.ApplyUpgrade(uid); Assert(run.Binder[0].Perk == TriadPerk.Reckless && o.Screen == OdysseyScreen.Shop);
            run.Upgrade = new UpgradeState { Enhancement = "dark-matter-1", ReturnTo = OdysseyScreen.Shop }; run.Screen = OdysseyScreen.Upgrade;
            Assert(o.UpgradePreview(uid, 0).Refusal != null);
            run.Upgrade = new UpgradeState { Enhancement = "glamour-prism", ReturnTo = OdysseyScreen.Shop }; run.Screen = OdysseyScreen.Upgrade;
            o.ApplyUpgrade(uid, 3); Assert(o.Card(run.Binder[0]).Type == 3);
            run.Binder[1].Raised = [0, 9, 9, 9];
            run.Upgrade = new UpgradeState { Enhancement = "dark-matter-superior", ReturnTo = OdysseyScreen.Shop }; run.Screen = OdysseyScreen.Upgrade;
            o.ApplyUpgrade(run.Binder[1].Uid); Assert(run.Binder[1].Raised[0] == 1 && o.Card(run.Binder[1]).Right == 10);
            // Materia: three slots fill, the fourth asks; keeping drops one, discarding keeps the slots.
            run.Materia.AddRange(["protect", "shell", "gravity"]);
            run.Reward = new RewardState { Choices = [new RewardChoice { Kind = RewardKind.Materia, Key = "odin", Rarity = Rarity.Legendary }] }; run.Screen = OdysseyScreen.Reward;
            o.PickReward(0);
            Assert(o.Screen == OdysseyScreen.MateriaSwap && o.MateriaSwap().New.Key == "odin" && o.MateriaSwap().Slots.Count == 3);
            o.KeepMateria(1); Assert(run.Materia.SequenceEqual(["protect", "odin", "gravity"]) && run.Stats.MateriaHistory.Any(h => h.Contains("dropped Shell")));
            run.Reward = new RewardState { Choices = [new RewardChoice { Kind = RewardKind.Materia, Key = "wall", Rarity = Rarity.Legendary }] }; run.Screen = OdysseyScreen.Reward;
            o.PickReward(0); o.DiscardNewMateria(); Assert(run.Materia.Count == 3 && !run.Materia.Contains("wall"));
            run.Reward = new RewardState { Choices = [new RewardChoice { Kind = RewardKind.MateriaSlot, Key = OdysseyContent.MateriaSlotKey, Rarity = Rarity.Rare }] }; run.Screen = OdysseyScreen.Reward;
            o.PickReward(0); Assert(run.Slots == 4);
        });

        await check("Odyssey: until an Enhancement or a full-slots Materia is applied, the player can go back to the gift, the rewards, the shop (gil back) or the workshop", () =>
        {
            var o = new Odyssey(Synthetic(), Brains);
            o.NewRun(7); o.Begin();
            Assert(!o.CanGoBack);
            Reject(() => o.GoBack());
            // The starting gift: back to the three gifts, unchanged, and another can be taken.
            var gift = o.Reward().Choices.Select(c => c.Name).ToArray();
            o.PickReward(0);
            Assert(o.Screen == OdysseyScreen.Upgrade && o.CanGoBack && o.Upgrade().Back == "Back to the gifts");
            o.GoBack();
            Assert(o.Screen == OdysseyScreen.Reward && o.Reward().Starting && o.Reward().Choices.Select(c => c.Name).SequenceEqual(gift) && o.Run!.Upgrade == null);
            o.SkipReward(); o.EnterPath(); o.ChoosePath(0); Deck(o); o.StartDuel();
            o.Resolve(new DuelReport(DuelOutcome.Won, 1, new Dictionary<uint, int>(), 0, 0));
            var run = o.Run!;
            // A duel's reward: back to the same choices, the reroll price untouched.
            run.Reward!.Choices[1] = new RewardChoice { Kind = RewardKind.Enhancement, Key = "dark-matter-1", Rarity = Rarity.Common };
            var reroll = o.Reward().RerollPrice; var gil = run.Mgp;
            o.PickReward(1);
            Assert(o.Upgrade().Back == "Back to the rewards");
            o.GoBack();
            Assert(o.Screen == OdysseyScreen.Reward && o.Reward().RerollPrice == reroll && run.Mgp == gil && run.Reward.Choices[1].Key == "dark-matter-1");
            // A Materia with the slots full: back to the rewards rather than losing it.
            run.Materia.AddRange(["protect", "shell", "gravity"]);
            run.Reward.Choices[1] = new RewardChoice { Kind = RewardKind.Materia, Key = "odin", Rarity = Rarity.Legendary };
            o.PickReward(1);
            Assert(o.Screen == OdysseyScreen.MateriaSwap && o.MateriaSwap().Back == "Back to the rewards");
            o.GoBack();
            Assert(o.Screen == OdysseyScreen.Reward && run.Swap == null && run.Materia.Count == 3 && !run.Materia.Contains("odin"));
            // The shop: the price comes back and the ware is for sale again, for an Enhancement and a Materia alike.
            run.Shop = new ShopState { Wares = [new Ware { Kind = WareKind.Enhancement, Key = "dark-matter-1", Rarity = Rarity.Common, Price = 40 },
                new Ware { Kind = WareKind.Materia, Key = "wall", Rarity = Rarity.Rare, Price = 90 }] };
            run.Screen = OdysseyScreen.Shop; run.Mgp = 200; var spent = run.Stats.MgpSpent;
            o.Buy(0);
            Assert(o.Screen == OdysseyScreen.Upgrade && run.Mgp == 160 && run.Shop.Wares[0].Sold && o.Upgrade().Back == "Back · 40 gil back");
            o.GoBack();
            Assert(o.Screen == OdysseyScreen.Shop && run.Mgp == 200 && !run.Shop.Wares[0].Sold && run.Stats.MgpSpent == spent && o.BuyRefusal(0) == null);
            o.Buy(1);
            Assert(o.Screen == OdysseyScreen.MateriaSwap && run.Mgp == 110 && o.MateriaSwap().Back == "Back · 90 gil back");
            o.GoBack();
            Assert(o.Screen == OdysseyScreen.Shop && run.Mgp == 200 && !run.Shop.Wares[1].Sold && run.Stats.MgpSpent == spent);
            // Once applied there is no going back.
            o.Buy(0); o.ApplyUpgrade(run.Binder[0].Uid, 0);
            Assert(o.Screen == OdysseyScreen.Shop && run.Mgp == 160 && run.Shop.Wares[0].Sold && !o.CanGoBack);
            // Held Items sell back for half their price, freeing a slot to buy another (three held block buying).
            run.Items.Clear(); run.Items.AddRange(["potion", "ether", "phoenix-down"]);
            run.Shop.Wares.Add(new Ware { Kind = WareKind.Item, Key = "potion", Rarity = Rarity.Common, Price = 40 });
            Assert(o.BuyRefusal(2) == "You already hold three Items.");
            var resale = o.SellPrice(1);
            Assert(resale == Math.Max(1, o.Shop().SellPrices[1]) && resale > 0 && o.Shop().SellPrices.Count == 3);
            var before = run.Mgp; var earned = run.Stats.MgpEarned;
            o.SellItem(1);
            Assert(run.Items.SequenceEqual(["potion", "phoenix-down"]) && run.Mgp == before + resale && run.Stats.MgpEarned == earned + resale && o.BuyRefusal(2) == null);
            // The workshop's paid job: back to its offer with the gil returned.
            run.Event = new EventState { Kind = EventKind.Workshop }; run.Screen = OdysseyScreen.Event; run.Mgp = 500;
            o.ChooseEvent(1);
            Assert(o.Screen == OdysseyScreen.Upgrade && run.Mgp == 500 - OdysseyBalance.WorkshopAllSidesPrice && o.Upgrade().Back == "Back");
            o.GoBack();
            Assert(o.Screen == OdysseyScreen.Event && run.Mgp == 500 && run.Event.Result == null);
            // A run saved on the Upgrade screen keeps what going back gives back.
            o.ChooseEvent(1);
            var saved = OdysseyJson.Load(OdysseyJson.Save(run));
            Assert(saved.Upgrade!.Paid == OdysseyBalance.WorkshopAllSidesPrice && saved.Upgrade.Ware == -1);
        });

        await check("Odyssey limits: a card takes three Enhancements at most (rewards, shop, workshop, shrine), one Legendary Materia is slotted (two with the Master Materia, found once a run), and the shop buys one card a visit, priced by stars and Enhancements, never one the deck needs", () =>
        {
            var o = new Odyssey(Synthetic(), Brains);
            o.NewRun(11); o.Begin(); o.SkipReward();
            var run = o.Run!;
            // Three Enhancements, then every one is refused for that card; a card under the cap still takes them.
            var card = run.Binder[0];
            foreach (var key in new[] { "dark-matter-1", "dark-matter-5", "scroll-fury" })
            {
                run.Upgrade = new UpgradeState { Enhancement = key, ReturnTo = OdysseyScreen.Shop }; run.Screen = OdysseyScreen.Upgrade;
                var choice = Enumerable.Range(0, 4).First(c => o.UpgradePreview(card.Uid, c).Refusal == null);
                o.ApplyUpgrade(card.Uid, choice);
            }
            Assert(card.Enhancements == 3 && o.Binder().First(c => c.Uid == card.Uid).Enhancements == 3);
            run.Upgrade = new UpgradeState { Enhancement = "dark-matter-superior", ReturnTo = OdysseyScreen.Shop }; run.Screen = OdysseyScreen.Upgrade;
            Assert(o.UpgradePreview(card.Uid).Refusal is { } full && full.Contains("3 Enhancements"));
            Reject(() => o.ApplyUpgrade(card.Uid));
            Assert(o.UpgradePreview(run.Binder[1].Uid).Refusal == null);
            run.Upgrade = null;
            // With every card at the cap, rewards, wares and the workshop refuse Enhancements, and the shrine refuses a blessing.
            foreach (var b in run.Binder) b.Enhancements = OdysseyBalance.MaxEnhancements;
            Assert(!o.CanApply("dark-matter-1") && !o.CanApply("scroll-crown"));
            run.Reward = new RewardState { Choices = [new RewardChoice { Kind = RewardKind.Enhancement, Key = "dark-matter-1", Rarity = Rarity.Common }] }; run.Screen = OdysseyScreen.Reward;
            Assert(o.RewardRefusal(0) == "No card of yours can take it.");
            run.Shop = new ShopState { Wares = [new Ware { Kind = WareKind.Enhancement, Key = "dark-matter-1", Rarity = Rarity.Common, Price = 1 }] }; run.Screen = OdysseyScreen.Shop; run.Mgp = 500;
            Assert(o.BuyRefusal(0) == "No card of yours can take it.");
            run.Event = new EventState { Kind = EventKind.Workshop }; run.Screen = OdysseyScreen.Event;
            Assert(o.EventChoices().Take(2).All(c => !c.Enabled));
            run.Event = new EventState { Kind = EventKind.Shrine };
            Assert(o.EventCardRefusal(1, card.Uid) != null);
            run.Binder[1].Enhancements = 2;
            Assert(o.EventCardRefusal(1, run.Binder[1].Uid) == null);
            o.ChooseEvent(1, run.Binder[1].Uid, 2);
            Assert(run.Binder[1].Enhancements == 3 && o.Card(run.Binder[1]).Type == 2);
            run.Event = null;
            foreach (var b in run.Binder) b.Enhancements = 0;

            // One Legendary Materia: a second, with a slot free, asks to replace the first (and only the first) or to let it go.
            run.Materia.Clear(); run.Materia.AddRange(["protect", "odin"]); run.Slots = 4;
            run.Reward = new RewardState { Choices = [new RewardChoice { Kind = RewardKind.Materia, Key = "wall", Rarity = Rarity.Legendary }] }; run.Screen = OdysseyScreen.Reward;
            Assert(o.LegendariesFull && o.LegendaryLimit == 1 && o.Reward().Choices[0].Enabled && o.Reward().Choices[0].Description.Contains("only 1 Legendary"));
            o.PickReward(0);
            var swap = o.MateriaSwap();
            Assert(o.Screen == OdysseyScreen.MateriaSwap && swap.LegendaryLimited && swap.Refusals[0] != null && swap.Refusals[1] == null);
            Reject(() => o.KeepMateria(0));
            o.KeepMateria(1);
            Assert(run.Materia.SequenceEqual(["protect", "wall"]));
            // An ordinary Materia still fills a free slot.
            run.Reward = new RewardState { Choices = [new RewardChoice { Kind = RewardKind.Materia, Key = "shell", Rarity = Rarity.Common }] }; run.Screen = OdysseyScreen.Reward;
            o.PickReward(0);
            Assert(run.Materia.SequenceEqual(["protect", "wall", "shell"]));
            // The Master Materia: two Legendaries, and it is never offered again once taken.
            run.Reward = new RewardState { Choices = [new RewardChoice { Kind = RewardKind.MasterMateria, Key = OdysseyContent.MasterMateriaKey, Rarity = Rarity.Legendary }] }; run.Screen = OdysseyScreen.Reward;
            Assert(o.Reward().Choices[0].Name == "Master Materia" && o.Reward().Choices[0].Icon != "" && o.Reward().Choices[0].Enabled);
            o.PickReward(0);
            Assert(run.MasterMateria && o.LegendaryLimit == 2 && !o.LegendariesFull && !run.Materia.Contains(OdysseyContent.MasterMateriaKey));
            run.Reward = new RewardState { Choices = [new RewardChoice { Kind = RewardKind.Materia, Key = "odin", Rarity = Rarity.Legendary }] }; run.Screen = OdysseyScreen.Reward;
            o.PickReward(0);
            Assert(run.Materia.SequenceEqual(["protect", "wall", "shell", "odin"]) && o.LegendariesFull);
            run.Reward = new RewardState { Choices = [new RewardChoice { Kind = RewardKind.MasterMateria, Key = OdysseyContent.MasterMateriaKey, Rarity = Rarity.Legendary }] }; run.Screen = OdysseyScreen.Reward;
            Assert(o.RewardRefusal(0) != null);
            // Rolled Legendaries offer it until it is taken, at its low weight (about one Legendary Materia choice in nine).
            var fresh = new Odyssey(Synthetic(), Brains);
            fresh.NewRun(12); fresh.Begin(); fresh.SkipReward(); fresh.EnterPath(); fresh.ChoosePath(0); Deck(fresh); fresh.StartDuel();
            fresh.Resolve(new DuelReport(DuelOutcome.Won, 1, new Dictionary<uint, int>(), 0, 0));
            var f = fresh.Run!;
            Assert(fresh.Screen == OdysseyScreen.Reward && f.Opponent != null);
            f.Opponent!.Event = EventKind.Gilgamesh;   // rerolls roll Legendaries only
            f.Items.Clear(); f.Items.AddRange(["potion", "ether", "gysahl-greens"]);   // and no Items
            var masters = 0; var legendaries = 0;
            for (var i = 0; i < 400; i++)
            {
                f.Mgp = 100000; f.Reward!.Rerolls = 0;
                fresh.Reroll();
                foreach (var c in f.Reward.Choices.Where(c => c.Kind is RewardKind.Materia or RewardKind.MasterMateria)) { legendaries++; if (c.Kind == RewardKind.MasterMateria) masters++; }
            }
            Assert(masters > 0 && masters < legendaries / 4);
            f.MasterMateria = true;
            for (var i = 0; i < 100; i++) { f.Mgp = 100000; f.Reward!.Rerolls = 0; fresh.Reroll(); Assert(f.Reward.Choices.All(c => c.Kind != RewardKind.MasterMateria)); }

            // Selling a card: by printed stars and Enhancements, one a visit, never one the binder needs.
            run.Shop = new ShopState(); run.Screen = OdysseyScreen.Shop; run.Mgp = 0;
            var spare = run.Binder.OrderBy(b => b.Uid).First(b => o.CardSaleRefusal(b.Uid) == null);
            spare.Enhancements = 2;
            var stars = o.Binder().First(c => c.Uid == spare.Uid).Base.Stars;
            var price = o.CardSalePrice(spare.Uid);
            Assert(price == OdysseyBalance.CardSaleStars[stars] + 2 * OdysseyBalance.CardSaleEnhancement && o.Shop().ForSale.First(x => x.Card.Uid == spare.Uid).Price == price);
            Assert(OdysseyBalance.CardSalePrice(1, 0) < OdysseyBalance.CardSalePrice(5, 0) && OdysseyBalance.CardSalePrice(5, 3) <= OdysseyBalance.PackPrice + 10);
            var count = run.Binder.Count; var earned = run.Stats.MgpEarned;
            o.SellCard(spare.Uid);
            Assert(run.Binder.Count == count - 1 && run.Mgp == price && run.Stats.MgpEarned == earned + price && run.Shop.CardSold && o.Shop().CardSold);
            Assert(o.CardSaleRefusal(run.Binder[0].Uid) == "The shop buys one card a visit.");
            Reject(() => o.SellCard(run.Binder[0].Uid));
            // A new visit buys again, but not a card the binder can't make a deck without.
            run.Shop = new ShopState();
            while (run.Binder.Count > OdysseyBalance.DeckSize) run.Binder.RemoveAt(run.Binder.Count - 1);
            Assert(o.CanFormDeck(run.Binder) && run.Binder.All(b => o.CardSaleRefusal(b.Uid) == "The binder couldn't make a deck without it."));
            Reject(() => o.SellCard(run.Binder[0].Uid));
            // The sold flag and the counts survive a save.
            run.Shop.CardSold = true; run.Binder[0].Enhancements = 2;
            var saved = OdysseyJson.Load(OdysseyJson.Save(run));
            Assert(saved.Shop!.CardSold && saved.Binder[0].Enhancements == 2 && saved.MasterMateria);
        });

        await check("Odyssey boss signatures: no boss's signature card is offered, dealt to another opponent or owned before the run reaches that boss's world, and each boss's deck holds its own", () =>
        {
            Assert(OdysseyContent.Worlds.All(w => OdysseyContent.SignatureWorld[w.Boss.Fixed[0]] >= w.Number));
            // No elite carries a signature of its own world's boss or a later one.
            Assert(OdysseyContent.Worlds.All(w => w.Elites.All(e => !OdysseyContent.SignatureWorld.TryGetValue(e.SignatureCard, out var signed) || signed < w.Number)));
            var cards = Synthetic();
            bool Early(uint id, int world) => OdysseyContent.SignatureWorld.TryGetValue(id, out var signed) && signed >= world;
            for (var seed = 300; seed < 306; seed++)
            {
                var o = new Odyssey(cards, Brains);
                o.NewRun(seed);
                for (var step = 0; step < 4000 && o.Screen != OdysseyScreen.Title; step++)
                {
                    var run = o.Run!;
                    if (run.World > 0)
                    {
                        foreach (var plan in run.Path.Append(run.Opponent).OfType<OpponentPlan>().Where(p => !p.IsBoss))
                            Assert(plan.Deck.All(c => !Early(c.Id, run.World)));
                        if (run.Shop is { } shop) Assert(shop.Pack.All(c => !Early(c.Id, run.World)));
                        // On the path (before the world's boss; a boss's own card may be claimed once it is beaten).
                        if (o.Screen == OdysseyScreen.Path) Assert(run.Binder.All(b => !Early(b.Id, run.World)));
                        if (run.Opponent is { IsBoss: true } boss && boss.Name == OdysseyContent.WorldAt(run.World).Boss.Name)
                            Assert(boss.Deck[0].Id == OdysseyContent.WorldAt(run.World).Boss.Fixed[0] && boss.Signature == boss.Deck[0].Id);
                    }
                    Drive(o, step, win: true);
                }
                Assert(o.Run!.Won);
            }
        });

        await check("Odyssey full runs: headless runs with a plain brain end cleanly for many seeds, and forced wins cross all fourteen worlds, every shop, the events and the Endsinger's second form", () =>
        {
            var cards = Synthetic();
            var ended = 0;
            for (var seed = 100; seed < 112; seed++)
            {
                var o = new Odyssey(cards, Brains);
                o.NewRun(seed);
                for (var step = 0; step < 2000 && o.Screen != OdysseyScreen.Title; step++) Drive(o, step, win: null);
                if (!o.Run!.Over) throw new Exception($"Run {seed} not over after 2000 steps: {o.Screen}, world {o.Run.World} duel {o.Run.Duel}, {o.Run.Stats.DuelsPlayed} duels, binder {o.Run.Binder.Count}, reward {string.Join("/", o.Run.Reward?.Choices.Select(c => c.Key) ?? [])}");
                ended++;
            }
            Assert(ended == 12);
            // Forced wins: the run reaches the victory through every world; events of every kind appear across the seeds.
            var kinds = new HashSet<EventKind>();
            var wons = 0;
            for (var seed = 200; seed < 216; seed++)
            {
                var records = new OdysseyRecords { BestRankCleared = 9 };
                var o = new Odyssey(cards, Brains, records);
                o.NewRun(seed, rank: seed % 2 == 0 ? 10 : 0);
                var shops = new HashSet<int>(); var worlds = new HashSet<int>();
                for (var step = 0; step < 4000 && o.Screen != OdysseyScreen.Title; step++)
                {
                    if (o.Screen == OdysseyScreen.Event) kinds.Add(o.Run!.Event!.Kind);
                    // The merchant never sells a Legendary Item.
                    if (o.Screen == OdysseyScreen.Event && o.Run!.Event!.Kind == EventKind.Merchant) Assert(o.Run.Event.Wares.All(k => OdysseyContent.ItemOf(k)!.Rarity <= OdysseyBalance.MerchantMaxRarity));
                    if (o.Screen == OdysseyScreen.Shop) shops.Add(o.Run!.World);
                    if (o.Run!.World > 0) worlds.Add(o.Run.World);
                    Drive(o, step, win: true);
                }
                if (!(o.Run!.Over && o.Run.Won && o.Screen == OdysseyScreen.Title)) throw new Exception($"Run {seed} stopped at {o.Screen} world {o.Run.World} duel {o.Run.Duel} over {o.Run.Over} won {o.Run.Won}: {o.Run.Verdict}");
                if (!(shops.Count == 13 && worlds.Count == 14 && o.Run.Stats.DuelsWon >= 84)) throw new Exception($"Run {seed}: shops {shops.Count}, worlds {worlds.Count}, won {o.Run.Stats.DuelsWon} of {o.Run.Stats.DuelsPlayed}");
                Assert(seed % 2 == 1 || o.Run.SecondFormDone);
                Assert(records.Clears == 1 && records.BestRankCleared >= (seed % 2 == 0 ? 10 : 9) && records.Recent.Count == 1 && records.Recent[0].Won);
                wons++;
            }
            Assert(wons == 16 && kinds.Count >= 7);
        });
    }

    // Fills the deck with the strongest allowed cards (keeping those already in it: toggling one would take it out).
    private static void Deck(Odyssey o)
    {
        foreach (var card in o.Binder().OrderByDescending(c => c.Power)) if (o.Run!.Prep.Deck.Count < 5 && !o.Run.Prep.Deck.Contains(card.Uid) && o.DeckRefusal(card.Uid) == null) o.ToggleDeckCard(card.Uid);
    }

    // A scripted player for every screen: takes the first choice that is allowed. Duels are played by the plain brain (win: null)
    // or reported as won or lost outright.
    private static void Drive(Odyssey o, int step, bool? win)
    {
        var run = o.Run!;
        switch (o.Screen)
        {
            case OdysseyScreen.StartingBinder: if (run.ReDealsLeft > 0 && step % 2 == 0) o.ReDeal(); else o.Begin(); break;
            case OdysseyScreen.WorldTitle: o.EnterPath(); break;
            case OdysseyScreen.Path: o.ChoosePath(step % run.Path.Count); break;
            case OdysseyScreen.Deck:
                if (run.Prep.Deck.Count < 5) Deck(o);
                for (var i = run.Items.Count - 1; i >= 0; i--)
                    if (o.ItemRefusal(i, run.Prep.Deck[0], run.Opponent!.Rules.FirstOrDefault(r => r != TriadRule.SuddenDeath)) == null && OdysseyContent.ItemOf(run.Items[i])!.Kind != ItemKind.SmokeBomb)
                        o.UseItem(i, run.Prep.Deck[0], run.Opponent!.Rules.FirstOrDefault(r => r != TriadRule.SuddenDeath));
                o.StartDuel();
                break;
            case OdysseyScreen.Duel:
                if (win is { } w) o.Resolve(new DuelReport(w ? DuelOutcome.Won : DuelOutcome.Lost, w ? 1 + step % 5 : -1, new Dictionary<uint, int> { [run.Dealt!.Blue[0].Id] = 1 + step % 3 }, 1 + step % 3, run.Dealt.Blue[0].Id));
                // Synthetic decks of identical flat cards can draw for ever (no capture is ever possible); after six deals, call it won.
                else if (run.Dealt!.Attempt > 6) o.Resolve(new DuelReport(DuelOutcome.Won, 1, new Dictionary<uint, int>(), 0, 0));
                else o.Resolve(o.Plan().Play(new PlainBrain()));
                break;
            case OdysseyScreen.Reward:
                var reward = o.Reward();
                if (reward.Claiming) o.ClaimCard(step % reward.Claimable.Count);
                else if (reward.CanReroll && step % 9 == 0) o.Reroll();
                else
                {
                    var pick = reward.Choices.Where(c => c.Enabled).Skip(step % 2).FirstOrDefault() ?? reward.Choices.FirstOrDefault(c => c.Enabled);
                    if (pick != null) o.PickReward(pick.Index); else o.SkipReward();
                }
                break;
            case OdysseyScreen.Upgrade:
                foreach (var card in o.Upgrade().Binder)
                    for (var choice = 0; choice < 5; choice++)
                        if (o.UpgradePreview(card.Uid, choice).Refusal == null) { o.ApplyUpgrade(card.Uid, choice); return; }
                // Nothing can take it (every side an A): a new card from the opponent would, so give one a lower side.
                run.Binder[0].Raised = [0, 0, 0, 0]; run.Binder[0].Type = -1; run.Binder[0].Enhancements = 0;
                o.ApplyUpgrade(run.Binder[0].Uid, 1);
                break;
            case OdysseyScreen.MateriaSwap:
                var open = Enumerable.Range(0, run.Materia.Count).Where(i => o.KeepRefusal(i) == null).ToList();
                if (step % 3 == 0 || open.Count == 0) o.DiscardNewMateria(); else o.KeepMateria(open[step % open.Count]);
                break;
            case OdysseyScreen.Event:
                var choices = o.EventChoices();
                var index = Enumerable.Range(0, choices.Count).Where(i => choices[i].Enabled).Skip(step % 2).FirstOrDefault(choices.Count - 1);
                if (!choices[index].Enabled) index = choices.Count - 1;
                var target = run.Binder.Select(b => b.Uid).Where(uid => o.EventCardRefusal(index, uid) == null).ToList();
                if (choices[index].NeedsCard && target.Count == 0) index = choices.Count - 1;
                o.ChooseEvent(index, target.Count == 0 ? -1 : target[step % target.Count], 1 + step % 4);
                break;
            case OdysseyScreen.Shop:
                var shop = o.Shop();
                var ware = shop.Wares.FirstOrDefault(x => x.CanBuy && x.Kind != WareKind.Pack);
                if (ware != null) o.Buy(ware.Index);
                else if (!shop.PackSold && shop.Wares.Any(x => x.Kind == WareKind.Pack && x.CanBuy)) o.BuyPackCard(step % 3);
                else if (shop.Binder.Any(c => c.Allowed) && step % 5 == 0) o.RemoveCard(shop.Binder.First(c => c.Allowed).Uid);
                else if (step % 11 == 0 && run.Mgp >= shop.RerollPrice + 100) o.RerollShop();
                else o.LeaveShop();
                break;
            case OdysseyScreen.Victory or OdysseyScreen.GameOver: o.CloseSummary(); break;
        }
    }

    private static void Assert(bool value, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) { if (!value) throw new Exception($"Odyssey assertion failed (OdysseyTests.cs line {line})."); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected a refusal.");
    }
}
