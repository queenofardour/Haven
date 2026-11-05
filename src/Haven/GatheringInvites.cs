using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Haven.Core;
using HavenNative;
using static Haven.HavenTokens;

namespace Haven;

internal sealed record InviteSettings(bool AutoInvite = true, bool InviteOnly = false);

/// <summary>
/// Gathering invitations. A host's Haven invites the friends standing near it through the
/// Worker (never the game's servers, never the relay container); a friend's Haven asks the Worker every 20 s while a
/// friend is near and no visit is on, and shows "… is hosting a gathering" with Join and Decline. Join runs the same
/// flow as typing the visit code. The host can stop automatic invitations, invite one friend at a time, and make the
/// gathering invite-only (the relay then refuses anyone not invited, whatever code they have).
/// </summary>
internal sealed class GatheringInvites : IDisposable
{
    private readonly HousingController housing;
    private readonly NativeEngine engine;
    private readonly IPluginLog log;
    private readonly string path;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly InviteHostPlan plan = new();
    private readonly InviteInbox inbox = new();
    // Results of requests made off the framework thread, applied on it.
    private readonly ConcurrentQueue<Action> results = new();
    private InviteSettings settings = new();
    private IReadOnlyList<(ulong ContentId, string Name)> nearby = [];
    private long nextScan, nextList;
    private bool polling, sending;
    // The gathering being hosted: who hosts it (for the withdraw after it ends), its code and home.
    private (ulong Host, string Name, string Code, string Home)? hosted;
    private GuestList? listSent, listKept;

