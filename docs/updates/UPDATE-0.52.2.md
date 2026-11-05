# Haven 0.52.2: NPC fixes

**Glamourer changes show straight away.** Changes that need the NPC's model rebuilt, such as face, race, body or some hair, only showed after the NPC was sent away and brought back. Glamourer asks Penumbra to redraw the character, and Penumbra does that by hiding it for a moment. The game rebuilds the model at that point for normal characters, but not for characters a plugin made, like Haven's NPCs. Haven now notices the request and rebuilds the NPC itself, and Glamourer dresses the new model.

**Renaming saves when you leave the name field.** Pressing Enter, pressing Tab, or clicking anywhere else keeps the new name. You no longer have to press Enter.

**Jumps come down faster.** The fall pulls harder than the rise, as a player's jump does, so the jump no longer looks floaty.
