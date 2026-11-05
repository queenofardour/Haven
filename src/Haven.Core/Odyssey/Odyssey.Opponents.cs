namespace Haven.Core.Odyssey;

public sealed partial class Odyssey
{
    // ---- Opponents: decks fitted to the world's power target, rules, Materia and enhancements (design §2, §4) ----

    private IEnumerable<TriadCard> Themed(World world) => world.ThemedCards.Where(byId.ContainsKey).Select(id => byId[id]).Where(c => Shown(c, world.Number));
    /// <summary>
    /// Whether a card may turn up in a world (the run's, by default): a boss's signature card waits until the run passes
    /// that boss's world (the boss's own deck holds it regardless), so it is never claimed or bought before the boss shows it.
    /// A boss's deck is judged from its own world, so it is the same whenever it is asked for.
    /// </summary>
    private bool Shown(TriadCard card, int world) => !OdysseyContent.SignatureWorld.TryGetValue(card.Id, out var signed) || signed < world;
    private bool Shown(TriadCard card) => Shown(card, R.World);

    /// <summary>
    /// Fills a deck to five by a greedy fit: each slot takes a random card whose power is near what the remaining slots need
    /// to reach the target mean, widening the net until something fits; the deck limits (the opponent's) are kept.
    /// </summary>
    private List<TriadCard> Fit(Random random, List<TriadCard> deck, double target, (int Fives, int Strong) limits, int? world = null)
    {
        var pool = cards.Where(c => Shown(c, world ?? R.World)).OrderBy(c => c.Id).ToList();
        while (deck.Count < OdysseyBalance.DeckSize)
        {
            var remaining = OdysseyBalance.DeckSize - deck.Count;
            var need = (target * OdysseyBalance.DeckSize - deck.Sum(Power)) / remaining;
            var candidates = pool.Where(c => deck.All(d => d.Id != c.Id) && Allows(deck, c, limits)).ToList();
            // A fixed deck already over its limits (a boss's) takes any card.
            if (candidates.Count == 0) candidates = pool.Where(c => deck.All(d => d.Id != c.Id)).ToList();
            if (candidates.Count == 0) throw new InvalidOperationException("Too few cards to deal a deck.");
            var chosen = candidates;
            for (var tolerance = .03; tolerance <= 1; tolerance += .03)
            {
                var near = candidates.Where(c => Math.Abs(Power(c) - need) <= tolerance).ToList();
                if (near.Count > 0) { chosen = near; break; }
            }
            deck.Add(chosen[random.Next(chosen.Count)]);
        }
        return deck;
    }
    private static bool Allows(IReadOnlyCollection<TriadCard> deck, TriadCard card, (int Fives, int Strong) limits) =>
        deck.Count(c => c.Stars >= 5) + (card.Stars >= 5 ? 1 : 0) <= limits.Fives && deck.Count(c => c.Stars >= 4) + (card.Stars >= 4 ? 1 : 0) <= limits.Strong;

    private List<TriadRule> RollRules(Random random, int world, int extra, IEnumerable<TriadRule>? fixedRules = null)
    {
        var rules = new List<TriadRule>();
        if (fixedRules != null) rules.AddRange(fixedRules);
        else rules.Add(OdysseyBalance.OpenRule(world, random));
        var count = (fixedRules == null ? OdysseyBalance.RuleCount(world, R.Duel, random) : 0) + extra;
        var pool = (world == 1 ? OdysseyBalance.WorldOneBonus : OdysseyBalance.RulePool(world)).ToList();
        for (var i = 0; i < count; i++)
        {
            var fitting = pool.Where(r => rules.All(x => TriadGame.Compatible(x, r))).ToList();
            if (fitting.Count == 0) break;
            var rule = fitting[random.Next(fitting.Count)];
            rules.Add(rule); pool.Remove(rule);
        }
        if (!rules.Contains(TriadRule.SuddenDeath)) rules.Add(TriadRule.SuddenDeath);
        return rules;
    }

    private List<string> RollMateria(Random random, int world, bool boss, IEnumerable<string>? signature = null)
    {
        var materia = signature?.ToList() ?? [];
        var count = OdysseyBalance.OpponentMateria(world, boss, R.Rank);
        var pool = OdysseyBalance.OpponentMateriaPool(world).Where(k => !materia.Contains(k)).ToList();
        while (materia.Count < count && pool.Count > 0) { var k = pool[random.Next(pool.Count)]; materia.Add(k); pool.Remove(k); }
        return materia;
    }

