# Safety and privacy

This page explains what Haven does to your game, what it sends over the internet, and what it keeps. The source for everything described here is in this repository, and each point links to the code that does it.

## Your character and the game world

A Haven home is a client-side scene. When you enter one, Haven loads an interior into your own client only. The game's servers still see you standing where you entered.

- **Packets are held back while you're inside.** Haven stops the game sending its usual zone packets (movement, actions, emotes) and lets only the connection's heartbeat through, so the server never sees you move. Inside a home you can still use tells, free company and linkshell chat, your friend list and party commands; Haven lets those specific requests through as they're made. ([PacketFilterService.cs](../src/Haven/Native/Upstream/Services/PacketFilterService.cs), [SocialRequests.cs](../src/Haven/Native/SocialRequests.cs))
- **The filter opens only once you're back.** When you leave, Haven puts your character back at the exact spot you entered and lifts the filter only after that. If the plugin is unloaded mid-visit, it records where you were and finishes the return when it loads again. ([ZoneLoadService.cs](../src/Haven/Native/Upstream/Services/ZoneLoadService.cs), [ReturnGuard.cs](../src/Haven.Core/ReturnGuard.cs))
- **Haven refuses to start in duties, PvP, duty queues, combat, cutscenes and events**, while changing areas, or while knocked out. The duty finder windows close while you're inside. ([EntryGuard.cs](../src/Haven.Core/EntryGuard.cs))
- **Nothing is placed on the game's servers.** Furnishing never uses the game's housing services: homes exist only on your PC and, while you host, on Haven's relay.
- **Other players** near the spot where you entered are hidden inside your home and shown again when you return.

## What Haven sends, and where

Haven talks to the internet only for the features below. There is no telemetry or analytics.

| When | Where | What |
| --- | --- | --- |
| Once an hour | The repository you installed Haven from | A request for the plugin list, to tell you about updates |
| Signing in | Discord, then Haven's Worker | Discord's `identify` scope: your Discord ID and display name. No email, servers or messages |
| In a gathering | Haven's relay | The home's layout and settings; members' position, animation, emotes and pose; room chat; whether you're typing (never the draft text); music; switch, pose ball and minigame states |
| Inviting friends | Haven's Worker | The character IDs of friends standing near you, so their Haven can offer to join |
| Your own content | Haven's Worker and storage | Pictures, songs, furniture and animation packages you upload, and the guests' downloads of them |

Haven's relay and Worker run on Cloudflare at `haven-relay.eevicat.workers.dev`. The relay keeps a gathering's state in memory while it's open, with a short checkpoint so a gathering survives a relay restart. It keeps the last 30 seconds of room chat in memory so members who lose connection briefly don't miss lines.

## Accounts

Hosting and joining gatherings, and uploading content, need a Haven account. Signing in opens Discord in your browser. The page then shows a short code that must match the one Haven shows, so nobody can trick you into signing in for them. ([accounts.ts](../infra/cloudflare/src/accounts.ts))

The Worker keeps:
- your Discord ID and display name;
- a hashed key for each PC you sign in on (the key itself stays on your PC);
- each character you connect with (its character ID and name), tied to your account so nobody else can use it.

Haven is invite-only for now: a new account needs a one-time access code from the maintainer. The maintainer can ban an account, sign it out on every PC, or release a character tied to the wrong account.

## Custom content

Uploading is optional, and only the home's owner uploads its content. Before your first upload you're asked to accept a short content agreement.

Guests are asked, per home, whether to load a home's custom pictures, songs, furniture and animations. If they say no, they see the game's own items instead. Files are checked before use: images are re-encoded, and models, textures, sounds and animations are validated.

Uploads are stored in Cloudflare R2 and deleted automatically 30 days after they were last used. Deleting an item in Haven deletes it from storage too. Each character has an upload allowance, and the whole service has a hard budget.

## On your PC

Homes, layouts, settings and downloaded content are kept in Haven's Dalamud configuration folder. Which homes belong to which of your characters is recorded under a hash of the character ID, not the ID itself.

## Other plugins

Haven works with Penumbra, Glamourer, Customize+ and sync plugins such as Lightless through their public interfaces. It uses temporary collections, which are gone when you leave, and a few Penumbra mods of its own, all named "Haven …" (an animation's props, an NPC's shared mods). It doesn't change your other mods or settings.

## Reporting a problem

If you find a security problem, please report it privately to the maintainer rather than in a public issue.
