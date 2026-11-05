using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;
using HavenNative;
using static Haven.HavenTokens;

namespace Haven;

/// <summary>
/// Fades the game out when the player enters a home from the launcher, holds a loading screen while the room and its
/// furnishings load (and, for a guest who loads the home's custom content, while it downloads, for a while), then fades
/// back in. Joining a gathering (which waits on the host first) and leaving a home show it from the moment the home
/// changes. It only draws: it never holds up the visit itself, and it always lifts on its own (a failed entry, or after
/// a minute at most). Also draws the download bar under the room's toolbar while custom content downloads.
/// </summary>
internal sealed class LoadingScreen(HousingController housing, NativeEngine engine, Func<uint, LauncherArt> art)
{
    private enum Stage { Idle, Closing, Holding, Opening }
    private const float FadeOut = .35f, FadeIn = .7f, QuickFade = .2f;
    // A home whose scene is drawn waits this long for its custom content, then shows what it has.
    private const double ContentWait = 20, MostHeld = 60, MostLeaving = 30;

    private Stage stage;
    private float alpha;
    private double stageStarted, shownSince = -1;
    private bool leaving;
    private Action? pending;
    private string title = "", eyebrow = "";
    private uint territory;
    private House? lastHome;
    // The download bar: when its downloads last ran and when they finished, for its "ready" moment and fade.
    private double downloadsFinished = -1;
    private float downloadAlpha;
    private int lastCount;

    public bool Covering => alpha > 0;
    /// <summary>Set by the plugin: an NPC mod share's bar (NpcModShare.Bar), shown under the download bar.</summary>
    public Func<DownloadBarModel?>? ShareBar { get; set; }
    private float shareAlpha;

    /// <summary>Fades out, then starts the visit (Enter or Host from the launcher): the fade hides the room swap.</summary>
    public void Enter(House home, Action start)
    {
        if (stage is Stage.Closing) return;
        title = home.Name; eyebrow = "Entering"; territory = home.CustomLayout ? CustomCanvas.TerritoryId : home.TerritoryId;
        leaving = false; pending = start; Go(Stage.Closing);
    }

    public void Draw()
    {
        var now = ImGui.GetTime(); var dt = ImGui.GetIO().DeltaTime;
        var home = housing.ActiveHouse;
        Follow(home, now);
        lastHome = home;
        alpha = stage switch
        {
            Stage.Closing or Stage.Holding => Math.Min(1, alpha + dt / (pending != null ? FadeOut : QuickFade)),
            Stage.Opening => Math.Max(0, alpha - dt / FadeIn),
            _ => 0,
        };
        if (stage == Stage.Closing && alpha >= 1)
        {
            if (pending is { } start) { pending = null; housing.Run(start); }
            Go(Stage.Holding);
        }
        if (stage == Stage.Opening && alpha <= 0) stage = Stage.Idle;

        DrawDownloads(now, dt);
        if (alpha <= 0) return;
        var model = Model(now);
        if (LoadingScreenUi.Draw(model, inputs: stage is Stage.Holding && alpha >= 1)) Go(Stage.Opening);
    }

    // Starts, holds and lifts the screen from what the visit is doing.
    private void Follow(House? home, double now)
    {
        switch (stage)
        {
            case Stage.Idle or Stage.Opening:
                // A home appeared without the launcher's fade (joining a gathering, a host's restart): cover it at once.
                if (lastHome == null && home != null && !housing.SceneShown)
                {
                    title = home.Name; eyebrow = housing.OwnsActiveHouse ? "Entering" : "Visiting"; territory = Territory(home);
                    leaving = false; Go(Stage.Holding);
                }
                // The home went away: cover the trip back to where the player entered.
                else if (lastHome != null && home == null && stage == Stage.Idle)
                {
                    title = lastHome.Name; eyebrow = "Leaving"; territory = Territory(lastHome);
                    leaving = true; Go(Stage.Holding);
                }
                break;
            case Stage.Holding:
                var held = now - stageStarted;
                if (leaving)
                {
                    // Straight into another home: cover that instead.
                    if (home != null) { title = home.Name; eyebrow = "Entering"; territory = Territory(home); leaving = false; Go(Stage.Holding); }
                    else if (held > 1 && !engine.HousingReturnPending || held > MostLeaving) Go(Stage.Opening);
                    break;
                }
                if (home != null) { title = home.Name; territory = Territory(home); }
                if (housing.SceneShown) { if (shownSince < 0) shownSince = now; } else shownSince = -1;
                var shown = shownSince >= 0 && now - shownSince >= .5;
                var content = housing.ContentDownloads;
                // The consent question comes after the room is shown, and nothing downloads before it is answered.
                var waitForContent = content.Active && !housing.CustomConsentPending && now - shownSince < ContentWait;
                if (shown && !waitForContent || home == null && held > .75 || held > MostHeld) Go(Stage.Opening);
                break;
        }
    }

