# Haven 0.67.0: the world's effects and sounds, and World pieces that showed nothing

**Place the game's own effects and ambient sounds.** The Furnish drawer has two new sources beside World: **Effects** (about 1,150 continuous effects from the game's zones: waterfalls, fires, smoke, steam, sparkles, mist, falling leaves) and **Sounds** (about 740 environment sounds: rivers, wind, fires, machinery, crowds, and each district's day and night ambience, with its weather versions). Each has categories, search (a name, a zone or a file) and favourites.
- Effects show a thumbnail of the effect itself.
- Every sound tile has a **▶** button: press it to hear the sound for a few seconds before placing it, and again to stop.
- Effects and sounds have no surface to click, so while you decorate each shows a small marker (a sparkle or a note) where it is. Click the marker to choose it, then move, turn or delete it like anything else.
- A sound has a volume slider in the inspector. World sets with sounds of their own get one too.
- Only effects and sounds your game has the files for are offered, so a free trial isn't offered Endwalker or Dawntrail ones.
- They need Haven's own effects and sounds (the default; not "Use Stagehand's … instead").

**For now, effects and sounds can be placed only in Just me visits.** Haven's server checks every shared home, and it doesn't know these pieces yet. Hosting a home that has them says so; visit it Just me, or take them out to host it. They'll work in gatherings once the server is updated.

**World pieces that showed nothing.** Two causes, both fixed:
- **Invisible pieces hidden.** Some of the game's world models are invisible walls and placeholders that the game never draws, though their pictures showed them as plain coloured shapes ("Yellow wedge", "Tan wedge", "Yellow panel"). The 206 such pieces are no longer listed, and pictures no longer show the invisible parts of other pieces.
- **Pieces that sat out of sight.** Many world models describe themselves as far bigger than they are (a weather vane modelled 14 m up its tower, a frame "40 m long"), so a placed one stood high above the floor or off to the side. About 1,500 such pieces now rest on the floor where you place them, with a collision box their real size. Any you've already placed move to where they now rest.

World pictures are drawn again once, so the World list fills in a little more slowly the first time.

This release changes Haven only; the relay is unchanged.
