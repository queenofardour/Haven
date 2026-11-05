import test from "node:test";
import { createHash } from "node:crypto";
import assert from "node:assert/strict";
import { handleContent, sign, canonical, limits, addressKey } from "../src/content.ts";
import { ContentLedger, budget } from "../src/content-budget.ts";

// An in-memory stand-in for the parts of R2 the locker uses.
class Bucket {
  objects = new Map();
  record(key) {
    const item = this.objects.get(key); if (!item) return null;
    return { key, size: item.bytes.byteLength, uploaded: new Date(item.at), customMetadata: item.customMetadata ?? {},
      body: new Response(item.bytes).body, arrayBuffer: async () => item.bytes.slice(0), json: async () => JSON.parse(new TextDecoder().decode(item.bytes)) };
  }
  async head(key) { return this.record(key); }
  async get(key) { return this.record(key); }
  async delete(key) { for (const k of Array.isArray(key) ? key : [key]) this.objects.delete(k); }
  async list({ prefix, cursor, limit = 1000 }) {
    const keys = [...this.objects.keys()].filter(k => k.startsWith(prefix)).sort();
    const start = cursor ? Number(cursor) : 0, page = keys.slice(start, start + limit);
    return { objects: page.map(key => this.record(key)), truncated: start + limit < keys.length, cursor: String(start + limit) };
  }
  async put(key, value, options = {}) {
    // Like R2: a stream is read whole, and a stated SHA-256 that doesn't match refuses the object.
    if (value instanceof ReadableStream) value = await new Response(value).arrayBuffer();
    if (options.sha256 && await sha(new Uint8Array(value)) !== options.sha256) throw new Error("checksum mismatch");
    const bytes = typeof value === "string" ? new TextEncoder().encode(value).buffer : value;
    this.objects.set(key, { bytes, at: this.now, customMetadata: options.customMetadata });
  }
  now = Date.parse("2026-10-26T12:00:00Z");
  // R2's multipart uploads: parts kept per upload until completed (in the order listed, each by its etag) or aborted.
  uploads = new Map();
  async createMultipartUpload(key, options = {}) {
    const id = `upload+${this.uploads.size + 1}/=`;
    this.uploads.set(id, { key, options, parts: new Map() });
    return this.resumeMultipartUpload(key, id);
  }
  resumeMultipartUpload(key, uploadId) {
    const bucket = this;
    const find = () => { const u = bucket.uploads.get(uploadId); if (!u || u.key !== key) throw new Error("no such upload"); return u; };
    return { key, uploadId,
      async uploadPart(n, value) {
        const u = find(), bytes = new Uint8Array(await new Response(value).arrayBuffer());
        u.parts.set(n, bytes);
        return { partNumber: n, etag: `e${n}-${bytes.length}` };
      },
      async abort() { bucket.uploads.delete(uploadId); },
      async complete(parts) {
        const u = find();
        const chunks = parts.map(p => { const b = u.parts.get(p.partNumber); if (!b || p.etag !== `e${p.partNumber}-${b.length}`) throw new Error("bad part"); return b; });
        const all = new Uint8Array(chunks.reduce((t, c) => t + c.length, 0));
        chunks.reduce((at, c) => (all.set(c, at), at + c.length), 0);
        bucket.uploads.delete(uploadId);
        await bucket.put(key, all.buffer, u.options);
        return bucket.record(key);
      } };
  }
}
// Workers' crypto.DigestStream: a sink whose digest settles when it is closed.
crypto.DigestStream ??= class extends WritableStream {
  constructor(algorithm) {
    const hash = createHash(algorithm.replace("-", "").toLowerCase());
    let settle;
    const digest = new Promise(resolve => settle = resolve);
    super({ write(chunk) { hash.update(chunk); }, close() { settle(new Uint8Array(hash.digest()).buffer); } });
    this.digest = digest;
  }
};
// Workers' FixedLengthStream: a pass-through that errors when the bytes aren't exactly the length stated.
globalThis.FixedLengthStream ??= class extends TransformStream {
  constructor(length) {
    let seen = 0;
    super({ transform(chunk, c) { if ((seen += chunk.byteLength) > length) throw new Error("too long"); c.enqueue(chunk); },
      flush() { if (seen !== length) throw new Error("too short"); } });
  }
};
// The Durable Object's storage, in memory.
class Store {
  map = new Map();
  async get(keys) { return new Map(keys.filter(k => this.map.has(k)).map(k => [k, structuredClone(this.map.get(k))])); }
  async put(entries) { for (const [k, v] of Object.entries(entries)) this.map.set(k, structuredClone(v)); }
  async delete(keys) { let n = 0; for (const k of keys) if (this.map.delete(k)) n++; return n; }
  async deleteAll() { this.map.clear(); }
  async list({ prefix, startAfter, limit = Infinity }) {
    const keys = [...this.map.keys()].filter(k => k.startsWith(prefix) && (startAfter === undefined || k > startAfter)).sort().slice(0, limit);
    return new Map(keys.map(k => [k, structuredClone(this.map.get(k))]));
  }
}
const key = "k".repeat(40);
const allow = { limit: async () => ({ success: true }) };
const env = () => ({ HAVEN_CONTENT: new Bucket(), HAVEN_CONTENT_KEY: key, budget: new ContentLedger(new Store()), rate: allow });
const jpeg = size => { const b = new Uint8Array(size); b.set([0xFF, 0xD8, 0xFF, 0xE0]); for (let i = 4; i < size; i++) b[i] = i % 251; return b; };
const sha = async bytes => [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))].map(x => x.toString(16).padStart(2, "0")).join("");
async function ticket(op, hash, { size = 0, owner = "", agreement = 0, expires, by = "", media = "picture" } = {}, now) {
  expires ??= Math.floor(now / 1000) + 600;
  const sig = await sign(key, canonical(op, hash, size, owner, agreement, expires, by, media));
  return `https://relay.example/content/${hash}?op=${op}&size=${size}&owner=${owner}&agreement=${agreement}&exp=${expires}${by ? `&by=${by}` : ""}${media === "picture" ? "" : `&media=${media}`}&sig=${sig}`;
}
const owner = "0123456789abcdef";

