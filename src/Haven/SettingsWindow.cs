using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Haven.Core;
using HavenNative;
using Stagehand.Api;
using static Haven.HavenTokens;

namespace Haven;

// Haven's Settings window: adapts the controller and engine to SettingsUi, and owns the
// file dialog used for import and export (also from the launcher's ⋯ menu).
internal sealed class SettingsWindow : Window, IDisposable
{
    private readonly HousingController housing;
    private readonly NativeEngine engine;
    private readonly ICommandManager commands;
    private readonly ITextureProvider textures;
    // Settings → Custom content: the owner's pictures, rebuilt every 2 seconds rather than every frame, and their thumbnails.
    private SettingsPicture[] pictures = [];
    private DateTime picturesAt;
    private readonly Dictionary<string, Task<IDalamudTextureWrap?>> thumbnails = [];
    private readonly FileDialogManager files = new();
    private readonly SettingsState state = new();
    // The footer shows what the player's own actions here reported (HousingController.StatusVersion past the last
    // one), not whatever Haven last said elsewhere: a home deleted in the launcher left its message under About.
    private int statusSince = int.MaxValue;
    private static readonly string Version = typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "alpha";
    // Reports a completed file action outside the footer status (a Dalamud notification).
    public System.Action<string>? Notify;
    /// <summary>Opens "What's new" (the latest versions' notes).</summary>
    public System.Action? ShowChangelog;
    /// <summary>Set by the plugin: the NPC mods this PC keeps, and the names of the NPCs in Your NPCs (every character's), by id.</summary>
    public Func<IReadOnlyList<NpcModSet>>? KeptNpcMods;
    public Func<IReadOnlyDictionary<Guid, string>>? RosterNames;
    // Settings → Custom content: what's kept on this PC and the NPC mods, rebuilt every 3 seconds (they read the disk).
    private SettingsStorage? storage;
    private SettingsNpcMods[] npcMods = [];
    private DateTime storageAt;

    public SettingsWindow(HousingController housing, NativeEngine engine, ICommandManager commands, IDataManager data, ITextureProvider textures)
        : base("Haven settings###Haven.Settings", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.housing = housing; this.engine = engine; this.commands = commands; this.textures = textures;
        Size = new Vector2(640, 620); SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new() { MinimumSize = new Vector2(520, 480), MaximumSize = new Vector2(float.MaxValue) };
    }

    private SettingsPicture[] Pictures()
    {
        // What the server holds is checked, not remembered: the operator may have cleared it.
        housing.Run(housing.CheckPicturesOnServer);
        if (DateTime.UtcNow - picturesAt < TimeSpan.FromSeconds(2)) return pictures;
        picturesAt = DateTime.UtcNow;
        SettingsPicture Row(string hash, PictureDetails d, string title) => new(hash, d.Size, d.Height > 0 ? d.Width / (float)d.Height : 1, title,
            $"{ContentSizes.Megabytes(d.Size)} · {(d.FromCommunity ? "from the former community gallery" : d.LastStored != null ? "on Haven's server" : "not on Haven's server; uploaded when you place it")}", d.Homes);
        var mine = housing.MyPictures().Select(h => (Hash: h, Details: housing.Details(h))).ToArray();
        return pictures = mine.Select(p => Row(p.Hash, p.Details, p.Details.Width > 0 ? $"{p.Details.Width} × {p.Details.Height}" : "Picture")).ToArray();
    }

