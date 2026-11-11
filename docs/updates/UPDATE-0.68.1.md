# Haven 0.68.1: ready for the game's November update

The game's update of 3 November (version 2026.11.03) broke nothing in Haven. All of Haven's code lookups still find their places in the game, and homes, furniture, pose balls, the Gold Saucer machines and Triple Triad all work on it.

**Paused features back on.** After a game update, Haven pauses five features that write into the game's memory in places it has to check by hand, until a Haven release says the new version has been checked. This is that release, so these are back on by themselves:
- lamp glow and machine flashes;
- the free building camera passing through walls;
- every emote in the emote window inside a home, owned or not;
- the Gold Saucer machines;
- `/facecamera` in gatherings.

If you had turned them on yourself in Settings → Advanced → "After a game update", nothing changes for you.

**The free camera goes up and down.** While the free building camera is on, **Space** raises it and **C** lowers it, faster with Shift, like WASD. C doesn't open the character window while the camera is on.

**People walking in during a visit.** The update changed how the game tells Haven a player has come into view. Haven already noticed this by itself and caught up, but it could forget one of the two ways the game now uses each time it learned the other. It now keeps both.

This release changes Haven only; the relay is unchanged.
