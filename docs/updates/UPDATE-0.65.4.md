# Haven 0.65.4: ready for game updates

**After a game update, Haven pauses what it can't vouch for.** Haven now knows which version of the game it was last checked on. On a newer one, the few features that touch game memory Haven located itself pause until Haven is updated:
- the glow of lamps and other furnishings (it turns itself back on once Haven sees it still works);
- the building camera passing through walls (the camera itself still works);
- unowned emotes in the emote window;
- the Gold Saucer machines;
- `/facecamera` in gatherings.

You get one notification when this happens. Settings → Advanced → **After a game update** lists what's paused, and can turn them on anyway for that game version.

**`/haven selfcheck`** says whether Haven found everything it needs in the current game, which features are affected if not, and whether homes can be entered. The same lines are in the log when Haven starts.

**Fixed:** one of the quest checks Haven uses outside housing was hooked into the wrong game function. It had no visible effect in homes.

Client only: the relay is unchanged.