    private void Storage()
    {
        if (DateTime.UtcNow - storageAt < TimeSpan.FromSeconds(3)) return;
        storageAt = DateTime.UtcNow;
        var downloads = housing.Downloads();
        storage = new(downloads.Sum(d => d.Bytes), downloads.Count, housing.DownloadLimit, housing.FramesSize(), housing.ActiveHouse != null, housing.ClearingDownloads);
        var homes = housing.NpcHomes();
        var roster = RosterNames?.Invoke() ?? new Dictionary<Guid, string>();
        npcMods = [.. (KeptNpcMods?.Invoke() ?? []).Select(set =>
        {
            var inHomes = homes.TryGetValue(set.Npc, out var home) ? home : (Name: "", Homes: Array.Empty<string>());
            var saved = roster.TryGetValue(set.Npc, out var rosterName);
            var name = inHomes.Homes.Length > 0 ? inHomes.Name : saved ? rosterName! : set.From != null ? "A friend's NPC" : "An NPC no longer in a home";
            var bytes = set.Files.DistinctBy(f => f.Hash).Sum(f => f.Size);
            var mods = set.Mods is { } count ? $" · {count} mod{(count == 1 ? "" : "s")}" : "";
            var detail = $"{set.Files.Length} file{(set.Files.Length == 1 ? "" : "s")} · {ContentSizes.Megabytes(bytes)}{mods} · kept {set.Shared.ToLocalTime():d MMM yyyy}";
            var where = inHomes.Homes.Length > 0 ? "In " + string.Join(", ", inHomes.Homes) : saved ? "Saved in Your NPCs"
                : set.From != null ? "Sent from another player's home" : "Not in any home on this PC";
            return new SettingsNpcMods(set.Npc, name, detail, where, inHomes.Homes, saved);
        }).OrderBy(n => n.Homes.Length > 0 ? 0 : n.Saved ? 1 : 2).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private ImTextureID? Thumbnail(string hash)
    {
        if (!thumbnails.TryGetValue(hash, out var task))
            thumbnails[hash] = task = Task.Run<IDalamudTextureWrap?>(() => housing.PictureThumbnail(hash) is {} t
                ? textures.CreateFromRaw(RawImageSpecification.Rgba32(t.Width, t.Height), t.Rgba, "Haven settings picture") : null);
        return task.IsCompletedSuccessfully ? task.Result?.Handle : null;
    }

    public void Dispose()
    {
        foreach (var task in thumbnails.Values) if (task.IsCompletedSuccessfully) task.Result?.Dispose();
        thumbnails.Clear();
    }

    public void Show(int tab = 0, Guid? home = null)
    {
        state.Tab = tab; if (home != null) state.Home = home;
        statusSince = int.MaxValue;
        IsOpen = true; BringToFront();
    }

    public override void PreDraw()
    {
        HavenKit.PushTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space4)); ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
    }
    public override void PostDraw() { ImGui.PopStyleVar(2); HavenKit.PopTheme(); }

    public override void Draw()
    {
        var relay = engine.HousingRelay;
        var model = new SettingsModel(engine.HousingAmbientEffects, engine.HousingAmbientSounds, AmbientBudget.MaxEffects, AmbientBudget.MaxSounds,
            housing.Houses.Select(h => new SettingsHome(h.Id, h.Name)).ToArray(),
            housing.Store.DirectoryPath, housing.Store.DeletedDirectoryPath, engine.HousingRelayUrl, RelayConnectionFailure.HostedEndpoint, engine.HousingHostCode,
            housing.CanStartVisit, housing.IsSharedVisit,
            [$"Stagehand: {housing.StagehandStatus} · native hooks {(engine.HousingCanLoad ? "resolved" : "unavailable")}",
             $"Active collision parts: {housing.ColliderCount}", housing.FixtureStatus,
             "Editing imported Stagehand objects updates Haven's saved copy. Close Stagehand's own preview to avoid duplicates."],
            relay.IsRecovering ? relay.RecoveryStatus : housing.StatusVersion > statusSince ? housing.Status : "", "Version " + Version)
        {
            LayoutsFolder = housing.LayoutsFolder, GroupsFolder = housing.GroupsFolder,
            Chat = engine.HousingChat,
            WarnMinionProps = engine.HousingWarnMinionProps,
            WarnTiltedSeats = engine.HousingWarnTiltedSeats,
            Patch = state.Tab == 4 ? new SettingsPatch(Native.PatchCheck.PatchGuard.Checked,
                Native.PatchCheck.PatchGuard.AllowedFor.Length > 0 && Native.PatchCheck.PatchGuard.AllowedFor == Native.PatchCheck.PatchGuard.Running,
                [.. Native.PatchCheck.PatchGuard.Paused.Select(f => Haven.Core.PatchGate.Names[f])],
                [.. Native.PatchCheck.PatchGuard.Lines().Skip(1).Concat(Native.PatchCheck.PatchReport.Lines())]) : new(true, false, [], []),
            HavenLights = housing.HavenLights, HavenLightCount = housing.HavenLightCount,
            HavenEffects = housing.HavenEffects, HavenEffectCount = housing.HavenEffectCount,
            HavenSounds = housing.HavenSounds, HavenSoundCount = housing.HavenSoundCount,
            Pictures = state.Tab == 2 ? Pictures() : [],
            Furniture = state.Tab == 2 ? [.. housing.Furniture.Library.Select(f => new SettingsFurniture(f.Hash, f.Item.Name,
                f.Item.BaseItem == 0 ? "A prop made from a minion model" : "Built on " + (housing.Catalogue.Find(f.Item.BaseItem)?.Name ?? "an unknown item"), housing.FurnitureHomes(f.Hash)))] : [],
            Animations = state.Tab == 2 ? [.. housing.Animations.Library.Select(a => new SettingsFurniture(a.Hash, a.Item.Name,
                a.Item.Stored != null ? "On Haven's server" : "On this PC only", housing.AnimationHomes(a.Hash)))] : [],
            AnimationProps = state.Tab == 2 ? [.. housing.Gear.Installed.Values] : [],
            PropChoices = housing.Gear.Remembered,
            Songs = state.Tab == 2 && state.Content == 1 ? [.. housing.Songs.Library.Select(s => new SettingsSong(s.Hash, s.Song.Title,
                $"{s.Song.Milliseconds / 60000}:{s.Song.Milliseconds / 1000 % 60:00} · {(s.Song.Stored != null ? "on Haven's server" : "on this PC only")}",
                housing.SongHomes(s.Hash), s.Song.Stored != null && !s.Song.FromCommunity))] : [],
            Usage = housing.PictureUsage is {} u ? new(u.OwnerBytes, u.OwnerLimit, u.UploadsToday, u.UploadsPerDay, u.PoolBytes, u.PoolLimit, u.At) : null,
            Thumbnail = Thumbnail,
            Storage = state.Tab == 2 ? StorageNow() : null,
            NpcMods = state.Tab == 2 ? npcMods : [],
            Account = new(housing.Account.SignedIn, housing.Account.Name, housing.Account.SigningIn, housing.Account.Problem, housing.Account.Code),
        };
        var command = SettingsUi.Draw(state, model);
        if (command.Action != SettingsAction.None) Execute(command);
    }

