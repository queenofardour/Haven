using Dalamud.Bindings.ImGui;
using Haven.Core;
using HavenNative.Services;

namespace Haven;

// The in-room side panels: which one is open, and the Room, Music and Visit
// adapters between HousingController / the relay and RoomPanelUi, MusicPanelUi and VisitPanelUi.
internal sealed partial class FurnishingEditor
{
    private RoomPanel panel = RoomPanel.Furnish;
    private bool panelVisible;
    private readonly MusicPanelState music = new();
    private readonly VisitPanelState visit = new();
    private string? musicFilter;
    private TrackChoice[] musicMatches = [];

    // Opens a panel, or closes it when it is already open. Furnish also starts editing.
    private void TogglePanel(RoomPanel next)
    {
        if (panel == next && panelVisible) { panelVisible = false; return; }
        panel = next; panelVisible = true;
        if (next == RoomPanel.Furnish && !editing) StartEditing();
    }
    public void ShowPanel(RoomPanel next) { panel = next; panelVisible = true; CloseWorkspace?.Invoke(); }
    private RoomPanel ShownPanel => !panelVisible || panel is RoomPanel.Furnish or RoomPanel.Remodel && !editing || panel == RoomPanel.Room && !housing.CanFurnish
        || panel == RoomPanel.Remodel && !housing.CanRemodel ? RoomPanel.None : panel;

    // Draws the open panel, if any, and returns its bounds for keeping clicks out of the room.
    private UiBounds DrawPanels()
    {
        var open = true;
        // A sound previewed from the drawer stops once the drawer isn't shown.
        if (ShownPanel != RoomPanel.Furnish && housing.PreviewingSound != null) housing.Run(housing.StopPreview);
        switch (ShownPanel)
        {
            case RoomPanel.Furnish: drawerOpen = true; DrawDrawer(); open = drawerOpen; break;
            case RoomPanel.Room: DrawRoomPanel(ref open); break;
            case RoomPanel.Music: DrawMusicPanel(ref open); break;
            case RoomPanel.Visit: DrawVisitPanel(ref open); break;
            case RoomPanel.Npcs: DrawNpcPanel(ref open); break;
            case RoomPanel.Remodel: DrawRemodelPanel(ref open); break;
            default: SidePanelUi.Clear(); return default;
        }
        if (!open) panelVisible = false;
        return open ? SidePanelUi.Bounds : default;
    }

    private string VisitLabel
    {
        get
        {
            var relay = engine.HousingRelay;
            if (relay.IsRecovering) return "Visit · reconnecting";
            return relay.IsHost && !relay.SoloMode && relay.HousingMembers.Count > 1 ? $"Visit · {relay.HousingMembers.Count}" : "Visit";
        }
    }

    // Room ---------------------------------------------------------------

