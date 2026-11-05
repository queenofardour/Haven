import test from "node:test";
import assert from "node:assert/strict";
import { RecoveryStore } from "../src/recovery-store.ts";

class KV {
  values = new Map();
  serial = Promise.resolve();
  failAfter = Infinity;
  async transaction(action) {
    const run = this.serial.then(async () => {
      const draft = new KV(); draft.values = structuredClone(this.values); draft.failAfter = this.failAfter;
      const result = await action(draft); this.values = draft.values; return result;
    });
    this.serial = run.catch(() => {}); return run;
  }
  async get(key) { return structuredClone(this.values.get(key)); }
  async put(key, value) {
    if (--this.failAfter < 0) throw new Error("simulated storage failure");
    assert.ok(JSON.stringify(value).length < 128 * 1024, "storage value exceeds platform budget");
    this.values.set(key, structuredClone(value));
  }
  async delete(keys) { for (const key of Array.isArray(keys) ? keys : [keys]) this.values.delete(key); }
  async list({ prefix }) { return new Map([...this.values].filter(([key]) => key.startsWith(prefix))); }
}
const id = "a".repeat(32);
const checkpoint = (revision, house = null) => ({ Revision: revision, RoomId: id, Removed: false,
  State: { Id: id, Members: [], House: house, ExpiresAt: Date.now() + 1800000 } });

test("large house chunks survive an edge restart and metadata-only refresh", async () => {
  const kv = new KV(); const store = new RecoveryStore(kv); const house = "abcd".repeat(300000);
  await store.save(checkpoint(1, house));
  await store.save(checkpoint(2));
  const resumed = await new RecoveryStore(kv).rooms();
  assert.equal(resumed[0].State.House, house); assert.equal(resumed[0].Revision, 2);
});
test("out-of-order checkpoints and ended-room resurrection are rejected", async () => {
  const store = new RecoveryStore(new KV());
  await store.save(checkpoint(2, "latest")); await store.save(checkpoint(1, "old"));
  assert.equal((await store.rooms())[0].State.House, "latest");
  await store.save({ Revision: 3, RoomId: id, Removed: true, State: null });
  await store.save(checkpoint(2, "stale")); assert.deepEqual(await store.rooms(), []);
});
test("failed chunk writes roll back atomically and can be retried", async () => {
  const kv = new KV(); const store = new RecoveryStore(kv);
  await store.save(checkpoint(1, "safe"));
  kv.failAfter = 1;
  await assert.rejects(store.save(checkpoint(2, "x".repeat(150000))));
  assert.equal((await store.rooms())[0].State.House, "safe");
  kv.failAfter = Infinity; await store.save(checkpoint(2, "new"));
  assert.equal((await store.rooms())[0].State.House, "new");
});
test("concurrent updates retain the highest revision and expired data is removed", async () => {
  const kv = new KV(); const store = new RecoveryStore(kv);
  await Promise.all([store.save(checkpoint(1, "old")), store.save(checkpoint(3, "latest")), store.save(checkpoint(2, "stale"))]);
  assert.equal((await store.rooms())[0].State.House, "latest");
  const expired = checkpoint(4); expired.State.ExpiresAt = Date.now() - 1000;
  await store.save(expired); assert.deepEqual(await store.rooms(), []); assert.equal(kv.values.size, 0);
});


test("owner and decorator identities above JavaScript's safe integer range remain exact", async () => {
  const kv = new KV(); const store = new RecoveryStore(kv);
  const value = checkpoint(1, "approved-scene");
  value.State.Owner = "9007199254740993";
  value.State.Members = [{ ContentId: "18446744073709551614", Name: "Decorator" }];
  value.State.Editors = ["18446744073709551614"];
  value.State.Banned = ["9007199254740995"];
  value.State.PublishedHash = "a".repeat(64);
  await store.save(JSON.parse(JSON.stringify(value)));
  const restored = JSON.parse(JSON.stringify((await new RecoveryStore(kv).rooms())[0]));
  assert.equal(restored.State.Owner, "9007199254740993");
  assert.equal(restored.State.Members[0].ContentId, "18446744073709551614");
  assert.deepEqual(restored.State.Editors, ["18446744073709551614"]);
  assert.deepEqual(restored.State.Banned, ["9007199254740995"]);
  assert.equal(restored.State.PublishedHash, "a".repeat(64));
});
