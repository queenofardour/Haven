import test from "node:test";
import assert from "node:assert/strict";
import { AccountLedger, handleAuth, verifyConnection, stamp, sha256, base64url, isContentId, signInCode } from "../src/accounts.ts";

// The Durable Object's storage, in memory.
class Store {
  map = new Map();
  async get(keys) { return new Map(keys.filter(k => this.map.has(k)).map(k => [k, structuredClone(this.map.get(k))])); }
  async put(entries) { for (const [k, v] of Object.entries(entries)) this.map.set(k, structuredClone(v)); }
  async delete(keys) { let n = 0; for (const k of keys) if (this.map.delete(k)) n++; return n; }
  async deleteAll() { this.map.clear(); }
  async list({ prefix }) { return new Map([...this.map.keys()].filter(k => k.startsWith(prefix)).sort().map(k => [k, structuredClone(this.map.get(k))])); }
}
const now = Date.parse("2026-10-28T12:00:00Z");
const verifier = () => base64url(crypto.getRandomValues(new Uint8Array(32)));
// Discord's token and user endpoints, faked: each code signs in the Discord user it names.
const discord = users => async (url, init) => {
  if (url.endsWith("/oauth2/token")) return new Response(JSON.stringify({ access_token: "token-" + new URLSearchParams(init.body).get("code") }));
  const code = init.headers.Authorization.replace("Bearer token-", "");
  return users[code] ? new Response(JSON.stringify(users[code])) : new Response("no", { status: 401 });
};
const environment = (ledger, users = {}) => ({ accounts: ledger, DISCORD_CLIENT_ID: "1564576728410759199", DISCORD_CLIENT_SECRET: "secret", HAVEN_ADMIN_KEY: "a".repeat(40), discord: discord(users) });

async function signIn(e, code) {
  const v = verifier(), state = await sha256(v);
  const start = await handleAuth(new Request(`https://relay.example/auth/discord?state=${state}`), e, "1.2.3.4", now);
  assert.equal(start.status, 302);
  const location = new URL(start.headers.get("Location"));
  assert.equal(location.origin + location.pathname, "https://discord.com/oauth2/authorize");
  assert.equal(location.searchParams.get("state"), state);
  assert.equal(location.searchParams.get("redirect_uri"), "https://relay.example/auth/discord/callback");
  assert.equal(location.searchParams.get("scope"), "identify");
  assert.equal(location.searchParams.get("prompt"), null); // Discord asks every time
  const claim = () => handleAuth(new Request("https://relay.example/auth/claim", { method: "POST", body: JSON.stringify({ state, verifier: v }) }), e, "1.2.3.4", now);
  assert.equal((await (await claim()).json()).status, "pending");
  const back = await handleAuth(new Request(`https://relay.example/auth/discord/callback?code=${code}&state=${state}`), e, "1.2.3.4", now);
  assert.equal(back.status, 200);
  // Discord's answer alone issues nothing: the page asks the person to compare Haven's code first.
  const html = await back.text();
  assert.ok(html.includes(await signInCode(state)));
  assert.equal((await (await claim()).json()).status, "pending");
  const token = /name="token" value="([^"]+)"/.exec(html)[1];
  const confirmed = await confirm(e, state, token);
  assert.equal(confirmed.status, 200);
  const result = await (await claim()).json();
  assert.equal(result.status, "ready");
  assert.equal((await claim()).status, 410); // Collected once.
  return result;
}

const confirm = (e, state, token, headers = { Origin: "https://relay.example", "Sec-Fetch-Site": "same-origin" }) =>
  handleAuth(new Request("https://relay.example/auth/discord/confirm", { method: "POST", headers, body: new URLSearchParams({ state, token }) }), e, "1.2.3.4", now);

