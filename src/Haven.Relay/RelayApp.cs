using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Haven.Core;
using HavenNative.Wire;
using MessagePack;

namespace Haven.Relay;

public static class RelayApp
{
    public static WebApplication Build(string[] args, IEnumerable<RecoveryCheckpoint>? initialRecovery = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (!args.Any(x => x.StartsWith("--urls", StringComparison.OrdinalIgnoreCase))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            builder.WebHost.UseUrls("http://localhost:9420");
        builder.Logging.AddFilter("Haven.Relay", LogLevel.Information);
        var app = builder.Build();
        var recoveryKey = app.Configuration["HAVEN_RECOVERY_KEY"];
        var hub = new RoomHub(app.Services.GetRequiredService<ILogger<RoomHub>>(), !string.IsNullOrEmpty(recoveryKey))
        { ContentKey = app.Configuration["HAVEN_CONTENT_KEY"] is { Length: >= 32 } contentKey ? contentKey : null,
          AccountsRequired = app.Configuration["HAVEN_ACCOUNTS"] == "required" };
        foreach (var checkpoint in initialRecovery ?? []) hub.TryRestore(checkpoint);
        app.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.MapGet("/health", () => Results.Ok(new { service = "Haven", version = typeof(RelayApp).Assembly.GetName().Version?.ToString(3), protocol = WireFormat.ProtocolVersion, housingProtocol = HousingWire.ProtocolRevision, typingProtocol = 1, editingProtocol = 2, switchProtocol = SwitchWire.Protocol, poseProtocol = PoseWire.Protocol, noticeProtocol = NoticeWire.Protocol, gameProtocol = GameMessage.Protocol, inviteProtocol = GuestList.Protocol, recoveryIdentityProtocol = 1,
            contentProtocol = hub.ContentKey == null ? 0 : 6, bootId = hub.BootId, uptimeSeconds = (long)(DateTimeOffset.UtcNow - hub.StartedAt).TotalSeconds }));
        // Internal routes answer only the edge, which holds the recovery secret.
        bool FromEdge(HttpContext context) => !string.IsNullOrEmpty(recoveryKey) && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-Haven-Recovery"].ToString())), SHA256.HashData(Encoding.UTF8.GetBytes(recoveryKey)));
        // Operator notices: GET the standing one, POST a new one (sent to everyone connected), DELETE to withdraw it.
        app.MapMethods("/internal/notice", ["GET", "POST", "DELETE"], async (HttpContext context) =>
        {
            if (!FromEdge(context)) return Results.NotFound();
            if (HttpMethods.IsGet(context.Request.Method)) return Results.Ok(hub.CurrentNotice);
            if (HttpMethods.IsDelete(context.Request.Method)) { hub.PostNotice(new("clear", "clear", "")); return Results.Ok(); }
            if (context.Request.ContentLength is null or > 8192) return Results.BadRequest();
            RelayNotice? posted;
            try { posted = await JsonSerializer.DeserializeAsync<RelayNotice>(context.Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { return Results.BadRequest(); }
            return posted != null && hub.PostNotice(posted) ? Results.Ok(hub.CurrentNotice) : Results.BadRequest();
        });
        app.MapPost("/internal/restore", async (HttpContext context) =>
        {
            if (!FromEdge(context)) return Results.NotFound();
            if (context.Request.ContentLength is > 2 * HouseFormat.MaxBytes) return Results.BadRequest();
            if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is {} bodyLimit)
                bodyLimit.MaxRequestBodySize = 2 * HouseFormat.MaxBytes;
            RecoveryCheckpoint? checkpoint;
            try { checkpoint = await JsonSerializer.DeserializeAsync<RecoveryCheckpoint>(context.Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { checkpoint = null; }
            // A checkpoint this relay can't take (damaged, or refused by a newer release's checks) is skipped and logged:
            // refusing it made the edge stop hydrating, and every connection failed until the record expired.
            if (checkpoint != null) hub.TryRestore(checkpoint);
            return Results.Ok();
        });
        app.MapGet("/", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            // Cloudflare sets CF-Connecting-IP at its edge; the container has no other ingress. Self-hosted relays
            // fall back to the socket's address.
            // The character the edge verified for this connection's account (AccountStamp); null when none. A signed-in
            // connection counts against its account's limits, wherever it connects from; any other, its address's.
            var verified = AccountStamp.Read(context.Request.Headers[AccountStamp.Header].ToString(), recoveryKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var address = verified != null ? RoomHub.AccountKey(verified)
                : RoomHub.AddressKey(context.Request.Headers["CF-Connecting-IP"].ToString(), context.Connection.RemoteIpAddress);
            if (!hub.TryOpen(address)) { context.Response.StatusCode = 429; return; }
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await hub.Run(socket, context.RequestAborted, address, verified);
            }
            finally { hub.Close(address); }
        });
        return app;
    }
}

internal sealed partial class RoomHub(ILogger<RoomHub> log, bool durableRecovery)
{
    public string BootId { get; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    private const int RoomCap = 20;
    // The largest housing zone request (ZoneLoadExecute) the relay keeps: a territory, a spawn point and two short names.
    internal const int MaxZoneBytes = 4096;
    private readonly object gate = new();
    private readonly Dictionary<string, Room> rooms = [];
    private int connections;
    private readonly Dictionary<string, int> openByAddress = [];
    private const int ConnectionsPerAddress = 8, RoomsPerAddress = 3;
    private long nextExpiry;

    public static string AddressKey(string forwarded, System.Net.IPAddress? remote) => RelayAddress.Key(forwarded, remote);
    /// <summary>What a signed-in connection's limits are counted by: its Discord account, never an address.</summary>
    public static string AccountKey(VerifiedAccount verified) => "account:" + verified.Account;
    /// <summary>
    /// At most a few connections per account (signed in) or address (not): a handful of sockets, or one account on many
    /// addresses, must not be able to fill the relay. Gatherings are limited by the same key (RoomsPerAddress).
    /// </summary>
    public bool TryOpen(string address)
    {
        if (address.Length == 0) return true;
        lock (openByAddress)
        {
            var open = openByAddress.GetValueOrDefault(address);
            if (open >= ConnectionsPerAddress) return false;
            openByAddress[address] = open + 1;
            return true;
        }
    }
    public void Close(string address)
    {
        lock (openByAddress)
            if (openByAddress.TryGetValue(address, out var open)) { if (open <= 1) openByAddress.Remove(address); else openByAddress[address] = open - 1; }
    }

    /// <summary>Accounts are on (hosted relay): every join and content session needs a verified character.</summary>
    public bool AccountsRequired { get; init; }
    // Null when allowed; otherwise why this connection may not act as that character.
    private string? AccountRefusal(Peer peer, ulong contentId) =>
        peer.Verified is { } verified ? verified.ContentId == contentId ? null : "This connection is signed in for a different character. Reconnect from that character."
        : AccountsRequired ? "Sign in with Discord in Haven (Settings → Connection) to join gatherings and share pictures." : null;

    public async Task Run(WebSocket socket, CancellationToken requestAborted, string address = "", VerifiedAccount? verified = null)
    {
        if (Interlocked.Increment(ref connections) > 128)
        {
            Interlocked.Decrement(ref connections);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Relay full", requestAborted);
            return;
        }
        using var peer = new Peer(socket, requestAborted) { Address = address, Verified = verified };
        var sender = peer.SendLoop();
        try
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
            admission.CancelAfter(TimeSpan.FromSeconds(10));
            var first = FrameHeader.ParseUpleg(await Receive(socket, 16 * 1024, admission.Token));
            if (first.Ok && first.Kind == ContentWire.Session)
            {
                peer.FinalDeparture = true; peer.DisconnectReason = "content_session";
                await RunContentSession(socket, peer, first.Payload);
                return;
            }
            if (!first.Ok || first.Kind != WireKind.JoinRoom) throw new InvalidDataException("Join required.");
            var join = Decode<JoinPayload>(first.Payload);
            if (AccountRefusal(peer, join.ContentId) is { } refusal)
            { Error(peer, ErrCode.Generic, refusal); peer.FinalDeparture = true; peer.DisconnectReason = "account_required"; return; }
            lock (gate) Admit(peer, join);
            if (peer.Room == null) { peer.FinalDeparture = true; peer.DisconnectReason = "admission_rejected"; return; }
            var window = DateTime.UtcNow;
            int frames = 0, bytes = 0;
            while (socket.State == WebSocketState.Open && !peer.Token.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                var data = await Receive(socket, HousingWire.MaxFrameBytes, idle.Token);
                if ((DateTime.UtcNow - window).TotalSeconds >= 1) { window = DateTime.UtcNow; frames = bytes = 0; }
                peer.LastActivity = Environment.TickCount64;
                bytes += data.Length;
                // Over the allowance the frame is dropped and the sender told (0x08, advisory); the peer stays in the
                // room and resumable. Only far beyond it is the connection closed.
                if (++frames > 600 || bytes > 8 * HousingWire.MaxFrameBytes) { peer.DisconnectReason = "rate_limit"; throw new InvalidDataException("Rate exceeded."); }
                if (frames > 120 || bytes > 2 * HousingWire.MaxFrameBytes)
                {
                    if (frames == 121 || bytes - data.Length <= 2 * HousingWire.MaxFrameBytes) peer.Queue(WireKind.RateLimited, WireFormat.RelaySender, []);
                    continue;
                }
                var frame = FrameHeader.ParseUpleg(data);
                if (!frame.Ok) throw new InvalidDataException("Malformed frame.");
                // A full house is the heaviest frame. Everything about it that depends only on the frame (decoding,
                // validating, hashing, re-encoding) is done here, outside the lock every room shares, so one large
                // publish no longer stalls the others; the lock covers only the room's state and the queues.
                PreparedSnapshot? prepared = null;
                var publishes = false;
                if (frame.Kind is HousingWire.Snapshot or HousingWire.RestoreSnapshot or HousingWire.CompressedSnapshot)
                    // Only a frame the room would accept is prepared: a guest's full house is refused unread, as before.
                    lock (gate) { if (peer.Room == null) break; publishes = frame.Kind == HousingWire.RestoreSnapshot || peer.Id == peer.Room.HostId; }
                if (publishes)
                {
                    if (!peer.SnapshotRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency))
                    {
                        lock (gate) { if (peer.Room == null) break; Result(peer, Guid.Empty, "error", "The home is being published too quickly. Wait a moment and try again."); }
                        continue;
                    }
                    prepared = PreparedSnapshot.Of(frame.Kind, frame.Payload);
                }
                lock (gate)
                {
                    if (peer.Room == null) break;
                    if (!Handle(peer, frame, prepared)) break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException
            or MessagePackSerializationException or JsonException or ArgumentException)
        {
            peer.FinalDeparture |= ex is InvalidDataException or MessagePackSerializationException or JsonException or ArgumentException;
            peer.DisconnectReason ??= ex switch
            {
                InvalidDataException { Message: "Binary frames required." } => "binary_required",
                InvalidDataException { Message: "Join required." } => "join_required",
                InvalidDataException => "invalid_frame",
                MessagePackSerializationException => "invalid_payload",
                JsonException => "invalid_json",
                ArgumentException => "invalid_argument",
                _ => null
            };
            log.LogWarning("relay_transport_closed boot={Boot} peer={Peer} reason={Reason} errorType={Type}",
                BootId, peer.Id, peer.DisconnectReason ?? (Environment.TickCount64 - peer.LastActivity > 59000 ? "idle_timeout" : "transport_lost"), ex.GetType().Name);
        }
        finally
        {
            lock (gate) { if (peer.FinalDeparture) Depart(peer); else Suspend(peer); }
            peer.Finish();
            try { await sender.WaitAsync(TimeSpan.FromSeconds(3)); } catch { peer.Abort(); }
            // Complete the handshake before disposing the upgraded HTTP connection.
            // Returning immediately after CloseOutput can truncate buffered control frames.
            if (socket.State == WebSocketState.CloseSent)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", closing.Token); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            }
            Interlocked.Decrement(ref connections);
        }
    }

    private void Admit(Peer peer, JoinPayload join)
    {
        ExpireReservations();
        if (join.HavenProtocolRevision != HousingWire.ProtocolRevision)
        { Error(peer, ErrCode.Generic, $"This relay requires Haven housing protocol revision {HousingWire.ProtocolRevision} (Haven 0.68.0 or newer)."); return; }
        if (!string.IsNullOrEmpty(join.ResumeToken)) { Resume(peer, join); return; }
        var password = join.RoomPassword ?? "";
        if (join.ContentId == 0 || PoseNpcs.IsNpc(join.ContentId) || string.IsNullOrWhiteSpace(join.CharacterName) || join.CharacterName.Length > 80 || join.CharacterName.Any(char.IsControl)
            || password.Length is < 8 or > 128 || join.NearbyContentIds?.Length > 256)
        { Error(peer, ErrCode.Generic, "Use an 8–128 character visit code and a valid character."); return; }
        // No house was ever published and nobody is connected: this is an abandoned lobby,
        // not a recoverable visit. A failed initial entry must not reserve a character for 30 minutes.
        // So is a lobby that never published a scene within ten minutes: holding one blocks room creation and characters.
        foreach (var abandoned in rooms.Values.Where(r => r.Scene == null && (r.Peers.All(p => !p.Online) || NowMs - r.CreatedAt > 600_000)).ToArray())
        {
            Checkpoint(abandoned, peer, removed: true); // Persist tombstone on this live stream before admission.
            foreach (var member in abandoned.Peers.ToArray()) Depart(member);
        }
        if (rooms.Values.SelectMany(x => x.Peers).Any(x => x.ContentId == join.ContentId))
        { Error(peer, ErrCode.AlreadyHosting, "This character is already in a room."); return; }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        Room? room;
        if (join.CreateIfMissing == true)
        {
            if (rooms.Count >= 32) { Error(peer, ErrCode.RoomFull, "Relay room capacity reached."); return; }
            if (peer.Address.Length > 0 && rooms.Values.Count(r => r.Address == peer.Address) >= RoomsPerAddress)
            { Error(peer, ErrCode.RoomFull, peer.Verified != null ? "Too many gatherings are open on your account." : "Too many gatherings are open from this connection."); return; }
            if (rooms.Values.Any(x => CryptographicOperations.FixedTimeEquals(x.PasswordHash, hash)))
            { Error(peer, ErrCode.AlreadyHosting, "Choose a different visit code."); return; }
            room = new Room { Id = Guid.NewGuid().ToString("N"), PasswordHash = hash, HostId = peer.Id, Owner = join.ContentId, Address = peer.Address, CreatedAt = NowMs };
            rooms.Add(room.Id, room);
        }
        else
        {
            room = !string.IsNullOrEmpty(join.RoomId) ? rooms.GetValueOrDefault(join.RoomId)
                : rooms.Values.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(x.PasswordHash, hash));
            if (room == null) { Error(peer, ErrCode.RoomNotFound, "Visit code not found."); return; }
            if (!CryptographicOperations.FixedTimeEquals(room.PasswordHash, hash)) { Error(peer, ErrCode.WrongPassword, "Incorrect visit code."); return; }
            if (room.Banned.Contains(join.ContentId)) { Error(peer, ErrCode.Banned, "Removed from this room."); return; }
            if (InviteRefusal(room, join.ContentId) is { } uninvited) { Error(peer, ErrCode.Generic, uninvited); return; }
            // The alpha reuses rendered characters. Meet in the same real zone before joining.
            if (!(join.NearbyContentIds ?? []).Any(cid => room.Peers.Any(p => p.ContentId == cid)))
            { Error(peer, ErrCode.NotHosting, "Gather within rendering range before joining."); return; }
        }
        if (room.Peers.Count >= RoomCap) { Error(peer, ErrCode.RoomFull, "Room full."); return; }
        peer.ContentId = join.ContentId;
        peer.ContentSecret = ContentLocker.IsSecret(join.ContentSecret) ? join.ContentSecret : "";
        peer.Batching = join.Batching;
        // A signed-in character goes by the name it was first tied to its account with (AccountStamp), not one the client
        // claims, so nobody passes their own character off as a friend's.
        peer.Name = peer.Verified is { Name.Length: > 0 } bound ? bound.Name : join.CharacterName;
        peer.Room = room;
        room.Peers.Add(peer);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        peer.ResumeHash = SHA256.HashData(Encoding.ASCII.GetBytes(token));
        Checkpoint(room, peer);
        peer.Queue(WireKind.RoomJoined, WireFormat.RelaySender, Encode(new RoomJoinedPayload
        { AssignedPeerId = peer.Id, IsHost = peer.Id == room.HostId, RoomId = room.Id, RoomCap = RoomCap, ResumeToken = token }));
        foreach (var other in room.Peers.Where(p => p != peer))
        {
            peer.Queue(WireKind.PeerJoined, other.Id, Encode(new PeerJoinedPayload { PeerId = other.Id, ContentId = other.ContentId, CharacterName = other.Name }));
            peer.Queue(HousingWire.PeerConnection, WireFormat.RelaySender, JsonSerializer.SerializeToUtf8Bytes(new PeerConnection(other.Id, other.Online, other.ResumeUntil)));
            other.Queue(WireKind.PeerJoined, peer.Id, Encode(new PeerJoinedPayload { PeerId = peer.Id, ContentId = peer.ContentId, CharacterName = peer.Name }));
        }
        log.LogInformation("relay_admitted boot={Boot} room={Room} peer={Peer}", BootId, room.Id, peer.Id);
        peer.Queue(WireKind.HostTransfer, room.HostId, Encode(new HostTransferPayload { TargetPeerId = room.HostId }));
        if (room.Zone != null) peer.Queue(WireKind.ZoneLoadExecute, room.HostId, room.Zone);
        if (room.House != null) peer.Queue(HousingWire.CompressedSnapshot, WireFormat.RelaySender, room.House);
        SendEditing(room, peer);
    }

    private bool Handle(Peer peer, FrameHeader.ParsedUpleg frame, PreparedSnapshot? prepared = null)
    {
        var room = peer.Room!;
        var host = peer.Id == room.HostId;
        var payload = frame.Payload;
        if (NowMs >= nextExpiry) { nextExpiry = NowMs + 1000; ExpireReservations(); }
        if (frame.Kind == WireKind.Ping)
        {
            if (payload.Length > 8) throw new InvalidDataException("Invalid ping.");
            if (DateTimeOffset.UtcNow - room.LastCheckpoint > TimeSpan.FromSeconds(60)) Checkpoint(room, peer);
            peer.Queue(WireKind.Pong, WireFormat.RelaySender, payload); return true;
        }
        if (frame.Kind == WireKind.LeaveRoom)
        {
            // A guest whose game lacks the home's zone says which expansion, so the others (the host) learn why.
            peer.MissingExpansion = GameDataPaths.LeaveExpansion(payload);
            peer.FinalDeparture = true; peer.DisconnectReason = "explicit_leave"; return false;
        }
        if (frame.Kind is WireKind.HostUpdate or WireKind.ZoneLoadExecute or WireKind.SessionEnd or WireKind.KickPeer or WireKind.HostTransfer
            || frame.Kind is HousingWire.Snapshot or HousingWire.CompressedSnapshot)
            if (!host) { Error(peer, ErrCode.NotHost, "Host-only operation."); return true; }
        if (HandleEditing(peer, frame.Kind, payload)) return true;
        switch (frame.Kind)
        {
            case HousingWire.Typing:
                var typing = TypingWire.Decode(payload);
                if (!peer.Ready || room.Scene == null) return true;
                if (!peer.TypingRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency)) return true;
                foreach (var occupant in room.Peers.Where(p => p != peer && p.Ready)) occupant.Queue(HousingWire.Typing, peer.Id, [(byte)typing]);
                return true; // Never cache, checkpoint or replay typing presence.
            case ContentWire.TicketRequest:
                HandleContentTicket(peer, payload);
                return true;
            case HousingWire.SwitchState:
                var switches = SwitchWire.Decode(payload);
                if (!peer.Ready || room.Current == null) return true;
                if (!peer.SwitchRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency)) return true;
                var present = room.Current.Furnishings.Select(f => f.Id).ToHashSet();
                var accepted = switches.Where(s => present.Contains(s.Furnishing)).ToArray();
                // Only the owner sends many at once (their saved sides, on scene ready); anyone else's click is one switch,
                // and a frame flipping every switch in the home made each PC redo the lot.
                if (peer.ContentId != room.Owner && accepted.Length > 1) accepted = accepted[..1];
                if (accepted.Length == 0) return true;
                foreach (var state in accepted) room.Switches[state.Furnishing] = state.On;
                // Furnishings come and go through edits: forget switches of pieces no longer here once the table is full.
                if (room.Switches.Count > SwitchWire.MaxEntries)
                    foreach (var gone in room.Switches.Keys.Where(k => !present.Contains(k)).ToArray()) room.Switches.Remove(gone);
                var forwarded = SwitchWire.Encode(accepted);
                foreach (var occupant in room.Peers.Where(p => p != peer && p.Ready)) occupant.Queue(HousingWire.SwitchState, peer.Id, forwarded);
                return true;
            case HousingWire.PoseState:
                HandlePose(peer, payload);
                return true;
            case HousingWire.PoseAdjust:
                HandlePoseAdjust(peer, payload);
                return true;
            case HousingWire.PoseClose:
                HandlePoseClose(peer, payload);
                return true;
            case HousingWire.MiniGame:
                HandleMiniGame(peer, payload);
                return true;
            case HousingWire.GuestList:
                HandleGuestList(peer, payload);
                return true;
            case HousingWire.RoomChat:
                var message = RoomChat.Decode(payload);
                if (!peer.Ready || room.Scene == null)
                { peer.Queue(HousingWire.RoomChatRejected, WireFormat.RelaySender, Encoding.UTF8.GetBytes("Wait for the shared home to finish loading before chatting.")); return true; }
                if (!peer.ChatRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency))
                { peer.Queue(HousingWire.RoomChatRejected, WireFormat.RelaySender, Encoding.UTF8.GetBytes("Room chat is sending too quickly. Please wait a moment.")); return true; }
                // A roll is made here, never by the roller (RoomRolls).
                if (message.Channel == RoomRolls.Channel)
                {
                    var rolled = RoomRolls.Roll(message.Roll);
                    message = message with { Rolled = rolled, Text = RoomRolls.Text(peer.Name, rolled, message.Roll) };
                }
                var stamped = RoomChat.Encode(message with { Name = peer.Name });
                foreach (var occupant in room.Peers.Where(p => p != peer && p.Ready)) occupant.Queue(HousingWire.Typing, peer.Id, [0]);
                foreach (var occupant in room.Peers.Where(p => p.Ready)) occupant.Queue(HousingWire.RoomChat, peer.Id, stamped);
                // Kept briefly in memory only for participants who drop and resume; never sent to late joiners.
                room.RecentChat.Enqueue((NowMs, peer.Id, stamped));
                TrimChat(room);
                return true;
            case HousingWire.SceneReady:
                if (room.Scene == null) return true; // Owner may still be restoring the last published snapshot.
                if (payload.Length != 64 || Encoding.ASCII.GetString(payload) != room.Scene.Hash)
                { if (NowMs >= peer.NextScene) { peer.NextScene = NowMs + 2000; SendScene(room, peer); } return true; }
                peer.Queue(HousingWire.SceneReadyAck, WireFormat.RelaySender, payload);
                if (peer.Ready) return true;
                peer.Ready = true;
                Broadcast(room, HousingWire.PeerConnection, WireFormat.RelaySender, JsonSerializer.SerializeToUtf8Bytes(new PeerConnection(peer.Id, true, 0)), peer);
                if (peer.ReplayChat)
                {
                    peer.ReplayChat = false;
                    TrimChat(room);
                    foreach (var (_, from, line) in room.RecentChat.Where(c => c.From != peer.Id)) peer.Queue(HousingWire.RoomChatReplay, from, line);
                }
                log.LogInformation("relay_ready boot={Boot} room={Room} peer={Peer}", BootId, room.Id, peer.Id);
                SendNotice(peer);
                if (room.Current != null)
                {
                    var placed = room.Current.Furnishings.Select(f => f.Id).ToHashSet();
                    var table = room.Switches.Where(s => placed.Contains(s.Key)).Select(s => new SwitchState(s.Key, s.Value)).ToArray();
                    if (table.Length > 0) peer.Queue(HousingWire.SwitchState, WireFormat.RelaySender, SwitchWire.Encode(table));
                    room.Poses.Keep(room.Current);
                    var poses = room.Poses.Entries.Take(PoseWire.MaxEntries).ToArray();
                    if (poses.Length > 0) peer.Queue(HousingWire.PoseState, WireFormat.RelaySender, PoseWire.Encode(poses));
                }
                foreach (var other in room.Peers.Where(p => p != peer && p.Ready))
                    foreach (var kind in new[] { WireKind.HotUpdate, WireKind.WarmUpdate, WireKind.ColdUpdate, WireKind.HostUpdate })
                        if (other.Latest.TryGetValue(kind, out var state))
                        {
                            if (kind == WireKind.WarmUpdate)
                            {
                                var warm = Decode<WarmPayload>(state);
                                warm.StandupTimelineId = 0; // Bootstrap held state, not an old stand-up event.
                                state = Encode(warm);
                            }
                            peer.Queue(kind, other.Id, state);
                        }
                return true;
            case WireKind.HotUpdate:
                var hot = Decode<HotPayload>(payload);
                if (hot.SubjectId != "" || hot.SenderContentId != peer.ContentId || hot.Protocol != WireFormat.ProtocolVersion
                    || !new[] { hot.X, hot.Y, hot.Z, hot.Rotation, hot.MountPitch, hot.BodyDrawOffsetX, hot.BodyDrawOffsetY, hot.BodyDrawOffsetZ }.All(float.IsFinite))
                    throw new InvalidDataException("Invalid movement identity or position.");
                break;
            case WireKind.WarmUpdate:
                if (Decode<WarmPayload>(payload).SubjectId != "") throw new InvalidDataException("Player subjects only.");
                break;
            case WireKind.ColdUpdate:
                if (Decode<ColdPayload>(payload).SubjectId != "") throw new InvalidDataException("Player subjects only.");
                break;
            case WireKind.HostUpdate:
                Decode<HostPayload>(payload);
                break;
            case WireKind.ZoneLoadExecute:
                if (room.Zone != null && payload.AsSpan().SequenceEqual(room.Zone)) return true;
                // A zone request is a territory, a spawn point and two short names: refuse anything larger before it is
                // stored, checkpointed and sent to every joiner (the general 64 KiB check came after all that).
                if (payload.Length > MaxZoneBytes) throw new InvalidDataException("Oversized housing zone request.");
                if (!peer.ZoneRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency))
                { peer.Queue(WireKind.RateLimited, WireFormat.RelaySender, []); return true; }
                using (var zone = JsonDocument.Parse(payload))
                {
                    if (zone.RootElement.ValueKind != JsonValueKind.Object
                        || !zone.RootElement.TryGetProperty("zone", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var territory) || territory is 0 or > ushort.MaxValue
                        || (zone.RootElement.TryGetProperty("stbg", out var bg) && (bg.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || !string.IsNullOrEmpty(bg.GetString()))))
                        throw new InvalidDataException("Invalid housing zone request.");
                    if (room.Territory != null && room.Territory != territory)
                    { Error(peer, ErrCode.Generic, "End this visit before changing the housing shell."); return true; }
                    room.Territory = territory;
                }
                room.Zone = payload;
                Checkpoint(room, peer);
                break;
            case HousingWire.Snapshot:
            case HousingWire.RestoreSnapshot:
            case HousingWire.CompressedSnapshot:
                if (frame.Kind == HousingWire.RestoreSnapshot && room.Current != null)
                { if (NowMs >= peer.NextScene) { peer.NextScene = NowMs + 2000; SendScene(room, peer); } return true; }
                // Rate-limited and prepared (decoded, hashed, re-encoded) before the lock: see Run.
                if (frame.Kind == HousingWire.RestoreSnapshot && room.PublishedHash == null)
                { Result(peer, Guid.Empty, "error", "There is no verified scene to restore. Wait for the home owner."); return true; }
                prepared ??= PreparedSnapshot.Of(frame.Kind, payload);
                var house = prepared.House;
                if (room.Current == null && (room.PublishedHash != null ? prepared.SnapshotHash != room.PublishedHash : peer.ContentId != room.Owner))
                { Result(peer, Guid.Empty, "error", "Recovery can restore only the last approved scene. Waiting for a matching published copy."); return true; }
                if (room.HouseId != null && room.HouseId != house.Id) throw new InvalidDataException("A room represents one house.");
                if ((room.Scene != null && !room.Scene.Matches(prepared.Scene)) || (room.Territory != null && room.Territory != house.TerritoryId))
                { Error(peer, ErrCode.Generic, "The published layout is fixed. End this visit before decorating."); return true; }
                if (room.Current != null && peer.ContentId != room.Owner && (room.CurrentMusic ??= JsonSerializer.Serialize(room.Current.Music)) != prepared.Music)
                { Result(peer, Guid.Empty, "error", "Only the original home owner can change shared music."); return true; }
                // The same home again (everything but the time it was saved): nothing to checkpoint or send. Each
                // checkpoint writes the whole house to storage.
                if (room.Current != null && prepared.SnapshotHash == room.PublishedHash) return true;
                room.Scene ??= new PublishedHouseScene(prepared.Scene);
                room.Current = house; room.PublishedHash = prepared.SnapshotHash; room.CurrentMusic = prepared.Music;
                room.Poses.Keep(house);
                room.Territory = house.TerritoryId;
                room.HouseId = house.Id;
                room.House = prepared.Encoded;
                Checkpoint(room, peer, includeHouse: true);
                Broadcast(room, HousingWire.CompressedSnapshot, WireFormat.RelaySender, room.House, peer);
                BroadcastEditing(room);
                return true;
            case WireKind.SessionEnd:
                Checkpoint(room, peer, removed: true);
                peer.FinalDeparture = true;
                Broadcast(room, frame.Kind, peer.Id, payload, peer);
                foreach (var other in room.Peers.Where(p => p != peer)) { other.Room = null; other.Finish(); }
                room.Peers.RemoveAll(p => p != peer);
                return false;
            case WireKind.KickPeer:
                var target = room.Peers.FirstOrDefault(p => p.Id == Decode<KickPeerPayload>(payload).TargetPeerId);
                if (target != null && target.ContentId == room.Owner) { Error(peer, ErrCode.NotHost, "The home's owner cannot be removed."); return true; }
                if (target != null && target != peer) { room.Banned.Add(target.ContentId); target.FinalDeparture = true; Depart(target); Error(target, ErrCode.Kicked, "Removed by host."); target.Finish(); }
                return true;
            case WireKind.HostTransfer:
                var successor = room.Peers.FirstOrDefault(p => p.Id == Decode<HostTransferPayload>(payload).TargetPeerId);
                if (successor != null) { room.HostId = successor.Id; Checkpoint(room, peer); Broadcast(room, WireKind.HostTransfer, successor.Id, Encode(new HostTransferPayload { TargetPeerId = successor.Id })); }
                return true;
            default: return true; // Non-housing extension messages are outside this relay's alpha protocol.
        }
        if (payload.Length > 64 * 1024 && frame.Kind != HousingWire.Snapshot) throw new InvalidDataException("Oversized actor message.");
        if (frame.Kind is WireKind.HotUpdate or WireKind.WarmUpdate or WireKind.ColdUpdate or WireKind.HostUpdate)
        {
            // A character's lanes are small (the host's map state carries a list of hidden NPCs).
            if (payload.Length > (frame.Kind == WireKind.HostUpdate ? 32 * 1024 : 4 * 1024)) throw new InvalidDataException("Oversized actor message.");
            if (!peer.Ready) return true;
            peer.Latest[frame.Kind] = payload;
            // Haven sends each lane at most ten times a second (StateCaptureService). Beyond a generous allowance the
            // update is kept for late joiners but not forwarded, so one guest can't fill everyone's send queues.
            if (!peer.LaneRate(frame.Kind).Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency)) return true;
            foreach (var other in room.Peers.Where(p => p != peer && p.Ready)) other.Queue(frame.Kind, peer.Id, payload);
            return true;
        }
        Broadcast(room, frame.Kind, peer.Id, payload, peer);
        return true;
    }

    // A participant's pose entry: stamped with their character, kept only for a set the current home offers, a ball
    // someone else holds refused (the entry becomes a viewer's), and sent to everyone ready, the sender included, so
    // a refused claim is corrected on their screen too.
    private void HandlePose(Peer peer, byte[] payload)
    {
        var room = peer.Room!;
        var entries = PoseWire.Decode(payload);
        if (entries.Length != 1 || !peer.Ready || room.Current == null) return;
        if (!peer.PoseRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency)) return;
        var asked = entries[0] with { Character = peer.ContentId };
        var set = asked.Empty ? null : room.Current.Furnishings.FirstOrDefault(f => f.Id == asked.Furnishing)?.Poses?.FirstOrDefault(s => s.Id == asked.Set);
        if (!asked.Empty && (set == null || asked.Holds && asked.Role >= set.Roles.Length)) asked = PoseEntry.None(peer.ContentId);
        var stands = room.Poses.Apply(asked);
        var forwarded = PoseWire.Encode([stands]);
        foreach (var occupant in room.Peers.Where(p => p.Ready)) occupant.Queue(HousingWire.PoseState, peer.Id, forwarded);
    }

    // One player adjusting another's fit on the same set: stamped with the sender, sent only to the target, and only
    // while the relay's table has both on balls of that set (a stranger can't move anyone).
    private void HandlePoseAdjust(Peer peer, byte[] payload)
    {
        var room = peer.Room!;
        var adjust = PoseAdjust.Decode(payload) with { Sender = peer.ContentId };
        if (!peer.Ready || room.Current == null || !peer.AdjustRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency)) return;
        if (!room.Poses.MayAdjust(peer.ContentId, adjust)) return;
        var forwarded = adjust.Encode();
        foreach (var target in room.Peers.Where(p => p.Ready && p.ContentId == adjust.Target)) target.Queue(HousingWire.PoseAdjust, peer.Id, forwarded);
    }

    // A minigame message: stamped with the sender's character and passed only to the target (or, target 0, everyone
    // else ready in the room: a machine's result, an arcade player's swing). Nothing is kept; a message the relay
    // can't read is dropped.
    private void HandleMiniGame(Peer peer, byte[] payload)
    {
        var room = peer.Room!;
        if (!peer.Ready || room.Current == null || !peer.GameRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency)) return;
        GameMessage message;
        try { message = GameMessage.Decode(payload); } catch (InvalidDataException) { return; }
        if (message.Target == peer.ContentId) return;
        var forwarded = (message with { Sender = peer.ContentId }).Encode();
        foreach (var target in room.Peers.Where(p => p.Ready && p != peer && (message.Target == GameMessage.Everyone || p.ContentId == message.Target)))
            target.Queue(HousingWire.MiniGame, peer.Id, forwarded);
    }

    // The owner, a decorator, or someone holding or viewing the set hides its balls for everyone: its entries go from the
    // table (late joiners don't see them), and the close goes to everyone ready, the sender included, so each PC stands
    // its player up. Anyone else's close is dropped.
    private void HandlePoseClose(Peer peer, byte[] payload)
    {
        var room = peer.Room!;
        var close = PoseClose.Decode(payload);
        if (!peer.Ready || room.Current == null || !peer.PoseRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency)) return;
        if (!Editing(room).CanEdit(peer.ContentId) && !room.Poses.OnSet(peer.ContentId, close.Furnishing, close.Set)) return;
        room.Poses.Close(close.Furnishing, close.Set);
        var forwarded = close.Encode();
        foreach (var occupant in room.Peers.Where(p => p.Ready)) occupant.Queue(HousingWire.PoseClose, peer.Id, forwarded);
    }

    private void Depart(Peer peer)
    {
        var room = peer.Room;
        if (room == null) return;
        room.Peers.Remove(peer);
        RemoveEditingPeer(room, peer);
        if (room.Poses.Remove(peer.ContentId) is { } released && !room.Peers.Any(p => p.ContentId == peer.ContentId))
            Broadcast(room, HousingWire.PoseState, WireFormat.RelaySender, PoseWire.Encode([released]));
        peer.Room = null;
        if (room.Peers.Count == 0) { Checkpoint(room, peer, removed: true); rooms.Remove(room.Id); return; }
        var newHost = "";
        if (room.HostId == peer.Id) newHost = room.HostId = (room.Peers.FirstOrDefault(p => p.Online && p.Ready) ?? room.Peers.FirstOrDefault(p => p.Online) ?? room.Peers[0]).Id;
        Checkpoint(room, peer);
        Broadcast(room, WireKind.PeerLeft, peer.Id, Encode(new PeerLeftPayload { PeerId = peer.Id, NewHostId = newHost, MissingExpansion = peer.MissingExpansion }));
        if (newHost != "") Broadcast(room, WireKind.HostTransfer, newHost, Encode(new HostTransferPayload { TargetPeerId = newHost }));
    }

    private static void Error(Peer peer, uint code, string message) => peer.Queue(WireKind.Error, WireFormat.RelaySender, Encode(new ErrorPayload { Code = code, Message = message }));
    private static void Broadcast(Room room, byte kind, string sender, byte[] payload, Peer? except = null)
    { foreach (var peer in room.Peers) if (peer != except) peer.Queue(kind, sender, payload); }
    private static byte[] Encode<T>(T value) => MessagePackSerializer.Serialize(value, WireFormat.Options);
    private static T Decode<T>(byte[] payload) => MessagePackSerializer.Deserialize<T>(payload, WireFormat.Options);

    private static async Task<byte[]> Receive(WebSocket socket, int max, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult part;
        using var rest = CancellationTokenSource.CreateLinkedTokenSource(token);
        var started = false;
        do
        {
            part = await socket.ReceiveAsync(buffer, started ? rest.Token : token);
            if (!started) { started = true; rest.CancelAfter(TimeSpan.FromSeconds(15)); }
            if (part.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Transport closed.");
            if (part.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Binary frames required.");
            if (output.Length + part.Count > max) throw new InvalidDataException("Frame exceeds budget.");
            output.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return output.ToArray();
    }

    // Everything about a full-house frame that depends only on the frame, computed outside the lock.
    private sealed record PreparedSnapshot(House House, byte[] Scene, string SnapshotHash, byte[] Encoded, string Music)
    {
        public static PreparedSnapshot Of(byte kind, byte[] payload)
        {
            var house = kind != HousingWire.Snapshot ? HousingSnapshotCodec.Decode(payload) : HouseFormat.Parse(payload);
            return new(house, PublishedHouseScene.SceneOf(house), PublishedHouseScene.SnapshotHash(house), HousingSnapshotCodec.Encode(house),
                JsonSerializer.Serialize(house.Music));
        }
    }

    private static void TrimChat(Room room)
    {
        while (room.RecentChat.Count > ChatReplay.MaxLines || room.RecentChat.Count > 0 && NowMs - room.RecentChat.Peek().At > ChatReplay.WindowMs)
            room.RecentChat.Dequeue();
    }

    private sealed partial class Room
    {
        public required string Id;
        public required byte[] PasswordHash;
        public required string HostId;
        public readonly List<Peer> Peers = [];
        public readonly HashSet<ulong> Banned = [];
        public Guid? HouseId;
        public uint? Territory;
        public PublishedHouseScene? Scene;
        public byte[]? House, Zone;
        public long Revision;
        public ulong Owner;
        public bool EditingEnabled;
        public readonly HashSet<ulong> Editors = [];
        public readonly Dictionary<Guid, PendingEdit> Proposals = [];
        public long LayoutRevision;
        // Latest clicked side per furnishing for this gathering; late joiners receive it once ready.
        public readonly Dictionary<Guid, bool> Switches = [];
        // Each participant's pose balls (open or held) in this gathering; late joiners receive them once ready.
        public readonly PoseTable Poses = new();
        public House? Current;
        // Current's music as JSON, kept to compare a publish against without re-serializing it under the lock.
        public string? CurrentMusic;
        public string? PublishedHash;
        public DateTimeOffset LastCheckpoint;
        public string Address = "";
        public long CreatedAt = NowMs;
        // The last ChatReplay.Window of chat, in memory only, for participants who resume after a dropped connection.
        public readonly Queue<(long At, string From, byte[] Line)> RecentChat = new();
    }

    private sealed class Peer(WebSocket? socket, CancellationToken aborted) : IDisposable
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public byte[] ResumeHash = [];
        public long ResumeUntil;
        public bool Online => ResumeUntil == 0 && socket?.State == WebSocketState.Open;
        public bool FinalDeparture;
        public long LastActivity = Environment.TickCount64;
        public string? DisconnectReason;
        public ulong ContentId;
        public string ContentSecret = "";
        public string Address = "";
        // Full houses (8) and zone changes (4) at once, then one every 2 s.
        public readonly RoomChatRateLimit SnapshotRate = new(8, 0.5), ZoneRate = new(4, 0.5);
        public string Name = "";
        // The character the edge verified for this connection's account, if any (AccountStamp).
        public VerifiedAccount? Verified;
        public int MissingExpansion;   // the expansion its game lacked for the home, when it left for that
        public Room? Room;
        public bool Ready;
        // Resumed after a dropped connection: replay the room's recent chat once ready.
        public bool ReplayChat;
        public long NextEdit, NextScene;
        public readonly RoomChatRateLimit ChatRate = new();
        public readonly RoomChatRateLimit TypingRate = new();
        public readonly RoomChatRateLimit SwitchRate = new(), PoseRate = new(12, 3), AdjustRate = new(24, 10), GameRate = new(32, 12);
        public readonly RoomChatRateLimit MusicRate = new(6, 1), ReviewRate = new(8, 2), GuestRate = new(6, 1);
        private readonly RoomChatRateLimit hotRate = new(30, 15), warmRate = new(20, 12), coldRate = new(20, 12), hostRate = new(20, 12);
        public RoomChatRateLimit LaneRate(byte kind) => kind switch
        {
            WireKind.HotUpdate => hotRate, WireKind.WarmUpdate => warmRate, WireKind.ColdUpdate => coldRate, _ => hostRate,
        };
        public readonly Dictionary<byte, byte[]> Latest = [];
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        private readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(128);
        private long queuedBytes;
        public CancellationToken Token => stop.Token;
        public void Queue(byte kind, string sender, byte[] payload)
        {
            if (!Online) return;
            // A slow reader with a backed-up queue skips movement and typing (newer ones follow within a moment)
            // rather than being disconnected when the queue overflows.
            if (kind is WireKind.HotUpdate or HousingWire.Typing && (Interlocked.Read(ref queuedBytes) > HousingWire.MaxFrameBytes || outgoing.Reader.CanCount && outgoing.Reader.Count > 64)) return;
            var frame = FrameHeader.BuildDownleg(kind, sender, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payload);
            if (Interlocked.Add(ref queuedBytes, frame.Length) > 4L * HousingWire.MaxFrameBytes || !outgoing.Writer.TryWrite(frame)) { DisconnectReason = "send_queue_overflow"; Abort(); }
        }
        // Joined with Batching: frames gathered over FrameBatch.WindowMs go out as one message (see FrameBatch).
        public bool Batching;
        private long lastSend;
        public async Task SendLoop()
        {
            var graceful = false;
            try
            {
                await foreach (var frame in outgoing.Reader.ReadAllAsync(Token))
                {
                    if (!Batching)
                    { await socket!.SendAsync(frame, WebSocketMessageType.Binary, true, Token); Interlocked.Add(ref queuedBytes, -frame.Length); continue; }
                    // While traffic flows, wait out the rest of the window so the frames behind this one join it.
                    var wait = lastSend + FrameBatch.WindowMs - Environment.TickCount64;
                    if (wait > 0) await Task.Delay((int)wait, Token);
                    var gathered = new List<byte[]> { frame };
                    while (gathered.Count < 4096 && outgoing.Reader.TryRead(out var more)) gathered.Add(more);
                    foreach (var message in FrameBatch.Messages(gathered, WireKind.HotUpdate, HousingWire.RecoveryCheckpoint,
                        payload => FrameHeader.BuildDownleg(HousingWire.Batch, WireFormat.RelaySender, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payload)))
                        await socket!.SendAsync(message, WebSocketMessageType.Binary, true, Token);
                    Interlocked.Add(ref queuedBytes, -gathered.Sum(f => (long)f.Length));
                    lastSend = Environment.TickCount64;
                }
                if (socket?.State == WebSocketState.Open)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, DisconnectReason ?? (FinalDeparture ? "visit_ended" : "transport_reconnect"), Token);
                    graceful = true;
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ChannelClosedException) { }
            // Cancelling an outstanding ManagedWebSocket.ReceiveAsync aborts its socket.
            // Allow the peer/proxy to receive the final frame and answer Close first.
            finally { if (graceful) stop.CancelAfter(TimeSpan.FromSeconds(5)); else stop.Cancel(); }
        }
        public void Finish() => outgoing.Writer.TryComplete();
        public void Abort() { stop.Cancel(); socket?.Abort(); }
        public void Dispose() { stop.Cancel(); stop.Dispose(); }
    }
}
