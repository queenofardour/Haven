namespace Haven.Core;

/// <summary>A Triple Triad card: its TripleTriadCard row, name, four numbers (10 shows as A), stars, type (TripleTriadCardType:
/// 1 Primal, 2 Scion, 3 Society, 4 Garlean, 0 none) and its art icon.</summary>
public sealed record TriadCard(uint Id, string Name, int Top, int Right, int Bottom, int Left, int Stars, int Type = 0)
{
    /// <summary>What an Odyssey enhancement gave the card (none in a normal match).</summary>
    public TriadPerk Perk { get; init; }
    public bool Is(TriadPerk perk) => (Perk & perk) != 0;
    /// <summary>The card's art, as the game's card list shows it (icon 87000 + id).</summary>
    public uint Icon => 87000 + Id;
    public int Side(int direction) => direction switch { 0 => Top, 1 => Right, 2 => Bottom, _ => Left };
}

public enum TriadPlayer { Blue, Red }

/// <summary>An opponent's way of choosing a move for the player whose turn it is (the Odyssey's AI); null plays TriadGame.Choose.</summary>
public interface ITriadBrain
{
    /// <summary>A playable hand index and an empty cell. Called on a copy of the game, possibly off the framework thread.</summary>
    (int Hand, int Cell) Choose(TriadGame game, Random random);
}

/// <summary>
/// A card's own trait, from the Odyssey's enhancements: Steadfast can't be taken by comparison, Aegis can't be taken by
/// Same, Plus or a combo, Ace Killer's 1 takes an A as Fallen Ace does, Avalanche's captures start combos, Reckless
/// strikes harder (+2) and guards worse (-1), Guardian the other way round, Cornerstone is +2 in a corner, Crown counts
/// twice in its owner's score, Wild counts as every type. Vanguard is +2 when it is its owner's first card of the duel,
/// Desperate +2 when played while behind (both stay), Keystone +2 in the centre; Frostbite chills the enemy cards beside
/// it by 1 when played, Rally strengthens its owner's by 1, Drain gains 1 per card its placement takes (all for good);
/// Contrary takes with lower numbers (higher under Reverse), Masked counts a star lower for the Odyssey's deck limits and
/// plays -1, and Mimic's sides copy the higher numbers they face when played. A card may carry several (one scroll may
/// grant two).
/// </summary>
[Flags]
public enum TriadPerk
{
    None = 0, Steadfast = 1, Aegis = 2, AceKiller = 4, Avalanche = 8, Reckless = 16, Guardian = 32, Cornerstone = 64, Crown = 128, Wild = 256,
    Vanguard = 512, Desperate = 1024, Keystone = 2048, Frostbite = 4096, Rally = 8192, Drain = 16384, Contrary = 32768, Masked = 65536, Mimic = 131072,
}

/// <summary>
/// One player's standing advantages in a match (the Odyssey's Materia, and its opponents' own). Numbers are added to a
/// card's sides before the 1-A clamp; null in a normal match. Not changed once the game starts.
/// </summary>
public sealed class TriadEdge
{
    /// <summary>Added to every side of every card.</summary>
    public int All;
    /// <summary>By where the card lies: a corner, the middle of an edge, the centre.</summary>
    public int Corner, Side, Centre;
    /// <summary>By card type (index 1 Primal, 2 Scion, 3 Society, 4 Garlean).</summary>
    public readonly int[] Type = new int[5];
    /// <summary>For cards of one or two stars, and of four or five.</summary>
    public int Common, Rare;
    /// <summary>Sides below this count as this (0: none).</summary>
    public int Floor;
    /// <summary>When placing (attacking) or lying on the board (defending).</summary>
    public int Attack, Defend;
    /// <summary>Stays on the first card placed, the last card placed, a card placed while behind, the card after one that took two or more.</summary>
    public int First, Last, Behind, Momentum;
    /// <summary>Same and Plus for this player's placements even when not a rule of the match.</summary>
    public bool Same, Plus;
    /// <summary>The board's walls count as A beside this player's placements, for Same and for Plus.</summary>
    public bool SameWall, PlusWall;
    /// <summary>This player's captures compare normally under Reverse.</summary>
    public bool IgnoreReverse;
    /// <summary>Order and Chaos don't bind this player.</summary>
    public bool FreeChoice;
    /// <summary>This player's typed cards ascend even without the rule, and never descend.</summary>
    public bool Ascend;
    /// <summary>This player's corner cards can't be taken.</summary>
    public bool CornerGuard;
    /// <summary>Same, Plus and combos can't take this player's cards.</summary>
    public bool Shielded;
    /// <summary>No Open rule (nor the other's Reveal) shows this player's hand.</summary>
    public bool Concealed;
    /// <summary>Enemy cards beside each card this player places lose 1 on every side (it stays).</summary>
    public bool Frost;
    /// <summary>A card placed beside this player's own card of its type: both gain 1 (it stays).</summary>
    public bool Bond;
    /// <summary>Equal numbers take, for this player's placements.</summary>
    public bool TiesWin;
    /// <summary>This player sees the other's hand.</summary>
    public bool Reveal;
    /// <summary>Captures of this player's cards undone per match (the first ones).</summary>
    public int Reflect;

