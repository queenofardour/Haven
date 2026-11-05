namespace Haven.Core.Odyssey;

/// <summary>How a duel ended: the match maps its TriadOutcome to this (None is never reported). A draw is one Sudden Death could not settle.</summary>
public enum DuelOutcome { Won, Lost, Drawn, Forfeit }

/// <summary>
/// What the match or the simulator reports back from a duel: how it ended, the score margin (the player's score minus
/// the opponent's), the player's captures by card id and their best single placement. DuelTally builds one.
/// </summary>
public sealed record DuelReport(DuelOutcome Outcome, int Margin, IReadOnlyDictionary<uint, int> Captures, int BestPlacement, uint BestPlacementCard);

/// <summary>Counts the player's captures as a duel plays (Note after each placement of theirs), then makes the report.</summary>
public sealed class DuelTally
{
    private readonly Dictionary<uint, int> captures = [];
    public int BestPlacement { get; private set; }
    public uint BestPlacementCard { get; private set; }
    public void Note(TriadCard card, TriadPlacement placement)
    {
        var count = placement.All.Count();
        if (count == 0) return;
        captures[card.Id] = captures.GetValueOrDefault(card.Id) + count;
        if (count > BestPlacement) { BestPlacement = count; BestPlacementCard = card.Id; }
    }
    public DuelReport Report(TriadGame game, DuelOutcome outcome) =>
        new(outcome, game.Score(TriadPlayer.Blue) - game.Score(TriadPlayer.Red), captures, BestPlacement, BestPlacementCard);
}

/// <summary>
/// A dealt duel, ready to play: NewGame builds the engine's game; Brain is the opponent's. The player is always blue.
/// </summary>
public sealed record DuelPlan(IReadOnlyList<TriadCard> Blue, IReadOnlyList<TriadCard> Red, TriadPlayer First, IReadOnlyList<TriadRule> Rules,
    TriadEdge BlueEdge, TriadEdge? RedEdge, int AiLevel, uint Music, int Seed, IReadOnlyList<TriadCard> Unseen, ITriadBrain Brain,
    OpponentPlan Opponent, double PlayerPower, double OpponentPower, string LossNote)
{
    public double PowerGap => OpponentPower - PlayerPower;
    /// <summary>Whether a rule of chance is in play (Chaos, Swap, Roulette), for the Moogle's Verdict.</summary>
    public bool Luck => Rules.Any(r => r is TriadRule.Chaos or TriadRule.Swap or TriadRule.Roulette);
    // Sudden Death replays the hands as dealt: an upgraded card the opponent captured never plays for them.
    public TriadGame NewGame() => new(Blue, Red, First, Rules, new Random(Seed), BlueEdge, RedEdge, RouletteSkips) { KeepsOwnCards = true };
    /// <summary>Rules Roulette may not roll in this duel (OdysseyBalance.RouletteSkips).</summary>
    public IReadOnlyList<TriadRule> RouletteSkips { get; init; } = [];

    /// <summary>
    /// Plays the duel out headless with a brain for each side (the simulator, the tests): Sudden Death restarts included;
    /// a draw it can't settle is Drawn.
    /// </summary>
    public DuelReport Play(ITriadBrain blue, ITriadBrain? red = null, Random? random = null)
    {
        red ??= Brain;
        random ??= new Random(Seed);
        var game = NewGame();
        var tally = new DuelTally();
        while (true)
        {
            while (!game.Over)
            {
                var brain = game.Turn == TriadPlayer.Blue ? blue : red;
                var (hand, cell) = brain.Choose(game.Copy(), random);
                var card = game.Hand(game.Turn)[hand];
                var mine = game.Turn == TriadPlayer.Blue;
                var placement = game.PlaceCard(hand, cell);
                if (mine) tally.Note(card, placement);
            }
            if (game.Winner != null || !game.RestartSuddenDeath()) break;
        }
        return tally.Report(game, game.Winner switch { TriadPlayer.Blue => DuelOutcome.Won, TriadPlayer.Red => DuelOutcome.Lost, _ => DuelOutcome.Drawn });
    }
}

/// <summary>
/// The Crystal Odyssey's director: the run's logic as a state machine over its screens (design §1, §7). The UI reads
/// a view model per screen and calls commands; the simulator does the same headless. Every random decision draws from
/// the run's seed and step, so saving and loading never rerolls anything. Commands throw InvalidOperationException
/// when the screen or the choice doesn't allow them; the views say what is allowed and why not.
/// </summary>
public sealed partial class Odyssey
{
    private readonly IReadOnlyList<TriadCard> cards;
    private readonly Dictionary<uint, TriadCard> byId;
    private readonly Func<int, IReadOnlyList<TriadCard>, ITriadBrain> brainFactory;
    private readonly TriadPower.Scale scale;

