# Haven 0.52.1: NPC fixes

**Glamourer looks show on NPCs again.** Glamourer took an NPC's look, but nothing changed on screen after the NPC was renamed, sent away and brought back, or made new. Penumbra and Glamourer keep a list of which character carries which name, and only update it when the game adds or removes a character. Haven reuses an NPC's character, so after a name change they were still looking for the old name. Haven now asks them to update the list whenever an NPC's name changes and before every Glamourer look. This fixes Copy my look, Glamourer designs, and editing an NPC in Glamourer's Actors tab.

**Renaming shows the new name above the NPC's head.** The game kept its old nameplate text. Haven now sets the nameplate to the NPC's name and asks the game to redraw it after every rename. The "Lv0" in front of the name is gone too.

**NPCs need a first and last name.** Glamourer only recognises characters with a player-style name. The name field says so if you type something else, and keeps your old name until you give a full one. NPCs that still have one-word names keep working, under a temporary full name shown in the panel.

**The "Speaking as" button looks like a button.** It is blue while you speak as yourself and green while you speak as the NPC. Click it to switch.

**Jumping:** the take-off now plays once over the falling pose instead of being held for the whole rise, which made the game restart or freeze it. Haven also writes the first few jumps of each visit to the log (`[NPC-JUMP]` lines). If jumping still looks wrong, those lines show what the game did.
