namespace Haven.Core.Odyssey;

public sealed partial class Odyssey
{
    // ---- The Gold Saucer Card Shop, after each boss (design §1.4, §3.4) ----

    private void OpenShop()
    {
        R.Shop = new ShopState();
        StockShop(R.Next(), R.Shop);
        R.Screen = OdysseyScreen.Shop;
    }

    private int Price(Rarity rarity) => OdysseyBalance.Price(OdysseyBalance.BasePrice(rarity), Has(RunEffect.Haggler), R.Rank);
    private int Price(int basePrice) => OdysseyBalance.Price(basePrice, Has(RunEffect.Haggler), R.Rank);

    private void StockShop(Random random, ShopState shop)
    {
        shop.Wares.Clear(); shop.Pack.Clear(); shop.PackSold = false;
        var nextWorld = Math.Min(OdysseyBalance.Worlds, R.World + 1);
        for (var i = 0; i < OdysseyBalance.ShopEnhancements; i++)
        {
            var rarity = RollRarity(random, nextWorld, elite: false, Rarity.Common);
            var options = OdysseyContent.Enhancements.Where(e => e.Rarity == rarity && shop.Wares.All(w => w.Key != e.Key)).ToList();
            if (options.Count == 0) options = OdysseyContent.Enhancements.Where(e => shop.Wares.All(w => w.Key != e.Key)).ToList();
            var e = options[random.Next(options.Count)];
            shop.Wares.Add(new Ware { Kind = WareKind.Enhancement, Key = e.Key, Rarity = e.Rarity, Price = Price(e.Rarity) });
        }
        for (var i = 0; i < OdysseyBalance.ShopMateria; i++)
        {
            var rarity = RollRarity(random, nextWorld, elite: false, Rarity.Common);
            var options = OdysseyContent.AllMateria.Where(m => m.Rarity == rarity && !R.Materia.Contains(m.Key) && shop.Wares.All(w => w.Key != m.Key)).ToList();
            if (options.Count == 0) options = OdysseyContent.AllMateria.Where(m => !R.Materia.Contains(m.Key) && shop.Wares.All(w => w.Key != m.Key)).ToList();
            if (options.Count == 0) break;
            var m = options[random.Next(options.Count)];
            shop.Wares.Add(new Ware { Kind = WareKind.Materia, Key = m.Key, Rarity = m.Rarity, Price = Price(m.Rarity) });
        }
        for (var i = 0; i < OdysseyBalance.ShopItems; i++)
        {
            var rarity = RollRarity(random, nextWorld, elite: false, Rarity.Common);
            var options = OdysseyContent.Items.Where(it => it.Rarity == rarity && OdysseyBalance.ShopSells(it, R.Rank) && shop.Wares.All(w => w.Key != it.Key)).ToList();
            if (options.Count == 0) options = OdysseyContent.Items.Where(it => OdysseyBalance.ShopSells(it, R.Rank) && shop.Wares.All(w => w.Key != it.Key)).ToList();
            var it = options[random.Next(options.Count)];
            shop.Wares.Add(new Ware { Kind = WareKind.Item, Key = it.Key, Rarity = it.Rarity, Price = Price(it.Rarity) });
        }
        // The card pack: three cards of the next world's tier (fewer stars first when the tier is thin).
        var stars = OdysseyBalance.PackStars(nextWorld);
        var pool = cards.Where(c => c.Stars == stars && Shown(c)).ToList();
        if (pool.Count < OdysseyBalance.PackChoices) pool = cards.Where(c => c.Stars <= stars && Shown(c)).ToList();
        foreach (var card in pool.OrderBy(_ => random.Next()).Take(OdysseyBalance.PackChoices)) shop.Pack.Add(CardState.From(card));
        shop.Wares.Add(new Ware { Kind = WareKind.Pack, Key = "pack", Rarity = Rarity.Uncommon, Price = Price(OdysseyBalance.PackPrice) });
    }

    /// <summary>Why a ware can't be bought (null: it can).</summary>
    public string? BuyRefusal(int index)
    {
        if (Screen != OdysseyScreen.Shop) return "Not in the shop.";
        var shop = R.Shop!;
        if (index < 0 || index >= shop.Wares.Count) return "No such ware.";
        var ware = shop.Wares[index];
        if (ware.Sold) return "Sold.";
        if (ware.Price > R.Mgp) return "Not enough gil.";
        if (ware.Kind == WareKind.Item && R.Items.Count >= OdysseyBalance.MaxItems) return "You already hold three Items.";
        if (ware.Kind == WareKind.Enhancement && !CanApply(ware.Key)) return "No card of yours can take it.";
        if (ware.Kind == WareKind.Pack && shop.PackSold) return "Sold.";
        return null;
    }

    /// <summary>Buys a ware: an Enhancement opens the Upgrade screen (back to the shop after), a Materia slots in (or asks which to drop), an Item is held.</summary>
    public void Buy(int index)
    {
        if (BuyRefusal(index) is { } why) throw new InvalidOperationException(why);
        var ware = R.Shop!.Wares[index];
        if (ware.Kind == WareKind.Pack) throw new InvalidOperationException("Choose a card of the pack (BuyPackCard).");
        R.Mgp -= ware.Price; R.Stats.MgpSpent += ware.Price;
        ware.Sold = true;
        switch (ware.Kind)
        {
            case WareKind.Enhancement: BeginUpgrade(ware.Key, OdysseyScreen.Shop, ware.Price, index); break;
            case WareKind.Materia: GainMateria(ware.Key, OdysseyScreen.Shop, ware.Price, index); break;
            case WareKind.Item: R.Items.Add(ware.Key); break;
        }
        Touch();
    }

