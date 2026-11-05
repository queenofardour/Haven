namespace Haven.Core.Odyssey;

/// <summary>
/// One skill level's play: plies searched, the number of empty cells from which the endgame is solved exactly, the
/// chance of a blunder (a random move from the better half instead of the best) and the evaluation noise (a share of
/// the spread of the root values).
/// </summary>
public sealed record AiSkill(int Depth, int ExactFrom, double Blunder, double Noise);

/// <summary>
/// The Odyssey opponent: a Triple Triad brain with skill levels 1-12. It searches ahead (negamax with alpha-beta on
/// copies of the game, so every rule, Materia and card perk counts through the engine itself), solves the endgame
/// exactly once few cells are empty, and plays fair: cards it can't see (the other player's, outside the Open set) are
/// replaced by samples from the pool it could know (the player's binder), and it plays the move that does best on
/// average. Lower levels search less, blunder more and judge with more noise.
/// </summary>
public sealed class OdysseyAi : ITriadBrain
{
    public const int MinLevel = 1, MaxLevel = 12;
    /// <summary>Hidden hands are sampled at least this often, and no further once a move's search has visited this many positions (about a quarter of a second).</summary>
    public const int MinSamples = 4, NodeBudget = 250_000;

    private static readonly AiSkill[] Levels =
    [
        new(1, 0, .35, .6), new(1, 0, .25, .5), new(2, 3, .18, .4), new(2, 4, .14, .3), new(2, 4, .10, .25), new(3, 5, .08, .2),
        new(3, 5, .06, .15), new(3, 6, .05, .12), new(3, 6, .04, .1), new(4, 7, .03, .08), new(4, 7, .025, .06), new(4, 7, .02, .05),
    ];
    /// <summary>The play of a level (clamped to 1-12).</summary>
    public static AiSkill For(int level) => Levels[Math.Clamp(level, MinLevel, MaxLevel) - 1];

    /// <summary>The level, or 0 for a brain made from its own AiSkill.</summary>
    public int Level { get; }
    public AiSkill Skill { get; }
    /// <summary>Hidden hands sampled per move: 4 at level 1 to 8 at level 12.</summary>
    public int Samples { get; }
    private readonly TriadCard[] unseen;

    /// <summary>A brain of a level, with the cards the other player might hold that this one can't see (their binder; may be empty).</summary>
    public OdysseyAi(int level, IReadOnlyList<TriadCard>? unseen = null) : this(For(level), unseen, Math.Clamp(level, MinLevel, MaxLevel)) { }
    /// <summary>A brain with its own play (tests, the simulator).</summary>
    public OdysseyAi(AiSkill skill, IReadOnlyList<TriadCard>? unseen = null) : this(skill, unseen, 0) { }
    private OdysseyAi(AiSkill skill, IReadOnlyList<TriadCard>? unseen, int level)
    {
        Skill = skill; Level = level;
        Samples = 4 + (Math.Max(level, 1) - 1) * 4 / 11;
        this.unseen = unseen is null ? [] : [.. unseen];
    }