    /// <summary>The run in progress (or just over); null before any run.</summary>
    public OdysseyRun? Run { get; private set; }
    public OdysseyRecords Records { get; }
    /// <summary>Raised after every command, so the plugin can save the run (and the records when a run ends).</summary>
    public event Action? Changed;

    public Odyssey(IReadOnlyList<TriadCard> cards, Func<int, IReadOnlyList<TriadCard>, ITriadBrain> brainFactory, OdysseyRecords? records = null, OdysseyRun? run = null)
    {
        this.cards = cards;
        byId = cards.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        this.brainFactory = brainFactory;
        scale = new TriadPower.Scale(cards);
        Records = records ?? new();
        Run = run;
    }

    /// <summary>The screen to show: Title without a run; a finished run shows its summary until CloseSummary.</summary>
    public OdysseyScreen Screen => Run?.Screen ?? OdysseyScreen.Title;
    public World World => OdysseyContent.WorldAt(R.World);
    private OdysseyRun R => Run ?? throw new InvalidOperationException("No run is in progress.");

    // ---- Cards ----

    public double Power(TriadCard card) => TriadPower.Score(card, scale);
    public double Power(IEnumerable<TriadCard> deck) => TriadPower.Deck(deck, scale);
    private TriadCard Base(uint id) => byId.TryGetValue(id, out var card) ? card : new TriadCard(id, "Unknown card " + id, 1, 1, 1, 1, 1);
    /// <summary>A binder card as it plays: its base card with the raised sides (clamped to A), its perk and its changed type.</summary>
    public TriadCard Card(BinderCard b)
    {
        var c = Base(b.Id);
        return new TriadCard(c.Id, c.Name, Math.Clamp(c.Top + b.Raised[0], 1, 10), Math.Clamp(c.Right + b.Raised[1], 1, 10), Math.Clamp(c.Bottom + b.Raised[2], 1, 10),
            Math.Clamp(c.Left + b.Raised[3], 1, 10), c.Stars, b.Type >= 0 ? b.Type : c.Type) { Perk = b.Perk };
    }
    private BinderCard Find(int uid) => R.Binder.FirstOrDefault(b => b.Uid == uid) ?? throw new InvalidOperationException("No such card in the binder.");
    private BinderCard AddToBinder(TriadCard card)
    {
        var b = ToBinder(card);
        b.Uid = R.NextUid++; b.World = R.World; b.Duel = R.Duel;
        R.Binder.Add(b);
        return b;
    }
    // A card (an opponent's enhanced one, say) as a binder entry: what it has over its base card.
    private BinderCard ToBinder(TriadCard card)
    {
        var c = Base(card.Id);
        return new BinderCard { Id = card.Id, Raised = [card.Top - c.Top, card.Right - c.Right, card.Bottom - c.Bottom, card.Left - c.Left], Perk = card.Perk, Type = card.Type != c.Type ? card.Type : -1 };
    }
    private bool Has(RunEffect effect) => R.Materia.Any(k => OdysseyContent.MateriaOf(k)?.Effect == effect);
    private bool RoyalCharter => Has(RunEffect.RoyalCharter);

    /// <summary>Whether five cards of a binder could still make a legal deck (distinct ids, under the limits); removals must keep this true.</summary>
    public bool CanFormDeck(IEnumerable<BinderCard> binder)
    {
        var deck = new List<TriadCard>();
        foreach (var card in binder.Select(Card).OrderBy(c => c.Stars).ThenBy(c => c.Id))
            if (deck.All(d => d.Id != card.Id) && OdysseyBalance.DeckAllows(deck, card, RoyalCharter)) { deck.Add(card); if (deck.Count == 5) return true; }
        return false;
    }

    private void Touch() => Changed?.Invoke();
    private void Require(OdysseyScreen screen)
    {
        if (Screen != screen) throw new InvalidOperationException($"Not on the {screen} screen.");
    }

    // ---- Title and the starting binder ----

    /// <summary>Whether there is a run to continue.</summary>
    public bool CanContinue => Run is { Over: false, Screen: not OdysseyScreen.Title };