    private static readonly string[] FloorNames = ["Ground floor", "Upper floor", "Basement"];
    private void DrawRoomPanel(ref bool open)
    {
        if (housing.ActiveHouse is not {} house) { open = false; return; }
        var floors = InteriorFloorPickerUi.FloorsForShell(Catalogue.InteriorShellCode(house.TerritoryId)).ToArray();
        if (floors.Length > 0 && !floors.Contains(floor)) floor = floors[0];
        var slots = new List<RoomSlot>(); var parts = new List<(uint Part, bool MainHall)>();
        if (!house.CustomLayout && floors.Length > 0)
        {
            var finishes = house.Interior.Floors[floor];
            void Add(string label, uint part, uint current, bool mainHall = false)
            {
                slots.Add(new(label, current, Catalogue.Fixtures.Where(x => x.Part == part).Select(x => new FixtureChoice(x.FixtureId, x.Name, x.Icon)).ToArray()));
                parts.Add((part, mainHall));
            }
            Add("Wallpaper", 0, finishes.Wallpaper); Add("Flooring", 3, finishes.Flooring); Add("Ceiling light", 4, finishes.CeilingLight);
            if (Catalogue.HasMainHallChandelier(house.TerritoryId)) Add("Main hall chandelier", 4, house.Interior.MainHallCeilingLight, true);
        }
        var layouts = house.CustomLayout ? [] : housing.SavedLayouts.Where(x => x.TerritoryId == house.TerritoryId).ToArray();
        var model = new RoomPanelModel(house.CustomLayout, housing.CanFurnish, floors.Select(f => FloorNames[f]).ToArray(), Array.IndexOf(floors, floor),
            slots, house.Interior.Brightness, housing.FixtureStatus, IconTexture)
        {
            Layouts = layouts.Select(x => x.Name).ToArray(), Inn = engine.IsInnTerritory(house.TerritoryId),
            Weathers = engine.HousingWeathers(house.TerritoryId), Weather = house.Interior.Weather, TimeOfDay = house.Interior.TimeOfDay,
            ClockNow = engine.HousingTimeOfDay, Outside = house.Interior.Outside, OutsideAvailable = housing.OutsideAvailable, OutsideVolume = housing.OutsideVolume,
            Moods = housing.Moods.Select(m => new MoodRow(m.Name, MoodDetail(m), HomeMoods.Showing(house, m))).ToArray(),
        };
        var command = RoomPanelUi.Draw(model, ref open, SidePanelUi.Top);
        housing.TimePreview = command.Action == RoomAction.PreviewTime ? (int)command.Level : null;
        var moods = housing.Moods;
        switch (command.Action)
        {
            case RoomAction.SetTime: var time = command.Level < 0 ? (int?)null : (int)command.Level;
                housing.Run(() => housing.SetAtmosphere(house.Interior.Weather, time, house.Interior.Outside)); break;
            case RoomAction.SetWeather: var weather = command.Fixture == 0 ? (uint?)null : command.Fixture;
                housing.Run(() => housing.SetAtmosphere(weather, house.Interior.TimeOfDay, house.Interior.Outside)); break;
            case RoomAction.SetOutside: var outside = command.Text;
                housing.Run(() => housing.SetAtmosphere(house.Interior.Weather, house.Interior.TimeOfDay, outside)); break;
            case RoomAction.OutsideVolume: housing.OutsideVolume = command.Level; break;
            case RoomAction.SaveMood when command.Text is { } moodName: var music = command.Level > 0;
                housing.Run(() => housing.SaveMood(moodName, music)); break;
            case RoomAction.ApplyMood when command.Index < moods.Count: var apply = moods[command.Index].Id; housing.Run(() => housing.ApplyMood(apply)); break;
            case RoomAction.UpdateMood when command.Index < moods.Count: var update = moods[command.Index].Id; housing.Run(() => housing.UpdateMood(update)); break;
            case RoomAction.RenameMood when command.Index < moods.Count && command.Text is { } newName:
                var rename = moods[command.Index].Id; housing.Run(() => housing.RenameMood(rename, newName)); break;
            case RoomAction.DeleteMood when command.Index < moods.Count: var delete = moods[command.Index].Id; housing.Run(() => housing.DeleteMood(delete)); break;
            case RoomAction.Floor: floor = floors[command.Index]; break;
            case RoomAction.Fixture: SetFixture(house, parts[command.Index].Part, command.Fixture, parts[command.Index].MainHall); break;
            case RoomAction.Brightness: housing.Run(() => housing.SetInterior(house.Interior with { Brightness = command.Level })); break;
            case RoomAction.RestoreLighting: housing.Run(() => housing.SetInterior(house.Interior with { Brightness = null })); break;
            // The finishes and brightness go back; the home's atmosphere and moods stay.
            case RoomAction.RestoreInterior: housing.Run(() => housing.SetInterior(new()
                { Weather = house.Interior.Weather, TimeOfDay = house.Interior.TimeOfDay, Outside = house.Interior.Outside, Moods = house.Interior.Moods })); break;
            case RoomAction.ReturnToPlatform: housing.Run(housing.ReturnToPlatform); break;
            case RoomAction.ApplyLayout when command.Index < layouts.Length:
                var id = layouts[command.Index].Id; housing.Run(() => housing.ApplyLayout(id)); break;
            case RoomAction.RenameLayout when command.Index < layouts.Length && command.Text is { } name:
                var renamed = layouts[command.Index].Id; housing.Run(() => housing.RenameLayout(renamed, name)); break;
            case RoomAction.DeleteLayout when command.Index < layouts.Length:
                var deleted = layouts[command.Index].Id; housing.Run(() => housing.DeleteLayout(deleted)); break;
        }
    }

