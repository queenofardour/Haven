# Haven 0.68.0: effects and sounds in gatherings

The world's effects and sounds, placed from Furnish → Catalogue → World, now work in gatherings as well as Just me visits. Guests see and hear them, decorators can place them, and the owner approves them like any other piece. The sound volume of World sets can also be changed while hosting.

This release needs Haven's updated server (relay 0.47.0). Everyone in a gathering needs Haven 0.68.0: an older Haven is told to update when it tries to host or join.

Earlier changes since 0.65.3, for anyone updating from the public release:

- **Haven draws lights, effects and sounds itself.** Stagehand is still needed, but only for imported Stagehand objects. Settings → Advanced can hand lights, effects or sounds back to Stagehand.
- **World sets' own lights, effects and sounds.** Sets marked with a bulb, sparkle or note light their surroundings, show their flames and fountains, or play their sounds. Filters find them.
- **The world's effects and sounds to place.** About 1,050 effects (fires, fountains, glows, weather and more), each with a picture and its size, and about 740 looping sounds with a play button to listen first. Names are guesses and can be wrong; place an effect to see it. A filter lists the effects that fit a room.
- **Tidier World catalogue.** Thousands of pieces that draw nothing, are only collision, or are far too large for a home are hidden, and world models rest on the floor by their real shape.
- **Tidier storage.** Settings → Custom content → On this PC shows what's kept for other people's homes, with a limit and a way to clear it. NPC mods have their own tab.
- **Safety after game updates.** `/haven selfcheck` says whether every part of Haven found what it needs. After a game patch, the few features that write to the game's memory directly pause until Haven is checked on that version (Settings → Advanced → After a game update).
- **Free trials.** Custom furniture and animations that need an expansion a free trial doesn't have are left out cleanly instead of failing.
