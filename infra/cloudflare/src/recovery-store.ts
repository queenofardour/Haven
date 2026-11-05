type Store = Pick<DurableObjectStorage, "get" | "put" | "delete" | "list" | "transaction">;
type Saved = { Revision: number; RoomId: string; Removed: boolean; State: any; chunks: number; expires: number };
const chunkSize = 60000;

// Only fixed house data and hashed membership credentials are persisted. Never chat or action events.
export class RecoveryStore {
  private storage: Store;
  constructor(storage: Store) { this.storage = storage; }
  async save(value: any): Promise<void> {
    if (!/^[0-9a-f]{32}$/.test(value.RoomId) || !Number.isSafeInteger(value.Revision) || value.Revision < 1)
      throw new Error("Invalid checkpoint identity");
    if (!value.Removed && (!value.State || value.State.Id !== value.RoomId || !Number.isSafeInteger(value.State.ExpiresAt)
      || !Array.isArray(value.State.Members) || value.State.Members.length > 20)) throw new Error("Invalid checkpoint state");
    const key = `room:${value.RoomId}`;
    await this.storage.transaction(async tx => {
      const old = await tx.get<Saved>(key);
      if (old && old.Revision >= value.Revision) return;
      const state = value.State ? { ...value.State } : null;
      const house = state?.House;
      if (house != null && (typeof house !== "string" || house.length > 1500000)) throw new Error("Invalid checkpoint budget");
      let chunks = value.Removed ? 0 : old?.chunks ?? 0;
      if (house != null) {
        chunks = Math.ceil(house.length / chunkSize);
        for (let i = 0; i < chunks; i++) await tx.put(`${key}:house:${i}`, house.slice(i * chunkSize, (i + 1) * chunkSize));
      }
      if (state) state.House = null;
      for (let i = chunks; i < (old?.chunks ?? 0); i++) await tx.delete(`${key}:house:${i}`);
      await tx.put(key, { Revision: value.Revision, RoomId: value.RoomId, Removed: value.Removed, State: state, chunks,
        expires: value.Removed ? Date.now() + 1800000 : state.ExpiresAt } satisfies Saved);
    });
  }
  async rooms(): Promise<any[]> {
    return this.storage.transaction(async tx => {
    const result = [];
    const entries = await tx.list<Saved>({ prefix: "room:" });
    for (const [key, saved] of entries) {
      if (key.includes(":house:")) continue;
      if (saved.expires < Date.now()) {
        await tx.delete([key, ...Array.from({ length: saved.chunks }, (_, i) => `${key}:house:${i}`)]);
        continue;
      }
      if (saved.Removed) continue;
      let house = "";
      for (let i = 0; i < saved.chunks; i++) {
        const part = await tx.get<string>(`${key}:house:${i}`);
        if (part == null) throw new Error("Incomplete room checkpoint");
        house += part;
      }
      result.push({ Revision: saved.Revision, RoomId: saved.RoomId, Removed: false,
        State: { ...saved.State, House: saved.chunks ? house : null } });
    }
    return result;
    });
  }
}
