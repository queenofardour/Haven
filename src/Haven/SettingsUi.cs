using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

using Haven.Core;

namespace Haven;

internal readonly record struct SettingsHome(Guid Id, string Name);
// Settings → Custom content: one of the owner's custom pictures or songs, and the allowance the locker last reported
// (one allowance for both).
internal sealed record SettingsPicture(string Hash, long Size, float Aspect, string Title, string Detail, string[] Homes);
internal sealed record SettingsSong(string Hash, string Title, string Detail, string[] Homes, bool Stored);
// One of the player's custom furnishings: its name, what it is built on, where it is placed.
internal sealed record SettingsFurniture(string Hash, string Name, string Detail, string[] Homes);
/// <summary>
/// What this PC keeps of custom content downloaded for other people's homes (not used by any home here), the most it
/// keeps, and the built picture frames; whether a home is open (clearing waits until the player leaves it).
/// </summary>
internal sealed record SettingsStorage(long Downloaded, int Items, long Limit, long Frames, bool InHome, bool Clearing);
/// <summary>The mods this PC keeps for one NPC: its name, size, where it is used (homes, Your NPCs) or where it came from.</summary>
internal sealed record SettingsNpcMods(Guid Npc, string Name, string Detail, string Where, string[] Homes, bool Saved);
internal sealed record SettingsUsage(long OwnerBytes, long OwnerLimit, int UploadsToday, int UploadsPerDay, long PoolBytes, long PoolLimit, DateTimeOffset At);
internal sealed record SettingsModel(bool Effects, bool Sounds, int MaxEffects, int MaxSounds, IReadOnlyList<SettingsHome> Homes,
    string HomesFolder, string DeletedFolder, string Relay, string HostedRelay, string HostCode, bool CanChangeConnection,
    bool SharedVisit,
    IReadOnlyList<string> Diagnostics, string Status, string Version)
{
    public ChatPreferences Chat { get; init; } = new(true, true, true, true);
    /// <summary>Whether importing a minion prop shows the experimental warning first.</summary>
    public bool WarnMinionProps { get; init; } = true;
    /// <summary>Whether tilting a chair or bed warns that it can't be sat on then.</summary>
    public bool WarnTiltedSeats { get; init; } = true;
    // Saved layouts (real houses and MakePlace files), one file each.
    public string LayoutsFolder { get; init; } = "";
    public string GroupsFolder { get; init; } = "";
    public IReadOnlyList<SettingsPicture> Pictures { get; init; } = [];
    public IReadOnlyList<SettingsSong> Songs { get; init; } = [];
    public IReadOnlyList<SettingsFurniture> Furniture { get; init; } = [];
    public IReadOnlyList<SettingsFurniture> Animations { get; init; } = [];
    /// <summary>Props of animations installed in Penumbra by Haven (their names), and how many choices about props are remembered.</summary>
    public IReadOnlyList<string> AnimationProps { get; init; } = [];
    public int PropChoices { get; init; }
    public SettingsUsage? Usage { get; init; }
    public SettingsStorage? Storage { get; init; }
    public IReadOnlyList<SettingsNpcMods> NpcMods { get; init; } = [];
    public Func<string, ImTextureID?>? Thumbnail { get; init; }
    public SettingsAccount Account { get; init; } = new(false, "", false, null);
    public SettingsPatch Patch { get; init; } = new(true, false, [], []);
    /// <summary>Whether Haven draws furnishing lights itself (instead of Stagehand), and how many it draws now (all, world pieces').</summary>
    public bool HavenLights { get; init; }
    public (int All, int World) HavenLightCount { get; init; }
    /// <summary>Whether Haven draws furnishing effects itself (instead of Stagehand), and how many it draws now.</summary>
    public bool HavenEffects { get; init; }
    public int HavenEffectCount { get; init; }
    /// <summary>Whether Haven plays furnishing sounds itself (instead of Stagehand), and how many it plays now.</summary>
    public bool HavenSounds { get; init; }
    public int HavenSoundCount { get; init; }
}
/// <summary>
/// After a game update: whether the game is the version this Haven was checked on, whether the player turned paused
/// features on for it, what's paused, and the self-check's lines.
/// </summary>
internal sealed record SettingsPatch(bool Checked, bool AllowedHere, IReadOnlyList<string> Paused, IReadOnlyList<string> Lines);
/// <summary>This PC's Haven account (Discord sign-in).</summary>
internal sealed record SettingsAccount(bool SignedIn, string Name, bool SigningIn, string? Problem, string? Code = null);
/// <summary>Room chat preferences: the [Haven] sender marker, emote log lines and typing bubbles.</summary>
public readonly record struct ChatPreferences(bool Tag, bool EmoteMessages, bool ShareTyping, bool ShowTyping, WorldChannels World = WorldChannels.All);

internal enum SettingsAction { None, Chat, Ambience, Import, Export, OpenHomes, OpenDeleted, OpenLayouts, OpenGroups, SaveRelay, UseHostedRelay, SaveCode, NewCode, CopyCode,
    Close, DeletePicture, DeleteUnusedPictures, DeleteSong, DeleteFurniture, DeleteAnimations,
    SignIn, SignOut, CancelSignIn, WarnMinionProps, RemoveProps, ForgetPropChoices, Changelog, WarnTiltedSeats, PatchAllow,
    DownloadLimit, ClearDownloads, RemoveNpcMods, HavenLights, HavenEffects, HavenSounds }
internal readonly record struct SettingsCommand(SettingsAction Action, bool First = false, bool Second = false, Guid Home = default, string Text = "")
{
    public ChatPreferences Chat { get; init; }
}

