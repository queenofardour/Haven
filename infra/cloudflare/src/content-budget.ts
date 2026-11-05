// The content locker's account-wide budget, kept in one Durable Object so every check is exact. It is what bounds
// the R2 bill, whoever asks: an owner key is only what a client claims to the relay, so per-owner limits alone could
// be dodged by inventing owners.
//
// Storage bound: R2's lifecycle rule deletes a picture 30 days after it was last stored, so at any moment R2 holds
// at most the bytes stored in the last 30 days (the window allows two more for the rule's own delay). Capping those
// bytes caps storage. Every R2 operation is charged here before it runs, so reads and writes are capped per day too.
//
// Songs (music/) and furniture (furniture/) are charged to the same budget and the same per-owner allowance as pictures.
//
// It keeps only counters: the days' stores, reads and bytes, each owner's uploads, and the agreement each owner
// accepted. The community gallery it once kept (shared items, reports, public reads) is gone; dropCommunity deletes
// what that left behind, once.
export const budget = {
  windowDays: 32,
  // R2's monthly free allowances: 10 GB stored (all of it used), 1 million writes,
  // 10 million reads. Stored bytes are counted over the window as written, so what R2 holds is never more.
  storedBytes: 10 * 1000 ** 3,
  storesPerDay: 300,
  readsPerDay: 150_000,
  // Decimal like the pool (1 November 2026), so both read as set: 250 MB and 10 GB.
  ownerBytes: 250 * 1000 ** 2,
  ownerUploadsPerDay: 50,
};

export interface LedgerStore {
  get<T = unknown>(keys: string[]): Promise<Map<string, T>>;
  put<T>(entries: Record<string, T>): Promise<void>;
  delete(keys: string[]): Promise<number>;
  deleteAll(): Promise<void>;
  list<T = unknown>(options: { prefix: string; startAfter?: string; limit?: number }): Promise<Map<string, T>>;
}
type Day = { count: number; bytes: number };
/** A furniture package being sent in parts: whose, how large, and the day its charge was taken. */
export type Held = { owner: string; size: number; day: string };
export interface Usage { ownerBytes: number; ownerLimit: number; uploadsToday: number; uploadsPerDay: number; poolBytes: number; poolLimit: number }
export interface Stats { poolBytes: number; poolLimit: number; storesToday: number; storesPerDay: number; readsToday: number; readsPerDay: number }

export const dayOf = (time: number) => new Date(time).toISOString().slice(0, 10);
const window = (now: number) => Array.from({ length: budget.windowDays }, (_, i) => dayOf(now - i * 86_400_000));
const ownerPattern = /^[0-9a-f]{16}$/, dayPattern = /^\d{4}-\d{2}-\d{2}$/;

// What the old community gallery kept: `pic:<hash>` records, `reported:<hash>` marks, the `gallery:<shelf>:<inverse
// time>:<hash>` index and the day's `publicReads:` count. A record names its index entry's shelf and time.
type Leftover = { sharedAt: number; portrait?: boolean; media?: "music" | "furniture" };
const leftoverIndex = (hash: string, s: Leftover) =>
  `gallery:${s.media === "music" ? "m" : s.media === "furniture" ? "f" : s.portrait ? "p" : "l"}:${String(9_999_999_999_999 - s.sharedAt).padStart(13, "0")}:${hash}`;
const communityPrefixes = ["pic:", "reported:", "gallery:", "publicReads:"];
const communityDropped = "migrated:community";
// Durable Object storage deletes at most 128 keys a call.
const deleteBatch = 128;

export class ContentLedger {
  readonly storage: LedgerStore;
  constructor(storage: LedgerStore) { this.storage = storage; }

  /** Charges n R2 reads to today's allowance; false once it is spent. */
  async read(n: number, now: number): Promise<boolean> {
    if (!Number.isSafeInteger(n) || n < 1) return false;
    const key = `reads:${dayOf(now)}`;
    const used = (await this.storage.get<number>([key])).get(key) ?? 0;
    if (used + n > budget.readsPerDay) return false;
    await this.storage.put({ [key]: used + n });
    return true;
  }

  /**
   * Charges storing size bytes: a new upload (counted against its owner too) or the weekly re-store of a picture in
   * use. Returns null when allowed, otherwise why not.
   */
  async store(owner: string, size: number, agreement: number, upload: boolean, now: number): Promise<string | null> {
    if (!ownerPattern.test(owner) || !Number.isSafeInteger(size) || size <= 0) return "Invalid upload.";
    const days = window(now);
    const globalKeys = days.map(d => `stored:${d}`), ownerKeys = days.map(d => `owner:${owner}:${d}`);
    const found = await this.storage.get<Day>([...globalKeys, ...ownerKeys]);
    const sum = (keys: string[]) => keys.reduce((total, k) => total + (found.get(k)?.bytes ?? 0), 0);
    const today = found.get(globalKeys[0]) ?? { count: 0, bytes: 0 };
    if (today.count >= budget.storesPerDay) return "Haven's content storage has taken all it can for today. Try again tomorrow.";
    if (sum(globalKeys) + size > budget.storedBytes) return "Haven's content storage is full. Try again later.";
    const entries: Record<string, unknown> = { [globalKeys[0]]: { count: today.count + 1, bytes: today.bytes + size } };
    if (upload) {
      const mine = found.get(ownerKeys[0]) ?? { count: 0, bytes: 0 };
      if (mine.count >= budget.ownerUploadsPerDay) return "Daily upload limit reached. Try again tomorrow.";
      if (sum(ownerKeys) + size > budget.ownerBytes) return "Your custom content allowance is full.";
      entries[ownerKeys[0]] = { count: mine.count + 1, bytes: mine.bytes + size };
      // The agreement the uploader accepted, recorded with the upload it covers.
      const agreementKey = `agreement:${owner}`;
      const accepted = (await this.storage.get<{ version: number }>([agreementKey])).get(agreementKey);
      if (!accepted || accepted.version < agreement) entries[agreementKey] = { version: agreement, acceptedAt: now };
    }
    await this.storage.put(entries);
    return null;
  }