    internal int Fixed(TriadCard card)
    {
        var bonus = All + (card.Stars <= 2 ? Common : card.Stars >= 4 ? Rare : 0);
        if (card.Is(TriadPerk.Wild)) bonus += Math.Max(0, Type.Max());
        else if (card.Type is >= 1 and <= 4) bonus += Type[card.Type];
        return bonus;
    }
    internal int Place(int cell) => cell < 0 ? 0 : cell == 4 ? Centre : cell is 0 or 2 or 6 or 8 ? Corner : Side;
}

/// <summary>Triple Triad's rules, by TripleTriadRule row.</summary>
public enum TriadRule
{
    Roulette = 1, AllOpen = 2, ThreeOpen = 3, Same = 4, SuddenDeath = 5, Plus = 6, Random = 7, Order = 8, Chaos = 9,
    Reverse = 10, FallenAce = 11, Ascension = 12, Descension = 13, Swap = 14, Draft = 15,
}

/// <summary>What a placement did: cards taken by comparison, by Same or Plus, and by the combos those started.</summary>
public sealed record TriadPlacement(int Cell, int[] Taken, int[] Same, int[] Plus, int[] Combo)
{
    public IEnumerable<int> All => Taken.Concat(Same).Concat(Plus).Concat(Combo).Distinct();
}

/// <summary>
/// A game of Triple Triad, played locally (Haven's Triple Triad Board; nothing reaches the server and nothing is won):
/// a 3 × 3 board, five cards each, taking turns. A card placed next to an opponent's card takes it when its touching
/// number is higher (lower under Reverse), plus whatever the match's rules add: Same, Plus and their combos, Fallen
/// Ace, Ascension and Descension, Order, Chaos, the Open rules, Swap and Sudden Death. The board full, whoever owns
/// more cards (on the board and still in hand) wins.
/// </summary>
public sealed class TriadGame
{
    public readonly TriadCard?[] Board = new TriadCard?[9];
    public readonly TriadPlayer[] Owner = new TriadPlayer[9];
    // Hands keep deck order (Order plays the first card left).
    public readonly List<TriadCard> Blue, Red;
    public TriadPlayer Turn { get; private set; }
    public IReadOnlyList<TriadRule> Rules { get; }
    /// <summary>Cards of each hand shown to the other player (All Open, Three Open); without an Open rule, none.</summary>
    public HashSet<TriadCard> Open { get; } = [];
    /// <summary>Under Chaos, the hand index the current player must play this turn.</summary>
    public int ChaosPick { get; private set; } = -1;
    /// <summary>Under Swap, the two cards exchanged before the match (blue's, red's).</summary>
    public (TriadCard Blue, TriadCard Red)? Swapped { get; }
    public TriadPlacement? Last { get; private set; }
    /// <summary>Sudden Death restarts so far.</summary>
    public int Restarts { get; private set; }
    /// <summary>
    /// Sudden Death replays with the hands as dealt instead of the cards each side owns at the end (the Odyssey: a
    /// captured card never plays for the other side).
    /// </summary>
    public bool KeepsOwnCards { get; init; }
    // The hands as dealt (after Swap), for KeepsOwnCards.
    private readonly TriadCard[] dealtBlue, dealtRed;
    private readonly Random random;
    /// <summary>Each player's advantages (Odyssey Materia), or null.</summary>
    public TriadEdge? BlueEdge { get; }
    public TriadEdge? RedEdge { get; }
    public TriadEdge? Edge(TriadPlayer player) => player == TriadPlayer.Blue ? BlueEdge : RedEdge;
    // What stays on the card on a cell for the rest of the match (frost, bonds, a first or last card's bonus).
    private readonly int[] lasting = new int[9];
    // What a Mimic card's sides copied when it was played (cell × 4 + direction), added to those sides for the match.
    private readonly int[] mimicked = new int[36];
    // Per player: cards placed, undoes left (Reflect), and the bonus the next card gets (Momentum).
    private readonly int[] placed = new int[2], reflects = new int[2], pending = new int[2];
    /// <summary>What stays on the card on a cell (positive or negative), shown with its numbers.</summary>
    public int Lasting(int cell) => lasting[cell];
    /// <summary>A player's undoes (Reflect) left this match.</summary>
    public int Reflects(TriadPlayer player) => reflects[(int)player];