    // Opponent enhancements: +1 on a side of some cards from world VI, a perk on one or two from IX.
    private List<CardState> Enhanced(Random random, List<TriadCard> deck, int world)
    {
        var states = deck.Select(CardState.From).ToList();
        var raised = Math.Min(states.Count, OdysseyBalance.OpponentRaisedCards(world, R.Rank));
        foreach (var i in Enumerable.Range(0, states.Count).OrderBy(_ => random.Next()).Take(raised))
        {
            var s = states[i];
            switch (random.Next(4))
            {
                case 0: s.Top = Math.Min(10, s.Top + 1); break;
                case 1: s.Right = Math.Min(10, s.Right + 1); break;
                case 2: s.Bottom = Math.Min(10, s.Bottom + 1); break;
                default: s.Left = Math.Min(10, s.Left + 1); break;
            }
        }
        var perks = Math.Min(states.Count, OdysseyBalance.OpponentPerkCards(world));
        foreach (var i in Enumerable.Range(0, states.Count).OrderBy(_ => random.Next()).Take(perks))
            states[i].Perk = OdysseyBalance.OpponentPerkPool[random.Next(OdysseyBalance.OpponentPerkPool.Length)];
        return states;
    }

    private string PickName(Random random, IEnumerable<string> names)
    {
        var list = names.ToList();
        var fresh = list.Where(n => !R.UsedNames.Contains(n)).ToList();
        var name = (fresh.Count > 0 ? fresh : list)[random.Next((fresh.Count > 0 ? fresh : list).Count)];
        R.UsedNames.Add(name);
        return name;
    }

    private OpponentPlan RegularPlan(Random random)
    {
        var world = World;
        var target = OdysseyBalance.DeckPower(R.World, R.Duel, elite: false, boss: false);
        var deck = new List<TriadCard>();
        // A regular carries a themed card half the time: one near the deck's own strength, so an early world's hero isn't handed over.
        if (random.NextDouble() < OdysseyBalance.RegularThemedChance)
        {
            var themed = Themed(world).Where(c => Math.Abs(Power(c) - target) <= .15).ToList();
            if (themed.Count > 0) deck.Add(themed[random.Next(themed.Count)]);
        }
        Fit(random, deck, target, OdysseyBalance.OpponentLimits(R.World, elite: false));
        var name = PickName(random, world.Duelists.Select(d => d.Name));
        var duelist = world.Duelists.First(d => d.Name == name);
        return new OpponentPlan
        {
            Name = duelist.Name, Title = duelist.Title, Kind = OpponentKind.Regular, Rules = RollRules(random, R.World, 0), Materia = RollMateria(random, R.World, false),
            Deck = Enhanced(random, deck, R.World), AiLevel = OdysseyBalance.AiLevel(R.World, false, false, R.Rank), Music = world.DuelMusic,
            Signature = deck.OrderByDescending(Power).First().Id, Power = Power(deck),
        };
    }

    private OpponentPlan ElitePlan(Random random)
    {
        var world = World;
        var target = OdysseyBalance.DeckPower(R.World, R.Duel, elite: true, boss: false);
        var name = PickName(random, world.Elites.Select(e => e.Name));
        var elite = world.Elites.First(e => e.Name == name);
        var deck = new List<TriadCard>();
        if (byId.TryGetValue(elite.SignatureCard, out var signature) && Shown(signature)) deck.Add(signature);
        if (random.NextDouble() < OdysseyBalance.EliteSecondThemedChance)
        {
            var themed = Themed(world).Where(c => deck.All(d => d.Id != c.Id) && Allows(deck, c, OdysseyBalance.OpponentLimits(R.World, elite: true)) && Math.Abs(Power(c) - target) <= .2).ToList();
            if (themed.Count > 0) deck.Add(themed[random.Next(themed.Count)]);
        }
        Fit(random, deck, target, OdysseyBalance.OpponentLimits(R.World, elite: true));
        return new OpponentPlan
        {
            Name = elite.Name, Title = elite.Title, Kind = OpponentKind.Elite, Rules = RollRules(random, R.World, OdysseyBalance.EliteExtraRules), Materia = RollMateria(random, R.World, false),
            Deck = Enhanced(random, deck, R.World), AiLevel = OdysseyBalance.AiLevel(R.World, true, false, R.Rank), Music = world.EliteMusic,
            Signature = elite.SignatureCard, Power = Power(deck),
        };
    }

