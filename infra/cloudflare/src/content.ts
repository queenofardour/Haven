// The content locker (see Haven.Core ContentLocker): custom content stored once in R2 under its SHA-256
// fingerprint: JPEG pictures under pictures/, Ogg Vorbis songs under music/ and furniture packages (zip) under
// furniture/ (tickets for those say media=music or media=furniture). The
// relay signs short-lived tickets; this Worker checks them and moves the bytes, so file data never passes through the
// relay.
//
// Signed (tickets from the relay): get, put and delete. Nothing is public: there is no community gallery (it went with
// sharing, reports and review), so a stored item is reached only through a ticket for it. The operator's admin routes
// (HAVEN_ADMIN_KEY) list, fetch, delete, remove and clear.
import type { Held, Stats, Usage } from "./content-budget";

export interface ContentBudgetApi {
  read(n: number, now: number): Promise<boolean>;
  store(owner: string, size: number, agreement: number, upload: boolean, now: number): Promise<string | null>;
  usage(owner: string, now: number): Promise<Usage>;
  forget(hash: string): Promise<void>;
  refund(owner: string, size: number, uploadedDay: string | null, storedDay: string, now: number): Promise<void>;
  hold(upload: string, owner: string, size: number, now: number): Promise<void>;
  release(upload: string): Promise<Held | null>;
  clear(): Promise<void>;
  stats(now: number): Promise<Stats>;
}
export interface ContentRateApi { limit(options: { key: string }): Promise<{ success: boolean }> }
// Every part must be present, or the locker stays closed: the bucket, the ticket key, the account-wide budget
// (content-budget.ts) and the per-address rate limit. Without the admin key only the operator's routes are unavailable.
export interface ContentEnv { HAVEN_CONTENT?: R2Bucket; HAVEN_CONTENT_KEY?: string; HAVEN_ADMIN_KEY?: string; budget?: ContentBudgetApi; rate?: ContentRateApi }

const hashPattern = /^[0-9a-f]{64}$/;
const ownerPattern = /^[0-9a-f]{16}$/;
export const limits = {
  pictureBytes: 2 * 1024 * 1024,
  // Haven.Core MusicEncoder.MaxBytes.
  musicBytes: 8 * 1024 * 1024,
  // Haven.Core FurniturePackages.MaxBytes (custom furniture and pose animations): the owner's whole allowance, since the
  // user wants only that to bound them (3 November 2026). Cloudflare takes at most 100 MB a request, so a package
  // comes in one request up to singleBytes, or in parts of partBytes (Haven.Core ContentLocker.PartBytes; R2 multipart:
  // every part but the last that size, at least 5 MiB). Streamed, never held.
  furnitureBytes: 250 * 1000 ** 2,
  singleBytes: 90 * 1024 * 1024,
  partBytes: 16 * 1024 * 1024,
  // R2's lifecycle rules delete pictures/, music/ and furniture/ 30 days after they were stored; anything still in use is
  // re-stored at most weekly, which restarts that clock, so only what no gathering has used for 30 days goes.
  refreshMs: 7 * 86_400_000,
  ticketSeconds: 15 * 60,
};
const headers = { "X-Robots-Tag": "noindex, nofollow, noarchive", "Referrer-Policy": "no-referrer", "X-Content-Type-Options": "nosniff" };
const reply = (body: BodyInit | null, status = 200, extra: Record<string, string> = {}) =>
  new Response(body, { status, headers: { ...headers, "Cache-Control": "private, no-store", ...extra } });
const json = (value: unknown, status = 200, extra: Record<string, string> = {}) => reply(JSON.stringify(value), status, { "Content-Type": "application/json", ...extra });

// A UTC day as the ledger counts it (content-budget.ts dayOf; only types are imported from there, so Node's tests load
// this file as it is).
const dayOf = (time: number) => new Date(time).toISOString().slice(0, 10);

// Deletes a stored object and gives its bytes back to its owner's allowance and the shared pool (ContentLedger.refund).
// The upload day is recorded on objects stored since 0.37.5's Worker; older ones count from when they were last stored.
async function deleteStored(bucket: R2Bucket, budget: ContentBudgetApi, key: string, object: R2Object, now: number) {
  await bucket.delete(key);
  const meta = object.customMetadata ?? {};
  await budget.refund(meta.owner ?? "", object.size, meta.day ?? dayOf(object.uploaded.getTime()), dayOf(object.uploaded.getTime()), now);
}

