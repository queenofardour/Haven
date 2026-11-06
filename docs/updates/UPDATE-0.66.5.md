# Haven 0.66.5: world effects, and Haven's own sounds (experimental)

**World pieces bring their effects.** A World catalogue set ("(set)" pieces) now plays its own always-on effects: a brazier's flames, a fountain's spray, sparkles. 807 sets have them. In Furnish → World a small sparkle in a tile's corner marks them (beside the bulb for light), and the **Has effects** filter lists only those. They show with Haven's own effects, the default; with "Use Stagehand's effects instead" on, they don't.

**Haven can now play furnishing sounds itself**, without Stagehand: fountains, fires, chimes, clicks, instruments and an arcade machine's punch. It's the last of Stagehand's furnishing jobs, and it's off unless you turn it on: Settings → Advanced → Sounds → **Play furnishing sounds with Haven (experimental)**. With it on, the same sounds play at the same places, volumes and times as before. Turning it off goes straight back. If a sound is different with it on, please say which furnishing.

**Fixed:** the weather heard outside a home and the district's own ambience held on to a sound channel each time they changed, so over a long visit the game could run short of them. They now give each channel back.

The World list is rebuilt once to learn which sets have effects, which takes a few seconds the first time you open it.

This release changes Haven only; the relay is unchanged.
