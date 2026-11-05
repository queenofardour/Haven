using Dalamud.Bindings.ImGui;
using Haven.Core;
using static Haven.HavenTokens;

namespace Haven;

// One of the player's saved NPCs as the NPCs panel's Yours view shows it, with the mods this PC keeps for it (ModCount 0: not known).
internal readonly record struct RosterRow(Guid Id, string Name, int Level, bool Glamoured, bool HasBody, bool HasMods = false, int ModCount = 0);

// A visiting NPC here: who brought it (BroughtBy "you" for the player's own), whether the owner has let it in yet, and
// whether this player may possess it (as its bringer chose).
internal readonly record struct VisitorRow(Guid Id, string Name, string BroughtBy, bool Mine, bool Welcomed, bool Present, bool Possessed, bool HeldByOther,
    bool MayPossess, VisitRights Rights)
{
    // The mods this PC keeps for it (ModCount 0: not known), the line about them, and whether it has a Glamourer look
    // (its bringer wears it to share them).
    public bool HasMods { get; init; }
    public int ModCount { get; init; }
    public string Mods { get; init; } = "";
    public bool Glamoured { get; init; }
}

// The NPCs panel's Visiting and Yours views. Commands carry the row's
// index in the model's list.
internal static class NpcRosterUi
{
    private static Guid chosenSaved, chosenVisitor, renaming, removing;
    private static string nameDraft = "", shownName = "";
    private static readonly string[] RightsNames = ["Only me", "Me and the home's owner", "Anyone here"];
    public const string ModsNote = "A visiting NPC wears its Glamourer look, its Customize+ profile and the mods each PC keeps for it. Its owner sends them; each guest chooses whether to keep them.";

    private static string ModsLabel(bool has, int count) => !has ? "" : count > 0 ? $"{count} mod{(count == 1 ? "" : "s")}" : "mods";

    public static void Visiting(NpcPanelModel model, ref RoomCommand command)
    {
        if (model.Visitors.Count == 0)
            HavenKit.EmptyState(HavenIcon.Friends, "No visiting NPCs",
                model.OwnsHome ? "Guests can bring their own NPCs into your home for the gathering. They never become part of it."
                    : "Bring one of Your NPCs along for this gathering. It leaves with you and never becomes part of the home.");
        else
        {
            var chosen = model.Visitors.ToList().FindIndex(x => x.Id == chosenVisitor);
            if (chosen < 0) { chosen = 0; chosenVisitor = model.Visitors[0].Id; }
            for (var i = 0; i < model.Visitors.Count; i++)
            {
                var row = model.Visitors[i];
                if (HavenKit.ListRow($"##visitor-row{row.Id}", row.Name, VisitorStatus(row), i == chosen, HavenIcon.Friends)) { chosenVisitor = row.Id; chosen = i; }
                ImGui.Dummy(Px(0, 2));
            }
            ImGui.Dummy(Px(0, Space2));
            HavenKit.Rule();
            ImGui.Dummy(Px(0, Space2));
            VisitorDetails(model, chosen, ref command);
        }
        ImGui.Dummy(Px(0, Space3));
        if (model.OwnsHome)
        {
            var refusing = model.RefusesVisitors;
            if (HavenKit.Toggle("Turn visiting NPCs away##visit-refuse", ref refusing)) command = new(RoomAction.RefuseVisitors, Level: refusing ? 1 : 0);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Guests' NPCs aren't let into your homes, and any here now leave.");
        }
        if (model.VisitNote.Length > 0) HavenKit.Caption(model.VisitNote);
    }

    private static string VisitorStatus(VisitorRow row) =>
        !row.Welcomed ? "Waiting for the owner to let it in…"
        : row.Possessed ? "You're controlling it"
        : row.HeldByOther ? "Someone is controlling it"
        : !row.Present ? "Arriving…"
        : string.Join(" · ", new[] { row.Mine ? "Brought by you" : $"Brought by {row.BroughtBy}", ModsLabel(row.HasMods, row.ModCount) }.Where(x => x.Length > 0));

