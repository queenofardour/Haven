using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

internal readonly record struct TrackChoice(uint Selection, string Title, string Detail);
// Custom: one of the owner's own songs rather than a game track, shown in green.
internal readonly record struct PlaylistEntry(string Title, int Repeats, bool Current, bool Custom = false);
// A song in the player's music: taken from the former community gallery, or stored on Haven's server.
internal readonly record struct SongChoice(string Hash, string Title, string Detail, bool FromCommunity = false, bool Stored = false);
internal sealed record MusicPanelModel(bool Editable, bool Ready, string NowPlaying, bool Playing, IReadOnlyList<PlaylistEntry> Playlist,
    IReadOnlyList<TrackChoice> Library, bool Loop, bool Shuffle, int Gap, string Status)
{
    /// <summary>The player's own songs (custom music), and the one being converted, if any.</summary>
    public IReadOnlyList<SongChoice> Songs { get; init; } = [];
    public (string Title, float Progress)? Converting { get; init; }
    /// <summary>The owner must accept the custom content agreement before their songs go to guests.</summary>
    public bool NeedsAgreement { get; init; }
}
internal enum MusicAction
{
    None, Play, Stop, Add, Remove, MoveUp, MoveDown, Repeats, Loop, Shuffle, Gap, AddSongFile, ImportSong, AddSong, RenameSong, DeleteSong, CancelSong,
    // Custom content: the agreement, then add the song.
    AgreeAndAddSong,
}
internal readonly record struct MusicCommand(MusicAction Action, int Index = -1, uint Track = 0, int Value = 0, string Text = "", string Song = "");

internal sealed class MusicPanelState
{
    public int Tab;
    public string Search = "";
    public int Selected = -1;
    public string? Song;             // the selected song in Your music
    public uint Track;               // the selected game track
    public string SongName = "";     // the rename field
    public string? NewSong;          // a file just chosen, waiting for its name
    public string NewSongName = "";
    public bool NewSongShown;
    public bool DeleteFromServer = true; // Delete…: also from Haven's server
}

// The Music panel: now playing, the playlist with per-track repeats and order,
// playback options, and the game's track library to add from. Guests see it read-only.
internal static class MusicPanelUi
{
    public const int MaxTracks = 64;
    private static float? liveRepeats, liveGap;

    public static MusicCommand Draw(MusicPanelState state, MusicPanelModel model, ref bool open, float top)
    {
        var command = default(MusicCommand);
        if (SidePanelUi.Begin("Music", "Haven.MusicPanel", ref open, top))
        {
            NowPlaying(model, ref command);
            ImGui.Dummy(Px(0, Space1));
            var tab = state.Tab;
            if (HavenKit.Segmented("music-tab", [$"Playlist · {model.Playlist.Count}", "Game", "Yours"], ref tab)) state.Tab = tab;
            ImGui.Dummy(Px(0, Space1));
            // Choosing the music is the owner's (or a music decorator's).
            state.Tab = Math.Clamp(state.Tab, 0, 2);
            ImGui.BeginDisabled(!model.Editable);
            var result = state.Tab switch { 0 => Playlist(state, model), 1 => Library(state, model), _ => YourMusic(state, model) };
            if (result.Action != MusicAction.None) command = result;
            ImGui.EndDisabled();
            if (NameSong(state) is { Action: not MusicAction.None } named) command = named;
        }
        SidePanelUi.End(open);
        return command;
    }