test("signatures match Haven.Core's ContentLocker byte for byte", async () => {
  assert.equal(await sign("k".repeat(40), canonical("get", "a".repeat(64), 0, "", 0, 1793000000)), "ZXdRh38x2NORnm10hnnBp_xvlNDgqPaA14J-ssQ6FA0");
  assert.equal(await sign("k".repeat(40), canonical("put", "b".repeat(64), 12345, owner, 1, 1793000600)), "6mKFO9fzhhtU7ZY0th3pGUWF_d2GUKBHJfVW3cEQrc4");
  assert.equal(await sign("k".repeat(40), canonical("put", "b".repeat(64), 12345, owner, 1, 1793000600, "4001")), "EDmDLT01uHDSqp5VkV82zGGqgt-6dJxclPWIgZAxyn4");
});

test("an owner uploads a picture once, and a ticket holder downloads exactly those bytes", async () => {
  const e = env(), now = e.HAVEN_CONTENT.now, bytes = jpeg(4096), hash = await sha(bytes);
  const put = await ticket("put", hash, { size: bytes.length, owner, agreement: 1 }, now);
  assert.equal((await handleContent(new Request(put, { method: "HEAD" }), e, now)).status, 404);
  assert.equal((await handleContent(new Request(put, { method: "PUT", body: bytes, headers: { "Content-Length": String(bytes.length) } }), e, now)).status, 201);
  assert.equal((await handleContent(new Request(put, { method: "HEAD" }), e, now)).status, 200);
  const got = await handleContent(new Request(await ticket("get", hash, {}, now)), e, now);
  assert.equal(got.status, 200);
  assert.equal(await sha(await got.arrayBuffer()), hash);
  assert.equal(e.budget.storage.map.get(`agreement:${owner}`).version, 1);
  assert.equal(e.budget.storage.map.get(`owner:${owner}:2026-10-26`).bytes, bytes.length);
});

test("tickets are checked: forged, expired, wrong method or wrong operation are refused", async () => {
  const e = env(), now = e.HAVEN_CONTENT.now, hash = "c".repeat(64);
  const good = await ticket("get", hash, {}, now);
  assert.equal((await handleContent(new Request(good.replace(/sig=[^&]+/, "sig=forged")), e, now)).status, 404);
  assert.equal((await handleContent(new Request(await ticket("get", hash, { expires: Math.floor(now / 1000) - 1 }, now)), e, now)).status, 403);
  assert.equal((await handleContent(new Request(await ticket("get", hash, { expires: Math.floor(now / 1000) + 3600 }, now)), e, now)).status, 403);
  assert.equal((await handleContent(new Request(good, { method: "PUT", body: "x" }), e, now)).status, 405);
  assert.equal((await handleContent(new Request(good.replace("op=get", "op=put")), e, now)).status, 405);
  // Every part must be present, or nothing is served.
  for (const part of ["HAVEN_CONTENT_KEY", "budget", "rate"])
    assert.equal((await handleContent(new Request(good), { ...e, [part]: undefined }, now)).status, 404);
});

test("uploads must match the ticket's fingerprint and size, be JPEG and stay within the limits", async () => {
  const e = env(), now = e.HAVEN_CONTENT.now, bytes = jpeg(1024), hash = await sha(bytes);
  const send = async (url, body) => (await handleContent(new Request(url, { method: "PUT", body, headers: { "Content-Length": String(body.length) } }), e, now)).status;
  assert.equal(await send(await ticket("put", "d".repeat(64), { size: bytes.length, owner, agreement: 1 }, now), bytes), 400);
  assert.equal(await send(await ticket("put", hash, { size: bytes.length + 1, owner, agreement: 1 }, now), bytes), 400);
  const png = new Uint8Array(1024); png[0] = 0x89;
  assert.equal(await send(await ticket("put", await sha(png), { size: png.length, owner, agreement: 1 }, now), png), 415);
  assert.equal(await send(await ticket("put", hash, { size: bytes.length, owner, agreement: 0 }, now), bytes), 400);
  assert.equal(await send(await ticket("put", hash, { size: limits.pictureBytes + 1, owner, agreement: 1 }, now), bytes), 400);
  // A body that does not declare exactly the ticket's size is refused unread.
  const put = await ticket("put", hash, { size: bytes.length, owner, agreement: 1 }, now);
  assert.equal((await handleContent(new Request(put, { method: "PUT", body: bytes }), e, now)).status, 400);
  // The owner's daily allowance, kept by the budget.
  e.budget.storage.map.set(`owner:${owner}:2026-10-26`, { count: budget.ownerUploadsPerDay, bytes: 0 });
  assert.equal(await send(put, bytes), 429);
  // The owner's storage, counting only what was stored in the window.
  e.budget.storage.map.set(`owner:${owner}:2026-10-26`, { count: 0, bytes: 0 });
  e.budget.storage.map.set(`owner:${owner}:2026-10-01`, { count: 1, bytes: budget.ownerBytes });
  assert.equal(await send(put, bytes), 429);
  e.budget.storage.map.delete(`owner:${owner}:2026-10-01`);
  e.budget.storage.map.set(`owner:${owner}:2026-09-01`, { count: 1, bytes: budget.ownerBytes });
  assert.equal(await send(put, bytes), 201);
});

