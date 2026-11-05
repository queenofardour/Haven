namespace Haven.Core.Odyssey;

public sealed partial class Odyssey
{
    // ---- Rewards after a won duel (design §1.2) ----

    private void OfferReward()
    {
        var o = R.Opponent!;
        var random = R.Next();
        var secret = o.Kind == OpponentKind.Secret;
        var reward = new RewardState
        {
            From = o.Kind, Secret = secret,
            Heading = o.IsBoss ? $"{o.Name} falls" : secret ? $"{o.Name} yields" : "Victory",
            Claimable = [.. o.Deck.Where(c => o.Claimable.Count == 0 || o.Claimable.Contains(c.Id))],
        };
        reward.Choices.Add(new RewardChoice { Kind = RewardKind.ClaimCard, Key = "", Rarity = Rarity.Common });
        if (!o.IsBoss) RollChoices(random, reward, elite: o.IsElite, legendaryOnly: o.Event == EventKind.Gilgamesh, first: true);
        R.Reward = reward;
        R.Screen = OdysseyScreen.Reward;
    }

    // A free Enhancement before world I (0.52.6): three Common ones, no rerolls, so a bad first duel or two doesn't end
    // a run before it has anything.
    private void OfferStartingEnhancement()
    {
        var random = R.Next();
        var reward = new RewardState { Starting = true, Heading = "A gift for the road" };
        for (var i = 0; i < OdysseyBalance.StartingEnhancements; i++)
        {
            var list = EnhancementChoices(Rarity.Common, reward.Choices);
            if (list.Count > 0) reward.Choices.Add(list[random.Next(list.Count)]);
        }
        R.Reward = reward;
        R.Screen = OdysseyScreen.Reward;
    }

    // The boss's second stage: a Materia (Rare or better) out of three.
    private void OfferBossMateria()
    {
        var random = R.Next();
        var reward = new RewardState { From = OpponentKind.Boss, Stage = 1, Heading = "A Materia from the crystal" };
        var count = OdysseyBalance.BossMateriaChoices + (Has(RunEffect.TreasureHunter) ? 1 : 0) + (R.Prep.Map != null ? 1 : 0);
        for (var i = 0; i < count; i++)
        {
            var rarity = random.NextDouble() < OdysseyBalance.BossLegendaryChance ? Rarity.Legendary : Rarity.Rare;
            var choice = RollMateriaChoice(random, rarity, reward.Choices) ?? RollMateriaChoice(random, Rarity.Rare, reward.Choices);
            if (choice != null) reward.Choices.Add(choice);
        }
        // Every boss also offers a Materia Slot while there is room for one (0.52.5): rolled, a slot was one Rare entry
        // among a dozen, so most runs stayed on three slots for many worlds while Materia kept being offered.
        if (R.Slots < OdysseyBalance.MaxSlots && reward.Choices.All(c => c.Kind != RewardKind.MateriaSlot))
            reward.Choices.Add(new RewardChoice { Kind = RewardKind.MateriaSlot, Key = OdysseyContent.MateriaSlotKey, Rarity = OdysseyContent.MateriaSlot.Rarity });
        R.Reward = reward;
        R.Screen = OdysseyScreen.Reward;
    }

    // Rolls the rolled choices of a reward screen (the card claim stays). Elites get one Rare or better; Gilgamesh gives
    // Legendaries; a Treasure Map used on the duel adds a choice (an Ancient one makes every choice Rare or better,
    // rerolls included: the duel's prep lasts until the next opponent).
    private void RollChoices(Random random, RewardState reward, bool elite, bool legendaryOnly, bool first)
    {
        reward.Choices.RemoveAll(c => c.Kind != RewardKind.ClaimCard);
        var count = legendaryOnly ? OdysseyBalance.SecretLegendaryChoices : elite ? OdysseyBalance.EliteRolled : OdysseyBalance.RegularRolled;
        if (Has(RunEffect.TreasureHunter)) count++;
        if (R.Prep.Map != null) count++;
        var minimum = R.Prep.Map ?? Rarity.Common;
        for (var i = 0; i < count; i++)
        {
            var rarity = legendaryOnly ? Rarity.Legendary : RollRarity(random, R.World, elite, minimum);
            if (elite && !legendaryOnly && i == count - 1 && reward.Choices.All(c => c.Rarity < Rarity.Rare)) rarity = RollRarity(random, R.World, elite, Rarity.Rare);
            var choice = RollChoice(random, rarity, reward.Choices);
            if (choice != null) reward.Choices.Add(choice);
        }
        // Pity counts the screens (not the rerolls) that showed nothing Rare.
        if (first) R.Pity = reward.Choices.Any(c => c.Rarity >= Rarity.Rare) ? 0 : R.Pity + OdysseyBalance.PityStep;
    }

