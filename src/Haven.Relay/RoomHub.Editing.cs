using System.Text.Json;
using Haven.Core;
using HavenNative.Wire;

namespace Haven.Relay;

internal sealed partial class RoomHub
{
    private sealed record PendingEdit(EditProposal Proposal, byte[] Wire, int Cost);
    // The largest review (approve or reject several proposals) the relay expands: well over the 128 a room can hold.
    internal const int MaxReviewBytes = 64 * 1024;
    private static EditingState Editing(Room room) => new(room.Owner, room.EditingEnabled, room.Editors.Order().ToArray(),
        room.LayoutRevision, room.Scene?.Hash ?? "", room.Proposals.Keys.ToArray(), Protocol: 2);
    private static void Result(Peer peer, Guid id, string outcome, string message)
        => peer.Queue(HousingWire.EditResult, WireFormat.RelaySender, JsonSerializer.SerializeToUtf8Bytes(new EditResult(id, outcome, message)));
    private static void SendEditing(Room room, Peer peer)
    {
        peer.Queue(HousingWire.EditState, WireFormat.RelaySender, JsonSerializer.SerializeToUtf8Bytes(Editing(room)));
        if (peer.ContentId == room.Owner)
            foreach (var proposal in room.Proposals.Values) peer.Queue(HousingWire.EditProposal, WireFormat.RelaySender, proposal.Wire);
    }
    private static void BroadcastEditing(Room room) => Broadcast(room, HousingWire.EditState, WireFormat.RelaySender,
        JsonSerializer.SerializeToUtf8Bytes(Editing(room)));
    private static void SendScene(Room room, Peer peer)
    {
        if (room.House != null) peer.Queue(HousingWire.CompressedSnapshot, WireFormat.RelaySender, room.House);
        SendEditing(room, peer);
    }
    private void RemoveEditingPeer(Room room, Peer peer)
    {
        room.Editors.Remove(peer.ContentId);
        foreach (var entry in room.Proposals.Where(p => p.Value.Proposal.Author == peer.ContentId || peer.ContentId == room.Owner).ToArray())
        {
            var author = room.Peers.FirstOrDefault(p => p.ContentId == entry.Value.Proposal.Author);
            if (author != null) Result(author, entry.Key, "rejected", "Decorating access ended. Your private draft is preserved.");
            room.Proposals.Remove(entry.Key);
        }
        if (peer.ContentId == room.Owner) { room.EditingEnabled = false; room.Editors.Clear(); }
        BroadcastEditing(room); // Host succession never changes Owner.
    }
    private void PublishEdit(Room room, Peer carrier, ulong author, EditBatch batch)
    {
        var next = HostedEditing.Apply(room.Current!, batch);
        var compressed = HousingSnapshotCodec.Encode(next);
        // Build and validate the downleg before changing the authoritative state.
        var scene = new PublishedHouseScene(next);
        var message = HostedEditing.Pack(new EditApplied(author, batch, room.LayoutRevision + 1, scene.Hash));
        room.Current = next; room.Poses.Keep(next); room.CurrentMusic = null; room.PublishedHash = PublishedHouseScene.SnapshotHash(next); room.House = compressed; room.Scene = scene; room.LayoutRevision++;
        room.Proposals.Remove(batch.Id);
        Checkpoint(room, carrier, includeHouse: true);
        Broadcast(room, HousingWire.EditApplied, WireFormat.RelaySender, message);
        var submitter = room.Peers.FirstOrDefault(p => p.ContentId == author);
        if (submitter != null) Result(submitter, batch.Id, "applied", "Changes published to the gathering.");
        BroadcastEditing(room);
    }
    private bool HandleEditing(Peer peer, byte kind, byte[] payload)
    {
        if (kind is not (HousingWire.EditPermissions or HousingWire.EditSubmit or HousingWire.EditDecision or HousingWire.SceneRequest or HousingWire.MusicUpdate or HousingWire.EditWithdraw or HousingWire.EditStage or HousingWire.EditReview)) return false;
        var room = peer.Room!;
        var id = Guid.Empty;
        try
        {
            if (kind == HousingWire.SceneRequest)
            {
                if (NowMs >= peer.NextScene) { peer.NextScene = NowMs + 2000; SendScene(room, peer); }
                return true;
            }
            if (kind == HousingWire.EditWithdraw)
            {
                var pendingEdits = room.Proposals.Where(p => p.Value.Proposal.Author == peer.ContentId).ToArray();
                foreach (var pending in pendingEdits)
                { room.Proposals.Remove(pending.Key); Result(peer, pending.Key, "withdrawn", "Your pending proposal was withdrawn."); }
                if (pendingEdits.Length > 0) BroadcastEditing(room); return true;
            }
            if (!peer.Ready || room.Current == null) throw new InvalidOperationException("Wait for the shared home to finish loading.");
            if (kind == HousingWire.EditPermissions)
            {
                if (peer.ContentId != room.Owner) throw new InvalidOperationException("Only the original home owner can choose decorators.");
                if (payload.Length > 2048) throw new InvalidDataException("Invalid decorating access.");
                var permission = JsonSerializer.Deserialize<EditingPermissions>(payload) ?? throw new InvalidDataException("Missing decorating access.");
                if (permission.Editors == null || permission.Editors.Length > RoomCap
                    || permission.Editors.Any(cid => cid == room.Owner || !room.Peers.Any(p => p.ContentId == cid)))
                    throw new InvalidDataException("Choose decorators from the current gathering.");
                room.EditingEnabled = permission.Enabled; room.Editors.Clear();
                if (permission.Enabled) room.Editors.UnionWith(permission.Editors);
                foreach (var entry in room.Proposals.Where(x => !room.Editors.Contains(x.Value.Proposal.Author)).ToArray())
                {
                    var author = room.Peers.FirstOrDefault(p => p.ContentId == entry.Value.Proposal.Author);
                    if (author != null) Result(author, entry.Key, "rejected", "Decorating access ended. Your private draft is preserved.");
                    room.Proposals.Remove(entry.Key);
                }
                Checkpoint(room, peer); BroadcastEditing(room); return true;
            }
            if (kind == HousingWire.MusicUpdate)
            {
                if (peer.ContentId != room.Owner) throw new InvalidOperationException("Only the original home owner can change the shared music.");
                // Each change re-encodes and checkpoints the whole house.
                if (!peer.MusicRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency))
                    throw new InvalidOperationException("The music is changing too quickly. Wait a moment and try again.");
                var music = HostedEditing.Unpack<MusicOptions>(payload);
                var next = room.Current with { Music = music };
                var compressed = HousingSnapshotCodec.Encode(next);
                room.Current = next; room.Poses.Keep(next); room.CurrentMusic = null; room.PublishedHash = PublishedHouseScene.SnapshotHash(next); room.House = compressed;
                Checkpoint(room, peer, includeHouse: true);
                Broadcast(room, HousingWire.MusicUpdate, WireFormat.RelaySender, payload); return true;
            }
            if (kind == HousingWire.EditDecision)
            {
                if (peer.ContentId != room.Owner) throw new InvalidOperationException("Only the original home owner can approve or reject proposals.");
                if (payload.Length > 256) throw new InvalidDataException("Invalid review decision.");
                if (!Reviewing(peer)) throw new InvalidOperationException("Reviewing too quickly. Wait a moment and try again.");
                var decision = JsonSerializer.Deserialize<EditDecision>(payload) ?? throw new InvalidDataException("Missing review decision.");
                id = decision.Id;
                if (!room.Proposals.TryGetValue(id, out var pending)) throw new InvalidOperationException("This proposal is no longer pending.");
                ResolveEdits(room, peer, new([id], decision.Accept));
                return true;
            }
            if (kind == HousingWire.EditReview)
            {
                if (peer.ContentId != room.Owner) throw new InvalidOperationException("Only the original home owner can approve or reject proposals.");
                // A review is a list of proposal IDs (a few KB at most): refuse one that claims to expand further before
                // decompressing it, as any frame could otherwise make the relay inflate 4 MiB under the rooms' lock.
                if (payload.Length < 5 || HostedEditing.ExpandedSize(payload) is <= 0 or > MaxReviewBytes) throw new InvalidDataException("Invalid review.");
                if (!Reviewing(peer)) throw new InvalidOperationException("Reviewing too quickly. Wait a moment and try again.");
                ResolveEdits(room, peer, HostedEditing.Unpack<EditReview>(payload)); return true;
            }
            if (!Editing(room).CanEdit(peer.ContentId)) throw new InvalidOperationException("The owner has not invited you to decorate.");
            if (NowMs < peer.NextEdit) throw new InvalidOperationException("Please wait a moment before submitting another saved batch.");
            peer.NextEdit = NowMs + 1500;
            var batch = HostedEditing.Unpack<EditBatch>(payload); id = batch.Id;
            HostedEditing.Validate(batch);
            if (batch.Changes.Length == 0) throw new InvalidDataException("There are no changes to save.");
            if (kind == HousingWire.EditStage)
            {
                if (peer.ContentId == room.Owner) throw new InvalidOperationException("The owner publishes their own draft explicitly.");
                StageEdits(room, peer, batch); return true;
            }
            _ = HostedEditing.Apply(room.Current, batch); // Conflict check before storing a proposal.
            if (peer.ContentId == room.Owner) PublishEdit(room, peer, peer.ContentId, batch);
            else
            {
                var owner = room.Peers.FirstOrDefault(p => p.ContentId == room.Owner && p.Online);
                if (owner == null) throw new InvalidOperationException("The home owner is offline. Keep your draft and submit when they return.");
                var proposal = new EditProposal(peer.ContentId, peer.Name, batch);
                var wire = HostedEditing.Pack(proposal); var cost = HostedEditing.ExpandedSize(wire);
                var previous = room.Proposals.Where(x => x.Value.Proposal.Author == peer.ContentId).ToArray();
                var previousCost = previous.Sum(x => x.Value.Cost);
                if (room.Proposals.Values.Sum(x => x.Cost) - previousCost + cost > 2 * HouseFormat.MaxBytes
                    || rooms.Values.Sum(r => r.Proposals.Values.Sum(x => x.Cost)) - previousCost + cost > 8 * HouseFormat.MaxBytes)
                    throw new InvalidOperationException("The review queue is full. Ask the owner to review pending work first.");
                foreach (var entry in previous) room.Proposals.Remove(entry.Key);
                room.Proposals[batch.Id] = new(proposal, wire, cost);
                owner.Queue(HousingWire.EditProposal, WireFormat.RelaySender, wire);
                Result(peer, id, "submitted", "Proposal sent. Only the home owner can publish it.");
                BroadcastEditing(room);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or JsonException or ArgumentException or NullReferenceException)
        { Result(peer, id, "error", ex.Message); }
        return true; // Editing failures never disconnect the house or touch the normal game world.
    }

