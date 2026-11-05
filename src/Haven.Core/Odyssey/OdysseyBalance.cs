namespace Haven.Core.Odyssey;

/// <summary>
/// Every difficulty dial of the Odyssey (design §4 and the numbers of §1 and §3), in one place for the simulator to
/// tune. Worlds are w = 1-14, duels d = 1-5 (6 is the boss). Everything is a plain number or a small function of them;
/// the director never hides a constant of its own.
/// </summary>
public static class OdysseyBalance
{
    public const int Worlds = 14, DuelsPerWorld = 5, BossDuel = 6, StartingBinder = 8, DeckSize = 5, MaxItems = 3, StartingSlots = 3, MaxSlots = 6;
    /// <summary>
    /// Enhancements one card may take (3 November 2026): a playtester stacked upgrades until a card was nearly all A,
    /// while opponents get at most +1 on a few cards.
    /// </summary>
    public const int MaxEnhancements = 3;
    /// <summary>
    /// Legendary Materia slotted at once (3 November 2026): Wall with Alexander's corners and two more was too strong.
    /// The Master Materia raises it by one, once a run.
    /// </summary>
    public const int LegendaryMateria = 1;

    // ---- The starting binder (§1.1) ----
    public static readonly int[] StartingStars = [1, 1, 1, 1, 1, 2, 2, 3];
    /// <summary>The mean power the starting binder is re-drawn into (weaker at Crystal Rank 6).</summary>
    public static (double Low, double High) StartingPower(int rank) => rank >= 6 ? (.36, .42) : (.40, .46);
    public static int ReDeals(int rank) => rank >= 2 ? 2 : 3;
    public const int StartingDrawAttempts = 400;
    /// <summary>The free Common Enhancements offered once the starting binder is accepted (one is taken).</summary>
    public const int StartingEnhancements = 3;

    // ---- Deck limits (§1.1): five-star cards, and cards of four stars or more ----
    public static (int Fives, int Strong) DeckLimits(bool royalCharter) => royalCharter ? (2, 3) : (1, 2);
    /// <summary>Whether a deck may take a card under the limits (the game's, or the Royal Charter's).</summary>
    public static bool DeckAllows(IReadOnlyCollection<TriadCard> deck, TriadCard card, bool royalCharter)
    {
        var (fives, strong) = DeckLimits(royalCharter);
        return deck.Count(c => LimitStars(c) >= 5) + (LimitStars(card) >= 5 ? 1 : 0) <= fives && deck.Count(c => LimitStars(c) >= 4) + (LimitStars(card) >= 4 ? 1 : 0) <= strong;
    }
    /// <summary>The stars a card counts as for the deck limits: a Masked card one fewer (Moogle Charm and Star Power still see its own).</summary>
    public static int LimitStars(TriadCard card) => card.Is(TriadPerk.Masked) ? card.Stars - 1 : card.Stars;

    // ---- 1. Opponent deck power ----
    /// <summary>Regular w,d: base + per world + per duel, plus a step at each act start (V, IX, XIII) so the pressure resets after the player overtakes an act (the sawtooth of §4).</summary>
    public const double PowerBase = .43, PowerPerWorld = .030, PowerPerDuel = .004, ActStep = .035, ElitePower = .05, PowerTolerance = .02;
    /// <summary>Bosses above their world's regulars: less in act I, where the binder is still the starting one (Garland at +.10 killed one run in seven).</summary>
    public const double BossPowerEarly = .07, BossPowerLate = .10;
    public static double BossPower(int w) => Act(w) == 1 ? BossPowerEarly : BossPowerLate;
    /// <summary>Regulars and elites cap where the game's deck limits run out of cards; bosses, who ignore the limits, above.</summary>
    public const double PowerCap = .86, BossPowerCap = .88;
    public static int Act(int w) => w <= 4 ? 1 : w <= 8 ? 2 : w <= 12 ? 3 : 4;
    public static double DeckPower(int w, int d, bool elite, bool boss) =>
        Math.Min(boss ? BossPowerCap : PowerCap, PowerBase + PowerPerWorld * (w - 1) + ActStep * (Act(w) - 1) + PowerPerDuel * d + (elite ? ElitePower : 0) + (boss ? BossPower(w) : 0));
    /// <summary>The secret duels (Gilgamesh, a guest star) are elites a little above the world's elites.</summary>
    public const double SecretDuelPower = .03;
    /// <summary>Themed cards in a deck: regulars 0-1 (half the time one), elites 1-2 (the signature plus half the time another).</summary>
    public const double RegularThemedChance = .5, EliteSecondThemedChance = .5;
    /// <summary>
    /// Regulars and elites keep the deck limits, which cap a deck's power near 0.75 (one 5★, one 4★, three 3★); so from
    /// world IX elites, and from XI regulars too, get the Royal Charter's limits, and XIII-XIV elites one more 4★.
    /// </summary>
    public static (int Fives, int Strong) OpponentLimits(int w, bool elite) => w >= 13 && elite ? (2, 4) : w >= 11 || w >= 9 && elite ? (2, 3) : (1, 2);
    /// <summary>How far from the power a boss's fifth card still needs a themed card may be and still be chosen over any card.</summary>
    public const double BossThemedSlack = .08;
    /// <summary>Boss decks ignore the deck limits from world III: two 5★ cards from V, three from XII.</summary>
    public static (int Fives, int Strong) BossLimits(int w) => w >= 12 ? (3, 5) : w >= 5 ? (2, 5) : w >= 3 ? (1, 5) : (1, 2);
    /// <summary>The starting binder's cards and the shop's card packs are drawn from a star tier by world.</summary>
    public static int PackStars(int nextWorld) => nextWorld >= 12 ? 5 : nextWorld >= 8 ? 4 : nextWorld >= 4 ? 3 : 2;