test("a picture still in use is re-stored weekly, and a removed one is gone for good", async () => {
  const e = env(), bytes = jpeg(2048), hash = await sha(bytes);
  let now = e.HAVEN_CONTENT.now;
  const put = async () => ticket("put", hash, { size: bytes.length, owner, agreement: 1 }, now);
  await handleContent(new Request(await put(), { method: "PUT", body: bytes, headers: { "Content-Length": String(bytes.length) } }), e, now);
  const stored = e.HAVEN_CONTENT.objects.get(`pictures/${hash}`).at;
  now += limits.refreshMs + 1000; e.HAVEN_CONTENT.now = now;
  assert.equal((await handleContent(new Request(await put(), { method: "HEAD" }), e, now)).status, 200);
  assert.ok(e.HAVEN_CONTENT.objects.get(`pictures/${hash}`).at > stored);
  await e.HAVEN_CONTENT.put(`deny/${hash}`, "removed");
  assert.equal((await handleContent(new Request(await ticket("get", hash, {}, now)), e, now)).status, 410);
});

test("the account-wide budget holds whoever claims to be the owner", async () => {
  const ledger = new ContentLedger(new Store()), now = Date.parse("2026-10-26T12:00:00Z");
  // Many invented owners together still stop at the day's store count…
  for (let i = 0; i < budget.storesPerDay; i++) assert.equal(await ledger.store(i.toString(16).padStart(16, "0"), 1000, 1, true, now), null);
  assert.match(await ledger.store("f".repeat(16), 1000, 1, true, now), /today/);
  // …and at the bytes stored over the window, which bounds what R2 can hold.
  const full = new ContentLedger(new Store()); let day = now, refused = null;
  for (let i = 0; refused === null && i < 10_000; i++) {
    refused = await full.store(i.toString(16).padStart(16, "0"), limits.pictureBytes, 1, true, day);
    if (i % (budget.storesPerDay - 1) === budget.storesPerDay - 2) day += 86_400_000;
  }
  assert.match(refused ?? "", /full/);
  let stored = 0; for (const [k, v] of full.storage.map) if (k.startsWith("stored:")) stored += v.bytes;
  assert.ok(stored <= budget.storedBytes);
  // A month on, the window has moved and storing works again.
  assert.equal(await full.store("a".repeat(16), 1000, 1, true, day + budget.windowDays * 86_400_000), null);
  // Reads are charged per day, and refused once spent.
  const reads = new ContentLedger(new Store());
  assert.equal(await reads.read(budget.readsPerDay, now), true);
  assert.equal(await reads.read(1, now), false);
  assert.equal(await reads.read(1, now + 86_400_000), true);
  assert.equal(await reads.read(0, now), false);
});

test("the locker refuses before touching storage when an address floods it or the budget is spent", async () => {
  const e = env(), now = e.HAVEN_CONTENT.now, hash = "a".repeat(64);
  let touched = 0; const bucket = e.HAVEN_CONTENT; const head = bucket.head.bind(bucket);
  bucket.head = async k => { touched++; return head(k); };
  const flooded = { ...e, rate: { limit: async () => ({ success: false }) } };
  assert.equal((await handleContent(new Request(await ticket("get", hash, {}, now)), flooded, now)).status, 429);
  const spent = { ...e, budget: { read: async () => false, store: async () => "spent" } };
  assert.equal((await handleContent(new Request(await ticket("get", hash, {}, now)), spent, now)).status, 503);
  assert.equal(touched, 0);
});

async function uploaded(e, bytes, now, by = owner) {
  const hash = await sha(bytes);
  await handleContent(new Request(await ticket("put", hash, { size: bytes.length, owner: by, agreement: 1 }, now), { method: "PUT", body: bytes, headers: { "Content-Length": String(bytes.length) } }), e, now);
  return hash;
}
const post = async (e, url, now, address = "203.0.113.1") => handleContent(new Request(url, { method: "POST", headers: { "CF-Connecting-IP": address } }), e, now);
const operator = { Authorization: "Bearer " + "a".repeat(40) };

test("there is no community: share and unshare tickets, reports, public reads and the gallery are all refused", async () => {
  const e = { ...env(), HAVEN_ADMIN_KEY: "a".repeat(40) }, now = e.HAVEN_CONTENT.now;
  const hash = await uploaded(e, jpeg(3000), now);
  // Even a correctly signed share or unshare ticket is an unknown operation now, for every kind of content.
  for (const media of ["picture", "music", "furniture"])
    for (const op of ["share", "unshare"])
      assert.equal((await post(e, await ticket(op, hash, { size: 0, owner, agreement: 1, media }, now), now)).status, 405);
  assert.equal((await post(e, `https://relay.example/content/${hash}?op=report`, now)).status, 405);
  assert.equal((await handleContent(new Request(`https://relay.example/content/${hash}?op=public`), e, now)).status, 405);
  for (const query of ["", "?portrait=1&adult=1", "?media=music", "?media=furniture"])
    assert.equal((await handleContent(new Request(`https://relay.example/content/gallery${query}`), e, now)).status, 404);
  // The operator's review routes are gone too.
  assert.equal((await handleContent(new Request("https://relay.example/content/admin/reports", { headers: operator }), e, now)).status, 404);
  assert.equal((await handleContent(new Request(`https://relay.example/content/admin/keep/${hash}`, { method: "POST", headers: operator }), e, now)).status, 404);
  // Nothing about sharing was recorded, and the picture is still there for its ticket holders.
  assert.ok(![...e.budget.storage.map.keys()].some(k => /^(pic|reported|gallery|publicReads):/.test(k)));
  assert.equal((await handleContent(new Request(await ticket("get", hash, {}, now)), e, now)).status, 200);
});