    /// <summary>Starts a run at a Crystal Rank the records allow (0 up to one above the best cleared), dealing the starting binder. Started defaults to now.</summary>
    public void NewRun(int seed, int rank = 0, DateTime? started = null)
    {
        if (rank < 0 || rank > Records.UnlockedRank) throw new InvalidOperationException("That Crystal Rank isn't unlocked.");
        // A run under way that this replaces goes into the records as abandoned (one still choosing its starting binder
        // never began).
        if (Run is { Over: false, Screen: not OdysseyScreen.StartingBinder }) Abandon();
        Run = new OdysseyRun { Seed = seed, Rank = rank, Started = started ?? DateTime.UtcNow, Mgp = OdysseyBalance.StartingMgp, ReDealsLeft = OdysseyBalance.ReDeals(rank) };
        DealStartingBinder();
        R.Screen = OdysseyScreen.StartingBinder;
        Touch();
    }

    private void DealStartingBinder()
    {
        var random = R.Next();
        var (low, high) = OdysseyBalance.StartingPower(R.Rank);
        List<TriadCard> best = [];
        var bestDistance = double.MaxValue;
        for (var attempt = 0; attempt < OdysseyBalance.StartingDrawAttempts; attempt++)
        {
            var drawn = new List<TriadCard>();
            foreach (var stars in OdysseyBalance.StartingStars)
            {
                var pool = cards.Where(c => c.Stars == stars && Shown(c) && drawn.All(d => d.Id != c.Id)).ToList();
                if (pool.Count == 0) pool = cards.Where(c => Shown(c) && drawn.All(d => d.Id != c.Id)).ToList();
                if (pool.Count > 0) drawn.Add(pool[random.Next(pool.Count)]);
            }
            var mean = Power(drawn);
            var distance = mean < low ? low - mean : mean > high ? mean - high : 0;
            if (distance < bestDistance) { bestDistance = distance; best = drawn; }
            if (distance == 0) break;
        }
        R.Binder.Clear();
        foreach (var card in best) AddToBinder(card);
    }

    /// <summary>Deals the starting binder again (limited).</summary>
    public void ReDeal()
    {
        Require(OdysseyScreen.StartingBinder);
        if (R.ReDealsLeft <= 0) throw new InvalidOperationException("No re-deals left.");
        R.ReDealsLeft--;
        R.NextUid = 1;
        DealStartingBinder();
        Touch();
    }

    /// <summary>Accepts the starting binder and offers the starting Enhancement; world I opens after it.</summary>
    public void Begin()
    {
        Require(OdysseyScreen.StartingBinder);
        EnterWorld(1);
        OfferStartingEnhancement();
        Touch();
    }

    private void EnterWorld(int world)
    {
        R.World = world; R.Duel = 1;
        R.CardSharpUsed = R.PinionUsed = R.EventLastDuel = false;
        R.UsedNames.Clear();
        R.Shop = null; R.Path.Clear(); R.Opponent = null;
        R.Screen = OdysseyScreen.WorldTitle;
    }

    /// <summary>Leaves the world's title card for the first path.</summary>
    public void EnterPath()
    {
        Require(OdysseyScreen.WorldTitle);
        OfferPath();
        Touch();
    }

    // ---- Path ----

    private void OfferPath()
    {
        R.Path.Clear();
        if (R.Duel >= OdysseyBalance.BossDuel) R.Path.Add(BossPlan(R.World));
        else
        {
            var random = R.Next();
            var count = random.NextDouble() < OdysseyBalance.ThirdChoiceChance ? 3 : 2;
            var eliteAt = OdysseyBalance.EliteGuaranteed(R.World, R.Rank) || random.NextDouble() < OdysseyBalance.EliteChance ? random.Next(count) : -1;
            for (var i = 0; i < count; i++) R.Path.Add(i == eliteAt ? ElitePlan(random) : RegularPlan(random));
        }
        R.Screen = OdysseyScreen.Path;
    }

    /// <summary>Picks an opponent from the path and opens the Deck screen.</summary>
    public void ChoosePath(int index)
    {
        Require(OdysseyScreen.Path);
        if (index < 0 || index >= R.Path.Count) throw new InvalidOperationException("No such path.");
        FaceOpponent(R.Path[index]);
        Touch();
    }

    private void FaceOpponent(OpponentPlan opponent)
    {
        R.Opponent = opponent;
        R.Path.Clear();
        MendBinder();
        // The last deck is kept where its cards still exist and fit.
        R.Prep = new DuelPrep { Deck = [.. R.Prep.Deck.Where(uid => R.Binder.Any(b => b.Uid == uid))] };
        while (!DeckLegal(R.Prep.Deck) && R.Prep.Deck.Count > 0) R.Prep.Deck.RemoveAt(R.Prep.Deck.Count - 1);
        R.Screen = OdysseyScreen.Deck;
    }

    // ---- Deck ----

