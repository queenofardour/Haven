using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

internal readonly record struct FixtureChoice(uint Id, string Name, ushort Icon);
// One fixture slot on the chosen floor (wallpaper, flooring, ceiling light) or the mansion's main hall chandelier.
internal sealed record RoomSlot(string Label, uint Current, IReadOnlyList<FixtureChoice> Options);
internal sealed record RoomPanelModel(bool Custom, bool Editable, IReadOnlyList<string> Floors, int Floor, IReadOnlyList<RoomSlot> Slots,
    float? Brightness, string Status, Func<ushort, ImTextureID?> Icon)
{
    // Saved layouts made for this home's shell, by name.
    public IReadOnlyList<string> Layouts { get; init; } = [];
    // An inn room: none of housing's finishes or brightness.
    public bool Inn { get; init; }
    // Atmosphere: the weathers the room's level holds, the home's choices, Eorzea's time now and the saved moods.
    public IReadOnlyList<(uint Id, string Name)> Weathers { get; init; } = [];
    public uint? Weather { get; init; }
    public int? TimeOfDay { get; init; }
    public int ClockNow { get; init; }
    public string? Outside { get; init; }
    public bool OutsideAvailable { get; init; }
    public float OutsideVolume { get; init; } = .6f;
    public IReadOnlyList<MoodRow> Moods { get; init; } = [];
}
internal readonly record struct MoodRow(string Name, string Detail, bool Showing);
internal enum RoomAction { None, Floor, Fixture, Brightness, RestoreLighting, RestoreInterior, ReturnToPlatform, ApplyLayout,
    // NPCs (NpcPanelUi): Index is the NPC; NpcAway's Level is 1 to send it away, 0 to bring it back.
    AddNpc, PossessNpc, SaveNpcSpot, CopyNpcLook, RenameNpc, RemoveNpc, DressNpc, NpcAway, BringNpc, KeepNpcGlamour, ResetNpcColours, NpcLevel,
    // An NPC's mods (NpcModShare): ShareNpcMods' Text is the Penumbra collection to wear and capture from.
    ShareNpcMods, SendNpcMods, FinishNpcMods, ForgetNpcMods,
    // Its Customize+ profile (NpcBody): Text is the profile, empty to take it away.
    NpcBody,
    // Saved NPCs and visitors (NpcRosterUi): from SaveNpcToRoster on, Index is the row in the model's Roster or Visitors
    // list (SaveNpcToRoster: the home's NPC); VisitorRights' Level is the VisitRights, RefuseVisitors' 1 to refuse.
    SaveNpcToRoster, RosterRename, RosterRemove, RosterBringHome, RosterBringVisit, VisitorPossess, VisitorAway, VisitorRights, RefuseVisitors, VisitorSendMods, VisitorFinishMods,
    // A saved layout in the Room panel's list: Index is the layout, RenameLayout's Text the new name.
    RenameLayout, DeleteLayout,
    // Atmosphere: SetTime/PreviewTime's Level is the minutes after midnight (-1 follows Eorzea's clock); SetWeather's
    // Fixture the weather (0 the room's own); SetOutside's Text a HomeAmbience key (null for none). Moods: Index is the
    // mood; SaveMood's Text its name and Level 1 to keep the music; RenameMood's Text the new name.
    SetTime, PreviewTime, SetWeather, SetOutside, SaveMood, ApplyMood, UpdateMood, RenameMood, DeleteMood,
    // This PC's volume for the weather heard outside: Level 0-1.
    OutsideVolume }
internal readonly record struct RoomCommand(RoomAction Action, int Index = -1, uint Fixture = 0, float Level = 0, string? Text = null);

// The Room panel: per-floor finishes as icon pickers, house brightness and resets.
internal static class RoomPanelUi
{
    private static float? liveBrightness, liveTime, liveOutside;
    private static string moodName = "";
    private static bool moodMusic = true, renameMood;
    private static int moodIndex = -1;
    private static int pickerSlot = -1, layout;
    private static string layoutName = "";

