using Haven.Core;
using HavenNative.Wire;

namespace Haven.Relay;

// Operator notices (NoticeWire): an announcement, an update, or maintenance with a countdown, sent to everyone
// connected and to anyone who becomes ready while it stands. Posted through /internal/notice (the edge's admin route).
internal sealed partial class RoomHub
{
    private RelayNotice? notice;
    private long noticeEndsAt, noticeExpires;
    // A notice without a countdown stands this long; one with a countdown, this long after it ends.
    private const long NoticeLifeMs = 2 * 60 * 60 * 1000, NoticeAfterMs = 15 * 60 * 1000;

    /// <summary>The standing notice with its countdown measured now, or null.</summary>
    public RelayNotice? CurrentNotice { get { lock (gate) return LiveNotice(); } }

    private RelayNotice? LiveNotice()
    {
        if (notice == null || NowMs >= noticeExpires) { notice = null; return null; }
        return notice with { EndsInMs = noticeEndsAt > 0 ? Math.Max(0, noticeEndsAt - NowMs) : 0 };
    }

    /// <summary>Posts a notice (or, with Kind "clear", withdraws the standing one) and sends it to everyone connected.</summary>
    public bool PostNotice(RelayNotice posted)
    {
        if (!NoticeWire.Valid(posted)) return false;
        lock (gate)
        {
            if (posted.Kind == "clear") notice = null;
            else
            {
                notice = posted;
                noticeEndsAt = posted.EndsInMs > 0 ? NowMs + posted.EndsInMs : 0;
                noticeExpires = noticeEndsAt > 0 ? noticeEndsAt + NoticeAfterMs : NowMs + NoticeLifeMs;
            }
            var payload = NoticeWire.Encode(posted.Kind == "clear" ? posted : LiveNotice()!);
            var sent = 0;
            foreach (var room in rooms.Values)
                foreach (var peer in room.Peers.Where(p => p.Online)) { peer.Queue(HousingWire.Notice, WireFormat.RelaySender, payload); sent++; }
            log.LogInformation("relay_notice boot={Boot} kind={Kind} id={Id} sent={Sent}", BootId, posted.Kind, posted.Id, sent);
        }
        return true;
    }

    // A peer that became ready gets the standing notice, if any (called under the gate).
    private void SendNotice(Peer peer)
    {
        if (LiveNotice() is { } current) peer.Queue(HousingWire.Notice, WireFormat.RelaySender, NoticeWire.Encode(current));
    }
}