    // ---- 2. Rules ----
    public static readonly TriadRule[] EarlyRulePool = [TriadRule.Same, TriadRule.Plus, TriadRule.FallenAce, TriadRule.Ascension, TriadRule.Descension, TriadRule.Order];
    /// <summary>
    /// Reverse turns a binder built strong into a liability: in simulation it carried 70 % of losses at 18 % lost per duel,
    /// against 3-6 % for every other rule, so it is out of the random pool (a world past the last). Zeromus (IV) and Seymour
    /// (X) keep it as signature rules, shown on the world's title card from its start, so the player can plan weak cards,
    /// Esuna or an Echo Screen for them. Set this to 4 to put it back in the pool from world IV.
    /// </summary>
    public const int ReverseFromWorld = Worlds + 1;
    /// <summary>
    /// Rules Roulette may not roll in a world: Reverse while it is out of the random pool. A friend's run (2 November
    /// 2026) had Roulette roll Reverse and five rounds of Sudden Death end level.
    /// </summary>
    public static IReadOnlyList<TriadRule> RouletteSkips(int w) => w >= ReverseFromWorld ? [] : [TriadRule.Reverse];
    /// <summary>
    /// Rules every duel plays, left out of rule lists and the rule box: Sudden Death (a duel must end won or lost, so a
    /// draw is played again). The deck screen says so once instead; it took one of the rule box's four lines.
    /// </summary>
    public static readonly TriadRule[] Unlisted = [TriadRule.SuddenDeath];
    public static IEnumerable<TriadRule> Listed(IEnumerable<TriadRule> rules) => rules.Where(r => !Unlisted.Contains(r));
    public static readonly TriadRule[] WorldOneBonus = [TriadRule.Same, TriadRule.Plus];
    public const double WorldOneBonusChance = .3;
    public const int WorldOneBonusFromDuel = 3;
    public const int ChaosFromWorld = 6, ThreeOpenFromWorld = 6, SwapRouletteFromWorld = 9;
    public const double ThreeOpenChanceMid = .5, AllOpenChanceLate = .25, TwoRulesChanceMid = .5, ThreeRulesChanceLate = .5;
    /// <summary>The rules an opponent of a world may draw, besides the Open rule.</summary>
    public static IReadOnlyList<TriadRule> RulePool(int w)
    {
        var pool = EarlyRulePool.ToList();
        if (w >= ReverseFromWorld) pool.Add(TriadRule.Reverse);
        if (w >= ChaosFromWorld) pool.Add(TriadRule.Chaos);
        if (w >= SwapRouletteFromWorld) { pool.Add(TriadRule.Swap); pool.Add(TriadRule.Roulette); }
        return pool;
    }
    /// <summary>How many rules a regular of the world draws from the pool (before the elite's extra rule and the Open rule).</summary>
    public static int RuleCount(int w, int d, Random random) => w switch
    {
        1 => d >= WorldOneBonusFromDuel && random.NextDouble() < WorldOneBonusChance ? 1 : 0,
        <= 4 => 1,
        <= 8 => random.NextDouble() < TwoRulesChanceMid ? 2 : 1,
        <= 12 => 2,
        _ => random.NextDouble() < ThreeRulesChanceLate ? 3 : 2,
    };
    /// <summary>The Open rule of a regular duel: All Open early, Three Open later.</summary>
    public static TriadRule OpenRule(int w, Random random) =>
        w < ThreeOpenFromWorld ? TriadRule.AllOpen
        : w <= 8 ? (random.NextDouble() < ThreeOpenChanceMid ? TriadRule.ThreeOpen : TriadRule.AllOpen)
        : random.NextDouble() < AllOpenChanceLate ? TriadRule.AllOpen : TriadRule.ThreeOpen;
    public const int EliteExtraRules = 1;
    public static int BossExtraRules(int rank) => rank >= 5 ? 1 : 0;

