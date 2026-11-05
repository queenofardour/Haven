# Haven 0.60.3: no more crashes from game data a free trial doesn't have

**Free trials are safe in every Haven home.** A free trial (and any account without an expansion) doesn't download that expansion's game data, and the game closes when asked for a missing file. A friend on a trial crashed the moment they entered The For'ard Cabins. Haven now checks your game's own files before loading a home:

- **Joining** a friend's home in a place your game doesn't have shows a red message naming the expansion it needs, and leaves the gathering instead of loading it. Your game stays open.
- **Entering or hosting** such a home is refused the same way. You can still make, import and keep it.
- **New home** shows those inns as "Needs Endwalker" or "Needs Dawntrail", and doesn't let you pick them.

On a free trial today that's two inns: Andron (Endwalker) and The For'ard Cabins (Dawntrail). Every house, apartment and the other inns, the Pendants included, work as before. Players with every expansion see no change.

**For troubleshooting:** Haven's log now says when it loads which expansions' data your game has.

This update needs no relay change: everyone on 0.60.x can play together.
