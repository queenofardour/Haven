using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Haven.Core;
using HavenNative.Wire;

namespace Haven.Relay;

internal sealed partial class RoomHub
{
    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private void Checkpoint(Room room, Peer? carrier = null, bool includeHouse = false, bool removed = false)
    {
        if (!durableRecovery) return;
        carrier = carrier?.Online == true ? carrier : room.Peers.FirstOrDefault(p => p.Online);
        if (carrier == null) return;
        var until = NowMs + (long)RecoveryPolicy.Reservation.TotalMilliseconds;
        var state = removed ? null : new RecoveryRoom(room.Id, room.PasswordHash, room.HostId,
            room.Peers.Select(p => new RecoveryMember(p.Id, p.ContentId, p.Name, p.ResumeHash, p.ResumeUntil == 0 ? until : p.ResumeUntil)).ToArray(),
            room.Banned.ToArray(), includeHouse ? room.House : null, room.Zone, until, room.Owner, room.EditingEnabled, room.Editors.ToArray(), room.LayoutRevision, room.PublishedHash);
        var checkpoint = JsonSerializer.SerializeToUtf8Bytes(new RecoveryCheckpoint(++room.Revision, room.Id, removed, state));
        // Put the durability barrier on every affected stream before its following control messages. Only the
        // carrier's stream gets the checkpoint itself (the house can be a megabyte); the others get a few bytes that
        // hold their stream at the edge until that revision is durable (relay-proxy / HavenRelay.awaitDurable).
        var barrier = JsonSerializer.SerializeToUtf8Bytes(new { Revision = room.Revision, RoomId = room.Id, Barrier = true });
        carrier.Queue(HousingWire.RecoveryCheckpoint, WireFormat.RelaySender, checkpoint);
        foreach (var recipient in room.Peers.Where(p => p.Online && p != carrier))
            recipient.Queue(HousingWire.RecoveryCheckpoint, WireFormat.RelaySender, barrier);
        room.LastCheckpoint = DateTimeOffset.UtcNow;
    }

    /// <summary>Restores a room, or logs and skips a checkpoint that can't be restored (the others still are).</summary>
    public bool TryRestore(RecoveryCheckpoint checkpoint)
    {
        try { Restore(checkpoint); return true; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or FormatException or JsonException)
        {
            log.LogWarning("relay_room_restore_skipped boot={Boot} room={Room} reason={Reason}", BootId, checkpoint.RoomId, ex.Message);
            return false;
        }
    }

    public void Restore(RecoveryCheckpoint checkpoint)
    {
        lock (gate)
        {
            var state = checkpoint.State;
            if (checkpoint.Removed || state == null || state.ExpiresAt < NowMs || rooms.ContainsKey(state.Id)) return;
            if (rooms.Count >= 32 || state.Id.Length != 32 || checkpoint.RoomId != state.Id || state.PasswordHash.Length != 32
                || state.Members.Length is 0 or > RoomCap || state.Members.Select(m => m.Id).Distinct().Count() != state.Members.Length
                || state.Members.Select(m => m.ContentId).Distinct().Count() != state.Members.Length
                || !state.Members.Any(m => m.Id == state.HostId)) throw new InvalidDataException("Invalid room checkpoint.");
            var room = new Room { Id = state.Id, PasswordHash = state.PasswordHash, HostId = state.HostId,
                Revision = checkpoint.Revision, House = state.House, Zone = state.Zone,
                Owner = state.Owner == 0 ? state.Members.First(m => m.Id == state.HostId).ContentId : state.Owner,
                EditingEnabled = state.EditingEnabled && state.Members.Any(m => m.ContentId == state.Owner), LayoutRevision = state.LayoutRevision, PublishedHash = state.PublishedHash };
            if (room.House != null)
            {
                var house = HousingSnapshotCodec.Decode(room.House);
                var hash = PublishedHouseScene.SnapshotHash(house);
                if (room.PublishedHash != null && room.PublishedHash != hash) throw new InvalidDataException("Recovery scene fingerprint differs.");
                room.PublishedHash = hash;
                room.Current = house; room.CurrentMusic = null; room.Scene = new PublishedHouseScene(house); room.Territory = house.TerritoryId; room.HouseId = house.Id;
            }
            foreach (var member in state.Members)
            {
                if (member.Id.Length != 12 || member.ContentId == 0 || member.TokenHash.Length != 32
                    || string.IsNullOrWhiteSpace(member.Name) || member.Name.Length > 80 || member.Name.Any(char.IsControl))
                    throw new InvalidDataException("Invalid recovery member.");
                room.Peers.Add(new Peer(null, CancellationToken.None) { Id = member.Id, ContentId = member.ContentId,
                    Name = member.Name, ResumeHash = member.TokenHash, ResumeUntil = member.ResumeUntil, Room = room });
            }
            if (state.PublishedHash != null && (state.PublishedHash.Length != 64 || !state.PublishedHash.All(Uri.IsHexDigit))) throw new InvalidDataException("Invalid recovery scene fingerprint.");
            if (state.Editors?.Length > RoomCap || state.LayoutRevision < 0) throw new InvalidDataException("Invalid editing checkpoint.");
            room.Editors.UnionWith((state.Editors ?? []).Where(id => room.Peers.Any(p => p.ContentId == id)));
            room.Banned.UnionWith(state.Banned);
            rooms.Add(room.Id, room);
            log.LogInformation("relay_room_restored boot={Boot} room={Room} members={Count}", BootId, room.Id, room.Peers.Count);
        }
    }

