using HavenNative.Wire;

namespace Haven.Core;

public static class RelayReceivePolicy
{
    // Actor echoes must not drive the local character. Room chat deliberately echoes only
    // after relay acceptance, so dropping the sender's frame would erase their own chat line.
    public static bool Accept(byte kind, string senderId, string localPeerId) =>
        senderId != localPeerId || kind is WireKind.HostTransfer or HousingWire.RoomChat;
}
