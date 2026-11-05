// Gathering invitations: a host's Haven tells the Worker which nearby friends to invite, and
// each friend's Haven asks the Worker, now and then, whether anyone invited them. Kept in the accounts Durable Object
// (HavenAccounts), so one request checks the key and character (AccountLedger.authorize) and reads or writes the
// invitations together: an invitation reaches only the character it names, and only that character's account reads it.
//
// The relay container is never touched (it sleeps when idle; polling it would keep it awake).
//
// Cost bounds, charged before anything is written: a day's invitation writes and polls across everyone
// (inviteLimits.writesPerDay, pollsPerDay), each account's invitations a day, one send and one poll per character every
// few seconds, and the Worker's per-address rate limit (index.ts). Records are small and bounded (8 invitations per
// invitee, 32 invitees per gathering) and expire after 30 minutes; a sweep at most once an hour deletes expired ones.
import type { LedgerStore } from "./content-budget";
import type { Authorized } from "./accounts";

// As accounts.ts (type imports only between modules, so the tests run them straight from source).
const isContentId = (value: unknown): value is string => typeof value === "string" && /^[1-9][0-9]{0,19}$/.test(value) && BigInt(value) < 2n ** 64n;
const sha256 = async (text: string) => btoa(String.fromCharCode(...new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text)))))
  .replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");

export const inviteLimits = {
  inviteMs: 30 * 60_000,       // an invitation lasts this long after the host last sent it
  perRequest: 16,              // invitees in one send
  perInvitee: 8,               // invitations one character can hold at once (newest kept)
  perGathering: 32,            // invitees one gathering can have out at once
  hostEveryMs: 4_000,          // one send or withdraw per host character this often
  pollEveryMs: 8_000,          // one poll per character this often (Haven asks every 20 s)
  accountInvitesPerDay: 300,
  writesPerDay: 20_000,        // invitation records written a day, everyone together
  pollsPerDay: 200_000,        // polls a day, everyone together
  sweepMs: 3_600_000,
  pollFlush: 100,              // the poll count is kept in memory and stored every this many polls
};

export interface Invitation { from: string; name: string; home: string; code: string; gathering: string; expires: number }
interface Hosting { gathering: string; invitees: string[]; expires: number }
export interface SendRequest { code?: unknown; home?: unknown; gathering?: unknown; invitees?: unknown }
export type InviteRefusal = "signed_out" | "banned" | "taken" | "too_many" | "slow" | "busy" | "invalid" | "limit";
export type PollResult = { invites: Invitation[] } | { error: InviteRefusal };
export type SendResult = { sent: number } | { error: InviteRefusal };

const day = (now: number) => new Date(now).toISOString().slice(0, 10);
const clean = (text: unknown, max: number) => typeof text === "string" ? text.replace(/[\u0000-\u001f\u007f]/g, "").trim().slice(0, max) : "";
const GATHERING = /^[0-9a-f]{16,32}$/;

export interface Authorizer { authorize(keyHash: string, contentId: string, name: string, now: number): Promise<Authorized> }

export class InviteBook {
  private readonly store: LedgerStore;
  private readonly accounts: Authorizer;
  // Per-character throttles, in memory (lost when the object is evicted, which only lets one more request through).
  private readonly lastPoll = new Map<string, number>();
  private readonly lastSend = new Map<string, number>();
  private polls?: { day: string; count: number; stored: number };
  constructor(store: LedgerStore, accounts: Authorizer) { this.store = store; this.accounts = accounts; }

  private async one<T>(key: string) { return (await this.store.get<T>([key])).get(key); }
  private static throttle(map: Map<string, number>, key: string, every: number, now: number) {
    const last = map.get(key);
    if (last !== undefined && now - last < every) return false;
    if (map.size > 20_000) map.clear();
    map.set(key, now);
    return true;
  }

  private async who(keyHash: string, contentId: string, name: string, now: number) {
    if (!isContentId(contentId)) return { error: "invalid" as const };
    return await this.accounts.authorize(keyHash, contentId, name, now);
  }

  /** The invitations waiting for this character (unexpired). */
  async poll(keyHash: string, contentId: string, name: string, now: number): Promise<PollResult> {
    // Signed in first, so nobody can hold a character's polls back by asking as it.
    const who = await this.who(keyHash, contentId, name, now);
    if ("error" in who) return { error: who.error };
    if (!InviteBook.throttle(this.lastPoll, contentId, inviteLimits.pollEveryMs, now)) return { error: "slow" };
    const today = day(now);
    if (this.polls?.day !== today) {
      const stored = await this.one<number>(`invites:polls:${today}`) ?? 0;
      this.polls = { day: today, count: stored, stored };
    }
    if (this.polls.count >= inviteLimits.pollsPerDay) return { error: "busy" };
    this.polls.count++;
    if (this.polls.count - this.polls.stored >= inviteLimits.pollFlush) { this.polls.stored = this.polls.count; await this.store.put({ [`invites:polls:${today}`]: this.polls.count }); }
    const held = await this.one<Invitation[]>(`invite:${contentId}`) ?? [];
    return { invites: held.filter(i => i.expires > now) };
  }