    /// <param name="notRolled">Rules Roulette may not roll here (the Odyssey keeps Reverse out of its random rules).</param>
    public TriadGame(IEnumerable<TriadCard> blue, IEnumerable<TriadCard> red, TriadPlayer first, IEnumerable<TriadRule>? rules = null, Random? random = null,
        TriadEdge? blueEdge = null, TriadEdge? redEdge = null, IEnumerable<TriadRule>? notRolled = null)
    {
        BlueEdge = blueEdge; RedEdge = redEdge;
        reflects[0] = blueEdge?.Reflect ?? 0; reflects[1] = redEdge?.Reflect ?? 0;
        Blue = [.. blue]; Red = [.. red];
        if (Blue.Count != 5 || Red.Count != 5) throw new ArgumentException("Each player needs five cards.");
        this.random = random ?? new Random();
        Turn = first;
        Rules = Roll(rules ?? [], this.random, notRolled);
        if (Has(TriadRule.Swap))
        {
            int b = this.random.Next(5), r = this.random.Next(5);
            Swapped = (Blue[b], Red[r]);
            (Blue[b], Red[r]) = (Red[r], Blue[b]);
        }
        dealtBlue = [.. Blue]; dealtRed = [.. Red];
        if (Has(TriadRule.AllOpen)) foreach (var card in Blue.Concat(Red)) Open.Add(card);
        else if (Has(TriadRule.ThreeOpen))
        {
            foreach (var card in Blue.OrderBy(_ => this.random.Next()).Take(3)) Open.Add(card);
            foreach (var card in Red.OrderBy(_ => this.random.Next()).Take(3)) Open.Add(card);
        }
        if (blueEdge?.Reveal == true) foreach (var card in Red) Open.Add(card);
        if (redEdge?.Reveal == true) foreach (var card in Blue) Open.Add(card);
        // A concealed hand stays hidden whatever shows it (Prism Powder). Open holds cards by value, so a card the other
        // hand holds an identical copy of stays open: hiding it would hide the other side's copy too.
        if (blueEdge?.Concealed == true) foreach (var card in Blue) if (!Red.Contains(card)) Open.Remove(card);
        if (redEdge?.Concealed == true) foreach (var card in Red) if (!Blue.Contains(card)) Open.Remove(card);
        PickChaos();
    }

    // A copy for the opponent's look-ahead. It draws from its own generator (or one the searcher hands every copy), so
    // thinking never moves the game's own (two players' PCs keep one game in step by drawing the same numbers in the same
    // order). It carries what is open and swapped, so a brain thinking on a copy knows what it may look at.
    private TriadGame(TriadGame other, Random? random = null)
    {
        Array.Copy(other.Board, Board, 9); Array.Copy(other.Owner, Owner, 9);
        Blue = [.. other.Blue]; Red = [.. other.Red]; Turn = other.Turn; Rules = other.Rules; this.random = random ?? new Random(0); ChaosPick = other.ChaosPick;
        BlueEdge = other.BlueEdge; RedEdge = other.RedEdge; Swapped = other.Swapped; Restarts = other.Restarts;
        KeepsOwnCards = other.KeepsOwnCards; dealtBlue = other.dealtBlue; dealtRed = other.dealtRed;
        foreach (var card in other.Open) Open.Add(card);
        Array.Copy(other.lasting, lasting, 9); Array.Copy(other.mimicked, mimicked, 36); Array.Copy(other.placed, placed, 2); Array.Copy(other.reflects, reflects, 2); Array.Copy(other.pending, pending, 2);
    }
    /// <summary>A copy to think ahead with (search): the same position, its own random numbers.</summary>
    public TriadGame Copy() => new(this);
    /// <summary>A copy for a search that makes many: it draws Chaos picks from the generator given, so no copy seeds one of its own.</summary>
    public TriadGame Copy(Random random) => new(this, random);
    /// <summary>Lifts the Chaos pick on a copy, so a search may try every card (the real pick is the game's own random's).</summary>
    public void ReleaseChaos() => ChaosPick = -1;

    public bool Has(TriadRule rule) => Rules.Contains(rule);

    // Rules that cannot be played together (TripleTriadRule columns 2 and 3).
    private static readonly (TriadRule, TriadRule)[] Excluded =
        [(TriadRule.AllOpen, TriadRule.ThreeOpen), (TriadRule.Order, TriadRule.Chaos), (TriadRule.Ascension, TriadRule.Descension),
         (TriadRule.Random, TriadRule.Draft), (TriadRule.Order, TriadRule.Draft)];
    public static bool Compatible(TriadRule a, TriadRule b) => a != b && !Excluded.Contains((a, b)) && !Excluded.Contains((b, a));

