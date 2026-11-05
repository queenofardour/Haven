import { DurableObject } from "cloudflare:workers";
import { Container, getContainer } from "@cloudflare/containers";
import { RecoveryStore } from "./recovery-store";
import { DurableRevisions } from "./durable-revisions";
import { bridgeRelay } from "./relay-proxy";
import { addressKey, fromBrowser, handleContent, type ContentBudgetApi } from "./content";
import { ContentLedger } from "./content-budget";
import { AccountLedger, equalSecret, handleAuth, verifyConnection, type AccountsApi } from "./accounts";
import { InviteBook, handleInvites, type InvitesApi, type SendRequest } from "./invites";

interface Env {
  HAVEN_RELAY: DurableObjectNamespace<HavenRelay>; HAVEN_RECOVERY_KEY: string; HAVEN_CONTENT?: R2Bucket; HAVEN_CONTENT_KEY?: string;
  HAVEN_BUDGET?: DurableObjectNamespace<ContentBudget>; HAVEN_CONTENT_RATE?: RateLimit; HAVEN_ADMIN_KEY?: string;
  // Accounts (accounts.ts): the store, Discord's application, and "required" once every client must sign in.
  HAVEN_ACCOUNT_STORE?: DurableObjectNamespace<HavenAccounts>; DISCORD_CLIENT_ID?: string; DISCORD_CLIENT_SECRET?: string; HAVEN_ACCOUNTS?: string;
  // "required": a Discord account Haven hasn't seen before needs a one-time access code (accounts.ts).
  HAVEN_ACCESS_CODES?: string;
}

// Haven accounts (accounts.ts): one instance, so a character is tied to exactly one account.
export class HavenAccounts extends DurableObject<Env> {
  private readonly ledger = new AccountLedger(this.ctx.storage);
  begin(state: string, now: number) { return this.ledger.begin(state, now); }
  identify(state: string, discordId: string, name: string, now: number) { return this.ledger.identify(state, discordId, name, now); }
  confirm(state: string, token: string, now: number, accessCode?: string, codesRequired?: boolean) { return this.ledger.confirm(state, token, now, accessCode, codesRequired); }
  complete(state: string, discordId: string, name: string, now: number) { return this.ledger.complete(state, discordId, name, now); }
  claim(state: string, verifier: string, now: number) { return this.ledger.claim(state, verifier, now); }
  authorize(keyHash: string, contentId: string, name: string, now: number) { return this.ledger.authorize(keyHash, contentId, name, now); }
  accounts() { return this.ledger.accounts(); }
  ban(id: string, banned: boolean) { return this.ledger.ban(id, banned); }
  signOut(id: string) { return this.ledger.signOut(id); }
  release(contentId: string) { return this.ledger.release(contentId); }
  makeCodes(count: number, note: string, now: number) { return this.ledger.makeCodes(count, note, now); }
  codes() { return this.ledger.codes(); }
  revokeCode(code: string) { return this.ledger.revokeCode(code); }
  // Gathering invitations (invites.ts): in the same object, so the key check and the invitations are one request.
  private readonly invites = new InviteBook(this.ctx.storage, this.ledger);
  invitePoll(keyHash: string, contentId: string, name: string, now: number) { return this.invites.poll(keyHash, contentId, name, now); }
  inviteSend(keyHash: string, contentId: string, name: string, body: SendRequest, now: number) { return this.invites.send(keyHash, contentId, name, body, now); }
  inviteWithdraw(keyHash: string, contentId: string, name: string, invitees: unknown, now: number) { return this.invites.withdraw(keyHash, contentId, name, invitees, now); }
}
const accountsOf = (env: Env) => env.HAVEN_ACCOUNT_STORE?.get(env.HAVEN_ACCOUNT_STORE.idFromName("global")) as unknown as AccountsApi | undefined;

