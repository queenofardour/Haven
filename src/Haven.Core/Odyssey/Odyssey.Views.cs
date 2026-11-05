namespace Haven.Core.Odyssey;

/// <summary>A card as a screen shows it: the binder entry (Uid 0 for a card not in the binder), the card as it plays, its base card, its power and what its enhancements add.</summary>
public sealed record CardView(int Uid, TriadCard Card, TriadCard Base, double Power, bool InDeck = false, bool Allowed = true, string? Reason = null)
{
    /// <summary>The total change to its sides from enhancements and buffs (the +n badge).</summary>
    public int Delta => Card.Top + Card.Right + Card.Bottom + Card.Left - (Base.Top + Base.Right + Base.Bottom + Base.Left);
    public bool Enhanced => Delta != 0 || Card.Perk != TriadPerk.None || Card.Type != Base.Type;
    /// <summary>The Enhancements applied to a binder card (of OdysseyBalance.MaxEnhancements).</summary>
    public int Enhancements { get; init; }
}

public sealed record ItemView(int Index, Item Item, bool CanUse, string? Reason);

public sealed record OpponentView(OpponentPlan Plan, IReadOnlyList<Materia> Materia, CardView Signature, string RewardNote)
{
    /// <summary>Their whole hand, when Libra scans it (null without Libra).</summary>
    public IReadOnlyList<CardView>? Scanned { get; init; }
    public string Name => Plan.Name;
    public string Title => Plan.Title;
    public OpponentKind Kind => Plan.Kind;
    public IReadOnlyList<TriadRule> Rules => Plan.Rules;
}

public sealed record TitleView(bool CanContinue, string ContinueText, int UnlockedRank, OdysseyRecords Records, uint Music);
public sealed record StartingBinderView(IReadOnlyList<CardView> Cards, int ReDealsLeft, double MeanPower, int Rank, uint Music);
public sealed record WorldTitleView(World World, OpponentView Boss, IReadOnlyList<Materia> BossMateria, uint Music);
public sealed record PathView(World World, int Duel, IReadOnlyList<OpponentView> Choices, bool BossAhead, uint Music);
public sealed record DeckView(World World, int Duel, OpponentView Opponent, IReadOnlyList<CardView> Binder, IReadOnlyList<CardView> Deck,
    IReadOnlyList<CardView> OpponentCardsShown, IReadOnlyList<TriadRule> RulesInPlay, IReadOnlyList<Materia> MyMateria, IReadOnlyList<Materia> TheirMateria,
    IReadOnlyList<ItemView> Items, int Mgp, bool CanStart, string? WhyNot, IReadOnlyList<string> Buffs, bool PhoenixHeld, uint Music);
public sealed record RewardChoiceView(int Index, RewardKind Kind, string Name, string Description, string Icon, Rarity Rarity, bool Enabled, string? Reason);
public sealed record RewardView(string Heading, IReadOnlyList<RewardChoiceView> Choices, int? RerollPrice, bool CanReroll, bool Claiming, IReadOnlyList<CardView> Claimable,
    int Mgp, bool Boss, int Stage, uint Music, bool Starting = false);
/// <param name="Back">The Back button's label when the player can still go back to the previous choice (null: they can't).</param>
public sealed record UpgradeView(Enhancement Enhancement, IReadOnlyList<CardView> Binder, bool NeedsChoice, uint Music, string? Back = null);
public sealed record MateriaSwapView(Materia New, IReadOnlyList<Materia> Slots, uint Music, string? Back = null)
{
    /// <summary>Why the new Materia can't take each slot (null: it can), in Slots' order.</summary>
    public IReadOnlyList<string?> Refusals { get; init; } = [];
    /// <summary>The new Materia is Legendary and the Legendary limit is reached (rather than the slots being full).</summary>
    public bool LegendaryLimited { get; init; }
    public int LegendaryLimit { get; init; } = OdysseyBalance.LegendaryMateria;
}
public sealed record EventView(string Title, string Text, IReadOnlyList<EventChoice> Choices, IReadOnlyList<CardView> Binder, CardView? Offered, bool Resolved, uint Music);
public sealed record WareView(int Index, WareKind Kind, string Name, string Description, string Icon, Rarity Rarity, int Price, bool Sold, bool CanBuy, string? Reason);
public sealed record ShopView(World World, IReadOnlyList<WareView> Wares, IReadOnlyList<CardView> Pack, int PackPrice, bool PackSold, int RemovalPrice, int RerollPrice,
    int Mgp, IReadOnlyList<CardView> Binder, IReadOnlyList<Materia> MyMateria, IReadOnlyList<Item> Items, uint Music)
{
    /// <summary>Every Materia slot is taken: a bought Materia replaces one (or is let go).</summary>
    public bool SlotsFull { get; init; }
    /// <summary>What the shop pays for each held Item, in Items' order.</summary>
    public IReadOnlyList<int> SellPrices { get; init; } = [];
    /// <summary>The binder for selling a card: each card's price, Allowed and Reason as CardSaleRefusal says.</summary>
    public IReadOnlyList<(CardView Card, int Price)> ForSale { get; init; } = [];
    /// <summary>A card was sold on this visit.</summary>
    public bool CardSold { get; init; }
    /// <summary>The Legendary Materia are at the limit: a bought one replaces one of them, or is let go.</summary>
    public bool LegendariesFull { get; init; }
    public int LegendaryLimit { get; init; } = OdysseyBalance.LegendaryMateria;
}
public sealed record SummaryView(bool Won, World World, int Duel, int DuelsWon, int DuelsPlayed, CardView? Mvp, int MvpCaptures, int BestPlacement, CardView? BestPlacementCard,
    string BestPlacementWhere, IReadOnlyList<Materia> Materia, IReadOnlyList<CardView> Claimed, int MgpEarned, string Verdict, int Rank, bool RankUnlocked,
    IReadOnlyList<DuelLog> Duels, uint Music, uint CreditsMusic);

