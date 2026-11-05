# Haven 0.60.2: World IX's music, and a round of safety fixes

**The Crystal Odyssey works on free trial accounts.** A third "Could not locate data files" (c0500) exit in World IX showed what these crashes have in common: World IX's theme and its fanfare were the only Odyssey music stored with Dawntrail's data, and a free trial (which runs through Shadowbringers) doesn't download that data at all. World IX now plays Festival of the Hunt (Endwalker) on its screens and the FFIX fanfare from its orchestrion roll, and World X's boss plays Locus (The Primals) from its roll too: every Odyssey song now comes from data every install has. Haven also checks that a minigame song is in your game's files before playing it, and stops asking for one that won't load.

**The Crystal Odyssey.** A run can no longer get stuck on the Deck screen without enough cards for a legal deck (after a trade, an upgrade or dropping the Royal Charter): the binder gets back the weakest cards it lacks. A won duel counts even if you leave the home while "Victory!" shows. A Smoke Bomb waits until no other item is used on the duel. A run you replace is recorded as abandoned, and a save that can't be read is set aside instead of breaking the Odyssey tab.

**Safer gatherings.** Other players can no longer send things that crash or stall your game: unknown minions or arcade animations, or one message listing every switch in the home. Nobody can take an NPC someone else is playing, or force a Triple Triad rematch with made-up cards.

**Safer custom content.** Sounds, effects, textures, prop gear and songs that come with a home are checked more thoroughly before your game reads them. Big animations are prepared in the background, and at most three downloads run at once.

**Steadier.** One failure no longer stops Haven's other per-frame work, its shutdown, or the reset of your pose after leaving a home. Picture uploads try again after any failure. A song or animation that won't build is reported once instead of retried every frame.

This update comes with relay 0.41.2, but nothing changes in how they talk: everyone on 0.60.x can still play together.