internal sealed class SettingsState
{
    public int Tab;
    public Guid? Home;
    public string? RelayText, CodeText;
    // Pictures: the one being deleted, while its dialog is open.
    public string? Deleting;
    // The delete dialog's "also from Haven's server" choice.
    public bool DeleteFromServer = true;
    // Custom content: 0 pictures, 1 music, 2 furniture, 3 animations, 4 NPC mods.
    public int Content;
    // NPC mods: the NPC whose mods are being removed, while its dialog is open.
    public Guid RemovingNpc;
}

// Sizes of custom content, as Settings and the panels show them.
internal static class ContentSizes
{
    // Decimal units, as the server's limits are set and Cloudflare counts them (the shared pool is 10,000,000,000 bytes:
    // 10 GB, which binary units showed as 9.31).
    public static string Megabytes(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.##} GB"
        : bytes >= 1_000_000 ? $"{bytes / 1e6:0.#} MB" : $"{Math.Max(1, bytes / 1000)} KB";
}

// Haven's Settings, replacing the studio window: furnishing effects, files (import,
// export, folders), connection (relay and hosting code), and the advanced Stagehand bridge and diagnostics.
internal static class SettingsUi
{
    public static readonly string[] Tabs = ["General", "Files", "Custom content", "Connection", "Advanced"];

    public static SettingsCommand Draw(SettingsState state, SettingsModel model)
    {
        var command = default(SettingsCommand);
        var start = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        using (HavenFonts.Use(HavenText.Title)) draw.AddText(start + Px(0, 6), HavenUi.U(Text), "Settings");
        ImGui.SetCursorScreenPos(new(start.X + ImGui.GetContentRegionAvail().X - Px(ControlHeight), start.Y));
        if (HavenKit.IconButton("##settings-close", HavenIcon.Close, "Close")) command = new(SettingsAction.Close);
        ImGui.SetCursorScreenPos(start + new Vector2(0, Px(ControlHeight + Space2)));
        var tab = state.Tab;
        if (HavenKit.Segmented("settings-tab", Tabs, ref tab)) state.Tab = tab;
        ImGui.Dummy(Px(0, Space2));
        var footer = model.Status.Length > 0 ? Px(Space6) : 0;
        if (HavenKit.BeginScrollBody("##settings-body", new(0, -footer)))
        {
            var result = state.Tab switch { 1 => Files(state, model), 2 => Pictures(state, model), 3 => Connection(state, model), 4 => Advanced(state, model), _ => General(model) };
            if (result.Action != SettingsAction.None) command = result;
        }
        ImGui.EndChild();
        if (footer > 0) HavenKit.Caption(model.Status);
        return command;
    }

    private static SettingsCommand General(SettingsModel model)
    {
        var command = default(SettingsCommand);
        HavenKit.SectionTitle("Haven", model.Version);
        if (HavenKit.Button("What's new##settings-changelog", ButtonKind.Secondary)) command = new(SettingsAction.Changelog);
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Furnishing effects");
        bool effects = model.Effects, sounds = model.Sounds;
        if (HavenKit.Toggle("Flames, steam, water and other effects", ref effects)) command = new(SettingsAction.Ambience, effects, sounds);
        if (HavenKit.Toggle("Ambient furnishing sounds", ref sounds)) command = new(SettingsAction.Ambience, effects, sounds);
        HavenKit.Caption($"These apply only on this PC. Up to {model.MaxEffects} effects and {model.MaxSounds} sounds play per home.");
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Room chat");
        var chat = model.Chat;
        bool tag = chat.Tag, emotes = chat.EmoteMessages, share = chat.ShareTyping, show = chat.ShowTyping;
        if (HavenKit.Toggle("Mark room chat with [Haven]", ref tag)) command = new(SettingsAction.Chat) { Chat = chat with { Tag = tag } };
        if (HavenKit.Toggle("Share emote messages in room chat", ref emotes)) command = new(SettingsAction.Chat) { Chat = chat with { EmoteMessages = emotes } };
        HavenKit.Caption("When the emote window's \"Display log message\" is on, your emotes' lines (\"You blow Aria a kiss.\") show in the room as they do in the game.");
        if (HavenKit.Toggle("Show others when I'm typing", ref share)) command = new(SettingsAction.Chat) { Chat = chat with { ShareTyping = share } };
        if (HavenKit.Toggle("Show typing bubbles over others", ref show)) command = new(SettingsAction.Chat) { Chat = chat with { ShowTyping = show } };
        HavenKit.Caption("Commands starting with / show no bubble, except room chat such as /em, /say or /yell followed by a space.");
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Real-world chat in a home");
        foreach (var (channel, label) in new[] { (WorldChannels.Tells, "Tells"), (WorldChannels.Party, "Party"), (WorldChannels.FreeCompany, "Free company"),
            (WorldChannels.Linkshells, "Linkshells"), (WorldChannels.CrossLinkshells, "Cross-world linkshells") })
        {
            var on = chat.World.HasFlag(channel);
            if (HavenKit.Toggle(label + "##world-" + channel, ref on)) command = new(SettingsAction.Chat) { Chat = chat with { World = on ? chat.World | channel : chat.World & ~channel } };
        }
        HavenKit.Caption("These reach the real world and come back as usual while you're inside. Say, shout, yell and emotes stay in the room, heard by everyone in it; the Novice Network stays blocked.");
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Interface");
        HavenKit.Caption("Haven follows Dalamud's global font scale (Dalamud Settings → Look & Feel).");
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("About", model.Version);
        HavenKit.Caption("Haven is a development alpha. Your homes are saved on this PC; visits use the Haven relay.");
        return command;
    }