// by: the uploading character, when the relay verified it through its account (Haven.Core ContentLocker.Canonical).
// A song's ticket ends with the line "music" and a furnishing's with "furniture"; a picture's has none, so picture
// tickets sign as they always have.
export type Media = "picture" | "music" | "furniture";
export const isMedia = (value: string): value is Media => value === "picture" || value === "music" || value === "furniture";
export function canonical(method: string, hash: string, size: number, owner: string, agreement: number, expires: number, by = "", media: Media = "picture"): string {
  return [method, hash, String(size), owner, String(agreement), String(expires), ...(by ? [by] : []), ...(media === "picture" ? [] : [media])].join("\n");
}
const folders: Record<Media, string> = { picture: "pictures/", music: "music/", furniture: "furniture/" };
const folder = (media: Media) => folders[media];
const contentType = (media: Media) => media === "music" ? "audio/ogg" : media === "furniture" ? "application/zip" : "image/jpeg";
export async function sign(key: string, text: string): Promise<string> {
  const crypto_key = await crypto.subtle.importKey("raw", new TextEncoder().encode(key), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = new Uint8Array(await crypto.subtle.sign("HMAC", crypto_key, new TextEncoder().encode(text)));
  let binary = ""; for (const b of mac) binary += String.fromCharCode(b);
  return btoa(binary).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
}
function same(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let difference = 0; for (let i = 0; i < a.length; i++) difference |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return difference === 0;
}
const hex = (bytes: ArrayBuffer) => [...new Uint8Array(bytes)].map(x => x.toString(16).padStart(2, "0")).join("");
const isJpeg = (bytes: Uint8Array) => bytes.length >= 4 && bytes[0] === 0xFF && bytes[1] === 0xD8 && bytes[2] === 0xFF;
// An Ogg stream whose first page carries the Vorbis identification header.
const isOggVorbis = (bytes: Uint8Array) => bytes.length >= 36 && bytes[0] === 0x4F && bytes[1] === 0x67 && bytes[2] === 0x67 && bytes[3] === 0x53
  && bytes[28] === 1 && [0x76, 0x6F, 0x72, 0x62, 0x69, 0x73].every((b, i) => bytes[29 + i] === b);
// A zip archive's local file header: the furniture package Haven builds (its contents are checked by every PC).
const isZip = (bytes: Uint8Array) => bytes.length >= 4 && bytes[0] === 0x50 && bytes[1] === 0x4B && bytes[2] === 0x03 && bytes[3] === 0x04;
const accepted = (media: Media, bytes: Uint8Array) => media === "music" ? isOggVorbis(bytes) : media === "furniture" ? isZip(bytes) : isJpeg(bytes);
const usageHeader = (u: Usage) => ({ "X-Haven-Usage": [u.ownerBytes, u.ownerLimit, u.uploadsToday, u.uploadsPerDay, u.poolBytes, u.poolLimit].join(",") });

/**
 * The key per-address limits use: an IPv4 address, or an IPv6 address's /64 (one subscriber's network), so rotating
 * through one network's addresses earns no extra allowance.
 */
export function addressKey(address: string | null): string {
  if (!address) return "unknown";
  if (!address.includes(":")) return address;
  const [head, tail = ""] = address.toLowerCase().split("::");
  const left = head ? head.split(":") : [], right = tail ? tail.split(":") : [];
  const groups = address.includes("::") ? [...left, ...Array(Math.max(0, 8 - left.length - right.length)).fill("0"), ...right] : left;
  return groups.slice(0, 4).map(g => g.padStart(4, "0")).join(":") + "::/64";
}

/**
 * Requests a web page makes carry Origin or Sec-Fetch-Site; Haven's own client sends neither. Sec-Fetch-Mode alone
 * is not a browser: Node's fetch (the local admin page) adds it to every request.
 */
export const fromBrowser = (request: Request) => request.headers.has("Origin") || request.headers.has("Sec-Fetch-Site");

export async function handleContent(request: Request, env: ContentEnv, now = Date.now()): Promise<Response> {
  const { HAVEN_CONTENT: bucket, HAVEN_CONTENT_KEY: key, budget, rate } = env;
  if (!bucket || !key || key.length < 32 || !budget || !rate) return reply("Not found", 404);
  // Other websites cannot spend the read budget, or anything else, through their visitors' browsers.
  if (fromBrowser(request)) return reply("Forbidden", 403);
  const address = addressKey(request.headers.get("CF-Connecting-IP"));
  // A flood from one address is turned away before anything is checked or stored.
  if (!(await rate.limit({ key: address })).success) return reply("Too many requests", 429);
  const url = new URL(request.url);
  const parts = url.pathname.split("/").filter(Boolean), query = url.searchParams;
  if (parts[0] !== "content" || parts.length < 2) return reply("Not found", 404);
  if (parts[1] === "admin") return admin(request, env, parts.slice(2), now);
  const hash = parts[1];
  if (parts.length !== 2 || !hashPattern.test(hash)) return reply("Not found", 404);
  const op = query.get("op") ?? "";
  const named = query.get("media") ?? "picture";
  if (!isMedia(named)) return reply("Not found", 404);
  const media: Media = named;
  const objectKey = folder(media) + hash, type = contentType(media);

  const owner = query.get("owner") ?? "", sig = query.get("sig") ?? "", by = query.get("by") ?? "";
  if (by && !/^[1-9][0-9]{0,19}$/.test(by)) return reply("Not found", 404);
  const size = Number(query.get("size")), agreement = Number(query.get("agreement")), expires = Number(query.get("exp"));
  if (!Number.isSafeInteger(size) || !Number.isSafeInteger(agreement) || !Number.isSafeInteger(expires)) return reply("Not found", 404);
  const allowed = op === "get" ? ["GET"] : op === "put" ? ["HEAD", "PUT", "POST"] : op === "delete" ? ["POST"] : [];
  if (!allowed.includes(request.method)) return reply("Method not allowed", 405);
  const seconds = Math.floor(now / 1000);
  if (expires < seconds || expires > seconds + limits.ticketSeconds) return reply("Ticket expired", 403);
  if (!same(sig, await sign(key, canonical(op, hash, size, owner, agreement, expires, by, media)))) return reply("Not found", 404);
  if (op !== "get" && !ownerPattern.test(owner)) return reply("Invalid ticket", 400);

  // The uploader deletes a picture, song or furnishing at once. Only its own: the owner key comes from the relay, which
  // issues delete tickets only to a verified character's content session. forget drops any record the old community
  // gallery left for it.
  if (op === "delete") {
    if (!(await budget.read(1, now))) return reply("Haven's content storage is busy. Try again tomorrow.", 503);
    const object = await bucket.head(objectKey);
    if (!object) { await budget.forget(hash); return json({ outcome: "gone" }); }
    if (object.customMetadata?.owner !== owner) return reply("Only the uploader can delete it.", 403);
    await deleteStored(bucket, budget, objectKey, object, now);
    await budget.forget(hash);
    // The allowance as it now stands, so Haven's meters show the bytes given back.
    return json({ outcome: "deleted" }, 200, usageHeader(await budget.usage(owner, now)));
  }
  const maximum = media === "music" ? limits.musicBytes : media === "furniture" ? limits.furnitureBytes : limits.pictureBytes;
  if (op === "put" && (size <= 0 || size > maximum || agreement < 1)) return reply("Invalid ticket", 400);
  // A furniture package sent in parts: the upload ticket covers starting, each part, finishing and abandoning it.
  const part = query.get("part");
  if (part !== null) {
    if (op !== "put" || media !== "furniture") return reply("Not found", 404);
    return multipart(request, bucket, budget, part, query.get("upload") ?? "", { objectKey, hash, size, owner, agreement, type, by }, now);
  }
  if (request.method === "POST") return reply("Method not allowed", 405);
  // Two reads follow either way: the deny list, then the content itself.
  if (!(await budget.read(2, now))) return reply("Haven's content storage is busy. Try again tomorrow.", 503);
  if (await bucket.head(`deny/${hash}`)) return reply("Removed", 410);

  if (op === "get") {
    const object = await bucket.get(objectKey);
    if (!object) return reply("Not found", 404);
    return reply(object.body, 200, { "Content-Type": type, "Cache-Control": "private, max-age=86400, immutable" });
  }

  const existing = await bucket.head(objectKey);
  if (existing) {
    // Still in use: restart the 30-day lifecycle clock at most weekly by storing the same bytes again, when the budget
    // allows; otherwise the picture simply keeps its current clock.
    // A picture stored before its uploader was known learns it the next time its owner, now verified, stores it.
    const attribute = !!by && !existing.customMetadata?.character && existing.customMetadata?.owner === owner;
    if ((now - existing.uploaded.getTime() > limits.refreshMs || attribute) && await budget.read(1, now)
      && await budget.store(owner, existing.size, agreement, false, now) === null) {
      const body = await bucket.get(objectKey);
      if (body) await bucket.put(objectKey, body.body.pipeThrough(new FixedLengthStream(existing.size)), { httpMetadata: { contentType: type },
        customMetadata: { ...existing.customMetadata, ...(attribute ? { character: by } : {}) } });
    }
    return reply(null, 200, usageHeader(await budget.usage(owner, now)));
  }
  if (request.method === "HEAD") return reply(null, 404, usageHeader(await budget.usage(owner, now)));

  // The body is read only when its declared length is exactly the ticket's, so nothing larger is ever accepted.
  if (request.headers.get("Content-Length") !== String(size)) return reply("Size does not match the ticket", 400);
  const meta = { owner, agreement: String(agreement), day: dayOf(now), ...(by ? { character: by } : {}) };
  if (media === "furniture") {
    if (size > limits.singleBytes) return reply("Send this package in parts", 413);
    return storeStreamed(request, bucket, budget, objectKey, hash, size, owner, agreement, type, meta, now);
  }
  const data = await request.arrayBuffer();
  if (data.byteLength !== size) return reply("Size does not match the ticket", 400);
  if (hex(await crypto.subtle.digest("SHA-256", data)) !== hash) return reply("Fingerprint does not match", 400);
  if (!accepted(media, new Uint8Array(data))) return reply(media === "music" ? "Only Ogg Vorbis songs are accepted" : "Only JPEG pictures are accepted", 415);
  const refused = await budget.store(owner, size, agreement, true, now);
  if (refused) return reply(refused, 429, usageHeader(await budget.usage(owner, now)));
  // The uploader's character, when known, lets the operator see whose picture it is (and ban that account).
  // The upload's day, so deleting it gives its bytes back to the right day of its owner's allowance.
  await bucket.put(objectKey, data, { httpMetadata: { contentType: type }, customMetadata: meta });
  return reply(null, 201, usageHeader(await budget.usage(owner, now)));
}

/**
 * A furniture package (custom furniture or pose animations, up to 90 MB) is more than a Worker should hold, so it is
 * streamed into R2: its first bytes are checked to be a zip before anything is charged, then R2 checks the length and
 * the fingerprint as it stores, and refuses a mismatch, whose charge is given back.
 */
async function storeStreamed(request: Request, bucket: R2Bucket, budget: ContentBudgetApi, objectKey: string, hash: string, size: number,
  owner: string, agreement: number, type: string, meta: Record<string, string>, now: number): Promise<Response> {
  const body = await zipBody(request);
  if (!body) return reply("Only Haven furniture packages are accepted", 415);
  const refused = await budget.store(owner, size, agreement, true, now);
  if (refused) { await body.cancel(); return reply(refused, 429, usageHeader(await budget.usage(owner, now))); }
  try {
    await bucket.put(objectKey, body.pipeThrough(new FixedLengthStream(size)), { sha256: hash, httpMetadata: { contentType: type }, customMetadata: meta });
  } catch {
    await bucket.delete(objectKey).catch(() => undefined);
    await budget.refund(owner, size, dayOf(now), dayOf(now), now);
    return reply("The upload does not match its ticket", 400);
  }
  return reply(null, 201, usageHeader(await budget.usage(owner, now)));
}

/** A request's body, unread past its first bytes, if they begin a zip; otherwise null (and the body is cancelled). */
async function zipBody(request: Request): Promise<ReadableStream<Uint8Array> | null> {
  if (!request.body) return null;
  const reader = request.body.getReader();
  const head: Uint8Array[] = [];
  let got = 0;
  while (got < 4) {
    const { done, value } = await reader.read();
    if (done) break;
    head.push(value); got += value.byteLength;
  }
  const start = new Uint8Array(got);
  head.reduce((at, part) => (start.set(part, at), at + part.byteLength), 0);
  if (!isZip(start)) { await reader.cancel(); return null; }
  return new ReadableStream<Uint8Array>({
    start(controller) { for (const part of head) controller.enqueue(part); },
    async pull(controller) { const { done, value } = await reader.read(); if (done) controller.close(); else controller.enqueue(value); },
    cancel(reason) { return reader.cancel(reason); },
  });
}

/** How many parts a package of size bytes comes in, and how large part n (from 1) is. */
export const partCount = (size: number) => Math.ceil(size / limits.partBytes);
export const partSize = (size: number, n: number) => n < partCount(size) ? limits.partBytes : size - (partCount(size) - 1) * limits.partBytes;
const uploadPattern = /^[\x21-\x7e]{1,1024}$/;

type Upload = { objectKey: string; hash: string; size: number; owner: string; agreement: number; type: string; by: string };

/**
 * A furniture package in parts: POST part=start charges the whole package to its owner's allowance and begins an R2
 * multipart upload; PUT part=<n> streams part n (its exact size, the first one a zip's start); POST part=done with the
 * parts' etags assembles it, then reads it back to check its SHA-256 (R2 checks each part, not the whole); POST
 * part=abort gives up. The ledger holds each begun upload until it is finished or abandoned, so its charge is refunded
 * at most once; an upload left unfinished keeps its charge until the window passes, and R2 deletes its parts after a week.
 */
async function multipart(request: Request, bucket: R2Bucket, budget: ContentBudgetApi, part: string, upload: string, u: Upload, now: number): Promise<Response> {
  if (part === "start") {
    if (request.method !== "POST") return reply("Method not allowed", 405);
    if (u.size <= limits.partBytes) return reply("Send this package in one request", 400);
    if (!(await budget.read(2, now))) return reply("Haven's content storage is busy. Try again tomorrow.", 503);
    if (await bucket.head(`deny/${u.hash}`)) return reply("Removed", 410);
    if (await bucket.head(u.objectKey)) return json({ outcome: "stored" }, 200, usageHeader(await budget.usage(u.owner, now)));
    const refused = await budget.store(u.owner, u.size, u.agreement, true, now);
    if (refused) return reply(refused, 429, usageHeader(await budget.usage(u.owner, now)));
    const meta = { owner: u.owner, agreement: String(u.agreement), day: dayOf(now), ...(u.by ? { character: u.by } : {}) };
    const begun = await bucket.createMultipartUpload(u.objectKey, { httpMetadata: { contentType: u.type }, customMetadata: meta });
    await budget.hold(begun.uploadId, u.owner, u.size, now);
    return json({ upload: begun.uploadId, partBytes: limits.partBytes }, 200, usageHeader(await budget.usage(u.owner, now)));
  }
  if (!uploadPattern.test(upload)) return reply("Unknown upload", 400);
  const parts = partCount(u.size);
  if (part === "done" || part === "abort") {
    if (request.method !== "POST") return reply("Method not allowed", 405);
    let listed: R2UploadedPart[] = [];
    if (part === "done") {
      const text = await request.text();
      if (text.length > 64 * 1024) return reply("Invalid parts", 400);
      try { listed = (JSON.parse(text) as { parts?: unknown }).parts as R2UploadedPart[]; } catch { return reply("Invalid parts", 400); }
      if (!Array.isArray(listed) || listed.length !== parts || listed.some((p, i) => !p || p.partNumber !== i + 1 || typeof p.etag !== "string" || p.etag.length > 256))
        return reply("Invalid parts", 400);
    }
    // Settled once: a second finish or abandon of the same upload finds nothing to refund.
    const held = await budget.release(upload);
    if (!held || held.owner !== u.owner || held.size !== u.size) return reply("Unknown upload", 400);
    const begun = bucket.resumeMultipartUpload(u.objectKey, upload);
    const refund = () => budget.refund(held.owner, held.size, held.day, held.day, now);
    if (part === "abort") {
      await begun.abort().catch(() => undefined);
      await refund();
      return json({ outcome: "aborted" }, 200, usageHeader(await budget.usage(u.owner, now)));
    }
    try { await begun.complete(listed); }
    catch { await begun.abort().catch(() => undefined); await refund(); return reply("The parts could not be put together", 400); }
    const stored = await budget.read(1, now) ? await bucket.get(u.objectKey) : null;
    let good = false;
    if (stored && stored.size === u.size) {
      const digest = new crypto.DigestStream("SHA-256");
      await stored.body.pipeTo(digest);
      good = hex(await digest.digest) === u.hash;
    } else if (stored) await stored.body.cancel();
    if (!good) {
      await bucket.delete(u.objectKey);
      await refund();
      return reply(stored ? "The upload does not match its ticket" : "Haven's content storage is busy. Try again tomorrow.", stored ? 400 : 503);
    }
    return reply(null, 201, usageHeader(await budget.usage(u.owner, now)));
  }
  const n = Number(part);
  if (request.method !== "PUT") return reply("Method not allowed", 405);
  if (!Number.isSafeInteger(n) || n < 1 || n > parts) return reply("Invalid part", 400);
  const expected = partSize(u.size, n);
  if (request.headers.get("Content-Length") !== String(expected)) return reply("Size does not match the part", 400);
  const body = n === 1 ? await zipBody(request) : request.body;
  if (!body) return reply("Only Haven furniture packages are accepted", 415);
  try {
    const sent = await bucket.resumeMultipartUpload(u.objectKey, upload).uploadPart(n, body.pipeThrough(new FixedLengthStream(expected)));
    return json({ partNumber: sent.partNumber, etag: sent.etag });
  } catch { return reply("The part was not stored", 400); }
}

// The operator's routes (infra/cloudflare/admin.mjs): today's use of the budget, every stored item, fetching one,
// deleting one (its owner may upload it again) or removing one for good (deny list, then deleted). Clear starts the
// locker afresh: every stored picture, song and furnishing is deleted and the allowances reset; the deny list stays.
// Listing and fetching take ?media=music or ?media=furniture.
async function admin(request: Request, env: ContentEnv, path: string[], now: number): Promise<Response> {
  const adminKey = env.HAVEN_ADMIN_KEY, budget = env.budget!, bucket = env.HAVEN_CONTENT!;
  const given = (request.headers.get("Authorization") ?? "").replace(/^Bearer /, "");
  if (!adminKey || adminKey.length < 32 || !same(given, adminKey)) return reply("Not found", 404);
  if (request.method === "GET" && path.length === 1 && path[0] === "stats") return json(await budget.stats(now));
  const asked = new URL(request.url).searchParams.get("media") ?? "picture";
  const media: Media = isMedia(asked) ? asked : "picture";
  // Every stored picture (or song, or furnishing), 100 at a time: its size, when it was stored, the owner key that
  // uploaded it and, when known, the uploader's character.
  if (request.method === "GET" && path.length === 1 && path[0] === "pictures") {
    const cursor = new URL(request.url).searchParams.get("cursor") ?? undefined;
    const prefix = folder(media);
    const page = await bucket.list({ prefix, cursor, limit: 100, include: ["customMetadata"] });
    return json({
      pictures: page.objects.map(o => ({ hash: o.key.slice(prefix.length), size: o.size, uploaded: o.uploaded.getTime(), owner: o.customMetadata?.owner ?? "",
        character: o.customMetadata?.character ?? "" })),
      cursor: page.truncated ? page.cursor : null,
    });
  }
  const hash = path[1] ?? "";
  if (request.method === "GET" && path.length === 2 && path[0] === "picture" && hashPattern.test(hash)) {
    const object = await bucket.get(folder(media) + hash);
    return object ? reply(object.body, 200, { "Content-Type": contentType(media) }) : reply("Not found", 404);
  }
  if (request.method === "POST" && path.length === 1 && path[0] === "clear") {
    let deleted = 0;
    for (const prefix of ["pictures/", "music/", "furniture/"]) {
      let cursor: string | undefined;
      do {
        const page = await bucket.list({ prefix, cursor, limit: 1000 });
        if (page.objects.length > 0) await bucket.delete(page.objects.map(o => o.key));
        deleted += page.objects.length;
        cursor = page.truncated ? page.cursor : undefined;
      } while (cursor);
    }
    await budget.clear();
    return json({ deleted });
  }
  if (request.method !== "POST" || path.length !== 2 || !hashPattern.test(hash)) return reply("Not found", 404);
  // Deletes a picture or song without blocking it: its owner may upload it again (unlike remove).
  // Either way the stored bytes go back to their owner's allowance and the pool, as an owner's own delete does.
  const deleteAll = async () => {
    for (const key of [`pictures/${hash}`, `music/${hash}`, `furniture/${hash}`]) {
      const object = await bucket.head(key);
      if (object) await deleteStored(bucket, budget, key, object, now);
    }
    await budget.forget(hash);
  };
  if (path[0] === "delete") {
    await deleteAll();
    return json({ deleted: true });
  }
  if (path[0] === "remove") {
    await bucket.put(`deny/${hash}`, "removed by the operator", { customMetadata: { at: String(now) } });
    await deleteAll();
    return json({ removed: true });
  }
  return reply("Not found", 404);
}
