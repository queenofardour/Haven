# Haven 0.64.6: no outdoor ambience inside a home, found

**The wind and gulls inside a home came from the home itself.** Every housing interior in the game names its district's outdoor sound set (a Shirogane cottage names the Shirogane coast, a Mist house the Mist coast), and in a Haven home those played as if you stood outside, whichever zone you came from. 0.64.5 muted them, but the game drives their volume below the level Haven can set, so they were still heard. Haven now stops them while you're in a home, along with anything of the real zone you left that's still playing. The home's music, its furnishings' sounds and the weather heard outside aren't touched, and the real zone's sounds come back when you leave.

Haven logs what it stops, and what's playing five seconds after you enter. If the game keeps starting a sound again, Haven leaves it alone after a few tries and says so.

Client only: the relay is unchanged.