    /// <summary>
    /// The rules as a four-line rule box shows them (the game's has four lines): rules Roulette rolled first (they're
    /// the ones a player can't know beforehand), then the rest; past four, three lines and a fourth for the others
    /// (More), so a rolled rule is never the one cut off. Unlisted rules (the Odyssey's Sudden Death) aren't shown.
    /// </summary>
    public static (IReadOnlyList<TriadRule> Lines, IReadOnlyList<TriadRule> More) RuleLines(IReadOnlyList<TriadRule> rules, IEnumerable<TriadRule> asked, int lines = 4,
        IEnumerable<TriadRule>? unlisted = null)
    {
        var given = asked.ToHashSet();
        var hidden = unlisted?.ToHashSet() ?? [];
        var shown = rules.Where(r => !hidden.Contains(r)).ToList();
        var ordered = shown.Where(r => !given.Contains(r)).Concat(shown.Where(given.Contains)).ToList();
        return ordered.Count <= lines ? (ordered, []) : (ordered.Take(lines - 1).ToList(), ordered.Skip(lines - 1).ToList());
    }

    /// <summary>The rules in play: each Roulette replaced by a random rule that fits with the others (and isn't one of notRolled).</summary>
    public static IReadOnlyList<TriadRule> Roll(IEnumerable<TriadRule> rules, Random random, IEnumerable<TriadRule>? notRolled = null)
    {
        var list = rules.ToList();
        var skip = notRolled?.ToHashSet() ?? [];
        var chosen = list.Where(r => r != TriadRule.Roulette).Distinct().ToList();
        foreach (var _ in list.Where(r => r == TriadRule.Roulette))
        {
            var pool = Enum.GetValues<TriadRule>().Where(r => r is not (TriadRule.Roulette or TriadRule.Draft or TriadRule.Random or TriadRule.SuddenDeath)
                && !skip.Contains(r) && chosen.All(c => Compatible(c, r))).ToArray();
            if (pool.Length > 0) chosen.Add(pool[random.Next(pool.Length)]);
        }
        return chosen;
    }

    public List<TriadCard> Hand(TriadPlayer player) => player == TriadPlayer.Blue ? Blue : Red;
    public bool Over => Board.All(c => c != null);
    /// <summary>A player's score: the board cards they own plus the cards left in their hand.</summary>
    public int Score(TriadPlayer player)
    {
        var score = Hand(player).Count;
        for (var i = 0; i < 9; i++) if (Board[i] is { } card && Owner[i] == player) score += card.Is(TriadPerk.Crown) ? 2 : 1;
        return score;
    }
    /// <summary>The winner once the board is full, or null for a draw (or while playing).</summary>
    public TriadPlayer? Winner => !Over ? null : Score(TriadPlayer.Blue) > Score(TriadPlayer.Red) ? TriadPlayer.Blue : Score(TriadPlayer.Red) > Score(TriadPlayer.Blue) ? TriadPlayer.Red : null;

    // The cell beside a cell in a direction (0 up, 1 right, 2 down, 3 left), or -1 off the board.
    public static int Neighbour(int cell, int direction) => direction switch
    {
        0 => cell >= 3 ? cell - 3 : -1,
        1 => cell % 3 < 2 ? cell + 1 : -1,
        2 => cell < 6 ? cell + 3 : -1,
        _ => cell % 3 > 0 ? cell - 1 : -1,
    };

    /// <summary>
    /// Ascension and Descension: a typed card's numbers change by the number of cards of its type on the board (up for
    /// Ascension, down for Descension); others never change.
    /// </summary>
    public int Modifier(TriadCard card) => Modifier(card, null);
    /// <summary>The same for a card of a player, whose Materia may make it ascend (and never descend); a Wild card counts as every type.</summary>
    public int Modifier(TriadCard card, TriadPlayer? owner)
    {
        var up = Has(TriadRule.Ascension) || owner is { } o && Edge(o)?.Ascend == true;
        if (!up && !Has(TriadRule.Descension)) return 0;
        int count;
        if (card.Is(TriadPerk.Wild)) count = Enumerable.Range(1, 4).Max(TypeCount);
        else if (card.Type == 0) return 0;
        else count = TypeCount(card.Type);
        return up ? count : -count;
    }
    private int TypeCount(int type) => Board.Count(c => c != null && (c.Type == type || c.Is(TriadPerk.Wild)));
    /// <summary>A card's number on a side as it plays now, clamped to 1-A (10).</summary>
    public int Value(TriadCard card, int direction, int? modifier = null) => Math.Clamp(card.Side(direction) + (modifier ?? Modifier(card)), 1, 10);