    // ---- 3. Opponent Materia ----
    public static readonly string[] MateriaPoolEarly = ["protect", "shell", "berserk", "steel-wall", "haste", "final-attack", "toughness"];
    public static readonly string[] MateriaPoolMid = [.. MateriaPoolEarly, "limit-break", "counter", "gravity", "moogle-charm", "star-power", "fortify", "bahamut"];
    public static readonly string[] MateriaPoolLate = [.. MateriaPoolMid, "same", "plus", "carbuncle", "shiva", "same-wall", "plus-wall"];
    public static IReadOnlyList<string> OpponentMateriaPool(int w) => w >= 12 ? MateriaPoolLate : w >= 8 ? MateriaPoolMid : MateriaPoolEarly;
    /// <summary>How many Materia an opponent carries: none in I-III, one in IV-VII, two in VIII-XI, three in XII-XIV; bosses +1 from II and +2 from IX; Crystal Rank 1 +1.</summary>
    public static int OpponentMateria(int w, bool boss, int rank)
    {
        var count = w >= 12 ? 3 : w >= 8 ? 2 : w >= 4 ? 1 : 0;
        if (boss && w >= 2) count++;
        if (boss && w >= 9) count++;
        if (rank >= 1) count++;
        return count;
    }

    // ---- 4. Opponent enhancements ----
    /// <summary>Cards with +1 on a side: from VI, ⌊(w−4)/2⌋; Crystal Rank 9 one more.</summary>
    public static int OpponentRaisedCards(int w, int rank) => (w >= 6 ? (w - 4) / 2 : 0) + (rank >= 9 ? 1 : 0);
    /// <summary>Cards with a perk: one from IX, two from XII.</summary>
    public static int OpponentPerkCards(int w) => w >= 12 ? 2 : w >= 9 ? 1 : 0;
    /// <summary>Keystone, Frostbite, Drain and Desperate give late opponents variety and pressure without raising their mean power; Contrary, Masked, Mimic and the two-trait scrolls stay the player's.</summary>
    public static readonly TriadPerk[] OpponentPerkPool = [TriadPerk.Reckless, TriadPerk.Guardian, TriadPerk.Cornerstone, TriadPerk.Aegis, TriadPerk.AceKiller, TriadPerk.Avalanche,
        TriadPerk.Keystone, TriadPerk.Frostbite, TriadPerk.Drain, TriadPerk.Desperate];

    // ---- 5. AI skill ----
    public const double AiLevelPerWorld = .75;
    public const int AiLevelMax = 12, EliteAiBonus = 1, BossAiBonus = 2;
    public static int AiLevel(int w, bool elite, bool boss, int rank) =>
        Math.Clamp((int)Math.Round(1 + (w - 1) * AiLevelPerWorld) + (elite ? EliteAiBonus : 0) + (boss ? BossAiBonus : 0) + (rank >= 4 ? 1 : 0), 1, AiLevelMax);

    // ---- The path (§1) ----
    public const double ThirdChoiceChance = .25, EliteChance = .35;
    /// <summary>Crystal Rank 3: an elite in every path from world II.</summary>
    public static bool EliteGuaranteed(int w, int rank) => rank >= 3 && w >= 2;
    public const double EventChance = .22, RareEventChance = .12;
    public const int EventsUntilDuel = 4;