test("the gallery's leftovers are dropped once, in batches, and the counters stay", async () => {
  const store = new Store(), ledger = new ContentLedger(store);
  let largest = 0; const remove = store.delete.bind(store);
  store.delete = async keys => { largest = Math.max(largest, keys.length); return remove(keys); };
  const hash = i => i.toString(16).padStart(64, "0");
  for (let i = 0; i < 300; i++) {
    store.map.set(`pic:${hash(i)}`, { owner, adult: false, portrait: false, sharedAt: i, reporters: [], accepted: false, hidden: false });
    store.map.set(`gallery:l:${String(9_999_999_999_999 - i).padStart(13, "0")}:${hash(i)}`, { adult: false });
  }
  store.map.set(`reported:${hash(1)}`, 1); store.map.set("publicReads:2026-10-26", 12);
  const kept = { "stored:2026-10-26": { count: 1, bytes: 10 }, [`owner:${owner}:2026-10-26`]: { count: 1, bytes: 10 }, [`agreement:${owner}`]: { version: 1 }, "reads:2026-10-26": 5 };
  for (const [k, v] of Object.entries(kept)) store.map.set(k, v);
  assert.equal(await ledger.dropCommunity(), 602);
  assert.ok(largest > 0 && largest <= 128);
  assert.deepEqual([...store.map.keys()].filter(k => !k.startsWith("migrated:")).sort(), Object.keys(kept).sort());
  // It runs once: a later start finds the marker and touches nothing.
  store.map.set(`pic:${hash(9)}`, { sharedAt: 9 });
  assert.equal(await ledger.dropCommunity(), 0);
  assert.ok(store.map.has(`pic:${hash(9)}`));
  // Deleting an item still clears any record of it, index entry included.
  store.map.set(`gallery:l:${String(9_999_999_999_999 - 9).padStart(13, "0")}:${hash(9)}`, { adult: false });
  await ledger.forget(hash(9));
  assert.ok(![...store.map.keys()].some(k => k.includes(hash(9))));
  // A cleared store has nothing left to drop, so its marker comes back with it.
  await ledger.clear();
  assert.deepEqual([...store.map.keys()], ["migrated:community"]);
});

test("the operator removes a picture for good; uploads report the owner's and the pool's usage", async () => {
  const e = { ...env(), HAVEN_ADMIN_KEY: "a".repeat(40) }, now = e.HAVEN_CONTENT.now, bytes = jpeg(5000);
  const hash = await sha(bytes);
  const put = await ticket("put", hash, { size: bytes.length, owner, agreement: 1 }, now);
  const stored = await handleContent(new Request(put, { method: "PUT", body: bytes, headers: { "Content-Length": String(bytes.length) } }), e, now);
  const [ownerBytes, ownerLimit, today, daily, pool, poolLimit] = stored.headers.get("X-Haven-Usage").split(",").map(Number);
  assert.deepEqual([ownerBytes, today, pool], [5000, 1, 5000]);
  assert.ok(ownerLimit > 0 && daily > 0 && poolLimit > 0);
  const removed = await handleContent(new Request(`https://relay.example/content/admin/remove/${hash}`, { method: "POST", headers: operator }), e, now);
  assert.equal((await removed.json()).removed, true);
  assert.equal(e.HAVEN_CONTENT.objects.has(`pictures/${hash}`), false);
  assert.equal((await handleContent(new Request(put, { method: "HEAD" }), e, now)).status, 410);
});

test("clearing deletes every picture and resets the allowances, but keeps the deny list", async () => {
  const e = { ...env(), HAVEN_ADMIN_KEY: "a".repeat(40) }, now = e.HAVEN_CONTENT.now;
  const kept = await uploaded(e, jpeg(1500), now), other = await uploaded(e, jpeg(1700), now);
  await e.HAVEN_CONTENT.put(`deny/${"d".repeat(64)}`, "removed");
  const admin = (path, key = "a".repeat(40)) => handleContent(new Request(`https://relay.example/content/admin/${path}`, { method: "POST", headers: { Authorization: "Bearer " + key } }), e, now);
  assert.equal((await admin("clear", "b".repeat(40))).status, 404);
  assert.equal((await (await admin("clear")).json()).deleted, 2);
  assert.equal([...e.HAVEN_CONTENT.objects.keys()].filter(k => k.startsWith("pictures/")).length, 0);
  assert.ok(e.HAVEN_CONTENT.objects.has(`deny/${"d".repeat(64)}`));
  assert.equal((await e.budget.usage(owner, now)).ownerBytes, 0);
  assert.ok(kept.length === 64 && other.length === 64);
});

