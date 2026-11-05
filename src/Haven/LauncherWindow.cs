using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Haven.Core;
using HavenNative;
using Lumina.Excel.Sheets;
using static Haven.HavenTokens;

namespace Haven;

// The Haven launcher window: adapts the controller's homes, visit state and the
// game's district art to LauncherUi, and carries out the commands it returns.
// Decorating, visit settings and files stay in the studio window (HousingWindow)
// until later redesign phases replace them.
internal sealed class LauncherWindow : Window
{
    private readonly HousingController housing;
    private readonly NativeEngine engine;
    private readonly ITextureProvider textures;
    private readonly SettingsWindow settings;
    private readonly HomeLayoutOption[] layouts;
    private readonly LauncherShell[] shells;
    private readonly Dictionary<uint, string> shellNames, artPaths;
    private readonly LauncherState state = new();
    private IReadOnlyList<House>? cachedHomes;
    private readonly Dictionary<Guid, string> details = [];
    private bool closeWhenInside;
    // Starts editing in the room (FurnishingEditor.StartDecorating).
    public System.Action? Decorate;
    // Fades the game out before a visit starts (LoadingScreen.Enter), then starts it.
    public System.Action<House, System.Action>? Curtain;
    private void Begin(House home, System.Action start)
    {
        if (Curtain != null) Curtain(home, start); else housing.Run(start);
    }

    public LauncherWindow(HousingController housing, NativeEngine engine, IDataManager data, ITextureProvider textures, SettingsWindow settings)
        : base("Haven###Haven.Launcher", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.housing = housing; this.engine = engine; this.textures = textures; this.settings = settings;
        Size = new Vector2(1040, 700); SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new() { MinimumSize = new Vector2(720, 520), MaximumSize = new Vector2(float.MaxValue) };
        layouts = Layouts(data, engine);
        var sheet = data.GetExcelSheet<TerritoryType>();
        shellNames = sheet.Where(x => engine.IsHavenTerritory(x.RowId)).ToDictionary(x => x.RowId, x => x.PlaceName.Value.Name.ToString().Replace(" - ", " · "));
        // District loading art: the regular size when the client has it, else the high-resolution one.
        artPaths = [];
        foreach (var territory in sheet.Where(x => engine.IsHavenTerritory(x.RowId)))
        {
            var file = territory.LoadingImage.ValueNullable?.FileName.ToString();
            if (string.IsNullOrEmpty(file)) continue;
            var path = $"ui/loadingimage/{file}.tex";
            if (!data.FileExists(path)) path = $"ui/loadingimage/{file}_hr1.tex";
            if (data.FileExists(path)) artPaths[territory.RowId] = path;
        }
        shells = layouts.Select((x, i) =>
        {
            // A shell whose zone this install lacks (an Endwalker or Dawntrail inn on a free trial) is shown, not offered.
            var needs = x.Size == HomeLayoutPickerUi.Custom ? null : engine.HousingTerritoryNeeds(x.Id);
            return new LauncherShell(i, x.Size == HomeLayoutPickerUi.Custom ? "Blank canvas" : x.Subtype,
                x.Size == HomeLayoutPickerUi.Custom ? "Protected starter platform" : needs is { } n ? $"Needs {Haven.Core.GameDataPaths.ExpansionName(n)}" : x.Name.Replace(" - ", " · "),
                x.Size, default, needs is { } m ? Haven.Core.GameDataPaths.Missing(x.Name, m) : null);
        }).ToArray();
    }

    // Saved-home shells offered for new homes: the named presets plus the custom canvas.
    internal static HomeLayoutOption[] Layouts(IDataManager data, NativeEngine engine)
    {
        var housingTerritories = data.GetExcelSheet<TerritoryType>().Where(x => engine.IsHavenTerritory(x.RowId))
            .Select(x => (Id: x.RowId, Name: x.PlaceName.Value.Name.ToString(), Code: x.Name.ToString(), City: x.PlaceNameZone.ValueNullable?.Name.ToString() ?? "")).ToArray();
        // Outdoor housing territory names supply localized district labels.
        var districtNames = data.GetExcelSheet<TerritoryType>().Where(x => x.TerritoryIntendedUse.RowId == 13
            && x.Name.ToString().EndsWith("h1", StringComparison.Ordinal))
            .ToDictionary(x => x.Name.ToString()[..^2], x => x.PlaceName.Value.Name.ToString());
        return housingTerritories.Where(x => !string.IsNullOrWhiteSpace(x.Name) && HomeLayoutPickerUi.SizeForCode(x.Code) >= 0)
            .Select(x => new HomeLayoutOption(x.Id, x.Name,
                // An inn's card is titled by its city; a house's by its district.
                engine.IsInnTerritory(x.Id) ? string.IsNullOrWhiteSpace(x.City) ? x.Name : x.City : districtNames.GetValueOrDefault(x.Code[..2], x.Name),
                x.Code[..2], HomeLayoutPickerUi.SizeForCode(x.Code)))
            .Append(new HomeLayoutOption(CustomCanvas.TerritoryId, "Blank canvas · protected starter platform", "Blank canvas", "custom", HomeLayoutPickerUi.Custom))
            .OrderBy(x => x.Size).ThenBy(x => x.Subtype).ToArray();
    }

