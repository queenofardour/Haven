using Haven.Core;

namespace Haven;

// Notices from the relay's operator (NoticeWire): an announcement, an update, or relay maintenance with a countdown.
// Each shows once as a notification; maintenance also shows a countdown under the room's toolbar while in a home.
internal sealed partial class HousingController
{
    /// <summary>A notice arrived (once per notice): its title and text, and whether it warns (maintenance).</summary>
    public event Action<string, string, bool>? Noticed;
    private RelayNotice? notice;
    private long noticeEnds;
    private readonly HashSet<string> noticesShown = [];
    private static readonly Version ClientVersion = typeof(HousingController).Assembly.GetName().Version is { } v ? new(v.Major, v.Minor, Math.Max(0, v.Build)) : new(0, 0, 0);

    private void InitialiseNotices() => engine.HousingRelay.OnNotice += OnNotice;
    private void DisposeNotices() => engine.HousingRelay.OnNotice -= OnNotice;

    private void OnNotice(RelayNotice received) => Run(() =>
    {
        if (received.Kind == "clear") { notice = null; log.Information("[NOTICE] withdrawn."); return; }
        if (!NoticeWire.Concerns(received, ClientVersion)) return;
        notice = received;
        noticeEnds = received.EndsInMs > 0 ? Environment.TickCount64 + received.EndsInMs : 0;
        log.Information("[NOTICE] {Kind} {Id}: {Text}", received.Kind, received.Id, received.Text);
        if (noticesShown.Add(received.Id)) Noticed?.Invoke(NoticeTitle(received, noticeEnds), received.Text, received.Kind == "maintenance");
    });

    private static string NoticeTitle(RelayNotice notice, long ends) => notice.Kind switch
    {
        "maintenance" => ends > 0 ? $"Haven relay maintenance in {NoticeWire.Countdown(TimeSpan.FromMilliseconds(ends - Environment.TickCount64))}" : "Haven relay maintenance",
        "update" => notice.Version is { } version ? $"Haven {version} is available" : "A Haven update is available",
        _ => "Haven",
    };

    /// <summary>
    /// The maintenance countdown to show in a home: its title (counting down) and text, or null. It stays a few minutes
    /// after the moment, while the relay restarts, then goes.
    /// </summary>
    public (string Title, string Text)? MaintenanceBanner
    {
        get
        {
            if (notice is not { Kind: "maintenance" } current || active == null) return null;
            if (noticeEnds > 0 && Environment.TickCount64 - noticeEnds > 5 * 60_000) { notice = null; return null; }
            var title = noticeEnds == 0 ? "Haven relay maintenance"
                : noticeEnds > Environment.TickCount64 ? $"Relay maintenance in {NoticeWire.Countdown(TimeSpan.FromMilliseconds(noticeEnds - Environment.TickCount64))}"
                : "Relay maintenance now: your gathering reconnects by itself";
            return (title, current.Text);
        }
    }
}