public sealed partial class Odyssey
{
    // ---- View models: what each screen shows (design §7). Views never roll anything. ----

    private CardView View(BinderCard b) => new(b.Uid, Card(b), Base(b.Id), Power(Card(b))) { Enhancements = b.Enhancements };
    private CardView View(TriadCard c) => new(0, c, Base(c.Id), Power(c));
    private CardView View(CardState s) => View(s.ToCard());
    private IReadOnlyList<CardView> BinderViews() => [.. R.Binder.Select(View)];
    private IReadOnlyList<Materia> MateriaViews(IEnumerable<string> keys) => [.. keys.Select(OdysseyContent.MateriaOf).Where(m => m != null)!];
    private OpponentView View(OpponentPlan o)
    {
        var note = o.Kind switch
        {
            OpponentKind.Boss => "Claim one of their cards, and a Rare Materia",
            OpponentKind.Secret when o.Event == EventKind.Gilgamesh => "His card, and a Legendary reward",
            OpponentKind.Secret => "Their card",
            OpponentKind.Elite => "Claim a card, or one of three (one Rare or better)",
            _ => "Claim a card, or one of two",
        };
        var signature = o.Deck.FirstOrDefault(c => c.Id == o.Signature) is { } s ? View(s) : View(Base(o.Signature));
        return new OpponentView(o, MateriaViews(o.Materia), signature, note) { Scanned = R.Materia.Contains("libra") ? [.. o.Deck.Select(View)] : null };
    }

    public TitleView Title() => new(CanContinue, Run is { Over: false } r && r.Screen != OdysseyScreen.Title ? $"World {OdysseyContent.WorldAt(Math.Max(1, r.World)).Numeral}, duel {Math.Min(r.Duel, OdysseyBalance.BossDuel)}" : "",
        Records.UnlockedRank, Records, OdysseyContent.TitleMusic);

    public StartingBinderView StartingBinder()
    {
        Require(OdysseyScreen.StartingBinder);
        var views = BinderViews();
        return new(views, R.ReDealsLeft, views.Count == 0 ? 0 : views.Average(v => v.Power), R.Rank, OdysseyContent.TitleMusic);
    }

    public WorldTitleView WorldTitle()
    {
        Require(OdysseyScreen.WorldTitle);
        var boss = BossPlan(R.World);
        return new(World, View(boss), MateriaViews(boss.Materia), World.MapMusic);
    }

    public PathView Path()
    {
        Require(OdysseyScreen.Path);
        return new(World, R.Duel, [.. R.Path.Select(View)], R.Duel >= OdysseyBalance.BossDuel, World.MapMusic);
    }