    private bool DeckLegal(IReadOnlyList<int> uids)
    {
        var deck = new List<TriadCard>();
        foreach (var uid in uids)
        {
            var card = Card(Find(uid));
            if (deck.Any(d => d.Id == card.Id) || !OdysseyBalance.DeckAllows(deck, card, RoyalCharter)) return false;
            deck.Add(card);
        }
        return deck.Count <= OdysseyBalance.DeckSize;
    }
    /// <summary>Why a binder card can't join the deck now (null: it can, or it is in it).</summary>
    public string? DeckRefusal(int uid)
    {
        if (R.Prep.Deck.Contains(uid)) return null;
        if (R.Prep.Deck.Count >= OdysseyBalance.DeckSize) return "The deck is full.";
        var deck = R.Prep.Deck.Select(u => Card(Find(u))).ToList();
        var card = Card(Find(uid));
        if (deck.Any(d => d.Id == card.Id)) return "A deck can't hold two of one card.";
        var (fives, strong) = OdysseyBalance.DeckLimits(RoyalCharter);
        if (!OdysseyBalance.DeckAllows(deck, card, RoyalCharter))
            return OdysseyBalance.LimitStars(card) >= 5 && deck.Count(d => OdysseyBalance.LimitStars(d) >= 5) >= fives ? $"At most {(fives == 1 ? "one" : "two")} five-star card{(fives == 1 ? "" : "s")}." : $"At most {(strong == 2 ? "two" : "three")} cards of four stars or more.";
        return null;
    }

    /// <summary>Puts a binder card into the deck or takes it out.</summary>
    public void ToggleDeckCard(int uid)
    {
        Require(OdysseyScreen.Deck);
        if (R.Prep.Deck.Remove(uid))
        {
            // A Potion drunk by a card taken back out goes back in the bag, unspent (it was lost before).
            if (R.Prep.Potions.Remove(uid) && OdysseyContent.Items.FirstOrDefault(i => i.Kind == ItemKind.Potion) is { } potion)
            {
                R.Items.Add(potion.Key);
                R.Prep.Used.Remove(potion.Key);
            }
            Touch();
            return;
        }
        if (DeckRefusal(uid) is { } why) throw new InvalidOperationException(why);
        R.Prep.Deck.Add(uid);
        Touch();
    }

    /// <summary>
    /// Why an item can't be used now (null: it can). A Potion needs a deck card; an Echo Screen a rule of the opponent's;
    /// Queen of Cards' Favour a rule to add (AddableRules).
    /// </summary>
    public string? ItemRefusal(int index, int cardUid = -1, TriadRule rule = TriadRule.SuddenDeath)
    {
        if (Screen != OdysseyScreen.Deck) return "Items are used on the Deck screen.";
        if (index < 0 || index >= R.Items.Count) return "No such item.";
        var item = OdysseyContent.ItemOf(R.Items[index]) ?? throw new InvalidOperationException("Unknown item.");
        var o = R.Opponent!;
        var p = R.Prep;
        var rules = PlayedRules();
        return item.Kind switch
        {
            ItemKind.PhoenixDown => "A Phoenix Down is held; it works by itself when you lose.",
            ItemKind.Potion => cardUid < 0 ? "Choose a deck card." : !p.Deck.Contains(cardUid) ? "That card isn't in the deck." : p.Potions.Contains(cardUid) ? "That card already had a Potion." : null,
            ItemKind.Ether => p.Ether ? "You already see their hand." : null,
            ItemKind.GysahlGreens => p.Gysahl ? "You already move first." : null,
            ItemKind.HiPotion => p.HiPotion ? "A Hi-Potion is already in effect." : null,
            ItemKind.Elixir => p.Elixir ? "An Elixir is already in effect." : null,
            ItemKind.Megalixir => p.Megalixir ? "A Megalixir is already in effect." : null,
            ItemKind.Remedy => o.Materia.Count == 0 ? "They carry no Materia." : p.Remedy || p.Ribbon ? "Their Materia is already cancelled." : null,
            // A map's reward would be lost with the duel.
            // So would the other items already used on this duel.
            ItemKind.SmokeBomb => o.Kind != OpponentKind.Regular ? "Only a regular duel can be skipped." : p.Map != null ? "A Treasure Map is in use: win the duel for its reward."
                : p.Used.Count > 0 ? "Items are already used on this duel; they'd be lost with it." : null,
            ItemKind.EchoScreen => p.Echo != null ? "A rule is already cancelled."
                : rule == TriadRule.SuddenDeath ? "Choose one of their rules (not Sudden Death)." : !o.Rules.Contains(rule) || !rules.Contains(rule) ? "They don't play that rule." : null,
            ItemKind.Tonic => p.Tonics.Contains(item.Key) ? $"A {item.Name} is already in effect." : item.Refuse?.Invoke(rules),
            ItemKind.TreasureMap => p.Map != null ? "A Treasure Map is already in use." : null,
            ItemKind.PrismPowder => p.Hidden ? "Your hand is hidden already." : !rules.Any(r => r is TriadRule.AllOpen or TriadRule.ThreeOpen) ? "No Open rule shows them your hand." : null,
            ItemKind.AddRule => p.Added != null ? "A rule is already added." : rule == TriadRule.SuddenDeath ? "Choose a rule to add."
                : !OdysseyContent.AddableRules.Contains(rule) ? "The Queen's Favour can't add that rule." : rules.Contains(rule) ? "That rule is in play already."
                : !rules.All(r => TriadGame.Compatible(r, rule)) ? "That rule doesn't fit with theirs." : null,
            ItemKind.Ribbon => p.Ribbon ? "A Ribbon is already in effect."
                : rules.All(r => r is TriadRule.AllOpen or TriadRule.ThreeOpen or TriadRule.SuddenDeath || r == p.Added) && (o.Materia.Count == 0 || p.Remedy) ? "There's nothing of theirs to cancel." : null,
            ItemKind.Pandora => p.Pandora != null ? "The box is already open." : PandoraChoices().Count == 0 ? "You hold every Legendary Materia." : null,
            _ => null,
        };
    }