    /// <summary>
    /// A card's number on a side with everything that changes it: its owner's Materia (by type, stars, where it lies, and
    /// whether it strikes or guards), what stays on its cell, its own trait, Ascension and Descension; clamped to 1-A.
    /// A card in hand has no cell (-1) and shows as it would strike.
    /// </summary>
    public int Number(TriadCard card, int direction, TriadPlayer owner, int cell, bool attacking, int? modifier = null) =>
        Math.Clamp(Raw(card, direction, owner, cell, attacking, modifier), 1, 10);
    // The same before the clamp (a Mimic's copy is measured from it, so a side far below 1 still copies exactly).
    private int Raw(TriadCard card, int direction, TriadPlayer owner, int cell, bool attacking, int? modifier)
    {
        var value = card.Side(direction);
        if (Edge(owner) is { } edge)
        {
            if (value < edge.Floor) value = edge.Floor;
            value += edge.Fixed(card) + edge.Place(cell) + (attacking ? edge.Attack : edge.Defend);
        }
        if (cell >= 0) value += lasting[cell] + mimicked[cell * 4 + direction];
        if (card.Perk != TriadPerk.None)
        {
            if (card.Is(TriadPerk.Reckless)) value += attacking ? 2 : -1;
            if (card.Is(TriadPerk.Guardian)) value += attacking ? -1 : 2;
            if (card.Is(TriadPerk.Cornerstone) && Corner(cell)) value += 2;
            if (card.Is(TriadPerk.Keystone) && cell == 4) value += 2;
            if (card.Is(TriadPerk.Masked)) value -= 1;
        }
        return value + (modifier ?? Modifier(card, owner));
    }
    /// <summary>The number a board card shows on a side now (as it guards).</summary>
    public int Number(int cell, int direction) => Board[cell] is { } card ? Number(card, direction, Owner[cell], cell, false) : 0;
    private static bool Corner(int cell) => cell is 0 or 2 or 6 or 8;

    // Whether a number takes the facing one: higher (lower under Reverse); Fallen Ace lets 1 take A (A take 1 under
    // Reverse). The attacker's Materia may ignore Reverse or let ties take; an Ace Killer card has Fallen Ace by itself,
    // and a Contrary card compares the other way round (combos pass no card, so they compare normally).
    private bool Beats(int mine, int theirs, bool fallenAce, TriadPlayer attacker = TriadPlayer.Blue, TriadCard? card = null)
    {
        var edge = Edge(attacker);
        var reverse = Has(TriadRule.Reverse) && edge?.IgnoreReverse != true;
        if (card?.Is(TriadPerk.Contrary) == true) reverse = !reverse;
        if (fallenAce && (Has(TriadRule.FallenAce) || card?.Is(TriadPerk.AceKiller) == true) && (reverse ? mine == 10 && theirs == 1 : mine == 1 && theirs == 10)) return true;
        if (edge?.TiesWin == true && mine == theirs) return true;
        return reverse ? mine < theirs : mine > theirs;
    }

    // Whether a card on a cell can be taken: by comparison (Steadfast keeps it), by Same, Plus or a combo (Aegis keeps
    // it); a player's Corner Guard keeps their corners, and their Shell Stone their cards from Same, Plus and combos.
    private bool Takeable(int cell, bool special)
    {
        if (Board[cell] is not { } card || card.Is(special ? TriadPerk.Aegis : TriadPerk.Steadfast)) return false;
        var edge = Edge(Owner[cell]);
        if (special && edge?.Shielded == true) return false;
        return !(Corner(cell) && edge?.CornerGuard == true);
    }

    /// <summary>Whether the current player may play this card of their hand (Order: only the first; Chaos: the one picked).</summary>
    public bool Playable(int handIndex) => handIndex >= 0 && handIndex < Hand(Turn).Count
        && (Edge(Turn)?.FreeChoice == true || (!Has(TriadRule.Order) || handIndex == 0) && (!Has(TriadRule.Chaos) || ChaosPick < 0 || handIndex == ChaosPick));

    private void PickChaos() => ChaosPick = Has(TriadRule.Chaos) && Hand(Turn).Count > 0 && Edge(Turn)?.FreeChoice != true ? random.Next(Hand(Turn).Count) : -1;

    /// <summary>The cells a card would take at a cell for a player, by comparison alone (without placing it).</summary>
    public int[] Takes(TriadCard card, int cell, TriadPlayer player) => [.. Takes(card, cell, player, Arrival(card, cell, player))];