test("the operator's admin page sees stored pictures and today's use of the budget", async () => {
  const e = { ...env(), HAVEN_ADMIN_KEY: "a".repeat(40) }, now = e.HAVEN_CONTENT.now;
  const first = await uploaded(e, jpeg(1200), now), second = await uploaded(e, jpeg(1300), now);
  const get = path => handleContent(new Request(`https://relay.example/content/admin/${path}`, { headers: operator }), e, now);
  assert.equal((await handleContent(new Request("https://relay.example/content/admin/stats"), e, now)).status, 404);
  const stats = await (await get("stats")).json();
  assert.deepEqual([stats.storesToday, stats.poolBytes], [2, 2500]);
  assert.ok(stats.readsToday > 0 && stats.poolLimit > 0 && stats.storesPerDay > 0 && stats.readsPerDay > 0);
  assert.ok(!("shared" in stats) && !("reported" in stats));
  const listed = await (await get("pictures")).json();
  assert.deepEqual(listed.pictures.map(p => p.hash).sort(), [first, second].sort());
  assert.ok(listed.pictures.every(p => p.owner === owner && p.size > 0 && p.uploaded > 0));
  assert.equal(listed.cursor, null);
});

test("web pages cannot drive the locker, and an IPv6 network counts as one address", async () => {
  const e = env(), now = e.HAVEN_CONTENT.now;
  const hash = await uploaded(e, jpeg(2000), now), get = await ticket("get", hash, {}, now);
  for (const header of [["Origin", "https://evil.example"], ["Sec-Fetch-Site", "cross-site"]])
  {
    const request = new Request(get, { headers: { [header[0]]: header[1], "CF-Connecting-IP": "198.51.100.9" } });
    assert.equal((await handleContent(request, e, now)).status, 403);
  }
  // The local admin page proxies with Node's fetch, which sends Sec-Fetch-Mode: cors on its own.
  const node = new Request(get, { headers: { "Sec-Fetch-Mode": "cors", "CF-Connecting-IP": "198.51.100.9" } });
  assert.equal((await handleContent(node, e, now)).status, 200);
  assert.equal(addressKey("2001:db8:1:2:aaaa::1"), addressKey("2001:db8:1:2:ffff:1:2:3"));
  assert.notEqual(addressKey("2001:db8:1:2::1"), addressKey("2001:db8:1:3::1"));
  assert.equal(addressKey("198.51.100.4"), "198.51.100.4");
});

test("an uploader deletes a picture at once; the operator can delete without blocking", async () => {
  const e = { ...env(), HAVEN_ADMIN_KEY: "a".repeat(40) }, now = e.HAVEN_CONTENT.now;
  const hash = await uploaded(e, jpeg(2500), now);
  // A record the old gallery left for it goes with it.
  e.budget.storage.map.set(`pic:${hash}`, { sharedAt: now }); e.budget.storage.map.set(`reported:${hash}`, now);
  // Someone else's owner key cannot delete it.
  assert.equal((await post(e, await ticket("delete", hash, { owner: "f".repeat(16) }, now), now)).status, 403);
  assert.ok(e.HAVEN_CONTENT.objects.has(`pictures/${hash}`));
  const deleted = await post(e, await ticket("delete", hash, { owner }, now), now);
  assert.equal(deleted.status, 200);
  assert.equal((await deleted.json()).outcome, "deleted");
  assert.ok(!e.HAVEN_CONTENT.objects.has(`pictures/${hash}`));
  assert.ok(!e.budget.storage.map.has(`pic:${hash}`) && !e.budget.storage.map.has(`reported:${hash}`));
  // Asking again is harmless, and it is not blocked: the owner may upload it again.
  assert.equal((await (await post(e, await ticket("delete", hash, { owner }, now), now)).json()).outcome, "gone");
  assert.equal(await uploaded(e, jpeg(2500), now), hash);
  assert.ok(e.HAVEN_CONTENT.objects.has(`pictures/${hash}`));
  // The operator's Delete leaves no block either; Remove does.
  const admin = path => handleContent(new Request(`https://relay.example/content/admin/${path}`, { method: "POST", headers: { Authorization: "Bearer " + "a".repeat(40) } }), e, now);
  assert.equal((await admin(`delete/${hash}`)).status, 200);
  assert.ok(!e.HAVEN_CONTENT.objects.has(`pictures/${hash}`) && !e.HAVEN_CONTENT.objects.has(`deny/${hash}`));
  assert.equal(await uploaded(e, jpeg(2500), now), hash);
  assert.ok(e.HAVEN_CONTENT.objects.has(`pictures/${hash}`));
});

test("deleting gives the stored bytes back to the owner's allowance and the pool, but not the day's upload", async () => {
  const e = { ...env(), HAVEN_ADMIN_KEY: "a".repeat(40) }, now = e.HAVEN_CONTENT.now;
  const first = await uploaded(e, jpeg(3000), now), second = await uploaded(e, jpeg(4000), now);
  assert.equal(e.HAVEN_CONTENT.objects.get(`pictures/${first}`).customMetadata.day, "2026-10-26");
  let usage = await e.budget.usage(owner, now);
  assert.deepEqual([usage.ownerBytes, usage.uploadsToday, usage.poolBytes], [7000, 2, 7000]);
  // The answer to a delete carries the allowance as it now stands.
  const deleted = await post(e, await ticket("delete", first, { owner }, now), now);
  assert.equal(deleted.headers.get("X-Haven-Usage"), `4000,${budget.ownerBytes},2,${budget.ownerUploadsPerDay},4000,${budget.storedBytes}`);
  // Deleting again gives nothing more back.
  await post(e, await ticket("delete", first, { owner }, now), now);
  usage = await e.budget.usage(owner, now);
  assert.deepEqual([usage.ownerBytes, usage.uploadsToday, usage.poolBytes], [4000, 2, 4000]);
  // The operator's delete gives the bytes back too; an older picture without its upload day counts from when it was stored.
  delete e.HAVEN_CONTENT.objects.get(`pictures/${second}`).customMetadata.day;
  const admin = path => handleContent(new Request(`https://relay.example/content/admin/${path}`, { method: "POST", headers: { Authorization: "Bearer " + "a".repeat(40) } }), e, now);
  assert.equal((await admin(`remove/${second}`)).status, 200);
  usage = await e.budget.usage(owner, now);
  assert.deepEqual([usage.ownerBytes, usage.uploadsToday, usage.poolBytes], [0, 2, 0]);
  // A refund never goes below nothing, and outside the window does nothing.
  await e.budget.refund(owner, 999999, "2026-10-26", "2026-10-26", now);
  await e.budget.refund(owner, 10, "2025-01-01", "2025-01-01", now);
  usage = await e.budget.usage(owner, now);
  assert.deepEqual([usage.ownerBytes, usage.poolBytes], [0, 0]);
});