  /**
   * Gives back what a deleted upload counted, since R2 no longer holds it: its bytes in its owner's allowance (on the
   * day it was uploaded) and in the shared pool (on the day it was last stored). The days' upload and store counts
   * stay: they bound operations, and those happened. So deleting frees room but never more uploads a day, and
   * uploading and deleting in a loop can't run up writes past the daily limits.
   */
  async refund(owner: string, size: number, uploadedDay: string | null, storedDay: string, now: number): Promise<void> {
    if (!Number.isSafeInteger(size) || size <= 0) return;
    const days = new Set(window(now));
    const keys: string[] = [];
    if (dayPattern.test(storedDay) && days.has(storedDay)) keys.push(`stored:${storedDay}`);
    if (ownerPattern.test(owner) && uploadedDay && dayPattern.test(uploadedDay) && days.has(uploadedDay)) keys.push(`owner:${owner}:${uploadedDay}`);
    if (keys.length === 0) return;
    const found = await this.storage.get<Day>(keys);
    const entries: Record<string, Day> = {};
    for (const key of keys) { const day = found.get(key); if (day) entries[key] = { count: day.count, bytes: Math.max(0, day.bytes - size) }; }
    if (Object.keys(entries).length > 0) await this.storage.put(entries);
  }

  /** Notes a package begun in parts, its charge already taken by store, so finishing or abandoning it settles it once. */
  async hold(upload: string, owner: string, size: number, now: number): Promise<void> {
    await this.storage.put({ [`upload:${upload}`]: { owner, size, day: dayOf(now) } satisfies Held });
  }

  /** A begun package's note, removed as it is read: given once, so its charge is refunded at most once. */
  async release(upload: string): Promise<Held | null> {
    const key = `upload:${upload}`;
    const held = (await this.storage.get<Held>([key])).get(key);
    if (held) await this.storage.delete([key]);
    return held ?? null;
  }

  /** An owner's allowance and the shared pool, for Haven's Pictures page. */
  async usage(owner: string, now: number): Promise<Usage> {
    const days = window(now);
    const found = await this.storage.get<Day>([...days.map(d => `stored:${d}`), ...days.map(d => `owner:${owner}:${d}`)]);
    const sum = (prefix: string) => days.reduce((total, d) => total + (found.get(`${prefix}${d}`)?.bytes ?? 0), 0);
    return {
      ownerBytes: ownerPattern.test(owner) ? sum(`owner:${owner}:`) : 0, ownerLimit: budget.ownerBytes,
      uploadsToday: found.get(`owner:${owner}:${days[0]}`)?.count ?? 0, uploadsPerDay: budget.ownerUploadsPerDay,
      poolBytes: sum("stored:"), poolLimit: budget.storedBytes,
    };
  }

  /** Today's use of the account-wide budget, for the operator's admin page. */
  async stats(now: number): Promise<Stats> {
    const day = dayOf(now);
    const today = await this.storage.get<Day | number>([`stored:${day}`, `reads:${day}`]);
    const pool = await this.usage("", now);
    return {
      poolBytes: pool.poolBytes, poolLimit: pool.poolLimit,
      storesToday: (today.get(`stored:${day}`) as Day | undefined)?.count ?? 0, storesPerDay: budget.storesPerDay,
      readsToday: (today.get(`reads:${day}`) as number | undefined) ?? 0, readsPerDay: budget.readsPerDay,
    };
  }

  /**
   * A fresh start: every counter goes. The deny list lives in R2 and stays. Nothing of the community gallery can be
   * left afterwards, so its marker is put back and the next start does not look again.
   */
  async clear(): Promise<void> {
    await this.storage.deleteAll();
    await this.storage.put({ [communityDropped]: true });
  }

  /** Drops whatever the old community gallery recorded for an item, when it is deleted or removed. */
  async forget(hash: string): Promise<void> {
    const key = `pic:${hash}`;
    const shared = (await this.storage.get<Leftover>([key])).get(key);
    await this.storage.delete([key, `reported:${hash}`, ...(shared ? [leftoverIndex(hash, shared)] : [])]);
  }

  /**
   * Deletes everything the community gallery left (its records, reports, index and public read counts), once: a
   * marker says it is done, so later starts read one key and nothing more. Keys are listed and deleted a batch at a
   * time, so however much was shared, no call holds more than one batch. Returns how many keys went.
   */
  async dropCommunity(): Promise<number> {
    if ((await this.storage.get<boolean>([communityDropped])).get(communityDropped)) return 0;
    let dropped = 0;
    for (const prefix of communityPrefixes) {
      let after: string | undefined;
      for (;;) {
        const keys = [...(await this.storage.list({ prefix, startAfter: after, limit: deleteBatch })).keys()];
        if (keys.length === 0) break;
        dropped += await this.storage.delete(keys);
        if (keys.length < deleteBatch) break;
        after = keys[keys.length - 1];
      }
    }
    await this.storage.put({ [communityDropped]: true });
    return dropped;
  }
}