    // The owner's custom content: the allowance the locker last reported (pictures and music share it), then their
    // pictures (originals, each framed per frame), their songs or their furniture.
    private static SettingsCommand Pictures(SettingsState state, SettingsModel model)
    {
        var command = default(SettingsCommand);
        HavenKit.SectionTitle("Your allowance", model.Usage is {} reported ? $"as of {reported.At.ToLocalTime():d MMM, HH:mm}" : "");
        if (model.Usage is not {} usage)
            HavenKit.Caption("Haven's server reports your allowance whenever you upload or delete custom content. Upload something to see it here.");
        else
        {
            Meter($"Stored in the last 30 days: {ContentSizes.Megabytes(usage.OwnerBytes)} of {ContentSizes.Megabytes(usage.OwnerLimit)}", usage.OwnerBytes, usage.OwnerLimit);
            Meter($"Uploads today: {usage.UploadsToday} of {usage.UploadsPerDay}", usage.UploadsToday, usage.UploadsPerDay);
            Meter($"Haven's shared storage, for everyone's content: {ContentSizes.Megabytes(usage.PoolBytes)} of {ContentSizes.Megabytes(usage.PoolLimit)}", usage.PoolBytes, usage.PoolLimit);
            HavenKit.Caption("Pictures, songs, furniture and animations share this allowance. Each counts once, however many frames, playlists or homes use it. Deleting something from Haven's server gives its space back, but not the day's upload.");
        }
        ImGui.Dummy(Px(0, Space3));
        var content = state.Content;
        if (HavenKit.Segmented("settings-content", [$"Pictures · {model.Pictures.Count}", $"Music · {model.Songs.Count}", $"Furniture · {model.Furniture.Count}",
            $"Animations · {model.Animations.Count}", $"NPC mods · {model.NpcMods.Count}"], ref content)) state.Content = content;
        ImGui.Dummy(Px(0, Space1));
        var chosen = state.Content switch { 1 => Music(state, model), 2 => Furniture(state, model), 3 => Animations(state, model), 4 => NpcMods(state, model), _ => PictureList(state, model) };
        if (chosen.Action != SettingsAction.None) command = chosen;
        // What this PC keeps, below whichever list is shown.
        ImGui.Dummy(Px(0, Space4));
        if (OnThisPc(model) is { Action: not SettingsAction.None } stored) command = stored;
        return command;
    }