    private void Resume(Peer peer, JoinPayload join)
    {
        var room = rooms.GetValueOrDefault(join.RoomId);
        var member = room?.Peers.FirstOrDefault(p => p.ContentId == join.ContentId);
        if (room == null || member == null || join.ResumeToken.Length != 64 || room.Banned.Contains(join.ContentId)
            || (member.ResumeUntil != 0 && member.ResumeUntil < NowMs)
            || !CryptographicOperations.FixedTimeEquals(member.ResumeHash, SHA256.HashData(Encoding.ASCII.GetBytes(join.ResumeToken))))
        { Error(peer, ErrCode.RoomNotFound, "This gathering cannot be resumed with those credentials. Your local house can stay open; leave when ready."); return; }
        peer.Id = member.Id; peer.ContentId = member.ContentId; peer.Name = member.Name; peer.ResumeHash = member.ResumeHash;
        peer.ContentSecret = ContentLocker.IsSecret(join.ContentSecret) ? join.ContentSecret : member.ContentSecret;
        peer.Batching = join.Batching;
        peer.Room = room;
        peer.ReplayChat = true;
        foreach (var pair in member.Latest) peer.Latest[pair.Key] = pair.Value;
        var index = room.Peers.IndexOf(member);
        room.Peers[index] = peer;
        member.Room = null;
        try { member.Abort(); } catch (ObjectDisposedException) { }
        Checkpoint(room, peer);
        peer.Queue(WireKind.RoomJoined, WireFormat.RelaySender, Encode(new RoomJoinedPayload { AssignedPeerId = peer.Id,
            IsHost = room.HostId == peer.Id, RoomId = room.Id, RoomCap = RoomCap, ResumeToken = join.ResumeToken, Resumed = true, NeedsSnapshot = room.House == null }));
        foreach (var other in room.Peers.Where(p => p != peer))
        {
            peer.Queue(WireKind.PeerJoined, other.Id, Encode(new PeerJoinedPayload { PeerId = other.Id, ContentId = other.ContentId, CharacterName = other.Name }));
            peer.Queue(HousingWire.PeerConnection, WireFormat.RelaySender, JsonSerializer.SerializeToUtf8Bytes(new PeerConnection(other.Id, other.Online, other.ResumeUntil)));
        }
        peer.Queue(WireKind.HostTransfer, room.HostId, Encode(new HostTransferPayload { TargetPeerId = room.HostId }));
        if (room.House != null) peer.Queue(HousingWire.CompressedSnapshot, WireFormat.RelaySender, room.House);
        SendEditing(room, peer);
        // No ZoneLoad event on resume: clients retain the scene and current local transform.
        log.LogInformation("relay_resumed boot={Boot} room={Room} peer={Peer}", BootId, room.Id, peer.Id);
    }

    private void Suspend(Peer peer)
    {
        if (peer.Room is not {} room) return;
        peer.Ready = false;
        peer.ResumeUntil = NowMs + (long)RecoveryPolicy.Reservation.TotalMilliseconds;
        // A dropped player's ball is free again at once (their Haven sends its entry again after a resume).
        if (room.Poses.Remove(peer.ContentId) is { } released && !room.Peers.Any(p => p != peer && p.ContentId == peer.ContentId))
            Broadcast(room, HousingWire.PoseState, WireFormat.RelaySender, PoseWire.Encode([released]), peer);
        Checkpoint(room);
        Broadcast(room, HousingWire.PeerConnection, WireFormat.RelaySender, JsonSerializer.SerializeToUtf8Bytes(new PeerConnection(peer.Id, false, peer.ResumeUntil)), peer);
        log.LogInformation("relay_reserved boot={Boot} room={Room} peer={Peer} until={Until}", BootId, room.Id, peer.Id, peer.ResumeUntil);
    }

    private void ExpireReservations()
    {
        foreach (var room in rooms.Values.ToArray())
            foreach (var member in room.Peers.Where(p => p.ResumeUntil != 0 && p.ResumeUntil < NowMs).ToArray())
                Depart(member);
    }
}