    private SettingsStorage? StorageNow() { Storage(); return storage; }

    private void Execute(SettingsCommand command)
    {
        statusSince = housing.StatusVersion;
        switch (command.Action)
        {
            case SettingsAction.Close: IsOpen = false; break;
            case SettingsAction.SignIn: housing.Account.SignIn(engine.HousingRelayUrl, OpenLink); break;
            case SettingsAction.SignOut: housing.Account.SignOut(); break;
            case SettingsAction.CancelSignIn: housing.Account.Cancel(); break;
            case SettingsAction.DeletePicture: housing.Run(() => housing.DeleteOriginal(command.Text, command.First)); picturesAt = default; break;
            case SettingsAction.DeleteUnusedPictures: housing.Run(housing.DeleteUnusedPictures); picturesAt = default; break;
            case SettingsAction.DeleteFurniture: housing.Run(() => housing.DeleteFurniture(command.Text)); break;
            case SettingsAction.DeleteAnimations: housing.Run(() => housing.DeleteAnimations(command.Text)); break;
            case SettingsAction.RemoveProps: housing.Run(housing.RemoveAnimationProps); break;
            case SettingsAction.ForgetPropChoices: housing.Gear.ForgetChoices(); break;
            case SettingsAction.DeleteSong: housing.Run(() => housing.DeleteSong(command.Text, command.First)); break;
            case SettingsAction.Ambience: housing.Run(() => housing.SetAmbience(command.First, command.Second)); break;
            case SettingsAction.Chat: housing.Run(() => engine.SetHousingChat(command.Chat)); break;
            case SettingsAction.WarnMinionProps: housing.Run(() => engine.HousingWarnMinionProps = command.First); break;
            case SettingsAction.PatchAllow:
                Native.PatchCheck.PatchGuard.AllowedFor = engine.HousingPatchAllowedFor = command.First ? Native.PatchCheck.PatchGuard.Running : "";
                break;
            case SettingsAction.WarnTiltedSeats: housing.Run(() => engine.HousingWarnTiltedSeats = command.First); break;
            case SettingsAction.Changelog: ShowChangelog?.Invoke(); break;
            case SettingsAction.DownloadLimit:
                if (long.TryParse(command.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit)) housing.Run(() => housing.DownloadLimit = limit);
                storageAt = default; break;
            case SettingsAction.HavenLights: housing.Run(() => housing.HavenLights = command.First); break;
            case SettingsAction.HavenEffects: housing.Run(() => housing.HavenEffects = command.First); break;
            case SettingsAction.HavenSounds: housing.Run(() => housing.HavenSounds = command.First); break;
            case SettingsAction.ClearDownloads: housing.Run(() => housing.TidyDownloads(all: true)); storageAt = default; break;
            case SettingsAction.RemoveNpcMods:
                var ids = command.Text.Split(',').Select(t => Guid.TryParseExact(t, "N", out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToArray();
                housing.Run(() => housing.RemoveNpcMods(ids)); storageAt = default; break;
            case SettingsAction.Import: Import(); break;
            case SettingsAction.Export: Export(command.Home); break;
            case SettingsAction.OpenHomes: OpenFolder(housing.Store.DirectoryPath); break;
            case SettingsAction.OpenDeleted: OpenFolder(housing.Store.DeletedDirectoryPath); break;
            case SettingsAction.OpenLayouts: OpenFolder(housing.LayoutsFolder); break;
            case SettingsAction.OpenGroups: OpenFolder(housing.GroupsFolder); break;
            case SettingsAction.SaveRelay: housing.Run(() => engine.SetHousingRelay(command.Text)); break;
            case SettingsAction.UseHostedRelay: housing.Run(() => engine.SetHousingRelay(RelayConnectionFailure.HostedEndpoint)); break;
            case SettingsAction.SaveCode: housing.Run(() => { engine.SetHousingVisitCodes(command.Text, engine.HousingGuestCode); housing.VisitCode = command.Text; }); break;
            case SettingsAction.CopyCode: ImGui.SetClipboardText(command.Text); break;
            case SettingsAction.NewCode:
                var code = System.Security.Cryptography.RandomNumberGenerator.GetHexString(16).ToLowerInvariant();
                housing.Run(() => { engine.SetHousingVisitCodes(code, engine.HousingGuestCode); housing.VisitCode = code; });
                break;
        }
    }

    public void Import() => files.OpenFileDialog("Import a Haven home", "Haven homes{.json}", (ok, paths) =>
    {
        if (!ok || paths.Count == 0) return;
        var path = paths[0];
        housing.Run(() => { housing.Import(path); Notify?.Invoke($"Imported {Path.GetFileName(path)} as a new home."); });
    }, 1, housing.Store.DirectoryPath);

    // A MakePlace layout joins the saved layouts; done receives it once saved.
    public void ImportMakePlace(Func<HomeLayout, uint?> shell, Action<Guid> done) => files.OpenFileDialog("Import a MakePlace layout", "MakePlace layouts{.json}", (ok, paths) =>
    {
        if (!ok || paths.Count == 0) return;
        var path = paths[0];
        housing.Run(() => { var result = housing.ImportMakePlace(path, shell); done(result.Layout); Notify?.Invoke(result.Summary("Imported " + Path.GetFileName(path))); });
    }, 1, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    // An image for a picture frame; picked receives its path.
    public void PickPicture(Action<string> picked) => files.OpenFileDialog("Choose a picture", "Pictures{.png,.jpg,.jpeg,.bmp,.gif,.tga}", (ok, paths) =>
    {
        if (ok && paths.Count > 0) picked(paths[0]);
    }, 1, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

    public void PickSong(Action<string> picked) => files.OpenFileDialog("Choose a song", "Songs{.mp3,.ogg,.wav,.flac}", (ok, paths) =>
    {
        if (ok && paths.Count > 0) picked(paths[0]);
    }, 1, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));

    // A furniture mod for custom furniture: a .pmp, or a Penumbra mod folder.
    public void PickFurnitureMod(bool folder, Action<string> picked)
    {
        if (folder) files.OpenFolderDialog("Choose a Penumbra mod folder", (ok, path) => { if (ok && path.Length > 0) picked(path); });
        else files.OpenFileDialog("Choose a furniture mod", "Penumbra mods{.pmp}", (ok, paths) => { if (ok && paths.Count > 0) picked(paths[0]); }, 1);
    }

    // An animation mod for pose balls: a .pmp, or a Penumbra mod folder.
    public void PickAnimationMod(bool folder, Action<string> picked)
    {
        if (folder) files.OpenFolderDialog("Choose a Penumbra animation mod folder", (ok, path) => { if (ok && path.Length > 0) picked(path); });
        else files.OpenFileDialog("Choose an animation mod", "Penumbra mods{.pmp}", (ok, paths) => { if (ok && paths.Count > 0) picked(paths[0]); }, 1);
    }

    public void Export(Guid id)
    {
        if (housing.Houses.FirstOrDefault(h => h.Id == id) is not { } home) return;
        var name = string.Concat(home.Name.Split(Path.GetInvalidFileNameChars())).Trim();
        files.SaveFileDialog("Export a Haven home", "Haven homes{.json}", (name.Length > 0 ? name : "home") + ".haven", ".json", (ok, path) =>
        {
            if (!ok) return;
            housing.Run(() => { housing.Export(home, path); Notify?.Invoke($"Exported {home.Name} to {Path.GetFileName(path)}."); });
        }, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    // Discord's sign-in page, in the player's own browser.
    private void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { housing.Run(() => throw new InvalidOperationException("The browser could not be opened: " + ex.Message)); }
    }

    private void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { housing.Run(() => throw new InvalidOperationException("The folder could not be opened: " + ex.Message)); }
    }

    // The file dialog draws every frame, whether or not Settings is open, so the launcher can use it too.
    public void DrawDialogs() => files.Draw();
}
