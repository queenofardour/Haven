namespace Haven.Core;

public static class HousingWire
{
    public const int ProtocolRevision = 40;
    public const byte Snapshot = 0x60;
    public const byte CompressedSnapshot = 0x61;
    public const byte SceneReady = 0x62;
    public const byte SceneReadyAck = 0x63;
    public const byte RoomChat = 0x64;
    public const byte RoomChatRejected = 0x65;
    public const byte RecoveryCheckpoint = 0x66; // Server→Durable Object only; consumed at the edge.
    public const byte PeerConnection = 0x67;
    public const byte Typing = 0x68; // Optional, ephemeral one-byte presence; no draft text.
    public const byte EditPermissions = 0x69, EditState = 0x6A, EditSubmit = 0x6B, EditProposal = 0x6C,
        EditResult = 0x6D, EditDecision = 0x6E, SceneRequest = 0x6F, EditApplied = 0x70, MusicUpdate = 0x71, EditWithdraw = 0x72, RestoreSnapshot = 0x73;
    public const byte EditStage = 0x74, EditReview = 0x75, EditResolved = 0x76;
    public const byte SwitchState = 0x77; // Any ready participant; relay keeps the latest side per furnishing.
    // Relay → a participant who resumed after a dropped connection: the room's chat lines from the last
    // ChatReplay.Window, as RoomChat payloads. The client skips lines it already showed.
    public const byte RoomChatReplay = 0x7B;
    // Relay → a client that joined with Batching: several downleg frames in one message (FrameBatch).
    public const byte Batch = 0x7C;
    // Any ready participant: their pose entry (PoseWire), the balls they have open or hold. The relay keeps one per
    // participant, refuses a ball someone else holds, and gives late joiners the room's table.
    public const byte PoseState = 0x7D;
    // A participant on a set's ball → another on the same set (PoseAdjust): a fit for them to take. The relay forwards
    // it only to the target, and only while both hold balls of that set.
    public const byte PoseAdjust = 0x7E;
    // The owner, a decorator, or a ready participant holding or viewing the set: hide its balls for everyone (PoseClose).
    // The relay drops every entry for that set and passes the close to everyone ready, whose PCs stand up anyone on its
    // balls; anyone else's close is dropped.
    public const byte PoseClose = 0x7F;
    // Relay → client: an operator notice (an announcement, an update, maintenance with a countdown; NoticeWire).
    // Clients before it ignore the kind.
    public const byte Notice = 0x80;
    // A participant → another one (GameMessage): a two-player minigame's invitation, deal or move. The relay stamps the
    // sender and passes it only to the target, while both are ready in the room. Clients before it ignore the kind.
    public const byte MiniGame = 0x81;
    // The home's owner → relay (GuestList): whether only the characters it invited may join, and who they are. The relay
    // answers with the list it keeps. Relays and clients before it ignore the kind.
    public const byte GuestList = 0x82;
    public const int MaxFrameBytes = HouseFormat.MaxBytes + 64;
}