    private LoadingModel Model(double now)
    {
        var picture = art(territory);
        if (leaving) return new(eyebrow, title, "Returning you to where you entered…", null, picture, alpha);
        var home = housing.ActiveHouse;
        string status; float? progress = null;
        if (home == null) status = "Starting…";
        else if (!housing.IsReady)
            status = !housing.IsSharedVisit ? "Loading the room…" : !housing.OwnsActiveHouse ? "Loading the room for this gathering…"
                : engine.HousingRelay.RoomJoinedAcknowledged ? "Loading the room…" : "Opening the gathering…";
        else if (!housing.SceneShown)
        {
            var (ready, count) = housing.FurnishingsReady;
            status = count > 0 ? $"Setting out the furnishings · {ready} of {count}" : "Setting out the furnishings…";
            if (count > 0) progress = (float)ready / count;
        }
        else { status = "Almost there…"; progress = 1; }
        var content = housing.ContentDownloads;
        if (!content.Active || housing.CustomConsentPending) return new(eyebrow, title, status, progress, picture, alpha);
        var canSkip = shownSince >= 0 && now - shownSince > 2 && stage == Stage.Holding;
        return new(eyebrow, title, status, progress, picture, alpha, $"Downloading custom content · {content.Finished} of {content.Count}",
            Detail(content), content.Progress, canSkip);
    }

    // Under the toolbar while a home's custom content downloads; says "ready" for a moment when it is all here.
    private void DrawDownloads(double now, float dt)
    {
        var content = housing.ActiveHouse != null ? housing.ContentDownloads : default;
        var active = content.Active;
        if (active) { downloadsFinished = -1; lastCount = content.Count; }
        else if (lastCount > 0 && downloadsFinished < 0) downloadsFinished = now;
        var done = !active && downloadsFinished >= 0 && now - downloadsFinished < 2.5;
        if (!active && !done) lastCount = 0;
        var target = (active || done) && alpha <= 0 && !CinematicMode.Hiding ? 1f : 0f;
        downloadAlpha = target > downloadAlpha ? Math.Min(target, downloadAlpha + dt / .25f) : Math.Max(target, downloadAlpha - dt / .5f);
        var toolbar = PlacementUi.OwnerBounds;
        var viewport = ImGui.GetMainViewport();
        var top = toolbar.Size.Y > 0 ? new Vector2(toolbar.Position.X + toolbar.Size.X / 2, toolbar.Position.Y + toolbar.Size.Y + Px(Space3))
            : viewport.Pos + new Vector2(viewport.Size.X / 2, Px(18));
        if (housing.MaintenanceBanner != null) top.Y += Px(96);
        // An NPC mod share's bar, under the download bar when both show.
        var share = housing.ActiveHouse != null && alpha <= 0 && !CinematicMode.Hiding ? ShareBar?.Invoke() : null;
        shareAlpha = share != null ? Math.Min(1, shareAlpha + dt / .25f) : Math.Max(0, shareAlpha - dt / .5f);
        if (share is { } bar) LoadingScreenUi.DrawDownloads(bar with { Alpha = shareAlpha }, top + new Vector2(0, downloadAlpha > 0 ? Px(84) : 0), "###Haven.NpcModShare");
        if (downloadAlpha <= 0) return;
        var model = active
            ? new DownloadBarModel("Downloading custom content", Detail(content), $"{content.Finished} of {content.Count}", content.Progress, false, downloadAlpha)
            : new DownloadBarModel("Custom content ready", "Everything in this home is shown as its owner made it.", $"{lastCount} of {lastCount}", 1, true, downloadAlpha);
        LoadingScreenUi.DrawDownloads(model, top);
    }

    // "Furniture and animations · 1.4 of 3.2 MB"
    private static string Detail(ContentDownloadState content)
    {
        var kinds = content.Kinds.Select(k => k switch
        {
            ContentKind.Picture => "pictures", ContentKind.Song => "songs", ContentKind.Furniture => "furniture", _ => "animations",
        }).ToArray();
        var what = kinds.Length switch
        {
            0 => "Custom content", 1 => kinds[0], _ => string.Join(", ", kinds[..^1]) + " and " + kinds[^1],
        };
        what = char.ToUpperInvariant(what[0]) + what[1..];
        return content.Total > 0 ? $"{what} · {Size(content.Received)} of {Size(content.Total)}" : what + " · waiting for the server…";
    }
    private static string Size(long bytes) => bytes >= 1 << 20 ? FormattableString.Invariant($"{bytes / 1048576.0:0.0} MB") : FormattableString.Invariant($"{Math.Max(1, bytes / 1024)} KB");

    private static uint Territory(House home) => home.CustomLayout ? CustomCanvas.TerritoryId : home.TerritoryId;
    private void Go(Stage next)
    {
        stage = next; stageStarted = ImGui.GetTime();
        if (next == Stage.Holding) shownSince = -1;
    }
}
