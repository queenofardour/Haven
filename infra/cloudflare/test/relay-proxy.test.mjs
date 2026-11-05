import test from "node:test";
import assert from "node:assert/strict";
import { bridgeRelay } from "../src/relay-proxy.ts";

class Socket extends EventTarget {
  sent = [];
  closed = false;
  send(data) { assert.equal(this.closed, false); this.sent.push(data); }
  close(code, reason) { this.closed = true; this.code = code; }
  emit(type, properties) { const event = new Event(type); Object.assign(event, properties); this.dispatchEvent(event); }
}
const settle = () => new Promise(resolve => setImmediate(resolve));
function checkpointFrame() {
  const json = new TextEncoder().encode(JSON.stringify({ Revision: 1 }));
  const frame = new Uint8Array(18 + json.length);
  frame.set([0xA5, 0x66, 0, 5, 0]); frame.set(new TextEncoder().encode("relay"), 5); frame.set(json, 18);
  return frame.buffer;
}

test("Blob binary payloads remain binary in both directions before terminal close", async () => {
  const upstream = new Socket(), edge = new Socket();
  bridgeRelay(upstream, edge, async () => assert.fail("unexpected checkpoint"), () => {});
  edge.emit("message", { data: new Blob([new Uint8Array([0xA5, 1, 2])]) });
  upstream.emit("message", { data: new Blob([new Uint8Array([0xA5, 0xFF, 4])]) });
  upstream.emit("close", { code: 1000, reason: "admission_rejected" });
  await settle();
  assert.deepEqual(new Uint8Array(upstream.sent[0]), new Uint8Array([0xA5, 1, 2]));
  assert.deepEqual(new Uint8Array(edge.sent[0]), new Uint8Array([0xA5, 0xFF, 4]));
  assert.equal(edge.closed, true);
});

test("checkpoint is consumed and committed before admission and close reach client", async () => {
  const upstream = new Socket(), edge = new Socket();
  let commit; const pending = new Promise(resolve => { commit = resolve; });
  bridgeRelay(upstream, edge, async value => { assert.equal(value.Revision, 1); await pending; }, () => {});
  upstream.emit("message", { data: new Blob([checkpointFrame()]) });
  const ack = new Uint8Array([0xA5, 3]).buffer;
  upstream.emit("message", { data: ack }); upstream.emit("close", { code: 1000, reason: "done" });
  await settle(); assert.equal(edge.sent.length, 0); assert.equal(edge.closed, false);
  commit(); await settle(); assert.deepEqual(edge.sent, [ack]); assert.equal(edge.closed, true);
});

test("failed persistence closes the stream without exposing checkpoint or admitting client", async () => {
  const upstream = new Socket(), edge = new Socket();
  bridgeRelay(upstream, edge, async () => { throw new Error("storage unavailable"); }, () => {});
  upstream.emit("message", { data: checkpointFrame() });
  upstream.emit("message", { data: new Uint8Array([0xA5, 3]).buffer });
  await settle(); assert.deepEqual(edge.sent, []); assert.equal(edge.code, 1011); assert.equal(upstream.code, 1011);
});

test("checkpoint barriers hold a stream until the carrier's revision is durable, never forever", async () => {
  const { DurableRevisions } = await import("../src/durable-revisions.ts");
  const revisions = new DurableRevisions(50), room = "a".repeat(32);
  let released = false;
  const held = revisions.until(room, 3).then(() => { released = true; });
  revisions.saved(room, 2); await settle(); assert.equal(released, false);
  revisions.saved(room, 3); await held; assert.equal(released, true);
  await revisions.until(room, 1);               // already durable: immediate
  const start = Date.now(); await revisions.until(room, 9);
  assert.ok(Date.now() - start >= 40);          // a carrier that never saves only delays, never hangs
});
