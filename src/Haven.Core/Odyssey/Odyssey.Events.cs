namespace Haven.Core.Odyssey;

/// <summary>A button on the Event screen: its text, whether it can be pressed and why not, and whether it needs a binder card (and a type) chosen.</summary>
public sealed record EventChoice(string Text, bool Enabled, string? Reason = null, bool NeedsCard = false, bool NeedsType = false);

public sealed partial class Odyssey
{
    // ---- Events between duels (design §1.3) ----

    private void OfferEvent()
    {
        var random = R.Next();
        var rare = random.NextDouble() < OdysseyBalance.RareEventChance;
        var pool = OdysseyContent.Events.Where(e => R.World >= e.MinWorld && e.Rare == rare
            && !(e.Kind == EventKind.Gilgamesh && R.GilgameshDone) && !(e.Kind == EventKind.GuestStar && R.GuestStarDone)
            && !(e.Kind == EventKind.FortuneTeller && R.World >= OdysseyBalance.Worlds)
            && !(e.Kind == EventKind.Stable && !R.Binder.Any(b => Base(b.Id).Stars < 5))).ToList();
        if (pool.Count == 0) pool = OdysseyContent.Events.Where(e => !e.Rare && R.World >= e.MinWorld && e.Kind != EventKind.FortuneTeller).ToList();
        var chosen = pool[random.Next(pool.Count)];
        var state = new EventState { Kind = chosen.Kind };
        switch (chosen.Kind)
        {
            case EventKind.Merchant:
                foreach (var item in OdysseyContent.Items.Where(i => OdysseyBalance.ShopSells(i, R.Rank) && i.Rarity <= OdysseyBalance.MerchantMaxRarity).OrderBy(_ => random.Next()).Take(OdysseyBalance.MerchantWares)) state.Wares.Add(item.Key);
                break;
            case EventKind.MooglePost:
                var commons = OdysseyContent.Items.Where(i => i.Rarity == Rarity.Common).ToList();
                state.Parcel = commons[random.Next(commons.Count)].Key;
                break;
            case EventKind.GuestStar:
                state.Guest = random.Next(OdysseyContent.GuestStars.Count);
                break;
        }
        if (chosen.Kind == EventKind.Gilgamesh) R.GilgameshDone = true;
        if (chosen.Kind == EventKind.GuestStar) R.GuestStarDone = true;
        R.EventLastDuel = true;
        R.Event = state;
        R.Screen = OdysseyScreen.Event;
    }

    /// <summary>The Event screen's buttons as the run stands (after a result, a single Continue).</summary>
    public IReadOnlyList<EventChoice> EventChoices()
    {
        Require(OdysseyScreen.Event);
        var e = R.Event!;
        if (e.Result != null) return [new("Continue", true)];
        var full = R.Items.Count >= OdysseyBalance.MaxItems ? "You already hold three Items." : null;
        switch (e.Kind)
        {
            case EventKind.Merchant:
                var wares = e.Wares.Select(k => OdysseyContent.ItemOf(k)!).Select(i => { var p = Price(i.Rarity); return new EventChoice($"Buy {i.Name} ({p} gil)", full == null && p <= R.Mgp, full ?? (p > R.Mgp ? "Not enough gil." : null)); }).ToList();
                wares.Add(new("Walk on", true));
                return wares;
            case EventKind.Workshop:
                var quick = CanApply("dark-matter-1"); var good = CanApply("dark-matter-superior");
                return [new("The quick job: +1 to one side, free", quick, quick ? null : "No card of yours can take it."),
                    new($"The good job: +1 to every side, {OdysseyBalance.WorkshopAllSidesPrice} gil", good && R.Mgp >= OdysseyBalance.WorkshopAllSidesPrice, !good ? "No card of yours can take it." : R.Mgp >= OdysseyBalance.WorkshopAllSidesPrice ? null : "Not enough gil."),
                    new("Leave the engine be", true)];
            case EventKind.Wager:
                return [new($"Stake {OdysseyBalance.WagerStake} gil ({OdysseyBalance.WagerWinChance:P0} to win {OdysseyBalance.WagerPrize})", R.Mgp >= OdysseyBalance.WagerStake, R.Mgp >= OdysseyBalance.WagerStake ? null : "Not enough gil."),
                    new("Back away slowly", true)];
            case EventKind.MooglePost:
                return [new($"The parcel: {OdysseyContent.ItemOf(e.Parcel!)?.Name}", full == null, full), new($"The refund: {OdysseyBalance.MooglePostMgp} gil", true)];
            case EventKind.Stable:
                if (e.Stage == 0) return [new("Offer a card to the chocobo", true, NeedsCard: true), new("Keep your cards", true)];
                // A trade that leaves the binder unable to make a legal deck (one star more can break the limits) is refused.
                var traded = e.Offered != null && CanFormDeck([.. R.Binder.Where(b => b.Uid != e.TradeUid), ToBinder(e.Offered.ToCard())]);
                return [new("Take the trade", traded, traded ? null : "The binder couldn't make a deck after this trade."), new("Keep your card", true)];
            case EventKind.Shrine:
                return [new("Give a card to the crystal", R.Binder.Count > OdysseyBalance.DeckSize, R.Binder.Count > OdysseyBalance.DeckSize ? null : "The binder is as thin as it can be.", NeedsCard: true),
                    new("Bless a card's type", true, NeedsCard: true, NeedsType: true), new("Bow and leave", true)];
            case EventKind.Gilgamesh:
                return [new("Have at you! (duel)", true), new("Cross a different bridge", true)];
            case EventKind.GuestStar:
                return [new($"Duel {OdysseyContent.GuestStars[e.Guest].Name}", true), new("Let the glitch pass", true)];
            case EventKind.FortuneTeller:
                return [new("Thank her and go", true)];
        }
        return [new("Continue", true)];
    }

