using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Haven.Core;
using Haven.Relay;
using HavenNative.Sync;
using HavenNative.Wire;
using MessagePack;

internal static class RecoveryIntegration
{
    private static void Require(bool condition) { if (!condition) throw new Exception("Recovery assertion failed."); }
    private static void Drop(RelaySyncService client) => ((ClientWebSocket)typeof(RelaySyncService).GetField("ws", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!).Abort();
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
    private static byte[] Pack<T>(T value) => MessagePackSerializer.Serialize(value, WireFormat.Options);
    private static T Unpack<T>(byte[] value) => MessagePackSerializer.Deserialize<T>(value, WireFormat.Options);

    public static async Task ClientResume(Uri endpoint, House house)
    {
        using var client = new RelaySyncService(new ClientTestLog());
        var recovered = 0;
        var chats = 0;
        client.OnRecovered += () => Interlocked.Increment(ref recovered);
        client.OnRoomChat += _ => Interlocked.Increment(ref chats);
        client.OnRoomJoined += joined => { if (client.ResumedAdmission) _ = client.SendHousingReady(new PublishedHouseScene(house).Hash); };
        client.OnHousingSnapshot += bytes => { _ = client.SendHousingReady(new PublishedHouseScene(house).Hash); };
        Require(await client.Connect(endpoint.ToString(), "", true, "production-recovery-test", [], 701, 0, "RecoveryHost"));
        await Until(() => client.RoomJoinedAcknowledged);
        var id = client.LocalPeerId;
        var room = client.CachedRoomId;
        await client.SendHousingSnapshot(HouseFormat.Serialize(house));
        await client.SendHousingReady(new PublishedHouseScene(house).Hash);
        await Until(() => client.HousingSceneReady);
        using var observer = await Client.Connect(endpoint, new TestJoin { ContentId = 702, CharacterName = "RecoveryGuest", RoomPassword = "production-recovery-test", NearbyContentIds = [701] });
        await observer.Expect(WireKind.RoomJoined); await observer.Expect(HousingWire.Snapshot); await observer.Ready(house);
        Drop(client);
        await Until(() => client.IsRecovering);
        Require(client.IsSessionActive);
        await Until(() => recovered == 1 && client.HousingSceneReady);
        Require(client.LocalPeerId == id && client.CachedRoomId == room && client.ResumedAdmission && !client.IsRecovering);
        await observer.Send(WireKind.Ping, []);
        await observer.ExpectWithout(WireKind.Pong, WireKind.PeerLeft, WireKind.HostTransfer, WireKind.ZoneLoadExecute);
        await client.SendRoomChat(new(10, "after reconnect"));
        await observer.Expect(HousingWire.RoomChat); await Until(() => chats == 1);
        // Repeated loss recovers too; a connected intentional leave releases membership.
        Drop(client); await Until(() => client.IsRecovering);
        await Until(() => recovered == 2 && client.HousingSceneReady);
        await client.Disconnect();
        await observer.Expect(WireKind.PeerLeft);
        await observer.Expect(WireKind.HostTransfer);
        await Task.Delay(1500);
        Require(!client.IsConnected && !client.IsRecovering && !client.IsSessionActive);
        await observer.Send(WireKind.SessionEnd, []); // Host succession after explicit leave.
    }

    public static async Task IntentionalReplacement(Uri endpoint)
    {
        using var host = new RelaySyncService(new ClientTestLog());
        Require(await host.Connect(endpoint.ToString(), "", true, "replace-host-original", [], 741, 0, "ReplaceHost"));
        await Until(() => host.RoomJoinedAcknowledged);
        using var guest = await Client.Connect(endpoint, new TestJoin { Revision = HousingWire.ProtocolRevision,
            ContentId = 742, CharacterName = "ReplaceGuest", RoomPassword = "replace-host-original", CreateIfMissing = false, NearbyContentIds = [741] });
        await guest.Expect(WireKind.RoomJoined);
        // Replacing a live transport must first send LeaveRoom, not create a recovery reservation.
        Require(await host.Connect(endpoint.ToString(), "", true, "replace-host-next", [], 741, 0, "ReplaceHost"));
        await Until(() => host.RoomJoinedAcknowledged);
        await guest.Expect(WireKind.PeerLeft); await guest.Expect(WireKind.HostTransfer);
        await Task.WhenAll(host.Disconnect(), host.Disconnect());
        Require(await host.Connect(endpoint.ToString(), "", true, "replace-host-next", [], 741, 0, "ReplaceHost"));
        await Until(() => host.RoomJoinedAcknowledged);
        await host.Disconnect();
        // A user cancellation during the initial asynchronous transport setup must never reopen a room.
        var pending = host.Connect(endpoint.ToString(), "", true, "cancel-host-before-admission", [], 741, 0, "ReplaceHost");
        await host.Disconnect();
        Require(!await pending && !host.IsConnected && !host.IsConnecting && !host.IsSessionActive);
        await guest.Send(WireKind.SessionEnd, []);
    }

    public static async Task RejectedResume(Uri endpoint, House house)
    {
        using var client = new RelaySyncService(new ClientTestLog());
        Require(await client.Connect(endpoint.ToString(), "", true, "rejected-recovery-test", [], 711, 0, "RejectedResume"));
        await Until(() => client.RoomJoinedAcknowledged);
        await client.SendHousingSnapshot(HouseFormat.Serialize(house));
        await client.SendHousingReady(new PublishedHouseScene(house).Hash); await Until(() => client.HousingSceneReady);
        var validToken = typeof(RelaySyncService).GetField("resumeToken", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client);
        typeof(RelaySyncService).GetField("resumeToken", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, new string('0', 64));
        Drop(client); await Until(() => client.RecoveryBlocked);
        Require(client.IsRecovering && client.IsSessionActive);
        // A corrected credential can be retried, without rebuilding the local session.
        typeof(RelaySyncService).GetField("resumeToken", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, validToken);
        client.OnRoomJoined += joined => { if (client.ResumedAdmission) _ = client.SendHousingReady(new PublishedHouseScene(house).Hash); };
        client.OnHousingSnapshot += bytes => { _ = client.SendHousingReady(new PublishedHouseScene(house).Hash); };
        client.RetryRecovery(); await Until(() => client.HousingSceneReady && !client.IsRecovering);
        await client.SendSessionEnd(); await client.Disconnect();
    }

    public static async Task ProcessRestart()
    {
        const string key = "headless-loopback-recovery-key";
        var app = RelayApp.Build(["--urls", "http://127.0.0.1:0", "--HAVEN_RECOVERY_KEY", key]);
        await app.StartAsync();
        var address = app.Urls.Single(); var endpoint = new Uri(address.Replace("http:", "ws:") + "/");
        var house = new House { Name = "Durable test", TerritoryId = 282, Stage = JsonDocument.Parse("{\"FormatVersion\":2,\"Objects\":{}}").RootElement.Clone() };
        using var h = await Client.Connect(endpoint, new TestJoin { ContentId = 721, CharacterName = "DurableHost", RoomPassword = "durable-recovery-test", CreateIfMissing = true });
        var ha = Unpack<RoomJoinedPayload>((await h.Expect(WireKind.RoomJoined)).Payload);
        using var g = await Client.Connect(endpoint, new TestJoin { ContentId = 722, CharacterName = "DurableGuest", RoomPassword = "durable-recovery-test", NearbyContentIds = [721] });
        var ga = Unpack<RoomJoinedPayload>((await g.Expect(WireKind.RoomJoined)).Payload);
        await h.Send(HousingWire.CompressedSnapshot, HousingSnapshotCodec.Encode(house));
        RecoveryCheckpoint saved;
        do { saved = JsonSerializer.Deserialize<RecoveryCheckpoint>((await h.Expect(HousingWire.RecoveryCheckpoint)).Payload)!; }
        while (saved.State?.House == null);
        Require(saved.State!.House != null && saved.State.Members.Length == 2);
        Require(!JsonSerializer.Serialize(saved).Contains(ha.ResumeToken));
        h.Dispose(); g.Dispose();
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2))) { try { await app.StopAsync(stop.Token); } catch (OperationCanceledException) { } }
        await app.DisposeAsync();
        await using var restarted = RelayApp.Build(["--urls", address, "--HAVEN_RECOVERY_KEY", key]);
        await restarted.StartAsync();
        using var http = new HttpClient();
        using var denied = await http.PostAsJsonAsync(address + "/internal/restore", saved);
        Require(denied.StatusCode == System.Net.HttpStatusCode.NotFound);
        // A damaged checkpoint is skipped and logged, not refused: a refusal stopped the edge restoring the others.
        using var damaged = new HttpRequestMessage(HttpMethod.Post, address + "/internal/restore")
        { Content = JsonContent.Create(saved with { RoomId = new string('f', 32), State = saved.State! with { Id = new string('f', 32), Members = [] } }) };
        damaged.Headers.Add("X-Haven-Recovery", key);
        using var skipped = await http.SendAsync(damaged); Require(skipped.IsSuccessStatusCode);
        using var restore = new HttpRequestMessage(HttpMethod.Post, address + "/internal/restore") { Content = JsonContent.Create(saved) };
        restore.Headers.Add("X-Haven-Recovery", key);
        using var restored = await http.SendAsync(restore); Require(restored.IsSuccessStatusCode);
        using var wrong = await Client.Connect(endpoint, new TestJoin { ContentId = 721, CharacterName = "Intruder", RoomId = ha.RoomId, ResumeToken = new string('0', 64) });
        Require(Unpack<ErrorPayload>((await wrong.Expect(WireKind.Error)).Payload).Code == ErrCode.RoomNotFound);
        using var h2 = await Client.Connect(endpoint, new TestJoin { ContentId = 721, CharacterName = "Ignored", RoomId = ha.RoomId, ResumeToken = ha.ResumeToken });
        var resumedHost = Unpack<RoomJoinedPayload>((await h2.Expect(WireKind.RoomJoined)).Payload);
        Require(resumedHost.Resumed && resumedHost.IsHost && resumedHost.AssignedPeerId == ha.AssignedPeerId);
        await h2.Expect(HousingWire.Snapshot); await h2.Ready(house);
        using var g2 = await Client.Connect(endpoint, new TestJoin { ContentId = 722, CharacterName = "Ignored", RoomId = ga.RoomId, ResumeToken = ga.ResumeToken });
        var resumedGuest = Unpack<RoomJoinedPayload>((await g2.Expect(WireKind.RoomJoined)).Payload);
        Require(resumedGuest.Resumed && !resumedGuest.IsHost && resumedGuest.AssignedPeerId == ga.AssignedPeerId);
        await g2.Expect(HousingWire.Snapshot); await g2.Ready(house);
        await g2.Send(WireKind.Ping, []); await g2.ExpectWithout(WireKind.Pong, WireKind.ZoneLoadExecute, HousingWire.RoomChat);
        await g2.Send(HousingWire.RoomChat, RoomChat.Encode(new(10, "restored gathering"))); await h2.Expect(HousingWire.RoomChat);
        await h2.Send(WireKind.SessionEnd, []); await g2.Expect(WireKind.SessionEnd);
        using var ended = await Client.Connect(endpoint, new TestJoin { ContentId = 721, CharacterName = "Ended", RoomId = ha.RoomId, ResumeToken = ha.ResumeToken });
        Require(Unpack<ErrorPayload>((await ended.Expect(WireKind.Error)).Payload).Code == ErrCode.RoomNotFound);
    }

    public static async Task GroupResume()
    {
        const string key = "group-recovery-loopback-key";
        var app = RelayApp.Build(["--urls", "http://127.0.0.1:0", "--HAVEN_RECOVERY_KEY", key]);
        await app.StartAsync();
        var address = app.Urls.Single(); var endpoint = new Uri(address.Replace("http:", "ws:") + "/");
        var house = new House { Name = "Group recovery", TerritoryId = 282, Stage = JsonDocument.Parse("{\"FormatVersion\":2,\"Objects\":{}}").RootElement.Clone() };
        var hash = new PublishedHouseScene(house).Hash;
        using var host = new RelaySyncService(new ClientTestLog());
        using var guest = new RelaySyncService(new ClientTestLog());
        var hostRecovered = 0; var guestRecovered = 0; var zoneLoads = 0; float guestSawX = 0;
        foreach (var client in new[] { host, guest })
        {
            client.OnHousingSnapshot += data => { _ = client.SendHousingReady(hash); };
            client.OnRoomJoined += joined => { if (client.ResumedAdmission) _ = client.SendHousingReady(hash); };
            client.OnZoneLoadReceived += _ => Interlocked.Increment(ref zoneLoads);
        }
        host.OnRecovered += () => Interlocked.Increment(ref hostRecovered);
        guest.OnRecovered += () => Interlocked.Increment(ref guestRecovered);
        guest.OnTransformReceived += (_, data, hot) => { if (hot) guestSawX = data.X; };
        Require(await host.Connect(endpoint.ToString(), "", true, "native-group-recovery-test", [], 731, 0, "GroupHost"));
        await Until(() => host.RoomJoinedAcknowledged);
        await host.SendHousingSnapshot(HouseFormat.Serialize(house)); await host.SendHousingReady(hash); await Until(() => host.HousingSceneReady);
        Require(await guest.Connect(endpoint.ToString(), "", false, "native-group-recovery-test", [731], 732, 0, "GroupGuest"));
        await Until(() => guest.HousingSceneReady);
        await host.SendTransformAsLanes(new() { Protocol = 4, SenderContentId = 731, X = 1, EmoteId = 50, CPoseState = 3 }, true, true, true, false, .01f, .01f);
        await Until(() => guestSawX == 1);
        var hostId = host.LocalPeerId; var guestId = guest.LocalPeerId;
        await host.SetEditing(new(true, [732]));
        await Until(() => guest.Editing?.CanEdit(732) == true);
        using var capture = await Client.Connect(endpoint, new TestJoin { ContentId = 733, CharacterName = "CheckpointObserver", RoomPassword = "native-group-recovery-test", NearbyContentIds = [731] });
        await capture.Expect(WireKind.RoomJoined); await capture.Expect(HousingWire.Snapshot);
        var saved = capture.LastCheckpoint ?? throw new Exception("Recovery checkpoint missing.");
        Require(saved.State?.Owner == 731 && saved.State.Editors!.Contains(732UL));
        // Deliberately omit the scene, representing an edge restart between metadata and the first scene write.
        saved = saved with { State = saved.State! with { Members = saved.State!.Members.Where(m => m.ContentId != 733).ToArray(), House = null } };
        capture.Dispose();
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2))) { try { await app.StopAsync(stop.Token); } catch (OperationCanceledException) { } }
        await app.DisposeAsync();
        await Until(() => host.IsRecovering && guest.IsRecovering);
        Require(host.IsSessionActive && guest.IsSessionActive);
        await using var restarted = RelayApp.Build(["--urls", address, "--HAVEN_RECOVERY_KEY", key], [saved]);
        await restarted.StartAsync();
        await Until(() => hostRecovered == 1 && guestRecovered == 1 && host.HousingSceneReady && guest.HousingSceneReady);
        Require(host.LocalPeerId == hostId && guest.LocalPeerId == guestId && host.IsHost && !guest.IsHost && zoneLoads == 0);
        Require(host.Editing?.Owner == 731 && guest.Editing?.CanEdit(732) == true);
        await host.SendTransformAsLanes(new() { Protocol = 4, SenderContentId = 731, X = 77, EmoteId = 50, CPoseState = 3 }, true, true, true, false, .01f, .01f);
        await Until(() => guestSawX == 77);
        // Leaving without a live transport must cancel retry rather than reopen the visit later.
        Drop(guest); await Until(() => guest.IsRecovering);
        await guest.Disconnect(); await Task.Delay(1500);
        Require(!guest.IsSessionActive && !guest.IsRecovering && !guest.IsConnected);
        await host.SendSessionEnd();
        await host.Disconnect();
    }
}