    /// <summary>The rarity weights in force (world, elite shift, pity), as the UI may show them.</summary>
    /// <summary>A reward's rarity weights in a world (the current one unless given: the shop stocks for the next).</summary>
    public double[] RarityWeights(bool elite, int? world = null)
    {
        var w = OdysseyBalance.RarityWeights(world ?? R.World);
        if (elite)
        {
            var shifted = new double[4];
            for (var i = 0; i < 4; i++) { var up = i < 3 ? w[i] * OdysseyBalance.EliteRarityShift : 0; shifted[i] += w[i] - up; if (i < 3) shifted[i + 1] += up; }
            w = shifted;
        }
        var pity = Math.Min(w[0], R.Pity);
        w[0] -= pity; w[2] += pity;
        return w;
    }

    private Rarity RollRarity(Random random, int world, bool elite, Rarity minimum)
    {
        var w = RarityWeights(elite, world);
        var total = 0.0;
        for (var i = (int)minimum; i < 4; i++) total += w[i];
        var roll = random.NextDouble() * total;
        for (var i = (int)minimum; i < 4; i++) { roll -= w[i]; if (roll < 0) return (Rarity)i; }
        return Rarity.Legendary;
    }

    // One rolled reward of a rarity: a kind by weight, then an entry of that kind and rarity not already offered (stepping
    // down a rarity when nothing of it is left to offer).
    private RewardChoice? RollChoice(Random random, Rarity rarity, IReadOnlyList<RewardChoice> offered)
    {
        for (var r = rarity; r >= Rarity.Common; r--)
        {
            var kinds = new List<(int Kind, double Weight)>();
            if (EnhancementChoices(r, offered).Count > 0) kinds.Add((0, OdysseyBalance.KindWeights[0]));
            if (MateriaChoices(r, offered).Count > 0) kinds.Add((1, OdysseyBalance.KindWeights[1]));
            if (ItemChoices(r, offered).Count > 0) kinds.Add((2, OdysseyBalance.KindWeights[2]));
            if (kinds.Count == 0) continue;
            var roll = random.NextDouble() * kinds.Sum(k => k.Weight);
            var kind = kinds[^1].Kind;
            foreach (var k in kinds) { roll -= k.Weight; if (roll < 0) { kind = k.Kind; break; } }
            var list = kind == 0 ? EnhancementChoices(r, offered) : kind == 1 ? MateriaChoices(r, offered) : ItemChoices(r, offered);
            return Pick(random, list);
        }
        return null;
    }
    private RewardChoice? RollMateriaChoice(Random random, Rarity rarity, IReadOnlyList<RewardChoice> offered)
    {
        for (var r = rarity; r >= Rarity.Common; r--)
        {
            var list = MateriaChoices(r, offered);
            if (list.Count > 0) return Pick(random, list);
        }
        return null;
    }
    private static List<RewardChoice> EnhancementChoices(Rarity r, IReadOnlyList<RewardChoice> offered) =>
        [.. OdysseyContent.Enhancements.Where(e => e.Rarity == r && offered.All(o => o.Key != e.Key)).Select(e => new RewardChoice { Kind = RewardKind.Enhancement, Key = e.Key, Rarity = r })];
    private List<RewardChoice> MateriaChoices(Rarity r, IReadOnlyList<RewardChoice> offered)
    {
        var list = OdysseyContent.AllMateria.Where(m => m.Rarity == r && !R.Materia.Contains(m.Key) && offered.All(o => o.Key != m.Key))
            .Select(m => new RewardChoice { Kind = RewardKind.Materia, Key = m.Key, Rarity = r }).ToList();
        if (r == OdysseyContent.MateriaSlot.Rarity && R.Slots < OdysseyBalance.MaxSlots && offered.All(o => o.Kind != RewardKind.MateriaSlot))
            list.Add(new RewardChoice { Kind = RewardKind.MateriaSlot, Key = OdysseyContent.MateriaSlotKey, Rarity = r });
        if (r == OdysseyContent.MasterMateria.Rarity && !R.MasterMateria && offered.All(o => o.Kind != RewardKind.MasterMateria))
            list.Add(new RewardChoice { Kind = RewardKind.MasterMateria, Key = OdysseyContent.MasterMateriaKey, Rarity = r });
        return list;
    }
    // One choice of a list: evenly, but the Master Materia at its own weight (a list without it draws as it always did).
    private static RewardChoice Pick(Random random, List<RewardChoice> list)
    {
        if (list.All(c => c.Kind != RewardKind.MasterMateria)) return list[random.Next(list.Count)];
        double Weight(RewardChoice c) => c.Kind == RewardKind.MasterMateria ? OdysseyBalance.MasterMateriaWeight : 1;
        var roll = random.NextDouble() * list.Sum(Weight);
        foreach (var c in list) { roll -= Weight(c); if (roll < 0) return c; }
        return list[^1];
    }
    private List<RewardChoice> ItemChoices(Rarity r, IReadOnlyList<RewardChoice> offered) => R.Items.Count >= OdysseyBalance.MaxItems ? []
        : [.. OdysseyContent.Items.Where(i => i.Rarity == r && offered.All(o => o.Key != i.Key)).Select(i => new RewardChoice { Kind = RewardKind.Item, Key = i.Key, Rarity = r })];