    // The rules the duel will be played under: theirs less an Echo Screen's (only the Open rule under a Ribbon), a
    // Favour's added rule, and Sudden Death.
    private List<TriadRule> PlayedRules()
    {
        var o = R.Opponent!;
        var p = R.Prep;
        var rules = o.Rules.Where(r => r != p.Echo && (!p.Ribbon || r is TriadRule.AllOpen or TriadRule.ThreeOpen or TriadRule.SuddenDeath)).ToList();
        if (p.Added is { } added && !rules.Contains(added))
        {
            var sudden = rules.IndexOf(TriadRule.SuddenDeath);
            rules.Insert(sudden < 0 ? rules.Count : sudden, added);
        }
        if (!rules.Contains(TriadRule.SuddenDeath)) rules.Add(TriadRule.SuddenDeath);
        return rules;
    }

    /// <summary>The rules Queen of Cards' Favour could add now: from its pool, not in play, and fitting with every rule that is.</summary>
    public IReadOnlyList<TriadRule> AddableRules()
    {
        if (Screen != OdysseyScreen.Deck) return [];
        var rules = PlayedRules();
        return [.. OdysseyContent.AddableRules.Where(r => rules.All(x => TriadGame.Compatible(x, r)))];
    }

    // Pandora's Box: the Legendary Materia not slotted.
    private List<Materia> PandoraChoices() => [.. OdysseyContent.AllMateria.Where(m => m.Rarity == Rarity.Legendary && m.InDuel && !R.Materia.Contains(m.Key))];

    /// <summary>Uses an item for this duel (a Smoke Bomb skips it at once).</summary>
    public void UseItem(int index, int cardUid = -1, TriadRule rule = TriadRule.SuddenDeath)
    {
        Require(OdysseyScreen.Deck);
        if (ItemRefusal(index, cardUid, rule) is { } why) throw new InvalidOperationException(why);
        var key = R.Items[index];
        var item = OdysseyContent.ItemOf(key)!;
        R.Items.RemoveAt(index);
        R.Prep.Used.Add(key);
        switch (item.Kind)
        {
            case ItemKind.Potion: R.Prep.Potions.Add(cardUid); break;
            case ItemKind.Ether: R.Prep.Ether = true; break;
            case ItemKind.GysahlGreens: R.Prep.Gysahl = true; break;
            case ItemKind.HiPotion: R.Prep.HiPotion = true; break;
            case ItemKind.Elixir: R.Prep.Elixir = true; break;
            case ItemKind.Megalixir: R.Prep.Megalixir = true; break;
            case ItemKind.Remedy: R.Prep.Remedy = true; break;
            case ItemKind.EchoScreen: R.Prep.Echo = rule; break;
            case ItemKind.Tonic: R.Prep.Tonics.Add(key); break;
            case ItemKind.TreasureMap: R.Prep.Map = item.MapMinimum; break;
            case ItemKind.PrismPowder: R.Prep.Hidden = true; break;
            case ItemKind.AddRule: R.Prep.Added = rule; break;
            case ItemKind.Ribbon: R.Prep.Ribbon = true; break;
            case ItemKind.Pandora:
                // Rolled now, before the duel starts, so the deck can be built around it.
                var box = PandoraChoices();
                R.Prep.Pandora = box[R.Next().Next(box.Count)].Key;
                break;
            case ItemKind.SmokeBomb:
                R.Opponent = null;
                AfterDuel(skipped: true);
                break;
        }
        Touch();
    }