    private static void VisitorDetails(NpcPanelModel model, int i, ref RoomCommand command)
    {
        var row = model.Visitors[i];
        var width = ImGui.GetContentRegionAvail().X;
        var half = (width - Px(Space2)) / 2;
        ImGui.PushID(row.Id.ToString());
        ImGui.BeginDisabled(row.HeldByOther || !row.Possessed && (!row.Present || !row.MayPossess));
        if (HavenKit.Button((row.Possessed ? "Let go" : "Possess") + "##visitor-possess", row.Possessed ? ButtonKind.Secondary : ButtonKind.Primary, new(half, 0)))
            command = new(RoomAction.VisitorPossess, i);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(row.HeldByOther ? "Someone else is controlling it right now." : !row.MayPossess && !row.Possessed ? NpcPanelUi.NotYourVisitor
                : "Walk, emote and talk as it; your own character stays where it is.");
        ImGui.SameLine(0, Px(Space2));
        ImGui.BeginDisabled(!row.Mine && !model.OwnsHome);
        if (HavenKit.Button((row.Mine ? "Take it back" : "Send it away") + "##visitor-away", ButtonKind.Secondary, new(half, 0))) command = new(RoomAction.VisitorAway, i);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(row.Mine ? "It leaves the gathering. It stays in Your NPCs." : model.OwnsHome ? "It leaves your home for this gathering." : "Only its owner or the home's owner can send it away.");
        ImGui.Dummy(Px(0, Space2));
        if (row.Mine)
        {
            var rights = (int)row.Rights;
            if (HavenKit.Select("##visitor-rights", "Who can possess it", RightsNames, ref rights)) command = new(RoomAction.VisitorRights, i, Level: rights);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Checked on everyone's PC. You can always possess it yourself.");
        }
        else HavenKit.Caption(row.Rights switch
        {
            VisitRights.Anyone => "Anyone here can possess it.",
            VisitRights.MeAndHost => $"{Cap(row.BroughtBy)} and the home's owner can possess it.",
            _ => $"Only {row.BroughtBy} can possess it.",
        });
        // Its mods: the bringer sends them as the home's owner does for the home's NPCs (NpcModShare).
        ImGui.Dummy(Px(0, Space1));
        HavenKit.Caption(row.Mods.Length > 0 ? row.Mods : row.HasMods ? "Its mods are kept on this PC." : "No mods kept for it on this PC.");
        if (row.Mine)
        {
            if (model.SharingMods == row.Id)
            {
                if (HavenKit.Button("Finish sharing##visitor-mods-finish", ButtonKind.Secondary, new(-1, 0))) command = new(RoomAction.VisitorFinishMods, i);
            }
            else
            {
                var why = model.SharingMods != null ? "Another NPC's mods are being shared."
                    : !model.InGathering ? "There's nobody else here to send them to."
                    : !row.HasMods ? "It has no mods kept here. Save it to Your NPCs from a home where it keeps them."
                    : !row.Glamoured ? "It needs a Glamourer look: you wear it while its mods are sent."
                    : row.Possessed ? "Let go of it first."
                    : !row.Present ? "It isn't here right now."
                    : "";
                ImGui.BeginDisabled(why.Length > 0);
                if (HavenKit.Button("Send its mods##visitor-mods-send", ButtonKind.Secondary, new(-1, 0))) command = new(RoomAction.VisitorSendMods, i);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(why.Length > 0 ? why : "You wear its mods and look for a moment, so everyone's sync plugins pick them up. Then your own look comes back.");
            }
        }
        if (model.ModsNote.Length > 0 && row.Mine) HavenKit.Caption(model.ModsNote);
        HavenKit.Caption(ModsNote);
        ImGui.PopID();
    }

    private static string Cap(string name) => name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;

