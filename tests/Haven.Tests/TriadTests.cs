using Haven.Core;

internal static class TriadTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        TriadCard Card(uint id, int top, int right, int bottom, int left, int stars = 1, int type = 0) => new(id, "Card " + id, top, right, bottom, left, stars, type);
        TriadCard[] Five(uint first, int n) => [.. Enumerable.Range(0, 5).Select(i => Card(first + (uint)i, n, n, n, n))];

        await check("Two players' Triple Triad: the relay message and deal round-trip, and two PCs playing one deal stay identical under every rule", () =>
        {
            var match = Guid.NewGuid();
            var deal = new TriadDeal(12345, TriadPlayer.Red, [1, 2, 3, 4, 5], [11, 12, 13, 14, 15]);
            var message = new GameMessage(7, 8, "triad", "start", match, deal.Encode());
            var read = GameMessage.Decode(message.Encode());
            Assert(read.Sender == 7 && read.Target == 8 && read.Kind == "start" && read.Match == match && TriadDeal.Decode(read.Values) is { Seed: 12345, First: TriadPlayer.Red } back
                && back.HostDeck.SequenceEqual(deal.HostDeck) && back.GuestDeck.SequenceEqual(deal.GuestDeck));
            foreach (var bad in new[] { message with { Game = "chess" }, message with { Kind = "cheat" }, message with { Match = Guid.Empty }, message with { Values = new int[30] } })
                try { GameMessage.Decode(bad.Encode()); throw new Exception("A minigame message Haven doesn't play was read."); } catch (InvalidDataException) { }
            Assert(TriadDeal.Decode([1, 2, 3]) == null && TriadDeal.Decode([1, 5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]) == null);
            // Both PCs build the game from the deal with the same seed; one side's moves (chosen however that PC likes)
            // replayed on the other leave both boards, owners, hands and turns the same, Sudden Death restarts included.
            var pool = Enumerable.Range(1, 40).Select(i => Card((uint)i, 1 + i % 9, 1 + i * 7 % 9, 1 + i * 3 % 9, 1 + i * 5 % 9, 1 + i % 3, i % 5)).ToArray();
            TriadRule[][] sets = [[], [TriadRule.Same, TriadRule.Plus], [TriadRule.Chaos, TriadRule.Swap], [TriadRule.Order, TriadRule.SuddenDeath], [TriadRule.Roulette],
                [TriadRule.ThreeOpen, TriadRule.Ascension, TriadRule.Reverse], [TriadRule.FallenAce, TriadRule.Descension, TriadRule.AllOpen]];
            var chooser = new Random(3);
            for (var round = 0; round < 70; round++)
            {
                var rules = sets[round % sets.Length];
                var host = pool.Skip(round % 30).Take(5).ToArray(); var guest = pool.Skip((round * 7) % 30 + 5).Take(5).ToArray();
                var seed = 1000 + round; var first = round % 2 == 0 ? TriadPlayer.Blue : TriadPlayer.Red;
                var a = new TriadGame(host, guest, first, rules, new Random(seed));
                var b = new TriadGame(host, guest, first, rules, new Random(seed));
                Assert(a.Rules.SequenceEqual(b.Rules) && a.Open.Select(c => c.Id).OrderBy(x => x).SequenceEqual(b.Open.Select(c => c.Id).OrderBy(x => x)));
                for (var turn = 0; turn < 60; turn++)
                {
                    if (a.Over) { var again = a.RestartSuddenDeath(); Assert(b.RestartSuddenDeath() == again); if (!again) break; }
                    var (hand, cell) = a.Choose(chooser);
                    a.PlaceCard(hand, cell); b.PlaceCard(hand, cell);
                    Assert(a.Turn == b.Turn && a.ChaosPick == b.ChaosPick && a.Owner.SequenceEqual(b.Owner)
                        && a.Board.Select(c => c?.Id).SequenceEqual(b.Board.Select(c => c?.Id))
                        && a.Blue.Select(c => c.Id).SequenceEqual(b.Blue.Select(c => c.Id)) && a.Red.Select(c => c.Id).SequenceEqual(b.Red.Select(c => c.Id)));
                }
                Assert(a.Over && b.Over && a.Winner == b.Winner && a.Restarts == b.Restarts);
            }
        });

        await check("Triple Triad with card perks: a copy of the game reads every number as the game does (Mimic's copies, Drain, Frostbite, Rally), and Sudden Death starts clean", () =>
        {
            var perks = Enum.GetValues<TriadPerk>().Where(p => p != TriadPerk.None).ToArray();
            var pool = Enumerable.Range(1, 40).Select(i => Card((uint)i, 1 + i % 9, 1 + i * 7 % 9, 1 + i * 3 % 9, 1 + i * 5 % 9, 1 + i % 3, i % 5) with { Perk = perks[i % perks.Length] | perks[i * 7 % perks.Length] }).ToArray();
            TriadRule[][] sets = [[TriadRule.SuddenDeath], [TriadRule.Same, TriadRule.Plus, TriadRule.SuddenDeath], [TriadRule.Reverse, TriadRule.Descension], [TriadRule.Ascension, TriadRule.FallenAce]];
            var chooser = new Random(5);
            var restarted = 0;
            for (var round = 0; round < 40; round++)
            {
                var game = new TriadGame(pool.Skip(round % 30).Take(5), pool.Skip((round * 7) % 30 + 5).Take(5), round % 2 == 0 ? TriadPlayer.Blue : TriadPlayer.Red, sets[round % sets.Length], new Random(round));
                for (var turn = 0; turn < 60; turn++)
                {
                    if (game.Over)
                    {
                        if (!game.RestartSuddenDeath()) break;
                        restarted++;
                        Assert(Enumerable.Range(0, 9).All(c => game.Lasting(c) == 0 && game.Board[c] == null));
                    }
                    var (hand, cell) = game.Choose(chooser);
                    game.PlaceCard(hand, cell);
                    var copy = game.Copy();
                    Assert(Enumerable.Range(0, 36).All(i => game.Number(i / 4, i % 4) == copy.Number(i / 4, i % 4)) && Enumerable.Range(0, 9).All(c => game.Lasting(c) == copy.Lasting(c)));
                    Assert(Enumerable.Range(0, 36).All(i => game.Board[i / 4] == null || game.Number(i / 4, i % 4) is >= 1 and <= 10));
                }
            }
            Assert(restarted > 0);
        });

        await check("Triple Triad takes a neighbour with a higher touching number, scores board and hand, and deals by the deck rules", () =>
        {
            var blue = Five(1, 5); var red = Five(11, 4);
            var game = new TriadGame(blue, red, TriadPlayer.Red);
            Assert(game.Place(0, 4).Length == 0 && game.Turn == TriadPlayer.Blue);       // red in the centre
            Assert(game.Place(0, 1).SequenceEqual([4]) && game.Owner[4] == TriadPlayer.Blue);  // blue above: 5 (bottom) beats 4 (top)
            // Equal numbers take nothing.
            var even = new TriadGame(Five(1, 4), red, TriadPlayer.Red);
            even.Place(0, 0); Assert(even.Place(0, 1).Length == 0);
            Assert(TriadGame.Neighbour(0, 0) == -1 && TriadGame.Neighbour(0, 1) == 1 && TriadGame.Neighbour(4, 2) == 7 && TriadGame.Neighbour(3, 3) == -1);
            // Played out by the opponent's choices, the board fills and the scores count every card once.
            var random = new Random(3);
            while (!game.Over) { var (h, cell) = game.Choose(random); game.Place(h, cell); }
            Assert(game.Score(TriadPlayer.Blue) + game.Score(TriadPlayer.Red) == 10);
            try { game.Place(0, 0); throw new Exception("A move on a full board was allowed."); } catch (InvalidOperationException) { }
            // At most one five-star card and two of four stars or more; a small pool is filled from cards up to three stars.
            var all = Enumerable.Range(1, 30).Select(i => Card((uint)i, 1, 1, 1, 1, i <= 5 ? 5 : i <= 10 ? 4 : i <= 20 ? 3 : 1)).ToArray();
            for (var seed = 0; seed < 20; seed++)
            {
                var hand = TriadGame.Deal(all[..12], all, new Random(seed));
                Assert(hand.Length == 5 && hand.Count(c => c.Stars >= 5) <= 1 && hand.Count(c => c.Stars >= 4) <= 2 && hand.Select(c => c.Id).Distinct().Count() == 5);
            }
        });

        await check("Triple Triad's special rules: Same, Plus and their combos, Reverse, Fallen Ace, Ascension, Order and Roulette", () =>
        {
            // Same: blue lays a card whose top and left equal the red cards above and to its left (two equal pairs): both flip.
            var same = new TriadGame([Card(1, 3, 1, 1, 6), .. Five(2, 1)[..4]], [Card(11, 1, 1, 3, 1), Card(12, 1, 6, 1, 1), Card(13, 1, 9, 9, 9), .. Five(14, 1)[..2]], TriadPlayer.Red, [TriadRule.Same]);
            same.Place(0, 1); same.Place(1, 0);   // red: 11 at the top middle, blue somewhere harmless
            same.Place(0, 3);                     // red: 12 at the middle left
            var placement = same.PlaceCard(0, 4); // blue: 1 in the centre, top 3 = red's bottom 3, left 6 = red's right 6
            Assert(placement.Same.OrderBy(x => x).SequenceEqual([1, 3]) && same.Owner[1] == TriadPlayer.Blue && same.Owner[3] == TriadPlayer.Blue);
            // Without Same the equal numbers take nothing.
            var plain = new TriadGame([Card(1, 3, 1, 1, 6), .. Five(2, 1)[..4]], [Card(11, 1, 1, 3, 1), Card(12, 1, 6, 1, 1), .. Five(13, 1)[..3]], TriadPlayer.Red);
            plain.Place(0, 1); plain.Place(1, 0); plain.Place(0, 3);
            Assert(plain.PlaceCard(0, 4).All.Any() == false);

            // Plus: sums 2+5 = 7 above and 4+3 = 7 to the left: both flip.
            var plus = new TriadGame([Card(1, 2, 1, 1, 4), .. Five(2, 1)[..4]], [Card(11, 1, 1, 5, 1), Card(12, 1, 3, 1, 1), .. Five(13, 1)[..3]], TriadPlayer.Red, [TriadRule.Plus]);
            plus.Place(0, 1); plus.Place(1, 8); plus.Place(0, 3);
            var p = plus.PlaceCard(0, 4);
            Assert(p.Plus.OrderBy(x => x).SequenceEqual([1, 3]));

            // Combo: a card Same took then takes its weaker neighbour by comparison.
            var combo = new TriadGame([Card(1, 3, 1, 1, 6), .. Five(2, 1)[..4]], [Card(11, 1, 9, 3, 1), Card(12, 1, 6, 1, 1), Card(13, 1, 1, 1, 1), .. Five(14, 1)[..2]], TriadPlayer.Red, [TriadRule.Same]);
            combo.Place(0, 1); combo.Place(1, 8); combo.Place(0, 3); combo.Place(1, 7);
            combo.Place(0, 2);   // red 13 (all 1s) at the top right, beside red 11 whose right side is 9
            var c = combo.PlaceCard(0, 4);
            Assert(c.Same.Contains(1) && c.Combo.Contains(2) && combo.Owner[2] == TriadPlayer.Blue);

            // Reverse: lower takes higher.
            var reverse = new TriadGame(Five(1, 3), Five(11, 6), TriadPlayer.Red, [TriadRule.Reverse]);
            reverse.Place(0, 4); Assert(reverse.Place(0, 1).SequenceEqual([4]));
            // Fallen Ace: a 1 takes an A.
            var ace = new TriadGame(Five(1, 1), Five(11, 10), TriadPlayer.Red, [TriadRule.FallenAce]);
            ace.Place(0, 4); Assert(ace.Place(0, 1).SequenceEqual([4]));
            var noAce = new TriadGame(Five(1, 1), Five(11, 10), TriadPlayer.Red);
            noAce.Place(0, 4); Assert(noAce.Place(0, 1).Length == 0);

            // Ascension: a typed card gains one per card of its type already on the board, clamped to A.
            var asc = new TriadGame([.. Enumerable.Range(1, 5).Select(i => Card((uint)i, 4, 4, 4, 4, 1, 1))], [.. Enumerable.Range(11, 5).Select(i => Card((uint)i, 5, 5, 5, 5, 1, 2))], TriadPlayer.Blue, [TriadRule.Ascension]);
            asc.Place(0, 0); asc.Place(0, 8);     // one Primal on the board
            Assert(asc.Value(asc.Blue[0], 0) == 5);
            asc.Place(0, 6); asc.Place(0, 4);     // two Primals; red's second Scion in the centre has 5 + 2
            Assert(asc.Value(asc.Board[4]!, 0) == 7 && asc.Takes(asc.Blue[0], 1, TriadPlayer.Blue).Length == 0);
            Assert(new TriadGame(Five(1, 9), Five(11, 1), TriadPlayer.Blue, [TriadRule.Ascension]).Value(Card(99, 10, 10, 10, 10, 1, 0), 0) == 10);

            // Order: only the first card left may be played.
            var order = new TriadGame(Five(1, 5), Five(11, 4), TriadPlayer.Blue, [TriadRule.Order]);
            Assert(order.Playable(0) && !order.Playable(1));
            try { order.Place(2, 0); throw new Exception("Order allowed a later card."); } catch (InvalidOperationException) { }
            // Chaos: exactly one card may be played each turn.
            var chaos = new TriadGame(Five(1, 5), Five(11, 4), TriadPlayer.Blue, [TriadRule.Chaos], new Random(4));
            Assert(Enumerable.Range(0, 5).Count(chaos.Playable) == 1);
            // Roulette rolls rules that can be played together.
            for (var seed = 0; seed < 50; seed++)
            {
                var rolled = TriadGame.Roll([TriadRule.Roulette, TriadRule.Roulette, TriadRule.AllOpen], new Random(seed));
                Assert(rolled.Count == 3 && !rolled.Contains(TriadRule.Roulette) && !rolled.Contains(TriadRule.ThreeOpen)
                    && rolled.SelectMany(a => rolled.Where(b => b != a).Select(b => (a, b))).All(x => TriadGame.Compatible(x.a, x.b)));
            }
            // A rule Roulette may not roll here (the Odyssey's Reverse) never comes up.
            for (var seed = 0; seed < 200; seed++)
                Assert(!TriadGame.Roll([TriadRule.Roulette, TriadRule.Same], new Random(seed), [TriadRule.Reverse]).Contains(TriadRule.Reverse));
            Assert(Enumerable.Range(0, 200).Any(seed => TriadGame.Roll([TriadRule.Roulette], new Random(seed)).Contains(TriadRule.Reverse)));
            // The rule box (four lines): Roulette's picks first; past four, three lines and the rest on the last.
            var asked = new[] { TriadRule.Same, TriadRule.Plus, TriadRule.Roulette, TriadRule.ThreeOpen, TriadRule.SuddenDeath };
            var five = new[] { TriadRule.Same, TriadRule.Plus, TriadRule.ThreeOpen, TriadRule.SuddenDeath, TriadRule.Reverse };
            var (lines, more) = TriadGame.RuleLines(five, asked);
            Assert(lines.SequenceEqual([TriadRule.Reverse, TriadRule.Same, TriadRule.Plus]) && more.SequenceEqual([TriadRule.ThreeOpen, TriadRule.SuddenDeath]));
            var (few, none) = TriadGame.RuleLines([TriadRule.Same, TriadRule.Reverse], [TriadRule.Same, TriadRule.Roulette]);
            Assert(few.SequenceEqual([TriadRule.Reverse, TriadRule.Same]) && none.Count == 0);
            // The Odyssey leaves Sudden Death out (every duel plays it): five rules then fit the four lines.
            var (listed, rest) = TriadGame.RuleLines(five, asked, unlisted: Haven.Core.Odyssey.OdysseyBalance.Unlisted);
            Assert(listed.SequenceEqual([TriadRule.Reverse, TriadRule.Same, TriadRule.Plus, TriadRule.ThreeOpen]) && rest.Count == 0);
            Assert(!Haven.Core.Odyssey.OdysseyBalance.Listed([TriadRule.AllOpen, TriadRule.SuddenDeath]).Contains(TriadRule.SuddenDeath));
            // Sudden Death: a draw restarts with the cards each side owns.
            var sudden = new TriadGame(Five(1, 5), Five(11, 5), TriadPlayer.Blue, [TriadRule.SuddenDeath], new Random(1));
            var r = new Random(2);
            while (!sudden.Over) { var (h, cell) = sudden.Choose(r); sudden.Place(h, cell); }
            Assert(sudden.Winner == null && sudden.RestartSuddenDeath() && sudden.Blue.Count == 5 && sudden.Red.Count == 5 && sudden.Board.All(x => x == null));
            // Kept cards (the Odyssey): a draw with captures replays the hands as dealt, not the cards each side owns.
            var kept = false;
            for (var seed = 0; seed < 2000 && !kept; seed++)
            {
                var deal = new Random(seed);
                TriadCard[] Hand(uint first) => [.. Enumerable.Range(0, 5).Select(i => Card(first + (uint)i, deal.Next(1, 10), deal.Next(1, 10), deal.Next(1, 10), deal.Next(1, 10)))];
                var game = new TriadGame(Hand(1), Hand(11), TriadPlayer.Blue, [TriadRule.SuddenDeath], new Random(seed)) { KeepsOwnCards = true };
                var (blue, red) = (game.Blue.ToList(), game.Red.ToList());
                var moves = new Random(seed + 1);
                while (!game.Over) { var (h, cell) = game.Choose(moves); game.Place(h, cell); }
                var crossed = Enumerable.Range(0, 9).Any(i => game.Owner[i] == TriadPlayer.Blue && red.Contains(game.Board[i]!));
                if (game.Winner != null || !crossed) continue;
                Assert(game.RestartSuddenDeath() && game.Blue.SequenceEqual(blue) && game.Red.SequenceEqual(red) && game.Copy().KeepsOwnCards);
                kept = true;
            }
            Assert(kept);
            // Open rules: All Open shows all ten cards, Three Open three of each hand.
            Assert(new TriadGame(Five(1, 5), Five(11, 4), TriadPlayer.Blue, [TriadRule.AllOpen]).Open.Count == 10);
            var three = new TriadGame(Five(1, 5), Five(11, 4), TriadPlayer.Blue, [TriadRule.ThreeOpen]);
            Assert(three.Open.Count(three.Blue.Contains) == 3 && three.Open.Count(three.Red.Contains) == 3);
            // An NPC deck holds its fixed cards and fills from its variable ones.
            var deck = TriadGame.NpcDeck(Five(1, 5)[..3], Five(11, 4), new Random(9));
            Assert(deck.Length == 5 && deck.Take(3).Select(x => x.Id).SequenceEqual(new uint[] { 1, 2, 3 }));
        });
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
}