    // A mood's row: what it sets, briefly ("6:00 pm · Rain · brightness 2 · music").
    private static string MoodDetail(HomeMood mood)
    {
        var parts = new List<string>();
        parts.Add(mood.TimeOfDay is { } t ? HomeMoods.Clock(t) : "Eorzea's clock");
        if (mood.Outside is { } o && HomeAmbience.Find(o) is { } sound) parts.Add(sound.Name);
        parts.Add(mood.Brightness is { } b ? $"brightness {b:0}" : "original lighting");
        if (mood.Music is { } m) parts.Add(m.Playing && m.Playlist.Length > 0 ? $"music ({m.Playlist.Length} {(m.Playlist.Length == 1 ? "song" : "songs")})" : "no music");
        return string.Join(" · ", parts);
    }

    // NPCs ---------------------------------------------------------------

    private void DrawNpcPanel(ref bool open)
    {
        if (housing.ActiveHouse == null) { open = false; return; }
        var list = housing.Npcs;
        var names = Npcs?.GameNames(list) ?? list.Select(x => x.Name).ToArray();
        var model = new NpcPanelModel(housing.CanFurnish,
            list.Select((x, i) => new NpcRow(x.Id, x.Name, names[i], x.Away, Npcs?.Present(x.Id) == true, Npcs?.IsPossessed(x.Id) == true,
                Npcs?.HeldByOther(x.Id) == true, x.Look.Design != null, x.Emote != null, x.Look.Design is { } design && PinsColours(design), x.Seat != null, x.Level ?? 0)
                { Collection = NpcMods?.CollectionOf(x.Id), Mods = NpcMods?.Status(x.Id) ?? "", HasMods = NpcMods?.Stored(x.Id) != null, HasBody = x.Look.Body != null,
                    ModCount = NpcMods?.ModCountOf(x.Id) ?? 0 }).ToArray(),
            "")
        {
            Designs = () => Npcs?.Glamour?.Designs() ?? [],
            Collections = () => NpcMods?.Collections() ?? [],
            BodyProfiles = () => NpcBodies?.Profiles() ?? [],
            MayShareMods = NpcMods?.MayShare == true,
            // The relay lists the other members only (never this PC), so one guest is enough.
            InGathering = housing.IsSharedVisit && engine.HousingRelay.HousingMembers.Values.Any(m => m.ContentId != engine.HousingRelay.LocalContentId),
            SharingMods = NpcMods?.SharingNpc,
            ModsNote = NpcMods?.LastNote ?? "",
        };
        model = WithRoster(model);
        var command = NpcPanelUi.Draw(model, ref open, SidePanelUi.Top);
        if (command.Action >= RoomAction.SaveNpcToRoster) { RosterCommand(command, model); return; }
        switch (command.Action)
        {
            case RoomAction.AddNpc when Npcs != null: housing.Run(() => { var said = Npcs.Add(); if (said != housing.Status) throw new InvalidOperationException(said); }); break;
            case >= RoomAction.PossessNpc when command.Index >= 0 && command.Index < housing.Npcs.Count: NpcCommand(command, housing.Npcs[command.Index].Id); break;
        }
    }
    // Whether a look sets advanced colours (read once per look: it's decompressed to tell).
    private readonly Dictionary<string, bool> pinsColours = [];
    private bool PinsColours(string design)
    {
        if (pinsColours.TryGetValue(design, out var pins)) return pins;
        if (pinsColours.Count > 64) pinsColours.Clear();
        return pinsColours[design] = GlamourerStates.PinsParameters(design);
    }