    // The owner's pictures (originals, each framed per frame).
    private static SettingsCommand PictureList(SettingsState state, SettingsModel model)
    {
        var command = default(SettingsCommand);
        var total = model.Pictures.Sum(p => p.Size);
        HavenKit.SectionTitle("Your pictures", model.Pictures.Count == 0 ? "" : $"{model.Pictures.Count} · {ContentSizes.Megabytes(total)} on this PC");
        HavenKit.Caption(model.Pictures.Count == 0 ? "Pictures you upload appear here, ready to hang in any frame."
            : "Each picture is uploaded once, however many frames show it. To change how a frame shows its picture, select the frame and press Adjust.");
        var unused = model.Pictures.Count(p => p.Homes.Length == 0);
        if (unused > 0)
        {
            if (HavenKit.Button(unused == 1 ? "Delete 1 unused picture…" : $"Delete {unused} unused pictures…", ButtonKind.Secondary)) ImGui.OpenPopup("##settings-delete-unused");
            if (HavenKit.BeginDialog("##settings-delete-unused", "Delete unused pictures?", 460))
            {
                HavenKit.Caption($"Deletes the {(unused == 1 ? "picture" : $"{unused} pictures")} that no home on this PC shows.");
                HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Delete") + Px(Space2));
                if (HavenKit.Button("Keep##delete-unused", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Delete##delete-unused", ButtonKind.Danger)) { command = new(SettingsAction.DeleteUnusedPictures); ImGui.CloseCurrentPopup(); }
                HavenKit.EndDialog();
            }
        }
        foreach (var picture in model.Pictures)
            if (PictureRow(state, model, picture, "Delete…", ButtonKind.Ghost) && command.Action == SettingsAction.None)
            { state.Deleting = picture.Hash; ImGui.OpenPopup("##settings-delete-" + picture.Hash); }
        foreach (var picture in model.Pictures)
            if (DeleteDialog(state, picture.Hash) is { Action: not SettingsAction.None } deleted) command = deleted;
        ImGui.Dummy(Px(0, Space2));
        HavenKit.Caption("Pictures no gathering uses expire from Haven's server after 30 days; they stay on this PC until you delete them.");
        return command;
    }

    // Content downloaded for other people's homes: how much this PC keeps, the most it keeps, and clearing it.
    private static readonly string[] LimitNames = [.. ContentCache.Limits.Select(ContentSizes.Megabytes)];
    private static SettingsCommand OnThisPc(SettingsModel model)
    {
        var command = default(SettingsCommand);
        if (model.Storage is not { } storage) return command;
        HavenKit.SectionTitle("On this PC", storage.Frames > 0 ? $"{ContentSizes.Megabytes(storage.Frames)} of built picture frames" : "");
        Meter(storage.Items == 0 ? "Nothing downloaded for other people's homes is kept here."
            : $"Downloaded for other people's homes: {ContentSizes.Megabytes(storage.Downloaded)} of {ContentSizes.Megabytes(storage.Limit)} kept ({storage.Items} item{(storage.Items == 1 ? "" : "s")})",
            storage.Downloaded, storage.Limit);
        var limit = Array.IndexOf(ContentCache.Limits, storage.Limit);
        if (HavenKit.Select("##settings-download-limit", "Keep at most", LimitNames, ref limit, Px(160)) && limit >= 0)
            command = new(SettingsAction.DownloadLimit, Text: ContentCache.Limits[limit].ToString(System.Globalization.CultureInfo.InvariantCulture));
        ImGui.SameLine(0, Px(Space2));
        ImGui.BeginDisabled(storage.InHome || storage.Clearing || storage.Items == 0);
        if (HavenKit.Button(storage.Clearing ? "Clearing…##settings-clear-downloads" : "Clear downloaded content…##settings-clear-downloads", ButtonKind.Secondary)) ImGui.OpenPopup("##settings-clear-downloads");
        ImGui.EndDisabled();
        if (storage.InHome && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) HavenKit.Tooltip("Leave the home first.");
        if (HavenKit.BeginDialog("##settings-clear-downloads", "Clear downloaded content?", 460))
        {
            HavenKit.Caption($"Deletes the {ContentSizes.Megabytes(storage.Downloaded)} of pictures, furniture and animations downloaded for other people's homes. Haven downloads them again the next time you visit a home that has them.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Clear") + Px(Space2));
            if (HavenKit.Button("Keep##clear-downloads", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Clear##clear-downloads", ButtonKind.Danger)) { command = new(SettingsAction.ClearDownloads); ImGui.CloseCurrentPopup(); }
            HavenKit.EndDialog();
        }
        HavenKit.Caption("Past the limit, what a home used longest ago is cleared when Haven starts, and downloaded again on your next visit. Your own content, and anything a home or saved group on this PC uses, is never cleared.");
        return command;
    }

    // The mods this PC keeps for NPCs: the player's own NPCs', those of Your NPCs, and those other people's homes sent.
    private static SettingsCommand NpcMods(SettingsState state, SettingsModel model)
    {
        var command = default(SettingsCommand);
        var inHome = model.Storage?.InHome ?? false;
        HavenKit.SectionTitle("NPC mods", model.NpcMods.Count == 0 ? "" : $"{model.NpcMods.Count}");
        HavenKit.Caption(model.NpcMods.Count == 0 ? "Mods you keep for an NPC (its Mods tab in the NPCs panel), or keep when a friend sends their NPC's, appear here."
            : "The mods this PC keeps for NPCs: yours, and those friends sent you for theirs. Removing them here doesn't tell anyone else; an NPC without them is drawn in its Glamourer look only.");
        var unused = model.NpcMods.Where(n => n.Homes.Length == 0 && !n.Saved).ToArray();
        if (unused.Length > 0)
        {
            ImGui.BeginDisabled(inHome);
            if (HavenKit.Button(unused.Length == 1 ? "Remove 1 not used here…" : $"Remove {unused.Length} not used here…", ButtonKind.Secondary)) ImGui.OpenPopup("##settings-remove-unused-npcs");
            ImGui.EndDisabled();
            if (inHome && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) HavenKit.Tooltip("Leave the home first.");
            if (HavenKit.BeginDialog("##settings-remove-unused-npcs", "Remove NPC mods not used here?", 460))
            {
                HavenKit.Caption($"Removes the mods kept for {(unused.Length == 1 ? "1 NPC" : $"{unused.Length} NPCs")} that are in no home on this PC and not in Your NPCs. Some may be friends' NPCs: their owner can send them again.");
                HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Remove") + Px(Space2));
                if (HavenKit.Button("Keep##remove-unused-npcs", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Remove##remove-unused-npcs", ButtonKind.Danger))
                { command = new(SettingsAction.RemoveNpcMods, Text: string.Join(",", unused.Select(n => n.Npc.ToString("N")))); ImGui.CloseCurrentPopup(); }
                HavenKit.EndDialog();
            }
        }
        ImGui.Dummy(Px(0, Space1));
        foreach (var item in model.NpcMods)
        {
            ImGui.PushID("npc-mods-" + item.Npc.ToString("N"));
            using (HavenFonts.Use(HavenText.Strong)) ImGui.TextUnformatted(item.Name);
            using (HavenFonts.Use(HavenText.Small))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
                ImGui.TextUnformatted(item.Detail);
                ImGui.TextUnformatted(item.Where);
                ImGui.PopStyleColor();
            }
            var inUse = item.Homes.Length > 0;
            ImGui.BeginDisabled(inHome || inUse);
            if (HavenKit.Button("Remove…", ButtonKind.Ghost)) { state.RemovingNpc = item.Npc; ImGui.OpenPopup("##settings-remove-npc-mods"); }
            ImGui.EndDisabled();
            if ((inHome || inUse) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                HavenKit.Tooltip(inUse ? "To remove them, use Forget in the NPC's Mods tab in that home." : "Leave the home first.");
            if (state.RemovingNpc == item.Npc && HavenKit.BeginDialog("##settings-remove-npc-mods", "Remove these NPC mods?", 460))
            {
                HavenKit.Caption(item.Saved ? $"{item.Name} in Your NPCs loses its mods, and NPCs you add from it later start without them."
                    : "They are deleted from this PC. If this is a friend's NPC, they can send its mods again.");
                HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Remove") + Px(Space2));
                if (HavenKit.Button("Keep##settings-remove-npc-mods", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Remove##settings-remove-npc-mods", ButtonKind.Danger)) { command = new(SettingsAction.RemoveNpcMods, Text: item.Npc.ToString("N")); ImGui.CloseCurrentPopup(); }
                HavenKit.EndDialog();
            }
            ImGui.PopID();
            ImGui.Dummy(Px(0, Space2));
        }
        return command;
    }

    // The owner's songs: where each plays, and deleting (from Haven's server too, when asked).
    private static SettingsCommand Music(SettingsState state, SettingsModel model)
    {
        var command = default(SettingsCommand);
        HavenKit.SectionTitle("Your music", model.Songs.Count == 0 ? "" : $"{model.Songs.Count} songs");
        HavenKit.Caption(model.Songs.Count == 0 ? "Songs you add in a home's Music panel appear here." : "Add songs to a playlist from the Music panel in a home.");
        foreach (var song in model.Songs)
        {
            ImGui.PushID("song-" + song.Hash);
            using (HavenFonts.Use(HavenText.Strong)) ImGui.TextUnformatted(song.Title);
            using (HavenFonts.Use(HavenText.Small))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
                ImGui.TextUnformatted(song.Detail);
                ImGui.TextUnformatted(song.Homes.Length == 0 ? "Not in any playlist" : "Plays in " + string.Join(", ", song.Homes));
                ImGui.PopStyleColor();
            }
            if (HavenKit.Button("Delete…", ButtonKind.Ghost)) { state.Deleting = song.Hash; state.DeleteFromServer = true; ImGui.OpenPopup("##settings-delete-song"); }
            if (state.Deleting == song.Hash && HavenKit.BeginDialog("##settings-delete-song", "Delete this song?", 460))
            {
                HavenKit.Caption("It is deleted from this PC. Haven refuses while a home on this PC plays it.");
                if (song.Stored)
                {
                    HavenKit.Toggle("Also delete it from Haven's server now", ref state.DeleteFromServer);
                    HavenKit.Caption(state.DeleteFromServer ? "Guests can no longer download it. Only the character that uploaded it can do this."
                        : "The server's copy is deleted by itself after 30 days without use.");
                }
                HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Delete") + Px(Space2));
                if (HavenKit.Button("Keep##settings-delete-song", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Delete##settings-delete-song", ButtonKind.Danger))
                { command = new(SettingsAction.DeleteSong, First: song.Stored && state.DeleteFromServer, Text: song.Hash); ImGui.CloseCurrentPopup(); }
                HavenKit.EndDialog();
            }
            ImGui.PopID();
            ImGui.Dummy(Px(0, Space2));
        }
        ImGui.Dummy(Px(0, Space2));
        HavenKit.Caption("Songs no gathering uses expire from Haven's server after 30 days; they stay on this PC until you delete them.");
        return command;
    }

    // The player's animation packages for pose balls: where they play, and deleting them.
    private static SettingsCommand Animations(SettingsState state, SettingsModel model)
    {
        var command = default(SettingsCommand);
        HavenKit.SectionTitle("Your animations", model.Animations.Count == 0 ? "" : $"{model.Animations.Count}");
        HavenKit.Caption(model.Animations.Count == 0 ? "Animations you import from a Penumbra animation mod for pose balls (a furnishing's inspector → Pose balls) appear here."
            : "Animations you imported for pose balls. Guests who load your home's custom content receive the ones its balls play.");
        ImGui.Dummy(Px(0, Space1));
        foreach (var item in model.Animations)
        {
            ImGui.PushID("animations-" + item.Hash);
            using (HavenFonts.Use(HavenText.Strong)) ImGui.TextUnformatted(item.Name);
            using (HavenFonts.Use(HavenText.Small))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
                ImGui.TextUnformatted(item.Detail);
                ImGui.TextUnformatted(item.Homes.Length == 0 ? "Not used in any home" : "Plays in " + string.Join(", ", item.Homes));
                ImGui.PopStyleColor();
            }
            ImGui.BeginDisabled(item.Homes.Length > 0);
            if (HavenKit.Button("Delete…", ButtonKind.Ghost)) { state.Deleting = item.Hash; ImGui.OpenPopup("##settings-delete-animations"); }
            ImGui.EndDisabled();
            if (item.Homes.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) HavenKit.Tooltip("Remove the pose balls that play them first.");
            if (state.Deleting == item.Hash && HavenKit.BeginDialog("##settings-delete-animations", "Delete these animations?", 460))
            {
                HavenKit.Caption("They are deleted from this PC and Haven's server. The mod they came from is not touched.");
                HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Delete") + Px(Space2));
                if (HavenKit.Button("Keep##settings-delete-animations", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Delete##settings-delete-animations", ButtonKind.Danger)) { command = new(SettingsAction.DeleteAnimations, Text: item.Hash); ImGui.CloseCurrentPopup(); }
                HavenKit.EndDialog();
            }
            ImGui.PopID();
            ImGui.Dummy(Px(0, Space2));
        }
        // Props: some animations come with a piece of gear, installed in Penumbra and worn only while they play.
        if (model.AnimationProps.Count > 0 || model.PropChoices > 0)
        {
            ImGui.Dummy(Px(0, Space2));
            HavenKit.SectionTitle("Animation props", model.AnimationProps.Count == 0 ? "" : $"{model.AnimationProps.Count}");
            HavenKit.Caption("Some animations use a piece of gear as their prop. Haven installs it in Penumbra's \"Haven Animation Props\" folder and wears it only while the animation plays. "
                + (model.AnimationProps.Count == 0 ? "None are installed." : "Installed: " + string.Join(", ", model.AnimationProps) + "."));
            if (model.AnimationProps.Count > 0)
            {
                if (HavenKit.Button("Remove them from Penumbra", ButtonKind.Ghost)) command = new(SettingsAction.RemoveProps);
                if (ImGui.IsItemHovered()) HavenKit.Tooltip("They are installed again the next time you wear one.");
                ImGui.SameLine(0, Px(Space2));
            }
            ImGui.BeginDisabled(model.PropChoices == 0);
            if (HavenKit.Button($"Ask again ({model.PropChoices} remembered)", ButtonKind.Ghost)) command = new(SettingsAction.ForgetPropChoices);
            ImGui.EndDisabled();
        }
        return command;
    }

    // The player's custom furniture: what each is built on, where it is placed, and deleting it.
    private static SettingsCommand Furniture(SettingsState state, SettingsModel model)
    {
        var command = default(SettingsCommand);
        HavenKit.SectionTitle("Your furniture", model.Furniture.Count == 0 ? "" : $"{model.Furniture.Count}");
        HavenKit.Caption(model.Furniture.Count == 0 ? "Furniture you import from a Penumbra mod (Furnish → Yours) appears here." : "Import and place furniture from Furnish → Yours in a home. Deleting a piece removes it from every home it is placed in.");
        var warn = model.WarnMinionProps;
        if (HavenKit.Toggle("Warn before importing a minion prop", ref warn)) command = new(SettingsAction.WarnMinionProps, warn);
        HavenKit.Caption("Turning a minion mod into furniture is experimental; the warning explains what can go wrong.");
        var tilt = model.WarnTiltedSeats;
        if (HavenKit.Toggle("Warn when tilting a chair or bed", ref tilt)) command = new(SettingsAction.WarnTiltedSeats, tilt);
        HavenKit.Caption("A tilted seat or bed can't be sat or lain on.");
        ImGui.Dummy(Px(0, Space1));
        foreach (var item in model.Furniture)
        {
            ImGui.PushID("furniture-" + item.Hash);
            using (HavenFonts.Use(HavenText.Strong)) ImGui.TextUnformatted(item.Name);
            using (HavenFonts.Use(HavenText.Small))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
                ImGui.TextUnformatted(item.Detail);
                ImGui.TextUnformatted(item.Homes.Length == 0 ? "Not placed in any home" : "Placed in " + string.Join(", ", item.Homes));
                ImGui.PopStyleColor();
            }
            if (HavenKit.Button("Delete…", ButtonKind.Ghost)) { state.Deleting = item.Hash; ImGui.OpenPopup("##settings-delete-furniture"); }
            if (state.Deleting == item.Hash && HavenKit.BeginDialog("##settings-delete-furniture", "Delete this furnishing?", 460))
            {
                HavenKit.Caption(item.Homes.Length == 0 ? "It is deleted from this PC and Haven's server. The mod it came from is not touched."
                    : $"It is deleted from this PC and Haven's server, and removed from {string.Join(", ", item.Homes)}. The mod it came from is not touched.");
                HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Delete") + Px(Space2));
                if (HavenKit.Button("Keep##settings-delete-furniture", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Delete##settings-delete-furniture", ButtonKind.Danger)) { command = new(SettingsAction.DeleteFurniture, Text: item.Hash); ImGui.CloseCurrentPopup(); }
                HavenKit.EndDialog();
            }
            ImGui.PopID();
            ImGui.Dummy(Px(0, Space2));
        }
        return command;
    }

    // One picture: its thumbnail in its own shape, what it is, where it hangs, and one action.
    private static bool PictureRow(SettingsState state, SettingsModel model, SettingsPicture picture, string action, ButtonKind kind)
    {
        ImGui.PushID(picture.Hash + action);
        var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        var box = Px(80, 56);
        var fit = Math.Min(box.X / Math.Max(.01f, picture.Aspect), box.Y);
        var size = new Vector2(fit * picture.Aspect, fit);
        var at = min + (box - size) / 2;
        if (model.Thumbnail?.Invoke(picture.Hash) is {} texture) draw.AddImageRounded(texture, at, at + size, Vector2.Zero, Vector2.One, HavenUi.U(Vector4.One), Px(RadiusField));
        else draw.AddRectFilled(at, at + size, HavenUi.U(Inset), Px(RadiusField));
        var text = min + new Vector2(box.X + Px(Space3), 0);
        using (HavenFonts.Use(HavenText.Strong)) draw.AddText(text, HavenUi.U(Text), picture.Title);
        using (HavenFonts.Use(HavenText.Small))
        {
            draw.AddText(text + new Vector2(0, Px(19)), HavenUi.U(TextSecondary), picture.Detail);
            draw.AddText(text + new Vector2(0, Px(35)), HavenUi.U(TextSecondary), picture.Homes.Length == 0 ? "Not in any home" : "In " + string.Join(", ", picture.Homes));
        }
        ImGui.Dummy(new(ImGui.GetContentRegionAvail().X, box.Y));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + box.X + Px(Space3));
        var clicked = HavenKit.Button(action, kind);
        ImGui.PopID();
        ImGui.Dummy(Px(0, Space2));
        return clicked;
    }

    private static void Meter(string label, long value, long limit)
    {
        HavenKit.Caption(label);
        var min = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X; var height = Px(6);
        var draw = ImGui.GetWindowDrawList(); var share = limit > 0 ? Math.Clamp(value / (float)limit, 0, 1) : 0;
        draw.AddRectFilled(min, min + new Vector2(width, height), HavenUi.U(Inset), height / 2);
        if (share > 0) draw.AddRectFilled(min, min + new Vector2(Math.Max(height, width * share), height), HavenUi.U(share > .9f ? Warning : Accent), height / 2);
        ImGui.Dummy(new(width, height + Px(Space1)));
    }

    private static SettingsCommand DeleteDialog(SettingsState state, string hash)
    {
        var command = default(SettingsCommand);
        if (!HavenKit.BeginDialog("##settings-delete-" + hash, "Delete this picture?", 460)) return command;
        HavenKit.Caption("It is deleted from this PC. Haven refuses while a home on this PC shows it.");
        HavenKit.Toggle("Also delete it from Haven's server now", ref state.DeleteFromServer);
        HavenKit.Caption(state.DeleteFromServer
            ? "Guests and anyone who took it from the gallery can no longer download it. Only the character that uploaded it can do this."
            : "The server's copy is deleted by itself after 30 days without use.");
        HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Delete") + Px(Space2));
        if (HavenKit.Button("Keep##settings-delete", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("Delete##settings-delete", ButtonKind.Danger) && state.Deleting == hash)
        {
            command = new(SettingsAction.DeletePicture, First: state.DeleteFromServer, Text: hash);
            ImGui.CloseCurrentPopup();
        }
        HavenKit.EndDialog();
        return command;
    }

    private static SettingsCommand Files(SettingsState state, SettingsModel model)
    {
        var command = default(SettingsCommand);
        HavenKit.SectionTitle("Import a home");
        HavenKit.Caption("Bring in a .haven.json file. The imported home gets its own identity alongside your others.");
        if (HavenKit.Button("Import…", ButtonKind.Primary)) command = new(SettingsAction.Import);
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Export a home");
        HavenKit.Caption("Save a portable copy for backup or to share. Local Stagehand links are never included.");
        var names = model.Homes.Select(h => h.Name).ToArray();
        var index = state.Home is { } id ? model.Homes.ToList().FindIndex(h => h.Id == id) : -1;
        if (index < 0 && model.Homes.Count > 0) { index = 0; state.Home = model.Homes[0].Id; }
        if (HavenKit.Select("##export-home", "Home", names, ref index)) state.Home = model.Homes[index].Id;
        ImGui.BeginDisabled(index < 0);
        if (HavenKit.Button("Export…", ButtonKind.Secondary) && index >= 0) command = new(SettingsAction.Export, Home: model.Homes[index].Id);
        ImGui.EndDisabled();
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Folders");
        Folder("Homes", model.HomesFolder, SettingsAction.OpenHomes, ref command);
        Folder("Deleted homes", model.DeletedFolder, SettingsAction.OpenDeleted, ref command);
        if (model.LayoutsFolder.Length > 0) Folder("Saved layouts", model.LayoutsFolder, SettingsAction.OpenLayouts, ref command);
        if (model.GroupsFolder.Length > 0) Folder("Your groups", model.GroupsFolder, SettingsAction.OpenGroups, ref command);
        HavenKit.Caption("Deleted homes keep a recovery copy; import it to restore the home.");
        return command;
    }

    private static void Folder(string label, string path, SettingsAction action, ref SettingsCommand command)
    {
        var width = ImGui.GetContentRegionAvail().X; var button = HavenKit.ButtonWidth("Open");
        var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList(); var height = Px(52);
        float labelHeight;
        using (HavenFonts.Use(HavenText.Caption)) { labelHeight = ImGui.GetTextLineHeight(); draw.AddText(min + Px(0, 6), HavenUi.U(TextTertiary), label); }
        draw.PushClipRect(min, min + new Vector2(width - button - Px(Space3), height), true);
        using (HavenFonts.Use(HavenText.Small)) draw.AddText(min + new Vector2(0, Px(8) + labelHeight), HavenUi.U(TextOnRaised), path);
        draw.PopClipRect();
        ImGui.SetCursorScreenPos(new(min.X + width - button, min.Y + (height - Px(ControlHeight)) / 2));
        if (HavenKit.Button("Open##" + label, ButtonKind.Ghost)) command = new(action);
        ImGui.SetCursorScreenPos(min); ImGui.Dummy(new(width, height));
    }

    private static SettingsCommand Connection(SettingsState state, SettingsModel model)
    {
        var command = default(SettingsCommand);
        state.RelayText ??= model.Relay; state.CodeText ??= model.HostCode;
        var account = model.Account;
        HavenKit.SectionTitle("Haven account", account.SignedIn ? "signed in as " + account.Name : "");
        if (account.SignedIn)
        {
            HavenKit.Caption("Your characters are tied to this Discord account the first time they join a gathering, so nobody else can use them.");
            if (HavenKit.Button("Sign out on this PC", ButtonKind.Secondary)) command = new(SettingsAction.SignOut);
        }
        else if (account.SigningIn)
        {
            HavenKit.Caption("Finish in your browser: approve Haven on Discord's page, then confirm this code on Haven's page. Haven signs in by itself when you have.");
            LauncherUi.SignInCode(account.Code);
            if (HavenKit.Button("Cancel", ButtonKind.Secondary)) command = new(SettingsAction.CancelSignIn);
        }
        else
        {
            HavenKit.Caption("Gatherings, and uploading your own pictures, songs, furniture and animations, need a Haven account. Sign in with Discord once on this PC; it ties your characters to you, so nobody else can use them. Haven is invite-only for now: a new account needs the access code you were given, entered on the sign-in page.");
            if (HavenKit.Button("Sign in with Discord", ButtonKind.Primary)) command = new(SettingsAction.SignIn);
        }
        if (account.Problem is { } problem) HavenKit.Caption(problem);
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Haven relay");
        HavenKit.Caption("Everyone in a gathering needs the same relay. The hosted relay is the default.");
        ImGui.BeginDisabled(!model.CanChangeConnection);
        var relay = state.RelayText;
        HavenKit.Field("##relay", "Relay address", ref relay, model.HostedRelay); state.RelayText = relay;
        var half = (ImGui.GetContentRegionAvail().X - Px(Space2)) / 2;
        ImGui.BeginDisabled(relay.Trim() == model.Relay);
        if (HavenKit.Button("Save address", ButtonKind.Primary, new(half, 0))) command = new(SettingsAction.SaveRelay, Text: relay.Trim());
        ImGui.EndDisabled(); ImGui.SameLine(0, Px(Space2));
        ImGui.BeginDisabled(model.Relay == model.HostedRelay);
        if (HavenKit.Button("Use the hosted relay", ButtonKind.Secondary, new(half, 0))) { command = new(SettingsAction.UseHostedRelay); state.RelayText = model.HostedRelay; }
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        if (!model.CanChangeConnection) HavenKit.Caption("Leave your current visit to change the relay.");
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Your hosting code");
        HavenKit.Caption("Friends join your gatherings with this code. Make a new one to stop old invitations from working.");
        var code = state.CodeText;
        HavenKit.Field("##host-code", "", ref code, "Hosting code"); state.CodeText = code;
        if (ImGui.IsItemDeactivatedAfterEdit() && code.Trim().Length > 0) command = new(SettingsAction.SaveCode, Text: code.Trim());
        if (HavenKit.Button("Copy code", ButtonKind.Secondary, new(half, 0))) command = new(SettingsAction.CopyCode, Text: code.Trim());
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("Make a new code", ButtonKind.Secondary, new(half, 0))) { command = new(SettingsAction.NewCode); state.CodeText = null; }
        ImGui.Dummy(Px(0, Space2));
        HavenKit.Caption("House chat stays inside the relay room, and world chat is blocked while you're inside a home.");
        return command;
    }

    private static SettingsCommand Advanced(SettingsState state, SettingsModel model)
    {
        var command = default(SettingsCommand);
        // Capturing homes from Stagehand stages (and linking them for later edits) was removed on 3 November 2026: the
        // user found it redundant, and no tester had used it.
        HavenKit.SectionTitle("After a game update");
        var patch = model.Patch;
        if (patch.Checked) HavenKit.Caption("The game is the version this Haven was checked on: everything is on.");
        else
        {
            HavenKit.Caption("The game has updated since this Haven was checked. A few extras that rely on details of the game's memory are paused until Haven is updated, so a changed game can't make them misbehave.");
            var allowed = patch.AllowedHere;
            if (HavenKit.Toggle("Use them anyway on this game version", ref allowed)) command = new(SettingsAction.PatchAllow, allowed);
            if (patch.Paused.Count > 0) HavenKit.Caption("Paused: " + string.Join(", ", patch.Paused) + ".");
        }
        foreach (var line in patch.Lines) HavenKit.Caption(line);
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Lights", model.HavenLights && model.HavenLightCount.All > 0
            ? $"{model.HavenLightCount.All} drawn by Haven" + (model.HavenLightCount.World > 0 ? $", {model.HavenLightCount.World} from world pieces" : "") : "");
        var stagehand = !model.HavenLights;
        if (HavenKit.Toggle("Use Stagehand's lights instead", ref stagehand)) command = new(SettingsAction.HavenLights, !stagehand);
        HavenKit.Caption(stagehand
            ? "Stagehand draws furnishing lights, as before 0.66. World pieces' own lights don't shine this way. Turn this off to go back to Haven's lights."
            : "Haven draws lamps, the blank canvas's fill light and world pieces' lights itself; Stagehand still draws imported Stagehand objects. If a room's lighting looks wrong, turn this on to compare, and tell us.");
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Effects", model.HavenEffects && model.HavenEffectCount > 0 ? $"{model.HavenEffectCount} drawn by Haven" : "");
        var stagehandEffects = !model.HavenEffects;
        if (HavenKit.Toggle("Use Stagehand's effects instead", ref stagehandEffects)) command = new(SettingsAction.HavenEffects, !stagehandEffects);
        HavenKit.Caption(stagehandEffects
            ? "Stagehand draws furnishings' flames, steam, water and sparkles, as before 0.66. Turn this off to go back to Haven's effects."
            : "Haven draws furnishings' flames, steam, water and sparkles itself; Stagehand still plays their sounds. If an effect looks wrong, turn this on to compare, and tell us.");
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Sounds", model.HavenSounds && model.HavenSoundCount > 0 ? $"{model.HavenSoundCount} played by Haven" : "");
        var stagehandSounds = !model.HavenSounds;
        if (HavenKit.Toggle("Use Stagehand's sounds instead", ref stagehandSounds)) command = new(SettingsAction.HavenSounds, !stagehandSounds);
        HavenKit.Caption(stagehandSounds
            ? "Stagehand plays furnishings' sounds, as before 0.66. World pieces' own sounds don't play this way. Turn this off to go back to Haven's sounds."
            : "Haven plays furnishings' sounds itself: fountains, fires, chimes, clicks and instruments. If one sounds wrong, turn this on to compare, and tell us.");
        ImGui.Dummy(Px(0, Space3));
        HavenKit.SectionTitle("Diagnostics");
        foreach (var line in model.Diagnostics) HavenKit.Caption(line);
        return command;
    }
}
