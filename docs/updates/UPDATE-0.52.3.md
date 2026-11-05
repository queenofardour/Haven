# Haven 0.52.3: music fixes, bigger card numbers, a new look for the Odyssey

**A crash on leaving a home.** The game could crash in its sound system shortly after you left a home where music had played (orchestrion songs, Triple Triad or the Crystal Odyssey). Haven plays these through the game's orchestrion player and stops each song before starting the next. In two cases it didn't recognise a song as its own, so the song was never stopped:
- a track that was still loading when the next one started (the Odyssey changes music between screens);
- a game track replaced by a Penumbra mod, which loads from the mod's folder under another name.

A song left running like that could later crash the game's sound system. Haven now recognises its songs in both cases. If it ever leaves one alone, it writes a `[MUSIC]` line to the log.

**Card numbers at the game's size.** The numbers on Triple Triad cards were noticeably smaller than in the game. Haven read the game's font sizes as pixels, but they are points, and a point is a third bigger than a pixel. So every game text Haven draws came out at three quarters of its size, and slightly soft. That includes the Triple Triad board, the Gold Saucer machines' windows and the Odyssey's cards. They are now drawn at the game's own size.

**A new look for the Crystal Odyssey.** Every screen outside a duel is now drawn in Haven's own style: a clean dark panel, sharp text in Haven's typeface at any screen size, and Haven's buttons and panels. Before, these screens were the game's Triple Triad result window stretched to fit, which looked blurry and low-resolution. Cards are still the game's own, and duels are still played on the game's board.

**Crystal Odyssey music:**
- Regular duels play the world's battle music. Before, they played the same track as the world's map screens, so they never sounded like a battle.
- The title screen plays *Prelude – Discoveries* instead of the Gold Saucer theme.

The Fortune Teller also names the boss's rules properly ("Sudden Death", not "SuddenDeath").