    // What a card placed now gets that stays on it: its owner's bonus for a first card, a last card (two cells or fewer
    // were empty), a card placed while behind, the momentum of a big capture before it, and its bonds; and the card's own
    // Vanguard (its owner's first card) and Desperate (played while behind) traits, which need no Materia.
    private int Arrival(TriadCard card, int cell, TriadPlayer player)
    {
        var p = (int)player;
        var behind = Score(player) < Score(player == TriadPlayer.Blue ? TriadPlayer.Red : TriadPlayer.Blue);
        var bonus = 0;
        if (card.Is(TriadPerk.Vanguard) && placed[p] == 0) bonus += 2;
        if (card.Is(TriadPerk.Desperate) && behind) bonus += 2;
        if (Edge(player) is not { } edge) return bonus;
        bonus += pending[p];
        if (placed[p] == 0) bonus += edge.First;
        if (Board.Count(c => c == null) <= 2) bonus += edge.Last;
        if (edge.Behind != 0 && behind) bonus += edge.Behind;
        if (edge.Bond)
            for (var d = 0; d < 4; d++)
                if (Neighbour(cell, d) is var n and >= 0 && Board[n] is { } other && Owner[n] == player && SameType(card, other)) bonus++;
        return bonus;
    }

    // What a Mimic card placed now copies: for each side facing a card whose number (as it guards) is higher than this
    // side's would be, the difference (the side then reads exactly that number); null for any other card.
    private int[]? Mimicry(TriadCard card, int cell, TriadPlayer player, int arrival, int modifier)
    {
        if (!card.Is(TriadPerk.Mimic)) return null;
        var copied = new int[4];
        for (var d = 0; d < 4; d++)
        {
            if (Neighbour(cell, d) is not (var n and >= 0) || Board[n] is null) continue;
            var theirs = Number(n, (d + 2) % 4);
            if (theirs > Math.Clamp(Number(card, d, player, cell, true, modifier) + arrival, 1, 10)) copied[d] = theirs - (Raw(card, d, player, cell, true, modifier) + arrival);
        }
        return copied;
    }
    private static bool SameType(TriadCard a, TriadCard b) => a.Is(TriadPerk.Wild) || b.Is(TriadPerk.Wild) || a.Type != 0 && a.Type == b.Type;

    private List<int> Takes(TriadCard card, int cell, TriadPlayer player, int arrival)
    {
        var taken = new List<int>();
        var modifier = Modifier(card, player);
        var copied = Board[cell] == null ? Mimicry(card, cell, player, arrival, modifier) : null;
        for (var direction = 0; direction < 4; direction++)
        {
            var next = Neighbour(cell, direction);
            if (next < 0 || Board[next] is null || Owner[next] == player || !Takeable(next, false)) continue;
            var mine = copied is { } c && c[direction] != 0 ? Math.Clamp(Raw(card, direction, player, cell, true, modifier) + arrival + c[direction], 1, 10)
                : Math.Clamp(Number(card, direction, player, cell, true, modifier) + arrival, 1, 10);
            if (Beats(mine, Number(next, (direction + 2) % 4), true, player, card)) taken.Add(next);
        }
        return taken;
    }

    /// <summary>Plays the current player's card from their hand onto an empty cell; returns every cell it took.</summary>
    public int[] Place(int handIndex, int cell) => [.. PlaceCard(handIndex, cell).All];

