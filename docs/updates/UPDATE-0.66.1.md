# Haven 0.66.1: Haven's lights by default

**Haven now draws furnishing lights itself by default.** After testing showed rooms look the same either way, Haven's own lights replace Stagehand's for lamps, chandeliers, building pieces and the blank canvas's fill light. Stagehand still draws imported Stagehand objects.

If a room's lighting looks wrong, Settings → Advanced → Lights → **Use Stagehand's lights instead** goes back to the old way, so you can compare. Please say which room and which lights if you need it.

**World pieces that give light are marked.** In Furnish → Catalogue → World, a small bulb in a tile's corner means the piece brings a light of its own, and the new **Gives light** filter lists only those. Many lanterns in the game's world only glow: their light is placed beside them in the zone, not in the lantern itself, so they don't light a room. The piece's inspector says whether it brings its own light. The World list is rebuilt once to learn this, which takes a few seconds the first time you open it.

This release changes Haven only; the relay is unchanged.