    /// <summary>Set by the plugin: Customize+ profiles, for the NPCs panel.</summary>
    public NpcBody? NpcBodies { get; set; }

    /// <summary>Set by the plugin: sharing NPCs' mods, for the NPCs panel.</summary>
    public NpcModShare? NpcMods { get; set; }

    /// <summary>Set by the plugin: the NPCs spawned in the home, for the NPCs panel.</summary>
    public NpcPossession? Npcs { get; set; }

    private void NpcCommand(RoomCommand command, Guid id)
    {
        switch (command.Action)
        {
            case RoomAction.PossessNpc when Npcs != null:
                if (Npcs.IsPossessed(id)) Npcs.Release(); else Npcs.Possess(id);
                break;
            case RoomAction.SaveNpcSpot when Npcs?.Place(id) is { } place: housing.Run(() => housing.SaveNpcSpot(id, place.Position, place.Yaw, place.Emote)); break;
            case RoomAction.BringNpc when Npcs?.InFront() is { } spot: housing.Run(() => housing.BringNpc(id, spot.Position, spot.Yaw)); break;
            case RoomAction.NpcAway: housing.Run(() => housing.SetNpcAway(id, command.Level > 0)); break;
            case RoomAction.CopyNpcLook:
                housing.Run(() => housing.CopyLookToNpc(id));
                // The owner's copy takes the mods they're drawn with too.
                if (NpcMods?.MayShare == true && NpcMods.Capture(id) is { } notCopied) housing.Run(() => throw new InvalidOperationException(notCopied));
                break;
            case RoomAction.KeepNpcGlamour when Npcs != null:
                var (kept, why) = Npcs.ReadGlamour(id);
                if (kept != null) housing.Run(() => housing.SetNpcLook(id, kept));
                else housing.Run(() => throw new InvalidOperationException(why ?? "Glamourer has no look for that NPC."));
                break;
            case RoomAction.DressNpc when Npcs != null && Guid.TryParse(command.Text, out var design):
                var (look, problem) = Npcs.DressIn(id, design);
                if (look != null) housing.Run(() => housing.SetNpcLook(id, look));
                else housing.Run(() => throw new InvalidOperationException(problem ?? "Glamourer couldn't dress that NPC."));
                break;
            case RoomAction.ResetNpcColours: housing.Run(() => housing.ResetNpcAdvancedColours(id)); break;
            case RoomAction.NpcLevel: housing.Run(() => housing.SetNpcLevel(id, command.Level > 0 ? (byte)command.Level : null)); break;
            case RoomAction.RenameNpc: housing.Run(() => housing.RenameNpc(id, command.Text ?? "")); break;
            case RoomAction.RemoveNpc: housing.Run(() => housing.RemoveNpc(id)); break;
            case RoomAction.ShareNpcMods when NpcMods != null && Guid.TryParse(command.Text, out var collection) && NpcMods.ShareFrom(id, collection) is { } refused:
                housing.Run(() => throw new InvalidOperationException(refused));
                break;
            case RoomAction.SendNpcMods when NpcMods != null: AskToSendNpcMods(id); break;
            case RoomAction.FinishNpcMods: NpcMods?.FinishSharing(); break;
            case RoomAction.ForgetNpcMods: NpcMods?.Forget(id); break;
            case RoomAction.NpcBody when string.IsNullOrEmpty(command.Text): housing.Run(() => housing.SetNpcBody(id, null)); break;
            case RoomAction.NpcBody when NpcBodies != null && Guid.TryParse(command.Text, out var profile):
                var shaped = NpcBodies.Read(profile);
                housing.Run(() => housing.SetNpcBody(id, shaped ?? throw new InvalidOperationException("Customize+ didn't give that profile.")));
                break;
        }
    }

