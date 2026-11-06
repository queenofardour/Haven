# Haven 0.65.6: safer custom content on a free trial

**Custom content no longer risks closing a free trial's game.** A free trial (or an account without the latest expansions) doesn't have Endwalker or Dawntrail game data, and the game closes when it's asked for a file it doesn't have. Custom furniture, minion props and pose animations carry the mod's own files, but a mod can also point at the game's own textures, effects or sounds. Now, before any of these is used on your PC, Haven checks that every game file it points at from an expansion is installed:
- custom furniture or a minion prop that needs data your game lacks shows as its original furnishing (a prop shows nothing);
- pose animations that need it aren't played: their balls stand still, as they do for a guest who didn't load the home's custom content.

Haven says once which item was left out and why. Nothing changes on a full game.

The World catalogue already guarded against this: a free trial lists no Endwalker or Dawntrail pieces, and a guest whose game lacks a piece sees nothing in its place.

This release changes Haven only; the relay is unchanged.
