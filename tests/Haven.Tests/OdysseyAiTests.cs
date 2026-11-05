using System.Diagnostics;
using Haven.Core;
using Haven.Core.Odyssey;

internal static class OdysseyAiTests
{
    private static readonly TriadRule[] Rules =
        [TriadRule.Roulette, TriadRule.AllOpen, TriadRule.ThreeOpen, TriadRule.Same, TriadRule.SuddenDeath, TriadRule.Plus, TriadRule.Order, TriadRule.Chaos,
         TriadRule.Reverse, TriadRule.FallenAce, TriadRule.Ascension, TriadRule.Descension, TriadRule.Swap];

    public static async Task Run(Func<string, Action, Task> check)
    {
        TriadCard Card(uint id, Random r, TriadPerk perk = TriadPerk.None) =>
            new(id, "Card " + id, r.Next(1, 11), r.Next(1, 11), r.Next(1, 11), r.Next(1, 11), 1 + r.Next(5), r.Next(5)) { Perk = perk };
        // Synthetic cards (the checks run without game data): a pool of 60, and the opponent's "binder" the brain samples from.
        var pool = Enumerable.Range(1, 60).Select(i => Card((uint)i, new Random(i))).ToArray();
        TriadCard[] Deal(Random r) => pool.OrderBy(_ => r.Next()).Take(10).ToArray();
        TriadRule[] SomeRules(Random r)
        {
            var rules = new List<TriadRule>();
            for (var n = r.Next(4); n > 0; n--)
            {
                var rule = Rules[r.Next(Rules.Length)];
                if (rules.All(x => TriadGame.Compatible(x, rule))) rules.Add(rule);
            }
            return [.. rules];
        }
        TriadEdge? SomeEdge(Random r)
        {
            if (r.Next(3) == 0) return null;
            var edge = new TriadEdge
            {
                All = r.Next(2), Corner = r.Next(2), Side = r.Next(2), Centre = r.Next(3), Common = r.Next(2), Rare = r.Next(2), Floor = r.Next(2) == 0 ? 0 : 3 + r.Next(3),
                Attack = r.Next(2), Defend = r.Next(2), First = r.Next(3), Last = r.Next(4), Behind = r.Next(3), Momentum = r.Next(2), Reflect = r.Next(3),
                Same = r.Next(4) == 0, Plus = r.Next(4) == 0, SameWall = r.Next(5) == 0, PlusWall = r.Next(5) == 0, IgnoreReverse = r.Next(4) == 0, FreeChoice = r.Next(4) == 0,
                Ascend = r.Next(4) == 0, CornerGuard = r.Next(5) == 0, Frost = r.Next(4) == 0, Bond = r.Next(4) == 0, TiesWin = r.Next(6) == 0, Reveal = r.Next(4) == 0,
            };
            edge.Type[1 + r.Next(4)] = r.Next(3);
            return edge;
        }
        // Plays a game out between two brains (blue's, red's), each on a copy with its own seeded generator, checking every move is legal.
        TriadGame Play(TriadGame game, ITriadBrain blue, ITriadBrain red, Random seeds)
        {
            while (!game.Over)
            {
                var brain = game.Turn == TriadPlayer.Blue ? blue : red;
                var (hand, cell) = brain.Choose(game.Copy(), new Random(seeds.Next()));
                Assert(game.Playable(hand) && cell is >= 0 and <= 8 && game.Board[cell] is null);
                game.PlaceCard(hand, cell);
            }
            return game;
        }

        await check("The Odyssey opponent's levels follow the design table, and it plays only legal moves under every rule, Materia and card perk", () =>
        {
            Assert(OdysseyAi.For(1) == new AiSkill(1, 0, .35, .6) && OdysseyAi.For(6) == new AiSkill(3, 5, .08, .2) && OdysseyAi.For(12) == new AiSkill(4, 7, .02, .05));
            Assert(OdysseyAi.For(0) == OdysseyAi.For(1) && OdysseyAi.For(99) == OdysseyAi.For(12) && new OdysseyAi(1).Samples == 4 && new OdysseyAi(12).Samples == 8);
            var random = new Random(11);
            for (var round = 0; round < 70; round++)
            {
                var deck = Deal(random);
                // Perks on some cards; hands that may repeat a card (a binder can hold two of one card).
                var blue = deck[..5].Select(c => random.Next(3) == 0 ? c with { Perk = (TriadPerk)(1 << random.Next(9)) } : c).ToArray();
                var red = deck[5..].Select(c => random.Next(3) == 0 ? c with { Perk = (TriadPerk)(1 << random.Next(9)) } : c).ToArray();
                if (round % 10 == 0) red[4] = red[0];
                var rules = SomeRules(random);
                var game = new TriadGame(blue, red, random.Next(2) == 0 ? TriadPlayer.Blue : TriadPlayer.Red, rules, new Random(round), SomeEdge(random), SomeEdge(random));
                // Mostly the cheap levels, so the check stays quick; the strong ones now and then, with and without a pool to sample from.
                int Level() => random.Next(4) == 0 ? 9 + random.Next(4) : 1 + random.Next(8);
                var played = Play(game, new OdysseyAi(Level(), round % 3 == 0 ? null : pool), new OdysseyAi(Level(), pool), random);
                Assert(played.Over && played.Score(TriadPlayer.Blue) + played.Score(TriadPlayer.Red) >= 10);
                // Sudden Death: a drawn game dealt again is played on legally from the new deal.
                if (played.Winner == null && played.RestartSuddenDeath()) Play(played, new OdysseyAi(Level()), new OdysseyAi(Level(), pool), random);
            }
            // A board with one cell left, and a hand bound by Order or Chaos: still a legal move.
            var bound = new TriadGame(Five(1), Five(11), TriadPlayer.Blue, [TriadRule.Order, TriadRule.AllOpen], new Random(1));
            Play(bound, new OdysseyAi(12), new OdysseyAi(5), random);
            var chaos = new TriadGame(Five(1), Five(11), TriadPlayer.Blue, [TriadRule.Chaos], new Random(2));
            Play(chaos, new OdysseyAi(12, pool), new OdysseyAi(1, pool), random);
            TriadCard[] Five(uint first) => [.. Enumerable.Range(0, 5).Select(i => Card(first + (uint)i, new Random(i)))];
        });

        await check("The Odyssey opponent's exact endgame agrees with brute force, it ignores the other player's hidden cards, and a move stays quick at level 12", () =>
        {
            // Exact play (no blunders or noise) in positions with few empty cells, every hand open: the move it picks is worth
            // as much as the best move by a full minimax, under assorted rules and Materia.
            var random = new Random(21);
            var perfect = new OdysseyAi(new AiSkill(9, 9, 0, 0));
            for (var round = 0; round < 40; round++)
            {
                var deck = Deal(random);
                var rules = SomeRules(random).Where(r => r is not (TriadRule.Chaos or TriadRule.ThreeOpen)).Append(TriadRule.AllOpen).Distinct().ToArray();
                var game = new TriadGame(deck[..5], deck[5..], TriadPlayer.Blue, rules, new Random(round), SomeEdge(random), SomeEdge(random));
                for (var empties = 9; empties > 2 + round % 4; empties--) { var (h, c) = game.Choose(random); game.PlaceCard(h, c); }
                var (hand, cell) = perfect.Choose(game.Copy(), new Random(round));
                var best = int.MinValue; var chosen = int.MinValue;
                for (var h = 0; h < game.Hand(game.Turn).Count; h++)
                {
                    if (!game.Playable(h)) continue;
                    for (var c = 0; c < 9; c++)
                    {
                        if (game.Board[c] is not null) continue;
                        var value = Minimax(game, h, c, game.Turn);
                        best = Math.Max(best, value);
                        if (h == hand && c == cell) chosen = value;
                    }
                }
                Assert(chosen == best);
            }

            // Fairness: two games alike in everything the brain may see (its own hand, the board, the open cards) but
            // with different hidden cards in the other hand give the same move for the same seed, at every level.
            foreach (var rules in new TriadRule[][] { [], [TriadRule.Same, TriadRule.Plus], [TriadRule.ThreeOpen, TriadRule.Reverse], [TriadRule.ThreeOpen, TriadRule.Same] })
                for (var round = 0; round < 6; round++)
                {
                    var deck = pool.OrderBy(_ => random.Next()).Take(15).ToArray();
                    var a = new TriadGame(deck[..5], deck[5..10], TriadPlayer.Blue, rules, new Random(round));
                    // The same seed opens the same hand positions, so only the cards that stay hidden are swapped for others.
                    var hiddenRed = deck[5..10].Select((c, i) => a.Open.Contains(c) ? c : deck[10 + i]).ToArray();
                    var b = new TriadGame(deck[..5], hiddenRed, TriadPlayer.Blue, rules, new Random(round));
                    Assert(a.Open.Where(deck[..5].Contains).Count() == b.Open.Where(deck[..5].Contains).Count() && a.Red.Count(a.Open.Contains) == b.Red.Count(b.Open.Contains));
                    Assert(a.Red.Where(c => !a.Open.Contains(c)).Any(c => !b.Red.Contains(c)));
                    foreach (var level in new[] { 1, 4, 8, 12 })
                    {
                        var brain = new OdysseyAi(level, pool);
                        Assert(brain.Choose(a.Copy(), new Random(round * 100 + level)) == brain.Choose(b.Copy(), new Random(round * 100 + level)));
                    }
                    // And with nothing known of the other player's binder at all.
                    Assert(new OdysseyAi(12).Choose(a.Copy(), new Random(round)) == new OdysseyAi(12).Choose(b.Copy(), new Random(round)));
                }

            // Speed: the opening move at level 12 (five cards each, nine empty cells, Same and Plus, every hand open) stays
            // well under the design's budget, generously bounded for CI.
            var fast = new OdysseyAi(12, pool);
            var warm = Deal(random);
            fast.Choose(new TriadGame(warm[..5], warm[5..], TriadPlayer.Blue, [TriadRule.AllOpen, TriadRule.Same, TriadRule.Plus]), new Random(0));
            var watch = new Stopwatch();
            const int openings = 8;
            for (var round = 0; round < openings; round++)
            {
                var deck = Deal(random);
                var game = new TriadGame(deck[..5], deck[5..], TriadPlayer.Blue, [TriadRule.AllOpen, TriadRule.Same, TriadRule.Plus], new Random(round));
                watch.Start();
                var (hand, cell) = fast.Choose(game.Copy(), new Random(round));
                watch.Stop();
                Assert(game.Playable(hand) && game.Board[cell] is null);
            }
            Assert(watch.ElapsedMilliseconds / openings < 400);
        });

        await check("The Odyssey opponent's levels form a ladder: 12 beats 1 by a wide margin, and 3, 6 and 10 beat 1, 3 and 6", () =>
        {
            // Equal random decks, each level playing both colours and both starts, under a mix of rules (hands open and hidden).
            TriadRule[][] sets = [[TriadRule.AllOpen], [TriadRule.AllOpen, TriadRule.Same], [TriadRule.ThreeOpen, TriadRule.Plus], [TriadRule.AllOpen, TriadRule.Same, TriadRule.Plus],
                [TriadRule.AllOpen, TriadRule.Reverse], [TriadRule.AllOpen, TriadRule.FallenAce, TriadRule.Ascension]];
            double Ladder(int high, int low, int games)
            {
                var wins = 0.0;
                var a = new OdysseyAi(high, pool); var b = new OdysseyAi(low, pool);
                for (var g = 0; g < games; g++)
                {
                    var random = new Random(1000 + g);
                    var deck = Deal(random);
                    var highIsBlue = g % 2 == 0;
                    var game = new TriadGame(deck[..5], deck[5..], g % 4 < 2 ? TriadPlayer.Blue : TriadPlayer.Red, sets[g % sets.Length], new Random(g));
                    Play(game, highIsBlue ? a : b, highIsBlue ? b : a, random);
                    wins += game.Winner == (highIsBlue ? TriadPlayer.Blue : TriadPlayer.Red) ? 1 : game.Winner == null ? .5 : 0;
                }
                return wins / games;
            }
            Assert(Ladder(12, 1, 120) > .75);
            Assert(Ladder(3, 1, 150) > .55);
            Assert(Ladder(6, 3, 150) > .55);
            Assert(Ladder(10, 6, 120) > .55);
        });
    }

    // The value of a move for a player by full minimax to the end of the game (a draw 0), on copies.
    private static int Minimax(TriadGame game, int hand, int cell, TriadPlayer me)
    {
        var next = game.Copy(); next.PlaceCard(hand, cell); next.ReleaseChaos();
        return Value(next, me);
    }
    private static int Value(TriadGame game, TriadPlayer me)
    {
        var other = me == TriadPlayer.Blue ? TriadPlayer.Red : TriadPlayer.Blue;
        if (game.Over) return game.Score(me) - game.Score(other);
        var mine = game.Turn == me;
        var best = mine ? int.MinValue : int.MaxValue;
        for (var h = 0; h < game.Hand(game.Turn).Count; h++)
        {
            if (!game.Playable(h)) continue;
            for (var c = 0; c < 9; c++)
            {
                if (game.Board[c] is not null) continue;
                var value = Minimax(game, h, c, me);
                best = mine ? Math.Max(best, value) : Math.Min(best, value);
            }
        }
        return best;
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
}