    /// <summary>The Event screen's text, with the fortune teller's reading and the stable's offer written in.</summary>
    public string EventText()
    {
        Require(OdysseyScreen.Event);
        var e = R.Event!;
        var text = OdysseyContent.EventOf(e.Kind).Text;
        if (e.Kind == EventKind.GuestStar) text += $"\n\n{OdysseyContent.GuestStars[e.Guest].Name}, {OdysseyContent.GuestStars[e.Guest].Title}. {OdysseyContent.GuestStars[e.Guest].Line}";
        if (e.Kind == EventKind.FortuneTeller && R.World < OdysseyBalance.Worlds)
        {
            var boss = BossPlan(R.World + 1);
            var materia = boss.Materia.Count == 0 ? "no Materia" : string.Join(", ", boss.Materia.Select(k => OdysseyContent.MateriaOf(k)?.Name ?? k));
            text += $"\n\n\"{boss.Name}, {boss.Title}. The rules: {string.Join(", ", boss.Rules.Select(RuleWords))}. In hand: {string.Join(", ", boss.Deck.Select(c => c.Name))}. And {materia}.\"";
        }
        if (e.Kind == EventKind.Stable && e.Stage == 1 && e.Offered != null) text += $"\n\nThe chocobo trots back with {e.Offered.Name} ({e.Offered.Stars}-star) in its beak.";
        return e.Result != null ? text + "\n\n" + e.Result : text;
    }

    /// <summary>Why a binder card can't be the one an Event button asks for (null: it can): the stable has nothing above 5★, the shrine keeps a deck possible.</summary>
    public string? EventCardRefusal(int index, int cardUid)
    {
        if (Screen != OdysseyScreen.Event || R.Event!.Result != null) return "Not now.";
        if (R.Binder.All(b => b.Uid != cardUid)) return "No such card.";
        var e = R.Event;
        return e.Kind switch
        {
            EventKind.Stable when e.Stage == 0 && index == 0 => Base(Find(cardUid).Id).Stars >= 5 ? "The chocobo has nothing better than a five-star card." : null,
            EventKind.Shrine when index == 0 => CanFormDeck(R.Binder.Where(b => b.Uid != cardUid)) ? null : "The binder couldn't make a deck without it.",
            // The blessing is an Enhancement like a Glamour Prism, under the same cap.
            EventKind.Shrine when index == 1 => Find(cardUid).Enhancements >= OdysseyBalance.MaxEnhancements ? EnhancedFully : null,
            _ => null,
        };
    }