    private void SetFixture(House house, uint part, uint id, bool mainHall)
    {
        if (mainHall) { housing.Run(() => housing.SetInterior(house.Interior with { MainHallCeilingLight = id })); return; }
        var floors = house.Interior.Floors.ToArray(); var original = floors[floor];
        floors[floor] = part switch { 0 => original with { Wallpaper = id }, 3 => original with { Flooring = id }, _ => original with { CeilingLight = id } };
        housing.Run(() => housing.SetInterior(house.Interior with { Floors = floors }));
    }

    // Music --------------------------------------------------------------

    // Custom music: the file picker (Settings' dialog).
    public Action<Action<string>>? PickSong { get; set; }

    private void DrawMusicPanel(ref bool open)
    {
        if (housing.ActiveHouse is not {} house) { open = false; return; }
        var options = house.Music;
        var tracks = engine.HousingTracks;
        if (!ReferenceEquals(trackSource, tracks))
        {
            trackSource = tracks; trackTitles.Clear();
            trackRows = tracks.Select(t => new TrackRow(t.sel, t.title, t.category)).ToArray();
            foreach (var track in trackRows) trackTitles[track.Selection] = track.Title;
            musicFilter = null;
        }
        if (musicFilter != music.Search)
        {
            musicFilter = music.Search;
            musicMatches = trackRows.Where(t => t.Title.Contains(music.Search, StringComparison.CurrentCultureIgnoreCase))
                .Select(t => new TrackChoice(t.Selection, t.Title, t.Label)).ToArray();
        }
        string Title(MusicTrack track) => track.IsCustom ? track.Title ?? "Untitled song" : trackTitles.GetValueOrDefault(MapSettingsService.OrchestrionIdBase + track.OrchestrionId, "Unknown track");
        var slot = housing.MusicSlot;
        var current = slot is {} s && !s.Silent ? s.PlaylistIndex : -1;
        var now = current >= 0 ? Title(options.Playlist[current]) : options.Playing ? "Between tracks" : options.Playlist.Length == 0 ? "Add songs to begin" : "Ready to play";
        var playlist = options.Playlist.Select((t, i) => new PlaylistEntry(Title(t), t.Repeats, i == current, t.IsCustom)).ToArray();
        static string Length(long ms) => $"{ms / 60000}:{ms / 1000 % 60:00}";
        var model = new MusicPanelModel(housing.CanControlMusic, housing.MusicReady, now, options.Playing, playlist, musicMatches,
            options.Loop, options.Shuffle, options.GapSeconds, housing.MusicStatus ?? "")
        {
            Songs = music.Tab == 2 ? [.. housing.Songs.Library.Select(s => new SongChoice(s.Hash, s.Song.Title,
                $"{Length(s.Song.Milliseconds)} · {(s.Song.FromCommunity ? "from the former gallery" : "added " + s.Song.Added.LocalDateTime.ToString("d"))}",
                s.Song.FromCommunity, s.Song.Stored != null))] : [],
            Converting = housing.SongImport,
            NeedsAgreement = housing.OwnsActiveHouse && !housing.AgreementAccepted,
        };
        var command = MusicPanelUi.Draw(music, model, ref open, SidePanelUi.Top);
        MusicTrack[] List() => options.Playlist.ToArray();
        switch (command.Action)
        {
            case MusicAction.Play: housing.Run(() => housing.SetMusic(options with { Playing = true, StartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() })); break;
            case MusicAction.Stop: housing.Run(() => housing.SetMusic(options with { Playing = false })); break;
            case MusicAction.Add when options.Playlist.Length < MusicPanelUi.MaxTracks:
                housing.Run(() => housing.SetMusic(options with { Playlist = [.. options.Playlist, new MusicTrack { OrchestrionId = command.Track - MapSettingsService.OrchestrionIdBase }] }));
                break;
            case MusicAction.Remove: housing.Run(() => housing.SetMusic(options with { Playlist = options.Playlist.Where((_, n) => n != command.Index).ToArray() })); break;
            case MusicAction.MoveUp when command.Index > 0:
            { var list = List(); (list[command.Index], list[command.Index - 1]) = (list[command.Index - 1], list[command.Index]); housing.Run(() => housing.SetMusic(options with { Playlist = list })); break; }
            case MusicAction.MoveDown when command.Index < options.Playlist.Length - 1:
            { var list = List(); (list[command.Index], list[command.Index + 1]) = (list[command.Index + 1], list[command.Index]); housing.Run(() => housing.SetMusic(options with { Playlist = list })); break; }
            case MusicAction.Repeats:
            { var list = List(); list[command.Index] = list[command.Index] with { Repeats = command.Value }; housing.Run(() => housing.SetMusic(options with { Playlist = list })); break; }
            case MusicAction.Loop: housing.Run(() => housing.SetMusic(options with { Loop = command.Value != 0 })); break;
            case MusicAction.Shuffle: housing.Run(() => housing.SetMusic(options with { Shuffle = command.Value != 0 })); break;
            case MusicAction.Gap: housing.Run(() => housing.SetMusic(options with { GapSeconds = command.Value })); break;
            case MusicAction.AddSongFile: PickSong?.Invoke(path => { music.NewSong = path; music.NewSongName = Path.GetFileNameWithoutExtension(path); }); break;
            case MusicAction.ImportSong: housing.Run(() => housing.ImportSong(command.Song, command.Text)); break;
            case MusicAction.AddSong: housing.Run(() => housing.AddSongToPlaylist(command.Song)); break;
            case MusicAction.RenameSong: housing.Run(() => housing.RenameSong(command.Song, command.Text)); break;
            case MusicAction.DeleteSong: housing.Run(() => housing.DeleteSong(command.Song, command.Value != 0)); break;
            case MusicAction.AgreeAndAddSong: housing.Run(() => { housing.AcceptAgreement(); housing.AddSongToPlaylist(command.Song); }); break;
            case MusicAction.CancelSong: housing.CancelSongImport(); break;
        }
    }