    /// <summary>Buys the card pack, taking one of its three cards.</summary>
    public void BuyPackCard(int cardIndex)
    {
        Require(OdysseyScreen.Shop);
        var shop = R.Shop!;
        var pack = shop.Wares.FindIndex(w => w.Kind == WareKind.Pack);
        if (pack < 0) throw new InvalidOperationException("No pack.");
        if (BuyRefusal(pack) is { } why) throw new InvalidOperationException(why);
        if (cardIndex < 0 || cardIndex >= shop.Pack.Count) throw new InvalidOperationException("No such card.");
        var ware = shop.Wares[pack];
        R.Mgp -= ware.Price; R.Stats.MgpSpent += ware.Price;
        ware.Sold = true; shop.PackSold = true;
        var card = shop.Pack[cardIndex].ToCard();
        AddToBinder(card);
        R.Stats.Claimed.Add(card.Id);
        Touch();
    }

    /// <summary>What the shop pays for a held Item: half what it would ask for it now (at least 1 gil).</summary>
    public int SellPrice(int index) =>
        index >= 0 && index < R.Items.Count && OdysseyContent.ItemOf(R.Items[index]) is { } item ? Math.Max(1, Price(item.Rarity) / OdysseyBalance.ItemResale) : 0;

    /// <summary>
    /// Sells a held Item back to the shop for half its price: gil, and a free slot for another (three held Items
    /// otherwise block buying more, and rewards stop offering them).
    /// </summary>
    public void SellItem(int index)
    {
        Require(OdysseyScreen.Shop);
        if (index < 0 || index >= R.Items.Count) throw new InvalidOperationException("No such Item.");
        var price = SellPrice(index);
        R.Items.RemoveAt(index);
        R.Mgp += price; R.Stats.MgpEarned += price;
        Touch();
    }

    /// <summary>What the shop pays for a binder card: by its printed stars, and more for each Enhancement on it (OdysseyBalance.CardSalePrice).</summary>
    public int CardSalePrice(int uid) => R.Binder.FirstOrDefault(b => b.Uid == uid) is { } b ? OdysseyBalance.CardSalePrice(Base(b.Id).Stars, b.Enhancements) : 0;
    /// <summary>Why a card can't be sold here (null: it can): one a visit, and never one the binder needs to make a deck.</summary>
    public string? CardSaleRefusal(int uid)
    {
        if (Screen != OdysseyScreen.Shop) return "Not in the shop.";
        if (R.Binder.All(b => b.Uid != uid)) return "No such card.";
        if (R.Shop!.CardSold) return "The shop buys one card a visit.";
        if (!CanFormDeck(R.Binder.Where(b => b.Uid != uid))) return "The binder couldn't make a deck without it.";
        return null;
    }
    /// <summary>
    /// Sells a binder card to the shop (one a visit): gil, and the card is gone like a removed one. A boss's signature
    /// card sells like any other, by its stars.
    /// </summary>
    public void SellCard(int uid)
    {
        if (CardSaleRefusal(uid) is { } why) throw new InvalidOperationException(why);
        var price = CardSalePrice(uid);
        R.Binder.Remove(Find(uid));
        R.Prep.Deck.Remove(uid);
        R.Shop!.CardSold = true;
        R.Mgp += price; R.Stats.MgpEarned += price;
        Touch();
    }

    /// <summary>The price of removing a card now.</summary>
    public int RemovalPrice() => OdysseyBalance.RemovalPrice(R.Shop?.Removals ?? 0, Has(RunEffect.Haggler), R.Rank);
    /// <summary>Why a card can't be removed from the binder here (null: it can).</summary>
    public string? RemoveRefusal(int uid)
    {
        if (Screen != OdysseyScreen.Shop) return "Not in the shop.";
        if (RemovalPrice() > R.Mgp) return "Not enough gil.";
        if (!CanFormDeck(R.Binder.Where(b => b.Uid != uid))) return "The binder couldn't make a deck without it.";
        return null;
    }
    /// <summary>Removes a card from the binder for MGP (the price rises each time).</summary>
    public void RemoveCard(int uid)
    {
        if (RemoveRefusal(uid) is { } why) throw new InvalidOperationException(why);
        var price = RemovalPrice();
        R.Mgp -= price; R.Stats.MgpSpent += price;
        R.Binder.Remove(Find(uid));
        R.Prep.Deck.Remove(uid);
        R.Shop!.Removals++;
        Touch();
    }

    public int ShopRerollPrice() => Price(OdysseyBalance.ShopReroll);
    /// <summary>Rolls the shop's unsold wares and pack again, for MGP.</summary>
    public void RerollShop()
    {
        Require(OdysseyScreen.Shop);
        var price = ShopRerollPrice();
        if (price > R.Mgp) throw new InvalidOperationException("Not enough gil.");
        R.Mgp -= price; R.Stats.MgpSpent += price;
        var shop = R.Shop!;
        shop.Rerolls++;
        var soldPack = shop.PackSold;
        StockShop(R.Next(), shop);   // a card sold stays sold: CardSold is the visit's
        if (soldPack) { shop.PackSold = true; shop.Wares.First(w => w.Kind == WareKind.Pack).Sold = true; }
        Touch();
    }

    /// <summary>Leaves the shop for the next world's title card.</summary>
    public void LeaveShop()
    {
        Require(OdysseyScreen.Shop);
        EnterWorld(R.World + 1);
        Touch();
    }
}
