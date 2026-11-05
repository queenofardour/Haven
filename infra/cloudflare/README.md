# Haven's hosted relay

This folder is the Cloudflare Worker in front of Haven's relay. It runs at `haven-relay.eevicat.workers.dev`.

| File | What it does |
| --- | --- |
| `src/index.ts` | Routes requests: relay connections to the relay container, and sign-in, content and invitations to their handlers |
| `src/accounts.ts` | Discord sign-in, one-time access codes, which characters belong to which account, and the check of each relay connection's account |
| `src/relay-proxy.ts` | Passes a connection's messages to and from the relay container, in order, taking checkpoints |
| `src/content.ts`, `src/content-budget.ts` | Storage for players' own pictures, songs, furniture and animations, with per-character allowances and a hard overall budget |
| `src/invites.ts` | Gathering invitations between friends |
| `src/recovery-store.ts`, `src/durable-revisions.ts` | Checkpoints that let a gathering survive a relay restart |
| `src/downloads.ts` | The download service for the private testing channel |
| `test/` | The Worker's tests (`npm test`) |

The relay itself is the .NET project in `src/Haven.Relay`, run in a Cloudflare container built from the repository's `Dockerfile`.

## Running your own

The relay runs anywhere .NET 10 does ([scripts/start-relay.ps1](../../scripts/start-relay.ps1)). Point Haven at it under Settings → Connection. A self-hosted relay without the account variables accepts unsigned connections.

To run this Worker yourself you need a Cloudflare account on Workers Paid, a Discord application for sign-in, and the secrets the code reads (`HAVEN_RECOVERY_KEY`, `HAVEN_CONTENT_KEY`, `HAVEN_ADMIN_KEY`, `DISCORD_CLIENT_SECRET`). Change the names and ids in `wrangler.jsonc` to your own.
