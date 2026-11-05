import test from "node:test";
import assert from "node:assert/strict";
import { InviteBook, handleInvites, inviteLimits } from "../src/invites.ts";

class Store {
  map = new Map();
  async get(keys) { return new Map(keys.filter(k => this.map.has(k)).map(k => [k, structuredClone(this.map.get(k))])); }
  async put(entries) { for (const [k, v] of Object.entries(entries)) this.map.set(k, structuredClone(v)); }
  async delete(keys) { let n = 0; for (const k of keys) if (this.map.delete(k)) n++; return n; }
  async deleteAll() { this.map.clear(); }
  async list({ prefix }) { return new Map([...this.map.keys()].filter(k => k.startsWith(prefix)).sort().map(k => [k, structuredClone(this.map.get(k))])); }
}
// Keys "k-<account>" belong to that account; every character binds to whoever asks first.
const accounts = () => {
  const bound = new Map();
  return { async authorize(keyHash, contentId, name) {
    const account = keyHash.startsWith("k-") ? keyHash.slice(2) : null;
    if (!account) return { error: "signed_out" };
    if (!bound.has(contentId)) bound.set(contentId, { account, name: name || "Bound " + contentId });
    const record = bound.get(contentId);
    return record.account === account ? record : { error: "taken" };
  } };
};
const now = Date.parse("2026-11-03T12:00:00Z");
const host = "1001", friend = "2002", other = "3003";
const gathering = "0123456789abcdef";
const send = (book, invitees, at = now, extra = {}) => book.send("k-host", host, "Host Name", { code: "visit-code-1", home: "Cosy Cottage", gathering, invitees, ...extra }, at);

test("an invitation reaches only the character it names, from the host's bound name", async () => {
  const book = new InviteBook(new Store(), accounts());
  assert.deepEqual(await book.poll("k-friend", friend, "Friend", now), { invites: [] });
  assert.deepEqual(await send(book, [friend], now + 10_000), { sent: 1 });
  const got = await book.poll("k-friend", friend, "Friend", now + 20_000);
  assert.equal(got.invites.length, 1);
  assert.equal(got.invites[0].from, host);
  assert.equal(got.invites[0].name, "Host Name");
  assert.equal(got.invites[0].code, "visit-code-1");
  assert.equal(got.invites[0].home, "Cosy Cottage");
  // Another account can't read the friend's invitations by claiming the character.
  assert.deepEqual(await book.poll("k-stranger", friend, "Friend", now + 40_000), { error: "taken" });
  assert.deepEqual(await book.poll("nope", other, "", now + 40_000), { error: "signed_out" });
  // Someone else holds nothing.
  assert.deepEqual(await book.poll("k-other", other, "Other", now + 40_000), { invites: [] });
});

test("invitations expire, refreshes are cheap, and withdrawing takes them back", async () => {
  const store = new Store(), book = new InviteBook(store, accounts());
  await send(book, [friend, other]);
  const writes = () => store.map.get("invites:writes:2026-11-03");
  assert.equal(writes(), 3); // two invitees and the gathering's record
  // A refresh soon after writes nothing new for the invitees (only the gathering's record).
  assert.deepEqual(await send(book, [friend], now + 60_000), { sent: 0 });
  assert.equal(writes(), 4);
  // Withdrawing one: the other keeps theirs.
  assert.deepEqual(await book.withdraw("k-host", host, "", [friend], now + 70_000), { sent: 0 });
  assert.deepEqual(await book.poll("k-friend", friend, "", now + 80_000), { invites: [] });
  assert.equal((await book.poll("k-other", other, "", now + 80_000)).invites.length, 1);
  // Withdrawing all ends the gathering's invitations.
  await book.withdraw("k-host", host, "", undefined, now + 90_000);
  assert.deepEqual(await book.poll("k-other", other, "", now + 100_000), { invites: [] });
  assert.equal(store.map.has("hosting:" + host), false);
  // An invitation not withdrawn expires by itself, and the hourly sweep deletes it.
  await send(book, [friend], now + 200_000);
  const later = now + 200_000 + inviteLimits.inviteMs + 1;
  assert.deepEqual(await book.poll("k-friend", friend, "", later), { invites: [] });
  await book.send("k-host", host, "", { code: "visit-code-1", home: "", gathering: "fedcba9876543210", invitees: [other] }, later + inviteLimits.sweepMs);
  assert.equal(store.map.has("invite:" + friend), false);
});

test("a new gathering replaces the host's old invitations", async () => {
  const book = new InviteBook(new Store(), accounts());
  await send(book, [friend, other]);
  await book.send("k-host", host, "", { code: "visit-code-2", home: "Inn", gathering: "fedcba9876543210", invitees: [friend] }, now + 10_000);
  const got = await book.poll("k-friend", friend, "", now + 20_000);
  assert.equal(got.invites.length, 1);
  assert.equal(got.invites[0].code, "visit-code-2");
  assert.deepEqual(await book.poll("k-other", other, "", now + 20_000), { invites: [] });
});

