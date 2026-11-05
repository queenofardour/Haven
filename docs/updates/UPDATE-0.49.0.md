# Haven 0.49.0: The Crystal Odyssey

**A Triple Triad roguelite at your Triple Triad Board.** Open the Board and choose the new **Odyssey** tab. Fourteen worlds, from FINAL FANTASY to FINAL FANTASY XIV; five duels and a boss in each. You start with a weak, random binder of cards and one rule: lose a duel and the run is over.

- **Grow your deck as you go.** After every win, take one of your opponent's cards, or an Enhancement (raise a card's numbers, give it a perk such as Steadfast or Crown), a Materia (a lasting advantage, from Protect to Knights of the Round) or an Item (Potions, Ethers, a Phoenix Down that saves you from one loss).
- **Every world is its own game.** Each has its own duelists, elites and boss, with their rules and Materia shown before you choose, and cards from that game to win: take Cloud from Sephiroth's deck, or Golbez from Zeromus's. Each world plays its own music, from Battle on the Big Bridge to Dancing Mad.
- **Opponents grow smarter.** They play loosely in the first worlds and nearly flawlessly by the last. They never peek at cards they shouldn't see.
- **Between worlds**, spend your gil at the Gold Saucer Card Shop. Events on the road bring a Tonberry's wager, Cid's workshop, and perhaps Gilgamesh himself.
- **Your run is saved** after every screen, so you can stop and continue later. Clear it once to unlock Crystal Ranks, ten harder ways to play.

Everything is drawn with the game's own Triple Triad windows, cards and fonts. Nothing reaches the server; no gil, MGP or cards are won or spent.

**Triple Triad cards show their real stars.** Every card showed five stars in Haven's matches; each now shows its own count.

**Typed emotes keep working.** Once during a visit, every typed emote (/laugh, /pet, even ones everyone owns) answered "That command is unavailable at this time" until you left the home, while the emote window kept working. The cause was a Gold Saucer machine: opening one and starting a round change a game UI flag that also switches the text commands' emote restriction, and in the Gold Saucer the machine's event puts it right afterwards. Haven now puts both back when a machine closes. Typed emotes also play the way the emote window plays them, so they keep working even if something else ever switches the restriction on. Emotes with more after them (/laugh motion) still go to the game as before; if the game refuses one, Haven lifts the restriction and writes the details to the log (`[EMOTE-CMD]`).

**Flowerpots bloom.** Select a Riviera, Glade or Oasis Flowerpot while decorating to plant any flower a vase takes, in its colour. It shows in full bloom, and guests see it too.

Client only: works with relay 0.32.0.
