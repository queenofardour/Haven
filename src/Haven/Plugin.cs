using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HavenNative;
using Haven.Core;
using Haven.Games;
using Haven.Native;
using Haven.Uld;

namespace Haven;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IChatGui chat;
    private readonly SyncProbe syncProbe;
    private readonly NativeEngine engine;
    private readonly HousingController housing;
    private readonly WindowSystem windows = new("Haven");
    private readonly SettingsWindow settings;
    private readonly LauncherWindow launcher;
    private readonly LoadingScreen loading;
    private readonly IPluginLog log;
    private readonly NpcPossession npc;
    private readonly NpcModShare npcMods;
    private readonly NpcVisitors visitors;
    private readonly IClientState clientState;
    private readonly FurnishingEditor editor;
    private readonly CinematicMode cinematic;
    private readonly ChangelogWindow changelog;
    private readonly UpdateWatch updates;
    private readonly GatheringInvites invites;
    private readonly MiniGameWindow minigames = new();
    private readonly NativeArcade arcadeNative;
    private readonly ArcadeSession arcade;
    private readonly GameUldHost uldHost;
    private readonly GameEffects gameEffects;
    private readonly TriadOpponentWindow triadOpponents;
    private readonly TriadLink triadLink;
    private readonly ArcadeWatch arcadeWatch;
    private readonly ArcadeHud arcadeHud;
    private readonly TriadInviteWindow triadInvite;
    private readonly TriadProbe triadProbe;
    private Task<List<TriadNpc>>? triadNpcs;
    private TriadCard[]? triadCards;
    private readonly IFontHandle nameFont, actionFont;
    private readonly Dictionary<HavenText, IFontHandle> interfaceFonts;
    private bool galleryOpen;
    private readonly NpcNameplates nameplates;
    private readonly NpcTargetBars targetBars;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IObjectTable objectTable,
        IFramework framework, IPluginLog log, IChatGui chat, IGameInteropProvider hooks, ISigScanner sigScanner,
        IDataManager dataManager, IClientState clientState, IGameGui gameGui, ITextureProvider textureProvider,
        IAddonLifecycle addonLifecycle, IDtrBar dtrBar, IKeyState keyState, ICondition condition, IToastGui toast, INotificationManager notifications,
        INamePlateGui namePlates)
    {
        // Every signature lookup and signature hook goes through recorders, so /haven selfcheck can say what a game
        // patch broke.
        hooks = new Haven.Native.PatchCheck.RecordingInterop(hooks);
        sigScanner = new Haven.Native.PatchCheck.RecordingSigScanner(sigScanner);
        Haven.Native.PatchCheck.PatchGuard.Start(RunningGameVersion());
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        this.chat = chat;
        syncProbe = new SyncProbe(pluginInterface, objectTable, chat, log);
        HavenUi.IconFontProvider = () => UiBuilder.IconFont;
        interfaceFonts = Enum.GetValues<HavenText>().ToDictionary(text => text, text => InterFont(pluginInterface.UiBuilder.FontAtlas, text));
        HavenFonts.Provider = text => interfaceFonts[text].Push();
        engine = new NativeEngine(pluginInterface, commands, objectTable, framework, log, chat, hooks,
            sigScanner, dataManager, clientState, gameGui, textureProvider, addonLifecycle, dtrBar, condition, toast);
        housing = new HousingController(pluginInterface, engine, log, framework, dataManager, sigScanner);
        editor = new FurnishingEditor(housing, engine, textureProvider, gameGui, keyState)
        {
            NameFont = nameFont = pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 18)),
            ActionFont = actionFont = pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 14)),
        };
        settings = new SettingsWindow(housing, engine, commands, dataManager, textureProvider)
        {
            Notify = message => notifications.AddNotification(new Notification { Title = "Haven", Content = message, Type = NotificationType.Success }),
        };
        launcher = new LauncherWindow(housing, engine, dataManager, textureProvider, settings);
        launcher.Decorate = editor.StartDecorating;
        loading = new LoadingScreen(housing, engine, launcher.Art);
        launcher.Curtain = loading.Enter;
        this.clientState = clientState;
        this.log = log;
        npc = new NpcPossession(housing, engine, objectTable, log);
        editor.Npcs = npc;
        var glamour = new NpcGlamour(pluginInterface, log);
        npc.Glamour = glamour;
        npc.Collections = new NpcCollections(pluginInterface, log);
        npcMods = new NpcModShare(pluginInterface, housing, engine, npc, glamour, objectTable, log);
        npc.Collections.Mods = npcMods.CollectionFiles;
        loading.ShareBar = npcMods.Bar;
        editor.NpcMods = npcMods;
        housing.NpcsCopied = npcMods.CopyKept;
        engine.HousingRelay.OnMiniGame += npcMods.Receive;
        housing.Gear.Prepare = index => glamour.Reidentify(index);
        nameplates = new NpcNameplates(namePlates, npc.NameAt);
        targetBars = new NpcTargetBars(addonLifecycle, npc.IsNpc, log);
        npc.NameChanged = nameplates.Refresh;
        housing.PlayerDesign = () => glamour.State(0);
        var body = new NpcBody(pluginInterface, log);
        npc.Body = body;
        editor.NpcBodies = body;
        housing.PlayerBody = body.ReadPlayer;
        npc.EmoteTimeline = emote => dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>().GetRowOrDefault(emote) is { } row && row.ActionTimeline[0].RowId is > 0 and <= ushort.MaxValue
            ? ((ushort)row.ActionTimeline[0].RowId, row.EmoteMode.RowId != 0) : null;
        npc.PrintSay = (name, text) => HavenNative.PrivateRoomChatService.SpeakAs?.Invoke(name, text);
        HavenNative.PrivateRoomChatService.NpcEmote = npc.PlayEmote;
        HavenNative.PrivateRoomChatService.NpcSay = npc.Say;
        HavenNative.TypingIndicatorService.SpeakerFor = npc.SpeakerFor;
        housing.Leaving += npc.RemoveAll;
        housing.Leaving += npcMods.Leaving;
        housing.PossessedNpc = () => npc.PossessedId;
        housing.NpcAt = npc.PositionOf;
        engine.HousingRelay.OnMiniGame += npc.Receive;
        var roster = new NpcRoster(Path.Combine(pluginInterface.ConfigDirectory.FullName, "npc-roster.json")) { CopyMods = npcMods.CopyKept, DropMods = npcMods.DropKept };
        visitors = new NpcVisitors(housing, engine, roster, log);
        settings.KeptNpcMods = npcMods.KeptSets;
        settings.RosterNames = () => roster.Everyone.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First().Name);
        housing.RemoveKeptNpcMods = npcMods.RemoveKept;
        npc.Visitors = visitors;
        npcMods.Visitors = visitors;
        visitors.Welcomed = npcMods.VisitorWelcomed;
        editor.Visitors = visitors;
        housing.Visitors = () => visitors.Shown;
        housing.Leaving += visitors.Clear;
        engine.HousingRelay.OnMiniGame += visitors.Receive;
        clientState.TerritoryChanged += OnTerritoryChanged;
        editor.PickPicture = settings.PickPicture;
        editor.PickSong = settings.PickSong;
        editor.PickFurnitureMod = settings.PickFurnitureMod;
        editor.PickAnimationMod = settings.PickAnimationMod;
        // Entering the room's tools steps the launcher aside.
        editor.CloseWorkspace = () => launcher.IsOpen = false;
        // Failures also show as a notification: the room's panels have no status line of their own.
        housing.Failed += message => notifications.AddNotification(new Notification { Title = "Haven", Content = message, Type = NotificationType.Error });
        changelog = new ChangelogWindow(engine);
        settings.ShowChangelog = changelog.ShowRecent;
        windows.AddWindow(settings);
        windows.AddWindow(launcher);
        windows.AddWindow(changelog);
        // The Gold Saucer: the arcade machines in the game's own windows (driven locally), Triple Triad drawn from the
        // game's own window layout. Nothing reaches the server and no MGP is spent or won.
        arcadeNative = new NativeArcade(sigScanner, addonLifecycle, log);
        triadProbe = new TriadProbe(hooks, log);
        arcade = new ArcadeSession(arcadeNative, objectTable, chat, dataManager, log)
        {
            Hold = hold => housing.ArcadeHolding = hold, MachineTimeline = housing.PlayMachineTimeline,
            Bests = new ArcadeBests(Path.Combine(pluginInterface.ConfigDirectory.FullName, "arcade-bests.json")),
            Character = () => engine.HousingCharacterId is var id and not 0 ? Haven.Core.HomeOwners.Key(id) : null,
        };
        uldHost = new GameUldHost(dataManager, textureProvider, pluginInterface.UiBuilder.FontAtlas);
        uldHost.Prepare();   // the minigames' fonts build with the plugin's, never while a screen is showing
        gameEffects = new GameEffects(arcadeNative, objectTable, dataManager, housing.OverrideMusic, log);
        windows.AddWindow(minigames);
        // Escape belongs to the minigame while it plays: taken from the game's keys, so it doesn't also close game windows.
        var escapeHeld = false;
        minigames.Escape = () =>
        {
            var down = keyState[Dalamud.Game.ClientState.Keys.VirtualKey.ESCAPE];
            if (down) keyState[Dalamud.Game.ClientState.Keys.VirtualKey.ESCAPE] = false;
            var pressed = down && !escapeHeld; escapeHeld = down;
            return pressed;
        };
        editor.PlayArcade = item => housing.Run(() =>
        {
            if (housing.ActiveHouse is not { } house || housing.Catalogue.Get(item).Arcade is not { } machine) return;
            if (arcade.Active) return;
            if (minigames.Playing) throw new InvalidOperationException("Finish the game you're playing first.");
            if (!Haven.Native.PatchCheck.PatchGuard.Allows(Haven.Core.PatchFeature.Arcade))
                throw new InvalidOperationException("The Gold Saucer machines are paused after the game's update until Haven is updated (Settings → Advanced can turn them on).");
            arcade.Start(machine, item.Id, FurnitureCatalogue.ItemMatrix(item) * FurnitureCatalogue.HouseMatrix(house));
        });
        // The Triple Triad Board: choose an opponent (Haven, or any of the game's Triple Triad NPCs), then play.
        var ruleNames = dataManager.GetExcelSheet<Lumina.Excel.Sheets.TripleTriadRule>();
        string RuleName(TriadRule rule) => ruleNames.GetRowOrDefault((uint)rule)?.Name.ExtractText() is { Length: > 0 } text ? text : rule.ToString();
        TriadCard[] Cards() => triadCards ??= TriadCards.Load(dataManager.GetExcelSheet<Lumina.Excel.Sheets.TripleTriadCardResident>(), dataManager.GetExcelSheet<Lumina.Excel.Sheets.TripleTriadCard>());
        // Two players in a gathering: invitations, deals and moves over the relay.
        IReadOnlyList<TriadCard> DeckCards(TriadDeck? deck) => deck?.Cards ?? TriadGame.Deal([.. Cards().Where(c => TriadCollection.Owns(c.Id))], Cards(), new Random());
        string PlayerName() => objectTable.LocalPlayer?.Name.TextValue ?? "You";
        housing.CameraForward = FurnishingEditor.CameraForward;
        arcadeHud = new ArcadeHud(arcade, gameGui, nameFont, actionFont);
        // Players at arcade machines, seen by the gathering: the stance, each swing, the end.
        arcadeWatch = new ArcadeWatch(arcadeNative, engine.HousingPeerCharacter);
        engine.HousingRelay.OnMiniGame += arcadeWatch.Receive;
        arcade.Share = (kind, item, timeline) =>
        {
            if (engine.HousingRelay.IsConnected)
                _ = engine.HousingRelay.SendMiniGame(new GameMessage(engine.HousingCharacterId, GameMessage.Everyone, "arcade", kind, item, [timeline]));
        };
        triadLink = new TriadLink(message => engine.HousingRelay.SendMiniGame(message), () => engine.HousingCharacterId,
            () => [.. engine.HousingRelay.HousingMembers.Values.Select(m => (m.ContentId, m.Name))], Cards)
        {
            Busy = () => minigames.Playing || arcade.Active,
        };
        engine.HousingRelay.OnMiniGame += triadLink.Receive;
        triadInvite = new TriadInviteWindow(triadLink, RuleName, () => TriadCollection.Decks(Cards()));
        triadInvite.Accepted = (invitation, deck) => housing.Run(() =>
        {
            if (minigames.Playing || arcade.Active) throw new InvalidOperationException("Finish the game you're playing first.");
            triadLink.Accept(invitation, DeckCards(deck));
        });
        windows.AddWindow(triadInvite);
        triadOpponents = new TriadOpponentWindow(() => triadNpcs is { IsCompletedSuccessfully: true } done ? done.Result : [], RuleName,
            () => TriadCollection.Decks(Cards()), triadLink)
        {
            Invite = (target, name, rules, deck) => housing.Run(() => triadLink.Invite(target, name, rules, DeckCards(deck))),
            Chosen = (opponent, deck) => housing.Run(() =>
            {
                if (minigames.Playing || arcade.Active) throw new InvalidOperationException("Finish the game you're playing first.");
                var name = objectTable.LocalPlayer?.Name.TextValue ?? "You";
                minigames.Play(new TripleTriadMatch(gameEffects, uldHost, new TriadSetup(Cards(), TriadCollection.Owns, name, opponent, RuleName, deck?.Cards)));
            }),
        };
        windows.AddWindow(triadOpponents);
        // The Crystal Odyssey (the Triple Triad roguelite): its run and records live in the config folder; the director
        // is loaded on first use and saved by its own Changed event; item icons for its Materia, Items and Enhancements
        // come from the Item sheet by name.
        var odysseySaves = new OdysseySaves(pluginInterface.ConfigDirectory.FullName, message => log.Warning(message));
        Haven.Core.Odyssey.Odyssey? odysseyDirector = null;
        Haven.Core.Odyssey.Odyssey OdysseyDirector() => odysseyDirector ??= odysseySaves.Open(Cards(), (level, unseen) => new Haven.Core.Odyssey.OdysseyAi(level, unseen));
        Dictionary<string, uint>? itemIcons = null;
        uint IconOf(string name)
        {
            if (itemIcons == null)
            {
                itemIcons = [];
                foreach (var item in dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()) if (item.Icon != 0) itemIcons.TryAdd(item.Name.ExtractText(), item.Icon);
            }
            return itemIcons.GetValueOrDefault(name);
        }
        triadOpponents.OdysseyInfo = () =>
        {
            var director = OdysseyDirector();
            var title = director.Title();
            return new OdysseyTabInfo(title.CanContinue, title.ContinueText, director.Run?.Binder.Count ?? 0, director.Run?.Materia.Count ?? 0, director.Run?.Mgp ?? 0, title.UnlockedRank, director.Records);
        };
        triadOpponents.StartOdyssey = (start, rank) => housing.Run(() =>
        {
            if (minigames.Playing || arcade.Active) throw new InvalidOperationException("Finish the game you're playing first.");
            triadOpponents.IsOpen = false;
            minigames.Play(new OdysseyGame(gameEffects, uldHost, new OdysseySetup(OdysseyDirector(), Cards(), PlayerName(), RuleName, IconOf, start, rank)));
        });
        triadOpponents.AbandonOdyssey = () => housing.Run(() => OdysseyDirector().Abandon());
        triadLink.Started = (name, rules, remote) => housing.Run(() =>
        {
            triadOpponents.IsOpen = false; triadInvite.IsOpen = false;
            if (minigames.Playing) minigames.Stop();
            minigames.Play(new TripleTriadMatch(gameEffects, uldHost, new TriadSetup(Cards(), TriadCollection.Owns, PlayerName(), new TriadOpponent(name, rules), RuleName, null, remote)));
        });
        editor.PlayTriad = () =>
        {
            if (minigames.Playing || arcade.Active) return;
            triadNpcs ??= Task.Run(() => TriadOpponents.Load(dataManager, Cards()));
            triadOpponents.Open();
        };
        commands.AddHandler("/haven", new CommandInfo(OnCommand)
        { HelpMessage = "Open Haven. /haven settings opens Settings; /haven changelog shows what's new; /haven leave returns from a visit; /haven kit shows the interface components." });
        // After an update, what's new in it (and any versions skipped).
        changelog.ShowIfUpdated();
        // Notices from the relay's operator, and Haven's own check for a newer version: a notification and a chat line.
        housing.Noticed += (title, text, warning) =>
        {
            notifications.AddNotification(new Notification { Title = title, Content = text, Type = warning ? NotificationType.Warning : NotificationType.Info, InitialDuration = TimeSpan.FromSeconds(20) });
            chat.Print($"[Haven] {title}: {text}");
        };
        updates = new UpdateWatch(pluginInterface, log);
        updates.Found += version => framework.RunOnFrameworkThread(() =>
        {
            var text = engine.HousingSessionActive ? "Update in /xlplugins after you leave your home." : "Update in /xlplugins. Click to open the plugin installer.";
            var shown = notifications.AddNotification(new Notification { Title = $"Haven {version} is available", Content = text, Type = NotificationType.Info, InitialDuration = TimeSpan.FromSeconds(30) });
            shown.Click += _ => updates.OpenInstaller();
            chat.Print($"[Haven] Haven {version} is available. {text}");
        });
        cinematic = new CinematicMode(pluginInterface, log)
        {
            ChatTwoHintSeen = () => engine.HousingCinematicChatTwoHintSeen,
            ChatTwoHintDismissed = () => engine.HousingCinematicChatTwoHintSeen = true,
        };
        editor.Cinematic = cinematic;
        // Friends nearby are asked to join a gathering; the visit code stays the fallback.
        invites = new GatheringInvites(housing, engine, pluginInterface.ConfigDirectory.FullName, log);
        editor.Invites = invites;
        // A game patch: what it broke, and what Haven paused.
        Haven.Native.PatchCheck.PatchGuard.AllowedFor = engine.HousingPatchAllowedFor;
        Haven.Native.PatchCheck.PatchGuard.CanaryFailed = (feature, why) => framework.RunOnFrameworkThread(() =>
        {
            log.Warning("[SELFCHECK] {Feature} switched off: {Why}", Haven.Core.PatchGate.Names[feature], why);
            notifications.AddNotification(new Notification { Title = "Haven", Content = $"Switched off {Haven.Core.PatchGate.Names[feature]}: the game has changed under it. A Haven update will bring it back.", Type = NotificationType.Warning });
        });
        Haven.Native.PatchCheck.PatchGuard.Warned = (feature, why) => log.Warning("[SELFCHECK] {Feature}'s runtime check failed on the checked game version (left on): {Why}", Haven.Core.PatchGate.Names[feature], why);
        foreach (var line in SelfCheck()) log.Information("[SELFCHECK] {Line}", line);
        framework.RunOnTick(() => NoticePatch(notifications), TimeSpan.FromSeconds(5));
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += Open;
        pluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        framework.Update += Update;
    }

    private static unsafe string RunningGameVersion()
    {
        try { return FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->GameVersionString ?? ""; }
        catch { return ""; }
    }

    /// <summary>The self-check's lines: the game version against the checked one, entering homes, and the code lookups.</summary>
    private IEnumerable<string> SelfCheck()
    {
        foreach (var line in Haven.Native.PatchCheck.PatchGuard.Lines()) yield return line;
        yield return engine.HousingCanLoad ? "Entering homes: available." : "Entering homes: NOT available on this game version (a hook it needs is missing). A Haven update will fix it.";
        foreach (var line in Haven.Native.PatchCheck.PatchReport.Lines()) yield return line;
    }

    // Once per game version: say when homes can't be entered, or which extras are paused, after a patch.
    private void NoticePatch(INotificationManager notifications)
    {
        var running = Haven.Native.PatchCheck.PatchGuard.Running;
        if (running.Length == 0 || engine.HousingPatchNoticeFor == running) return;
        string? text = !engine.HousingCanLoad
            ? "This game version broke something Haven needs to open homes, so entering is off until Haven is updated. /haven selfcheck shows the details."
            : Haven.Native.PatchCheck.PatchGuard.Paused is { Count: > 0 } paused
                ? "The game has updated since this Haven was checked. Until Haven is updated, these are paused: " + string.Join(", ", paused.Select(f => Haven.Core.PatchGate.Names[f])) + ". Settings → Advanced can turn them on."
                : null;
        if (text == null) return;
        engine.HousingPatchNoticeFor = running;
        notifications.AddNotification(new Notification { Title = "Haven and the game update", Content = text, Type = NotificationType.Warning, InitialDuration = TimeSpan.FromSeconds(30) });
        chat.Print("[Haven] " + text);
    }

    private void Update(IFramework _)
    {
        // Each part on its own: one that throws mustn't stop the rest every frame (NPC mods, cinematic mode, the arcade).
        Guarded("input", editor.ConsumeGameInput); Guarded("the home", housing.Update);
        Guarded("Visiting NPCs", visitors.Update); Guarded("NPCs", () => npc.Update((float)framework.UpdateDelta.TotalSeconds)); Guarded("NPC mods", npcMods.Update);
        Guarded("cinematic mode", () => cinematic.Update(housing.Bound is { Playing: true }));
        Guarded("the arcade", () => arcade.Update(housing.IsReady));
        Guarded("game sounds", gameEffects.Flush);
        Guarded("other players' arcade play", () => arcadeWatch.Update(housing.IsReady));
        Guarded("Triple Triad invitations", () =>
        {
            triadLink.Update(engine.HousingRelay.IsConnected && housing.IsReady, minigames.Playing);
            if (triadLink.Invitations.Count > 0 && !triadInvite.IsOpen) triadInvite.IsOpen = true;
        });
        Guarded("minigames", () => { if (minigames.Playing && !housing.IsReady) minigames.Stop(); });
        Guarded("gathering invitations", invites.Update);
    }
    // A failing part is logged at most every 10 s, with what it was.
    private readonly Dictionary<string, long> lastUpdateFailure = [];
    private void Guarded(string what, Action part)
    {
        try { part(); }
        catch (Exception ex)
        {
            var now = Environment.TickCount64;
            if (lastUpdateFailure.TryGetValue(what, out var last) && now - last < 10_000) return;
            lastUpdateFailure[what] = now;
            log.Error(ex, "[Haven] Updating {What} failed.", what);
        }
    }
    // Haven's windows, or in cinematic mode only its way back.
    private void Draw()
    {
        cinematic.Draw();
        if (!CinematicMode.Hiding) { windows.Draw(); settings.DrawDialogs(); editor.DrawWorld(); DrawGallery(); arcadeHud.Draw(); npc.Draw(); invites.Draw(); }
        // Last, so the loading screen covers every other Haven window while it shows.
        loading.Draw();
    }
    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("leave", StringComparison.OrdinalIgnoreCase)) housing.Run(housing.Leave);
        else if (args.Trim().Equals("kit", StringComparison.OrdinalIgnoreCase)) galleryOpen = !galleryOpen;
        // What this game version broke: code lookups that failed, entering homes, features paused.
        else if (args.Trim().Equals("selfcheck", StringComparison.OrdinalIgnoreCase))
            foreach (var line in SelfCheck()) { chat.Print("[Haven] " + line); log.Information("[SELFCHECK] {Line}", line); }
        else if (args.Trim().Equals("settings", StringComparison.OrdinalIgnoreCase)) settings.Show();
        // NPCs: add one in your look in front of you (saved in the home), and take or give up control of the nearest.
        else if (args.Trim().Equals("npc", StringComparison.OrdinalIgnoreCase)) chat.Print("[Haven] " + npc.Add());
        else if (args.Trim().Equals("possess", StringComparison.OrdinalIgnoreCase)) chat.Print("[Haven] " + npc.TogglePossess());
        else if (args.Trim().Equals("changelog", StringComparison.OrdinalIgnoreCase)) changelog.ShowRecent();
        else if (args.Trim().StartsWith("dyeprobe", StringComparison.OrdinalIgnoreCase)) DyeProbe(args.Trim()[8..].Trim());
        else if (args.Trim().StartsWith("glowprobe", StringComparison.OrdinalIgnoreCase)) DyeProbe(args.Trim()[9..].Trim(), glow: true);
        // Read-only: which files Penumbra draws a player with here (a synced friend's should come from their sync plugin's cache).
        else if (args.Trim().StartsWith("syncprobe", StringComparison.OrdinalIgnoreCase)) syncProbe.Run(args.Trim()[9..].Trim());
        // Read-only: how you and each gathering member are drawn here (for a member who doesn't show).
        else if (args.Trim().Equals("peers", StringComparison.OrdinalIgnoreCase))
        {
            var lines = engine.HousingMemberDrawStates();
            if (lines.Count == 0) chat.Print("[Haven] peers: nobody to show.");
            foreach (var line in lines) { chat.Print("[Haven] " + line); log.Information("[PEERS] {Line}", line); }
        }
        // Read-only: records a real Triple Triad match's window commands in the log, for Haven to drive that window later.
        else if (args.Trim().Equals("triadprobe", StringComparison.OrdinalIgnoreCase))
            chat.Print(triadProbe.Toggle() ? "[Haven] Recording Triple Triad: play an NPC match outside your home, then /haven triadprobe again and send the log." : "[Haven] Triple Triad recording stopped.");
        else Open();
    }

    // Unlisted diagnostics: in a real house, what the game writes into undyed furniture's dye buffers (dyeprobe), or
    // into each model's per-instance emissive glow (glowprobe), to compare a lamp switched on and off.
    private void DyeProbe(string filter, bool glow = false)
    {
        foreach (var line in HavenNative.DyeProbe.Read(filter.Length == 0 ? "hou/indoor/general" : filter, glow: glow)) chat.Print("[Haven] " + line);
    }

    // Sample-data gallery of the redesign components, for in-game review only.
    private void DrawGallery()
    {
        if (!galleryOpen) return;
        var viewport = ImGui.GetMainViewport(); var area = Vector2.Min(viewport.Size, new Vector2(1440, 900) * HavenUi.S);
        HavenKitGallery.Draw(viewport.Pos + (viewport.Size - area) / 2, area, ref galleryOpen, closable: true);
    }
    // The launcher, with a strip for the current visit when inside a home. The room's own tools are on its toolbar.
    private void Open() => launcher.Show();
    private void OpenSettings() => settings.Show();

    // Embedded Inter at one size per text style, merged with Dalamud's extra
    // language glyphs so names in other scripts still render.
    private static IFontHandle InterFont(IFontAtlas atlas, HavenText text) => InterFont(atlas, HavenTokens.SemiBold(text), HavenTokens.Size(text));

    /// <summary>Inter Regular or SemiBold at a size in pixels, from the embedded fonts.</summary>
    internal static IFontHandle InterFont(IFontAtlas atlas, bool strong, float sizePx) => atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
    {
        var file = strong ? "Inter-SemiBold.ttf" : "Inter-Regular.ttf";
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Haven.Fonts." + file)
            ?? throw new InvalidOperationException("Missing embedded font " + file);
        var font = tk.AddFontFromStream(stream, new SafeFontConfig { SizePx = sizePx }, false, "Haven " + file);
        tk.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx = sizePx, MergeFont = font });
        tk.Font = font;
    }));

    private void OnTerritoryChanged(uint _) => npc.Forget();


    // Each shutdown step on its own: one that throws (Stagehand not answering, a window already gone) must never skip the
    // rest, above all the engine's, which takes the player back to their real position before the packet filter goes
    // (the real-world safety invariant).
    private void Step(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { log.Warning(ex, "[Haven] {What} failed while shutting down.", what); }
    }

    public void Dispose()
    {
        try
        {
            Step("Territory events", () => clientState.TerritoryChanged -= OnTerritoryChanged);
            // Before the NPCs: a share in progress puts the player's own collection and look back.
            Step("NPC mods", npcMods.Dispose);
            Step("Removing NPCs", npc.RemoveAll);
            npc.NameChanged = null;
            Step("Nameplates", nameplates.Dispose);
            Step("Target bars", targetBars.Dispose);
            // The game's arcade agent holds a pointer into Haven while a game is open: let go of it before anything else.
            Step("Closing the arcade", arcade.Finish);
            Step("The arcade agent", arcadeNative.Dispose);
            Step("Mini games", minigames.Stop);
            Step("Relay game events", () =>
            {
                engine.HousingRelay.OnMiniGame -= triadLink.Receive;
                engine.HousingRelay.OnMiniGame -= arcadeWatch.Receive;
                engine.HousingRelay.OnMiniGame -= npc.Receive;
                engine.HousingRelay.OnMiniGame -= visitors.Receive;
                engine.HousingRelay.OnMiniGame -= npcMods.Receive;
            });
            HavenNative.PrivateRoomChatService.NpcEmote = null; HavenNative.PrivateRoomChatService.NpcSay = null; HavenNative.TypingIndicatorService.SpeakerFor = null;
            framework.Update -= Update;
            pluginInterface.UiBuilder.Draw -= Draw;
            Step("Cinematic mode", cinematic.Dispose);
            Step("Update watch", updates.Dispose);
            Step("Gathering invitations", invites.Dispose);
            pluginInterface.UiBuilder.OpenMainUi -= Open;
            pluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
            Step("Commands", () => commands.RemoveHandler("/haven"));
            Step("Triple Triad probe", triadProbe.Dispose);
            Step("Windows", windows.RemoveAllWindows);
            Step("Game layouts", uldHost.Dispose);
            Step("Editor", editor.Dispose);
            Step("Settings", settings.Dispose);
            Step("Housing", housing.Dispose);
        }
        finally
        {
            engine.Dispose();
            Step("Fonts", () =>
            {
                nameFont.Dispose();
                actionFont.Dispose();
                HavenFonts.Provider = null;
                foreach (var font in interfaceFonts.Values) font.Dispose();
            });
        }
    }
}
