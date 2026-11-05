// Recovery checkpoint barriers. The relay sends a room's checkpoint (house included) on one stream only, and a small
// barrier naming its revision on the others; each barrier holds its stream until that revision is durable, so no
// client sees a control message (an admission, an approval) before the state it depends on is saved.
export class DurableRevisions {
  private durable = new Map<string, number>();
  private waiting = new Map<string, { revision: number; release: () => void }[]>();
  private readonly holdMs: number;
  constructor(holdMs = 10_000) { this.holdMs = holdMs; }

  /** A checkpoint of this revision (or a newer one) is saved: release the streams waiting for it. */
  saved(room: string, revision: number): void {
    if ((this.durable.get(room) ?? 0) < revision) this.durable.set(room, revision);
    const held = this.waiting.get(room) ?? [];
    const still = held.filter(w => { if (w.revision <= revision) { w.release(); return false; } return true; });
    if (still.length) this.waiting.set(room, still); else this.waiting.delete(room);
    if (this.durable.size > 1000) this.durable.clear();
  }

  /**
   * Resolves once the revision is durable, or after holdMs: if the carrier's write failed its own stream closes,
   * and the others must not hang on it.
   */
  until(room: string, revision: number): Promise<void> {
    if ((this.durable.get(room) ?? 0) >= revision) return Promise.resolve();
    return new Promise(resolve => {
      const entry = { revision, release: () => { clearTimeout(timer); resolve(); } };
      const timer = setTimeout(() => {
        const list = this.waiting.get(room);
        if (list) this.waiting.set(room, list.filter(w => w !== entry));
        resolve();
      }, this.holdMs);
      this.waiting.set(room, [...(this.waiting.get(room) ?? []), entry]);
    });
  }
}
