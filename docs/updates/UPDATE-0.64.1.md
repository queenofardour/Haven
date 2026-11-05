# Haven 0.64.1: the weather heard outside, fixed

**Heard outside no longer drones.** In 0.64.0, choosing rain (or any weather) under Room → Atmosphere → Heard outside played a constant engine-like drone instead, and the home's music could no longer be heard over it. The game's outdoor weather sounds are four-channel surround, which the music player Haven plays them on can't handle. Haven now turns each one into an ordinary stereo track the first time it's needed (a moment's work, kept on your PC), and plays that. Penumbra is needed for it, as for custom songs.

Also:
- The music and the weather outside no longer get in each other's way when both start at once (on entering a home, say): the music could be started twice.
- Dragging the time of day across 6 o'clock no longer restarts the weather sound again and again; the day's or night's sound changes once the time has settled for a second.
- A weather sound that keeps stopping is left off for a few minutes, rather than started over and over.

Client only: the relay is unchanged, and 0.64.0 and 0.64.1 can share a gathering.