    public DeckView Deck()
    {
        Require(OdysseyScreen.Deck);
        var o = R.Opponent!;
        var p = R.Prep;
        var binder = R.Binder.Select(b => { var why = DeckRefusal(b.Uid); return View(b) with { InDeck = p.Deck.Contains(b.Uid), Allowed = why == null, Reason = why }; }).ToList();
        var deck = p.Deck.Select(uid => binder.First(v => v.Uid == uid)).ToList();
        var rules = PlayedRules();
        var reveal = p.Ether || R.Materia.Contains("libra") || rules.Contains(TriadRule.AllOpen);
        var shown = reveal ? o.Deck.Select(View).ToList() : o.Deck.Where(c => c.Id == o.Signature && o.Kind != OpponentKind.Regular).Select(View).ToList();
        // Items with a target are refused when the target is picked; the Favour already says when no rule could be added.
        string? Refusal(Item item, int i) => item.NeedsCard || item.Kind == ItemKind.EchoScreen ? null
            : item.AddsRule ? (p.Added != null ? "A rule is already added." : AddableRules().Count == 0 ? "No rule of its pool fits with theirs." : null) : ItemRefusal(i);
        var items = R.Items.Select((k, i) => { var item = OdysseyContent.ItemOf(k)!; var why = Refusal(item, i); return new ItemView(i, item, why == null && !item.Held, why ?? (item.Held ? "Held." : null)); }).ToList();
        var buffs = new List<string>();
        foreach (var uid in p.Potions) buffs.Add($"Potion on {Card(Find(uid)).Name}");
        if (p.HiPotion) buffs.Add("Hi-Potion: every card +1");
        if (p.Elixir) buffs.Add("Elixir: every card +2");
        if (p.Megalixir) buffs.Add("Megalixir: every card +2, first capture undone");
        if (p.Ether) buffs.Add("Ether: their hand is open");
        if (p.Gysahl) buffs.Add("Gysahl Greens: you move first");
        if (p.Remedy) buffs.Add("Remedy: their Materia is cancelled");
        if (p.Echo is { } echo) buffs.Add($"Echo Screen: {RuleWords(echo)} cancelled");
        foreach (var key in p.Tonics) if (OdysseyContent.ItemOf(key) is { } tonic) buffs.Add($"{tonic.Name}: {tonic.Description.TrimEnd('.')}");
        if (p.Map is { } map) buffs.Add(map >= Rarity.Rare ? "Ancient Treasure Map: one more reward choice, all Rare or better" : "Treasure Map: one more reward choice");
        if (p.Hidden) buffs.Add("Prism Powder: your hand is hidden");
        if (p.Added is { } added) buffs.Add($"Queen of Cards' Favour: {RuleWords(added)} added");
        if (p.Ribbon) buffs.Add("Ribbon: only their Open rule and Sudden Death, their Materia cancelled");
        if (p.Pandora is { } box) buffs.Add($"Pandora's Box: {OdysseyContent.MateriaOf(box)?.Name} is yours this duel");
        var why = StartRefusal();
        return new(World, R.Duel, View(o), binder, deck, shown, rules, MateriaViews(R.Materia), p.Remedy || p.Ribbon ? [] : MateriaViews(o.Materia), items, R.Mgp, why == null, why, buffs,
            R.Items.Contains("phoenix-down"), World.MapMusic);
    }

    public RewardView Reward()
    {
        Require(OdysseyScreen.Reward);
        var reward = R.Reward!;
        var choices = reward.Choices.Select((c, i) =>
        {
            var why = RewardRefusal(i);
            var (name, description, icon) = c.Kind switch
            {
                RewardKind.ClaimCard => ("Claim a card", $"Take one of {R.Opponent?.Name}'s cards.", "Triple Triad Card"),
                RewardKind.Enhancement => OdysseyContent.EnhancementOf(c.Key) is { } e ? (e.Name, e.Description, e.Icon) : (c.Key, "", ""),
                RewardKind.Item => OdysseyContent.ItemOf(c.Key) is { } it ? (it.Name, it.Description, it.Icon) : (c.Key, "", ""),
                RewardKind.Materia when IsLegendary(c.Key) && LegendariesFull && OdysseyContent.MateriaOf(c.Key) is { } legend
                    => (legend.Name, $"{legend.Description}\n\nYou can slot only {LegendaryLimit} Legendary Materia: it would replace one of yours, or you let it go.", legend.Icon),
                _ => OdysseyContent.MateriaOf(c.Key) is { } m ? (m.Name, m.Description, m.Icon) : (c.Key, "", ""),
            };
            return new RewardChoiceView(i, c.Kind, name, description, icon, c.Rarity, why == null, why);
        }).ToList();
        var price = RerollPrice();
        return new(reward.Heading, choices, price, price is { } p && p <= R.Mgp, reward.Claiming, [.. reward.Claimable.Select(View)], R.Mgp, reward.From == OpponentKind.Boss, reward.Stage,
            World.MapMusic, reward.Starting);   // after the duel's fanfare (OdysseyGame), the world's theme
    }

    public UpgradeView Upgrade()
    {
        Require(OdysseyScreen.Upgrade);
        var e = OdysseyContent.EnhancementOf(R.Upgrade!.Enhancement)!;
        return new(e, BinderViews(), e.NeedsChoice, R.Upgrade.ReturnTo == OdysseyScreen.Shop ? OdysseyContent.ShopMusic : World.MapMusic, BackLabel(R.Upgrade.ReturnTo, R.Upgrade.Paid));
    }
    private string? BackLabel(OdysseyScreen returnTo, int paid) => !CanGoBack ? null : returnTo switch
    {
        OdysseyScreen.Shop => paid > 0 ? $"Back · {paid} gil back" : "Back to the shop",
        OdysseyScreen.Reward => R.Reward?.Starting == true ? "Back to the gifts" : "Back to the rewards",
        _ => "Back",
    };

