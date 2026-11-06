# Haven 0.67.1: effects and sounds, tidied

Your first look at 0.67.0's effects and sounds found the Sounds tab off the edge of the drawer, names that were badly wrong (a 66-metre blaze called "Campfire flame card"), effects that showed nothing, and others far too big or small for a room. All of that is fixed:

**The drawer fits.** Catalogue now has three sources, Furnishings, Building and World. World has its own row with **Pieces**, **Effects** and **Sounds**, so everything fits the panel.

**Effects are measured by what they actually draw.** Before, an effect's size came from its file, which is often wrong. Now each one is measured from what shows.
- About 90 that draw nothing you could see (heat haze, invisible lights) are gone.
- Over 300 more than 150 m across are gone, along with a few too tiny to use even at 10×.
- About 1,050 remain.
- Each effect now sits centred on its marker with its bottom on the floor. Before, many appeared far above or beside where you placed them, because the game draws them away from their file's origin.
- An effect too big for a room starts small enough to see whole, about 6 m across. Scale it up in the inspector to get its full size back.
- The tooltip and inspector say how big an effect is ("About 66 m across at full size").
- A new filter, **Fits a room (under 10 m)**, lists only the effects that work indoors at full size.
- An effect or sound you placed with 0.67.0 that's no longer in the list stays in your home as just its marker, unseen and silent. That's an effect that showed nothing, or was too big or too small. Choose its marker to delete it.

**Better names.** Every effect was renamed knowing its size and the words in its file name, not just its picture. So a fire is called a fire, and a 66-metre one is "Huge scattered flames". Uncertain names had a second look. They are still guesses from a rough picture, and about a third are marked as such in their tooltip ("Its name is a guess: place it to see what it is").

**Sounds.** Some sounds had names that said little, such as "Machine hum" or "Magical hum". Where the game places them beside something telling, they're now named after it: "Engine hum", "Brazier fire", "Aetheryte hum", "Humming crystal", "Spinning propeller". A few district names with leftover code words read properly now, and one sound that plays practically silent is gone. Names that are still vague are best judged with the play button.

Effects and sounds can still only be placed in **Just me** visits until Haven's server is updated.

This release changes Haven only; the relay is unchanged.