    /// <summary>A world's boss, the same whenever it is asked for (the title card, the fortune teller, the path): four fixed cards and a fitted fifth.</summary>
    public OpponentPlan BossPlan(int world)
    {
        var w = OdysseyContent.WorldAt(world);
        var random = R.Fixed(world);
        var target = OdysseyBalance.DeckPower(world, OdysseyBalance.BossDuel, elite: false, boss: true);
        var deck = w.Boss.Fixed.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        var limits = OdysseyBalance.BossLimits(world);
        if (deck.Count < OdysseyBalance.DeckSize)
        {
            // The flexible slot: the themed card nearest the need when one is close, else any card. Close is generous:
            // a late boss's need is often past every card, and a card from another world (Ardbert in Kefka's deck) jars.
            var need = target * OdysseyBalance.DeckSize - deck.Sum(Power);
            var themed = Themed(w).Where(c => deck.All(d => d.Id != c.Id) && Allows(deck, c, limits)).OrderBy(c => Math.Abs(Power(c) - need)).ToList();
            if (themed.Count > 0 && Math.Abs(Power(themed[0]) - need) <= OdysseyBalance.BossThemedSlack) deck.Add(themed[0]);
            // A need past every themed card: the strongest themed one, when it is nearly as strong as any card the deck may take.
            else if (themed.Count > 0 && themed.MaxBy(Power) is { } best && need > Power(best)
                && cards.Where(c => Shown(c, world) && deck.All(d => d.Id != c.Id) && Allows(deck, c, limits)).Select(Power).DefaultIfEmpty(0).Max() - Power(best) <= OdysseyBalance.BossThemedSlack)
                deck.Add(best);
            Fit(random, deck, target, limits, world);
        }
        var rules = RollRules(random, world, OdysseyBalance.BossExtraRules(R.Rank), w.Boss.Rules);
        return new OpponentPlan
        {
            Name = w.Boss.Name, Title = w.Boss.Title, Line = w.Boss.Line, Kind = OpponentKind.Boss, Rules = rules, Materia = RollMateria(random, world, true, w.Boss.Materia),
            Deck = Enhanced(random, deck, world), AiLevel = OdysseyBalance.AiLevel(world, false, true, R.Rank), Music = w.BossMusic,
            Signature = deck[0].Id, Power = Power(deck),
        };
    }

    // The Endsinger's second form (Crystal Rank 10): her other deck, the same rules and Materia, a fresh deal.
    private OpponentPlan SecondFormPlan()
    {
        var first = BossPlan(OdysseyBalance.Worlds);
        var w = World;
        var random = R.Next();
        var deck = w.Boss.SecondForm.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        Fit(random, deck, OdysseyBalance.PowerCap, OdysseyBalance.BossLimits(R.World));
        return new OpponentPlan
        {
            Name = first.Name + ", Unbound", Title = "The Final Chord", Line = "The song is not yet over.", Kind = OpponentKind.Boss, Rules = first.Rules, Materia = first.Materia,
            Deck = Enhanced(random, deck, R.World), AiLevel = first.AiLevel, Music = first.Music, Signature = deck[0].Id, Power = Power(deck),
        };
    }

    // Gilgamesh: an elite with Swap, a little above the world's elites, with both his cards to claim.
    private OpponentPlan GilgameshPlan(Random random)
    {
        var target = OdysseyBalance.DeckPower(R.World, R.Duel, elite: true, boss: false) + OdysseyBalance.SecretDuelPower;
        var deck = OdysseyContent.GilgameshCards.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        Fit(random, deck, target, OdysseyBalance.OpponentLimits(R.World, elite: true));
        var rules = RollRules(random, R.World, OdysseyBalance.EliteExtraRules);
        if (!rules.Contains(TriadRule.Swap)) rules.Insert(rules.Count - 1, TriadRule.Swap);
        return new OpponentPlan
        {
            Name = OdysseyContent.Gilgamesh.Name, Title = OdysseyContent.Gilgamesh.Title, Line = "Enough talk. Have at you!", Kind = OpponentKind.Secret, Rules = rules,
            Materia = RollMateria(random, R.World, false), Deck = Enhanced(random, deck, R.World), AiLevel = OdysseyBalance.AiLevel(R.World, true, false, R.Rank),
            Music = OdysseyContent.GilgameshMusic, Signature = OdysseyContent.Gilgamesh.SignatureCard, Power = Power(deck), Claimable = [.. OdysseyContent.GilgameshCards], Event = EventKind.Gilgamesh,
        };
    }

    private OpponentPlan GuestStarPlan(Random random, GuestStar guest)
    {
        var target = OdysseyBalance.DeckPower(R.World, R.Duel, elite: true, boss: false) + OdysseyBalance.SecretDuelPower;
        var deck = new List<TriadCard>();
        if (byId.TryGetValue(guest.Card, out var card)) deck.Add(card);
        Fit(random, deck, target, OdysseyBalance.OpponentLimits(R.World, elite: true));
        return new OpponentPlan
        {
            Name = guest.Name, Title = guest.Title, Line = guest.Line, Kind = OpponentKind.Secret, Rules = RollRules(random, R.World, OdysseyBalance.EliteExtraRules),
            Materia = RollMateria(random, R.World, false), Deck = Enhanced(random, deck, R.World), AiLevel = OdysseyBalance.AiLevel(R.World, true, false, R.Rank),
            Music = OdysseyContent.GuestStarMusic, Signature = guest.Card, Power = Power(deck), Claimable = [guest.Card], Event = EventKind.GuestStar,
        };
    }
}