    public MateriaSwapView MateriaSwap()
    {
        Require(OdysseyScreen.MateriaSwap);
        return new(OdysseyContent.MateriaOf(R.Swap!.Materia)!, MateriaViews(R.Materia), R.Swap.ReturnTo == OdysseyScreen.Shop ? OdysseyContent.ShopMusic : World.MapMusic, BackLabel(R.Swap.ReturnTo, R.Swap.Paid))
        {
            Refusals = [.. R.Materia.Select((_, i) => KeepRefusal(i))],
            LegendaryLimited = IsLegendary(R.Swap.Materia) && Legendaries >= LegendaryLimit,
            LegendaryLimit = LegendaryLimit,
        };
    }

    public EventView Event()
    {
        Require(OdysseyScreen.Event);
        var e = R.Event!;
        return new(OdysseyContent.EventOf(e.Kind).Title, EventText(), EventChoices(), BinderViews(), e.Offered is { } offered ? View(offered) : null, e.Result != null, World.MapMusic);
    }

    public ShopView Shop()
    {
        Require(OdysseyScreen.Shop);
        var shop = R.Shop!;
        var wares = shop.Wares.Select((w, i) =>
        {
            var why = BuyRefusal(i);
            var (name, description, icon) = w.Kind switch
            {
                WareKind.Enhancement => OdysseyContent.EnhancementOf(w.Key) is { } e ? (e.Name, e.Description, e.Icon) : (w.Key, "", ""),
                WareKind.Materia => OdysseyContent.MateriaOf(w.Key) is { } m ? (m.Name, m.Description, m.Icon) : (w.Key, "", ""),
                WareKind.Item => OdysseyContent.ItemOf(w.Key) is { } it ? (it.Name, it.Description, it.Icon) : (w.Key, "", ""),
                _ => ("Card pack", "One of three cards of the next world's tier.", "Triple Triad Card"),
            };
            return new WareView(i, w.Kind, name, description, icon, w.Rarity, w.Price, w.Sold, why == null, why);
        }).ToList();
        var pack = shop.Wares.FirstOrDefault(w => w.Kind == WareKind.Pack);
        var binder = R.Binder.Select(b => { var why = RemoveRefusal(b.Uid); return View(b) with { Allowed = why == null, Reason = why }; }).ToList();
        return new(World, wares, [.. shop.Pack.Select(View)], pack?.Price ?? 0, shop.PackSold, RemovalPrice(), ShopRerollPrice(), R.Mgp, binder, MateriaViews(R.Materia),
            [.. R.Items.Select(OdysseyContent.ItemOf).Where(i => i != null)!], OdysseyContent.ShopMusic)
        {
            SlotsFull = R.Materia.Count >= R.Slots, SellPrices = [.. R.Items.Select((_, i) => SellPrice(i))],
            ForSale = [.. R.Binder.Select(b => { var why = CardSaleRefusal(b.Uid); return (View(b) with { Allowed = why == null, Reason = why }, CardSalePrice(b.Uid)); })],
            CardSold = shop.CardSold, LegendariesFull = LegendariesFull, LegendaryLimit = LegendaryLimit,
        };
    }

    /// <summary>The run summary (Victory or Game over).</summary>
    public SummaryView Summary()
    {
        if (Run is not { Over: true } r) throw new InvalidOperationException("The run isn't over.");
        var (mvpId, mvpCaptures) = Mvp();
        var world = OdysseyContent.WorldAt(Math.Max(1, r.World));
        return new(r.Won, world, r.Duel, r.Stats.DuelsWon, r.Stats.DuelsPlayed, mvpId == 0 ? null : View(Base(mvpId)), mvpCaptures, r.Stats.BestPlacement,
            r.Stats.BestPlacementCard == 0 ? null : View(Base(r.Stats.BestPlacementCard)), r.Stats.BestPlacementWhere, MateriaViews(r.Materia),
            [.. r.Stats.Claimed.Select(id => View(Base(id)))], r.Stats.MgpEarned, r.Verdict, r.Rank, r.Won && r.Rank == Records.BestRankCleared && Records.UnlockedRank > r.Rank, r.Stats.Duels,
            r.Won ? OdysseyContent.VictoryMusic : OdysseyContent.GameOverMusic, OdysseyContent.CreditsMusic);
    }

    /// <summary>The binder with each card's effective values, for any screen.</summary>
    public IReadOnlyList<CardView> Binder() => Run == null ? [] : BinderViews();
    public IReadOnlyList<Materia> MyMateria() => Run == null ? [] : MateriaViews(R.Materia);
}