    /// <summary>The price of the next reroll (0 with a Card Sharp's free one), or null when nothing can be rerolled.</summary>
    public int? RerollPrice()
    {
        if (Screen != OdysseyScreen.Reward || R.Reward!.Stage == 1 || R.Reward.Starting || R.Reward.Choices.All(c => c.Kind == RewardKind.ClaimCard)) return null;
        return Has(RunEffect.CardSharp) && !R.CardSharpUsed ? 0 : OdysseyBalance.RerollPrice(R.Reward.Rerolls);
    }

    /// <summary>Rerolls the rolled choices for MGP (not the card claim).</summary>
    public void Reroll()
    {
        Require(OdysseyScreen.Reward);
        var price = RerollPrice() ?? throw new InvalidOperationException("Nothing to reroll.");
        if (price > R.Mgp) throw new InvalidOperationException("Not enough gil.");
        if (price == 0) R.CardSharpUsed = true; else { R.Mgp -= price; R.Stats.MgpSpent += price; }
        var reward = R.Reward!;
        reward.Rerolls++;
        reward.Claiming = false;
        RollChoices(R.Next(), reward, elite: reward.From is OpponentKind.Elite or OpponentKind.Secret, legendaryOnly: R.Opponent?.Event == EventKind.Gilgamesh, first: false);
        Touch();
    }

    /// <summary>Whether some binder card can take an Enhancement (every side at A refuses a raise; a perk every card already has refuses it; a card with three Enhancements refuses all): an Upgrade screen must never be left with no card to pick.</summary>
    public bool CanApply(string enhancementKey)
    {
        if (OdysseyContent.EnhancementOf(enhancementKey) is not { } e) return false;
        var choices = e.Kind == EnhancementKind.SetType ? [1, 2, 3, 4] : e.NeedsChoice ? [0, 1, 2, 3] : new[] { 0 };
        return R.Binder.Any(b => choices.Any(choice => { Upgraded(b, e, choice, out var refusal); return refusal == null; }));
    }

    /// <summary>Why a reward choice can't be picked (null: it can).</summary>
    public string? RewardRefusal(int index)
    {
        if (Screen != OdysseyScreen.Reward) return "Not on the Reward screen.";
        var reward = R.Reward!;
        if (index < 0 || index >= reward.Choices.Count) return "No such choice.";
        var choice = reward.Choices[index];
        return choice.Kind switch
        {
            RewardKind.ClaimCard when reward.Claimable.Count == 0 => "Nothing to claim.",
            RewardKind.Item when R.Items.Count >= OdysseyBalance.MaxItems => "You already hold three Items.",
            RewardKind.Enhancement when !CanApply(choice.Key) => "No card of yours can take it.",
            RewardKind.MateriaSlot when R.Slots >= OdysseyBalance.MaxSlots => "You have six slots already.",
            RewardKind.MasterMateria when R.MasterMateria => "You hold the Master Materia already.",
            _ => null,
        };
    }