  /** A host invites characters to its gathering (or refreshes their invitations). */
  async send(keyHash: string, contentId: string, name: string, body: SendRequest, now: number): Promise<SendResult> {
    const code = clean(body.code, 128), home = clean(body.home, 60), gathering = typeof body.gathering === "string" ? body.gathering : "";
    const invitees = Array.isArray(body.invitees) ? [...new Set(body.invitees.filter((i): i is string => typeof i === "string"))] : null;
    if (code.length < 8 || code !== body.code || !GATHERING.test(gathering) || !invitees || invitees.length === 0 || invitees.length > inviteLimits.perRequest
      || !invitees.every(isContentId) || invitees.includes(contentId)) return { error: "invalid" };
    const who = await this.who(keyHash, contentId, name, now);
    if ("error" in who) return { error: who.error };
    if (!InviteBook.throttle(this.lastSend, contentId, inviteLimits.hostEveryMs, now)) return { error: "slow" };
    const today = day(now);
    const hostingKey = `hosting:${contentId}`, writesKey = `invites:writes:${today}`, accountKey = `invites:account:${who.account}:${today}`;
    const found = await this.store.get<unknown>([hostingKey, writesKey, accountKey, ...invitees.map(i => `invite:${i}`)]);
    let hosting = found.get(hostingKey) as Hosting | undefined;
    const updates: Record<string, unknown> = {};
    // A new gathering from the same host: the old one's invitations go first.
    if (hosting && (hosting.gathering !== gathering || hosting.expires <= now)) {
      Object.assign(updates, await this.removing(contentId, hosting.invitees.filter(i => !invitees.includes(i)), now));
      hosting = undefined;
    }
    const expires = now + inviteLimits.inviteMs;
    const out = new Set((hosting?.invitees ?? []).filter(i => !invitees.includes(i)));
    let sent = 0;
    for (const invitee of invitees) {
      const held = (found.get(`invite:${invitee}`) as Invitation[] | undefined ?? []).filter(i => i.expires > now);
      const mine = held.find(i => i.from === contentId);
      out.add(invitee);
      // Still fresh: nothing to write (a host refreshes every ten minutes; this keeps refreshes cheap).
      if (mine && mine.gathering === gathering && mine.code === code && mine.expires - now > inviteLimits.inviteMs / 2) continue;
      const kept = held.filter(i => i.from !== contentId).slice(-(inviteLimits.perInvitee - 1));
      updates[`invite:${invitee}`] = [...kept, { from: contentId, name: who.name || clean(name, 40), home, code, gathering, expires } satisfies Invitation];
      sent++;
    }
    if (out.size > inviteLimits.perGathering) return { error: "limit" };
    const writes = (found.get(writesKey) as number | undefined) ?? 0, account = (found.get(accountKey) as number | undefined) ?? 0;
    const count = Object.keys(updates).length + 1;
    if (writes + count > inviteLimits.writesPerDay) return { error: "busy" };
    if (account + sent > inviteLimits.accountInvitesPerDay) return { error: "limit" };
    updates[hostingKey] = { gathering, invitees: [...out], expires } satisfies Hosting;
    updates[writesKey] = writes + count;
    updates[accountKey] = account + sent;
    await this.store.put(updates);
    await this.sweep(now);
    return { sent };
  }

  /** A host's gathering ended (no invitees), or it takes back some invitations. */
  async withdraw(keyHash: string, contentId: string, name: string, invitees: unknown, now: number): Promise<SendResult> {
    const some = invitees === undefined || invitees === null ? null : Array.isArray(invitees) && invitees.length <= inviteLimits.perGathering && invitees.every(i => typeof i === "string" && isContentId(i)) ? invitees as string[] : undefined;
    if (some === undefined) return { error: "invalid" };
    const who = await this.who(keyHash, contentId, name, now);
    if ("error" in who) return { error: who.error };
    if (!InviteBook.throttle(this.lastSend, "w" + contentId, 1_000, now)) return { error: "slow" };
    const hostingKey = `hosting:${contentId}`;
    const hosting = await this.one<Hosting>(hostingKey);
    if (!hosting) return { sent: 0 };
    // Only invitations this host has out: a withdraw writes nothing that a send didn't pay for first.
    const targets = some ? hosting.invitees.filter(i => some.includes(i)) : hosting.invitees;
    const updates = await this.removing(contentId, targets, now);
    const left = hosting.invitees.filter(i => !targets.includes(i));
    if (left.length === 0) await this.store.delete([hostingKey]);
    else updates[hostingKey] = { ...hosting, invitees: left };
    if (Object.keys(updates).length) await this.store.put(updates);
    return { sent: 0 };
  }