    /// <summary>
    /// The move for the player whose turn it is: a playable hand index and an empty cell. Thread-safe (every call has
    /// its own search state) and deterministic for a seed: the same visible position and generator give the same move,
    /// whatever the hidden cards are.
    /// </summary>
    public (int Hand, int Cell) Choose(TriadGame game, Random random)
    {
        var me = game.Turn;
        var other = me == TriadPlayer.Blue ? TriadPlayer.Red : TriadPlayer.Blue;
        var myHand = game.Hand(me);
        var empties = 0;
        for (var i = 0; i < 9; i++) if (game.Board[i] is null) empties++;
        if (empties == 0 || myHand.Count == 0) return (-1, -1);

        // What the brain may look at: the board, its own hand, the Open set, and the card Swap gave the other player
        // from its own hand. Anything else in the other hand is hidden and sampled. Only these (and the pool) scale the
        // card power it judges hands by, so hidden cards never tilt a value.
        var theirHand = game.Hand(other);
        var known = game.Swapped is { } swapped ? (me == TriadPlayer.Blue ? swapped.Blue : swapped.Red) : null;
        var hidden = new List<int>();
        for (var i = 0; i < theirHand.Count; i++) if (!game.Open.Contains(theirHand[i]) && theirHand[i] != known) hidden.Add(i);
        var visible = new List<TriadCard>(myHand);
        for (var i = 0; i < 9; i++) if (game.Board[i] is { } card) visible.Add(card);
        for (var i = 0; i < theirHand.Count; i++) if (!hidden.Contains(i)) visible.Add(theirHand[i]);

        var depth = empties <= Skill.ExactFrom ? empties : Math.Min(Skill.Depth, empties);
        var search = new Search(depth, new Random(random.Next()), visible.Concat(unseen));

        // The root moves: what the real game lets this player do now (Order, the Chaos pick, Free Will), likeliest first.
        var moves = search.RootMoves(game);
        if (moves.Count == 0) return (-1, -1);
        if (moves.Count == 1) return moves[0];

        var values = new double[moves.Count];
        var order = new int[moves.Count];
        for (var m = 0; m < order.Length; m++) order[m] = m;
        if (hidden.Count == 0)
        {
            search.Rate(game, moves, order, values);
        }
        else
        {
            var pool = Pool(game, me, known);
            var total = new double[moves.Count];
            var samples = 0;
            while (samples < Samples)
            {
                var sample = game.Copy(search.Random);
                Determinize(sample.Hand(other), hidden, pool, random);
                search.Rate(sample, moves, order, values);
                for (var m = 0; m < values.Length; m++) total[m] += values[m];
                samples++;
                // Heavy positions (an exact endgame with many hidden cards under Same and Plus) stop after at least
                // four samples once another would overrun the node budget; node counts are deterministic, so a seed
                // still gives one move.
                if (samples >= MinSamples && search.Nodes * (samples + 1) / samples > NodeBudget) break;
                // The next sample tries the moves that have done best so far first (tighter windows).
                var keys = new double[moves.Count];
                for (var m = 0; m < order.Length; m++) { order[m] = m; keys[m] = -total[m]; }
                Array.Sort(keys, order);
            }
            for (var m = 0; m < values.Length; m++) values[m] = total[m] / samples;
        }
        return Imperfect(moves, values, random);
    }

    /// <summary>
    /// The cards the other player might hold unseen: the pool given, less one copy of each card already in sight (on the
    /// board, in this player's hand, open, or known from Swap).
    /// </summary>
    private List<TriadCard> Pool(TriadGame game, TriadPlayer me, TriadCard? known)
    {
        var pool = new List<TriadCard>(unseen);
        void Drop(TriadCard? card) { if (card is not null) pool.Remove(card); }
        for (var i = 0; i < 9; i++) Drop(game.Board[i]);
        foreach (var card in game.Hand(me)) Drop(card);
        foreach (var card in game.Open) Drop(card);
        Drop(known);
        return pool;
    }

    // Replaces the hidden cards of a hand with draws from the pool, without replacement; with the pool run dry, by
    // cards of random middling sides (the brain then assumes nothing in particular).
    private static void Determinize(List<TriadCard> hand, List<int> hidden, List<TriadCard> pool, Random random)
    {
        var left = new List<TriadCard>(pool);
        foreach (var index in hidden)
        {
            TriadCard card;
            if (left.Count > 0) { var pick = random.Next(left.Count); card = left[pick]; left.RemoveAt(pick); }
            else card = new TriadCard(0, "?", random.Next(1, 10), random.Next(1, 10), random.Next(1, 10), random.Next(1, 10), 3);
            hand[index] = card;
        }
    }