// The content locker's account-wide budget (content-budget.ts): one instance, so every limit is exact.
export class ContentBudget extends DurableObject<Env> {
  private readonly ledger = new ContentLedger(this.ctx.storage);
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    // Once, before the first request is served: what the removed community gallery left in storage goes.
    void ctx.blockConcurrencyWhile(() => this.ledger.dropCommunity());
  }
  read(n: number, now: number) { return this.ledger.read(n, now); }
  store(owner: string, size: number, agreement: number, upload: boolean, now: number) { return this.ledger.store(owner, size, agreement, upload, now); }
  usage(owner: string, now: number) { return this.ledger.usage(owner, now); }
  forget(hash: string) { return this.ledger.forget(hash); }
  refund(owner: string, size: number, uploadedDay: string | null, storedDay: string, now: number) { return this.ledger.refund(owner, size, uploadedDay, storedDay, now); }
  hold(upload: string, owner: string, size: number, now: number) { return this.ledger.hold(upload, owner, size, now); }
  release(upload: string) { return this.ledger.release(upload); }
  clear() { return this.ledger.clear(); }
  stats(now: number) { return this.ledger.stats(now); }
}

// One stable instance owns all rooms during the private beta. Random load balancing
// would split players using the same visit code across independent relay processes.
export class HavenRelay extends Container {
  defaultPort = 8080;
  sleepAfter = "5m";
  enableInternet = false;
  private recovery: RecoveryStore;
  private context: DurableObjectState<{}>;
  private recoveryKey: string;
  private hydratedBoot = "";
  private hydration?: Promise<void>;
  private writes: Promise<void> = Promise.resolve();
  private revisions = new DurableRevisions();
  constructor(ctx: DurableObjectState<{}>, env: Env) {
    super(ctx, env);
    this.context = ctx;
    this.recovery = new RecoveryStore(ctx.storage);
    this.recoveryKey = env.HAVEN_RECOVERY_KEY;
    // The content locker key lets the relay sign picture tickets; without it the relay refuses them.
    this.envVars = { HAVEN_RECOVERY_KEY: env.HAVEN_RECOVERY_KEY, ...(env.HAVEN_CONTENT_KEY ? { HAVEN_CONTENT_KEY: env.HAVEN_CONTENT_KEY } : {}),
      ...(env.HAVEN_ACCOUNTS ? { HAVEN_ACCOUNTS: env.HAVEN_ACCOUNTS } : {}) };
  }
  override onStart() { console.log("relay_container_started"); }
  override onStop(params: { exitCode: number; reason: string }) { console.log("relay_container_stopped", params); this.hydratedBoot = ""; }
  override onError(error: unknown) { console.error("relay_container_error", error instanceof Error ? error.name : "unknown"); }
  private async hydrate(): Promise<void> {
    if (!this.recoveryKey) throw new Error("Recovery secret unavailable");
    const info = await super.fetch(new Request("http://container/health"));
    if (!info.ok) throw new Error("Relay health unavailable");
    const health = await info.json() as { bootId: string };
    if (health.bootId === this.hydratedBoot) return;
    await this.writes;
    for (const room of await this.recovery.rooms()) {
      const response = await super.fetch(new Request("http://container/internal/restore", { method: "POST",
        headers: { "Content-Type": "application/json", "X-Haven-Recovery": this.recoveryKey }, body: JSON.stringify(room) }));
      if (!response.ok) throw new Error("Relay restore refused");
    }
    this.hydratedBoot = health.bootId;
    console.log("relay_recovery_hydrated", { bootId: health.bootId });
  }
  override async fetch(request: Request): Promise<Response> {
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") return super.fetch(request);
    this.hydration ??= this.hydrate().finally(() => { this.hydration = undefined; });
    await this.hydration;
    const upstream = await super.fetch(request);
    const socket = upstream.webSocket;
    if (!socket) return upstream;
    const pair = new WebSocketPair();
    const client = pair[0], edge = pair[1];
    // Coordinate both close handshakes after pending checkpoint/control forwarding.
    // The config also keeps the SDK's inner proxy on manual close semantics.
    socket.accept({ allowHalfOpen: true }); edge.accept({ allowHalfOpen: true });
    bridgeRelay(socket, edge, value => {
      if (value.Barrier === true) {
        if (!/^[0-9a-f]{32}$/.test(value.RoomId) || !Number.isSafeInteger(value.Revision)) return Promise.reject(new Error("Invalid barrier"));
        return this.revisions.until(value.RoomId, value.Revision);
      }
      const save = this.writes.then(() => this.recovery.save(value)).then(() => this.revisions.saved(value.RoomId, value.Revision));
      this.writes = save.catch(() => {});
      return save;
    }, work => this.context.waitUntil(work));
    return new Response(null, { status: 101, webSocket: client });
  }
}

