# Haven 0.53.0: NPCs on pose balls

**NPCs can use pose balls.** While you control an NPC, it takes balls the way you do:
- Click a furnishing with animations to open its sets, and click a ball. The NPC takes the ball, not you.
- Once every ball of the set is taken, the NPC plays its animation. A ball you or a friend takes counts, and so does another NPC's.
- The pose panel then adjusts the NPC: position, turn, tilt, lean and size, Save fit, Reset, Restart and Stand up.
- **Let go and the NPC stays on its ball**, so you can take the other ball yourself, for example to cuddle with your NPC.
- Anyone else on the set can adjust the NPC from their own panel, as they would a friend.
- Friends in the gathering see the NPC on its ball, including friends who arrive later.
- When the NPC stands up, or the balls are hidden, it goes back to where it stood.

**Rest on surface works for NPCs.** An NPC whose body would sink into the furniture is raised onto it, as you are, unless a saved fit already places it.

**NPCs keep their ball.** Press **Save its spot** while the NPC is on a ball, and it takes that ball every time it's in the home, for everyone.
- Leaving the ball, or hiding the balls, lets it go for the rest of the visit. It's back on the ball next time.
- To forget the ball, use **Bring to me**, or Save its spot again while it isn't on a ball.
- If you remove the furniture or its animation set, the NPC simply stands at its spot instead.

**NPCs wear their animation's props.** If an animation uses a prop (worn as gear), an NPC playing it wears it too. Haven asks you first, as it does for your own ball. Each PC in the gathering dresses its own copy of the NPC.

**NPC colours in Glamourer.** An NPC customised before 0.52.5 can keep old Advanced Customization colours. These override its ordinary skin, hair, eye and lip colours, so changing those did nothing.
- The NPCs panel now shows **Reset advanced colours** whenever an NPC's look sets any. Press it, and the ordinary colours show and can be changed in Glamourer again.
- Haven's log now names the advanced colours an NPC keeps.

**Rooms keep their finishes.** A friend's home once came up with the default wallpaper, flooring and lights, and a brightness that wouldn't change, until they left and came back. Haven now waits for the room to finish loading before setting its brightness. If the game still hasn't applied the finishes after 10 seconds, Haven loads the room again once by itself. Haven's log now records the finishes it asked for and how long they took (`[INTERIOR]`), so tell me if it happens again.

**The Crystal Odyssey: change your mind.**
- After choosing an Enhancement (a reward, the starting gift, the shop or Cid's Workshop), the card picker has a **Back** button, and Escape works too. Nothing changes until you press Apply. In the shop and the workshop you get your gil back.
- The same works when a new Materia arrives with your slots full.
- After a duel, no reward is highlighted until you point at one, and Enter no longer takes the first one for you. The same goes for opponents and event answers.
- An event's window now fits its text, without a gap above the answers.
- A Potion used on a card you then take out of the deck goes back in your bag. It used to be lost.
- **Combos.** A card a combo carries on from now attacks with its attacking numbers. A Reckless card gets its +2 and a Guardian its -1, as when it is played, and so does your Attack or Defend Materia. Before, Same, Plus and Avalanche chains used the card's defending numbers, so those perks and Materia worked the wrong way round.

**Signing in is safer.** Discord now asks you to approve Haven every time, and Haven shows a short code while it waits. The browser page shows the same code: press **Yes, sign me in** only if they match. Someone else's sign-in link can no longer sign your Discord account in for them.

**Smaller fixes.**
- **Updating during a visit.** Updating or turning off Haven inside a home always takes you back where you stood, even if something else fails while Haven shuts down.
- **Custom content downloads.** If a picture, song, furnishing or animation can't be downloaded, Haven stops trying and tells you what it was and why, with **Try again** and **Give up**. It used to try again every two minutes for the whole visit. A download that stalls is still retried, oversized ones are refused, and textures and songs are checked more strictly.
- **Custom furniture.** Two pieces made from the same import under different names no longer break each other: deleting one leaves the other drawn. A piece whose files clash with another one already shown shows as its original furnishing instead of replacing that one's look.
- **NPCs.** Only the person controlling an NPC can make it speak or emote.
- **Performance.** Homes with custom animations use less time each frame, and so do furnished homes in general: each piece's model data is worked out once a frame, the furniture under the pointer is found again only when the pointer or camera moves, and homes without NPCs no longer scan the characters around you.

**Wording.**
- The Visit panel no longer tells owners to publish: your edits reach everyone by themselves.
- Proposals read "3 furnishings".
- The Room panel no longer shows technical status lines.
- The inspector labels the dye.
- Text about uploading your own content no longer mentions picture sharing.

This update comes with relay 0.36.0 (housing protocol 32). Older Haven versions can't join gatherings on the new relay, so everyone needs to update. The relay also limits how fast one person's movement and appearance updates are passed on, so one guest can't slow a gathering down for everyone.
