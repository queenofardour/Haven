// Haven accounts: Discord sign-in, and which characters belong to which account.
//
// Character IDs are whatever a client claims, so on their own anyone could act as a nearby player. An account is a
// Discord user. Signing in gives the PC a private key; the first time a character connects with that key it is tied to
// the account, and no other account may use it after that. The relay container has no internet, so the check runs at
// the edge (verifyConnection): it forwards the connection stamped with the verified character (Haven.Core
// AccountStamp), which the relay checks with the key they share. An abuser is a Discord account the operator can ban,
// and the operator can release a character tied to the wrong account.
//
// Sign-in, without the key ever appearing in a URL: the plugin makes a random verifier and opens
// /auth/discord?state=<base64url SHA-256 of it>. Discord sends the browser back to /auth/discord/callback; the Worker
// looks up the Discord user and asks, on its page, whether Haven shows the same short code (signInCode, derived from the
// state; Haven shows it while it waits). Only once the person confirms does the Worker keep a fresh key for that state.
// The plugin then collects it from /auth/claim by presenting the verifier, once.
//
// The confirmation (31 October 2026): anyone can make a state and send someone the /auth/discord link for it. Before,
// a person who had approved Haven on Discord before was sent straight back (prompt=none) and the key went to whoever
// held the verifier. Now Discord asks every time, and the page names the code the person's own Haven must show.
//
// Access codes (5 November 2026): Haven is invite-only. With HAVEN_ACCESS_CODES = "required", a Discord account Haven
// hasn't seen before is asked on the confirmation page for a one-time access code the operator made on the admin page;
// the code is used up by that account, and accounts made before keep signing in without one.
import type { LedgerStore } from "./content-budget";

export const accountLimits = { loginMs: 10 * 60_000, charactersPerAccount: 40, keysPerAccount: 10, stampMs: 60_000 };

export interface AccountKey { hash: string; created: number; used: number }
export interface Account { id: string; name: string; created: number; banned: boolean; keys: AccountKey[]; characters: number }
export interface CharacterRecord { account: string; name: string; bound: number }
interface Login { created: number; key?: string; name?: string; pending?: { discordId: string; name: string; token: string; tries?: number } }
/** A one-time access code: who it was made for (the operator's note), and the account that used it up. */
export interface AccessCode { code: string; note: string; created: number; revoked?: boolean; used?: { account: string; name: string; at: number } }
export type ConfirmOutcome = "ok" | "expired" | "banned" | "code";
// name: the character's bound name, the one it was first tied to the account with (see authorize).
export type Authorized = { account: string; name: string } | { error: "signed_out" | "banned" | "taken" | "too_many" };
export interface AccountSummary { id: string; name: string; created: number; banned: boolean; keys: number; characters: { id: string; name: string; bound: number }[] }

export const accessLimits = { triesPerSignIn: 5, codesPerBatch: 50, noteLength: 60 };

const STATE = /^[A-Za-z0-9_-]{43}$/;
export const isState = (state: string | null): state is string => !!state && STATE.test(state);
export const isContentId = (value: string | null): value is string => !!value && /^[1-9][0-9]{0,19}$/.test(value) && BigInt(value) < 2n ** 64n;