    // Opens on the homes view, or on New home when there are none.
    public void Show(bool newHome = false)
    {
        state.Creating = newHome || housing.Houses.Count == 0;
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw()
    {
        HavenKit.PushTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
    }
    public override void PostDraw() { ImGui.PopStyleVar(2); HavenKit.PopTheme(); }

    public override void Draw()
    {
        // Entering, hosting or joining closes the launcher once the visit begins,
        // so the room is in view. Failures leave it open with the reason below.
        if (closeWhenInside && (housing.ActiveHouse != null || engine.HousingSessionActive)) { closeWhenInside = false; IsOpen = false; return; }
        var command = LauncherUi.Draw(state, Model());
        if (command.Action != LauncherAction.None) Execute(command);
    }

    private LauncherModel Model()
    {
        if (!ReferenceEquals(cachedHomes, housing.Houses))
        {
            cachedHomes = housing.Houses; details.Clear();
            foreach (var home in cachedHomes)
                details[home.Id] = $"{home.Furnishings.Length + home.Stage.GetProperty("Objects").EnumerateObject().Count()} furnishings · saved {Ago(home.SavedAt)}";
        }
        var active = housing.ActiveHouse; var relay = engine.HousingRelay;
        var hosting = active != null && relay.IsHost && !relay.SoloMode;
        var homes = cachedHomes.Select(home => new LauncherHome(home.Id, home.Name,
            home.CustomLayout ? "Custom · Blank canvas" : shellNames.GetValueOrDefault(home.TerritoryId, "Private housing interior"),
            details[home.Id], Art(home.CustomLayout ? CustomCanvas.TerritoryId : home.TerritoryId),
            active?.Id != home.Id ? "" : hosting ? Guests(relay.PeerConnections.Count) : "You're here",
            housing.CanDeleteHome(home.Id)) { Unsorted = housing.IsUnsorted(home.Id) }).ToArray();
        // Art resolves each frame: Dalamud streams game textures in and releases unused ones.
        var shellArt = state.Creating ? shells.Select(x => x with { Art = Art(layouts[x.Index].Id) }).ToArray() : shells;
        return new(homes, shellArt, Visit(), housing.CanStartVisit, engine.HousingRelay.IsRecovering ? relay.RecoveryStatus : housing.Status)
            { CopySource = housing.RealInteriorName, Layouts = state.Creating ? Layouts() : [],
              Account = new(housing.Account.SignedIn, housing.Account.SigningIn, housing.Account.Problem, housing.Account.Code) };
    }

    private LauncherVisit? Visit()
    {
        var relay = engine.HousingRelay; var active = housing.ActiveHouse;
        if (!housing.CanStopVisit && housing.VisitorHouse == null) return null;
        var hosting = relay.IsHost && !relay.SoloMode;
        var leave = housing.EndingVisit ? "Ending…" : hosting ? "Stop hosting" : relay.IsConnecting && active == null ? "Cancel" : "Leave home";
        string title, detail;
        if (active == null) { title = relay.IsConnecting ? "Connecting…" : "Starting your visit"; detail = "Meet your friends in the same zone, within sight."; }
        else if (housing.VisitorHouse is {} visiting) { title = "Visiting " + visiting.Name; detail = relay.HousingSceneReady ? "Shared home" : "Preparing the shared home…"; }
        else if (hosting) { title = "Hosting " + active.Name; detail = Guests(relay.PeerConnections.Count) + " · share your visit code so friends can join"; }
        else { title = "Inside " + active.Name; detail = housing.IsReady ? "Private visit" : "Getting your home ready…"; }
        if (relay.IsRecovering) detail = relay.RecoveryStatus;
        return new(title, detail, hosting, hosting ? housing.VisitCode : "", housing.CanFurnish,
            active is { CustomLayout: true } && housing.OwnsActiveHouse && housing.IsReady, housing.VisitorHouse != null,
            housing.CanRetryEntry, housing.EndingVisit, leave);
    }

    private void Execute(LauncherCommand command)
    {
        var home = housing.Houses.FirstOrDefault(x => x.Id == command.Home);
        switch (command.Action)
        {
            case LauncherAction.Enter when home != null:
                if (housing.ActiveHouse?.Id == home.Id) { Decorate?.Invoke(); IsOpen = false; break; }
                closeWhenInside = true; Begin(home, () => housing.VisitSolo(home)); break;
            // Without a Haven account the hosted relay refuses the connection: ask for the sign-in first.
            case LauncherAction.Host or LauncherAction.Join when NeedsSignIn:
                state.AfterSignIn = command; state.OpenSignIn = true; break;
            case LauncherAction.SignIn:
                housing.Account.Cancel();
                housing.Account.SignIn(engine.HousingRelayUrl, OpenLink); break;
            case LauncherAction.CancelSignIn: housing.Account.Cancel(); break;
            case LauncherAction.Host when home != null:
                closeWhenInside = true;
                Begin(home, () => { engine.SetHousingVisitCodes(engine.HousingHostCode, engine.HousingGuestCode); housing.VisitCode = engine.HousingHostCode; housing.Host(home); });
                break;
            case LauncherAction.Join:
                closeWhenInside = true;
                housing.Run(() => { engine.SetHousingVisitCodes(engine.HousingHostCode, command.Text); housing.VisitCode = command.Text; housing.Join(); });
                break;
            case LauncherAction.Create:
                var layout = layouts[command.Shell];
                housing.Run(() => housing.Create(command.Text, layout.Id, layout.Size == HomeLayoutPickerUi.Custom)); state.Creating = false; break;
            case LauncherAction.SaveLayout:
                housing.Run(() => settings.Notify?.Invoke(housing.SaveRealHouseLayout(command.Text).Summary("Saved this layout"))); break;
            case LauncherAction.RenameLayout: housing.Run(() => housing.RenameLayout(command.Home, command.Text)); break;
            case LauncherAction.KeepHome: housing.Run(() => housing.KeepForThisCharacter(command.Home)); break;
            case LauncherAction.KeepAllHomes: housing.Run(housing.KeepAllForThisCharacter); break;
            // A MakePlace file joins the saved layouts and is chosen as the new home's start.
            case LauncherAction.ImportLayout:
                var fallback = command.Shell >= 0 && command.Shell < layouts.Length && layouts[command.Shell].Size != HomeLayoutPickerUi.Custom ? layouts[command.Shell].Id : (uint?)null;
                settings.ImportMakePlace(layout => HomeLayouts.Shell(layout) is {} shell
                    ? layouts.FirstOrDefault(x => x.District == shell.District && x.Size == shell.Size)?.Id ?? fallback : fallback, id => state.Layout = id);
                break;
            // An untouched name takes the layout's.
            case LauncherAction.CreateFromLayout:
                var layoutName = command.Text is "" or "My haven" ? housing.SavedLayouts.FirstOrDefault(x => x.Id == command.Home)?.Name ?? "My haven" : command.Text;
                housing.Run(() => housing.CreateFromLayout(command.Home, layoutName)); state.Creating = false; state.Layout = Guid.Empty; break;
            case LauncherAction.DeleteLayout: housing.Run(() => housing.DeleteLayout(command.Home)); state.Layout = Guid.Empty; break;
            case LauncherAction.Rename when home != null: housing.Run(() => housing.RenameHome(home.Id, command.Text)); break;
            case LauncherAction.Duplicate when home != null: housing.Run(() => housing.DuplicateHome(home.Id)); break;
            case LauncherAction.Delete when home != null: housing.Run(() => housing.DeleteHome(home.Id)); break;
            case LauncherAction.Export when home != null: settings.Export(home.Id); break;
            case LauncherAction.Settings: settings.Show(); break;
            case LauncherAction.Decorate: Decorate?.Invoke(); IsOpen = false; break;
            case LauncherAction.Leave: housing.Run(housing.StopVisit); break;
            case LauncherAction.ReturnToPlatform: housing.Run(housing.ReturnToPlatform); break;
            case LauncherAction.SaveCopy: housing.Run(housing.SaveVisitorCopy); break;
            case LauncherAction.RetryEntry: housing.Run(housing.RetryHomeEntry); break;
            case LauncherAction.CopyCode: ImGui.SetClipboardText(housing.VisitCode); break;
            case LauncherAction.Close: IsOpen = false; break;
        }
    }

    private bool NeedsSignIn => !housing.Account.SignedIn && AccountSignIn.Required(engine.HousingRelayUrl);

    private void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { housing.Run(() => throw new InvalidOperationException("The browser could not be opened: " + ex.Message)); }
    }

    private LauncherLayout[] Layouts() => housing.SavedLayouts.Select(x => new LauncherLayout(x.Id, x.Name, housing.ShellName(x.TerritoryId),
        $"{x.Furnishings.Length} furnishing{(x.Furnishings.Length == 1 ? "" : "s")} · {x.Source}")).ToArray();

    public LauncherArt Art(uint territory)
    {
        if (artPaths.TryGetValue(territory, out var path) && textures.GetFromGame(path).TryGetWrap(out var wrap, out _))
            return new(wrap.Handle, true, IconWell);
        return new(default, false, IconWell);
    }

    private static string Guests(int count) => count == 1 ? "1 guest" : $"{count} guests";
    private static string Ago(DateTimeOffset when)
    {
        var days = (DateTime.Today - when.ToLocalTime().Date).Days;
        return days <= 0 ? "today" : days == 1 ? "yesterday" : days < 7 ? $"{days} days ago" : when.ToLocalTime().ToString("d MMM yyyy");
    }
}
