using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using Haven.Core;
using Haven.Relay;
using HavenNative.Wire;
using System.Security.Cryptography;
using System.Text;
using MessagePack;
using Dalamud.Plugin.Services;
using HavenNative.Sync;

internal static class EditingIntegration
{
    private static void Require(bool value) { if (!value) throw new Exception("Hosted editing assertion failed."); }
    private static void Conflict(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or JsonException or ArgumentException) { return; }
        throw new Exception("Expected a decorating conflict or validation failure.");
    }
    private static House Home() => new() { TerritoryId = 282, Stage = JsonSerializer.SerializeToElement(new { FormatVersion = 2, Objects = new { } }),
        Furnishings = [new() { ItemId = 1 }, new() { ItemId = 2 }] };
    public static void Core()
    {
        var home = Home(); var first = home.Furnishings[0]; var second = home.Furnishings[1];
        var a = home with { Furnishings = [first with { Position = [1, 2, 3] }, second] };
        var b = home with { Furnishings = [first, second with { Scale = 2 }] };
        var transaction = HostedEditing.Create(home, a);
        var packed = HostedEditing.Pack(transaction);
        var decoded = HostedEditing.Unpack<EditBatch>(packed);
        Require(packed.Length < HouseFormat.Serialize(home).Length);
        var combined = HostedEditing.Apply(b, decoded);
        Require(combined.Furnishings.Single(f => f.Id == first.Id).Position[0] == 1 && combined.Furnishings.Single(f => f.Id == second.Id).Scale == 2);
        Require(HostedEditing.Same(combined, HostedEditing.Apply(combined, decoded)));
        Conflict(() => HostedEditing.Apply(home with { Furnishings = [first with { Scale = 3 }, second] }, decoded));
        Require(home.Furnishings[0].Scale == 1); // Failed transaction never mutates its source.
        Require(HostedEditing.Same(home, home with { Furnishings = home.Furnishings.Reverse().ToArray() }));
        var formatted = home with { Stage = JsonSerializer.Deserialize<JsonElement>("{\"FormatVersion\":2,\"Objects\":{},\"Info\":{\"value\":1.0}}") };
        var normalized = formatted with { Stage = JsonSerializer.Deserialize<JsonElement>("{\"Info\":{\"value\":1},\"Objects\":{},\"FormatVersion\":2}") };
        Require(HostedEditing.Same(formatted, normalized) && HostedEditing.Create(formatted, normalized).Changes.Length == 0);
        // A value set where the home had none (a MakePlace import's brightness, null) and back again: it must survive
        // the trip to the relay (Pack/Unpack) and apply there. A null "before" once arrived as "absent" and every
        // publish was refused, so the owner's lighting never saved (user report, 3 November 2026).
        var unlit = home with { Interior = home.Interior with { Brightness = null } };
        var lit = unlit with { Interior = unlit.Interior with { Brightness = 5 } };
        foreach (var (from, to) in new[] { (unlit, lit), (lit, unlit) })
        {
            var sent = HostedEditing.Unpack<EditBatch>(HostedEditing.Pack(HostedEditing.Create(from, to)));
            Require(sent.Changes.Length == 1 && HostedEditing.Same(HostedEditing.Apply(from, sent), to));
        }
        var draft = new HostedDraft(home); draft.Change(a); draft.Receive(b);
        Require(draft.Dirty && draft.Conflict == null && HostedEditing.Same(draft.Working, combined));
        var submitted = draft.Working;
        draft.Change(submitted with { Furnishings = submitted.Furnishings.Select(f => f.Id == first.Id ? f with { Scale = 4 } : f).ToArray() });
        draft.Receive(submitted, submitted);
        Require(draft.Dirty && draft.Conflict == null && draft.Working.Furnishings.Single(f => f.Id == first.Id).Scale == 4);
        draft.Receive(submitted with { Furnishings = submitted.Furnishings.Select(f => f.Id == first.Id ? f with { Scale = 5 } : f).ToArray() });
        Require(draft.Conflict != null && draft.Working.Furnishings.Single(f => f.Id == first.Id).Scale == 4);
        draft.KeepLocalChanges(); Require(draft.Conflict == null && draft.Dirty); draft.Discard(); Require(!draft.Dirty);
        Conflict(() => HostedEditing.Create(home, a with { Id = Guid.NewGuid() }));
        Conflict(() => HostedEditing.Apply(home, decoded with { Changes = [decoded.Changes[0] with { Path = ["music"] }] }));
        Conflict(() => HostedEditing.Apply(home, decoded with { Changes = [decoded.Changes[0], decoded.Changes[0]] }));
        Conflict(() => HostedEditing.Unpack<EditBatch>(packed.Concat(new byte[] { 0 }).ToArray()));
        var bomb = (byte[])packed.Clone(); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bomb, HostedEditing.MaxExpanded + 1);
        Conflict(() => HostedEditing.Unpack<EditBatch>(bomb));
        var removed = home with { Furnishings = [second] };
        Require(HostedEditing.Apply(home, HostedEditing.Create(home, removed)).Furnishings.Single().Id == second.Id);
        Require(HostedEditing.Create(home, home with { Music = new() { Playing = true } }).Changes.Length == 0);
        Require(PublishedHouseScene.SnapshotHash(home) == PublishedHouseScene.SnapshotHash(home with { SavedAt = DateTimeOffset.UnixEpoch }));
        Require(PublishedHouseScene.SnapshotHash(home) != PublishedHouseScene.SnapshotHash(home with { Music = new() { Playing = true } }));
        // JavaScript edge storage must never round 64-bit character identifiers.
        const ulong largeCid = 9007199254740993UL;
        var checkpoint = new RecoveryRoom(new string('a', 32), new byte[32], "bbbbbbbbbbbb",
            [new("bbbbbbbbbbbb", largeCid, "Owner", new byte[32], 1)], [largeCid], null, null, 1,
            Owner: largeCid, Editors: [largeCid]);
        var json = JsonSerializer.Serialize(checkpoint);
        using (var doc = JsonDocument.Parse(json))
        {
            Require(doc.RootElement.GetProperty("Owner").ValueKind == JsonValueKind.String);
            Require(doc.RootElement.GetProperty("Members")[0].GetProperty("ContentId").GetString() == largeCid.ToString());
            Require(doc.RootElement.GetProperty("Editors")[0].GetString() == largeCid.ToString());
            Require(doc.RootElement.GetProperty("Banned")[0].GetString() == largeCid.ToString());
        }
        Require(JsonSerializer.Deserialize<RecoveryRoom>(json)!.Owner == largeCid);
        var large = home with { Furnishings = Enumerable.Range(0, 1000).Select(i => new Furnishing { ItemId = 1, Position = [i, 0, 0] }).ToArray() };
        var oneMove = large with { Furnishings = large.Furnishings.Select((f, i) => i == 900 ? f with { Position = [4, 5, 6] } : f).ToArray() };
        var deltaBytes = HostedEditing.Pack(HostedEditing.Create(large, oneMove)).Length;
        var fullBytes = HousingSnapshotCodec.Encode(oneMove).Length;
        Require(deltaBytes < fullBytes / 10);
        Console.WriteLine($"Saved one-object edit: {deltaBytes} bytes; compressed full 1000-object scene: {fullBytes} bytes.");
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!predicate()) await Task.Delay(15, timeout.Token);
    }
    public static void StagingCore()
    {
        var home = Home(); var a = home.Furnishings[0]; var b = home.Furnishings[1];
        var first = home with { Furnishings = [a with { Scale = 2 }, b with { Scale = 2 }] };
        var later = first with { Furnishings = [first.Furnishings[0] with { Scale = 3 }, first.Furnishings[1]] };
        var delta = HostedEditing.Stage(home, first, later);
        Require(delta.Changes.Length == 1 && delta.Changes[0].Path[1] == a.Id.ToString("N"));
        Require(HostedEditing.Apply(home, delta).Furnishings.Single(f => f.Id == a.Id).Scale == 3);
        var undo = HostedEditing.Stage(home, later, home);
        Require(undo.Changes.Length == 2 && undo.Changes.All(HostedEditing.NoChange));
        var aPatch = HostedEditing.Create(home, home with { Furnishings = [first.Furnishings[0], b] });
        var bPatch = HostedEditing.Create(home, home with { Furnishings = [a, first.Furnishings[1]] });
        var draft = new HostedDraft(home); draft.Change(later);
        var accepted = HostedEditing.Apply(home, aPatch);
        draft.Receive(accepted, HostedEditing.Apply(draft.Origin, aPatch, true));
        Require(draft.Conflict == null && draft.Working.Furnishings.Single(f => f.Id == a.Id).Scale == 3);
        draft.Reject(bPatch);
        Require(draft.Working.Furnishings.Single(f => f.Id == b.Id).Scale == 1 && draft.Working.Furnishings.Single(f => f.Id == a.Id).Scale == 3);
        // A rejection of an older proposal cannot erase a more recent local edit.
        draft.Reject(aPatch); Require(draft.Working.Furnishings.Single(f => f.Id == a.Id).Scale == 3);
        // Approving several proposals applies them in turn, in one pass over the home; a later one that no longer
        // fits (its "before" was changed by an earlier one) fails the whole approval.
        Require(HostedEditing.Same(HostedEditing.ApplyAll(home, [aPatch, bPatch]), HostedEditing.Apply(HostedEditing.Apply(home, aPatch), bPatch)));
        var cPatch = HostedEditing.Create(home, home with { Furnishings = [a with { Scale = 5 }, b] });
        Conflict(() => HostedEditing.ApplyAll(home, [aPatch, cPatch]));
        var group = home with { Stage = JsonSerializer.SerializeToElement(new { FormatVersion = 2, Objects = new { group = new { Type = "Group", Name = "A", Objects = new { } } } }) };
        var changedGroup = group with { Stage = JsonSerializer.SerializeToElement(new { FormatVersion = 2, Objects = new { group = new { Type = "Group", Name = "B", Objects = new { } } } }) };
        var groupDelta = HostedEditing.Stage(home, group, changedGroup);
        Require(groupDelta.Changes.Length == 1 && groupDelta.Changes[0].Path.Last() == "group");
        Require(HostedEditing.Same(changedGroup, HostedEditing.Apply(home, groupDelta)));
    }

    public static async Task StagingRelay(Uri endpoint)
    {
        var home = Home(); var a = home.Furnishings[0]; var b = home.Furnishings[1];
        var cid = (ulong)Random.Shared.NextInt64(100000000, long.MaxValue - 10);
        var code = "staging-test-" + Guid.NewGuid().ToString("N");
        using var owner = new RelaySyncService(new ClientTestLog()); using var guest = new RelaySyncService(new ClientTestLog()); using var viewer = new RelaySyncService(new ClientTestLog());
        var proposals = new ConcurrentDictionary<Guid, EditProposal>();
        var guestResults = new ConcurrentQueue<EditResult>(); var ownerResults = new ConcurrentQueue<EditResult>();
        var contributions = new ConcurrentQueue<EditApplied>(); var ownChat = new ConcurrentQueue<bool>(); var friendChat = new ConcurrentQueue<bool>();
        owner.OnEditProposal += p => proposals[p.Batch.Id] = p;
        guest.OnEditResult += r => guestResults.Enqueue(r); owner.OnEditResult += r => ownerResults.Enqueue(r);
        guest.OnEditApplied += r => contributions.Enqueue(r);
        guest.OnRoomChatDelivery += (_, own, _) => ownChat.Enqueue(own); owner.OnRoomChatDelivery += (_, own, _) => friendChat.Enqueue(own);
        var viewerProposals = 0; viewer.OnEditProposal += _ => Interlocked.Increment(ref viewerProposals);
        foreach (var client in new[] { owner, guest, viewer })
        {
            var local = client; local.OnHostTransfer += id => local.IsHost = id == local.LocalPeerId;
            local.OnHousingSnapshot += bytes => { if (!local.HousingSceneReady) _ = local.SendHousingReady(HostedEditing.Hash(HouseFormat.Parse(bytes))); };
        }
        Require(await owner.Connect(endpoint.ToString(), "", true, code, [], cid, 0, "StageOwner"));
        await Until(() => owner.RoomJoinedAcknowledged);
        await owner.SendHousingSnapshot(HouseFormat.Serialize(home)); await owner.SendHousingReady(HostedEditing.Hash(home)); await Until(() => owner.HousingSceneReady);
        Require(await guest.Connect(endpoint.ToString(), "", false, code, [cid], cid + 1, 0, "StageGuest"));
        Require(await viewer.Connect(endpoint.ToString(), "", false, code, [cid], cid + 2, 0, "StageViewer"));
        await Until(() => guest.HousingSceneReady && viewer.HousingSceneReady && guest.Editing?.Protocol == 2);
        await guest.StageEdits(HostedEditing.Create(home, home with { Furnishings = [a with { Scale = 2 }, b] }));
        await Until(() => guestResults.Any(r => r.Outcome == "error"));
        await owner.SetEditing(new(true, [cid + 1, cid + 2])); await Until(() => guest.Editing?.CanEdit(cid + 1) == true);
        var first = home with { Furnishings = [a with { Scale = 2 }, b with { Scale = 2 }] };
        var batch = HostedEditing.Stage(home, home, first); await guest.StageEdits(batch);
        await Until(() => owner.Editing?.Pending?.Length == 2 && proposals.Count == 2);
        Require(HostedEditing.Same(home, owner.SharedHouse!) && HostedEditing.Same(home, viewer.SharedHouse!) && viewerProposals == 0);
        var stale = proposals.Values.Single(p => p.Batch.Changes[0].Path[1] == a.Id.ToString("N")).Batch.Id;
        var bId = proposals.Values.Single(p => p.Batch.Changes[0].Path[1] == b.Id.ToString("N")).Batch.Id;
        var stableIds = owner.Editing!.Pending!.Order().ToArray();
        await Task.Delay(1600);
        var retry = HostedEditing.Stage(home, home, first); await guest.StageEdits(retry);
        await Until(() => guestResults.Any(r => r.Id == retry.Id && r.Outcome == "staged"));
        Require(owner.Editing!.Pending!.Order().SequenceEqual(stableIds) && proposals.Count == 2);
        await Task.Delay(1600);
        var second = first with { Furnishings = [a with { Scale = 3 }, first.Furnishings[1]] };
        var update = HostedEditing.Stage(home, first, second); await guest.StageEdits(update);
        await Until(() => guestResults.Any(r => r.Id == update.Id && r.Outcome == "staged"));
        await Until(() => owner.Editing!.Pending!.Length == 2 && !owner.Editing.Pending.Contains(stale));
        var aId = owner.Editing!.Pending!.Single(id => id != bId);
        var count = ownerResults.Count; await owner.ReviewEdits(new(stale, true)); await Until(() => ownerResults.Count > count);
        Require(ownerResults.Last().Outcome == "error" && HostedEditing.Same(home, owner.SharedHouse!));
        count = guestResults.Count; await guest.ReviewStaged(new([aId], true)); await Until(() => guestResults.Count > count); Require(guestResults.Last().Outcome == "error");
        await owner.ReviewEdits(new(aId, true)); await Until(() => guest.SharedHouse!.Furnishings.Single(f => f.Id == a.Id).Scale == 3);
        Require(contributions.Last().Contributions!.Single().Author == cid + 1 && owner.Editing!.Pending!.Contains(bId));
        await owner.ReviewEdits(new(bId, false)); await Until(() => guestResults.Any(r => r.Id == bId && r.Rejected != null));
        Require(owner.SharedHouse!.Furnishings.Single(f => f.Id == b.Id).Scale == 1);
        // Two collaborators propose different values for the same object. Approve all is atomic.
        home = owner.SharedHouse!; await Task.Delay(1600);
        await guest.StageEdits(HostedEditing.Create(home, home with { Furnishings = [a with { Scale = 4 }, b] }));
        await viewer.StageEdits(HostedEditing.Create(home, home with { Furnishings = [a with { Scale = 5 }, b] }));
        await Until(() => owner.Editing!.Pending!.Length == 2);
        count = ownerResults.Count; await owner.ReviewStaged(new(owner.Editing!.Pending!, true)); await Until(() => ownerResults.Count > count);
        Require(ownerResults.Last().Outcome == "error" && HostedEditing.Same(home, viewer.SharedHouse!) && owner.Editing!.Pending!.Length == 2);
        await owner.ReviewStaged(new(owner.Editing!.Pending!, false)); await Until(() => owner.Editing!.Pending!.Length == 0);
        // Disjoint proposals publish together in one accepted revision.
        await Task.Delay(1600);
        await guest.StageEdits(HostedEditing.Create(home, home with { Furnishings = [a with { Scale = 4 }, b] }));
        await viewer.StageEdits(HostedEditing.Create(home, home with { Furnishings = [home.Furnishings[0], b with { Scale = 5 }] }));
        await Until(() => owner.Editing!.Pending!.Length == 2);
        var revision = owner.Editing!.Revision;
        await owner.ReviewStaged(new(owner.Editing.Pending!, true)); await Until(() => owner.Editing!.Revision > revision);
        Require(owner.Editing!.Revision == revision + 1 && owner.SharedHouse!.Furnishings.Single(f => f.Id == a.Id).Scale == 4 && owner.SharedHouse.Furnishings.Single(f => f.Id == b.Id).Scale == 5);
        // Explicit undo removes a staged addition without publishing or leaving a ghost proposal.
        home = owner.SharedHouse!; var addition = home with { Furnishings = [..home.Furnishings, new() { ItemId = 3 }] };
        await Task.Delay(1600); await guest.StageEdits(HostedEditing.Stage(home, home, addition)); await Until(() => owner.Editing!.Pending!.Length == 1);
        await Task.Delay(1600); await guest.StageEdits(HostedEditing.Stage(home, addition, home)); await Until(() => owner.Editing!.Pending!.Length == 0);
        Require(HostedEditing.Same(home, viewer.SharedHouse!));
        // A compressed staging send cannot overtake its immediately queued discard.
        await Task.Delay(1600);
        count = guestResults.Count;
        var queued = guest.StageEdits(HostedEditing.Stage(home, home, addition));
        var discarded = guest.WithdrawEdits(); await Task.WhenAll(queued, discarded);
        await Until(() => guestResults.Skip(count).Any(r => r.Outcome == "withdrawn"));
        await Until(() => owner.Editing!.Pending!.Length == 0);
        // A decorator can't stage more changes than the owner could review: each would be packed under the relay's lock.
        await Task.Delay(1600);
        var many = home with { Furnishings = [.. home.Furnishings, .. Enumerable.Range(0, 129).Select(_ => new Furnishing { ItemId = 3 })] };
        var tooMany = HostedEditing.Stage(home, home, many); await guest.StageEdits(tooMany);
        await Until(() => guestResults.Any(r => r.Id == tooMany.Id && r.Outcome == "error"));
        Require(owner.Editing!.Pending!.Length == 0);
        // Rejection includes a compressed operation, even when the expanded edit is >64 KiB.
        await Task.Delay(1600);
        var large = home with { Stage = JsonSerializer.SerializeToElement(new { FormatVersion = 2, Objects = new { }, Info = new { Description = Convert.ToBase64String(RandomNumberGenerator.GetBytes(100000)) } }) };
        await guest.StageEdits(HostedEditing.Create(home, large)); await Until(() => owner.Editing!.Pending!.Length == 1);
        var largeId = owner.Editing!.Pending!.Single(); await owner.ReviewEdits(new(largeId, false));
        await Until(() => guestResults.Any(r => r.Id == largeId && r.Rejected != null));
        Require(HostedEditing.ExpandedSize(HostedEditing.Pack(guestResults.Last(r => r.Id == largeId).Rejected)) > 65536);
        // Sender classification is relay identity based, independent of local character spelling.
        foreach (var channel in new[] { 10, 11, 14, 28, 30 }) await guest.SendRoomChat(new(channel, "chat audit"));
        await Until(() => ownChat.Count == 5 && friendChat.Count == 5); Require(ownChat.All(x => x) && friendChat.All(x => !x));
        await owner.SendHostTransfer(viewer.LocalPeerId); await Until(() => viewer.IsHost && !owner.IsHost);
        Require(viewer.Editing!.Owner == cid);
        count = guestResults.Count; await guest.ReviewStaged(new([Guid.NewGuid()], true)); await Until(() => guestResults.Count > count); Require(guestResults.Last().Outcome == "error");
        await viewer.SendSessionEnd(); await guest.Disconnect(); await owner.Disconnect(); await viewer.Disconnect();
    }
    public static async Task Relay(Uri endpoint, House home)
    {
        var cid = (ulong)Random.Shared.NextInt64(100000000, long.MaxValue - 10);
        var code = "editing-test-" + Guid.NewGuid().ToString("N");
        using var owner = new RelaySyncService(new ClientTestLog());
        using var guest = new RelaySyncService(new ClientTestLog());
        using var viewer = new RelaySyncService(new ClientTestLog());
        var proposals = new ConcurrentDictionary<Guid, EditProposal>();
        var guestResults = new ConcurrentQueue<EditResult>(); var ownerResults = new ConcurrentQueue<EditResult>();
        owner.OnEditProposal += p => proposals[p.Batch.Id] = p;
        guest.OnEditResult += r => guestResults.Enqueue(r); owner.OnEditResult += r => ownerResults.Enqueue(r);
        foreach (var client in new[] { owner, guest, viewer })
        {
            var local = client;
            local.OnHostTransfer += id => local.IsHost = id == local.LocalPeerId;
            local.OnHousingSnapshot += bytes => { if (!local.HousingSceneReady) _ = local.SendHousingReady(HostedEditing.Hash(HouseFormat.Parse(bytes))); };
        }
        Require(await owner.Connect(endpoint.ToString(), "", true, code, [], cid, 0, "DecorOwner"));
        await Until(() => owner.RoomJoinedAcknowledged);
        await owner.SendHousingSnapshot(HouseFormat.Serialize(home)); await owner.SendHousingReady(HostedEditing.Hash(home));
        await Until(() => owner.HousingSceneReady && owner.Editing?.Owner == cid);
        Require(await guest.Connect(endpoint.ToString(), "", false, code, [cid], cid + 1, 0, "Decorator"));
        Require(await viewer.Connect(endpoint.ToString(), "", false, code, [cid], cid + 2, 0, "Viewer"));
        await Until(() => guest.HousingSceneReady && viewer.HousingSceneReady && guest.Editing != null);
        var placed = home with { Furnishings = [new() { ItemId = 1 }] };
        var batch = HostedEditing.Create(home, placed);
        await guest.SubmitEdits(batch); await Until(() => !guestResults.IsEmpty);
        Require(guestResults.Last().Outcome == "error");
        await owner.SetEditing(new(true, [cid + 1])); await Until(() => guest.Editing?.CanEdit(cid + 1) == true);
        var count = guestResults.Count;
        await guest.SubmitEdits(batch); await Until(() => proposals.ContainsKey(batch.Id));
        Require(HostedEditing.Same(owner.SharedHouse!, home) && HostedEditing.Same(viewer.SharedHouse!, home)); // Proposal is private to the owner.
        await guest.SetEditing(new(true, [cid + 2])); await Until(() => guestResults.Count > count + 1);
        Require(guest.Editing!.Editors.SequenceEqual(new[] { cid + 1 }));
        count = guestResults.Count;
        await guest.ReviewEdits(new(batch.Id, true)); await Until(() => guestResults.Count > count);
        Require(guestResults.Last().Outcome == "error");
        await owner.ReviewEdits(new(batch.Id, true));
        // The owner's own copy arrives over the relay like everyone's, and may come last on a slow machine.
        await Until(() => viewer.SharedHouse?.Furnishings.Length == 1 && guest.SharedHouse?.Furnishings.Length == 1
            && owner.SharedHouse is { } shared && HostedEditing.Same(shared, placed));
        Require(HostedEditing.Same(owner.SharedHouse!, placed));
        // Concurrent proposals touching different furnishings can be approved in either order.
        await owner.SetEditing(new(true, [cid + 1, cid + 2]));
        await Until(() => viewer.Editing?.CanEdit(cid + 2) == true);
        await Task.Delay(1600);
        var guestAddition = placed with { Furnishings = [..placed.Furnishings, new() { ItemId = 2 }] };
        var viewerAddition = placed with { Furnishings = [..placed.Furnishings, new() { ItemId = 3 }] };
        var guestBatch = HostedEditing.Create(placed, guestAddition); var viewerBatch = HostedEditing.Create(placed, viewerAddition);
        await guest.SubmitEdits(guestBatch); await viewer.SubmitEdits(viewerBatch);
        await Until(() => proposals.ContainsKey(guestBatch.Id) && proposals.ContainsKey(viewerBatch.Id));
        await owner.ReviewEdits(new(guestBatch.Id, true)); await owner.ReviewEdits(new(viewerBatch.Id, true));
        await Until(() => viewer.SharedHouse!.Furnishings.Length == 3);
        Require(viewer.SharedHouse!.Furnishings.Select(x => x.ItemId).Order().SequenceEqual(new uint[] { 1, 2, 3 }));
        placed = viewer.SharedHouse!;
        // Withdrawal removes a pending proposal; it cannot be approved after discard.
        await Task.Delay(1600);
        var withdrawBatch = HostedEditing.Create(placed, placed with { Furnishings = [..placed.Furnishings, new() { ItemId = 4 }] });
        await guest.SubmitEdits(withdrawBatch); await Until(() => proposals.ContainsKey(withdrawBatch.Id));
        await guest.WithdrawEdits(); await Until(() => guestResults.Any(r => r.Id == withdrawBatch.Id && r.Outcome == "withdrawn"));
        var ownerCount = ownerResults.Count; await owner.ReviewEdits(new(withdrawBatch.Id, true));
        await Until(() => ownerResults.Count > ownerCount); Require(ownerResults.Last().Outcome == "error" && viewer.SharedHouse!.Furnishings.Length == 3);
        // A music-only change never contains an owner's unfinished private geometry.
        var privateDraft = placed with { Furnishings = placed.Furnishings.Select((f, i) => i == 0 ? f with { Scale = 3 } : f).ToArray() };
        var music = placed.Music with { Playing = true };
        await owner.SetSharedMusic(music); await Until(() => viewer.SharedHouse!.Music.Playing);
        Require(viewer.SharedHouse!.Furnishings[0].Scale == 1);
        await Task.Delay(1600);
        var moved = HostedEditing.Create(placed, privateDraft);
        await guest.SubmitEdits(moved); await Until(() => proposals.ContainsKey(moved.Id));
        await owner.SetEditing(new(false, []));
        await Until(() => guest.Editing?.Enabled == false && guestResults.Any(r => r.Id == moved.Id && r.Outcome == "rejected"));
        var ownerMove = HostedEditing.Create(placed, privateDraft);
        await owner.SubmitEdits(ownerMove); await Until(() => viewer.SharedHouse!.Furnishings[0].Scale == 3);
        // Transport host changes, but approval authority does not.
        await owner.SendHostTransfer(guest.LocalPeerId); await Until(() => guest.IsHost && !owner.IsHost);
        Require(guest.Editing!.Owner == cid && !guest.Editing.CanEdit(cid + 1));
        count = guestResults.Count; await guest.SetSharedMusic(new()); await Until(() => guestResults.Count > count);
        Require(guestResults.Last().Outcome == "error" && viewer.SharedHouse!.Music.Playing);
        // The original owner can publish even when another participant carries transport hosting.
        await Task.Delay(1600);
        var final = privateDraft with { Furnishings = [privateDraft.Furnishings[0] with { Rotation = 90 }] };
        await owner.SubmitEdits(HostedEditing.Create(privateDraft, final)); await Until(() => viewer.SharedHouse!.Furnishings[0].Rotation == 90);
        // Original ownership and the latest committed scene survive authenticated reconnection.
        var ws = (ClientWebSocket)typeof(RelaySyncService).GetField("ws", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
        ws.Abort(); await Until(() => owner.IsRecovering); await Until(() => owner.HousingSceneReady && !owner.IsRecovering);
        Require(owner.Editing!.Owner == cid && HostedEditing.Same(owner.SharedHouse!, final));
        Require(owner.HousingMembers.Count == 2);
        await guest.SendSessionEnd(); await guest.Disconnect(); await owner.Disconnect(); await viewer.Disconnect();
    }
    public static async Task RestoreAuthority()
    {
        var home = Home(); var roomId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var until = now + 600000;
        var ownerToken = new string('A', 64); var hostToken = new string('B', 64);
        var state = new RecoveryRoom(roomId, SHA256.HashData(Encoding.ASCII.GetBytes("restore-authority-test")), "bbbbbbbbbbbb",
            [new("aaaaaaaaaaaa", 901, "OriginalOwner", SHA256.HashData(Encoding.ASCII.GetBytes(ownerToken)), until),
             new("bbbbbbbbbbbb", 902, "PromotedHost", SHA256.HashData(Encoding.ASCII.GetBytes(hostToken)), until)],
            [], null, null, until, Owner: 901, LayoutRevision: 7, PublishedHash: PublishedHouseScene.SnapshotHash(home));
        await using var app = RelayApp.Build(["--urls", "http://127.0.0.1:0", "--HAVEN_RECOVERY_KEY", "restore-test-key"], [new(8, roomId, false, state)]);
        await app.StartAsync(); var endpoint = new Uri(app.Urls.Single().Replace("http:", "ws:") + "/");
        using var host = await Client.Connect(endpoint, new() { ContentId = 902, CharacterName = "PromotedHost", RoomId = roomId, ResumeToken = hostToken });
        var ack = MessagePackSerializer.Deserialize<RoomJoinedPayload>((await host.Expect(WireKind.RoomJoined)).Payload, WireFormat.Options);
        Require(ack.NeedsSnapshot && ack.IsHost);
        var access = JsonSerializer.Deserialize<EditingState>((await host.Expect(HousingWire.EditState)).Payload)!;
        Require(access.Owner == 901 && !access.CanEdit(902));
        var wrong = home with { Furnishings = [home.Furnishings[0] with { Scale = 2 }] };
        foreach (var kind in new[] { HousingWire.RestoreSnapshot, HousingWire.CompressedSnapshot })
        {
            await host.Send(kind, HousingSnapshotCodec.Encode(wrong));
            Require(JsonSerializer.Deserialize<EditResult>((await host.Expect(HousingWire.EditResult)).Payload)!.Outcome == "error");
        }
        // Even the original owner cannot silently substitute a draft for the approved recovery copy.
        using var owner = await Client.Connect(endpoint, new() { ContentId = 901, CharacterName = "OriginalOwner", RoomId = roomId, ResumeToken = ownerToken });
        await owner.Expect(WireKind.RoomJoined);
        await owner.Send(HousingWire.RestoreSnapshot, HousingSnapshotCodec.Encode(wrong)); await owner.Expect(HousingWire.EditResult);
        await owner.Send(HousingWire.RestoreSnapshot, HousingSnapshotCodec.Encode(home));
        Require(HostedEditing.Same(HouseFormat.Parse((await host.Expect(HousingWire.Snapshot)).Payload), home));
        await owner.Ready(home); await host.Ready(home);
        await host.Send(HousingWire.EditSubmit, HostedEditing.Pack(HostedEditing.Create(home, wrong)));
        Require(JsonSerializer.Deserialize<EditResult>((await host.Expect(HousingWire.EditResult)).Payload)!.Outcome == "error");
        await host.Send(WireKind.SessionEnd, []); await owner.Expect(WireKind.SessionEnd);
        await app.StopAsync();
    }

}