    // ---- Rewards (§1.2) ----
    public const int RegularRolled = 2, EliteRolled = 3, BossMateriaChoices = 3, SecretLegendaryChoices = 2;
    /// <summary>Rarity weights (Common, Uncommon, Rare, Legendary), linear from world 1 to 14. Legendaries stay rare (1 → 3 %): a run should see one or two offers, not a set.</summary>
    public static double[] RarityWeights(int w)
    {
        var t = (Math.Clamp(w, 1, Worlds) - 1) / (double)(Worlds - 1);
        return [60 + (38 - 60) * t, 30 + (35 - 30) * t, 9.5 + (26 - 9.5) * t, .5 + (1 - .5) * t];
    }
    /// <summary>The boss's Materia stage offers Rares; each choice has this chance of a Legendary instead.</summary>
    public const double BossLegendaryChance = .03;
    /// <summary>
    /// The Master Materia's weight among the Legendary Materia a choice picks from, against 1 for each of the others: it
    /// joins the rolled rewards' and the boss crystal's Legendaries until taken, so most runs never see it.
    /// </summary>
    public const double MasterMateriaWeight = .5;
    /// <summary>Elites shift this share of each tier's weight up to the next tier.</summary>
    public const double EliteRarityShift = .15;
    /// <summary>Pity: each reward screen without a Rare or better adds this to the next screen's Rare chance (in weight points of 100), reset when one appears.</summary>
    public const double PityStep = 4;
    /// <summary>
    /// How rolled rewards split by kind (Enhancement, Materia, Item). Was 40 / 35 / 25 with 13 Enhancements and 10 Items,
    /// which repeated the few Commons; with 27 and 24 a little moves from permanent power to consumables.
    /// </summary>
    public static readonly double[] KindWeights = [36, 34, 30];
    public const int RerollBase = 20;
    public const double RerollGrowth = 2.0;
    public static int RerollPrice(int rerollsThisScreen) => (int)Math.Round(RerollBase * Math.Pow(RerollGrowth, rerollsThisScreen));
    /// <summary>MGP for a win: 10 + 4 × margin + 2 × world; elites ×1.5, bosses ×2; Moogle's Purse +50 %.</summary>
    public static int Mgp(int w, int margin, bool elite, bool boss, bool purse)
    {
        double mgp = 10 + 4 * Math.Max(0, margin) + 2 * w;
        if (elite) mgp *= 1.5;
        if (boss) mgp *= 2;
        if (purse) mgp *= 1.5;
        return (int)Math.Round(mgp);
    }
    public const int StartingMgp = 0;

    // ---- Prices (§3.4) and the shop (§1.4) ----
    /// <summary>A held Item sells back to the shop for its price divided by this (half).</summary>
    public const int ItemResale = 2;
    /// <summary>
    /// What the shop pays for a binder card, by its printed stars (1-5), plus CardSaleEnhancement for each Enhancement on
    /// it; one card a visit. About a third of a pack's price for a 3★ card, at most 70 gil for a 5★ card with three
    /// Enhancements: a little gil and a free thinning, not a way to buy the shop.
    /// </summary>
    public static readonly int[] CardSaleStars = [0, 8, 12, 20, 30, 40];
    public const int CardSaleEnhancement = 10;
    public static int CardSalePrice(int stars, int enhancements) => CardSaleStars[Math.Clamp(stars, 1, 5)] + CardSaleEnhancement * Math.Clamp(enhancements, 0, MaxEnhancements);
    public static int BasePrice(Rarity rarity) => rarity switch { Rarity.Common => 40, Rarity.Uncommon => 70, Rarity.Rare => 120, _ => 200 };
    public const int PackPrice = 60, RemovalBase = 40, RemovalStep = 20, ShopReroll = 20;
    /// <summary>Two Enhancements and three Items (3 November 2026; was three and two): Enhancements were three wares of eight.</summary>
    public const int ShopEnhancements = 2, ShopMateria = 2, ShopItems = 3, PackChoices = 3;
    public const double HagglerDiscount = .25, RankSevenMarkup = .25;
    public static int Price(int basePrice, bool haggler, int rank)
    {
        var price = basePrice * (haggler ? 1 - HagglerDiscount : 1) * (rank >= 7 ? 1 + RankSevenMarkup : 1);
        return Math.Max(1, (int)Math.Round(price));
    }
    public static int RemovalPrice(int removals, bool haggler, int rank) => Price(RemovalBase + RemovalStep * removals, haggler, rank);
    /// <summary>Crystal Rank 8: no Phoenix Down in shops.</summary>
    public static bool ShopSells(Item item, int rank) => !(rank >= 8 && item.Kind == ItemKind.PhoenixDown);

    // ---- Events (§1.3) ----
    public const int WagerStake = 30, WagerPrize = 90, WorkshopAllSidesPrice = 60, MooglePostMgp = 25;
    public const double WagerWinChance = .45;
    public const int MerchantWares = 3;
    /// <summary>The merchant's best ware: Legendary Items stay rare (three of 24 would be one ware in eight).</summary>
    public const Rarity MerchantMaxRarity = Rarity.Rare;

    // ---- Crystal Rank (§4): the modifiers above read the rank; these are the rest ----
    public const int MaxRank = 10;
    /// <summary>Crystal Rank 10: the Endsinger's second form, a second duel straight after the first.</summary>
    public static bool EndsingerSecondForm(int rank) => rank >= 10;
}