    private void StageEdits(Room room, Peer peer, EditBatch batch)
    {
        var owner = room.Peers.FirstOrDefault(p => p.ContentId == room.Owner && p.Online);
        if (owner == null) throw new InvalidOperationException("The home owner is reconnecting. Your edits will stage when they return.");
        // Each change becomes its own proposal, packed on its own: bounded before any of that work (under the rooms' lock).
        var operations = batch.Changes.Where(c => !HostedEditing.NoChange(c)).ToArray();
        if (operations.Length > MaxStagedPerAuthor) throw new InvalidOperationException(TooManyStaged);
        _ = HostedEditing.Apply(room.Current!, batch);
        // The paths this batch touches and their parents: a pending change overlaps the batch when one of its leading
        // parts is a touched path, or it is the parent of one. A lookup per change instead of comparing every pair.
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in batch.Changes)
        {
            touched.Add(PathKey(change.Path, change.Path.Length));
            for (var i = 1; i < change.Path.Length; i++) parents.Add(PathKey(change.Path, i));
        }
        bool Overlapping(string[] path)
        {
            if (parents.Contains(PathKey(path, path.Length))) return true;
            for (var i = 1; i <= path.Length; i++) if (touched.Contains(PathKey(path, i))) return true;
            return false;
        }
        var mine = room.Proposals.Where(p => p.Value.Proposal.Author == peer.ContentId).ToArray();
        var previous = mine.Where(p => p.Value.Proposal.Batch.Changes.Any(c => Overlapping(c.Path))).ToArray();
        // A pending single change at the same path that this one leaves as it was is kept as it is.
        var singles = new Dictionary<string, PendingEdit>(StringComparer.Ordinal);
        foreach (var entry in previous)
            if (entry.Value.Proposal.Batch.Changes is [var old]) singles.TryAdd(PathKey(old.Path, old.Path.Length), entry.Value);
        List<PendingEdit> updates = [];
        foreach (var operation in operations)
        {
            if (singles.TryGetValue(PathKey(operation.Path, operation.Path.Length), out var same) && same.Proposal.Batch.Changes is [var old]
                && HostedEditing.NoChange(operation with { BeforeExists = old.AfterExists, Before = old.After }))
            { updates.Add(same); continue; }
            var part = batch with { Id = Guid.NewGuid(), Changes = [operation] };
            var proposal = new EditProposal(peer.ContentId, peer.Name, part);
            var wire = HostedEditing.Pack(proposal);
            updates.Add(new(proposal, wire, HostedEditing.ExpandedSize(wire)));
        }
        if (mine.Length - previous.Length + updates.Count > MaxStagedPerAuthor) throw new InvalidOperationException(TooManyStaged);
        if (room.Proposals.Count - previous.Count(p => !updates.Contains(p.Value)) + updates.Count(u => !room.Proposals.ContainsKey(u.Proposal.Batch.Id)) > HostedEditing.MaxStagedPerHome)
            throw new InvalidOperationException("The review queue is full. Ask the owner to review pending work first.");
        var cost = updates.Sum(x => x.Cost) - previous.Sum(x => x.Value.Cost);
        if (room.Proposals.Values.Sum(x => x.Cost) + cost > 2 * HouseFormat.MaxBytes
            || rooms.Values.Sum(r => r.Proposals.Values.Sum(x => x.Cost)) + cost > 8 * HouseFormat.MaxBytes)
            throw new InvalidOperationException("The review queue is full. Ask the owner to review pending work first.");
        foreach (var entry in previous.Where(p => !updates.Contains(p.Value))) room.Proposals.Remove(entry.Key);
        foreach (var update in updates)
        {
            if (room.Proposals.ContainsKey(update.Proposal.Batch.Id)) continue;
            room.Proposals[update.Proposal.Batch.Id] = update;
            owner.Queue(HousingWire.EditProposal, WireFormat.RelaySender, update.Wire);
        }
        Result(peer, batch.Id, "staged", "Changes are visible to the home owner and awaiting approval.");
        BroadcastEditing(room);
    }

    // The owner's Haven shows as many staged changes as the relay keeps (HostedEditing): 128 per decorator, 512 a home.
    private const int MaxStagedPerAuthor = HostedEditing.MaxStagedPerAuthor, MaxReviewIds = 1024;
    private const string TooManyStaged = "You have too many changes waiting for the owner. Ask them to review your work first.";
    private static string PathKey(string[] path, int length) => string.Join('\0', path, 0, length);
    // Approving or declining rebuilds and checkpoints the whole home: a few a second at most.
    private static bool Reviewing(Peer peer) => peer.ReviewRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency);

    private void ResolveEdits(Room room, Peer owner, EditReview review)
    {
        if (review.Ids == null || review.Ids.Length is < 1 or > MaxReviewIds || review.Ids.Distinct().Count() != review.Ids.Length)
            throw new InvalidDataException("Invalid proposal selection.");
        var selected = review.Ids.Select(id => room.Proposals.TryGetValue(id, out var pending) ? pending
            : throw new InvalidOperationException("A proposal changed while you were reviewing it. Review the latest preview.")).ToArray();
        if (review.Accept)
        {
            // Validate the entire selection before changing anything. Conflicting proposals never
            // partially publish when the owner presses Approve all.
            var next = HostedEditing.ApplyAll(room.Current!, selected.Select(x => x.Proposal.Batch).ToArray());
            var batch = HostedEditing.Create(room.Current!, next);
            var scene = new PublishedHouseScene(next);
            var compressed = HousingSnapshotCodec.Encode(next);
            var contributions = selected.Select(p => new EditContribution(p.Proposal.Author, p.Proposal.Batch)).ToArray();
            var wire = HostedEditing.Pack(new EditApplied(0, batch, room.LayoutRevision + 1, scene.Hash, Contributions: contributions));
            room.Current = next; room.Poses.Keep(next); room.CurrentMusic = null; room.House = compressed; room.Scene = scene; room.PublishedHash = PublishedHouseScene.SnapshotHash(next); room.LayoutRevision++;
            foreach (var id in review.Ids) room.Proposals.Remove(id);
            Checkpoint(room, owner, includeHouse: true);
            Broadcast(room, HousingWire.EditApplied, WireFormat.RelaySender, wire);
            foreach (var item in selected)
            {
                var submitter = room.Peers.FirstOrDefault(p => p.ContentId == item.Proposal.Author);
                if (submitter != null) Result(submitter, item.Proposal.Batch.Id, "applied", "Changes published to the gathering.");
            }
        }
        else
        {
            var notices = selected.Select(entry => (entry.Proposal,
                Wire: HostedEditing.Pack(new EditResult(entry.Proposal.Batch.Id, "rejected", "The owner declined a staged change.", entry.Proposal.Batch)))).ToArray();
            foreach (var entry in notices)
            {
                var p = entry.Proposal; room.Proposals.Remove(p.Batch.Id);
                var author = room.Peers.FirstOrDefault(peer => peer.ContentId == p.Author);
                if (author != null) author.Queue(HousingWire.EditResolved, WireFormat.RelaySender, entry.Wire);
            }
        }
        Result(owner, Guid.Empty, "reviewed", review.Accept ? "Selected changes approved and published." : "Selected changes declined.");
        BroadcastEditing(room);
    }
}
