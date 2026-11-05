// Keep binary payloads and terminal control frames ordered across asynchronous storage.
export function bridgeRelay(socket: WebSocket, edge: WebSocket,
  checkpoint: (value: any) => Promise<void>, waitUntil: (work: Promise<unknown>) => void): void {
    let stream: Promise<void> = Promise.resolve();
    let incoming: Promise<void> = Promise.resolve();
    let failed = false;
    let upFrames = 0, downFrames = 0;
    // Frames waiting in each chain. A client that floods faster than the relay reads (or a relay that floods a slow
    // client) would otherwise grow the chain without bound in this Durable Object's memory.
    let pendingUp = 0, pendingDown = 0;
    const maxPending = 2048;
    edge.addEventListener("message", event => {
      if (++pendingUp > maxPending) { failed = true; edge.close(1011, "client sending too fast"); socket.close(1011, "client overloaded"); return; }
      incoming = incoming.then(async () => {
        pendingUp--;
        const data = event.data instanceof Blob ? await event.data.arrayBuffer() : event.data;
        socket.send(data); upFrames++;
      }).catch(() => { failed = true; edge.close(1011, "upstream unavailable"); socket.close(1011, "client forward failed"); });
      waitUntil(incoming);
    });
    socket.addEventListener("message", event => {
      if (++pendingDown > maxPending) { failed = true; edge.close(1011, "relay stream overloaded"); socket.close(1011, "client too slow"); return; }
      stream = stream.then(async () => {
        pendingDown--;
        if (failed) return;
        const data = event.data instanceof Blob ? await event.data.arrayBuffer() : event.data;
        const bytes = data instanceof ArrayBuffer ? new Uint8Array(data) : null;
        if (bytes && bytes.length >= 18 && bytes[0] === 0xA5 && bytes[1] === 0x66) {
          const senderSize = bytes[3] | (bytes[4] << 8);
          if (new TextDecoder().decode(bytes.subarray(5, 5 + senderSize)) !== "relay") throw new Error("Invalid checkpoint source");
          const value = JSON.parse(new TextDecoder().decode(bytes.subarray(13 + senderSize)));
          // Serialize checkpoints across sockets. Never expose membership/house checkpoint frames to clients.
          await checkpoint(value);
          return;
        }
        edge.send(data); downFrames++;
      }).catch(() => { failed = true; console.error("relay_proxy_checkpoint_or_forward_failed"); edge.close(1011, "relay recovery unavailable"); socket.close(1011, "checkpoint failed"); });
      waitUntil(stream);
    });
    const close = (target: WebSocket, code: number, reason: string) => {
      try { target.close(code === 1005 || code === 1006 ? 1011 : code, reason); } catch { /* already closed */ }
    };
    edge.addEventListener("close", e => { waitUntil(incoming.finally(() => close(socket, e.code, e.reason))); });
    socket.addEventListener("close", e => {
      console.log("relay_proxy_closed", { code: e.code, upFrames, downFrames });
      waitUntil(stream.finally(() => close(edge, e.code, e.reason)));
    });
    edge.addEventListener("error", () => close(socket, 1011, "client transport error"));
    socket.addEventListener("error", () => close(edge, 1011, "relay transport error"));
}