test("a verified upload records its uploader, and an older picture learns it when its owner stores it again", async () => {
  const e = { ...env(), HAVEN_ADMIN_KEY: "a".repeat(40) }, now = e.HAVEN_CONTENT.now, bytes = jpeg(2200), hash = await sha(bytes);
  const put = async by => handleContent(new Request(await ticket("put", hash, { size: bytes.length, owner, agreement: 1, by }, now), { method: "PUT", body: bytes, headers: { "Content-Length": String(bytes.length) } }), e, now);
  // Stored without an uploader (before accounts), then stored again by its verified owner.
  assert.equal((await put("")).status, 201);
  assert.equal(e.HAVEN_CONTENT.objects.get(`pictures/${hash}`).customMetadata.character, undefined);
  assert.equal((await put("4001")).status, 200);
  assert.equal(e.HAVEN_CONTENT.objects.get(`pictures/${hash}`).customMetadata.character, "4001");
  const listed = await (await handleContent(new Request("https://relay.example/content/admin/pictures", { headers: { Authorization: "Bearer " + "a".repeat(40) } }), e, now)).json();
  assert.equal(listed.pictures[0].character, "4001");
  // A forged or altered uploader breaks the signature.
  const forged = (await ticket("put", hash, { size: bytes.length, owner, agreement: 1, by: "4001" }, now)).replace("by=4001", "by=4002");
  assert.equal((await handleContent(new Request(forged, { method: "HEAD" }), e, now)).status, 404);
  assert.equal((await handleContent(new Request((await ticket("put", hash, { size: 1, owner, agreement: 1 }, now)).replace("&sig", "&by=01&sig"), { method: "HEAD" }), e, now)).status, 404);
});

// Custom music: Ogg Vorbis songs under music/, on the same budget and allowance as pictures.
const ogg = size => {
  const b = new Uint8Array(size); b.set([0x4F, 0x67, 0x67, 0x53]); b[28] = 1; b.set([0x76, 0x6F, 0x72, 0x62, 0x69, 0x73], 29);
  for (let i = 35; i < size; i++) b[i] = i % 241; return b;
};
async function uploadedSong(e, bytes, now, by = owner) {
  const hash = await sha(bytes);
  const response = await handleContent(new Request(await ticket("put", hash, { size: bytes.length, owner: by, agreement: 1, media: "music" }, now),
    { method: "PUT", body: bytes, headers: { "Content-Length": String(bytes.length) } }), e, now);
  return [hash, response.status];
}

test("song tickets sign with a music line, and match Haven.Core byte for byte", async () => {
  assert.equal(canonical("put", "b".repeat(64), 12345, owner, 1, 1793000600, "", "music"), ["put", "b".repeat(64), "12345", owner, "1", "1793000600", "music"].join("\n"));
  assert.equal(await sign("k".repeat(40), canonical("put", "b".repeat(64), 12345, owner, 1, 1793000600, "4001", "music")), "RohGXrQAvsJoW2ZRPKsJtJxSHnNgTZg9BH7p8BxHtkg");
});

test("songs store under music/, must be Ogg Vorbis within 8 MB, and share the owner's allowance with pictures", async () => {
  const e = env(), now = e.HAVEN_CONTENT.now, bytes = ogg(5000);
  const [hash, status] = await uploadedSong(e, bytes, now);
  assert.equal(status, 201);
  assert.ok(e.HAVEN_CONTENT.objects.has(`music/${hash}`) && !e.HAVEN_CONTENT.objects.has(`pictures/${hash}`));
  // A picture ticket cannot fetch a song.
  assert.equal((await handleContent(new Request(await ticket("get", hash, {}, now)), e, now)).status, 404);
  const got = await handleContent(new Request(await ticket("get", hash, { media: "music" }, now)), e, now);
  assert.equal(got.status, 200);
  assert.equal(got.headers.get("Content-Type"), "audio/ogg");
  assert.equal(await sha(await got.arrayBuffer()), hash);
  // A picture ticket's signature does not cover a song (the media line), so switching media is refused.
  assert.equal((await handleContent(new Request((await ticket("get", hash, {}, now)).replace("&sig=", "&media=music&sig=")), e, now)).status, 404);
  // JPEG is not a song, and a song larger than 8 MB has no ticket.
  assert.equal((await uploadedSong(e, jpeg(4000), now))[1], 415);
  assert.equal((await handleContent(new Request(await ticket("put", "e".repeat(64), { size: limits.musicBytes + 1, owner, agreement: 1, media: "music" }, now), { method: "HEAD" }), e, now)).status, 400);
  assert.equal((await handleContent(new Request(await ticket("put", "e".repeat(64), { size: limits.pictureBytes + 1, owner, agreement: 1, media: "music" }, now), { method: "HEAD" }), e, now)).status, 404);
  // One allowance: a picture and a song both count.
  await uploaded(e, jpeg(3000), now);
  assert.equal((await e.budget.usage(owner, now)).ownerBytes, 5000 + 3000);
});