    public static void Yours(NpcPanelModel model, ref RoomCommand command)
    {
        if (model.Roster.Count == 0)
        {
            HavenKit.EmptyState(HavenIcon.Friends, "No saved NPCs yet",
                "Save an NPC from a home you decorate (This home, then Save to Your NPCs). You can then add it to another home, or bring it along when you visit a friend's.");
            if (model.VisitNote.Length > 0) HavenKit.Caption(model.VisitNote);
            return;
        }
        var chosen = model.Roster.ToList().FindIndex(x => x.Id == chosenSaved);
        if (chosen < 0) { chosen = 0; chosenSaved = model.Roster[0].Id; }
        for (var i = 0; i < model.Roster.Count; i++)
        {
            var row = model.Roster[i];
            var detail = string.Join(" · ", new[] { row.Level > 0 ? $"Lv{row.Level}" : "", row.Glamoured ? "Glamourer" : "", row.HasBody ? "Customize+" : "", ModsLabel(row.HasMods, row.ModCount) }.Where(x => x.Length > 0));
            if (HavenKit.ListRow($"##saved-row{row.Id}", row.Name, detail, i == chosen, HavenIcon.Friends)) { chosenSaved = row.Id; chosen = i; }
            ImGui.Dummy(Px(0, 2));
        }
        ImGui.Dummy(Px(0, Space2));
        HavenKit.Rule();
        ImGui.Dummy(Px(0, Space2));

        var saved = model.Roster[chosen];
        var width = ImGui.GetContentRegionAvail().X;
        var half = (width - Px(Space2)) / 2;
        ImGui.PushID(saved.Id.ToString());
        if ((renaming != saved.Id || shownName != saved.Name) && !ImGui.IsAnyItemActive()) { renaming = saved.Id; shownName = nameDraft = saved.Name; }
        var left = HavenKit.CommitField("##saved-name", "Name", ref nameDraft, "First and last name", -1, HomeNpcs.MaxName);
        var draft = nameDraft.Trim();
        if (left && renaming == saved.Id && draft != saved.Name && HomeNpcs.PlayerStyleName(draft)) command = new(RoomAction.RosterRename, chosen, Text: draft);
        if (draft != saved.Name && draft.Length > 0 && !HomeNpcs.PlayerStyleName(draft))
            HavenKit.Caption("Use a first and last name, each starting with a capital letter (letters, ' and - only).");
        ImGui.Dummy(Px(0, Space1));

        ImGui.BeginDisabled(!model.Editable || model.Npcs.Count >= HomeNpcs.Max);
        if (HavenKit.Button("Add to this home##saved-add", ButtonKind.Secondary, new(half, 0))) command = new(RoomAction.RosterBringHome, chosen);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!model.Editable ? "Only the home's owner and its decorators can add NPCs to it."
                : model.Npcs.Count >= HomeNpcs.Max ? $"A home holds up to {HomeNpcs.Max} NPCs." : "A copy that stays in this home, a step in front of you.");
        ImGui.SameLine(0, Px(Space2));
        ImGui.BeginDisabled(model.CannotBring != null);
        if (HavenKit.Button("Bring it along##saved-visit", ButtonKind.Primary, new(half, 0))) command = new(RoomAction.RosterBringVisit, chosen);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(model.CannotBring ?? "A visitor for this gathering only: the home's owner lets it in, and it leaves with you.");
        ImGui.Dummy(Px(0, Space2));
        if (HavenKit.Button("Remove from Your NPCs…##saved-remove", ButtonKind.Danger)) { removing = saved.Id; ImGui.OpenPopup("##saved-remove"); }
        if (removing == saved.Id && HavenKit.BeginDialog("##saved-remove", $"Remove {saved.Name}?"))
        {
            HavenKit.Caption("It leaves Your NPCs on this PC. Homes it was added to keep their own copy.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Remove") + Px(Space2));
            if (HavenKit.Button("Cancel", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Remove", ButtonKind.Danger)) { command = new(RoomAction.RosterRemove, chosen); ImGui.CloseCurrentPopup(); }
            HavenKit.EndDialog();
        }
        ImGui.Dummy(Px(0, Space2));
        HavenKit.Caption(saved.HasMods ? "Its mods kept on this PC come along when you add it to a home or bring it to a gathering." : ModsNote);
        if (model.VisitNote.Length > 0) HavenKit.Caption(model.VisitNote);
        ImGui.PopID();
    }
}
