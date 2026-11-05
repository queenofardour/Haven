// Derived from Enceladeum/HMapSync at 8140dbb04cfbfa59da9ff0c3f4e787978d368303. AGPL-3.0-or-later. See THIRD_PARTY_NOTICES.md.
using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using HavenNative.Services;

namespace HavenNative.Sync;

public partial class RelaySyncService : IDisposable
{
    private readonly SemaphoreSlim havenSendGate = new(1, 1);
    private readonly SemaphoreSlim housingPublishGate = new(1, 1);
    public bool HousingSceneReady { get; private set; }
    public event Action? OnHousingSceneReady;
    private string? requestedScene;
    public event Action<byte[]>? OnHousingSnapshot;
    public event Action<Haven.Core.RoomChatMessage>? OnRoomChat;
    // A room chat line, whether it is this PC's own (the relay's echo), and the sender's character (0: not a member).
    public event Action<Haven.Core.RoomChatMessage, bool, ulong>? OnRoomChatDelivery;
    // Lines the relay replays after this client resumed a dropped connection.
    public event Action<Haven.Core.RoomChatMessage, ulong>? OnRoomChatReplay;
    // Why this PC is about to leave, sent with LeaveRoom (GameDataPaths.LeaveReason); used once.
    private byte[]? leaveReason;
    public void LeavingFor(byte[] reason) => leaveReason = reason;
    /// <summary>A guest left because their game lacks this home's zone: their name and the expansion it needs.</summary>
    public event Action<string, int>? OnGuestMissingExpansion;
    private ulong MemberCharacter(string peerId) => HousingMembers.TryGetValue(peerId, out var member) ? member.ContentId : 0;
    public event Action<string>? OnRoomChatRejected;
    public event Action<string, Haven.Core.TypingState>? OnTyping;
    // Furnishing switch sides from other participants, or the room's table once this client's scene is ready.
    public event Action<Haven.Core.SwitchState[]>? OnSwitchStates;
    public async Task SendSwitchStates(IReadOnlyCollection<Haven.Core.SwitchState> states)
    {
        var socket = ws;
        if (states.Count == 0 || !IsConnected || !HousingSceneReady || socket == null) return;
        await SendFrame(Haven.Core.HousingWire.SwitchState, Haven.Core.SwitchWire.Encode(states), socket);
    }
    // Pose entries (pose balls open or held) from the relay: one participant's change, or the room's table once ready.
    public event Action<Haven.Core.PoseEntry[]>? OnPoseEntries;
    public async Task SendPose(Haven.Core.PoseEntry entry)
    {
        var socket = ws;
        if (!IsConnected || !HousingSceneReady || socket == null) return;
        await SendFrame(Haven.Core.HousingWire.PoseState, Haven.Core.PoseWire.Encode([entry]), socket);
    }
    // A partner on the same pose set asking this player to take a fit.
    public event Action<Haven.Core.PoseAdjust>? OnPoseAdjust;
    /// <summary>A two-player minigame's message from another participant (invitation, deal, move).</summary>
    public event Action<Haven.Core.GameMessage>? OnMiniGame;
    public async Task SendMiniGame(Haven.Core.GameMessage message)
    {
        var socket = ws;
        if (!IsConnected || !HousingSceneReady || socket == null) return;
        await SendFrame(Haven.Core.HousingWire.MiniGame, message.Encode(), socket);
    }
    public async Task SendPoseAdjust(Haven.Core.PoseAdjust adjust)
    {
        var socket = ws;
        if (!IsConnected || !HousingSceneReady || socket == null) return;
        await SendFrame(Haven.Core.HousingWire.PoseAdjust, adjust.Encode(), socket);
    }
    // A set's balls hidden for everyone (by anyone, this player included).
    public event Action<Haven.Core.PoseClose>? OnPoseClose;
    /// <summary>A notice from the relay's operator (an announcement, an update, maintenance with a countdown).</summary>
    public event Action<Haven.Core.RelayNotice>? OnNotice;
    public async Task SendPoseClose(Haven.Core.PoseClose close)
    {
        var socket = ws;
        if (!IsConnected || !HousingSceneReady || socket == null) return;
        await SendFrame(Haven.Core.HousingWire.PoseClose, close.Encode(), socket);
    }
    public async Task SendTyping(Haven.Core.TypingState state)
    {
        var socket = ws;
        if (!IsConnected || !HousingSceneReady || socket == null) return;
        await SendFrame(Haven.Core.HousingWire.Typing, [(byte)state], socket);
    }
    public async Task SendRoomChat(Haven.Core.RoomChatMessage message)
    {
        if (!await TrySendRoomChat(message)) OnRoomChatRejected?.Invoke("Room chat is unavailable until the shared home is ready.");
    }
    /// <summary>Hands a chat line to the socket; false when it was not sent (not connected, not ready, or the send failed).</summary>
    public async Task<bool> TrySendRoomChat(Haven.Core.RoomChatMessage message)
    {
        var socket = ws;
        if (!IsConnected || !HousingSceneReady || socket == null) return false;
        return await SendFrame(Haven.Core.HousingWire.RoomChat, Haven.Core.RoomChat.Encode(message), socket);
    }
    public async Task SendHousingSnapshot(byte[] snapshot, bool restoring = false)
    {
        var socket = ws;
        if ((!restoring && !IsHost) || !IsConnected || socket == null) return;
        CacheHouse(Haven.Core.HouseFormat.Parse(snapshot), notify: false);
        await housingPublishGate.WaitAsync();
        try
        {
            var encoded = await Task.Run(() => Haven.Core.HousingSnapshotCodec.Encode(Haven.Core.HouseFormat.Parse(snapshot)));
            if (ReferenceEquals(ws, socket) && (restoring || IsHost) && IsConnected)
                await SendFrame(restoring ? Haven.Core.HousingWire.RestoreSnapshot : Haven.Core.HousingWire.CompressedSnapshot, encoded, socket);
        }
        catch (Exception ex) { log.Warning(ex, "Haven scene publication failed."); }
        finally { housingPublishGate.Release(); }
    }
    public async Task SendHousingReady(string hash)
    {
        var socket = ws;
        await housingPublishGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(socket, ws) || !IsConnected) return;
            requestedScene = hash;
            await SendFrame(Haven.Core.HousingWire.SceneReady, Encoding.ASCII.GetBytes(hash), socket);
        }
        finally { housingPublishGate.Release(); }
    }
    private readonly IPluginLog log;

    private ClientWebSocket? ws;
    private CancellationTokenSource? cts;
    private Task? receiveTask;
    private Task? keepAliveTask;

    // App-level lobby keepalive interval. A no-map lobby (participants gathered, no zone loaded yet) engages NO
    // synthetic session, so StateCaptureService - the only source of the HOT liveness heartbeat - never starts, and
    // the connection is otherwise held open by nothing but the 15s WebSocket protocol ping. If that ping fails to
    // traverse the Cloudflare tunnel (idle drop / client sleep / a blip) the socket dies, the relay reaps the peer,
    // and once every peer is in the same silent state the room empties and is pruned - the "lobby times out when no
    // map is loaded" symptom. This app-level Ping (relay answers Pong directly, never fanned out, negligible rate
    // cost) keeps the socket demonstrably alive from the moment we connect, independent of a map load - so a lobby
    // (e.g. an HDM open-world roleplay gather that never loads an HMS map) persists as long as anyone stays. It does
    // NOT engage the packet filter or broadcast anything, so the "friends stay real characters Mare can cache"
    // property of the pre-load lobby is preserved. Redundant-but-harmless during a synthetic session (the HOT
    // heartbeat already flows far more often); kept unconditional to stay decoupled from StateCaptureService.
    private const int KeepAliveIntervalMs = 5000;

    public string LocalPeerId { get; private set; } = Guid.NewGuid().ToString("N")[..12];
    // S331 (Stage 4): true once the relay's RoomJoined has arrived (it mints our peer id). The capture/send loop must
    // wait for this before emitting lane frames - the relay-minted id is our identity, and we need it to recognize our
    // own echoes. Reset on disconnect.
    public bool RoomJoinedAcknowledged { get; private set; }
    public string RoomId { get; private set; } = "";
    public bool IsConnected { get; private set; }
    // S328am: the URL we actually connected to (for the GUI to show the ACTIVE endpoint, not just the saved one).
    // Set on a successful connect, cleared on disconnect. Token is NOT stripped here - the UI redacts it for display.
    public string ConnectedUrl { get; private set; } = "";
    public bool IsHost { get; set; }

    // Opaque relay-generated RoomId, handed back in RoomJoined. Cached so a dropped host/peer can reconnect to the
    // SAME room (Reconnect mode) rather than re-resolving by nearby players (who aren't visible from the synthetic zone).
    public string CachedRoomId { get; private set; } = "";

    // Relay-authoritative room capacity (ROOM_CAP), handed to us on RoomJoined. 0 = unlimited / don't show. Never
    // hardcoded - the relay may lower it after perf testing with just a restart, so we only ever read it from here.
    public int RoomCap { get; private set; }

    // The room password for the current session - retained so the UI can show it as the shareable key (auto-generated
    // on Host if the user left the field blank). In-memory only; cleared on disconnect.
    public string CurrentPassword { get; private set; } = "";

    // S328f - SOLO MODE. When true, the plugin runs the full map-authoring feature set (zone load, time/weather/BGM,
    // NPC, cosmetics, movement, packet filter) with NO relay connection and no peers - the same client-side loop
    // Hyperborea does. The relay exists ONLY to sync peers; everything else is client-side and relay-independent.
    // These two accessors are the single point solo flows through, so solo is one flag rather than scattered checks:
    //   • HasMapAuthority - gates MAP actions (weather/time/BGM/NPC/load/reassert). True for the host OR in solo.
    //   • IsSessionActive - gates feature-availability / packet-filter / "in a session". True when connected OR in solo.
    // Peer-only operations (summon/kick/password/lock/transfer host) stay on the literal IsHost - solo can't do them.
    public bool SoloMode { get; set; }
    public bool HasMapAuthority => IsHost || SoloMode;
    public bool IsSessionActive => IsConnected || SoloMode || IsRecovering;

    public event Action<string, ulong, string>? OnPeerJoined;
    public event Action<string>? OnPeerLeft;
    public event Action<string>? OnHostTransfer;
    public event Action<string, TransformData, bool>? OnTransformReceived;   // (peerId, snapshot, isHotLane) - v0.7.461: lane bool for the HOT-only Seq gate

    // ── FEAT-R2 (HDM disguise-sync) ── inbound disguise atom / one-shot action from a peer. The payload is self-
    // describing (carries SenderContentId + SubjectId), so the receiver resolves identity itself - no relay peerId
    // needed. See HMapSync's docs/HDM-sync-HMS-side-brief.md §4. A puppet's per-frame TRANSFORM rides its own PuppetMove (0x52).
    public event Action<HavenNative.Wire.DisguiseUpdatePayload>? OnDisguiseReceived;
    public event Action<HavenNative.Wire.ActionPulsePayload>? OnActionPulseReceived;
    public event Action<HavenNative.Wire.PuppetMovePayload>? OnPuppetMoveReceived;
    // Bug B (IPC v1.1): a source DM's own-body hide bit. Self-describing (SenderContentId), snapshot-able (re-sent on
    // first-sight). Receiver suppresses that source's own-body mirror (Alpha 0) while Hidden - never reverts the disguise.
    public event Action<HavenNative.Wire.OwnBodyHiddenPayload>? OnOwnBodyHiddenReceived;
    // Lobby nameplate sync (0x54): a source's chosen Moniker nameplate, carried so peers see custom names in the LOBBY
    // (out of map, where the Cold transform lane that normally couriers Moniker isn't running). Self-describing
    // (SenderContentId), snapshot-able (re-broadcast on peer-join). Receiver resolves ContentId→ObjectIndex and applies.
    public event Action<HavenNative.Wire.LobbyNameplatePayload>? OnLobbyNameplateReceived;
    // Freeze-animation sync (0x55, HDM IPC MinorVersion 4): a source's per-subject freeze bit. Self-describing
    // (SenderContentId + SubjectId), snapshot-able (re-sent on first-sight). Receiver drives HDM.SetFrozen on the
    // resolved local actor (peer's synced body for "", else the mirror puppet).
    public event Action<HavenNative.Wire.FreezeUpdatePayload>? OnFreezeReceived;
    // Lights-out sync (0x56, HMS-native): a source's map-global light/flame suppression edge for one instanced
    // dungeon. Self-describing (SenderContentId + TerritoryId + Layer), snapshot-able (re-offered on peer-join).
    // Receiver runs the identical local suppression on its own layout copy when the TerritoryId matches its zone.
    public event Action<HavenNative.Wire.LightsOutPayload>? OnLightsOutReceived;

    // ── Per-subject composite cache (Stage 2a) ── each lane message updates its slice of the subject's composite
    // TransformData; the merged whole feeds OnTransformReceived. Keyed by SUBJECT entity id (payload `sid`), not
    // sender - the entity-addressing seam. This preserves the burst-coalescing property: one composite per subject,
    // each lane updates its own fields, the apply reads the merged whole. Evicted on peer-leave/despawn.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TransformData> laneComposites = new();

    private TransformData CompositeFor(string subjectId) =>
        laneComposites.GetOrAdd(subjectId, _ => new TransformData());

    /// <summary>Drop a subject's composite (peer left / entity despawned). Called from the peer-leave path.</summary>
    public void EvictComposite(string subjectId) => laneComposites.TryRemove(subjectId, out _);

    // A distinct COPY of the composite for handing to the apply/interpolation path. The composite is a single mutating
    // instance per subject (so lanes merge into one place); but interpolation stores references, so it needs its own
    // frozen snapshot each message or all history entries alias to the latest value (flip-book).
    private static TransformData SnapshotComposite(TransformData c) =>
        c.Snapshot();

    // ── S331 (Stage 4): wiredump - the required binary-debuggability tool. Captures the next N frames (sent and/or
    // received) and pretty-prints kind + decoded msgpack payload as readable text. Turns the binary wire back into
    // something eyeball-able on demand. Armed via /havenengine wiredump.
    public bool WireDumpActive { get; private set; }
    private int wireDumpRemaining;
    private readonly System.Collections.Generic.List<string> wireDumpLines = new();
    public System.Action<string>? WireDumpEmit;   // where to print (chat), set by the plugin

    public void ArmWireDump(int frames)
    {
        wireDumpLines.Clear();
        wireDumpRemaining = frames;
        WireDumpActive = true;
        WireDumpEmit?.Invoke("[HavenNative] wiredump armed - capturing next " + frames + " frames (sent+received).");
    }

    private void WireDumpCapture(bool sent, byte kind, byte[] payload, int frameLen)
    {
        if (!WireDumpActive) return;
        string dir = sent ? "SEND" : "RECV";
        string decoded = WireDumpDecoder.DecodePayload(kind, payload);
        wireDumpLines.Add(dir + " " + KindName(kind) + " (" + frameLen + " B): " + decoded);
        if (--wireDumpRemaining <= 0)
        {
            WireDumpActive = false;
            WireDumpEmit?.Invoke("[HavenNative] wiredump (" + wireDumpLines.Count + " frames):");
            foreach (var line in wireDumpLines) WireDumpEmit?.Invoke("  " + line);
        }
    }
    public event Action<ZoneLoadData>? OnZoneLoadReceived;
    public event Action<RoomJoinedData>? OnRoomJoined;
    public event Action? OnSessionEnded;
    public event Action<uint, string>? OnError;
    public event Action? OnDisconnected;

    // v0.7.464 (RMS QA F3, soft tier): the relay's SOFT ingress-throttle notice (WireKind.RateLimited 0x08).
    // Non-fatal - the socket stays open and the session continues; excess ingress is simply not fanned out.
    // Deliberately NOT an ErrorPayload code: see the note on WireKind.RateLimited for why an unknown code is
    // fatal on an older client and an unknown kind is inert.
    public event Action? OnRateLimited;

    // v0.7.464 (RMS QA F3, hard tier): the close status of the LAST closed connection, so the disconnect handler
    // can say WHY instead of the generic "lost connection". Null when the socket dropped without a close frame
    // (abort/timeout/network) - which is exactly the case we cannot diagnose, and must not guess at.
    public int? LastCloseCode { get; private set; }
    public string LastCloseReason { get; private set; } = "";

    private long nextFrameWarning;
    // The relay's forwarding time of the frame being handled (receive thread only).
    private long frameStamp;
    /// <summary>Haven: zeroes WARM identifiers that name nothing in the game data (WireSanity), before they are applied.</summary>
    public Action<HavenNative.Wire.WarmPayload>? CleanWarm { get; set; }
    public RelaySyncService(IPluginLog log)
    {
        this.log = log;
    }

    // S328ag: bandwidth instrumentation. Set by the plugin; the two byte choke points (Send / receive loop) feed it.
    // Null-safe so the relay works without it. Change-detection is always on (per-lane, in SendTransformAsLanes).
    public NetStatsService? NetStats { get; set; }
    // Haven: adds this PC's Haven account to a relay connection (HavenAccount.Apply): options, character, name.
    public Action<ClientWebSocketOptions, ulong, string>? PrepareConnection { get; set; }

    private async Task<bool> ConnectTransport(string relayUrl, string roomId, bool? createIfMissing, string? password, ulong[]? nearbyContentIds, ulong contentId, uint entityId, string charName, bool resuming, CancellationToken lifetime)
    {
        // Force-kill any existing connection state synchronously
        // Don't try graceful close - just nuke it
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
        cts = null;

        try { ws?.Abort(); } catch { } // Abort is synchronous, unlike CloseAsync
        try { ws?.Dispose(); } catch { }
        ws = null;

        IsConnected = false;
        if (!resuming) IsHost = false;
        HousingSceneReady = false;
        RoomJoinedAcknowledged = false;
        requestedScene = null;

        // Small delay to let any background receive loops exit
        await Task.Delay(100);
        if (lifetime.IsCancellationRequested) return false;

        RoomId = roomId;
        CurrentPassword = password ?? "";
        LastCloseCode = null;                // v0.7.464: fresh connection - a stale 4029 must not colour the next drop
        LastCloseReason = "";
        if (!resuming) IsHost = createIfMissing == true;
        // Intent cancellation stops connection attempts/retries, but must not abort the live receive
        // before an explicit LeaveRoom has been flushed. The transport is cancelled in Disconnect finally.
        cts = new CancellationTokenSource();

        try
        {
            ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            PrepareConnection?.Invoke(ws.Options, contentId, charName);
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, lifetime))
            {
                connectTimeout.CancelAfter(resuming ? Haven.Core.RecoveryPolicy.SilenceTimeout(RoundTripMs) : TimeSpan.FromSeconds(15));
                await ws.ConnectAsync(new Uri(relayUrl), connectTimeout.Token);
            }
            IsConnected = true;
            ConnectedUrl = relayUrl;   // S328am: record the active endpoint for the GUI
            log.Information("[HavenNative] Connected to relay");

            var joinWire = new HavenNative.Wire.JoinPayload
            {
                HavenProtocolRevision = Haven.Core.HousingWire.ProtocolRevision,
                RoomId = roomId,               // empty on host/join; the cached opaque id on reconnect
                ContentId = contentId,
                EntityId = entityId,
                CharacterName = charName,
                RoomPassword = password,        // the user's password - relay compares (constant-time)
                CreateIfMissing = createIfMissing,       // true=Host, false=Join/Reconnect, null=legacy (never null here)
                NearbyContentIds = nearbyContentIds,     // Join only - the ContentIds we can see; relay resolves the room
                ResumeToken = resuming ? resumeToken : "",
                ContentSecret = ContentSecretFor?.Invoke(contentId) ?? "",
                Batching = true,
            };
            await SendFrame(HavenNative.Wire.WireKind.JoinRoom,
                MessagePack.MessagePackSerializer.Serialize(joinWire, HavenNative.Wire.WireFormat.Options));

            var socket = ws;
            var token = cts.Token;
            lastReceiveTick = Environment.TickCount64;
            receiveTask = Task.Run(() => ReceiveLoop(socket, token));
            keepAliveTask = Task.Run(() => KeepAliveLoop(socket, token));
            return true;
        }
        catch (Exception ex)
        {
            if (lifetime.IsCancellationRequested) return false;
            log.Error(ex, "[HavenNative] Relay WebSocket connection failed.");
            IsConnected = false;
            // The edge says why it refused an account (X-Haven-Refusal), e.g. a character tied to another account.
            string? refusal = null; var status = 0;
            try { status = (int)(ws?.HttpStatusCode ?? 0); if (ws?.HttpResponseHeaders?.TryGetValue("X-Haven-Refusal", out var codes) == true) refusal = codes.FirstOrDefault(); }
            catch (Exception) { }
            if (!resuming || status is 401 or 403) OnError?.Invoke(0u, Haven.Core.RelayConnectionFailure.Describe(relayUrl, ex, refusal, status));
            return false;
        }
    }

    private readonly object disconnectGate = new();
    private Task? disconnectTask;
    private int disconnectVersion;
    public Task Disconnect()
    {
        lock (disconnectGate)
        {
            disconnectVersion++;
            CancelRecovery(); // Cancel intent even when sharing an already-running close operation.
            // Both the error callback and socket-close callback can request teardown together.
            // Share one leave/close operation so one caller cannot abort the other's LeaveRoom.
            if (disconnectTask is { IsCompleted: false }) return disconnectTask;
            return disconnectTask = DisconnectCore();
        }
    }
    private async Task DisconnectCore()
    {

        // Flip connection state OFF synchronously, FIRST - before any await. This is what the GUI and the
        // /havenengine stop guards read, so it must change the instant we decide to disconnect, not after the
        // (possibly slow/hanging) graceful close. The old code set it only AFTER awaiting CloseAsync, so a
        // server-half-closed socket faulted the fire-and-forget Task before the flag flipped → session
        // looked connected until a second /havenengine stop. Local socket cleanup still happens in finally.
        IsConnected = false;
        IsHost = false;
        HousingSceneReady = false;
        requestedScene = null;
        laneComposites.Clear();   // S330c (2b): drop all per-subject composites - a fresh session/reconnect starts clean
        RoomJoinedAcknowledged = false;   // S331: re-arm the join gate for the next connection
        var closing = ws;
        var closingCancellation = cts;

        try
        {
            // Best-effort graceful close, TIMEBOXED to 1s. Optional - teardown does not depend on it.
            if (closing?.State == WebSocketState.Open)
            {
                using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    // Tell the server we're leaving (matters for the /havenengine leave peer path, which sends no
                    // SessionEnd). Best-effort - SendFrame targets the live ws (still set until finally) and
                    // swallows its own errors; if the socket's already closing, CloseAsync below throws and
                    // we force teardown.
                    await SendFrame(HavenNative.Wire.WireKind.LeaveRoom, Interlocked.Exchange(ref leaveReason, null) ?? System.Array.Empty<byte>());
                    await closing.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", graceCts.Token);
                }
                catch (Exception ex) { log.Debug("[HavenNative] Graceful close skipped (" + ex.Message + ") - forcing teardown."); }
            }
        }
        finally
        {
            // ALWAYS run local teardown - never gated behind network I/O.
            try { closingCancellation?.Cancel(); } catch { }
            try { closingCancellation?.Dispose(); } catch { }
            if (ReferenceEquals(cts, closingCancellation)) cts = null;

            try { closing?.Abort(); } catch { }   // synchronous, unblocks anything pending
            try { closing?.Dispose(); } catch { }
            if (ReferenceEquals(ws, closing)) ws = null;
            if (ws == null) { RoomId = ""; CurrentPassword = ""; }
            log.Information("[HavenNative] Disconnected");
        }
    }

    // S331 (Stage 4 / Stage 3 retirement): SendTransform (the monolithic TransformUpdate 0x10) is REMOVED. The whole-
    // struct JSON send is gone - the binary lanes (SendTransformAsLanes) are the only wire format now. This is the
    // point where the monolith is retired: there is no method here that sends the entire struct as one message.

    // ── LANE SENDER (Stage 2a, S330a) ── emit ONLY the lanes whose fields changed since the last send. This IS the
    // "no monolithic send path" property: there is no method here that sends the whole struct as lanes - each lane is
    // an independent message with only its fields. A walking-not-emoting peer sends HOT only; an idle peer sends
    // nothing (until the keepalive forces a HOT). Envelope SenderId = LocalPeerId ALWAYS (relay sender-exclusion +
    // spoof-guard); the subject entity id rides IN the payload (== LocalPeerId today; an NPC's id tomorrow).
    private TransformData? lastSentLanes;   // last transform we emitted lanes for (per-lane diff basis)
    private uint hotSeq;
    private int sendingLanes, fullLanesPending;

    /// <summary>
    /// Emit changed lanes. Two force flags with distinct meaning:
    ///   <paramref name="forceHot"/> - send HOT even if unchanged (the keepalive/liveness heartbeat; HOT carries the
    ///   version and is the "still here" signal). True on keepalive, first-send, and dirty-check-off.
    ///   <paramref name="forceAllLanes"/> - send EVERY lane even if unchanged (a joiner needs the complete initial
    ///   picture: position + appearance + map-state + emote). True on first-send and dirty-check-off ONLY - NOT on a
    ///   bare keepalive, because resending unchanged COLD/HOST/WARM on every keepalive is redundant idle traffic.
    /// </summary>
    public async Task SendTransformAsLanes(TransformData t, bool isHost, bool forceHot, bool forceAllLanes, bool forceHostOnce, float posEps, float rotEps)
    {
        if (forceAllLanes) Interlocked.Exchange(ref fullLanesPending, 1);
        if (!IsConnected || !HousingSceneReady || Interlocked.CompareExchange(ref sendingLanes, 1, 0) != 0) return;
        try
        {
            await SendLanes(t, isHost, forceHot, Interlocked.Exchange(ref fullLanesPending, 0) != 0, forceHostOnce, posEps, rotEps, ws);
        }
        finally { Volatile.Write(ref sendingLanes, 0); }
    }
    private async Task SendLanes(TransformData t, bool isHost, bool forceHot, bool forceAllLanes, bool forceHostOnce, float posEps, float rotEps, ClientWebSocket? socket)
    {
        var prev = lastSentLanes;
        // S331 (Stage 4): subjectId = "" - for a player puppet the subject IS the relay-stamped sender, so we send the
        // 1-byte empty sentinel instead of the full id (spec §4). An NPC/entity later would put its explicit id here.
        const string subject = "";

        // HOT - position/movement. Sent on change OR on the keepalive/liveness heartbeat.
        if (forceHot || prev == null || !LaneProjection.HotEquals(t, prev, posEps, rotEps))
        {
            hotSeq++;
            var hot = LaneProjection.ToHotWire(t, subject, hotSeq);
            await SendFrame(HavenNative.Wire.WireKind.HotUpdate,
                MessagePack.MessagePackSerializer.Serialize(hot, HavenNative.Wire.WireFormat.Options), socket);
        }

        // WARM - emote/mount/minion/ornament/etc. STRICTLY change-gated (forceAllLanes only, never keepalive).
        if (forceAllLanes || prev == null || !LaneProjection.WarmEquals(t, prev))
        {
            var warm = LaneProjection.ToWarmWire(t, subject);
            await SendFrame(HavenNative.Wire.WireKind.WarmUpdate,
                MessagePack.MessagePackSerializer.Serialize(warm, HavenNative.Wire.WireFormat.Options), socket);
        }

        // COLD - Moniker/cosmetic toggles. STRICTLY change-gated (forceAllLanes only, never keepalive).
        if (forceAllLanes || prev == null || !LaneProjection.ColdEquals(t, prev))
        {
            var cold = LaneProjection.ToColdWire(t, subject);
            await SendFrame(HavenNative.Wire.WireKind.ColdUpdate,
                MessagePack.MessagePackSerializer.Serialize(cold, HavenNative.Wire.WireFormat.Options), socket);
        }

        // HOST - map-state block. Only the host emits it. STRICTLY change-gated (forceAllLanes only, never keepalive) -
        // EXCEPT forceHostOnce, the late-join re-broadcast: the host re-sends current map-state once when a peer joins so
        // the newcomer (who missed the original change) gets weather/time/BGM. Existing peers skip it via the epoch gate.
        if (isHost && (forceAllLanes || forceHostOnce || prev == null || !LaneProjection.HostEquals(t, prev)))
        {
            var host = LaneProjection.ToHostWire(t, subject);
            await SendFrame(HavenNative.Wire.WireKind.HostUpdate,
                MessagePack.MessagePackSerializer.Serialize(host, HavenNative.Wire.WireFormat.Options), socket);
        }

        // Retain a COPY for the next per-lane diff. (Clone so later mutation of t doesn't corrupt the diff basis.)
        if (ReferenceEquals(ws, socket) && HousingSceneReady) lastSentLanes = CloneTransform(t);
    }

    private static TransformData CloneTransform(TransformData t)
    {
        return t.Snapshot();
    }

    public async Task SendZoneLoad(ZoneLoadData zoneData)
    {
        if (!IsConnected || !IsHost) return;

        // S331 (Stage 4): ZoneLoadData isn't ported to a wire POCO yet (spec §4.5 - it rides its own path). Carry its
        // JSON as the payload bytes for now; the relay treats the payload as opaque either way, so this is transparent
        // to it. Port to a msgpack ZoneLoadPayload later if zone-load traffic ever matters for bytes (it's rare).
        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(zoneData));
        await SendFrame(HavenNative.Wire.WireKind.ZoneLoadExecute, jsonBytes);
    }

    public async Task SendSessionEnd()
    {
        if (!IsConnected || !IsHost) return;
        CancelRecovery();
        await SendFrame(HavenNative.Wire.WireKind.SessionEnd, System.Array.Empty<byte>());
    }

    // Host ejects + bans a peer (relay removes, bans the ContentId for the room's life, hard-closes their socket,
    // broadcasts PeerLeft; the kicked client gets Error code 5).
    public async Task SendKick(string targetPeerId)
    {
        if (!IsConnected || !IsHost || string.IsNullOrEmpty(targetPeerId)) return;
        var payload = MessagePack.MessagePackSerializer.Serialize(
            new HavenNative.Wire.KickPeerPayload { TargetPeerId = targetPeerId }, HavenNative.Wire.WireFormat.Options);
        await SendFrame(HavenNative.Wire.WireKind.KickPeer, payload);
    }

    // Host hands the role to a specific peer (relay reassigns host + broadcasts HostTransfer; we drop IsHost when the
    // broadcast comes back). Distinct from leave-driven auto-succession - this is an explicit pick.
    public async Task SendHostTransfer(string targetPeerId)
    {
        if (!IsConnected || !IsHost || string.IsNullOrEmpty(targetPeerId)) return;
        var payload = MessagePack.MessagePackSerializer.Serialize(
            new HavenNative.Wire.HostTransferPayload { TargetPeerId = targetPeerId }, HavenNative.Wire.WireFormat.Options);
        await SendFrame(HavenNative.Wire.WireKind.HostTransfer, payload);
    }

    // ── FEAT-R2 (HDM disguise-sync) senders ── the caller (StateApplyService's HDM bridge) fills the atom/action
    // fields (Kind/BaseId/… from HDM's IPC event, SenderContentId = local player, Epoch = HDM-authored); the service
    // stamps the per-lane Seq (dedup/ordering, an envelope concern like hotSeq). Relay-opaque; no host gate (a source
    // only ever disguises its own actor/puppets). SubjectId "" = own body, "<cid>:<slot>" = a spawned puppet.
    private uint disguiseSeq;
    private uint actionSeq;
    private uint ownHiddenSeq;
    private uint lobbyNameSeq;
    private uint freezeSeq;
    private uint lightsOutSeq;

    public async Task SendDisguiseUpdate(HavenNative.Wire.DisguiseUpdatePayload p)
    {
        if (!IsConnected) return;
        p.Seq = ++disguiseSeq;
        await SendFrame(HavenNative.Wire.WireKind.DisguiseUpdate,
            MessagePack.MessagePackSerializer.Serialize(p, HavenNative.Wire.WireFormat.Options));
    }

    public async Task SendActionPulse(HavenNative.Wire.ActionPulsePayload p)
    {
        if (!IsConnected) return;
        p.Seq = ++actionSeq;
        await SendFrame(HavenNative.Wire.WireKind.ActionPulse,
            MessagePack.MessagePackSerializer.Serialize(p, HavenNative.Wire.WireFormat.Options));
    }

    // Puppet transform: no Seq (coalesced last-writer-wins; a dropped/reordered frame is self-correcting on the next).
    public async Task SendPuppetMove(HavenNative.Wire.PuppetMovePayload p)
    {
        if (!IsConnected) return;
        await SendFrame(HavenNative.Wire.WireKind.PuppetMove,
            MessagePack.MessagePackSerializer.Serialize(p, HavenNative.Wire.WireFormat.Options));
    }

    // Bug B (v1.1): DM own-body hide bit. Snapshot-able (coalesced last-writer-wins per source), Seq-stamped like the
    // disguise lane so a peer applies the latest transition and re-sends carry a fresh Seq on first-sight.
    public async Task SendOwnBodyHidden(HavenNative.Wire.OwnBodyHiddenPayload p)
    {
        if (!IsConnected) return;
        p.Seq = ++ownHiddenSeq;
        await SendFrame(HavenNative.Wire.WireKind.OwnBodyHidden,
            MessagePack.MessagePackSerializer.Serialize(p, HavenNative.Wire.WireFormat.Options));
    }

    // Lobby nameplate (0x54): the source's chosen Moniker nameplate for out-of-map (lobby) display. Snapshot-able
    // (coalesced last-writer-wins per source), Seq-stamped like the other relay-opaque lanes so a peer applies the
    // latest and re-broadcasts on peer-join carry a fresh Seq.
    public async Task SendLobbyNameplate(HavenNative.Wire.LobbyNameplatePayload p)
    {
        if (!IsConnected) return;
        p.Seq = ++lobbyNameSeq;
        await SendFrame(HavenNative.Wire.WireKind.LobbyNameplate,
            MessagePack.MessagePackSerializer.Serialize(p, HavenNative.Wire.WireFormat.Options));
    }

    // Freeze anim (0x55): per-subject animation-freeze bit (own body "" OR puppet "<cid>:<slot>"). Snapshot-able
    // (coalesced last-writer-wins per subject), Seq-stamped like the other relay-opaque lanes so a peer applies the
    // latest transition and re-broadcasts on peer-join carry a fresh Seq.
    public async Task SendFreezeUpdate(HavenNative.Wire.FreezeUpdatePayload p)
    {
        if (!IsConnected) return;
        p.Seq = ++freezeSeq;
        await SendFrame(HavenNative.Wire.WireKind.FreezeUpdate,
            MessagePack.MessagePackSerializer.Serialize(p, HavenNative.Wire.WireFormat.Options));
    }

    // Lights-out (0x56): map-global light/flame suppression bit for one instanced dungeon. Snapshot-able (coalesced
    // last-writer-wins per (source, territory, layer)), Seq-stamped like the other relay-opaque lanes so a peer applies
    // the latest transition and re-offers on peer-join carry a fresh Seq.
    public async Task SendLightsOut(HavenNative.Wire.LightsOutPayload p)
    {
        if (!IsConnected) return;
        p.Seq = ++lightsOutSeq;
        await SendFrame(HavenNative.Wire.WireKind.LightsOut,
            MessagePack.MessagePackSerializer.Serialize(p, HavenNative.Wire.WireFormat.Options));
    }

    private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        // S328ak: reassemble multi-frame messages. A WebSocket message may arrive as several frames (EndOfMessage
        // false until the last), regardless of size - the old code assumed one frame == one message and used
        // result.Count directly, so a roster/burst/large payload would truncate and silently fail to parse (the
        // catch{continue} swallowed it). Accumulate until EndOfMessage, with a cap. The relay fixed the mirror
        // bug server-side; this is the client half. 64KB cap matches the relay's.
        const int MaxMessageBytes = Haven.Core.HousingWire.MaxFrameBytes;
        using var assembly = new System.IO.MemoryStream();

        while (!ct.IsCancellationRequested && ReferenceEquals(ws, socket) && socket.State == WebSocketState.Open)
        {
            try
            {
                assembly.SetLength(0);
                System.Net.WebSockets.WebSocketReceiveResult result;
                bool tooLarge = false;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (!ReferenceEquals(ws, socket)) return;
                    lastReceiveTick = Environment.TickCount64;
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // v0.7.464: record WHY the server closed before we unwind. Application close codes live in
                        // 4000–4999 (RFC 6455 §7.4.2) and .NET surfaces them cast into WebSocketCloseStatus, so an
                        // int cast is the honest read. Only a clean close frame carries this - an aborted socket
                        // throws WebSocketException instead and leaves LastCloseCode null (correctly undiagnosed).
                        var cs = result.CloseStatus ?? ws?.CloseStatus;
                        LastCloseCode = cs.HasValue ? (int?)cs.Value : (int?)null;
                        LastCloseReason = result.CloseStatusDescription ?? ws?.CloseStatusDescription ?? "";
                        log.Information("[HavenNative] Relay closed the connection (code=" +
                            (LastCloseCode?.ToString() ?? "none") + " reason=\"" + LastCloseReason + "\")");
                        break;
                    }
                    if (assembly.Length + result.Count > MaxMessageBytes)
                    {
                        // Oversized message - drain the rest of it and drop, rather than corrupt the stream.
                        tooLarge = true;
                        continue;
                    }
                    assembly.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (tooLarge)
                {
                    log.Warning("[HavenNative] Dropped oversized message (> " + (MaxMessageBytes / 1024) + "KB).");
                    continue;
                }

                var count = (int)assembly.Length;
                NetStats?.RecordIn(count);   // S328ag: wire bytes actually received (now the full reassembled frame)

                // S331 (Stage 4): parse the BINARY downleg frame (spec §1b) - [magic][kind][flags][senderLen][senderId]
                // [timestamp][msgpack payload]. The senderId is RELAY-STAMPED (trusted); the client never asserted it.
                var frameBytes = new byte[count];
                System.Buffer.BlockCopy(assembly.GetBuffer(), 0, frameBytes, 0, count);
                var parsed = HavenNative.Wire.FrameHeader.ParseDownleg(frameBytes);
                if (!parsed.Ok) { log.Warning("[HavenNative] Dropped bad frame (magic/length)."); continue; }
                // Haven: a relay batch is several frames, each handled as if it had arrived on its own.
                if (parsed.Kind == Haven.Core.HousingWire.Batch && parsed.SenderId == HavenNative.Wire.WireFormat.RelaySender)
                {
                    List<byte[]> inner;
                    try { inner = Haven.Core.FrameBatch.Unpack(parsed.Payload); }
                    catch (InvalidDataException ex) { log.Warning("[Haven] Dropped a relay batch that could not be read: {Message}", ex.Message); continue; }
                    foreach (var frame in inner)
                        if (HavenNative.Wire.FrameHeader.ParseDownleg(frame) is { Ok: true } each && each.Kind != Haven.Core.HousingWire.Batch)
                            Dispatch(each, frame.Length);
                    continue;
                }
                Dispatch(parsed, count);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                log.Warning(ex, "[Haven] Relay transport lost; closeCode={CloseCode}, lastReceiveAgeMs={Age}", LastCloseCode?.ToString() ?? "none", Environment.TickCount64 - lastReceiveTick);
                break;
            }
            catch (Exception ex)
            {
                log.Error("[HavenNative] Receive error: " + ex.Message);
                await Task.Delay(100, ct);
            }
        }

        // If we exit the loop unexpectedly, notify
        if (ReferenceEquals(ws, socket) && IsConnected)
        {
            IsConnected = false;
            HousingSceneReady = false;
            requestedScene = null;
            // Preserve composites and native actors while the transport recovers.
            RoomJoinedAcknowledged = false;   // S331: re-arm the join gate
            BeginRecovery();
            OnDisconnected?.Invoke();
        }
    }

    // S331 (Stage 4): binary frame dispatch. kind + relay-stamped senderId + opaque msgpack payload. Replaces the JSON
    // HandleMessage. Lane payloads decode to HavenNative.Wire types → merge into the per-subject composite → snapshot → apply.
    // The subjectId sentinel (spec §4): payload SubjectId == "" means "subject is the stamped sender" (player puppet) -
    // resolve it to senderId. A non-empty SubjectId is an explicit entity id (NPC, later).
    // One downleg frame, whether it arrived alone or inside a relay batch.
    private void Dispatch(HavenNative.Wire.FrameHeader.Parsed parsed, int count)
    {
        if (WireDumpActive) WireDumpCapture(false, parsed.Kind, parsed.Payload, count);

        // S328al: drop our OWN state broadcasts (we don't render our own puppet). EXCEPTION: HostTransfer is a
        // control message ABOUT us - the relay stamps it with senderId = the NEW host (i.e. us, when promoted),
        // so a blanket self-drop meant the new host never learned it was promoted. Let HostTransfer through even
        // when the stamped sender is our own id.
        if (!Haven.Core.RelayReceivePolicy.Accept(parsed.Kind, parsed.SenderId, LocalPeerId)) return;

        // Haven: one bad frame is dropped on its own; it must not stall the frames behind it.
        frameStamp = parsed.Timestamp;
        try { HandleFrame(parsed.Kind, parsed.SenderId, parsed.Payload); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Environment.TickCount64 >= nextFrameWarning)
            { nextFrameWarning = Environment.TickCount64 + 5000; log.Warning("[Haven] Dropped a relay frame (kind 0x{Kind:X2}) that could not be read: {Message}", parsed.Kind, ex.Message); }
        }
    }

    private void HandleFrame(byte kind, string senderId, byte[] payload)
    {
        // Resolve the subject id from a lane payload's SubjectId field: "" → the relay-stamped sender.
        string ResolveSubject(string sid) => string.IsNullOrEmpty(sid) ? senderId : sid;

        if (HandleEditing(kind, payload)) return;
        switch (kind)
        {
            case Haven.Core.HousingWire.Typing:
                if (HousingSceneReady) OnTyping?.Invoke(senderId, Haven.Core.TypingWire.Decode(payload));
                break;
            case Haven.Core.HousingWire.SwitchState:
                if (HousingSceneReady) OnSwitchStates?.Invoke(Haven.Core.SwitchWire.Decode(payload));
                break;
            case Haven.Core.HousingWire.PoseState:
                if (HousingSceneReady) OnPoseEntries?.Invoke(Haven.Core.PoseWire.Decode(payload));
                break;
            case Haven.Core.HousingWire.PoseClose:
                if (HousingSceneReady) OnPoseClose?.Invoke(Haven.Core.PoseClose.Decode(payload));
                break;
            case Haven.Core.HousingWire.Notice:
                OnNotice?.Invoke(Haven.Core.NoticeWire.Decode(payload));
                break;
            case Haven.Core.HousingWire.PoseAdjust:
                if (HousingSceneReady) OnPoseAdjust?.Invoke(Haven.Core.PoseAdjust.Decode(payload));
                break;
            case Haven.Core.HousingWire.MiniGame:
                if (HousingSceneReady) { try { OnMiniGame?.Invoke(Haven.Core.GameMessage.Decode(payload)); } catch (InvalidDataException) { } }
                break;
            case Haven.Core.ContentWire.Ticket:
                ReceiveContentTicket(payload);
                break;
            case Haven.Core.HousingWire.GuestList:
                try { OnGuestList?.Invoke(Haven.Core.GuestList.Decode(payload)); } catch (Exception ex) when (ex is InvalidDataException or JsonException) { }
                break;
            case Haven.Core.HousingWire.PeerConnection:
                var status = JsonSerializer.Deserialize<Haven.Core.PeerConnection>(payload);
                if (status is { PeerId.Length: > 0 and <= 64 }) PeerConnections[status.PeerId] = status.Online;
                break;
            case Haven.Core.HousingWire.RoomChat:
                if (HousingSceneReady)
                {
                    var roomMessage = Haven.Core.RoomChat.Decode(payload);
                    OnRoomChatDelivery?.Invoke(roomMessage, senderId == LocalPeerId, MemberCharacter(senderId));
                    OnRoomChat?.Invoke(roomMessage);
                }
                break;
            case Haven.Core.HousingWire.RoomChatReplay:
                if (HousingSceneReady) OnRoomChatReplay?.Invoke(Haven.Core.RoomChat.Decode(payload), MemberCharacter(senderId));
                break;
            case Haven.Core.HousingWire.RoomChatRejected:
                OnRoomChatRejected?.Invoke(Encoding.UTF8.GetString(payload));
                break;
            case Haven.Core.HousingWire.CompressedSnapshot:
                CacheHouse(Haven.Core.HousingSnapshotCodec.Decode(payload));
                break;
            case Haven.Core.HousingWire.SceneReadyAck:
                if (!HousingSceneReady && payload.Length == 64 && Encoding.ASCII.GetString(payload) == requestedScene)
                {
                    HousingSceneReady = true; OnHousingSceneReady?.Invoke();
                    if (IsRecovering) { IsRecovering = false; RecoveryBlocked = false; lastSentLanes = null; OnRecovered?.Invoke(); }
                }
                break;
            case Haven.Core.HousingWire.Snapshot:
                CacheHouse(Haven.Core.HouseFormat.Parse(payload));
                break;
            case HavenNative.Wire.WireKind.RoomJoined:
            {
                var data = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.RoomJoinedPayload>(payload, HavenNative.Wire.WireFormat.Options);
                // The relay MINTS our peer id - adopt it as our LocalPeerId (S331: relay-authoritative identity).
                if (!string.IsNullOrEmpty(data.AssignedPeerId)) LocalPeerId = data.AssignedPeerId;
                IsHost = data.IsHost;
                if (!string.IsNullOrEmpty(data.RoomId)) CachedRoomId = data.RoomId;   // opaque id → cache for reconnect
                RoomCap = data.RoomCap;   // relay-authoritative capacity (0 = unlimited/unshown)
                RoomJoinedAcknowledged = true;   // gate: the capture/send loop may start now (we have our id)
                resumeToken = data.ResumeToken;
                ResumedAdmission = data.Resumed;
                admission?.TrySetResult(true);
                if (data.Resumed && data.NeedsSnapshot && lastPublishedSnapshot != null)
                    _ = SendHousingSnapshot(lastPublishedSnapshot, restoring: true);
                // Bridge to the existing OnRoomJoined path. The v4 RoomJoined payload carries id+host; the richer
                // zone/spawn fields the old JSON relay sent aren't in v4 (map-state now arrives via the HOST lane +
                // the late-join re-broadcast, not baked into RoomJoined). Invoke with a minimal RoomJoinedData so the
                // existing handler runs; CurrentZoneId=0 → no positional auto-load (correct - HOST lane drives it).
                OnRoomJoined?.Invoke(new RoomJoinedData { PeerIds = System.Array.Empty<string>() });
                break;
            }

            case HavenNative.Wire.WireKind.PeerJoined:
            {
                string joiner = senderId;
                ulong joinerContentId = 0;
                string joinerName = "";
                try
                {
                    var p = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.PeerJoinedPayload>(payload, HavenNative.Wire.WireFormat.Options);
                    if (!string.IsNullOrEmpty(p.PeerId)) joiner = p.PeerId;
                    joinerContentId = p.ContentId;      // relay-stamped identity (0 from a pre-Part-2 relay)
                    joinerName = p.CharacterName;
                }
                catch { /* empty/malformed payload → the header sender is the joiner, identity stays blank */ }
                HousingMembers[joiner] = new(joinerContentId, joinerName);
                OnPeerJoined?.Invoke(joiner, joinerContentId, joinerName);
                break;
            }

            case HavenNative.Wire.WireKind.PeerLeft:
            {
                string leaver = senderId;
                string newHost = "";
                try
                {
                    var p = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.PeerLeftPayload>(payload, HavenNative.Wire.WireFormat.Options);
                    if (!string.IsNullOrEmpty(p.PeerId)) leaver = p.PeerId;
                    newHost = p.NewHostId;
                    if (p.MissingExpansion > 0)
                        OnGuestMissingExpansion?.Invoke(HousingMembers.TryGetValue(leaver, out var gone) ? gone.Name : "", p.MissingExpansion);
                }
                catch { /* empty/malformed payload → the header sender is the leaver, no succession info */ }
                OnPeerLeft?.Invoke(leaver);
                PeerConnections.TryRemove(leaver, out _);
                HousingMembers.TryRemove(leaver, out _);
                EvictComposite(leaver);   // Stage 2a: drop the leaver's lane composite
                if (!string.IsNullOrEmpty(newHost))
                {
                    log.Information("[HavenNative] Host transfer: new host = " + newHost[..Math.Min(6, newHost.Length)]);
                    OnHostTransfer?.Invoke(newHost);
                }
                break;
            }

            case HavenNative.Wire.WireKind.HostTransfer:
            {
                // The relay stamps senderId = the new host's id in the HEADER (spec §3.1 exception) - that's the
                // authoritative source. The payload MAY also carry it, but we don't require it: read the header sender
                // first, and only fall back to the payload if the header is somehow empty. This is robust to an empty
                // HostTransfer payload (the relay may send just the header stamp) - deserializing empty bytes would throw.
                string newHost = senderId;
                if (string.IsNullOrEmpty(newHost) && payload.Length > 0)
                {
                    try { newHost = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.HostTransferPayload>(payload, HavenNative.Wire.WireFormat.Options).TargetPeerId; }
                    catch { /* header was the source of truth anyway */ }
                }
                if (!string.IsNullOrEmpty(newHost))
                {
                    log.Information("[HavenNative] Received HostTransfer: new host = " + newHost[..Math.Min(6, newHost.Length)]);
                    OnHostTransfer?.Invoke(newHost);
                }
                break;
            }

            // ── LANE RECEIVERS (Stage 4 binary) ── decode → merge into per-subject composite → snapshot → apply.
            case HavenNative.Wire.WireKind.HotUpdate:
            {
                var h = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.HotPayload>(payload, HavenNative.Wire.WireFormat.Options);
                // Haven: other players' numbers reach native calls; a frame with an impossible float is dropped.
                if (!Haven.Core.SafeAngles.Sane(h.X, h.Y, h.Z) || !Haven.Core.SafeAngles.Sane(h.Rotation, h.MountPitch, 0) || !Haven.Core.SafeAngles.Sane(h.BodyDrawOffsetX, h.BodyDrawOffsetY, h.BodyDrawOffsetZ)) break;
                string subj = ResolveSubject(h.SubjectId);
                var comp = CompositeFor(subj);
                LaneProjection.MergeHotWire(comp, h);
                comp.RelayMs = frameStamp;
                // Hand a SNAPSHOT COPY to the apply path - interpolation stores references, so the shared mutating
                // composite would alias every snapshot to the latest position → flip-book. A copy per message gives
                // interpolation distinct points to glide between.
                OnTransformReceived?.Invoke(subj, SnapshotComposite(comp), /*isHotLane:*/ true);
                break;
            }
            case HavenNative.Wire.WireKind.WarmUpdate:
            {
                var w = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.WarmPayload>(payload, HavenNative.Wire.WireFormat.Options);
                if (!Haven.Core.SafeAngles.Sane(w.MinionOffX, w.MinionOffY, w.MinionOffZ) || !Haven.Core.SafeAngles.Sane(w.MinionRot) || !Haven.Core.SafeAngles.Sane(w.FaceCamX, w.FaceCamY, w.FaceCamZ)
                    || !Haven.Core.SafeAngles.Sane(w.GazeEyesX, w.GazeEyesY, w.GazeEyesZ) || !Haven.Core.SafeAngles.Sane(w.GazeBodyX, w.GazeBodyY, w.GazeBodyZ)
                    || !Haven.Core.SafeAngles.Sane(w.GazeHeadX, w.GazeHeadY, w.GazeHeadZ) || !Haven.Core.SafeAngles.Sane(w.ActionTgtX, w.ActionTgtY, w.ActionTgtZ)) break;
                CleanWarm?.Invoke(w);
                string subj = ResolveSubject(w.SubjectId);
                var comp = CompositeFor(subj);
                LaneProjection.MergeWarmWire(comp, w);
                OnTransformReceived?.Invoke(subj, SnapshotComposite(comp), /*isHotLane:*/ false);
                break;
            }
            case HavenNative.Wire.WireKind.ColdUpdate:
            {
                var d = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.ColdPayload>(payload, HavenNative.Wire.WireFormat.Options);
                d.MonikerName = Haven.Core.SafeText.Clean(d.MonikerName, 64);
                string subj = ResolveSubject(d.SubjectId);
                var comp = CompositeFor(subj);
                LaneProjection.MergeColdWire(comp, d);
                OnTransformReceived?.Invoke(subj, SnapshotComposite(comp), /*isHotLane:*/ false);
                break;
            }
            case HavenNative.Wire.WireKind.HostUpdate:
            {
                var hd = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.HostPayload>(payload, HavenNative.Wire.WireFormat.Options);
                string subj = ResolveSubject(hd.SubjectId);
                var comp = CompositeFor(subj);
                LaneProjection.MergeHostWire(comp, hd);
                OnTransformReceived?.Invoke(subj, SnapshotComposite(comp), /*isHotLane:*/ false);
                break;
            }

            // ── FEAT-R2 (HDM disguise-sync) receivers ── decode → hand the self-describing payload to the receiver,
            // which resolves SenderContentId→objectIndex and drives HDM's IPC. Kind-agnostic (Monster/Demi/Human ride
            // the same atom). A puppet's per-frame transform rides PuppetMove (0x52), decoded just below.
            case HavenNative.Wire.WireKind.DisguiseUpdate:
            {
                var d = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.DisguiseUpdatePayload>(payload, HavenNative.Wire.WireFormat.Options);
                if (!float.IsFinite(d.Scale) || d.Scale is < 0 or > 100 || !float.IsFinite(d.VOffset) || MathF.Abs(d.VOffset) > 100) break;
                OnDisguiseReceived?.Invoke(d);
                break;
            }
            case HavenNative.Wire.WireKind.ActionPulse:
            {
                var a = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.ActionPulsePayload>(payload, HavenNative.Wire.WireFormat.Options);
                OnActionPulseReceived?.Invoke(a);
                break;
            }
            case HavenNative.Wire.WireKind.PuppetMove:
            {
                var pm = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.PuppetMovePayload>(payload, HavenNative.Wire.WireFormat.Options);
                if (!Haven.Core.SafeAngles.Sane(pm.X, pm.Y, pm.Z) || !Haven.Core.SafeAngles.Sane(pm.Rot)) break;
                OnPuppetMoveReceived?.Invoke(pm);
                break;
            }
            case HavenNative.Wire.WireKind.OwnBodyHidden:
            {
                var ob = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.OwnBodyHiddenPayload>(payload, HavenNative.Wire.WireFormat.Options);
                OnOwnBodyHiddenReceived?.Invoke(ob);
                break;
            }
            case HavenNative.Wire.WireKind.LobbyNameplate:
            {
                var ln = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.LobbyNameplatePayload>(payload, HavenNative.Wire.WireFormat.Options);
                OnLobbyNameplateReceived?.Invoke(ln);
                break;
            }
            case HavenNative.Wire.WireKind.FreezeUpdate:
            {
                var fu = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.FreezeUpdatePayload>(payload, HavenNative.Wire.WireFormat.Options);
                OnFreezeReceived?.Invoke(fu);
                break;
            }
            case HavenNative.Wire.WireKind.LightsOut:
            {
                var lo = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.LightsOutPayload>(payload, HavenNative.Wire.WireFormat.Options);
                OnLightsOutReceived?.Invoke(lo);
                break;
            }

            case HavenNative.Wire.WireKind.ZoneLoadExecute:
            {
                // ZoneLoad carries its JSON as payload bytes (not yet ported to a wire POCO - see SendZoneLoad).
                var json = Encoding.UTF8.GetString(payload);
                var zd = JsonSerializer.Deserialize<ZoneLoadData>(json);
                if (zd != null) OnZoneLoadReceived?.Invoke(zd);
                break;
            }

            case HavenNative.Wire.WireKind.SessionEnd:
                CancelRecovery();
                OnSessionEnded?.Invoke();
                break;

            case HavenNative.Wire.WireKind.Pong:
                if (payload.Length == 8) RoundTripMs = Haven.Core.RecoveryPolicy.SmoothRoundTrip(RoundTripMs, Environment.TickCount64 - BitConverter.ToInt64(payload));
                // NB-27: Pong is a recognized-and-swallowed keepalive. The old lastPongReceived timestamp field was
                // set here but never read anywhere (no timeout/health check consumed it) - dead state, removed. Keep
                // the case so an inbound Pong is still consumed cleanly rather than falling through to the default.
                break;

            // v0.7.464 (RMS QA F3, soft tier): the relay is dropping some of our ingress but keeping us connected.
            // Payload is empty by contract - do NOT deserialize it (a future version may append fields; parsing an
            // empty buffer today would throw and turn an advisory into a log error). Advisory only: no teardown.
            case HavenNative.Wire.WireKind.RateLimited:
                OnRateLimited?.Invoke();
                break;

            case HavenNative.Wire.WireKind.Error:
            {
                var e = MessagePack.MessagePackSerializer.Deserialize<HavenNative.Wire.ErrorPayload>(payload, HavenNative.Wire.WireFormat.Options);
                if (e.Code is HavenNative.Wire.ErrCode.Kicked or HavenNative.Wire.ErrCode.Banned) CancelRecovery();
                if (IsRecovering && e.Code is not (HavenNative.Wire.ErrCode.Kicked or HavenNative.Wire.ErrCode.Banned))
                {
                    RecoveryBlocked = true;
                    // Say why: most often the relay was updated to a housing protocol this Haven doesn't speak.
                    var reason = e.Message is { Length: > 0 and <= 200 } text && text.All(c => !char.IsControl(c)) ? " " + text : "";
                    RecoveryStatus = reason.Contains("protocol revision", StringComparison.Ordinal)
                        ? "Offline · Haven's relay was updated and this gathering can't continue. Leave the home, update Haven, then host again." + reason
                        : "Offline · recovery refused. The house remains open; leave when ready." + reason;
                    OnRecoveryNotice?.Invoke(RecoveryStatus);
                    admission?.TrySetResult(false);
                    log.Warning("[Haven] Relay recovery refused; code={Code}, {Message}. Local scene preserved.", e.Code, e.Message);
                    return;
                }
                if (e.Code == 1) CachedRoomId = "";   // RoomNotFound → the cached opaque id is stale, drop it
                OnError?.Invoke(e.Code, e.Message);
                break;
            }
        }
    }

    // ── S331 (Stage 4): binary frame send. Builds an UPLEG frame [magic][kind][flags][timestamp][payload] and
    // sends it as a WS BINARY message. The payload is already-serialized msgpack bytes. No sender/room - the relay
    // stamps the trusted sender and knows the room from the connection (spec §1a). This replaces the JSON Send path.
    // App-level lobby keepalive loop (see KeepAliveIntervalMs). Runs for the life of the connection; ticks every
    // ~20s and, while the socket is up, sends a WireKind.Ping (empty payload). The relay answers Pong directly and
    // never fans it out. Gated on IsConnected only - a keepalive is purely socket-level, needed the moment we connect
    // (before RoomJoined, before any zone-load). Cancelled via the shared cts on Disconnect, so the Task.Delay breaks
    // and the loop exits cleanly. Every failure is swallowed - a keepalive must never fault the session.
    private async Task KeepAliveLoop(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ReferenceEquals(ws, socket))
            {
                await Task.Delay(KeepAliveIntervalMs, ct).ConfigureAwait(false);
                if (IsConnected && ReferenceEquals(ws, socket) && socket.State == WebSocketState.Open)
                {
                    if (Environment.TickCount64 - lastReceiveTick > Haven.Core.RecoveryPolicy.WatchdogTimeout(RoundTripMs).TotalMilliseconds)
                    { log.Warning("[Haven] Relay receive watchdog expired; preserving scene and reconnecting."); socket.Abort(); return; }
                    // Never awaited: a ping stuck behind a large send must not stop the watchdog above from running.
                    _ = SendFrame(HavenNative.Wire.WireKind.Ping, BitConverter.GetBytes(Environment.TickCount64), socket);
                }
            }
        }
        catch (OperationCanceledException) { /* disconnect - normal exit */ }
        catch (Exception ex) { log.Debug("[HavenNative] keepalive loop ended: " + ex.Message); }
    }

    // Haven: returns whether the frame was handed to the socket. A send that does not finish within 15 s means the
    // connection is stuck (a half-open link, a hung relay): the socket is aborted, so recovery starts at once instead
    // of every later send queuing behind it until the silence watchdog notices.
    private async Task<bool> SendFrame(byte kind, byte[] msgpackPayload, ClientWebSocket? expectedSocket = null)
    {
        if (expectedSocket != null && !ReferenceEquals(ws, expectedSocket)) return false;
        if (ws?.State != WebSocketState.Open) return false;
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var frame = HavenNative.Wire.FrameHeader.BuildUpleg(kind, ts, msgpackPayload);
        NetStats?.RecordOut(frame.Length, KindName(kind));   // upleg bytes (the minimality figure netdiag/dashboard track)
        if (WireDumpActive) WireDumpCapture(true, kind, msgpackPayload, frame.Length);
        try
        {
            var socket = expectedSocket ?? ws;
            var token = cts?.Token ?? CancellationToken.None;
            using var stuck = CancellationTokenSource.CreateLinkedTokenSource(token);
            stuck.CancelAfter(TimeSpan.FromSeconds(15));
            await havenSendGate.WaitAsync(stuck.Token);
            try
            {
                if (socket?.State != WebSocketState.Open) return false;
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, stuck.Token);
                return true;
            }
            finally { havenSendGate.Release(); }
        }
        catch (OperationCanceledException) when (cts?.IsCancellationRequested != true)
        {
            log.Warning("[Haven] A send to the relay did not finish in 15 s; reconnecting.");
            try { (expectedSocket ?? ws)?.Abort(); } catch (Exception) { }
            return false;
        }
        catch (Exception ex)
        {
            log.Error("[HavenNative] SendFrame error: " + ex.Message);
            return false;
        }
    }

    // Kind byte → name, for metering + wiredump (mirrors the relay's MsgType.Name).
    private static string KindName(byte kind) => kind switch
    {
        HavenNative.Wire.WireKind.JoinRoom => "JoinRoom",
        HavenNative.Wire.WireKind.LeaveRoom => "LeaveRoom",
        HavenNative.Wire.WireKind.RoomJoined => "RoomJoined",
        HavenNative.Wire.WireKind.PeerJoined => "PeerJoined",
        HavenNative.Wire.WireKind.PeerLeft => "PeerLeft",
        HavenNative.Wire.WireKind.HostTransfer => "HostTransfer",
        HavenNative.Wire.WireKind.RateLimited => "RateLimited",
        HavenNative.Wire.WireKind.HotUpdate => "HotUpdate",
        HavenNative.Wire.WireKind.WarmUpdate => "WarmUpdate",
        HavenNative.Wire.WireKind.ColdUpdate => "ColdUpdate",
        HavenNative.Wire.WireKind.HostUpdate => "HostUpdate",
        HavenNative.Wire.WireKind.EventPulse => "EventPulse",
        HavenNative.Wire.WireKind.DisguiseUpdate => "DisguiseUpdate",
        HavenNative.Wire.WireKind.ActionPulse => "ActionPulse",
        HavenNative.Wire.WireKind.PuppetMove => "PuppetMove",
        HavenNative.Wire.WireKind.OwnBodyHidden => "OwnBodyHidden",
        HavenNative.Wire.WireKind.LobbyNameplate => "LobbyNameplate",
        HavenNative.Wire.WireKind.FreezeUpdate => "FreezeUpdate",
        HavenNative.Wire.WireKind.LightsOut => "LightsOut",
        HavenNative.Wire.WireKind.ZoneLoadExecute => "ZoneLoadExecute",
        HavenNative.Wire.WireKind.SessionEnd => "SessionEnd",
        HavenNative.Wire.WireKind.Ping => "Ping",
        HavenNative.Wire.WireKind.Pong => "Pong",
        HavenNative.Wire.WireKind.Error => "Error",
        _ => "0x" + kind.ToString("X2"),
    };

    public void Dispose()
    {
        _ = Disconnect();
        cts?.Dispose();
    }
}