test("a sign-in link someone else made gives them nothing unless the person confirms the code on the page", async () => {
  const ledger = new AccountLedger(new Store()), e = environment(ledger, { zoe: { id: "111111111111111111", global_name: "Zoe" } });
  // The attacker makes a state and sends Zoe its link; Zoe goes through Discord.
  const v = verifier(), state = await sha256(v);
  await handleAuth(new Request(`https://relay.example/auth/discord?state=${state}`), e, "1.2.3.4", now);
  const back = await handleAuth(new Request(`https://relay.example/auth/discord/callback?code=zoe&state=${state}`), e, "1.2.3.4", now);
  const token = /name="token" value="([^"]+)"/.exec(await back.text())[1];
  const claim = () => handleAuth(new Request("https://relay.example/auth/claim", { method: "POST", body: JSON.stringify({ state, verifier: v }) }), e, "1.2.3.4", now);
  assert.equal((await (await claim()).json()).status, "pending");
  // Another site can't confirm on her behalf, and a made-up token confirms nothing.
  assert.equal((await confirm(e, state, token, { Origin: "https://evil.example", "Sec-Fetch-Site": "cross-site" })).status, 403);
  assert.equal((await confirm(e, state, token, { "Sec-Fetch-Site": "cross-site" })).status, 403);
  assert.equal((await confirm(e, state, "x".repeat(43))).status, 410);
  // Chrome posts the page's form with "Origin: null" when the page hides its referrer: accepted with same-origin.
  assert.equal((await confirm(e, state, "x".repeat(43), { Origin: "null", "Sec-Fetch-Site": "same-origin" })).status, 410);
  assert.equal((await confirm(e, state, token, { Origin: "null", "Sec-Fetch-Site": "cross-site" })).status, 403);
  assert.equal((await confirm(e, state, token, { Origin: "null" })).status, 403);
  assert.equal((await (await claim()).json()).status, "pending");
  // Confirmed (on the page, by the person) it completes once; a second confirm finds it done.
  assert.equal((await confirm(e, state, token)).status, 200);
  assert.equal((await confirm(e, state, token)).status, 410);
});