  // The invitee records with this host's invitations taken out (deleted outright when nothing is left: as undefined).
  private async removing(host: string, invitees: string[], now: number) {
    const updates: Record<string, unknown> = {};
    if (invitees.length === 0) return updates;
    const found = await this.store.get<Invitation[]>(invitees.map(i => `invite:${i}`));
    const empty: string[] = [];
    for (const [key, held] of found) {
      const kept = held.filter(i => i.from !== host && i.expires > now);
      if (kept.length === held.length) continue;
      if (kept.length) updates[key] = kept; else empty.push(key);
    }
    for (let i = 0; i < empty.length; i += 128) await this.store.delete(empty.slice(i, i + 128));
    return updates;
  }

  // At most once an hour: invitation and hosting records whose every invitation has expired, and old day counters.
  private async sweep(now: number) {
    const last = await this.one<number>("swept:invites");
    if (last !== undefined && now - last < inviteLimits.sweepMs) return;
    await this.store.put({ "swept:invites": now });
    const old: string[] = [];
    for (const [key, held] of await this.store.list<Invitation[]>({ prefix: "invite:", limit: 1000 })) if (!held.some(i => i.expires > now)) old.push(key);
    for (const [key, hosting] of await this.store.list<Hosting>({ prefix: "hosting:", limit: 1000 })) if (hosting.expires <= now) old.push(key);
    const today = day(now), yesterday = day(now - 86_400_000);
    for (const [key] of await this.store.list<number>({ prefix: "invites:", limit: 1000 }))
      if (!key.includes(today) && !key.includes(yesterday)) old.push(key);
    for (let i = 0; i < old.length; i += 128) await this.store.delete(old.slice(i, i + 128));
  }
}

export interface InvitesApi {
  invitePoll(keyHash: string, contentId: string, name: string, now: number): Promise<PollResult>;
  inviteSend(keyHash: string, contentId: string, name: string, body: SendRequest, now: number): Promise<SendResult>;
  inviteWithdraw(keyHash: string, contentId: string, name: string, invitees: unknown, now: number): Promise<SendResult>;
}

const json = (value: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json", "Cache-Control": "no-store", ...headers } });
const statusOf: Record<InviteRefusal, number> = { signed_out: 403, banned: 403, taken: 403, too_many: 403, slow: 429, busy: 429, invalid: 400, limit: 429 };

/** /invites (GET: yours), /invites/send and /invites/withdraw (POST, as the host): Haven only, never a browser. */
export async function handleInvites(request: Request, env: { invites?: InvitesApi; rate?: RateLimit }, address: string, now = Date.now()): Promise<Response> {
  const path = new URL(request.url).pathname;
  if (!env.invites) return new Response("Not found", { status: 404 });
  if (request.headers.has("Origin") || request.headers.has("Sec-Fetch-Site")) return new Response("Forbidden", { status: 403 });
  if (env.rate && !(await env.rate.limit({ key: "invites:" + address })).success) return json({ error: "slow" }, 429, { "Retry-After": "60" });
  const auth = request.headers.get("Authorization"), character = request.headers.get("X-Haven-Character");
  if (!auth?.startsWith("Bearer ") || auth.length > 200 || !isContentId(character)) return json({ error: "sign_in" }, 401);
  let name = "";
  try { name = decodeURIComponent(request.headers.get("X-Haven-Character-Name") ?? "").replace(/[\u0000-\u001f]/g, "").slice(0, 40); } catch { }
  const keyHash = await sha256(auth.slice(7));
  let result: PollResult | SendResult;
  if (path === "/invites" && request.method === "GET") result = await env.invites.invitePoll(keyHash, character, name, now);
  else if ((path === "/invites/send" || path === "/invites/withdraw") && request.method === "POST") {
    const length = Number(request.headers.get("Content-Length") ?? "0");
    if (!(length > 0 && length <= 4096)) return json({ error: "invalid" }, 400);
    let body: SendRequest & { invitees?: unknown };
    try { const text = await request.text(); if (text.length > 4096) return json({ error: "invalid" }, 400); body = JSON.parse(text); } catch { return json({ error: "invalid" }, 400); }
    if (!body || typeof body !== "object") return json({ error: "invalid" }, 400);
    result = path === "/invites/send" ? await env.invites.inviteSend(keyHash, character, name, body, now)
      : await env.invites.inviteWithdraw(keyHash, character, name, body.invitees, now);
  }
  else return new Response("Not found", { status: 404 });
  if ("error" in result) return json(result, statusOf[result.error], result.error === "busy" ? { "Retry-After": "600" } : result.error === "slow" ? { "Retry-After": "10" } : {});
  return json(result);
}
