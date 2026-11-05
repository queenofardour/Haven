# Haven 0.52.0: NPCs, properly

**A new NPCs tab.** Your NPCs are listed at the top; choose one to see everything about it below:
- **Name**, edited right there (Enter saves).
- **Possess** / **Let go**.
- **In the home**: turn it off to send an NPC away for a while. Nobody sees it, and it keeps its look and place until you bring it back.
- **Look**: Copy my look, or dress it in one of your Glamourer designs.
- **Place**: Bring to me, or Save its spot (sitting or emoting if it is: an NPC left in an emote stays in it, and Save its spot remembers that).
- **Remove NPC**, with a question first.

**Edit NPCs in Glamourer.** NPCs now appear in Glamourer's Actors tab. Change one there, then press **Save Glamourer edits** in Haven to keep it.

**Glamourer works on NPCs.** Glamourer only recognises a character with a first and last name, like a player's. An NPC with a full name (like Nora Vale) uses it everywhere. An NPC with one name (like Eevi) shows that name above its head, and Glamourer and the target bar know it as "Eevi Haven". The panel tells you which. New NPCs are called Npc One, Npc Two and so on.

**Fixes:**
- The game crashed on a Penumbra redraw after renaming an NPC a few times. Each rename removed the NPC's character and made a new one, which can leave other plugins holding the old one. Haven now keeps an NPC's character for the whole visit: renaming, a new look, sending it away and removing it all reuse it. NPCs also no longer use the character slots that GPose and cutscenes use.
- Renaming an NPC updates the name above its head.
- NPCs were removed and made again whenever a furnishing was placed or a lamp clicked (anything still loading). They now stay put.
- An NPC left sitting or emoting stays that way on your friends' screens too, and friends who arrive later see it where it was left.
- A plugin hook was left behind when Haven updated or unloaded.

Needs relay 0.35.0 (housing protocol 31). Older Haven versions can't join gatherings on it, so everyone should update.
