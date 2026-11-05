# Haven 0.58.6: steadier minigame music

**Safer music changes in minigames.** A friend's game froze in the Crystal Odyssey and closed with the game's "Could not locate data files" error (c0500). The likely cause is Haven cancelling a song while the game was still loading it, which the Odyssey can do when you move between screens quickly. Haven now lets a song finish loading before switching to the next one, usually well under a second. This covers the Odyssey, Triple Triad and the arcade machines.

**Music changes in the log.** Each minigame song change is now written to Dalamud's log, so if a freeze like this happens again the log shows what was playing just before.

This update needs no relay change: everyone on 0.57.x and 0.58.x can play with you.