test("the sign-in code matches Haven.Core's AccountSignIn.CodeOf", async () => {
  assert.equal(await signInCode("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), "TAM-K5D");
  assert.match(await signInCode(await sha256(verifier())), /^[A-HJ-NP-Z2-9]{3}-[A-HJ-NP-Z2-9]{3}$/);
});

test("stamps match Haven.Core's AccountStamp byte for byte", async () => {
  assert.equal(await stamp("r".repeat(40), "1234567890123", 1793000000000, "111111111111111111", "Zo\u00eb Chen"),
    "1234567890123.1793000000000.111111111111111111.Wm_DqyBDaGVu.dSmlmwXYruBar9rBO9gf0dPfN8dU5op7BzWNNf7n0cs");
  assert.ok(isContentId("18446744073709551615") && !isContentId("18446744073709551616") && !isContentId("0") && !isContentId("012"));
});

test("signing in with Discord gives the PC a key, collected once, only with the verifier", async () => {
  const ledger = new AccountLedger(new Store()), e = environment(ledger, { zoe: { id: "111111111111111111", global_name: "Zoe" } });
  const { key, name } = await signIn(e, "zoe");
  assert.equal(name, "Zoe");
  assert.ok(key.length >= 43);
  // A wrong verifier gets nothing, and the key is never in a URL the browser saw.
  const v = verifier(), state = await sha256(v);
  await handleAuth(new Request(`https://relay.example/auth/discord?state=${state}`), e, "1.2.3.4", now);
  const wrong = await handleAuth(new Request("https://relay.example/auth/claim", { method: "POST", body: JSON.stringify({ state, verifier: verifier() }) }), e, "1.2.3.4", now);
  assert.equal(wrong.status, 410);
  // A web page cannot collect keys.
  const page = await handleAuth(new Request("https://relay.example/auth/claim", { method: "POST", headers: { Origin: "https://evil.example" }, body: "{}" }), e, "1.2.3.4", now);
  assert.equal(page.status, 403);
  // Cancelled at Discord: nothing changes.
  assert.equal((await handleAuth(new Request(`https://relay.example/auth/discord/callback?error=access_denied&state=${state}`), e, "1.2.3.4", now)).status, 200);
  // A sign-in left too long expires.
  assert.equal(await ledger.complete(state, "111111111111111111", "Zoe", now + 11 * 60_000), "expired");
});

test("a character belongs to the first account that connects with it", async () => {
  const ledger = new AccountLedger(new Store());
  const e = environment(ledger, { zoe: { id: "111111111111111111", global_name: "Zoe" }, eve: { id: "222222222222222222", username: "eve" } });
  const zoe = await signIn(e, "zoe"), eve = await signIn(e, "eve");
  const hash = async k => sha256(k);
  assert.deepEqual(await ledger.authorize(await hash(zoe.key), "4001", "Zoe Chen", now), { account: "111111111111111111", name: "Zoe Chen" });
  assert.deepEqual(await ledger.authorize(await hash(zoe.key), "4001", "Zoe Chen", now), { account: "111111111111111111", name: "Zoe Chen" });
  // The name binds once: a client claiming another name for the character still gets the bound one.
  assert.deepEqual(await ledger.authorize(await hash(zoe.key), "4001", "Eve Friend", now), { account: "111111111111111111", name: "Zoe Chen" });
  assert.deepEqual(await ledger.authorize(await hash(eve.key), "4001", "Zoe Chen", now), { error: "taken" });
  assert.deepEqual(await ledger.authorize(await hash("made-up-key"), "4002", "", now), { error: "signed_out" });
  // The operator releases a character tied to the wrong account; the next account to connect gets it.
  assert.ok(await ledger.release("4001"));
  assert.deepEqual(await ledger.authorize(await hash(eve.key), "4001", "Zoe Chen", now), { account: "222222222222222222", name: "Zoe Chen" });
  // Banning refuses every PC of the account; signing out makes its keys useless.
  assert.ok(await ledger.ban("222222222222222222", true));
  assert.deepEqual(await ledger.authorize(await hash(eve.key), "4001", "", now), { error: "banned" });
  assert.equal(await ledger.complete(await sha256(verifier()), "222222222222222222", "eve", now), "expired");
  assert.ok(await ledger.signOut("111111111111111111"));
  assert.deepEqual(await ledger.authorize(await hash(zoe.key), "4003", "", now), { error: "signed_out" });
  const listed = await ledger.accounts();
  assert.equal(listed.find(a => a.id === "222222222222222222").characters[0].name, "Zoe Chen");
  assert.ok(listed.find(a => a.id === "222222222222222222").banned);
});

test("the edge forwards only a verified character, never the key or a forged stamp", async () => {
  const ledger = new AccountLedger(new Store()), e = environment(ledger, { zoe: { id: "111111111111111111", global_name: "Zoe" } });
  const { key } = await signIn(e, "zoe");
  const relayKey = "r".repeat(40);
  const connect = headers => verifyConnection(new Request("https://relay.example/", { headers: { Upgrade: "websocket", ...headers } }), ledger, relayKey, true, now);
  const ok = await connect({ Authorization: `Bearer ${key}`, "X-Haven-Character": "4001", "X-Haven-Character-Name": encodeURIComponent("Zoe Chen"), "X-Haven-Verified": "4002.1.forged" });
  assert.ok("headers" in ok);
  assert.equal(ok.headers.get("Authorization"), null);
  assert.equal(ok.headers.get("X-Haven-Verified"), await stamp(relayKey, "4001", now + 60_000, "111111111111111111", "Zoe Chen"));
  const unsigned = (await connect({ "X-Haven-Character": "4001" })).refusal;
  assert.equal(unsigned.status, 401); assert.equal(unsigned.headers.get("X-Haven-Refusal"), "sign_in");
  const expired = (await connect({ Authorization: "Bearer nope", "X-Haven-Character": "4001" })).refusal;
  assert.equal(expired.status, 403); assert.equal(expired.headers.get("X-Haven-Refusal"), "signed_out");
  // Not required (a self-hosted relay or the rollout): no key passes through unstamped, and a forged stamp is removed.
  const open = await verifyConnection(new Request("https://relay.example/", { headers: { "X-Haven-Verified": "4002.1.forged" } }), ledger, relayKey, false, now);
  assert.equal(open.headers.get("X-Haven-Verified"), null);
});

test("account administration needs the admin key", async () => {
  const ledger = new AccountLedger(new Store()), e = environment(ledger);
  assert.equal((await handleAuth(new Request("https://relay.example/auth/admin/accounts"), e, "1.2.3.4", now)).status, 404);
  const listed = await handleAuth(new Request("https://relay.example/auth/admin/accounts", { headers: { Authorization: `Bearer ${"a".repeat(40)}` } }), e, "1.2.3.4", now);
  assert.equal(listed.status, 200);
  assert.deepEqual(await listed.json(), []);
});

test("sign-ins nobody finished are swept, at most once an hour", async () => {
  const store = new Store(), ledger = new AccountLedger(store);
  const stale = await sha256(verifier()), fresh = await sha256(verifier());
  await ledger.begin(stale, now - 30 * 60_000);
  assert.ok(store.map.has(`login:${stale}`));
  await ledger.begin(fresh, now - 2 * 60_000);
  assert.ok(store.map.has(`login:${stale}`)); // swept less than an hour ago
  await ledger.begin(await sha256(verifier()), now + 60 * 60_000);
  assert.ok(!store.map.has(`login:${stale}`) && !store.map.has(`login:${fresh}`));
  assert.equal([...store.map.keys()].filter(k => k.startsWith("login:")).length, 1);
});

test("with access codes required, a new Discord account needs an unused code; accounts made before sign in without one", async () => {
  const ledger = new AccountLedger(new Store());
  const users = { old: { id: "222222222222222222", global_name: "Old" }, neu: { id: "333333333333333333", global_name: "New" }, two: { id: "444444444444444444", global_name: "Two" } };
  // An account made before codes: no code asked, then or now.
  assert.equal((await signIn(environment(ledger, users), "old")).status, "ready");
  const e = { ...environment(ledger, users), HAVEN_ACCESS_CODES: "required" };
  assert.equal((await signIn(e, "old")).status, "ready");
  const admin = (route, init = {}) => handleAuth(new Request(`https://relay.example/auth/admin/${route}`, { ...init, headers: { Authorization: "Bearer " + "a".repeat(40) } }), e, "1.2.3.4", now);
  assert.equal((await handleAuth(new Request("https://relay.example/auth/admin/codes"), e, "1.2.3.4", now)).status, 404); // admin only
  const made = await (await admin("codes/new", { method: "POST", body: JSON.stringify({ count: 2, note: "For Mira" }) })).json();
  assert.equal(made.length, 2);
  assert.match(made[0].code, /^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/);
  assert.equal(made[0].note, "For Mira");
  // A new account: the page asks for a code; a wrong one asks again, the right one (typed loosely) signs in and is used up.
  const start = async code => {
    const v = verifier(), state = await sha256(v);
    await handleAuth(new Request(`https://relay.example/auth/discord?state=${state}`), e, "1.2.3.4", now);
    const html = await (await handleAuth(new Request(`https://relay.example/auth/discord/callback?code=${code}&state=${state}`), e, "1.2.3.4", now)).text();
    const claim = async () => (await handleAuth(new Request("https://relay.example/auth/claim", { method: "POST", body: JSON.stringify({ state, verifier: v }) }), e, "1.2.3.4", now)).json();
    return { state, html, token: /name="token" value="([^"]+)"/.exec(html)[1], claim };
  };
  const post = (state, token, access) => handleAuth(new Request("https://relay.example/auth/discord/confirm", { method: "POST",
    headers: { Origin: "https://relay.example", "Sec-Fetch-Site": "same-origin" }, body: new URLSearchParams(access === undefined ? { state, token } : { state, token, access }) }), e, "1.2.3.4", now);
  const neu = await start("neu");
  assert.ok(neu.html.includes('name="access"'));
  const missing = await post(neu.state, neu.token);
  assert.ok((await missing.text()).includes("isn't valid"));
  assert.equal((await neu.claim()).status, "pending");
  assert.ok((await (await post(neu.state, neu.token, "AAAA-BBBB-CCCC")).text()).includes("isn't valid"));
  assert.equal((await post(neu.state, neu.token, made[0].code.toLowerCase().replace(/-/g, " "))).status, 200);
  assert.equal((await neu.claim()).status, "ready");
  // Used up: another new account can't use it, but can use the second code; the second code, revoked first, can't.
  const two = await start("two");
  assert.ok((await (await post(two.state, two.token, made[0].code)).text()).includes("isn't valid"));
  assert.equal((await (await admin(`codes/revoke/${made[1].code}`, { method: "POST" })).json()).done, true);
  assert.ok((await (await post(two.state, two.token, made[1].code)).text()).includes("isn't valid"));
  const list = await (await admin("codes")).json();
  assert.equal(list.find(c => c.code === made[0].code).used.account, "333333333333333333");
  assert.equal(list.find(c => c.code === made[1].code).revoked, true);
  assert.equal((await (await admin(`codes/revoke/${made[0].code}`, { method: "POST" })).json()).done, false); // a used code stays
  // Five wrong tries drop the sign-in.
  assert.ok((await (await post(two.state, two.token, "x")).text()).includes("isn't valid"));
  assert.ok((await (await post(two.state, two.token, "x")).text()).includes("isn't valid"));
  assert.equal((await post(two.state, two.token, "x")).status, 410);
  // The new account signs in again later without a code.
  assert.equal((await signIn(e, "neu")).status, "ready");
});