    public GatheringInvites(HousingController housing, NativeEngine engine, string configDirectory, IPluginLog log)
    {
        this.housing = housing; this.engine = engine; this.log = log;
        path = Path.Combine(configDirectory, "invites.json");
        try { if (File.Exists(path)) settings = JsonSerializer.Deserialize<InviteSettings>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { log.Warning(ex, "[Haven] [INVITES] The invitation settings could not be read."); }
        engine.HousingRelay.OnGuestList += list => results.Enqueue(() => listKept = list);
    }

    public bool AutoInvite => settings.AutoInvite;
    public bool InviteOnly => settings.InviteOnly;
    /// <summary>The relay answered with the invite-only list it keeps (an older relay never answers).</summary>
    public bool LockKept => listKept is { InviteOnly: true };
    /// <summary>The invitation to ask about now, or null.</summary>
    public GatheringInvite? Asking { get; private set; }
    public bool Hosting => hosted != null;

    /// <summary>Friends near the host, not already in the gathering, and whether each has an invitation out.</summary>
    public IReadOnlyList<InviteFriend> Friends
    {
        get
        {
            var members = Members();
            return [.. nearby.Where(f => !members.Contains(f.ContentId)).Select(f => new InviteFriend(f.ContentId, f.Name, plan.IsInvited(f.ContentId))).OrderBy(f => f.Name)];
        }
    }

    public void SetAutoInvite(bool on)
    {
        if (on == settings.AutoInvite) return;
        Save(settings with { AutoInvite = on });
        // Off: invitations sent automatically are taken back (chosen ones stay).
        if (!on && hosted is { } gathering && plan.WithdrawAutomatic() is { Length: > 0 } back) Withdraw(gathering.Host, gathering.Name, back);
    }
    public void SetInviteOnly(bool on) { if (on != settings.InviteOnly) { Save(settings with { InviteOnly = on }); nextList = 0; } }
    public void Invite(ulong friend) { plan.Choose(friend); nextList = 0; }
    public void Uninvite(ulong friend)
    {
        if (plan.Withdraw(friend) && hosted is { } gathering) Withdraw(gathering.Host, gathering.Name, [friend]);
        nextList = 0;
    }

    public void Join(GatheringInvite invite)
    {
        inbox.Answer(invite); Asking = null;
        // The same as typing the code under Join a friend (LauncherWindow), sign-in and entry checks included.
        housing.Run(() => { engine.SetHousingVisitCodes(engine.HousingHostCode, invite.Code); housing.VisitCode = invite.Code; housing.Join(); });
    }
    public void Decline(GatheringInvite invite) { inbox.Answer(invite); Asking = null; }

    private HashSet<ulong> Members() => [.. engine.HousingRelay.HousingMembers.Values.Select(m => m.ContentId)];

    /// <summary>Each frame, on the framework thread.</summary>
    public void Update()
    {
        while (results.TryDequeue(out var apply)) apply();
        var now = Environment.TickCount64;
        if (now >= nextScan) { nextScan = now + 1000; nearby = engine.HousingNearbyFriends(); }
        var origin = AccountSignIn.Origin(engine.HousingRelayUrl);
        var relay = engine.HousingRelay;
        var hostingNow = origin != null && engine.HousingSessionActive && !relay.SoloMode && relay.IsConnected && relay.HousingSceneReady
            && housing.OwnsActiveHouse && housing.IsSharedVisit && housing.VisitorHouse == null;
        if (hostingNow && hosted == null)
        {
            plan.Reset(); listSent = listKept = null; nextList = 0;
            hosted = (engine.HousingCharacterId, engine.HousingCharacterName, housing.VisitCode, housing.ActiveHouse?.Name ?? "");
            log.Information("[Haven] [INVITES] hosting: inviting nearby friends {Mode}.", settings.AutoInvite ? "automatically" : "only when chosen");
        }
        else if (!hostingNow && hosted is { } ended && !engine.HousingSessionActive)
        {
            // The gathering ended: its invitations are withdrawn (they'd expire within 30 minutes anyway).
            hosted = null;
            if (plan.Invited.Count > 0) Withdraw(ended.Host, ended.Name, null);
            plan.Reset();
        }
        if (hostingNow && hosted is { } gathering) Host(origin!, gathering, now);
        UpdateInbox(origin, now);
    }

    private void Host(string origin, (ulong Host, string Name, string Code, string Home) gathering, long now)
    {
        var members = Members();
        var due = plan.Due(nearby.Select(f => f.ContentId).Where(id => !members.Contains(id)), settings.AutoInvite, now);
        if (due.Length > 0 && !sending)
        {
            sending = true;
            var body = new { code = gathering.Code, home = gathering.Home, gathering = plan.Gathering, invitees = due.Select(Text).ToArray() };
            var identity = plan.Gathering;
            _ = Request(HttpMethod.Post, origin + "/invites/send", gathering.Host, gathering.Name, body).ContinueWith(task => results.Enqueue(() =>
            {
                sending = false;
                if (identity != plan.Gathering) return;
                var (status, error) = task.Result;
                if (status == 200) { plan.Sent(due, Environment.TickCount64); log.Information("[Haven] [INVITES] invited {Count} nearby friend(s).", due.Length); }
                else
                {
                    plan.Failed(Environment.TickCount64, error == "slow" ? 10_000 : error is "busy" or "limit" || status is 401 or 403 or 404 ? 600_000 : 60_000);
                    log.Warning("[Haven] [INVITES] the invitations weren't sent ({Status} {Error}).", status, error);
                }
            }));
        }
        // The relay's lock: sent when it changes and every 20 s while on (the relay keeps it in memory only).
        if (settings.InviteOnly || listSent is { InviteOnly: true })
        {
            var list = new GuestList(settings.InviteOnly, [.. plan.Allowed.Concat(members).Where(id => id != gathering.Host).Distinct().Take(GuestList.MaxInvited)]);
            var changed = listSent == null || listSent.InviteOnly != list.InviteOnly || !listSent.Invited.Order().SequenceEqual(list.Invited.Order());
            if (changed || now >= nextList)
            {
                listSent = list; nextList = now + 20_000;
                _ = engine.HousingRelay.SendGuestList(list);
            }
        }
    }

    private void UpdateInbox(string? origin, long now)
    {
        var inVisit = engine.HousingSessionActive || engine.HousingReturnPending;
        var blocked = engine.HousingEntryBlocked != null;
        var friends = nearby.Select(f => f.ContentId).ToHashSet();
        Asking = inVisit || blocked ? null : inbox.Showing(friends, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (origin == null || polling || !inbox.ShouldPoll(housing.Account.SignedIn, inVisit, friends.Count > 0, blocked, now)) return;
        inbox.Polling(now);
        polling = true;
        var id = engine.HousingCharacterId;
        _ = Poll(origin + "/invites", id, engine.HousingCharacterName).ContinueWith(task => results.Enqueue(() =>
        {
            polling = false;
            var (status, error, invites) = task.Result;
            if (status == 200) inbox.Polled(invites);
            else if (status != 429 || error != "slow") inbox.Failed(Environment.TickCount64, busy: status is 401 or 403 or 404 || error == "busy");
        }));
    }

    private async Task<(int Status, string Error, GatheringInvite[] Invites)> Poll(string url, ulong id, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!housing.Account.Apply(request, id, name)) return (401, "sign_in", []);
        try
        {
            using var answer = await http.SendAsync(request);
            var text = await answer.Content.ReadAsStringAsync();
            if (!answer.IsSuccessStatusCode) return ((int)answer.StatusCode, ErrorOf(text), []);
            return (200, "", GatheringInvite.Parse(text));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (0, "", []); }
    }

    private async Task<(int Status, string Error)> Request(HttpMethod method, string url, ulong id, string name, object body)
    {
        // A string body carries its Content-Length, which the Worker requires.
        using var request = new HttpRequestMessage(method, url) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
        if (!housing.Account.Apply(request, id, name)) return (401, "sign_in");
        try
        {
            using var answer = await http.SendAsync(request);
            return answer.IsSuccessStatusCode ? (200, "") : ((int)answer.StatusCode, ErrorOf(await answer.Content.ReadAsStringAsync()));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (0, ""); }
    }

    // Takes invitations back (null: all of this host's). Best effort: what isn't withdrawn expires within 30 minutes.
    private void Withdraw(ulong host, string name, ulong[]? friends)
    {
        if (AccountSignIn.Origin(engine.HousingRelayUrl) is not { } origin || host == 0) return;
        _ = Request(HttpMethod.Post, origin + "/invites/withdraw", host, name, friends == null ? new { } : new { invitees = friends.Select(Text).ToArray() });
    }

    private static string ErrorOf(string text)
    {
        try { using var json = JsonDocument.Parse(text); return json.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : ""; }
        catch (JsonException) { return ""; }
    }
    private static string Text(ulong id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Save(InviteSettings next)
    {
        settings = next;
        try { var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(next)); File.Move(temporary, path, overwrite: true); }
        catch (IOException ex) { log.Warning(ex, "[Haven] [INVITES] The invitation settings could not be saved."); }
    }

    /// <summary>The invitation prompt: "<host> is hosting a gathering. Would you like to join?"</summary>
    public void Draw()
    {
        if (Asking is not { } invite) return;
        var host = nearby.FirstOrDefault(f => f.ContentId == invite.Host).Name is { Length: > 0 } friend ? friend : invite.Name;
        HavenKit.PushTheme();
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2, viewport.Size.Y * .22f), ImGuiCond.Always, new Vector2(.5f, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space5)); ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space2, Space3));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow)); ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .97f });
        if (ImGui.Begin("Gathering invitation###Haven.Invitation", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            HavenKit.KeepOnTop();
            using (HavenFonts.Use(HavenText.Title)) ImGui.TextUnformatted("Gathering invitation");
            HavenKit.Caption($"{host} is hosting a gathering{(invite.Home.Length > 0 ? " in " + invite.Home : "")}. Would you like to join?", Px(380));
            HavenKit.Caption("If you decline, you can still join later with their visit code.", Px(380));
            ImGui.Dummy(Px(0, Space1));
            HavenKit.AlignRight(HavenKit.ButtonWidth("Decline") + HavenKit.ButtonWidth("Join") + Px(Space2));
            if (HavenKit.Button("Decline##invite", ButtonKind.Ghost)) Decline(invite);
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Join##invite", ButtonKind.Primary)) Join(invite);
        }
        ImGui.End();
        ImGui.PopStyleColor(); ImGui.PopStyleVar(3);
        HavenKit.PopTheme();
    }

    public void Dispose()
    {
        // Unloaded while hosting: the invitations are withdrawn if the request gets out in time, else they expire.
        // The client is left to the collector then, so the request isn't cut off.
        if (hosted is { } gathering && plan.Invited.Count > 0) Withdraw(gathering.Host, gathering.Name, null);
        else http.Dispose();
    }
}