test("the operator sees and plays stored songs, and only their uploader deletes them", async () => {
  const e = { ...env(), HAVEN_ADMIN_KEY: "a".repeat(40) }, now = e.HAVEN_CONTENT.now;
  const [hash] = await uploadedSong(e, ogg(4000), now);
  const get = path => handleContent(new Request(`https://relay.example/content/admin/${path}`, { headers: operator }), e, now);
  const songs = await (await get("pictures?media=music")).json();
  assert.deepEqual(songs.pictures.map(s => [s.hash, s.owner, s.size]), [[hash, owner, 4000]]);
  assert.deepEqual((await (await get("pictures")).json()).pictures, []);
  assert.equal((await get(`picture/${hash}?media=music`)).headers.get("Content-Type"), "audio/ogg");
  // Only its uploader deletes it, through a song ticket, and the bytes go back to the allowance.
  assert.equal((await post(e, await ticket("delete", hash, { owner: "f".repeat(16), media: "music" }, now), now)).status, 403);
  assert.equal((await (await post(e, await ticket("delete", hash, { owner, media: "music" }, now), now)).json()).outcome, "deleted");
  assert.ok(!e.HAVEN_CONTENT.objects.has(`music/${hash}`));
  assert.equal((await e.budget.usage(owner, now)).ownerBytes, 0);
});

test("clearing and removing reach songs too", async () => {
  const e = { ...env(), HAVEN_ADMIN_KEY: "a".repeat(40) }, now = e.HAVEN_CONTENT.now;
  const [song] = await uploadedSong(e, ogg(3000), now), [other] = await uploadedSong(e, ogg(3100), now);
  await uploaded(e, jpeg(1500), now);
  const admin = path => handleContent(new Request(`https://relay.example/content/admin/${path}`, { method: "POST", headers: { Authorization: "Bearer " + "a".repeat(40) } }), e, now);
  assert.equal((await admin(`remove/${song}`)).status, 200);
  assert.ok(!e.HAVEN_CONTENT.objects.has(`music/${song}`) && e.HAVEN_CONTENT.objects.has(`deny/${song}`));
  assert.equal((await uploadedSong(e, ogg(3000), now))[1], 410);
  assert.equal((await (await admin("clear")).json()).deleted, 2);
  assert.ok(!e.HAVEN_CONTENT.objects.has(`music/${other}`));
});

// Custom furniture: zip packages under furniture/, on the same budget and allowance.
const zipBytes = size => { const b = new Uint8Array(size); b.set([0x50, 0x4B, 0x03, 0x04]); for (let i = 4; i < size; i++) b[i] = i % 239; return b; };

test("furniture tickets sign with a furniture line, matching Haven.Core", async () => {
  // Haven.Core FurniturePackages.MaxBytes and ContentLocker.PartBytes.
  assert.equal(limits.furnitureBytes, 250_000_000);
  assert.equal(limits.partBytes, 16 * 1024 * 1024);
  assert.equal(await sign("k".repeat(40), canonical("put", "b".repeat(64), 12345, owner, 1, 1793000600, "4001", "furniture")), "GAmm_JIJWmao-ds75DlBifajL8Qhz4ekItv7gxNo0j8");
});

test("furniture stores under furniture/, must be a zip within the allowance, and deletes", async () => {
  const e = { ...env(), HAVEN_ADMIN_KEY: "a".repeat(40) }, now = e.HAVEN_CONTENT.now, bytes = zipBytes(6000), hash = await sha(bytes);
  const put = await ticket("put", hash, { size: bytes.length, owner, agreement: 1, media: "furniture" }, now);
  assert.equal((await handleContent(new Request(put, { method: "PUT", body: bytes, headers: { "Content-Length": String(bytes.length) } }), e, now)).status, 201);
  assert.ok(e.HAVEN_CONTENT.objects.has(`furniture/${hash}`));
  const got = await handleContent(new Request(await ticket("get", hash, { media: "furniture" }, now)), e, now);
  assert.equal(got.headers.get("Content-Type"), "application/zip");
  assert.equal(await sha(await got.arrayBuffer()), hash);
  // Streamed in: a body that doesn't match its fingerprint is stored nowhere and its charge is given back.
  const other = zipBytes(5000), wrong = "c".repeat(64), before = (await e.budget.usage(owner, now)).ownerBytes;
  const mismatch = await ticket("put", wrong, { size: other.length, owner, agreement: 1, media: "furniture" }, now);
  assert.equal((await handleContent(new Request(mismatch, { method: "PUT", body: other, headers: { "Content-Length": String(other.length) } }), e, now)).status, 400);
  assert.ok(!e.HAVEN_CONTENT.objects.has(`furniture/${wrong}`));
  assert.equal((await e.budget.usage(owner, now)).ownerBytes, before);
  // Not a zip, or larger than the allowance: refused.
  const jpg = jpeg(3000);
  const bad = await ticket("put", await sha(jpg), { size: jpg.length, owner, agreement: 1, media: "furniture" }, now);
  assert.equal((await handleContent(new Request(bad, { method: "PUT", body: jpg, headers: { "Content-Length": String(jpg.length) } }), e, now)).status, 415);
  assert.equal((await handleContent(new Request(await ticket("put", "e".repeat(64), { size: limits.furnitureBytes + 1, owner, agreement: 1, media: "furniture" }, now), { method: "HEAD" }), e, now)).status, 400);
  const get = path => handleContent(new Request(`https://relay.example/content/admin/${path}`, { headers: operator }), e, now);
  assert.deepEqual((await (await get("pictures?media=furniture")).json()).pictures.map(p => p.hash), [hash]);
  assert.deepEqual((await (await get("pictures?media=music")).json()).pictures, []);
  assert.equal((await (await post(e, await ticket("delete", hash, { owner, media: "furniture" }, now), now)).json()).outcome, "deleted");
  assert.ok(!e.HAVEN_CONTENT.objects.has(`furniture/${hash}`));
});