    // Visit --------------------------------------------------------------

    private void DrawVisitPanel(ref bool open)
    {
        var relay = engine.HousingRelay; var active = housing.ActiveHouse;
        if (active == null && !housing.CanStopVisit) { open = false; return; }
        var hosting = relay.IsHost && !relay.SoloMode;
        var mode = relay.SoloMode ? VisitMode.Solo : relay.IsConnecting && active == null ? VisitMode.Connecting : hosting ? VisitMode.Hosting : VisitMode.Guest;
        var title = mode switch
        {
            VisitMode.Solo => "Private visit", VisitMode.Connecting => "Connecting…",
            VisitMode.Hosting => "Hosting " + active?.Name, _ => "Visiting " + (housing.VisitorHouse?.Name ?? active?.Name),
        };
        var detail = mode switch
        {
            VisitMode.Solo => active?.Name ?? "", VisitMode.Connecting => "Meet your friends in the same zone, within sight.",
            _ => relay.HousingSceneReady ? "Shared home ready" : "Preparing the shared home…",
        };
        var access = relay.Editing;
        var members = relay.HousingMembers.ToArray().OrderBy(m => m.Value.Name).Select(m => new VisitMember(m.Value.ContentId, m.Value.Name,
            access == null ? "Guest" : m.Value.ContentId == access.Owner ? "Owner" : access.Enabled && access.Editors.Contains(m.Value.ContentId) ? "Decorator" : "Guest",
            !relay.PeerConnections.TryGetValue(m.Key, out var online) || online, access != null && m.Value.ContentId == access.Owner)).ToArray();
        var canDecorate = housing.IsSharedVisit && access != null && access.CanEdit(relay.LocalContentId);
        var status = housing.EditStatus ?? "";
        if (access is { Protocol: < 2 }) status = "Automatic staging and review need the updated relay. Your draft is kept. " + status;
        var proposals = housing.EditProposals.Select(p =>
        {
            var furniture = p.Batch.Changes.Count(c => c.Path[0] == "furnishings");
            return new VisitProposal(p.Batch.Id, p.Name, VisitPanelUi.ProposalSummary(furniture, p.Batch.Changes.Length - furniture));
        }).ToArray();
        var leave = housing.EndingVisit ? "Ending…" : hosting ? "Stop hosting" : relay.IsConnecting && active == null ? "Cancel" : "Leave home";
        var model = new VisitPanelModel(mode, title, detail, hosting ? housing.VisitCode : "", members, housing.OwnsActiveHouse, canDecorate,
            access?.Enabled ?? false, access?.Editors ?? [], housing.OwnsActiveHouse && housing.IsSharedVisit && access != null && !relay.IsRecovering,
            housing.HasSharedDraft, housing.HasArchivedDraft, housing.CanSaveShared, housing.SaveLabel, housing.EditBusy, housing.DraftConflict,
            housing.OwnsActiveHouse ? proposals : [], housing.CanReview, status.Trim(), relay.IsRecovering ? relay.RecoveryStatus : "", relay.RecoveryBlocked,
            housing.VisitorHouse != null, housing.CanRetryEntry, housing.EndingVisit, leave,
            housing.HomePictures.Select(h => new VisitPicture(h, housing.PictureHidden(h), PictureThumb(h) is {} t ? t.Width / (float)t.Height : 5 / 3f)).ToArray(), housing.CustomPicturesLoaded,
            h => PictureThumb(h)?.Wrap.Handle) { Songs = housing.HomeSongs.Count, Furniture = housing.HomeFurniture.Count, Animations = housing.HomeAnimations, AutoPublish = housing.AutoPublishing,
              Invites = hosting && housing.OwnsActiveHouse && Invites is { Hosting: true } invites ? new(invites.AutoInvite, invites.InviteOnly, invites.LockKept, invites.Friends) : null };
        var command = VisitPanelUi.Draw(visit, model, ref open, SidePanelUi.Top);
        switch (command.Action)
        {
            case VisitAction.CopyCode: ImGui.SetClipboardText(housing.VisitCode); break;
            case VisitAction.Leave: housing.Run(housing.StopVisit); break;
            case VisitAction.SaveCopy: housing.Run(housing.SaveVisitorCopy); break;
            case VisitAction.RetryEntry: housing.Run(housing.RetryHomeEntry); break;
            case VisitAction.RetryRecovery: relay.RetryRecovery(); break;
            case VisitAction.Publish: housing.Run(housing.SaveSharedChanges); break;
            case VisitAction.Discard: housing.Run(housing.DiscardSharedDraft); break;
            case VisitAction.RestoreDraft: housing.Run(housing.RestoreSharedDraft); break;
            case VisitAction.KeepDraft: housing.Run(housing.KeepDraftValues); break;
            case VisitAction.ApproveAll: housing.Run(() => housing.ReviewAllProposals()); break;
            case VisitAction.RejectAll: housing.Run(() => housing.ReviewAllProposals(false)); break;
            case VisitAction.Inspect: InspectProposal(command.Proposal); break;
            case VisitAction.Approve: housing.Run(() => housing.ReviewProposal(command.Proposal, true)); break;
            case VisitAction.Decline: housing.Run(() => housing.ReviewProposal(command.Proposal, false)); break;
            case VisitAction.ApplyDecorators: housing.Run(() => housing.SetDecorators(command.Enabled, command.Decorators ?? [])); break;
            case VisitAction.LoadPictures: housing.Run(() => housing.DecideCustomPictures(command.Enabled)); break;
            case VisitAction.HidePicture: housing.Run(() => housing.HidePicture(command.Picture!, command.Enabled)); break;
            case VisitAction.AutoInvite: Invites?.SetAutoInvite(command.Enabled); break;
            case VisitAction.InviteOnly: Invites?.SetInviteOnly(command.Enabled); break;
            case VisitAction.Invite: Invites?.Invite(command.Friend); break;
            case VisitAction.Uninvite: Invites?.Uninvite(command.Friend); break;
        }
    }
}
