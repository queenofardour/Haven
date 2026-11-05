namespace Haven.Core.Odyssey;

/// <summary>
/// How strong a Triple Triad card really is, whatever its stars. A card is placed in a corner (two sides face the
/// board), on an edge (three) or in the centre (four), and a good player puts its best sides where they face the board.
/// So a card's power is mostly its best corner pair and edge triple, each side scored by how often it beats, and is not
/// beaten by, a side of a card drawn from the whole set: 0 (never) to 1 (always). A 9-9 corner card with 1s behind is
/// strong; an even 5-5-5-5 card is weak, whatever its stars say.
/// </summary>
public static class TriadPower
{
    // Corners (two inward sides): top-left faces right and down, and so on.
    private static readonly (int, int)[] Pairs = [(1, 2), (2, 3), (3, 0), (0, 1)];
    // Edges: the three sides other than the one against the wall.
    private static readonly (int, int, int)[] Triples = [(1, 2, 3), (2, 3, 0), (3, 0, 1), (0, 1, 2)];

    /// <summary>A side's worth against a side of the whole card set: half how often it takes, half how often it holds.</summary>
    public sealed class Scale
    {
        private readonly double[] worth = new double[11];
        public Scale(IEnumerable<TriadCard> cards)
        {
            var counts = new double[11];
            var total = 0.0;
            foreach (var card in cards) for (var d = 0; d < 4; d++) { counts[Math.Clamp(card.Side(d), 1, 10)]++; total++; }
            if (total == 0) { for (var v = 1; v <= 10; v++) worth[v] = (v - 1) / 9.0; return; }
            for (var v = 1; v <= 10; v++)
            {
                double below = 0, above = 0;
                for (var o = 1; o <= 10; o++) { if (o < v) below += counts[o]; if (o > v) above += counts[o]; }
                worth[v] = (below / total + (1 - above / total)) / 2;
            }
        }
        public double Side(int value) => worth[Math.Clamp(value, 1, 10)];
    }

    /// <summary>0 to about 1: the card's best corner, best edge and all four sides, weighted 45/35/20.</summary>
    public static double Score(TriadCard card, Scale scale)
    {
        double S(int d) => scale.Side(card.Side(d));
        var corner = Pairs.Max(p => (S(p.Item1) + S(p.Item2)) / 2);
        var edge = Triples.Max(t => (S(t.Item1) + S(t.Item2) + S(t.Item3)) / 3);
        var all = (S(0) + S(1) + S(2) + S(3)) / 4;
        return .45 * corner + .35 * edge + .2 * all;
    }

    /// <summary>A deck's power: the mean of its cards'.</summary>
    public static double Deck(IEnumerable<TriadCard> cards, Scale scale) => cards.Select(c => Score(c, scale)).DefaultIfEmpty(0).Average();
}