    public static RoomCommand Draw(RoomPanelModel model, ref bool open, float top)
    {
        var command = default(RoomCommand);
        if (SidePanelUi.Begin("Room", "Haven.RoomPanel", ref open, top))
        {
            ImGui.BeginDisabled(!model.Editable);
            if (model.Custom)
            {
                HavenKit.Banner(BannerKind.Info, "Custom canvas", "Find walls, floors and stairs under Furnish → Building. Select a piece to change its surface finish.");
                ImGui.Dummy(Px(0, Space2));
                Brightness(model, ref command);
                ImGui.Dummy(Px(0, Space2));
                if (HavenKit.Button("Return to starter platform", ButtonKind.Secondary, new(-1, 0))) command = new(RoomAction.ReturnToPlatform);
                ImGui.Dummy(Px(0, Space3));
                Atmosphere(model, ref command);
            }
            else if (model.Inn)
            {
                HavenKit.Banner(BannerKind.Info, "Inn room", "An inn room has no wallpaper, flooring or brightness to change. Remove or move its own furniture under Remodel.");
                ImGui.Dummy(Px(0, Space3));
                Atmosphere(model, ref command);
                ImGui.Dummy(Px(0, Space3));
                Layouts(model, ref command);
            }
            else
            {
                if (model.Floors.Count > 1)
                {
                    var floor = model.Floor;
                    if (HavenKit.Segmented("room-floor", model.Floors, ref floor)) command = new(RoomAction.Floor, floor);
                    ImGui.Dummy(Px(0, Space1));
                }
                if (model.Slots.Count == 0) HavenKit.Caption("This layout has no native finishes to change.");
                for (var i = 0; i < model.Slots.Count; i++)
                    if (Slot(model, i) is { } fixture) command = new(RoomAction.Fixture, i, fixture);
                ImGui.Dummy(Px(0, Space2));
                Brightness(model, ref command);
                ImGui.Dummy(Px(0, Space2));
                var half = (ImGui.GetContentRegionAvail().X - Px(Space2)) / 2;
                if (HavenKit.Button("Original lighting", ButtonKind.Secondary, new(half, 0))) command = new(RoomAction.RestoreLighting);
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Original interior", ButtonKind.Secondary, new(half, 0))) command = new(RoomAction.RestoreInterior);
                HavenKit.Caption("Original interior (or choosing Original for a finish) briefly reloads the room. Your position and furnishings are kept.");
                ImGui.Dummy(Px(0, Space3));
                Atmosphere(model, ref command);
                ImGui.Dummy(Px(0, Space3));
                Layouts(model, ref command);
            }
            ImGui.EndDisabled();
            if (!model.Editable) HavenKit.Caption("Only the home's decorators can change the room.");
            if (model.Status.Length > 0) HavenKit.Caption(model.Status);
        }
        SidePanelUi.End(open);
        return command;
    }