    private static void NowPlaying(MusicPanelModel model, ref MusicCommand command)
    {
        var width = ImGui.GetContentRegionAvail().X; var height = Px(64); var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + new Vector2(width, height), HavenUi.U(model.Playing ? AccentTint : HavenTokens.Card), Px(RadiusCard));
        HavenUi.DrawIcon(draw, HavenIcon.Music, min + new Vector2(Px(Space4 + 9), height / 2), Px(18), model.Playing ? Accent : TextSecondary);
        var x = min.X + Px(Space4 + 18 + Space3); var buttons = Px(ControlHeight * 2 + Space1); var right = min.X + width - buttons - Px(Space3);
        float captionHeight;
        using (HavenFonts.Use(HavenText.Caption)) { captionHeight = ImGui.GetTextLineHeight(); draw.AddText(new(x, min.Y + Px(12)), HavenUi.U(TextTertiary), model.Playing ? "Now playing" : "Not playing"); }
        draw.PushClipRect(new(x, min.Y), new(right, min.Y + height), true);
        using (HavenFonts.Use(HavenText.Strong)) draw.AddText(new(x, min.Y + Px(14) + captionHeight), HavenUi.U(HavenTokens.Text), model.NowPlaying);
        draw.PopClipRect();
        ImGui.SetCursorScreenPos(new(min.X + width - buttons - Px(Space2), min.Y + (height - Px(ControlHeight)) / 2));
        ImGui.BeginDisabled(!model.Editable || model.Playlist.Count == 0 || !model.Ready);
        if (HavenKit.IconButton("##music-play", HavenIcon.Play, model.Playing ? "Restart the playlist" : "Play", true)) command = new(MusicAction.Play);
        ImGui.EndDisabled(); ImGui.SameLine(0, Px(Space1));
        ImGui.BeginDisabled(!model.Editable || !model.Playing);
        if (HavenKit.IconButton("##music-stop", HavenIcon.Stop, "Stop")) command = new(MusicAction.Stop);
        ImGui.EndDisabled();
        ImGui.SetCursorScreenPos(min); ImGui.Dummy(new(width, height));
        if (!model.Editable) HavenKit.Caption("The home's owner chooses the music. Volume follows your own game settings.");
        if (model.Status.Length > 0) HavenKit.Caption(model.Status);
    }

    private static MusicCommand Playlist(MusicPanelState state, MusicPanelModel model)
    {
        var command = default(MusicCommand);
        if (state.Selected >= model.Playlist.Count) state.Selected = -1;
        var controls = state.Selected >= 0 ? Px(150) : Px(0);
        var options = Px(170);
        if (ImGui.BeginChild("##playlist", new(0, Math.Max(Px(120), ImGui.GetContentRegionAvail().Y - controls - options)), false))
        {
            if (model.Playlist.Count == 0)
            {
                ImGui.Dummy(Px(0, Space4));
                if (HavenKit.EmptyState(HavenIcon.Music, "No tracks yet", "Add songs from the game's tracks, or your own music.", "Open game tracks")) state.Tab = 1;
            }
            for (var i = 0; i < model.Playlist.Count; i++)
            {
                var entry = model.Playlist[i];
                var detail = (entry.Current ? "Playing · " : "") + (entry.Custom ? "Custom song · " : "") + (entry.Repeats == 1 ? "Plays once" : $"Plays {entry.Repeats} times");
                if (HavenKit.ListRow($"##track-{i}", $"{i + 1}. {entry.Title}", detail, state.Selected == i, entry.Current ? HavenIcon.Play : HavenIcon.Music, green: entry.Custom))
                    state.Selected = state.Selected == i ? -1 : i;
            }
        }
        ImGui.EndChild();
        if (state.Selected >= 0)
        {
            var index = state.Selected; var entry = model.Playlist[index];
            var repeats = liveRepeats ?? entry.Repeats;
            var drag = HavenKit.Slide("##track-repeats", "Repeats", ref repeats, 1, 10, $"{MathF.Round(repeats):0}×");
            liveRepeats = drag.Active ? repeats : null;
            if (drag.Committed) command = new(MusicAction.Repeats, index, Value: (int)MathF.Round(repeats));
            var third = (ImGui.GetContentRegionAvail().X - Px(Space2) * 2) / 3;
            ImGui.BeginDisabled(index == 0); if (HavenKit.Button("Move up", ButtonKind.Secondary, new(third, 0))) { command = new(MusicAction.MoveUp, index); state.Selected--; } ImGui.EndDisabled();
            ImGui.SameLine(0, Px(Space2)); ImGui.BeginDisabled(index == model.Playlist.Count - 1);
            if (HavenKit.Button("Move down", ButtonKind.Secondary, new(third, 0))) { command = new(MusicAction.MoveDown, index); state.Selected++; }
            ImGui.EndDisabled(); ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Remove", ButtonKind.Danger, new(third, 0))) { command = new(MusicAction.Remove, index); state.Selected = -1; }
        }
        HavenKit.Rule();
        var loop = model.Loop; if (HavenKit.Toggle("Repeat playlist", ref loop)) command = new(MusicAction.Loop, Value: loop ? 1 : 0);
        var shuffle = model.Shuffle; if (HavenKit.Toggle("Shuffle", ref shuffle)) command = new(MusicAction.Shuffle, Value: shuffle ? 1 : 0);
        var gap = liveGap ?? model.Gap;
        var gapDrag = HavenKit.Slide("##music-gap", "Pause between tracks", ref gap, 0, 600, gap < 1 ? "None" : $"{MathF.Round(gap):0} s");
        liveGap = gapDrag.Active ? gap : null;
        if (gapDrag.Committed) command = new(MusicAction.Gap, Value: (int)MathF.Round(gap));
        return command;
    }

    // "Name this song", once a file is chosen: then Haven converts it.
    private static MusicCommand NameSong(MusicPanelState state)
    {
        var command = default(MusicCommand);
        if (state.NewSong is not { } file) return command;
        if (!state.NewSongShown) { ImGui.OpenPopup("##song-new"); state.NewSongShown = true; }
        if (!HavenKit.BeginDialog("##song-new", "Name this song", 440)) { state.NewSong = null; state.NewSongShown = false; return command; }
        HavenKit.Caption(Path.GetFileName(file));
        var name = state.NewSongName;
        var submitted = HavenKit.SubmitField("##song-new-name", "Name", ref name, "Song name", maxLength: 60);
        state.NewSongName = name;
        HavenKit.Caption("Your playlist shows this name, and so do your guests.");
        HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Add song") + Px(Space2));
        if (HavenKit.Button("Cancel##song-new", ButtonKind.Ghost)) { state.NewSong = null; state.NewSongShown = false; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine(0, Px(Space2));
        if ((HavenKit.Button("Add song##song-new", ButtonKind.Primary) || submitted) && name.Trim().Length > 0)
        { command = new(MusicAction.ImportSong, Song: file, Text: name.Trim()); state.NewSong = null; state.NewSongShown = false; ImGui.CloseCurrentPopup(); }
        HavenKit.EndDialog();
        return command;
    }

    // The player's own songs: add one from a file, put one in the playlist, rename or delete it.
    private static MusicCommand YourMusic(MusicPanelState state, MusicPanelModel model)
    {
        var command = default(MusicCommand);
        var width = ImGui.GetContentRegionAvail().X;
        if (model.Converting is var (title, progress))
        {
            HavenKit.SectionTitle("Converting " + title, $"{progress * 100:0}%");
            HavenKit.Caption("Haven converts the song on this PC. You can keep decorating meanwhile.");
            if (HavenKit.Button("Cancel##song-cancel", ButtonKind.Secondary, new(width, 0))) command = new(MusicAction.CancelSong);
        }
        else if (HavenKit.Button("Add a song…", ButtonKind.Primary, new(width, 0))) command = new(MusicAction.AddSongFile);
        HavenKit.Caption("MP3, Ogg Vorbis, WAV or FLAC, up to 10 minutes.");
        ImGui.Dummy(Px(0, Space1));
        var full = model.Playlist.Count >= MaxTracks;
        HavenKit.SectionTitle(full ? "Playlist full" : "Choose a song", $"{model.Songs.Count} songs");
        if (state.Song != null && model.Songs.All(s => s.Hash != state.Song)) state.Song = null;
        var actions = state.Song != null ? Px(ControlHeight * 2 + Space2 * 2) : 0;
        if (ImGui.BeginChild("##your-music", new(0, Math.Max(Px(80), ImGui.GetContentRegionAvail().Y - actions)), false))
        {
            if (model.Songs.Count == 0) { ImGui.Dummy(Px(0, Space4)); HavenKit.EmptyState(HavenIcon.Music, "No songs yet", "Add a song from your PC to play it in your homes."); }
            foreach (var song in model.Songs)
            {
                if (HavenKit.ListRow("##song-" + song.Hash, song.Title, song.Detail, state.Song == song.Hash, HavenIcon.Music))
                { state.Song = song.Hash; state.SongName = song.Title; }
                // A double-click adds the song, like the Add to playlist button.
                // (Before the content agreement, the button asks for it first.)
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !full && model.Editable && !model.NeedsAgreement)
                    command = new(MusicAction.AddSong, Song: song.Hash);
            }
        }
        ImGui.EndChild();
        if (state.Song is { } selected && model.Songs.FirstOrDefault(s => s.Hash == selected) is var chosen)
        {
            var half = (width - Px(Space2)) / 2;
            ImGui.BeginDisabled(full);
            if (HavenKit.Button("Add to playlist", ButtonKind.Primary, new(width, 0)))
            {
                if (model.NeedsAgreement) ImGui.OpenPopup("##music-agreement");
                else command = new(MusicAction.AddSong, Song: selected);
            }
            ImGui.EndDisabled();
            if (HavenKit.Button("Rename…", ButtonKind.Secondary, new(half, 0))) ImGui.OpenPopup("##song-rename");
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Delete…", ButtonKind.Danger, new(half, 0))) { state.DeleteFromServer = true; ImGui.OpenPopup("##song-delete"); }
            if (HavenKit.BeginDialog("##music-agreement", "Before you add your own music", 520))
            {
                HavenKit.Caption("Haven keeps the pictures and music you upload and shares them with everyone who visits your homes and agrees to load custom content. You are responsible for what you upload.");
                HavenKit.Caption("•  Upload only music you have the right to share.");
                HavenKit.Caption("•  Never upload anything involving minors, or anything illegal.");
                HavenKit.Caption("•  Haven may remove anything uploaded, and stop uploads from anyone who breaks these rules.");
                HavenKit.Caption("What your homes stop using is deleted after 30 days.");
                HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("I agree") + Px(Space2));
                if (HavenKit.Button("Cancel##music-agreement", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("I agree##music-agreement", ButtonKind.Primary)) { command = new(MusicAction.AgreeAndAddSong, Song: selected); ImGui.CloseCurrentPopup(); }
                HavenKit.EndDialog();
            }
            if (HavenKit.BeginDialog("##song-rename", "Rename this song", 420))
            {
                var name = state.SongName;
                var submitted = HavenKit.SubmitField("##song-name", "Name", ref name, "Song name");
                state.SongName = name;
                HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Rename") + Px(Space2));
                if (HavenKit.Button("Cancel##song-rename", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
                ImGui.SameLine(0, Px(Space2));
                if ((HavenKit.Button("Rename##song-rename", ButtonKind.Primary) || submitted) && name.Trim().Length > 0)
                { command = new(MusicAction.RenameSong, Song: selected, Text: name.Trim()); ImGui.CloseCurrentPopup(); }
                HavenKit.EndDialog();
            }
            if (HavenKit.BeginDialog("##song-delete", "Delete this song?", 420))
            {
                HavenKit.Caption("It is deleted from this PC. Haven refuses while one of your homes still plays it.");
                if (chosen.Stored && !chosen.FromCommunity)
                {
                    HavenKit.Toggle("Also delete it from Haven's server now", ref state.DeleteFromServer);
                    HavenKit.Caption(state.DeleteFromServer ? "Guests lose it at once." : "The server's copy goes after 30 days unused.");
                }
                HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Delete") + Px(Space2));
                if (HavenKit.Button("Keep##song-delete", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Delete##song-delete", ButtonKind.Danger))
                { command = new(MusicAction.DeleteSong, Song: selected, Value: chosen.Stored && !chosen.FromCommunity && state.DeleteFromServer ? 1 : 0); ImGui.CloseCurrentPopup(); }
                HavenKit.EndDialog();
            }
        }
        return command;
    }

    private static MusicCommand Library(MusicPanelState state, MusicPanelModel model)
    {
        var command = default(MusicCommand);
        HavenKit.Search("##music-search", ref state.Search, "Search game tracks");
        var full = model.Playlist.Count >= MaxTracks;
        HavenKit.SectionTitle(full ? "Playlist full" : "Double-click a track to add it", $"{model.Library.Count} tracks");
        if (ImGui.BeginChild("##music-library", Vector2.Zero, false))
        {
            if (model.Library.Count == 0) { ImGui.Dummy(Px(0, Space4)); HavenKit.EmptyState(HavenIcon.Search, "Nothing found", "Try another name."); }
            else
            {
                var height = Px(58) + ImGui.GetStyle().ItemSpacing.Y;
                var first = Math.Max(0, (int)(ImGui.GetScrollY() / height)); var last = Math.Min(model.Library.Count, first + (int)(ImGui.GetWindowHeight() / height) + 2);
                var top = ImGui.GetCursorPos();
                ImGui.BeginDisabled(full);
                for (var i = first; i < last; i++)
                {
                    ImGui.SetCursorPos(top + new Vector2(0, i * height));
                    var track = model.Library[i];
                    // A click selects; a double-click adds, so a quick extra click never adds a track twice.
                    if (HavenKit.ListRow("##lib-" + track.Selection, track.Title, track.Detail, state.Track == track.Selection, HavenIcon.Plus)) state.Track = track.Selection;
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) command = new(MusicAction.Add, Track: track.Selection);
                }
                ImGui.EndDisabled();
                ImGui.SetCursorPos(top + new Vector2(0, model.Library.Count * height)); ImGui.Dummy(Vector2.Zero);
            }
        }
        ImGui.EndChild();
        return command;
    }
}
