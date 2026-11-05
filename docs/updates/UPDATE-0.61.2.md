# Haven 0.61.2: bigger animations, lighting that saves, and Odyssey fixes

**Animations and custom furniture can be as large as your allowance.** A package used to be capped at 25 MB, so larger animation mods couldn't be imported. Now a package can use your whole 250 MB allowance. Large ones go up to Haven's server in 16 MB pieces, which the server puts back together and checks before keeping. A piece that fails is tried again; if the upload is given up, the space it was counted against comes back to you. Friends need 0.61.2 to download a package over 25 MB.

**Animation mods with slightly malformed sounds import.** Some mods' sound files say their audio is a little longer than the file really is (for example [BDG] Prone-Bone). The game plays them anyway, but Haven refused them. Haven now trims such a sound to what the file holds, and the import goes through.

**Lighting set in a gathering saves.** In a home made from a MakePlace layout, changing the room's brightness while hosting kept showing Saving… and never saved, so the old lighting came back when you returned. Any setting going from "not set" to a value was affected. It now saves like any other change. If the relay ever refuses your changes again, Haven notes why in its log and tries again more slowly instead of every second and a half.

**The Crystal Odyssey:**
- **Sell Items at the shop.** Click an Item you hold to sell it for half its shop price. You can now free a slot when you hold three.
- **The rule box shows Roulette's picks.** The box has four lines, and a rule Roulette rolled could be the one cut off. Rolled rules now come first, and with more than four the last line says how many more there are (hover it for the list). Hovering a rule tells you if Roulette rolled it.
- **Roulette never rolls Reverse in the Odyssey.** Reverse was meant to be left out of the random rules, but Roulette could still pick it, which could end in several rounds of Sudden Death. Bosses whose own rule is Reverse keep it.

This update comes with relay 0.42.2. Haven 0.60.x and 0.61.x keep working with it; packages over 25 MB and the lighting fix need 0.61.2.