    /// <summary>Whether the chosen deck keeps the limits now (a Royal Charter dropped after choosing can break them).</summary>
    public bool DeckLegalNow => Run != null && DeckLegal(R.Prep.Deck);
    /// <summary>Why the duel can't start (null: it can).</summary>
    public string? StartRefusal() => Screen != OdysseyScreen.Deck ? "Not on the Deck screen." : R.Prep.Deck.Count < OdysseyBalance.DeckSize ? "Choose five cards."
        : !DeckLegalNow ? "The deck breaks the limits." : null;

    /// <summary>Deals the duel from the chosen deck and items, and opens the Duel screen.</summary>
    public DuelPlan StartDuel()
    {
        if (StartRefusal() is { } why) throw new InvalidOperationException(why);
        Deal();
        R.Screen = OdysseyScreen.Duel;
        Touch();
        return Plan();
    }

    private void Deal()
    {
        var random = R.Next();
        var o = R.Opponent!;
        var p = R.Prep;
        var blue = new List<CardState>();
        foreach (var uid in p.Deck)
        {
            var c = Card(Find(uid));
            var up = (p.Potions.Contains(uid) ? 1 : 0) + (p.HiPotion ? 1 : 0) + (p.Elixir ? 2 : 0) + (p.Megalixir ? 2 : 0);
            blue.Add(CardState.From(c with { Top = Math.Clamp(c.Top + up, 1, 10), Right = Math.Clamp(c.Right + up, 1, 10), Bottom = Math.Clamp(c.Bottom + up, 1, 10), Left = Math.Clamp(c.Left + up, 1, 10) }));
        }
        var rules = PlayedRules();
        var unseen = R.Binder.Where(b => !p.Deck.Contains(b.Uid)).Select(b => CardState.From(Card(b))).Concat(blue).ToList();
        var first = p.Gysahl ? TriadPlayer.Blue : random.Next(2) == 0 ? TriadPlayer.Blue : TriadPlayer.Red;
        R.Dealt = new DuelState
        {
            Blue = blue, Red = [.. o.Deck.Select(c => new CardState { Id = c.Id, Name = c.Name, Top = c.Top, Right = c.Right, Bottom = c.Bottom, Left = c.Left, Stars = c.Stars, Type = c.Type, Perk = c.Perk })],
            First = first, Rules = rules,
            BlueMateria = [.. R.Materia.Where(k => OdysseyContent.MateriaOf(k)?.InDuel == true), .. p.Pandora is { } box ? [box] : Array.Empty<string>()],
            RedMateria = p.Remedy || p.Ribbon ? [] : [.. o.Materia],
            ExtraReveal = p.Ether, ExtraReflect = p.Megalixir ? 1 : 0,
            BlueTonics = [.. p.Tonics.Where(k => OdysseyContent.ItemOf(k)?.Mine != null)],
            RedTonics = [.. p.Tonics.Where(k => OdysseyContent.ItemOf(k)?.Theirs != null)],
            Hidden = p.Hidden,
            AiLevel = o.AiLevel, Music = o.Music, Seed = random.Next(),
            Unseen = unseen,
            PlayerPower = Power(blue.Select(c => c.ToCard())), OpponentPower = o.Power,
            Attempt = (R.Dealt?.Attempt ?? 0) + 1,
        };
    }

    /// <summary>The dealt duel, for the match or the simulator.</summary>
    public DuelPlan Plan()
    {
        Require(OdysseyScreen.Duel);
        var d = R.Dealt ?? throw new InvalidOperationException("No duel is dealt.");
        var o = R.Opponent!;
        var blueEdge = OdysseyContent.Edge(d.BlueMateria);
        if (d.ExtraReveal) blueEdge.Reveal = true;
        blueEdge.Reflect += d.ExtraReflect;
        foreach (var key in d.BlueTonics) OdysseyContent.ItemOf(key)?.Mine?.Invoke(blueEdge);
        if (d.Hidden) blueEdge.Concealed = true;
        // A tonic on their side makes them an edge even without Materia (and a Remedy doesn't lift it).
        var redEdge = d.RedMateria.Count > 0 || d.RedTonics.Count > 0 ? OdysseyContent.Edge(d.RedMateria) : null;
        if (redEdge != null) foreach (var key in d.RedTonics) OdysseyContent.ItemOf(key)?.Theirs?.Invoke(redEdge);
        var unseen = d.Unseen.Select(c => c.ToCard()).ToList();
        var note = R.Items.Contains("phoenix-down") ? "Your Phoenix Down flutters: the duel is dealt again."
            : Has(RunEffect.PhoenixPinion) && !R.PinionUsed ? "Your Phoenix Pinion glows: the duel is dealt again."
            : "The Odyssey ends here.";
        return new DuelPlan([.. d.Blue.Select(c => c.ToCard())], [.. d.Red.Select(c => c.ToCard())], d.First, d.Rules, blueEdge, redEdge, d.AiLevel, d.Music, d.Seed,
            unseen, brainFactory(d.AiLevel, unseen), o, d.PlayerPower, d.OpponentPower, note) { RouletteSkips = OdysseyBalance.RouletteSkips(R.World) };
    }

