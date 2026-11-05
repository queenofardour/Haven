using Haven.Core;
using HavenNative.Wire;

namespace Haven.Relay;

// Invite-only gatherings: the home's owner sends its guest list (GuestList); while it says
// InviteOnly, a new member must be on it. The joiner is the character the edge verified (AccountRefusal has already
// matched it with the join's), so a visit code alone no longer lets anyone else in. Members already admitted resume as
// before. The list lives in memory only: the owner's Haven sends it again every 20 s and after any change.
internal sealed partial class RoomHub
{
    private sealed partial class Room
    {
        public bool InviteOnly;
        public readonly HashSet<ulong> Invited = [];
    }

    // Null when this character may join the room; otherwise why not.
    private static string? InviteRefusal(Room room, ulong contentId) =>
        !room.InviteOnly || contentId == room.Owner || room.Invited.Contains(contentId) ? null
        : "This gathering is invite-only. Ask the host to invite you from their Visit panel.";

    // Only the home's owner keeps the list, whoever holds the transport host role. The relay answers with what it keeps.
    private void HandleGuestList(Peer peer, byte[] payload)
    {
        var room = peer.Room!;
        if (peer.ContentId != room.Owner) return;
        if (!peer.GuestRate.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency)) return;
        var list = GuestList.Decode(payload);
        room.InviteOnly = list.InviteOnly;
        room.Invited.Clear();
        room.Invited.UnionWith(list.Invited);
        peer.Queue(HousingWire.GuestList, WireFormat.RelaySender, new GuestList(room.InviteOnly, [.. room.Invited]).Encode());
    }
}