test("a large furniture package comes in parts, checked whole, its charge given back at most once", async () => {
  const saved = { ...limits };
  limits.partBytes = 1000; limits.singleBytes = 2500;
  try {
    const e = env(), now = e.HAVEN_CONTENT.now, bytes = zipBytes(2600), hash = await sha(bytes);
    const used = async () => (await e.budget.usage(owner, now)).ownerBytes;
    const put = await ticket("put", hash, { size: bytes.length, owner, agreement: 1, media: "furniture" }, now);
    const call = (url, init) => handleContent(new Request(url, init), e, now);
    const send = (url, body) => call(url, { method: "PUT", body, headers: { "Content-Length": String(body.length) } });
    // Too large for one request, and a package that fits one isn't started in parts.
    assert.equal((await send(put, bytes)).status, 413);
    const small = zipBytes(900), smallPut = await ticket("put", await sha(small), { size: small.length, owner, agreement: 1, media: "furniture" }, now);
    assert.equal((await call(`${smallPut}&part=start`, { method: "POST" })).status, 400);
    // Started: the whole package is charged at once.
    const started = await call(`${put}&part=start`, { method: "POST" });
    assert.equal(started.status, 200);
    const { upload, partBytes } = await started.json();
    assert.equal(partBytes, 1000);
    assert.equal(await used(), bytes.length);
    const at = n => `${put}&part=${n}&upload=${encodeURIComponent(upload)}`;
    // Each part its exact size; the first must begin a zip; no part past the last.
    assert.equal((await send(at(1), bytes.slice(0, 999))).status, 400);
    assert.equal((await send(at(4), bytes.slice(0, 1))).status, 400);
    const notZip = jpeg(1000);
    assert.equal((await send(at(1), notZip)).status, 415);
    const parts = [];
    for (let n = 1; n <= 3; n++) {
      const answer = await send(at(n), bytes.slice((n - 1) * 1000, n * 1000));
      assert.equal(answer.status, 200);
      parts.push(await answer.json());
    }
    assert.equal((await call(at("done"), { method: "POST", body: JSON.stringify({ parts: parts.slice(0, 2) }) })).status, 400);
    assert.equal((await call(at("done"), { method: "POST", body: JSON.stringify({ parts }) })).status, 201);
    const stored = e.HAVEN_CONTENT.objects.get(`furniture/${hash}`);
    assert.equal(await sha(stored.bytes), hash);
    assert.equal(stored.customMetadata.owner, owner);
    assert.equal(await used(), bytes.length);
    // Settled: finishing or abandoning it again changes nothing.
    assert.equal((await call(at("done"), { method: "POST", body: JSON.stringify({ parts }) })).status, 400);
    assert.equal((await call(at("abort"), { method: "POST" })).status, 400);
    assert.equal(await used(), bytes.length);
    // Already stored: starting again says so.
    assert.equal((await (await call(`${put}&part=start`, { method: "POST" })).json()).outcome, "stored");

    // Parts that don't make the ticket's fingerprint: put together, found wrong, deleted, refunded.
    const other = zipBytes(2700), wrong = "c".repeat(64);
    const wrongPut = await ticket("put", wrong, { size: other.length, owner, agreement: 1, media: "furniture" }, now);
    const w = (await (await call(`${wrongPut}&part=start`, { method: "POST" })).json()).upload;
    const wrongParts = [];
    for (let n = 1; n <= 3; n++) wrongParts.push(await (await send(`${wrongPut}&part=${n}&upload=${encodeURIComponent(w)}`, other.slice((n - 1) * 1000, n * 1000))).json());
    assert.equal((await call(`${wrongPut}&part=done&upload=${encodeURIComponent(w)}`, { method: "POST", body: JSON.stringify({ parts: wrongParts }) })).status, 400);
    assert.ok(!e.HAVEN_CONTENT.objects.has(`furniture/${wrong}`));
    assert.equal(await used(), bytes.length);

    // Abandoned: refunded once, and its parts gone.
    const third = zipBytes(2800), thirdPut = await ticket("put", await sha(third), { size: third.length, owner, agreement: 1, media: "furniture" }, now);
    const t = (await (await call(`${thirdPut}&part=start`, { method: "POST" })).json()).upload;
    assert.equal(await used(), bytes.length + third.length);
    assert.equal((await call(`${thirdPut}&part=abort&upload=${encodeURIComponent(t)}`, { method: "POST" })).status, 200);
    assert.equal((await call(`${thirdPut}&part=abort&upload=${encodeURIComponent(t)}`, { method: "POST" })).status, 400);
    assert.equal(await used(), bytes.length);
    assert.equal(e.HAVEN_CONTENT.uploads.size, 0);
    // Parts are only for furniture, and only with an upload ticket.
    const picture = await ticket("put", hash, { size: bytes.length, owner, agreement: 1 }, now);
    assert.equal((await call(`${picture}&part=start`, { method: "POST" })).status, 404);
  } finally { Object.assign(limits, saved); }
});