    /// <summary>Takes a reward: a card claim opens the sub-choice; an Enhancement opens the Upgrade screen; the rest apply at once.</summary>
    public void PickReward(int index)
    {
        if (RewardRefusal(index) is { } why) throw new InvalidOperationException(why);
        var reward = R.Reward!;
        var choice = reward.Choices[index];
        switch (choice.Kind)
        {
            case RewardKind.ClaimCard: reward.Claiming = true; Touch(); return;
            case RewardKind.Enhancement: BeginUpgrade(choice.Key, OdysseyScreen.Reward); Touch(); return;
            case RewardKind.Materia: GainMateria(choice.Key, OdysseyScreen.Reward); Touch(); return;
            case RewardKind.MateriaSlot: R.Slots++; R.Stats.MateriaHistory.Add($"{World.Numeral}-{R.Duel}: a Materia slot"); break;
            case RewardKind.MasterMateria: R.MasterMateria = true; R.Stats.MateriaHistory.Add($"{World.Numeral}-{R.Duel}: the Master Materia"); break;
            case RewardKind.Item: R.Items.Add(choice.Key); break;
        }
        RewardTaken();
        Touch();
    }

    /// <summary>Claims one of the beaten opponent's cards (after PickReward on the claim).</summary>
    public void ClaimCard(int index)
    {
        Require(OdysseyScreen.Reward);
        var reward = R.Reward!;
        if (!reward.Claiming) throw new InvalidOperationException("Pick the card claim first.");
        if (index < 0 || index >= reward.Claimable.Count) throw new InvalidOperationException("No such card.");
        var card = reward.Claimable[index].ToCard();
        AddToBinder(card);
        R.Stats.Claimed.Add(card.Id);
        RewardTaken();
        Touch();
    }

    /// <summary>Backs out of the card claim to the reward choices.</summary>
    public void CancelClaim()
    {
        Require(OdysseyScreen.Reward);
        R.Reward!.Claiming = false;
        Touch();
    }

    /// <summary>Takes nothing and moves on.</summary>
    public void SkipReward()
    {
        Require(OdysseyScreen.Reward);
        RewardTaken();
        Touch();
    }

    // After a reward is taken (or skipped): the boss's Materia stage, or what follows the duel.
    private void RewardTaken()
    {
        var reward = R.Reward!;
        if (reward.Starting) { R.Reward = null; R.Screen = OdysseyScreen.WorldTitle; return; }
        if (reward.From == OpponentKind.Boss && reward.Stage == 0) { OfferBossMateria(); return; }
        AfterDuel();
    }

    // ---- Materia ----

    // Slots a Materia, or asks which to drop when full; ReturnTo is where the Swap screen leads afterwards (and where
    // going back leads, giving back what was paid).
    private void GainMateria(string key, OdysseyScreen returnTo, int paid = 0, int ware = -1)
    {
        if (R.Materia.Count < R.Slots && !(IsLegendary(key) && Legendaries >= LegendaryLimit))
        {
            R.Materia.Add(key);
            R.Stats.MateriaHistory.Add($"{World.Numeral}-{R.Duel}: gained {OdysseyContent.MateriaOf(key)?.Name ?? key}");
            Return(returnTo);
            return;
        }
        R.Swap = new SwapState { Materia = key, ReturnTo = returnTo, Paid = paid, Ware = ware };
        R.Screen = OdysseyScreen.MateriaSwap;
    }

    /// <summary>
    /// Whether the Upgrade or Materia Swap screen can go back to where its Enhancement or Materia was chosen: the reward
    /// choices (the starting gift included), the shop (the price given back) or the workshop's offer. Nothing has been
    /// applied yet, so going back loses nothing.
    /// </summary>
    public bool CanGoBack => Screen switch
    {
        OdysseyScreen.Upgrade => R.Upgrade!.ReturnTo is OdysseyScreen.Reward or OdysseyScreen.Shop or OdysseyScreen.Event,
        OdysseyScreen.MateriaSwap => R.Swap!.ReturnTo is OdysseyScreen.Reward or OdysseyScreen.Shop,
        _ => false,
    };