// Operator notices (an announcement, an update, maintenance with a countdown), for the admin page or a script with the
// admin key: GET the standing notice, POST a new one, DELETE to withdraw it. Forwarded to the relay's internal route,
// which only the edge can reach (the recovery secret); the relay sends it to everyone connected.
async function relayNotice(request: Request, env: Env): Promise<Response> {
  const key = env.HAVEN_ADMIN_KEY ?? "";
  if (key.length < 32 || !await equalSecret(request.headers.get("Authorization") ?? "", `Bearer ${key}`) || fromBrowser(request)) return new Response("Not found", { status: 404 });
  if (!["GET", "POST", "DELETE"].includes(request.method)) return new Response("Method not allowed", { status: 405 });
  const body = request.method === "POST" ? await request.text() : undefined;
  if (body !== undefined && body.length > 8192) return new Response("Too large", { status: 413 });
  const forwarded = new Request("http://container/internal/notice", { method: request.method,
    headers: { "Content-Type": "application/json", "X-Haven-Recovery": env.HAVEN_RECOVERY_KEY }, body });
  try { return await getContainer(env.HAVEN_RELAY, "haven-private-beta").fetch(forwarded); }
  catch { return new Response("The Haven relay is starting or temporarily unavailable. Please retry.", { status: 503, headers: { "Retry-After": "5" } }); }
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const path = new URL(request.url).pathname;
    if (path === "/relay/admin/notice") return relayNotice(request, env);
    if (path.startsWith("/content/"))
      return handleContent(request, { HAVEN_CONTENT: env.HAVEN_CONTENT, HAVEN_CONTENT_KEY: env.HAVEN_CONTENT_KEY, HAVEN_ADMIN_KEY: env.HAVEN_ADMIN_KEY, rate: env.HAVEN_CONTENT_RATE,
        budget: env.HAVEN_BUDGET?.get(env.HAVEN_BUDGET.idFromName("global")) as unknown as ContentBudgetApi | undefined });
    if (path.startsWith("/auth/"))
      return handleAuth(request, { accounts: accountsOf(env), DISCORD_CLIENT_ID: env.DISCORD_CLIENT_ID, DISCORD_CLIENT_SECRET: env.DISCORD_CLIENT_SECRET,
        HAVEN_ADMIN_KEY: env.HAVEN_ADMIN_KEY, HAVEN_ACCESS_CODES: env.HAVEN_ACCESS_CODES, rate: env.HAVEN_CONTENT_RATE }, addressKey(request.headers.get("CF-Connecting-IP")));
    // Gathering invitations (invites.ts): the accounts object answers them; the relay container is never woken.
    if (path === "/invites" || path.startsWith("/invites/"))
      return handleInvites(request, { invites: accountsOf(env) as unknown as InvitesApi | undefined, rate: env.HAVEN_CONTENT_RATE }, addressKey(request.headers.get("CF-Connecting-IP")));
    if (path !== "/" && path !== "/health") return new Response("Not found", { status: 404 });
    if (request.method !== "GET") return new Response("Method not allowed", { status: 405 });
    // Connections and health checks per address, before the container is touched: floods neither keep it awake
    // nor reach the relay. A reconnecting client makes a few a minute.
    if (env.HAVEN_CONTENT_RATE && !(await env.HAVEN_CONTENT_RATE.limit({ key: "relay:" + addressKey(request.headers.get("CF-Connecting-IP")) })).success)
      return new Response("Too many connection attempts. Wait a minute and retry.", { status: 429, headers: { "Retry-After": "30" } });
    if (path === "/" && request.headers.get("Upgrade")?.toLowerCase() !== "websocket")
      return new Response("Haven relay. Connect with the Haven plugin.", { status: 426 });
    // Accounts: the connection's character is verified here, and the relay trusts only this stamp (accounts.ts).
    if (path === "/") {
      const verified = await verifyConnection(request, accountsOf(env), env.HAVEN_RECOVERY_KEY, env.HAVEN_ACCOUNTS === "required");
      if ("refusal" in verified) return verified.refusal;
      request = new Request(request, { headers: verified.headers });
    }
    try {
      return await getContainer(env.HAVEN_RELAY, "haven-private-beta").fetch(request);
    } catch {
      return new Response("The Haven relay is starting or temporarily unavailable. Please retry.", {
        status: 503, headers: { "Retry-After": "5" }
      });
    }
  }
} satisfies ExportedHandler<Env>;