    public TriadPlacement PlaceCard(int handIndex, int cell)
    {
        var hand = Hand(Turn);
        if (Over || !Playable(handIndex) || cell is < 0 or > 8 || Board[cell] != null) throw new InvalidOperationException("That move isn't allowed.");
        var card = hand[handIndex];
        var player = Turn;
        var other = player == TriadPlayer.Blue ? TriadPlayer.Red : TriadPlayer.Blue;
        var edge = Edge(player);
        // The placed card compares with the type count before it lands.
        var modifier = Modifier(card, player);
        var arrival = Arrival(card, cell, player);
        var taken = Takes(card, cell, player, arrival);
        // A Mimic keeps what it copied (before Same and Plus compare it).
        var copied = Mimicry(card, cell, player, arrival, modifier);
        for (var d = 0; d < 4; d++) mimicked[cell * 4 + d] = copied?[d] ?? 0;
        hand.RemoveAt(handIndex);
        Board[cell] = card; Owner[cell] = player;
        lasting[cell] = arrival;
        pending[(int)player] = 0;
        placed[(int)player]++;
        var same = new List<int>(); var plus = new List<int>();
        var sameOn = Has(TriadRule.Same) || edge?.Same == true;
        var plusOn = Has(TriadRule.Plus) || edge?.Plus == true;
        if (sameOn || plusOn)
        {
            // Each touching side: the neighbour (-1 for a wall counting as A), the placed number and the facing one.
            var sides = new List<(int Cell, int Mine, int Theirs)>();
            for (var d = 0; d < 4; d++)
            {
                var n = Neighbour(cell, d);
                var mine = Number(card, d, player, cell, true, modifier);
                if (n >= 0) { if (Board[n] != null && n != cell) sides.Add((n, mine, Number(n, (d + 2) % 4))); }
                else if (edge?.SameWall == true || edge?.PlusWall == true) sides.Add((-1, mine, 10));
            }
            if (sameOn)
            {
                var equal = sides.Where(x => x.Mine == x.Theirs && (x.Cell >= 0 || edge?.SameWall == true)).ToList();
                if (equal.Count >= 2) same.AddRange(equal.Where(x => x.Cell >= 0 && Owner[x.Cell] != player && Takeable(x.Cell, true)).Select(x => x.Cell));
            }
            if (plusOn)
            {
                var counted = sides.Where(x => x.Cell >= 0 || edge?.PlusWall == true).ToList();
                for (var a = 0; a < counted.Count; a++)
                    for (var b = 0; b < counted.Count; b++)
                        if (a != b && counted[a].Mine + counted[a].Theirs == counted[b].Mine + counted[b].Theirs)
                            foreach (var c in new[] { counted[a].Cell, counted[b].Cell })
                                if (c >= 0 && Owner[c] != player && !plus.Contains(c) && Takeable(c, true)) plus.Add(c);
            }
        }
        // The other player's Reflect undoes the first captures of their cards.
        if (reflects[(int)other] > 0)
            foreach (var c in taken.Concat(same).Concat(plus).Distinct().ToList())
            {
                if (reflects[(int)other] == 0) break;
                reflects[(int)other]--;
                taken.Remove(c); same.RemoveAll(x => x == c); plus.RemoveAll(x => x == c);
            }
        foreach (var c in taken.Concat(same).Concat(plus)) Owner[c] = player;
        // Combo: each card Same or Plus took takes its own weaker neighbours, and so on (by comparison only, no Fallen Ace);
        // an Avalanche card's own captures start combos too. The taking card strikes (Reckless +2, Guardian -1, the
        // taker's Attack Materia) and the other guards.
        var combo = new List<int>();
        var queue = new Queue<int>(same.Concat(plus).Distinct().Concat(card.Is(TriadPerk.Avalanche) ? taken : []));
        while (queue.Count > 0)
        {
            var from = queue.Dequeue();
            for (var d = 0; d < 4; d++)
            {
                var n = Neighbour(from, d);
                if (n < 0 || Board[n] is null || Owner[n] == player || !Takeable(n, true)) continue;
                if (!Beats(Number(Board[from]!, d, player, from, true), Number(n, (d + 2) % 4), false, player)) continue;
                if (reflects[(int)other] > 0) { reflects[(int)other]--; continue; }
                Owner[n] = player; combo.Add(n); queue.Enqueue(n);
            }
        }
        if (edge != null)
        {
            // Frost chills the enemy cards left beside the placed card; Bond strengthens its own kind beside it.
            for (var d = 0; d < 4; d++)
            {
                if (Neighbour(cell, d) is not (var n and >= 0) || Board[n] is not { } beside) continue;
                if (edge.Frost && Owner[n] != player) lasting[n]--;
                if (edge.Bond && Owner[n] == player && SameType(card, beside)) lasting[n]++;
            }
            if (edge.Momentum != 0 && taken.Count + same.Count + plus.Count + combo.Count >= 2) pending[(int)player] = edge.Momentum;
        }
        if (card.Is(TriadPerk.Frostbite | TriadPerk.Rally | TriadPerk.Drain))
        {
            // The card's own traits: Frostbite chills the enemy cards left beside it, Rally strengthens its owner's
            // (cards it just took included), Drain feeds on what the placement took.
            for (var d = 0; d < 4; d++)
            {
                if (Neighbour(cell, d) is not (var n and >= 0) || Board[n] is null) continue;
                if (card.Is(TriadPerk.Frostbite) && Owner[n] != player) lasting[n]--;
                if (card.Is(TriadPerk.Rally) && Owner[n] == player) lasting[n]++;
            }
            if (card.Is(TriadPerk.Drain)) lasting[cell] += taken.Concat(same).Concat(plus).Concat(combo).Distinct().Count();
        }
        Turn = other;
        PickChaos();
        return Last = new(cell, [.. taken], [.. same.Distinct()], [.. plus], [.. combo]);
    }