    /// <summary>Leaves the Upgrade or Materia Swap screen without applying anything, back to the previous choice.</summary>
    public void GoBack()
    {
        if (!CanGoBack) throw new InvalidOperationException("There is nothing to go back to.");
        var (returnTo, paid, ware) = Screen == OdysseyScreen.Upgrade ? (R.Upgrade!.ReturnTo, R.Upgrade.Paid, R.Upgrade.Ware) : (R.Swap!.ReturnTo, R.Swap.Paid, R.Swap.Ware);
        if (paid > 0) { R.Mgp += paid; R.Stats.MgpSpent -= paid; }
        if (ware >= 0 && R.Shop is { } shop && ware < shop.Wares.Count) shop.Wares[ware].Sold = false;
        R.Upgrade = null; R.Swap = null;
        R.Screen = returnTo;
        Touch();
    }

    /// <summary>How many Legendary Materia may be slotted at once (one, two with the Master Materia).</summary>
    public int LegendaryLimit => OdysseyBalance.LegendaryMateria + (R.MasterMateria ? 1 : 0);
    private int Legendaries => R.Materia.Count(IsLegendary);
    private static bool IsLegendary(string key) => OdysseyContent.MateriaOf(key)?.Rarity == Rarity.Legendary;
    /// <summary>Whether the Legendary Materia are at the limit (a new one must replace one of them, or be let go).</summary>
    public bool LegendariesFull => Run != null && Legendaries >= LegendaryLimit;

    /// <summary>Why the new Materia can't take this slot (null: it can): past the Legendary limit it may only replace a Legendary one.</summary>
    public string? KeepRefusal(int dropIndex)
    {
        if (Screen != OdysseyScreen.MateriaSwap) return "Not on the Materia screen.";
        if (dropIndex < 0 || dropIndex >= R.Materia.Count) return "No such slot.";
        if (IsLegendary(R.Swap!.Materia) && !IsLegendary(R.Materia[dropIndex]) && Legendaries >= LegendaryLimit)
            return LegendaryLimit == 1 ? "Only one Legendary Materia may be slotted: replace your Legendary one." : $"Only {LegendaryLimit} Legendary Materia may be slotted: replace one of them.";
        return null;
    }

    /// <summary>Keeps the new Materia in place of a slotted one.</summary>
    public void KeepMateria(int dropIndex)
    {
        Require(OdysseyScreen.MateriaSwap);
        if (KeepRefusal(dropIndex) is { } why) throw new InvalidOperationException(why);
        var swap = R.Swap!;
        R.Stats.MateriaHistory.Add($"{World.Numeral}-{R.Duel}: dropped {OdysseyContent.MateriaOf(R.Materia[dropIndex])?.Name} for {OdysseyContent.MateriaOf(swap.Materia)?.Name}");
        R.Materia[dropIndex] = swap.Materia;
        R.Swap = null;
        Return(swap.ReturnTo);
        Touch();
    }

    /// <summary>Discards the new Materia.</summary>
    public void DiscardNewMateria()
    {
        Require(OdysseyScreen.MateriaSwap);
        var swap = R.Swap!;
        R.Swap = null;
        Return(swap.ReturnTo);
        Touch();
    }

    // Where an Upgrade or Swap screen leads: back to the shop, on from the reward, or on from an event.
    private void Return(OdysseyScreen returnTo)
    {
        switch (returnTo)
        {
            case OdysseyScreen.Shop: R.Screen = OdysseyScreen.Shop; break;
            case OdysseyScreen.Reward: RewardTaken(); break;
            case OdysseyScreen.Event: EventDone(); break;
            default: R.Screen = returnTo; break;
        }
    }

    // ---- Upgrade (an Enhancement's card and side) ----

    private void BeginUpgrade(string enhancement, OdysseyScreen returnTo, int paid = 0, int ware = -1)
    {
        R.Upgrade = new UpgradeState { Enhancement = enhancement, ReturnTo = returnTo, Paid = paid, Ware = ware };
        R.Screen = OdysseyScreen.Upgrade;
    }