    // The imperfection of a level: noise on the root values in proportion to their spread, then with the blunder chance
    // a random move from the better half, otherwise the best.
    private (int Hand, int Cell) Imperfect(List<(int Hand, int Cell)> moves, double[] values, Random random)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in values) { if (v < min) min = v; if (v > max) max = v; }
        var spread = max - min;
        var order = new int[moves.Count];
        var noisy = new double[moves.Count];
        for (var m = 0; m < moves.Count; m++)
        {
            order[m] = m;
            // Box-Muller: a standard normal from two uniforms.
            var u1 = 1 - random.NextDouble(); var u2 = random.NextDouble();
            var gaussian = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            noisy[m] = values[m] + gaussian * Skill.Noise * spread;
        }
        Array.Sort(noisy, order);
        Array.Reverse(order);
        var blunder = random.NextDouble() < Skill.Blunder;
        var pick = blunder ? order[random.Next((moves.Count + 1) / 2)] : order[0];
        return moves[pick];
    }

    /// <summary>
    /// One search: negamax with alpha-beta over copies of the game. Values are from the perspective of the player to
    /// move, in hundredths of a card: a finished game is its score margin × 100 (a draw 0, Sudden Death not simulated),
    /// a leaf before that the margin plus how exposed each side's board cards are and the power of the hands left.
    /// Under Chaos, replies and later moves are searched as if any card could be played (the pick is the game's own
    /// random's), so a reply is judged at its worst for this player and its own later turns at their best.
    /// </summary>
    private sealed class Search
    {
        private const double Card = 100;
        private readonly int depth;
        public readonly Random Random;
        private readonly Move[][] buffers;
        private readonly int[] facing = new int[36];
        private readonly Dictionary<TriadCard, double> power = new(ReferenceEqualityComparer.Instance), invertedPower = new(ReferenceEqualityComparer.Instance);
        private readonly TriadPower.Scale scale;
        public long Nodes;

        private readonly struct Move(int hand, int cell, int key)
        {
            public readonly int Hand = hand, Cell = cell, Key = key;
        }

        // Root moves this far behind the best so far come back as a bound (at most that), which is all the average and
        // the better half need; the rest of the root is pruned like any node.
        private const double Window = Card;

        public Search(int depth, Random random, IEnumerable<TriadCard> known)
        {
            this.depth = depth; Random = random;
            scale = new TriadPower.Scale(known);
            buffers = new Move[depth + 1][];
            for (var i = 0; i <= depth; i++) buffers[i] = new Move[45];
        }

        /// <summary>The legal moves at the root, likeliest first.</summary>
        public List<(int Hand, int Cell)> RootMoves(TriadGame game)
        {
            var buffer = buffers[depth];
            var count = Generate(game, buffer);
            var moves = new List<(int Hand, int Cell)>(count);
            for (var i = 0; i < count; i++) moves.Add((buffer[i].Hand, buffer[i].Cell));
            return moves;
        }

        /// <summary>
        /// The value of each root move for the player to move, searched to the depth in the order given: the first with
        /// a full window, the rest with a window from the best so far less a margin, so a move within the margin gets
        /// its exact value and one further behind an upper bound.
        /// </summary>
        public void Rate(TriadGame game, List<(int Hand, int Cell)> moves, int[] order, double[] values)
        {
            var empties = 0;
            for (var i = 0; i < 9; i++) if (game.Board[i] is null) empties++;
            var best = double.NegativeInfinity;
            foreach (var m in order)
            {
                var child = game.Copy(Random);
                child.PlaceCard(moves[m].Hand, moves[m].Cell);
                child.ReleaseChaos();
                var alpha = best == double.NegativeInfinity ? best : best - Window;
                values[m] = -Negamax(child, depth - 1, empties - 1, double.NegativeInfinity, -alpha);
                if (values[m] > best) best = values[m];
            }
        }

        private double Negamax(TriadGame game, int depth, int empties, double alpha, double beta)
        {
            Nodes++;
            if (empties == 0) return Terminal(game);
            if (depth <= 0) return Evaluate(game);
            var buffer = buffers[depth];
            var count = Generate(game, buffer);
            var best = double.NegativeInfinity;
            for (var i = 0; i < count; i++)
            {
                var child = game.Copy(Random);
                child.PlaceCard(buffer[i].Hand, buffer[i].Cell);
                child.ReleaseChaos();
                var value = -Negamax(child, depth - 1, empties - 1, -beta, -alpha);
                if (value > best) best = value;
                if (best > alpha) alpha = best;
                if (alpha >= beta) break;
            }
            return best;
        }

        private static double Terminal(TriadGame game)
        {
            var me = game.Turn;
            var margin = game.Score(me) - game.Score(me == TriadPlayer.Blue ? TriadPlayer.Red : TriadPlayer.Blue);
            return margin * Card;
        }

        // The legal moves of the player to move, likeliest first: captures by comparison, then equal numbers (Same and
        // Plus chances), with weak sides left facing empty cells counted against. The facing numbers of each empty cell
        // are read once; a card's own numbers once per cell.
        private int Generate(TriadGame game, Move[] buffer)
        {
            var me = game.Turn;
            var hand = game.Hand(me);
            var reverse = game.Has(TriadRule.Reverse) && game.Edge(me)?.IgnoreReverse != true;
            for (var cell = 0; cell < 9; cell++)
            {
                if (game.Board[cell] is not null) continue;
                for (var d = 0; d < 4; d++)
                {
                    var n = TriadGame.Neighbour(cell, d);
                    // 0: a wall or an own card (nothing to compare); -1: empty; otherwise the enemy's facing number.
                    facing[cell * 4 + d] = n < 0 ? 0 : game.Board[n] is null ? -1 : game.Owner[n] == me ? 0 : game.Number(n, (d + 2) % 4);
                }
            }
            var count = 0;
            for (var h = 0; h < hand.Count; h++)
            {
                if (!game.Playable(h)) continue;
                var card = hand[h];
                var modifier = game.Modifier(card, me);
                for (var cell = 0; cell < 9; cell++)
                {
                    if (game.Board[cell] is not null) continue;
                    var key = 0;
                    for (var d = 0; d < 4; d++)
                    {
                        var theirs = facing[cell * 4 + d];
                        if (theirs == 0) continue;
                        var mine = game.Number(card, d, me, cell, true, modifier);
                        if (theirs < 0) { key -= reverse ? mine - 1 : 10 - mine; continue; }
                        if (reverse ? mine < theirs : mine > theirs) key += 100;
                        else if (mine == theirs) key += 30;
                    }
                    buffer[count++] = new Move(h, cell, key);
                }
            }
            // Insertion sort by key, highest first (at most 45 moves).
            for (var i = 1; i < count; i++)
            {
                var move = buffer[i];
                var j = i - 1;
                while (j >= 0 && buffer[j].Key < move.Key) { buffer[j + 1] = buffer[j]; j--; }
                buffer[j + 1] = move;
            }
            return count;
        }

        // The margin, each board card's weakest side facing an empty cell (and its other open sides, less so), and the
        // power of the hands left; from the perspective of the player to move. Under Reverse (for a player whose Materia
        // doesn't ignore it) a low side is the strong one, so a card's sides are read inverted.
        private double Evaluate(TriadGame game)
        {
            var me = game.Turn;
            var other = me == TriadPlayer.Blue ? TriadPlayer.Red : TriadPlayer.Blue;
            var value = (game.Score(me) - game.Score(other)) * Card;
            // Whether each player's attacks compare the reverse way: my board cards face the other's attacks, and the other way round.
            var reverse = game.Has(TriadRule.Reverse);
            var myAttacksReversed = reverse && game.Edge(me)?.IgnoreReverse != true;
            var theirAttacksReversed = reverse && game.Edge(other)?.IgnoreReverse != true;
            for (var cell = 0; cell < 9; cell++)
            {
                if (game.Board[cell] is null) continue;
                var mine = game.Owner[cell] == me;
                var inverted = mine ? theirAttacksReversed : myAttacksReversed;
                var weakest = 10; var others = 0; var open = 0;
                for (var d = 0; d < 4; d++)
                {
                    var n = TriadGame.Neighbour(cell, d);
                    if (n < 0 || game.Board[n] is not null) continue;
                    var v = game.Number(cell, d);
                    if (inverted) v = 11 - v;
                    open++;
                    if (v < weakest) { others += 10 - weakest; weakest = v; } else others += 10 - v;
                }
                if (open == 0) continue;
                var exposure = 5 * (10 - weakest) + others;
                value += mine ? -exposure : exposure;
            }
            foreach (var card in game.Hand(me)) value += 25 * Power(card, myAttacksReversed);
            foreach (var card in game.Hand(other)) value -= 25 * Power(card, theirAttacksReversed);
            return value;
        }

        private double Power(TriadCard card, bool inverted)
        {
            var table = inverted ? invertedPower : power;
            if (!table.TryGetValue(card, out var p))
                table[card] = p = TriadPower.Score(inverted ? card with { Top = 11 - card.Top, Right = 11 - card.Right, Bottom = 11 - card.Bottom, Left = 11 - card.Left } : card, scale);
            return p;
        }
    }
}