    /// <summary>Deals the current duel again (a draw, a revive, or a run resumed mid-duel).</summary>
    public void Redeal()
    {
        Require(OdysseyScreen.Duel);
        Deal();
        Touch();
    }

    /// <summary>Gives up the duel: the run ends (the UI warns first).</summary>
    public void Forfeit()
    {
        Require(OdysseyScreen.Duel);
        Resolve(new DuelReport(DuelOutcome.Forfeit, -5, new Dictionary<uint, int>(), 0, 0));
    }

    // ---- Resolve ----

    /// <summary>Takes a duel's result: a win goes on to the reward, a draw deals again, a loss revives or ends the run.</summary>
    public void Resolve(DuelReport report)
    {
        Require(OdysseyScreen.Duel);
        var d = R.Dealt!;
        var o = R.Opponent!;
        var luck = d.Rules.Any(r => r is TriadRule.Chaos or TriadRule.Swap or TriadRule.Roulette);
        var log = new DuelLog
        {
            World = R.World, Duel = R.Duel, Opponent = o.Name, Kind = o.Kind, Outcome = report.Outcome.ToString(), Margin = report.Margin,
            PlayerPower = d.PlayerPower, OpponentPower = d.OpponentPower, Rules = [.. d.Rules], Luck = luck,
        };
        R.Stats.DuelsPlayed++;
        foreach (var (id, n) in report.Captures) R.Stats.Captures[id] = R.Stats.Captures.GetValueOrDefault(id) + n;
        if (report.BestPlacement > R.Stats.BestPlacement)
        {
            R.Stats.BestPlacement = report.BestPlacement; R.Stats.BestPlacementCard = report.BestPlacementCard;
            R.Stats.BestPlacementWhere = $"{World.Numeral}, against {o.Name}";
        }
        switch (report.Outcome)
        {
            case DuelOutcome.Drawn:
                log.Revived = "drawn: dealt again";
                R.Stats.Duels.Add(log);
                Deal();
                break;
            case DuelOutcome.Lost when R.Items.Contains("phoenix-down"):
                // A held Phoenix Down is used up by itself; then a Phoenix Pinion, once a world.
                R.Items.Remove("phoenix-down");
                log.Revived = "Phoenix Down";
                R.Stats.Duels.Add(log); R.Stats.Revives++;
                Deal();
                break;
            case DuelOutcome.Lost when Has(RunEffect.PhoenixPinion) && !R.PinionUsed:
                R.PinionUsed = true;
                log.Revived = "Phoenix Pinion";
                R.Stats.Duels.Add(log); R.Stats.Revives++;
                Deal();
                break;
            case DuelOutcome.Lost or DuelOutcome.Forfeit:
                R.Stats.Duels.Add(log);
                R.LastGap = d.OpponentPower - d.PlayerPower; R.LastMargin = -report.Margin; R.LastLuck = luck;
                R.Verdict = report.Outcome == DuelOutcome.Forfeit ? "You walked away from the table, kupo." : OdysseyContent.Verdict(R.LastGap, R.LastMargin, luck);
                Finish(won: false);
                break;
            case DuelOutcome.Won:
                R.Stats.DuelsWon++;
                log.Mgp = OdysseyBalance.Mgp(R.World, report.Margin, o.IsElite, o.IsBoss, Has(RunEffect.MooglePurse));
                R.Mgp += log.Mgp; R.Stats.MgpEarned += log.Mgp;
                R.Stats.Duels.Add(log);
                if (Has(RunEffect.Mug) && o.Deck.Count > 0)
                {
                    var mug = o.Deck[R.Next().Next(o.Deck.Count)].ToCard();
                    AddToBinder(mug);
                    R.Stats.Claimed.Add(mug.Id);
                }
                if (o.IsBoss && R.World == OdysseyBalance.Worlds && OdysseyBalance.EndsingerSecondForm(R.Rank) && !R.SecondFormDone)
                {
                    // The Endsinger's second form: a fresh deal straight after, no reward between.
                    R.SecondFormDone = true;
                    R.Opponent = SecondFormPlan();
                    R.Prep = new DuelPrep { Deck = [.. R.Prep.Deck], Map = R.Prep.Map };   // a map used on the first form pays out after the second
                    R.Dealt = null;
                    Deal();
                    break;
                }
                OfferReward();
                break;
            default: throw new InvalidOperationException("The duel hasn't ended.");
        }
        Touch();
    }

