# Haven 0.58.7: friends show when they join, sharing checks your look

**Friends show when they join, take two.** Haven could cut short the redraw your sync plugin uses to put a friend's look on them as they appear, leaving them invisible (and you, on their screen) until someone redrew by hand. Haven no longer gets in its way, and it redraws each person a few seconds after they join. If someone still doesn't show, type `/haven peers` and send your log.

**Sharing an NPC's mods checks you really wear its look.** When you send an NPC's mods to your guests, you wear the NPC's look for a few seconds so your sync plugin can pass its files on. If your own look came back instead, the share used to wait a minute and give your guests nothing. Now Haven checks your race and gear, dresses you again if needed, and otherwise stops the share and tells you why.

While you wear the NPC's look your guests see you faded out; you come back when the share ends.

This update needs no relay change: everyone on 0.57.x and 0.58.x can play with you.