    // A saved layout for this shell replaces the furnishings and finishes, after a confirmation; Undo brings them back.
    private static void Layouts(RoomPanelModel model, ref RoomCommand command)
    {
        HavenKit.SectionTitle("Saved layouts");
        if (model.Layouts.Count == 0)
        {
            HavenKit.Caption("Layouts saved for this shell appear here. Open the Haven menu inside a real house to save one, or import a MakePlace file from New home.");
            return;
        }
        layout = Math.Clamp(layout, 0, model.Layouts.Count - 1);
        HavenKit.Select("##room-layout", "Layout", model.Layouts, ref layout);
        if (HavenKit.Button("Apply layout…##room-apply-layout", ButtonKind.Secondary, new(-1, 0))) ImGui.OpenPopup("##room-layout-apply");
        if (HavenKit.BeginDialog("##room-layout-apply", "Apply this layout?"))
        {
            HavenKit.Caption($"\"{model.Layouts[layout]}\" replaces this home's furnishings and finishes. Undo brings yours back.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Apply") + Px(Space2));
            if (HavenKit.Button("Cancel", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Apply", ButtonKind.Primary)) { command = new(RoomAction.ApplyLayout, layout); ImGui.CloseCurrentPopup(); }
            HavenKit.EndDialog();
        }
        // Renaming and deleting a layout here too (before, only New home offered them, under Start from).
        var half = (ImGui.GetContentRegionAvail().X - Px(Space2)) / 2;
        if (HavenKit.Button("Rename…##room-rename-layout", ButtonKind.Ghost, new(half, 0))) { layoutName = model.Layouts[layout]; ImGui.OpenPopup("##room-layout-rename"); }
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("Delete…##room-delete-layout", ButtonKind.Ghost, new(half, 0))) ImGui.OpenPopup("##room-layout-delete");
        if (HavenKit.BeginDialog("##room-layout-rename", "Rename layout"))
        {
            var submit = HavenKit.SubmitField("##room-layout-name", "Name", ref layoutName, "Layout name");
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere(-1);
            var valid = !string.IsNullOrWhiteSpace(layoutName);
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Save") + Px(Space2));
            if (HavenKit.Button("Cancel##room-layout-rename", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2)); ImGui.BeginDisabled(!valid);
            if ((HavenKit.Button("Save##room-layout-rename", ButtonKind.Primary) || submit) && valid)
            { command = new(RoomAction.RenameLayout, layout, Text: layoutName.Trim()); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            HavenKit.EndDialog();
        }
        if (HavenKit.BeginDialog("##room-layout-delete", "Delete this layout?"))
        {
            HavenKit.Caption($"\"{model.Layouts[layout]}\" leaves your saved layouts. This home and others made from it are kept, and a recovery copy stays in the layouts folder.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Delete") + Px(Space2));
            if (HavenKit.Button("Keep##room-layout-delete", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Delete##room-layout-delete", ButtonKind.Danger)) { command = new(RoomAction.DeleteLayout, layout); ImGui.CloseCurrentPopup(); }
            HavenKit.EndDialog();
        }
    }

    // Time of day, weather, weather heard outside, then the saved moods (one click switches to one).
    private static void Atmosphere(RoomPanelModel model, ref RoomCommand command)
    {
        HavenKit.SectionTitle("Atmosphere", "shared with your guests");
        var held = model.TimeOfDay != null || liveTime != null;
        if (HavenKit.Toggle("Hold a time of day##room-time", ref held))
            command = new(RoomAction.SetTime, Level: held ? Snap(model.ClockNow) : -1);
        if (held)
        {
            var value = liveTime ?? model.TimeOfDay ?? Snap(model.ClockNow);
            var drag = HavenKit.Slide("##room-time-of-day", "Eorzean time", ref value, 0, Haven.Core.HomeMoods.MinutesPerDay - 15, Haven.Core.HomeMoods.Clock(Snap(value)));
            liveTime = drag.Active ? value : null;
            if (drag.Active) command = new(RoomAction.PreviewTime, Level: Snap(value));
            if (drag.Committed) command = new(RoomAction.SetTime, Level: Snap(value));
        }
        else HavenKit.Caption($"Following Eorzea's clock ({Haven.Core.HomeMoods.Clock(model.ClockNow)} now).");
        if (model.Weathers.Count > 1)
        {
            var names = model.Weathers.Select(w => w.Name).Prepend("The room's own").ToList();
            var index = model.Weather is { } w ? model.Weathers.ToList().FindIndex(x => x.Id == w) + 1 : 0;
            if (HavenKit.Select("##room-weather", "Weather", names, ref index))
                command = new(RoomAction.SetWeather, Fixture: index == 0 ? 0 : model.Weathers[index - 1].Id);
        }
        if (model.OutsideAvailable)
        {
            var sounds = Haven.Core.HomeAmbience.All;
            var names = sounds.Select(x => x.Name).Prepend("Nothing").ToList();
            var index = model.Outside is { } key ? Array.FindIndex(sounds, x => x.Key == key) + 1 : 0;
            if (HavenKit.Select("##room-outside", "Heard outside", names, ref index))
                command = new(RoomAction.SetOutside, Text: index == 0 ? null : sounds[index - 1].Key);
            if (model.Outside != null)
            {
                // A per-PC choice: shown to guests too, so each can turn the rain down for themselves.
                ImGui.EndDisabled();
                var volume = liveOutside ?? model.OutsideVolume;
                var drag = HavenKit.Slide("##room-outside-volume", "Its volume on this PC", ref volume, 0, 1, volume <= 0 ? "Off" : $"{MathF.Round(volume * 100):0}%");
                liveOutside = drag.Active ? volume : null;
                if (drag.Committed) command = new(RoomAction.OutsideVolume, Level: MathF.Round(volume * 100) / 100);
                ImGui.BeginDisabled(!model.Editable);
            }
            HavenKit.Caption("The game's own weather sounds, as if through the walls. They play with your music volume.");
        }
        ImGui.Dummy(Px(0, Space2));
        HavenKit.SectionTitle("Moods", model.Moods.Count == 0 ? "" : "click one to switch to it");
        for (var i = 0; i < model.Moods.Count; i++)
        {
            var mood = model.Moods[i];
            if (HavenKit.ListRow("##mood-" + i, mood.Name, mood.Detail, mood.Showing, HavenIcon.Leaf)) command = new(RoomAction.ApplyMood, i);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) { moodIndex = i; ImGui.OpenPopup("##mood-menu"); }
            HavenKit.Tooltip("Click to switch to it; right-click to rename, update or delete it.");
        }
        if (moodIndex >= 0 && moodIndex < model.Moods.Count && HavenKit.BeginMenu("##mood-menu"))
        {
            if (HavenKit.MenuItem("Switch to it")) command = new(RoomAction.ApplyMood, moodIndex);
            if (HavenKit.MenuItem("Keep the room as it is now")) command = new(RoomAction.UpdateMood, moodIndex);
            HavenKit.Tooltip("Saves the room's brightness, weather, time of day and sounds (and the music, if the mood has some) into it.");
            if (HavenKit.MenuItem("Rename…")) { moodName = model.Moods[moodIndex].Name; renameMood = true; }
            if (HavenKit.MenuItem("Delete", danger: true)) command = new(RoomAction.DeleteMood, moodIndex);
            ImGui.EndPopup();
        }
        if (renameMood) { renameMood = false; ImGui.OpenPopup("##mood-rename"); }
        if (HavenKit.BeginDialog("##mood-rename", "Rename mood"))
        {
            var submit = HavenKit.SubmitField("##mood-rename-name", "Name", ref moodName, "Mood name", maxLength: Haven.Core.HomeMoods.MaxName);
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere(-1);
            var valid = !string.IsNullOrWhiteSpace(moodName);
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Save") + Px(Space2));
            if (HavenKit.Button("Cancel##mood-rename", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2)); ImGui.BeginDisabled(!valid);
            if ((HavenKit.Button("Save##mood-rename", ButtonKind.Primary) || submit) && valid)
            { command = new(RoomAction.RenameMood, moodIndex, Text: moodName.Trim()); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            HavenKit.EndDialog();
        }
        var full = model.Moods.Count >= Haven.Core.HomeMoods.MaxMoods;
        ImGui.BeginDisabled(full);
        if (HavenKit.Button("Save as a mood…##mood-save", ButtonKind.Secondary, new(-1, 0))) { moodName = ""; moodMusic = true; ImGui.OpenPopup("##mood-save"); }
        ImGui.EndDisabled();
        if (full) HavenKit.Caption($"A home keeps up to {Haven.Core.HomeMoods.MaxMoods} moods.");
        else if (model.Moods.Count == 0) HavenKit.Caption("A mood keeps the brightness, weather, time of day, sounds and, if you like, the music, so one click switches between Evening, Party and Rainy afternoon.");
        if (HavenKit.BeginDialog("##mood-save", "Save as a mood"))
        {
            var submit = HavenKit.SubmitField("##mood-save-name", "Name", ref moodName, "Evening, Party, Rainy afternoon…", maxLength: Haven.Core.HomeMoods.MaxName);
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere(-1);
            HavenKit.Toggle("Keep the music too##mood-save-music", ref moodMusic);
            HavenKit.Caption(moodMusic ? "Switching to it plays this playlist from the start." : "Switching to it leaves the music as it is.");
            var valid = !string.IsNullOrWhiteSpace(moodName);
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Save") + Px(Space2));
            if (HavenKit.Button("Cancel##mood-save", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2)); ImGui.BeginDisabled(!valid);
            if ((HavenKit.Button("Save##mood-save", ButtonKind.Primary) || submit) && valid)
            { command = new(RoomAction.SaveMood, Level: moodMusic ? 1 : 0, Text: moodName.Trim()); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            HavenKit.EndDialog();
        }
    }
    // Times go in quarter hours.
    private static int Snap(float minutes) => Math.Clamp((int)MathF.Round(minutes / 15) * 15, 0, Haven.Core.HomeMoods.MinutesPerDay - 15);

    private static void Brightness(RoomPanelModel model, ref RoomCommand command)
    {
        var value = liveBrightness ?? model.Brightness ?? 5;
        var drag = HavenKit.Slide("##room-brightness", "House brightness", ref value, 0, 5,
            model.Brightness == null && liveBrightness == null ? "Original" : $"{MathF.Round(value):0} / 5");
        liveBrightness = drag.Active ? value : null;
        if (drag.Committed) command = new(RoomAction.Brightness, Level: MathF.Round(value));
    }

    // A row showing the current fixture; clicking opens a grid of every choice for that slot.
    private static uint? Slot(RoomPanelModel model, int index)
    {
        var slot = model.Slots[index]; uint? picked = null;
        var current = slot.Options.FirstOrDefault(x => x.Id == slot.Current);
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Px(56));
        ImGui.PushID(index);
        var min = ImGui.GetCursorScreenPos();
        if (HavenKit.Card("##slot", size, false, out _)) { pickerSlot = index; ImGui.OpenPopup("##fixtures"); }
        var draw = ImGui.GetWindowDrawList(); var icon = new Vector2(Px(40)); var at = min + new Vector2(Px(Space2), (size.Y - icon.Y) / 2);
        DrawIcon(draw, model, current.Icon, at, icon);
        var x = at.X + icon.X + Px(Space3);
        float labelHeight;
        using (HavenFonts.Use(HavenText.Caption)) { labelHeight = ImGui.GetTextLineHeight(); draw.AddText(new(x, min.Y + Px(9)), HavenUi.U(TextTertiary), slot.Label); }
        draw.PushClipRect(new(x, min.Y), min + size - Px(28, 0), true);
        draw.AddText(new(x, min.Y + Px(11) + labelHeight), HavenUi.U(HavenTokens.Text), slot.Current == 0 ? "Original" : current.Name ?? "Original");
        draw.PopClipRect();
        var chevron = new Vector2(min.X + size.X - Px(Space4), min.Y + size.Y / 2);
        draw.AddLine(chevron + Px(-5, -2), chevron + Px(0, 3), HavenUi.U(TextSecondary), Px(1.6f)); draw.AddLine(chevron + Px(0, 3), chevron + Px(5, -2), HavenUi.U(TextSecondary), Px(1.6f));
        if (pickerSlot == index && HavenKit.BeginMenu("##fixtures"))
        {
            var tile = Px(48); var columns = 6;
            ImGui.Dummy(new(columns * tile + (columns - 1) * Px(4), 0));
            if (ImGui.BeginChild("##fixture-grid", new(columns * tile + (columns - 1) * Px(4), Math.Min(Px(320), ((slot.Options.Count + 1 + columns - 1) / columns) * (tile + Px(4)))), false))
            {
                for (var i = 0; i <= slot.Options.Count; i++)
                {
                    var choice = i == 0 ? new FixtureChoice(0, "Original", 0) : slot.Options[i - 1];
                    if (i % columns != 0) ImGui.SameLine(0, Px(4));
                    var tileMin = ImGui.GetCursorScreenPos();
                    if (HavenKit.Card("##fixture-" + choice.Id, new(tile), choice.Id == slot.Current, out _)) { picked = choice.Id; ImGui.CloseCurrentPopup(); }
                    HavenKit.Tooltip(choice.Name);
                    DrawIcon(ImGui.GetWindowDrawList(), model, choice.Icon, tileMin + new Vector2(Px(4)), new(tile - Px(8)));
                }
            }
            ImGui.EndChild();
            ImGui.EndPopup();
        }
        ImGui.PopID();
        return picked == slot.Current ? null : picked;
    }

    private static void DrawIcon(ImDrawListPtr draw, RoomPanelModel model, ushort icon, Vector2 at, Vector2 size)
    {
        if (icon != 0 && model.Icon(icon) is { } texture) draw.AddImageRounded(texture, at, at + size, Vector2.Zero, Vector2.One, HavenUi.U(Vector4.One), Px(RadiusField));
        else { draw.AddRectFilled(at, at + size, HavenUi.U(IconWell), Px(RadiusField)); HavenUi.DrawIcon(draw, HavenIcon.Paint, at + size / 2, size.X * .45f, TextTertiary); }
    }
}
