using Haven.Core;
using HavenNative.Wire;

namespace Haven.Relay;

// Content locker tickets (ContentLocker). The relay never carries file data: it signs short-lived links to the
// Worker in front of R2. Uploads: only the room's owner, only for a custom picture the current home uses.
// Downloads: any ready member, for a picture the current home uses. Either way at most 100 links an hour per peer;
// the Worker's own account-wide budget is what finally bounds storage and cost.
internal sealed partial class RoomHub
{
    public string? ContentKey { get; set; }

    private void HandleContentTicket(Peer peer, ReadOnlySpan<byte> payload)
    {
        var request = ContentWire.DecodeRequest(payload);
        var room = peer.Room!;
        ContentTicket Refuse(string error) => new(request.Kind, request.Hash, Error: error);
        ContentTicket ticket;
        var music = request.Media == ContentLocker.Music;
        // The current home must use it: a frame's picture, or a song in its playlist.
        var used = music ? room.Current?.Music.Playlist.Any(t => t.CustomMusic == request.Hash) == true
            // Animation packages (pose balls) travel as the furniture kind: a zip, with the same limits and expiry.
            : request.Media == ContentLocker.FurnitureMedia ? room.Current is { } home && (home.Furnishings.Any(f => f.CustomFurniture == request.Hash) || PoseSets.Packages(home).Contains(request.Hash))
            : room.Current?.Furnishings.Any(f => f.CustomPicture == request.Hash) == true;
        if (string.IsNullOrEmpty(ContentKey)) ticket = Refuse("Custom content is unavailable on this relay.");
        else if (!used) ticket = Refuse(ContentLocker.NotInHome);
        else if (!RateFor(peer.ContentId).Allow(DateTimeOffset.UtcNow)) ticket = Refuse("Too many custom pictures requested. Try again later.");
        else if (request.Kind is "share" or "unshare") ticket = Refuse(ContentLocker.GalleryClosed);
        else if (request.Kind == "delete") ticket = Refuse("Deleting goes through a content session.");
        else if (request.Kind == "upload")
            ticket = peer.ContentId != room.Owner ? Refuse("Only the home's owner can share its custom pictures.")
                : peer.ContentSecret.Length == 0 ? Refuse("Update Haven to share custom pictures.")
                : request.Size <= 0 || request.Size > ContentLocker.MaxBytes(request.Media) ? Refuse(ContentLocker.TooLarge(request.Media))
                : request.Agreement < ContentLocker.AgreementVersion ? Refuse("Accept the custom content agreement first.")
                : new(request.Kind, request.Hash, ContentLocker.TicketPath(ContentKey, "put", request.Hash, request.Size,
                    ContentLocker.OwnerKey(ContentKey, room.Owner, peer.ContentSecret), request.Agreement, DateTimeOffset.UtcNow, peer.Verified?.ContentId ?? 0, request.Media));
        else if (!peer.Ready) ticket = Refuse("Wait for the home to finish loading.");
        else ticket = new(request.Kind, request.Hash, ContentLocker.TicketPath(ContentKey, "get", request.Hash, 0, "", 0, DateTimeOffset.UtcNow, media: request.Media));
        peer.Queue(ContentWire.Ticket, WireFormat.RelaySender, ContentWire.Encode(ticket));
    }

    // Content sessions: a connection with no room, in which a character's own Haven uploads or deletes its content
    // while not hosting. Tickets are signed for the character the session names, as the relay signs a room owner's;
    // the Worker's budget is what bounds cost whoever claims to be whom. At most 5 minutes, 30 s idle, 100 tickets an
    // hour per character.
    private readonly Dictionary<ulong, ContentRateLimit> sessionRates = [];