// The short code Haven shows while it waits and the confirmation page names (Haven.Core AccountSignIn.CodeOf):
// SHA-256 of "haven sign-in\n<state>", its first six bytes each picking one of 32 letters and digits that can't be mixed up.
const CODE_LETTERS = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
export async function signInCode(state: string) {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(`haven sign-in\n${state}`)));
  const code = [...digest.slice(0, 6)].map(b => CODE_LETTERS[b % 32]).join("");
  return `${code.slice(0, 3)}-${code.slice(3)}`;
}
// Access codes: twelve of the sign-in code's letters (no 0/O, 1/I), shown as XXXX-XXXX-XXXX; typed with or without
// dashes, spaces or lowercase.
export function newAccessCode() {
  const bytes = crypto.getRandomValues(new Uint8Array(12));
  const letters = [...bytes].map(b => CODE_LETTERS[b % 32]).join("");
  return `${letters.slice(0, 4)}-${letters.slice(4, 8)}-${letters.slice(8)}`;
}
export function canonicalAccessCode(typed: unknown): string | null {
  if (typeof typed !== "string" || typed.length > 40) return null;
  const letters = typed.toUpperCase().replace(/[^A-Z0-9]/g, "");
  if (letters.length !== 12 || [...letters].some(c => !CODE_LETTERS.includes(c))) return null;
  return `${letters.slice(0, 4)}-${letters.slice(4, 8)}-${letters.slice(8)}`;
}
/** Compares a presented secret with the expected one by their digests, so the time taken says nothing about either (as downloads.ts). */
export async function equalSecret(actual: string, expected: string): Promise<boolean> {
  if (!expected || expected.length < 32) return false;
  const digest = async (s: string) => new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s)));
  const [a, b] = await Promise.all([digest(actual), digest(expected)]);
  let difference = 0; for (let i = 0; i < a.length; i++) difference |= a[i] ^ b[i];
  return difference === 0;
}
const sameText = (a: string, b: string) => { if (a.length !== b.length) return false; let d = 0; for (let i = 0; i < a.length; i++) d |= a.charCodeAt(i) ^ b.charCodeAt(i); return d === 0; };

export const base64url = (bytes: Uint8Array) => btoa(String.fromCharCode(...bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
export async function sha256(text: string) { return base64url(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text)))); }
export const newKey = () => base64url(crypto.getRandomValues(new Uint8Array(32)));

/**
 * The stamp Haven.Core's AccountStamp reads: "<contentId>.<expires>.<account>.<base64url name>.<base64url HMAC-SHA256 of
 * verified2\n<id>\n<expires>\n<account>\n<name>>". The relay limits connections by the account and names the character
 * by its bound name, whatever name the client claims.
 */