    // What follows a finished duel's reward (or a skipped duel): the boss's shop or the victory, an event, or the next path.
    private void AfterDuel(bool skipped = false)
    {
        var o = R.Opponent;
        R.Reward = null; R.Upgrade = null; R.Swap = null; R.Event = null; R.Dealt = null;
        if (o?.Kind == OpponentKind.Boss)
        {
            R.Opponent = null;
            if (R.World >= OdysseyBalance.Worlds) { Finish(won: true); return; }
            OpenShop();
            return;
        }
        R.Opponent = null;
        if (o?.Kind == OpponentKind.Secret) { R.EventLastDuel = true; OfferPath(); return; }
        var finished = R.Duel;
        R.Duel++;
        if (!skipped && finished <= OdysseyBalance.EventsUntilDuel && !R.EventLastDuel && R.Next().NextDouble() < OdysseyBalance.EventChance) { OfferEvent(); return; }
        R.EventLastDuel = false;
        OfferPath();
    }

    private void Finish(bool won)
    {
        R.Over = true; R.Won = won;
        R.Screen = won ? OdysseyScreen.Victory : OdysseyScreen.GameOver;
        if (won) R.Verdict = "A clear run, kupo! The crystals sing your name.";
        Records.Runs++;
        Records.DuelsWon += R.Stats.DuelsWon;
        if (won) { Records.Clears++; Records.BestRankCleared = Math.Max(Records.BestRankCleared, R.Rank); }
        if (R.World > Records.BestWorld || R.World == Records.BestWorld && R.Duel > Records.BestDuel) { Records.BestWorld = R.World; Records.BestDuel = R.Duel; }
        if (Mvp() is var (id, n) && n > Records.MvpCaptures) { Records.MvpCard = id; Records.MvpCaptures = n; }
        Records.Recent.Insert(0, new RunRecord { When = DateTime.UtcNow, Won = won, World = R.World, Duel = R.Duel, Rank = R.Rank, DuelsWon = R.Stats.DuelsWon, Materia = [.. R.Materia], Verdict = R.Verdict });
        while (Records.Recent.Count > 10) Records.Recent.RemoveAt(Records.Recent.Count - 1);
    }

    /// <summary>The run's MVP: the card with the most captures (id 0 when none).</summary>
    public (uint Id, int Captures) Mvp()
    {
        if (R.Stats.Captures.Count == 0) return (0, 0);
        var best = R.Stats.Captures.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First();
        return (best.Key, best.Value);
    }

    /// <summary>Closes the summary; the title screen follows (the finished run stays until a new one starts).</summary>
    public void CloseSummary()
    {
        if (Run is not { Over: true }) throw new InvalidOperationException("The run isn't over.");
        R.Screen = OdysseyScreen.Title;
        Touch();
    }

    /// <summary>Abandons the run in progress (from the title's New run with a run to continue).</summary>
    public void Abandon()
    {
        if (Run is { Over: false }) { R.Verdict = "Abandoned."; Finish(won: false); R.Screen = OdysseyScreen.Title; Touch(); }
    }

    /// <summary>Called by the plugin after loading a run on the Duel screen: the duel is dealt again (closing mid-duel never keeps a half-played board).</summary>
    public void Resume()
    {
        if (Run is { Over: false, Screen: OdysseyScreen.Duel }) Redeal();
        if (Run is { Over: false, Screen: OdysseyScreen.Deck } && MendBinder()) Touch();
    }

    /// <summary>
    /// A binder that can't make a legal deck (a trade, an upgrade or a Materia change that tightened the limits, or an
    /// older save) would leave the Deck screen with no way on, so it's given the low-star cards it lacks: the weakest
    /// cards it doesn't hold, until five can be played. Returns whether any were added.
    /// </summary>
    private bool MendBinder()
    {
        var added = false;
        foreach (var card in cards.Where(c => Shown(c) && R.Binder.All(b => b.Id != c.Id)).OrderBy(c => c.Stars).ThenBy(c => Power(c)).ThenBy(c => c.Id))
        {
            if (CanFormDeck(R.Binder)) break;
            AddToBinder(card); added = true;
        }
        return added;
    }
}
