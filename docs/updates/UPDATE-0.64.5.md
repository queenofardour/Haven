# Haven 0.64.5: no real-world ambience inside a home, third try

**Inside a home, the game's own environment loops are muted.** 0.64.4 muted the sounds playing as you entered, but Kugane's wind and gulls came back inside under another sound. Haven now mutes any of the game's environment loops (town and field backgrounds, weather spots) while you're in a home, whichever zone or time of day they belong to, and gives them their volume back when you return. The home's music, its furnishings' sounds and the weather heard outside aren't touched.

Five seconds after you enter, Haven also writes what's playing in the home to its log, so if a real-world sound still slips through, the log names it.

Client only: the relay is unchanged.