    private async Task RunContentSession(System.Net.WebSockets.WebSocket socket, Peer peer, byte[] payload)
    {
        var session = ContentWire.DecodeSession(payload);
        if (session.Revision != HousingWire.ProtocolRevision)
        {
            peer.Queue(ContentWire.Ticket, WireFormat.RelaySender, ContentWire.Encode(new ContentTicket("", "",
                Error: $"This relay requires Haven housing protocol revision {HousingWire.ProtocolRevision}.")));
            return;
        }
        if (AccountRefusal(peer, session.ContentId) is { } refusal)
        {
            peer.Queue(ContentWire.Ticket, WireFormat.RelaySender, ContentWire.Encode(new ContentTicket("", "", Error: refusal)));
            return;
        }
        peer.ContentId = session.ContentId;
        peer.ContentSecret = ContentLocker.IsSecret(session.Secret) ? session.Secret! : "";
        using var life = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
        life.CancelAfter(TimeSpan.FromMinutes(5));
        // A session asks for a few tickets (an upload, a delete); far more than that closes it before the shared lock.
        var frames = new RoomChatRateLimit(20, 2);
        while (socket.State == System.Net.WebSockets.WebSocketState.Open && !life.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            var frame = FrameHeader.ParseUpleg(await Receive(socket, 1024, idle.Token));
            if (!frame.Ok || frame.Kind != ContentWire.TicketRequest) throw new InvalidDataException("Invalid frame.");
            if (!frames.Allow((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency)) throw new InvalidDataException("Rate exceeded.");
            lock (gate) HandleSessionTicket(peer, frame.Payload);
        }
    }

    // Per character, in rooms and sessions alike: a reconnect must not refill the allowance.
    private ContentRateLimit RateFor(ulong contentId)
    {
        if (sessionRates.Count > 10_000) sessionRates.Clear();
        if (!sessionRates.TryGetValue(contentId, out var rate)) sessionRates[contentId] = rate = new();
        return rate;
    }

    private void HandleSessionTicket(Peer peer, ReadOnlySpan<byte> payload)
    {
        var request = ContentWire.DecodeRequest(payload);
        ContentTicket Refuse(string error) => new(request.Kind, request.Hash, Error: error);
        var rate = RateFor(peer.ContentId);
        var now = DateTimeOffset.UtcNow;
        var owner = string.IsNullOrEmpty(ContentKey) || peer.ContentSecret.Length == 0 ? "" : ContentLocker.OwnerKey(ContentKey, peer.ContentId, peer.ContentSecret);
        var ticket = string.IsNullOrEmpty(ContentKey) ? Refuse("Custom content is unavailable on this relay.")
            : owner.Length == 0 ? Refuse("Update Haven to share custom pictures.")
            : !rate.Allow(now) ? Refuse("Too many custom pictures requested. Try again later.")
            : request.Kind switch
            {
                "upload" when request.Size <= 0 || request.Size > ContentLocker.MaxBytes(request.Media) => Refuse(ContentLocker.TooLarge(request.Media)),
                "upload" when request.Agreement < ContentLocker.AgreementVersion => Refuse("Accept the custom content agreement first."),
                "upload" => new ContentTicket(request.Kind, request.Hash, ContentLocker.TicketPath(ContentKey, "put", request.Hash, request.Size, owner, request.Agreement, now, peer.Verified?.ContentId ?? 0, request.Media)),
                // The community gallery closed in 0.38.0: an older Haven asking to share is told so.
                "share" or "unshare" => Refuse(ContentLocker.GalleryClosed),
                // Deleting at once: the session's character is verified by its account (AccountStamp), and the
                // Worker deletes only a picture this owner key uploaded.
                "delete" => new ContentTicket(request.Kind, request.Hash, ContentLocker.TicketPath(ContentKey, "delete", request.Hash, 0, owner, 0, now, media: request.Media)),
                _ => Refuse("Pictures are downloaded in a gathering."),
            };
        peer.Queue(ContentWire.Ticket, WireFormat.RelaySender, ContentWire.Encode(ticket));
    }

    // At most Limit download links per peer in any rolling hour.
    private sealed class ContentRateLimit
    {
        public const int Limit = 100;
        private readonly Queue<DateTimeOffset> issued = new();
        public bool Allow(DateTimeOffset now)
        {
            while (issued.Count > 0 && now - issued.Peek() > TimeSpan.FromHours(1)) issued.Dequeue();
            if (issued.Count >= Limit) return false;
            issued.Enqueue(now);
            return true;
        }
    }
}