export async function stamp(key: string, contentId: string, expires: number, account: string, name: string) {
  const hmac = await crypto.subtle.importKey("raw", new TextEncoder().encode(key), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = new Uint8Array(await crypto.subtle.sign("HMAC", hmac, new TextEncoder().encode(`verified2\n${contentId}\n${expires}\n${account}\n${name}`)));
  return `${contentId}.${expires}.${account}.${base64url(new TextEncoder().encode(name))}.${base64url(mac)}`;
}

export class AccountLedger {
  private readonly store: LedgerStore;
  constructor(store: LedgerStore) { this.store = store; }

  private async one<T>(key: string) { return (await this.store.get<T>([key])).get(key); }

  /** A sign-in starts: the state is remembered for ten minutes. */
  async begin(state: string, now: number) {
    if (!isState(state)) return false;
    const login = await this.one<Login>(`login:${state}`);
    if (!login || now - login.created > accountLimits.loginMs) await this.store.put({ [`login:${state}`]: { created: now } satisfies Login });
    await this.sweep(now);
    return true;
  }

  // Sign-ins nobody finished stay in storage after they expire (an unclaimed one even holds a key): at most once an
  // hour, a new sign-in deletes up to 1,000 of them.
  private async sweep(now: number) {
    const last = await this.one<number>("swept:logins");
    if (last !== undefined && now - last < 3_600_000) return;
    await this.store.put({ "swept:logins": now });
    const old = [...(await this.store.list<Login>({ prefix: "login:", limit: 1000 }))].filter(([, l]) => !(now - l.created <= accountLimits.loginMs)).map(([k]) => k);
    // Durable Object storage deletes at most 128 keys a call.
    for (let i = 0; i < old.length; i += 128) await this.store.delete(old.slice(i, i + 128));
  }

  /** Discord confirmed who is signing in: kept with a one-time token until the person confirms the code on the page. */
  async identify(state: string, discordId: string, name: string, now: number): Promise<{ token: string; isNew: boolean } | "expired" | "banned"> {
    const login = await this.one<Login>(`login:${state}`);
    if (!login || now - login.created > accountLimits.loginMs || login.key) return "expired";
    const account = await this.one<Account>(`acct:${discordId}`);
    if (account?.banned) { await this.store.delete([`login:${state}`]); return "banned"; }
    const token = newKey();
    await this.store.put({ [`login:${state}`]: { ...login, pending: { discordId, name, token } } satisfies Login });
    return { token, isNew: !account };
  }

  /**
   * The person confirmed the code: the sign-in Discord identified completes (once, with the page's token). A Discord
   * account Haven hasn't seen before needs an unused access code when codes are required; it's used up by that account.
   * "code": no valid code (the page asks again; after five wrong tries the sign-in is dropped and answers "expired").
   */
  async confirm(state: string, token: string, now: number, accessCode?: string, codesRequired = false): Promise<{ outcome: ConfirmOutcome; name: string }> {
    const login = await this.one<Login>(`login:${state}`);
    const pending = login?.pending;
    if (!login || !pending || typeof token !== "string" || !sameText(token, pending.token)) return { outcome: "expired", name: "" };
    if (codesRequired && !(await this.one<Account>(`acct:${pending.discordId}`))) {
      const code = canonicalAccessCode(accessCode);
      const record = code ? await this.one<AccessCode>(`code:${code}`) : undefined;
      if (!record || record.revoked || record.used) {
        const tries = (pending.tries ?? 0) + 1;
        if (tries >= accessLimits.triesPerSignIn) { await this.store.delete([`login:${state}`]); return { outcome: "expired", name: pending.name }; }
        await this.store.put({ [`login:${state}`]: { ...login, pending: { ...pending, tries } } satisfies Login });
        return { outcome: "code", name: pending.name };
      }
      const outcome = await this.complete(state, pending.discordId, pending.name, now);
      if (outcome === "ok") await this.store.put({ [`code:${code}`]: { ...record, used: { account: pending.discordId, name: pending.name, at: now } } satisfies AccessCode });
      return { outcome, name: pending.name };
    }
    return { outcome: await this.complete(state, pending.discordId, pending.name, now), name: pending.name };
  }

  /** Discord confirmed who signed in: a new key for this PC, kept until the plugin collects it. */
  async complete(state: string, discordId: string, name: string, now: number): Promise<"ok" | "expired" | "banned"> {
    const login = await this.one<Login>(`login:${state}`);
    if (!login || now - login.created > accountLimits.loginMs || login.key) return "expired";
    const account = await this.one<Account>(`acct:${discordId}`) ?? { id: discordId, name, created: now, banned: false, keys: [], characters: 0 };
    if (account.banned) { await this.store.delete([`login:${state}`]); return "banned"; }
    const key = newKey(), hash = await sha256(key);
    account.name = name;
    account.keys.push({ hash, created: now, used: now });
    const dropped = account.keys.splice(0, Math.max(0, account.keys.length - accountLimits.keysPerAccount));
    if (dropped.length) await this.store.delete(dropped.map(k => `key:${k.hash}`));
    await this.store.put({ [`acct:${discordId}`]: account, [`key:${hash}`]: discordId, [`login:${state}`]: { ...login, key, name } satisfies Login });
    return "ok";
  }

  /** The plugin collects its key, once, by proving it made the state. */
  async claim(state: string, verifier: string, now: number): Promise<{ status: "ready"; key: string; name: string } | { status: "pending" } | null> {
    if (!isState(state) || typeof verifier !== "string" || verifier.length < 43 || verifier.length > 128 || await sha256(verifier) !== state) return null;
    const login = await this.one<Login>(`login:${state}`);
    if (!login || now - login.created > accountLimits.loginMs) return null;
    if (!login.key) return { status: "pending" };
    await this.store.delete([`login:${state}`]);
    return { status: "ready", key: login.key, name: login.name ?? "" };
  }

  /** A connection presents a key and a character: tie the character to the account the first time, refuse it for others. */
  async authorize(keyHash: string, contentId: string, name: string, now: number): Promise<Authorized> {
    const id = await this.one<string>(`key:${keyHash}`);
    const account = id ? await this.one<Account>(`acct:${id}`) : undefined;
    if (!id || !account || !account.keys.some(k => k.hash === keyHash)) return { error: "signed_out" };
    if (account.banned) return { error: "banned" };
    const character = await this.one<CharacterRecord>(`char:${contentId}`);
    const updates: Record<string, unknown> = {};
    if (!character) {
      if (account.characters >= accountLimits.charactersPerAccount) return { error: "too_many" };
      updates[`char:${contentId}`] = { account: id, name: name.slice(0, 40), bound: now } satisfies CharacterRecord;
      account.characters++;
    } else if (character.account !== id) return { error: "taken" };
    // The name is the one the character was first tied with: a client can't rename it later, so nobody passes their
    // own character off as a friend's. A record kept without one takes the next name sent. A real name change in game
    // needs the operator to release the character (the admin page), after which it binds again.
    else if (!character.name && name) updates[`char:${contentId}`] = { ...character, name: name.slice(0, 40) };
    const used = account.keys.find(k => k.hash === keyHash)!;
    if (now - used.used > 3_600_000 || Object.keys(updates).length) { used.used = now; updates[`acct:${id}`] = account; }
    if (Object.keys(updates).length) await this.store.put(updates);
    return { account: id, name: ((updates[`char:${contentId}`] as CharacterRecord | undefined) ?? character!).name };
  }

  // ── Operator ──
  /** New one-time access codes, each with the operator's note (who it's for). */
  async makeCodes(count: number, note: string, now: number): Promise<AccessCode[]> {
    const n = Math.max(1, Math.min(accessLimits.codesPerBatch, Math.floor(Number(count)) || 1));
    const clean = String(note ?? "").replace(/[\u0000-\u001f]/g, "").slice(0, accessLimits.noteLength);
    const made: AccessCode[] = [];
    while (made.length < n) {
      const code = newAccessCode();
      if (made.some(c => c.code === code) || await this.one<AccessCode>(`code:${code}`)) continue;
      made.push({ code, note: clean, created: now });
    }
    await this.store.put(Object.fromEntries(made.map(c => [`code:${c.code}`, c])));
    return made;
  }
  async codes(): Promise<AccessCode[]> {
    return [...(await this.store.list<AccessCode>({ prefix: "code:" })).values()].sort((a, b) => b.created - a.created);
  }
  /** Withdraws an unused code; a used one stays as the record of who it let in. */
  async revokeCode(typed: string) {
    const code = canonicalAccessCode(typed);
    const record = code ? await this.one<AccessCode>(`code:${code}`) : undefined;
    if (!record || record.used) return false;
    await this.store.put({ [`code:${code}`]: { ...record, revoked: true } satisfies AccessCode });
    return true;
  }
  async accounts(): Promise<AccountSummary[]> {
    const accounts = [...(await this.store.list<Account>({ prefix: "acct:" })).values()];
    const characters = [...(await this.store.list<CharacterRecord>({ prefix: "char:" })).entries()];
    return accounts.map(a => ({ id: a.id, name: a.name, created: a.created, banned: a.banned, keys: a.keys.length,
      characters: characters.filter(([, c]) => c.account === a.id).map(([k, c]) => ({ id: k.slice(5), name: c.name, bound: c.bound })) }));
  }
  async ban(id: string, banned: boolean) {
    const account = await this.one<Account>(`acct:${id}`);
    if (!account) return false;
    account.banned = banned;
    await this.store.put({ [`acct:${id}`]: account });
    return true;
  }
  /** Signs every PC of the account out; it signs in again with Discord. */
  async signOut(id: string) {
    const account = await this.one<Account>(`acct:${id}`);
    if (!account) return false;
    await this.store.delete(account.keys.map(k => `key:${k.hash}`));
    account.keys = [];
    await this.store.put({ [`acct:${id}`]: account });
    return true;
  }
  /** Frees a character tied to the wrong account; the next account to connect with it gets it. */
  async release(contentId: string) {
    const character = await this.one<CharacterRecord>(`char:${contentId}`);
    if (!character) return false;
    await this.store.delete([`char:${contentId}`]);
    const account = await this.one<Account>(`acct:${character.account}`);
    if (account) { account.characters = Math.max(0, account.characters - 1); await this.store.put({ [`acct:${account.id}`]: account }); }
    return true;
  }
}

export interface AccountsApi {
  begin(state: string, now: number): Promise<boolean>;
  identify(state: string, discordId: string, name: string, now: number): Promise<{ token: string; isNew: boolean } | "expired" | "banned">;
  confirm(state: string, token: string, now: number, accessCode?: string, codesRequired?: boolean): Promise<{ outcome: ConfirmOutcome; name: string }>;
  complete(state: string, discordId: string, name: string, now: number): Promise<"ok" | "expired" | "banned">;
  claim(state: string, verifier: string, now: number): Promise<{ status: "ready"; key: string; name: string } | { status: "pending" } | null>;
  authorize(keyHash: string, contentId: string, name: string, now: number): Promise<Authorized>;
  accounts(): Promise<AccountSummary[]>;
  ban(id: string, banned: boolean): Promise<boolean>;
  signOut(id: string): Promise<boolean>;
  release(contentId: string): Promise<boolean>;
  makeCodes(count: number, note: string, now: number): Promise<AccessCode[]>;
  codes(): Promise<AccessCode[]>;
  revokeCode(code: string): Promise<boolean>;
}

export interface AuthEnv {
  accounts?: AccountsApi;
  DISCORD_CLIENT_ID?: string; DISCORD_CLIENT_SECRET?: string; HAVEN_ADMIN_KEY?: string; rate?: RateLimit;
  /** "required": a new Discord account needs a one-time access code to make its Haven account. */
  HAVEN_ACCESS_CODES?: string;
  /** Discord's API, replaceable in tests. */
  discord?: (url: string, init: RequestInit) => Promise<Response>;
}

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json", "Cache-Control": "no-store" } });
const page = (title: string, text: string, status = 200) => new Response(
  `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Haven</title>
<body style="font-family:system-ui;background:#14161a;color:#e8e6e3;display:grid;place-items:center;min-height:90vh;margin:0">
<main style="max-width:420px;padding:24px;text-align:center"><h1 style="font-size:22px">${title}</h1><p style="color:#a9a59d">${text}</p></main>`,
  { status, headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer" } });
// The page that asks the person to compare the code with the one Haven shows, and to confirm.
// A new account (when codes are required) is also asked for its access code; a wrong one shows the page again with a note.
const confirmPage = (name: string, code: string, state: string, token: string, askCode = false, wrongCode = false) => new Response(
  `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Haven</title>
<body style="font-family:system-ui;background:#14161a;color:#e8e6e3;display:grid;place-items:center;min-height:90vh;margin:0">
<main style="max-width:440px;padding:24px;text-align:center"><h1 style="font-size:22px">Sign in to Haven as ${escape(name)}?</h1>
<p style="color:#a9a59d">Haven shows a code while it waits. Continue only if it is this one:</p>
<p style="font-size:34px;letter-spacing:4px;font-weight:600;margin:8px 0 20px">${escape(code)}</p>
<form method="post" action="/auth/discord/confirm"><input type="hidden" name="state" value="${escape(state)}"><input type="hidden" name="token" value="${escape(token)}">
${askCode ? `<p style="color:#a9a59d;margin:0 0 8px">Haven is invite-only for now. Enter the access code you were given:</p>
${wrongCode ? `<p style="color:#e5866f;margin:0 0 8px">That access code isn't valid, or has already been used.</p>` : ""}
<input name="access" required autocomplete="off" spellcheck="false" placeholder="XXXX-XXXX-XXXX" maxlength="40" style="font:inherit;font-size:20px;letter-spacing:2px;text-align:center;width:260px;padding:8px;border-radius:8px;border:1px solid #3a3d44;background:#1d2026;color:#e8e6e3;margin-bottom:16px"><br>` : ""}
<button style="font:inherit;font-size:16px;padding:10px 22px;border-radius:8px;border:0;background:#c9a45c;color:#14161a;cursor:pointer">Yes, sign me in</button></form>
<p style="color:#a9a59d;font-size:14px;margin-top:22px">If you didn't just choose Sign in with Discord in Haven, or Haven shows a different code, close this page. Someone may be trying to use your account.</p></main>`,
  // same-origin, not no-referrer: under no-referrer Chrome and Edge post the form with "Origin: null", which the
  // confirm step refused ("Forbidden" after "Yes, sign me in"; a new player's report, 3 November 2026).
  { status: 200, headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "Referrer-Policy": "same-origin", "X-Frame-Options": "DENY", "Content-Security-Policy": "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'" } });
const escape = (text: string) => text.replace(/[&<>"']/g, c => `&#${c.charCodeAt(0)};`);
/** Whether a confirm post came from the confirm page itself, by the browser's Sec-Fetch-Site and Origin headers. */
export const confirmFromOwnPage = (site: string | null, origin: string | null, self: string) =>
  (!site || site === "same-origin") && (!origin || origin === self || origin === "null" && site === "same-origin");

export async function handleAuth(request: Request, env: AuthEnv, address: string, now = Date.now()): Promise<Response> {
  const url = new URL(request.url), path = url.pathname;
  const { accounts } = env;
  if (!accounts || !env.DISCORD_CLIENT_ID) return new Response("Not found", { status: 404 });
  if (path.startsWith("/auth/admin/")) return admin(request, env, accounts, path.slice("/auth/admin/".length));
  if (env.rate && !(await env.rate.limit({ key: "auth:" + address })).success) return new Response("Too many requests", { status: 429 });
  const callback = `${url.origin}/auth/discord/callback`;
  const discord = env.discord ?? ((u, init) => fetch(u, init));

  if (path === "/auth/discord" && request.method === "GET") {
    const state = url.searchParams.get("state");
    if (!isState(state) || !(await accounts.begin(state, now))) return page("Sign-in link not valid", "Start signing in from Haven again.", 400);
    const target = new URL("https://discord.com/oauth2/authorize");
    for (const [k, v] of Object.entries({ client_id: env.DISCORD_CLIENT_ID, response_type: "code", redirect_uri: callback, scope: "identify", state }))
      target.searchParams.set(k, v);
    return new Response(null, { status: 302, headers: { Location: target.toString(), "Cache-Control": "no-store" } });
  }

  if (path === "/auth/discord/callback" && request.method === "GET") {
    const state = url.searchParams.get("state"), code = url.searchParams.get("code");
    if (url.searchParams.get("error")) return page("Signing in was cancelled", "Nothing changed. You can start again from Haven whenever you like.");
    if (!isState(state) || !code || code.length > 200 || !env.DISCORD_CLIENT_SECRET) return page("Sign-in link not valid", "Start signing in from Haven again.", 400);
    const token = await discord("https://discord.com/api/oauth2/token", { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ grant_type: "authorization_code", code, redirect_uri: callback, client_id: env.DISCORD_CLIENT_ID, client_secret: env.DISCORD_CLIENT_SECRET }) });
    if (!token.ok) return page("Discord didn't confirm the sign-in", "Please start again from Haven.", 502);
    const access = (await token.json() as { access_token?: string }).access_token;
    const user = access ? await discord("https://discord.com/api/users/@me", { headers: { Authorization: `Bearer ${access}` } }) : null;
    if (!user?.ok) return page("Discord didn't confirm the sign-in", "Please start again from Haven.", 502);
    const me = await user.json() as { id?: string; username?: string; global_name?: string | null };
    if (!me.id || !/^[0-9]{5,25}$/.test(me.id)) return page("Discord didn't confirm the sign-in", "Please start again from Haven.", 502);
    const name = (me.global_name || me.username || "Discord user").slice(0, 40);
    const identified = await accounts.identify(state, me.id, name, now);
    if (identified === "banned") return page("This account can't use Haven", "Ask the person who runs your Haven server.", 403);
    if (identified === "expired") return page("This sign-in has expired", "Start signing in from Haven again.", 410);
    return confirmPage(name, await signInCode(state), state, identified.token, identified.isNew && env.HAVEN_ACCESS_CODES === "required");
  }

  if (path === "/auth/discord/confirm" && request.method === "POST") {
    // Only this page's own form: another site can't post it on someone's behalf. A browser that hides the page's
    // origin sends "Origin: null"; that passes only when it also says the post came from this site.
    const site = request.headers.get("Sec-Fetch-Site"), origin = request.headers.get("Origin");
    if (!confirmFromOwnPage(site, origin, url.origin)) return page("This sign-in page was opened from somewhere else", "Start signing in from Haven again, and confirm on the page it opens.", 403);
    let form: FormData;
    try { form = await request.formData(); } catch { return page("Sign-in link not valid", "Start signing in from Haven again.", 400); }
    const state = form.get("state"), token = form.get("token"), access = form.get("access");
    if (typeof state !== "string" || !isState(state) || typeof token !== "string" || token.length > 64) return page("Sign-in link not valid", "Start signing in from Haven again.", 400);
    const { outcome, name } = await accounts.confirm(state, token, now, typeof access === "string" ? access : undefined, env.HAVEN_ACCESS_CODES === "required");
    if (outcome === "code") return confirmPage(name, await signInCode(state), state, token, true, true);
    if (outcome === "banned") return page("This account can't use Haven", "Ask the person who runs your Haven server.", 403);
    if (outcome === "expired") return page("This sign-in has expired", "Start signing in from Haven again.", 410);
    return page(`Signed in as ${escape(name)}`, "You can close this tab and return to the game. Haven finishes signing in by itself.");
  }

  if (path === "/auth/claim" && request.method === "POST") {
    if (request.headers.has("Origin") || request.headers.has("Sec-Fetch-Site")) return new Response("Forbidden", { status: 403 });
    let body: { state?: string; verifier?: string };
    try { body = await request.json(); } catch { return new Response("Bad request", { status: 400 }); }
    const result = await accounts.claim(body.state ?? "", body.verifier ?? "", now);
    return result ? json(result) : json({ status: "expired" }, 410);
  }
  return new Response("Not found", { status: 404 });
}

async function admin(request: Request, env: AuthEnv, accounts: AccountsApi, route: string): Promise<Response> {
  const key = env.HAVEN_ADMIN_KEY ?? "";
  const given = request.headers.get("Authorization") ?? "";
  if (key.length < 32 || !await equalSecret(given, `Bearer ${key}`)) return new Response("Not found", { status: 404 });
  if (route === "accounts" && request.method === "GET") return json(await accounts.accounts());
  // Access codes: list them, make a batch ({ count, note }), or withdraw an unused one.
  if (route === "codes" && request.method === "GET") return json(await accounts.codes());
  if (route === "codes/new" && request.method === "POST") {
    let body: { count?: number; note?: string } = {};
    try { body = await request.json(); } catch { }
    return json(await accounts.makeCodes(body.count ?? 1, body.note ?? "", Date.now()));
  }
  if (route.startsWith("codes/revoke/") && request.method === "POST") return json({ done: await accounts.revokeCode(decodeURIComponent(route.slice("codes/revoke/".length))) });
  const [action, id] = route.split("/");
  if (request.method !== "POST" || !id) return new Response("Not found", { status: 404 });
  const done = action === "ban" ? await accounts.ban(id, true) : action === "unban" ? await accounts.ban(id, false)
    : action === "signout" ? await accounts.signOut(id) : action === "release" && isContentId(id) ? await accounts.release(id) : null;
  return done === null ? new Response("Not found", { status: 404 }) : json({ done });
}

const refusals = {
  signed_out: "Your Haven sign-in has expired. Sign in with Discord again in Haven (Settings → Connection).",
  banned: "This Haven account can't connect. Ask the person who runs your Haven server.",
  taken: "This character is tied to another Haven account. If it's yours, ask the person who runs your Haven server to release it.",
  too_many: "This Haven account has reached its character limit. Ask the person who runs your Haven server.",
} as const;

/**
 * At the edge, before a relay connection reaches the container: checks the account and character the client presents
 * and returns the headers to forward (with the verified stamp, without the key), or the refusal to send instead.
 */
export async function verifyConnection(request: Request, accounts: AccountsApi | undefined, stampKey: string, required: boolean, now = Date.now())
  : Promise<{ headers: Headers } | { refusal: Response }> {
  const headers = new Headers(request.headers);
  headers.delete("X-Haven-Verified");
  headers.delete("Authorization");
  const auth = request.headers.get("Authorization"), character = request.headers.get("X-Haven-Character");
  if (accounts && auth?.startsWith("Bearer ") && auth.length < 200 && isContentId(character)) {
    let name = "";
    try { name = decodeURIComponent(request.headers.get("X-Haven-Character-Name") ?? "").replace(/[\u0000-\u001f]/g, "").slice(0, 40); } catch { }
    const result = await accounts.authorize(await sha256(auth.slice(7)), character, name, now);
    if ("error" in result) return { refusal: new Response(refusals[result.error], { status: 403, headers: { "X-Haven-Refusal": result.error } }) };
    headers.set("X-Haven-Verified", await stamp(stampKey, character, now + accountLimits.stampMs, result.account, result.name));
    return { headers };
  }
  if (required) return { refusal: new Response("Sign in with Discord in Haven (Settings → Connection) to join gatherings and share pictures.", { status: 401, headers: { "X-Haven-Refusal": "sign_in" } }) };
  return { headers };
}
