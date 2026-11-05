namespace Haven.Games;

/// <summary>The character's own Triple Triad cards, from the game's record of unlocked cards.</summary>
internal static class TriadCollection
{
    public static unsafe bool Owns(uint id)
    {
        var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        return ui != null && id <= ushort.MaxValue && ui->IsTripleTriadCardUnlocked((ushort)id);
    }

    /// <summary>
    /// The decks the player built in the game's Decks window (GoldSaucerModule; the game offers the first five), each with
    /// five cards the player owns. Incomplete decks and decks with cards no longer owned are left out, as the game refuses them.
    /// </summary>
    public static unsafe IReadOnlyList<TriadDeck> Decks(IReadOnlyList<Haven.Core.TriadCard> cards)
    {
        var ui = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        var module = ui == null ? null : ui->GetGoldSaucerModule();
        if (module == null) return [];
        var byId = cards.ToDictionary(c => c.Id);
        var decks = new List<TriadDeck>();
        var all = module->Decks;
        for (var i = 0; i < Math.Min(5, all.Length); i++)
        {
            ref var deck = ref all[i];
            var hand = new List<Haven.Core.TriadCard>();
            foreach (var id in deck.Cards) if (id != 0 && byId.TryGetValue(id, out var card) && Owns(id)) hand.Add(card);
            var allowed = new List<Haven.Core.TriadCard>();
            foreach (var card in hand) if (Haven.Core.TriadGame.DeckAllows(allowed, card)) allowed.Add(card);
            if (hand.Count != 5 || allowed.Count != 5) continue;
            var name = deck.NameString;
            decks.Add(new(string.IsNullOrWhiteSpace(name) ? $"Deck {i + 1}" : name, hand));
        }
        return decks;
    }
}