    /// <summary>
    /// Sudden Death after a draw: a new match where each player holds the cards they own (board and hand), or under
    /// KeepsOwnCards the hands they were dealt, starting with whoever held the unplayed card; at most five restarts.
    /// False when the rule isn't in play or the match was won.
    /// </summary>
    public bool RestartSuddenDeath()
    {
        if (!Has(TriadRule.SuddenDeath) || !Over || Winner != null || Restarts >= 5) return false;
        List<TriadCard> blue = KeepsOwnCards ? [.. dealtBlue] : [.. Enumerable.Range(0, 9).Where(i => Owner[i] == TriadPlayer.Blue).Select(i => Board[i]!).Concat(Blue)];
        List<TriadCard> red = KeepsOwnCards ? [.. dealtRed] : [.. Enumerable.Range(0, 9).Where(i => Owner[i] == TriadPlayer.Red).Select(i => Board[i]!).Concat(Red)];
        if (blue.Count != 5 || red.Count != 5) return false;
        var first = Blue.Count > 0 ? TriadPlayer.Blue : TriadPlayer.Red;
        Array.Clear(Board); Blue.Clear(); Blue.AddRange(blue); Red.Clear(); Red.AddRange(red);
        Turn = first; Restarts++; Last = null;
        Array.Clear(lasting); Array.Clear(mimicked); Array.Clear(placed); Array.Clear(pending);
        PickChaos();
        return true;
    }

    /// <summary>
    /// The opponent's move for the current player: for each move it may make, the board after it and after the other
    /// player's best reply; then the safest placement, the lowest card spent on ties; random among equals.
    /// </summary>
    public (int Hand, int Cell) Choose(Random random)
    {
        var me = Turn;
        var best = (Hand: -1, Cell: -1); var bestScore = double.MinValue;
        for (var h = 0; h < Hand(me).Count; h++)
        {
            if (!Playable(h)) continue;
            for (var cell = 0; cell < 9; cell++)
            {
                if (Board[cell] != null) continue;
                var card = Hand(me)[h];
                var after = new TriadGame(this); after.PlaceCard(h, cell);
                var score = (double)Margin(after, me) * 100;
                if (!after.Over)
                {
                    // The reply that hurts most.
                    var worst = double.MaxValue;
                    for (var rh = 0; rh < after.Hand(after.Turn).Count; rh++)
                        for (var rc = 0; rc < 9; rc++)
                        {
                            if (after.Board[rc] != null) continue;
                            var reply = new TriadGame(after) { ChaosPick = -1 };
                            if (!reply.Playable(rh)) continue;
                            reply.PlaceCard(rh, rc);
                            worst = Math.Min(worst, Margin(reply, me));
                        }
                    if (worst != double.MaxValue) score = score * .4 + worst * 60;
                }
                var exposed = 10;
                for (var d = 0; d < 4; d++) { var n = Neighbour(cell, d); if (n >= 0 && Board[n] == null) exposed = Math.Min(exposed, Value(card, d)); }
                score += exposed * 3 - (card.Top + card.Right + card.Bottom + card.Left) * .4 + random.NextDouble();
                if (score > bestScore) { bestScore = score; best = (h, cell); }
            }
        }
        return best;
    }
    private static int Margin(TriadGame game, TriadPlayer me) =>
        game.Score(me) - game.Score(me == TriadPlayer.Blue ? TriadPlayer.Red : TriadPlayer.Blue);

    /// <summary>
    /// Five cards from a pool, as the game's deck rules allow: at most one five-star card, at most two of four stars or
    /// more, and with a five-star card only one four-star. Fewer than five in the pool fills from cards up to three stars.
    /// </summary>
    public static TriadCard[] Deal(IReadOnlyList<TriadCard> pool, IReadOnlyList<TriadCard> all, Random random)
    {
        var shuffled = pool.OrderBy(_ => random.Next()).ToList();
        var hand = new List<TriadCard>();
        foreach (var card in shuffled.Concat(all.Where(c => c.Stars <= 3).OrderBy(_ => random.Next())))
        {
            if (hand.Count == 5) break;
            if (hand.Any(c => c.Id == card.Id) || !DeckAllows(hand, card)) continue;
            hand.Add(card);
        }
        return [.. hand];
    }
    /// <summary>Whether the deck rules allow adding a card to a deck.</summary>
    public static bool DeckAllows(IReadOnlyCollection<TriadCard> deck, TriadCard card)
    {
        var fives = deck.Count(c => c.Stars >= 5) + (card.Stars >= 5 ? 1 : 0);
        var strong = deck.Count(c => c.Stars >= 4) + (card.Stars >= 4 ? 1 : 0);
        return fives <= 1 && strong <= 2;
    }

    /// <summary>An NPC's deck: every fixed card, the rest drawn from its variable cards.</summary>
    public static TriadCard[] NpcDeck(IReadOnlyList<TriadCard> fixedCards, IReadOnlyList<TriadCard> variable, Random random)
    {
        var deck = fixedCards.Take(5).ToList();
        foreach (var card in variable.OrderBy(_ => random.Next()).Where(c => deck.All(d => d.Id != c.Id))) { if (deck.Count == 5) break; deck.Add(card); }
        return [.. deck];
    }
}