test("sends and polls are bounded: shape, pace, per account and in total", async () => {
  const store = new Store(), book = new InviteBook(store, accounts());
  for (const bad of [{ invitees: [] }, { invitees: ["x"] }, { invitees: [host] }, { invitees: Array.from({ length: 17 }, (_, i) => String(5000 + i)) },
    { invitees: [friend], code: "short" }, { invitees: [friend], gathering: "nope" }])
    assert.deepEqual(await book.send("k-host", host, "", { code: "visit-code-1", home: "", gathering, ...bad }, now), { error: "invalid" });
  assert.deepEqual(await send(book, [friend]), { sent: 1 });
  assert.deepEqual(await send(book, [other], now + 1_000), { error: "slow" });
  assert.deepEqual(await book.poll("k-friend", friend, "", now), { invites: [{ ...(await store.get(["invite:" + friend])).get("invite:" + friend)[0] }] });
  assert.deepEqual(await book.poll("k-friend", friend, "", now + 1_000), { error: "slow" });
  // The day's total writes: once spent, sends are refused before anything is written.
  await store.put({ "invites:writes:2026-11-03": inviteLimits.writesPerDay });
  assert.deepEqual(await send(book, [other], now + 10_000), { error: "busy" });
  assert.equal(store.map.has("invite:" + other), false);
  // The day's polls, counted in memory and stored as they go.
  const fresh = new InviteBook(store, accounts());
  await store.put({ "invites:polls:2026-11-03": inviteLimits.pollsPerDay });
  assert.deepEqual(await fresh.poll("k-friend", friend, "", now), { error: "busy" });
  // A gathering can't have more than 32 invitees out.
  const many = new InviteBook(new Store(), accounts());
  for (let round = 0; round < 2; round++)
    assert.deepEqual(await many.send("k-host", host, "", { code: "visit-code-1", home: "", gathering, invitees: Array.from({ length: 16 }, (_, i) => String(6000 + round * 16 + i)) }, now + round * 5_000), { sent: 16 });
  assert.deepEqual(await many.send("k-host", host, "", { code: "visit-code-1", home: "", gathering, invitees: ["9999"] }, now + 10_000), { error: "limit" });
});

test("an invitee holds at most 8 invitations, the newest kept", async () => {
  const book = new InviteBook(new Store(), accounts());
  for (let i = 0; i < 10; i++)
    await book.send("k-h" + i, String(7000 + i), "", { code: "visit-code-1", home: "", gathering, invitees: [friend] }, now + i);
  const got = await book.poll("k-friend", friend, "", now + 100);
  assert.equal(got.invites.length, inviteLimits.perInvitee);
  assert.equal(got.invites.at(-1).from, "7009");
  assert.equal(got.invites.some(i => i.from === "7000"), false);
});

test("the HTTP routes: Haven only, signed in, bounded bodies", async () => {
  const calls = [];
  const invites = {
    async invitePoll(...a) { calls.push(["poll", ...a]); return { invites: [] }; },
    async inviteSend(...a) { calls.push(["send", ...a]); return { sent: 1 }; },
    async inviteWithdraw(...a) { calls.push(["withdraw", ...a]); return { error: "slow" }; },
  };
  const headers = { Authorization: "Bearer secret-key", "X-Haven-Character": friend, "X-Haven-Character-Name": "Fr%C3%A9d" };
  const get = await handleInvites(new Request("https://relay.example/invites", { headers }), { invites }, "1.2.3.4", now);
  assert.equal(get.status, 200);
  assert.equal(calls[0][0], "poll");
  assert.notEqual(calls[0][1], "secret-key"); // only the key's digest reaches the store
  assert.equal(calls[0][3], "Fréd");
  const body = JSON.stringify({ code: "visit-code-1", home: "Home", gathering, invitees: [host] });
  const post = await handleInvites(new Request("https://relay.example/invites/send", { method: "POST", headers: { ...headers, "Content-Length": String(body.length) }, body }), { invites }, "1.2.3.4", now);
  assert.equal(post.status, 200);
  const slow = await handleInvites(new Request("https://relay.example/invites/withdraw", { method: "POST", headers: { ...headers, "Content-Length": "2" }, body: "{}" }), { invites }, "1.2.3.4", now);
  assert.equal(slow.status, 429);
  assert.equal((await handleInvites(new Request("https://relay.example/invites"), { invites }, "1.2.3.4", now)).status, 401);
  assert.equal((await handleInvites(new Request("https://relay.example/invites", { headers: { ...headers, Origin: "https://evil.example" } }), { invites }, "1.2.3.4", now)).status, 403);
  const huge = "x".repeat(5000);
  assert.equal((await handleInvites(new Request("https://relay.example/invites/send", { method: "POST", headers: { ...headers, "Content-Length": String(huge.length) }, body: huge }), { invites }, "1.2.3.4", now)).status, 400);
  const limited = { limit: async () => ({ success: false }) };
  assert.equal((await handleInvites(new Request("https://relay.example/invites", { headers }), { invites, rate: limited }, "1.2.3.4", now)).status, 429);
});
