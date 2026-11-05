using Haven.Core;

namespace HavenNative.Sync;

// Invite-only gatherings: the owner's guest list to the relay, and the list the relay keeps
// back (its answer, which says the relay enforces it; relays before it never answer).
public partial class RelaySyncService
{
    public event Action<GuestList>? OnGuestList;
    public async Task SendGuestList(GuestList list)
    {
        var socket = ws;
        if (!IsConnected || socket == null) return;
        await SendFrame(HousingWire.GuestList, list.Encode(), socket);
    }
}