    /// <summary>
    /// The card as it would be after the pending Enhancement. Choice: a side (0 top, 1 right, 2 bottom, 3 left) for
    /// Grade 1 Dark Matter and a Mythril Whetstone, a corner (0 top-right, 1 bottom-right, 2 bottom-left, 3 top-left) for
    /// Grade 5 and Fantasia, a type (1-4) for a Glamour Prism; ignored otherwise. Refusal says why it can't be applied (a side already at A, the same type).
    /// </summary>
    public (TriadCard Before, TriadCard After, bool ReplacesPerk, string? Refusal) UpgradePreview(int uid, int choice = 0)
    {
        if (Screen != OdysseyScreen.Upgrade) throw new InvalidOperationException("Not on the Upgrade screen.");
        var e = OdysseyContent.EnhancementOf(R.Upgrade!.Enhancement) ?? throw new InvalidOperationException("Unknown enhancement.");
        var b = Find(uid);
        var before = Card(b);
        var after = Upgraded(b, e, choice, out var refusal);
        return (before, Card(after), e.Kind == EnhancementKind.Perk && b.Perk != TriadPerk.None && b.Perk != e.Perk, refusal);
    }

    private BinderCard Upgraded(BinderCard b, Enhancement e, int choice, out string? refusal)
    {
        refusal = null;
        var c = new BinderCard { Uid = b.Uid, Id = b.Id, Raised = [.. b.Raised], Perk = b.Perk, Type = b.Type, Enhancements = b.Enhancements + 1, World = b.World, Duel = b.Duel };
        if (b.Enhancements >= OdysseyBalance.MaxEnhancements) { refusal = EnhancedFully; return c; }
        var card = Card(b);
        bool Raise(int side)
        {
            if (card.Side(side) >= 10) return false;
            c.Raised[side]++;
            return true;
        }
        switch (e.Kind)
        {
            case EnhancementKind.Side:
                if (choice is < 0 or > 3) refusal = "Choose a side.";
                else if (!Raise(choice)) refusal = "That side is an A already.";
                break;
            case EnhancementKind.Corner:
                if (choice is < 0 or > 3) refusal = "Choose a corner.";
                else { var a = Raise(choice); var d = Raise((choice + 1) % 4); if (!a && !d) refusal = "Both sides are an A already."; }
                break;
            case EnhancementKind.AllSides:
                if (!(Raise(0) | Raise(1) | Raise(2) | Raise(3))) refusal = "Every side is an A already.";
                break;
            case EnhancementKind.SetType:
                if (choice is < 1 or > 4) refusal = "Choose a type.";
                else if (card.Type == choice) refusal = "It is that type already.";
                else c.Type = choice;
                break;
            case EnhancementKind.Perk:
                if (b.Perk == e.Perk) refusal = "It has that perk already.";
                else c.Perk = e.Perk;
                break;
            case EnhancementKind.Shift:
                // +2 up to A on the chosen side, -1 down to 1 opposite (so no raise is ever hidden past the clamp).
                if (choice is < 0 or > 3) refusal = "Choose a side.";
                else if (card.Side(choice) >= 10) refusal = "That side is an A already.";
                else
                {
                    c.Raised[choice] += Math.Min(2, 10 - card.Side(choice));
                    if (card.Side((choice + 2) % 4) > 1) c.Raised[(choice + 2) % 4]--;
                }
                break;
            case EnhancementKind.SwapCorner:
                if (choice is < 0 or > 3) refusal = "Choose a corner.";
                else if (card.Side(choice) == card.Side((choice + 1) % 4)) refusal = "Both sides are the same already.";
                else
                {
                    // Each side takes the other's number as it plays now, measured from the printed card.
                    var printed = Base(b.Id);
                    var (a, d) = (choice, (choice + 1) % 4);
                    c.Raised[a] = card.Side(d) - printed.Side(a);
                    c.Raised[d] = card.Side(a) - printed.Side(d);
                }
                break;
        }
        return c;
    }

    private static readonly string EnhancedFully = $"It has {OdysseyBalance.MaxEnhancements} Enhancements already, the most a card can take.";

    /// <summary>Applies the pending Enhancement to a binder card (see UpgradePreview for the choice).</summary>
    public void ApplyUpgrade(int uid, int choice = 0)
    {
        Require(OdysseyScreen.Upgrade);
        var upgrade = R.Upgrade!;
        var e = OdysseyContent.EnhancementOf(upgrade.Enhancement) ?? throw new InvalidOperationException("Unknown enhancement.");
        var b = Find(uid);
        var after = Upgraded(b, e, choice, out var refusal);
        if (refusal != null) throw new InvalidOperationException(refusal);
        b.Raised = after.Raised; b.Perk = after.Perk; b.Type = after.Type; b.Enhancements = after.Enhancements;
        R.Upgrade = null;
        Return(upgrade.ReturnTo);
        Touch();
    }
}