    /// <summary>Presses an Event button; a card (and a type, 1-4) where the button asks for one.</summary>
    public void ChooseEvent(int index, int cardUid = -1, int type = 0)
    {
        var choices = EventChoices();
        if (index < 0 || index >= choices.Count) throw new InvalidOperationException("No such choice.");
        var choice = choices[index];
        if (!choice.Enabled) throw new InvalidOperationException(choice.Reason ?? "Not now.");
        if (choice.NeedsCard && cardUid < 0) throw new InvalidOperationException("Choose a card.");
        if (choice.NeedsCard && EventCardRefusal(index, cardUid) is { } cardWhy) throw new InvalidOperationException(cardWhy);
        if (choice.NeedsType && type is < 1 or > 4) throw new InvalidOperationException("Choose a type.");
        var e = R.Event!;
        if (e.Result != null) { EventDone(); Touch(); return; }
        switch (e.Kind)
        {
            case EventKind.Merchant:
                if (index < e.Wares.Count)
                {
                    var item = OdysseyContent.ItemOf(e.Wares[index])!;
                    var price = Price(item.Rarity);
                    R.Mgp -= price; R.Stats.MgpSpent += price; R.Items.Add(item.Key);
                    e.Result = $"\"Pleasure doing business.\" You tuck away the {item.Name}.";
                }
                else EventDone();
                break;
            case EventKind.Workshop:
                if (index == 0) BeginUpgrade("dark-matter-1", OdysseyScreen.Event);
                else if (index == 1) { R.Mgp -= OdysseyBalance.WorkshopAllSidesPrice; R.Stats.MgpSpent += OdysseyBalance.WorkshopAllSidesPrice; BeginUpgrade("dark-matter-superior", OdysseyScreen.Event, OdysseyBalance.WorkshopAllSidesPrice); }
                else EventDone();
                break;
            case EventKind.Wager:
                if (index == 0)
                {
                    R.Mgp -= OdysseyBalance.WagerStake; R.Stats.MgpSpent += OdysseyBalance.WagerStake;
                    if (R.Next().NextDouble() < OdysseyBalance.WagerWinChance) { R.Mgp += OdysseyBalance.WagerPrize; R.Stats.MgpEarned += OdysseyBalance.WagerPrize; e.Result = $"The tonberry turns the last card. It is yours. {OdysseyBalance.WagerPrize} gil slide back across the barrel, and the lantern dims a little, as if disappointed."; }
                    else e.Result = "The tonberry turns the last card. It is not yours. The stake vanishes into the robe, and the knife does not move. You count that as a win.";
                }
                else EventDone();
                break;
            case EventKind.MooglePost:
                if (index == 0) { R.Items.Add(e.Parcel!); e.Result = $"\"Sign here, kupo!\" The parcel holds a {OdysseyContent.ItemOf(e.Parcel!)?.Name}."; }
                else { R.Mgp += OdysseyBalance.MooglePostMgp; R.Stats.MgpEarned += OdysseyBalance.MooglePostMgp; e.Result = $"The moogle counts out {OdysseyBalance.MooglePostMgp} gil, twice, and flies off muttering about paperwork."; }
                break;
            case EventKind.Stable:
                if (e.Stage == 0)
                {
                    if (index == 0)
                    {
                        var given = Find(cardUid);
                        var stars = Base(given.Id).Stars;
                        var pool = cards.Where(c => c.Stars == stars + 1 && Shown(c)).ToList();
                        e.TradeUid = cardUid; e.Offered = CardState.From(pool[R.Next().Next(pool.Count)]); e.Stage = 1;
                    }
                    else EventDone();
                }
                else if (index == 0)
                {
                    var given = Find(e.TradeUid);
                    R.Binder.Remove(given); R.Prep.Deck.Remove(given.Uid);
                    var card = e.Offered!.ToCard();
                    AddToBinder(card); R.Stats.Claimed.Add(card.Id);
                    e.Result = $"The chocobo swallows {Base(given.Id).Name} whole. {card.Name} is yours. \"Kweh.\"";
                }
                else e.Result = "The chocobo looks betrayed, then eats the greens instead.";
                break;
            case EventKind.Shrine:
                if (index == 0)
                {
                    var given = Find(cardUid);
                    R.Binder.Remove(given); R.Prep.Deck.Remove(given.Uid);
                    e.Result = $"{Base(given.Id).Name} sinks into the crystal and is kept. Your binder feels lighter, and sharper.";
                }
                else if (index == 1)
                {
                    var blessed = Find(cardUid);
                    blessed.Type = type; blessed.Enhancements++;
                    e.Result = $"{Base(blessed.Id).Name} glows and is remade: it is of the {TypeName(type)} now.";
                }
                else EventDone();
                break;
            case EventKind.Gilgamesh:
                if (index == 0) { R.Event = null; FaceOpponent(GilgameshPlan(R.Next())); }
                else EventDone();
                break;
            case EventKind.GuestStar:
                if (index == 0) { var guest = OdysseyContent.GuestStars[e.Guest]; R.Event = null; FaceOpponent(GuestStarPlan(R.Next(), guest)); }
                else EventDone();
                break;
            case EventKind.FortuneTeller:
                EventDone();
                break;
        }
        Touch();
    }

    // A rule as the Board names it: "SuddenDeath" reads "Sudden Death".
    private static string RuleWords(TriadRule rule) => string.Concat(rule.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
    public static string TypeName(int type) => type switch { 1 => "Primals", 2 => "Scions", 3 => "Societies", 4 => "Empire", _ => "no type" };

    // The event is over: the next path.
    private void EventDone()
    {
        R.Event = null; R.Upgrade = null;
        OfferPath();
    }
}
