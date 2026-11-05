using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

// One of the home's NPCs as the NPCs panel shows it. GameName is the name its character carries (Glamourer knows it by
// that); Present means it is drawn here now; Resting, that it rests in a saved emote; Seated, that it has a saved pose ball;
// Level, the level its nameplate shows (0: none).
internal readonly record struct NpcRow(Guid Id, string Name, string GameName, bool Away, bool Present, bool Possessed, bool HeldByOther, bool Glamoured,
    bool Resting = false, bool AdvancedColours = false, bool Seated = false, int Level = 0)
{
    // Its mods (NpcModShare): the Penumbra collection last used to capture them on this PC, the line about them, and
    // whether this PC keeps them; and whether it has a Customize+ profile.
    public Guid? Collection { get; init; }
    public string Mods { get; init; } = "";
    public bool HasMods { get; init; }
    public bool HasBody { get; init; }
    public int ModCount { get; init; }   // how many mods its kept files come from (0: not known)
}

internal sealed record NpcPanelModel(bool Editable, IReadOnlyList<NpcRow> Npcs, string Status)
{
    // The player's Glamourer designs, Penumbra collections and Customize+ profiles (asked only while a list is open).
    public Func<IReadOnlyList<(Guid Id, string Name)>> Designs { get; init; } = () => [];
    public Func<IReadOnlyList<(Guid Id, string Name)>> Collections { get; init; } = () => [];
    public Func<IReadOnlyList<(Guid Id, string Name)>> BodyProfiles { get; init; } = () => [];
    // Whether the player may share NPCs' mods here (the home's owner), whether others are here to send them to, the NPC
    // whose mods are being shared now, and what the last share said.
    public bool MayShareMods { get; init; }
    public bool InGathering { get; init; }
    public Guid? SharingMods { get; init; }
    public string ModsNote { get; init; } = "";
    // The player's saved NPCs and the visiting NPCs here (NpcRosterUi): why the player can't bring one along now (null:
    // they can), whether they own this home (they let visitors in or send them away) and turn visitors away.
    public IReadOnlyList<RosterRow> Roster { get; init; } = [];
    public IReadOnlyList<VisitorRow> Visitors { get; init; } = [];
    public string? CannotBring { get; init; } = "Join a gathering in someone else's Haven to bring an NPC along.";
    public bool OwnsHome { get; init; }
    public bool RefusesVisitors { get; init; }
    public string VisitNote { get; init; } = "";
}

// The NPCs panel: the home's NPCs, and below them the chosen one: its name and level, Possess and
// whether it's in the home, then its Look, Mods and Place on tabs, and Remove. Explanations are tooltips. Only the
// owner and decorators possess, add or change them.
internal static class NpcPanelUi
{
    /// <summary>Said before the player is dressed as an NPC: Glamourer automation puts their own look back (it can't be paused over IPC).</summary>
    /// <summary>Said when a guest who may not decorate tries to possess an NPC.</summary>
    public const string NotYours = "Only the home's owner and its decorators can control its NPCs.";
    /// <summary>Said when someone tries to possess a visiting NPC its owner didn't open to them.</summary>
    public const string NotYourVisitor = "Its owner hasn't let you control it.";
    public const string AutomationNote = "If Glamourer automation is set up for your character, switch it off first (Glamourer, Automation tab): it puts your own look back, and the share stops.";
    private static Guid selected, editing, dressing, removing, shaping, capturing;
    private static string nameDraft = "", shownName = "", designFilter = "", levelDraft = "";
    private static Guid levelFor;
    private static int shownLevel = -1, tab, collectionChoice, view;
    private static IReadOnlyList<(Guid Id, string Name)> designs = [];
    private static IReadOnlyList<(Guid Id, string Name)> collections = [];
    private static readonly string[] Tabs = ["Look", "Mods", "Place"];

    /// <summary>For the UI preview: which tab the chosen NPC shows.</summary>
    internal static void ShowTab(int index) => tab = index;
    /// <summary>For the UI preview: This home (0), Visiting (1) or Yours (2).</summary>
    internal static void ShowView(int index) => view = index;

    public static RoomCommand Draw(NpcPanelModel model, ref bool open, float top)
    {
        var command = default(RoomCommand);
        if (SidePanelUi.Begin("NPCs", "Haven.NpcPanel", ref open, top))
        {
            HavenKit.Segmented("##npc-view", ["This home", model.Visitors.Count > 0 ? $"Visiting · {model.Visitors.Count}" : "Visiting", "Yours"], ref view);
            ImGui.Dummy(Px(0, Space2));
            if (view == 1) NpcRosterUi.Visiting(model, ref command);
            else if (view == 2) NpcRosterUi.Yours(model, ref command);
            else if (model.Npcs.Count == 0)
            {
                if (HavenKit.EmptyState(HavenIcon.Friends, "No NPCs yet",
                        "An NPC stays in this home for everyone who visits. Add one in your look, then possess it to walk, emote and talk as it while your own character stays put.",
                        model.Editable ? "Add NPC" : ""))
                    command = new(RoomAction.AddNpc);
            }
            else
            {
                var chosen = model.Npcs.ToList().FindIndex(x => x.Id == selected);
                if (chosen < 0) { chosen = 0; selected = model.Npcs[0].Id; }
                for (var i = 0; i < model.Npcs.Count; i++)
                {
                    var npc = model.Npcs[i];
                    if (HavenKit.ListRow($"##npc-row{npc.Id}", npc.Name, Status(npc), i == chosen, HavenIcon.Friends)) { selected = npc.Id; chosen = i; }
                    ImGui.Dummy(Px(0, 2));
                }
                ImGui.BeginDisabled(!model.Editable || model.Npcs.Count >= Core.HomeNpcs.Max);
                if (HavenKit.Button("Add NPC##npc-add", ButtonKind.Ghost, new(-1, 0), HavenIcon.Plus)) command = new(RoomAction.AddNpc);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(model.Npcs.Count >= Core.HomeNpcs.Max ? $"A home holds up to {Core.HomeNpcs.Max} NPCs." : "A new NPC in your look, a step in front of you.");
                ImGui.Dummy(Px(0, Space2));
                HavenKit.Rule();
                ImGui.Dummy(Px(0, Space2));
                Details(model, chosen, ref command);
            }
            if (view == 0 && !model.Editable) HavenKit.Caption("Only the home's owner and its decorators can control or change its NPCs.");
            if (view == 0 && model.Status.Length > 0) HavenKit.Caption(model.Status);
        }
        SidePanelUi.End(open);
        return command;
    }

    private static string Status(NpcRow npc) =>
        npc.Possessed ? "You're controlling it"
        : npc.HeldByOther ? "Someone is controlling it"
        : npc.Away ? "Away"
        : !npc.Present ? "Arriving…"
        : string.Join(" · ", new[] { "Here", npc.HasBody ? "Customize+" : "",
            !npc.HasMods ? "" : npc.ModCount > 0 ? $"{npc.ModCount} mod{(npc.ModCount == 1 ? "" : "s")}" : "mods" }.Where(x => x.Length > 0));

    private static void Details(NpcPanelModel model, int i, ref RoomCommand command)
    {
        var npc = model.Npcs[i];
        var width = ImGui.GetContentRegionAvail().X;
        var half = (width - Px(Space2)) / 2;
        ImGui.PushID(npc.Id.ToString());

        // Name and level on one row; leaving a field saves it.
        var levelWidth = Px(80);
        if ((editing != npc.Id || shownName != npc.Name) && !ImGui.IsAnyItemActive()) { editing = npc.Id; shownName = nameDraft = npc.Name; }
        if ((levelFor != npc.Id || shownLevel != npc.Level) && !ImGui.IsAnyItemActive()) { levelFor = npc.Id; shownLevel = npc.Level; levelDraft = npc.Level > 0 ? npc.Level.ToString() : ""; }
        ImGui.BeginDisabled(!model.Editable);
        // Placed by hand rather than SameLine: ImGui lowers a label that follows a framed field on the same line (to
        // line text up with frames), which put the Level column below the Name column.
        var start = ImGui.GetCursorScreenPos();
        ImGui.BeginGroup();
        var nameLeft = HavenKit.CommitField("##npc-name", "Name", ref nameDraft, "First and last name", width - levelWidth - Px(Space2), Core.HomeNpcs.MaxName);
        ImGui.EndGroup();
        var nameBottom = ImGui.GetCursorScreenPos().Y;
        ImGui.SetCursorScreenPos(new(start.X + width - levelWidth, start.Y));
        ImGui.BeginGroup();
        var levelLeft = HavenKit.CommitField("##npc-level", "Level", ref levelDraft, "None", levelWidth, 3);
        ImGui.EndGroup();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shown when it's targeted, as a player's level is. Leave it empty for none.");
        ImGui.SetCursorScreenPos(new(start.X, Math.Max(nameBottom, ImGui.GetCursorScreenPos().Y)));
        ImGui.EndDisabled();
        var draft = nameDraft.Trim();
        var fullName = Core.HomeNpcs.PlayerStyleName(draft);
        if (nameLeft && editing == npc.Id && draft != npc.Name && fullName) command = new(RoomAction.RenameNpc, i, Text: draft);
        var levelText = levelDraft.Trim();
        var level = levelText.Length == 0 ? 0 : int.TryParse(levelText, out var typed) && typed is >= 1 and <= Core.HomeNpcs.MaxLevel ? typed : -1;
        if (levelLeft && levelFor == npc.Id && level >= 0 && level != npc.Level) command = new(RoomAction.NpcLevel, i, Level: level);
        if (draft != npc.Name && draft.Length > 0 && !fullName) Warn("Use a first and last name, each starting with a capital letter (letters, ' and - only).");
        else if (npc.GameName != npc.Name) HavenKit.Caption($"NPCs need a first and last name. Until you give it one, Glamourer knows it as “{npc.GameName}”.");
        if (level < 0) Warn($"A level from 1 to {Core.HomeNpcs.MaxLevel}, or empty for none.");
        ImGui.Dummy(Px(0, Space1));

        // Possess, and whether it's here.
        ImGui.BeginDisabled(npc.HeldByOther || !npc.Possessed && (!npc.Present || !model.Editable));
        if (HavenKit.Button((npc.Possessed ? "Let go" : "Possess") + "##npc-possess", npc.Possessed ? ButtonKind.Secondary : ButtonKind.Primary, new(half, 0))) command = new(RoomAction.PossessNpc, i);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(npc.HeldByOther ? "Someone else is controlling it right now."
                : !model.Editable && !npc.Possessed ? NotYours : "Walk, emote and talk as it; your own character stays where it is.");
        ImGui.SameLine(0, Px(Space2));
        var here = !npc.Away;
        ImGui.BeginDisabled(!model.Editable);
        if (HavenKit.Toggle("In the home##npc-here", ref here, half)) command = new(RoomAction.NpcAway, i, Level: here ? 0 : 1);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Turn off to send it away for a while. It keeps its look and place.");
        ImGui.Dummy(Px(0, Space2));

        HavenKit.Segmented("##npc-tab", Tabs, ref tab);
        ImGui.Dummy(Px(0, Space2));
        switch (tab)
        {
            case 0: Look(model, npc, i, half, ref command); break;
            case 1: Mods(model, npc, i, half, ref command); break;
            default: Place(model, npc, i, half, ref command); break;
        }
        ImGui.Dummy(Px(0, Space3));

        ImGui.BeginDisabled(!model.Editable);
        if (HavenKit.Button("Remove NPC…##npc-remove", ButtonKind.Danger)) { removing = npc.Id; ImGui.OpenPopup("##npc-remove"); }
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("Save to Your NPCs##npc-save", ButtonKind.Ghost)) command = new(RoomAction.SaveNpcToRoster, i);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Keep a copy of its name, level and look on this PC, to add to another home or bring along when you visit a friend.");
        Dialogs(model, npc, i, ref command);
        ImGui.PopID();
    }

    private static void Warn(string text)
    {
        using (HavenFonts.Use(HavenText.Small))
        { ImGui.PushStyleColor(ImGuiCol.Text, Warning); ImGui.TextWrapped(text); ImGui.PopStyleColor(); }
    }

    // Its look: copy yours (Glamourer, Customize+ and, for the owner, the mods you're drawn with), or pick a design or
    // a profile; then the rarer Glamourer chores.
    private static void Look(NpcPanelModel model, NpcRow npc, int i, float half, ref RoomCommand command)
    {
        ImGui.BeginDisabled(!model.Editable);
        if (HavenKit.Button("Copy my look##npc-copy", ButtonKind.Primary, new(-1, 0))) command = new(RoomAction.CopyNpcLook, i);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(model.MayShareMods
                ? "Dress it as you look right now: your Glamourer look, your Customize+ profile and the mods you're drawn with."
                : "Dress it as you look right now: your Glamourer look and Customize+ profile.");
        ImGui.BeginDisabled(npc.Away || !npc.Present);
        if (HavenKit.Button("Glamourer design…##npc-design", ButtonKind.Secondary, new(half, 0))) { dressing = npc.Id; designs = model.Designs(); designFilter = ""; ImGui.OpenPopup("##npc-design"); }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Dress it in one of your Glamourer designs.");
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button((npc.HasBody ? "Customize+ (on)" : "Customize+…") + "##npc-body", ButtonKind.Secondary, new(half, 0)))
        { shaping = npc.Id; designs = model.BodyProfiles(); designFilter = ""; ImGui.OpenPopup("##npc-body"); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shape its body with one of your Customize+ profiles. It's kept with the home, so every guest with Customize+ sees it.");
        ImGui.BeginDisabled(npc.Away || !npc.Present);
        if (HavenKit.Button("Keep Glamourer edits##npc-keep", ButtonKind.Ghost, new(npc.AdvancedColours ? half : -1, 0))) command = new(RoomAction.KeepNpcGlamour, i);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip($"Edit it in Glamourer's Actors tab (it's listed as {npc.GameName}), then keep what it wears now.");
        if (npc.AdvancedColours)
        {
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Reset colours##npc-colours", ButtonKind.Ghost, new(half, 0))) command = new(RoomAction.ResetNpcColours, i);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Its look sets some Advanced Customization colours (skin, hair, eyes, lips…), which win over the ordinary colours. Let them go so the ordinary colours show and can be changed.");
        }
        ImGui.EndDisabled();
        HavenKit.Caption("Guests with Glamourer and Customize+ see this look exactly.");
    }

    // Its mods: copied with your look, sent to guests again, or captured from a Penumbra collection (NpcModShare).
    private static void Mods(NpcPanelModel model, NpcRow npc, int i, float half, ref RoomCommand command)
    {
        HavenKit.Caption(npc.Mods.Length > 0 ? npc.Mods : npc.HasMods ? "Its mods are kept on this PC." : "No mods kept for it on this PC.");
        if (model.MayShareMods)
        {
            if (model.SharingMods == npc.Id)
            {
                if (HavenKit.Button("Finish sharing##npc-mods-finish", ButtonKind.Secondary, new(-1, 0))) command = new(RoomAction.FinishNpcMods, i);
            }
            else
            {
                var why = model.SharingMods != null ? "Another NPC's mods are being shared."
                    : !model.InGathering ? "Host a gathering first: this sends its mods to the guests there."
                    : !npc.HasMods ? "It has no mods kept here. Copy your look to it while you wear them."
                    : !npc.Glamoured ? "It needs a Glamourer look: you wear it while its mods are sent."
                    : npc.Possessed ? "Let go of it first."
                    : !npc.Present ? "It isn't here right now."
                    : "";
                ImGui.BeginDisabled(why.Length > 0);
                if (HavenKit.Button("Send its mods to guests##npc-mods-send", ButtonKind.Primary, new(-1, 0))) command = new(RoomAction.SendNpcMods, i);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(why.Length > 0 ? why : "You wear its mods and look for a moment, so your guests' sync plugins pick them up. Then your own look comes back.");
                ImGui.BeginDisabled(model.SharingMods != null || !npc.Glamoured || npc.Possessed);
                if (HavenKit.Button("From a collection…##npc-mods-from", ButtonKind.Ghost, new(half, 0)))
                {
                    capturing = npc.Id; collections = model.Collections();
                    collectionChoice = npc.Collection is { } last ? Math.Max(0, collections.ToList().FindIndex(x => x.Id == last)) : 0;
                    ImGui.OpenPopup("##npc-collection");
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("For a look you haven't worn: wear a Penumbra collection with its Glamourer look for a moment, and keep the mods it's drawn with.");
                ImGui.SameLine(0, Px(Space2));
            }
        }
        if (npc.HasMods && model.SharingMods != npc.Id)
        {
            if (HavenKit.Button("Forget its mods##npc-mods-forget", ButtonKind.Ghost, new(model.MayShareMods ? half : -1, 0))) command = new(RoomAction.ForgetNpcMods, i);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Drop its mods on this PC; it shows its plain look here.");
        }
        else if (model.MayShareMods && model.SharingMods != npc.Id) ImGui.NewLine();
        if (model.ModsNote.Length > 0) HavenKit.Caption(model.ModsNote);
        HavenKit.Caption(model.MayShareMods
            ? "Guests paired with you in Lightless or PlayerSync get its mods through their sync plugin; each PC keeps them for later visits."
            : "When the owner shares its mods and your sync plugin is paired with them, you get them too, for later visits as well.");
    }

    private static void Place(NpcPanelModel model, NpcRow npc, int i, float half, ref RoomCommand command)
    {
        if (npc.Seated || npc.Resting) HavenKit.Caption(npc.Seated ? "It waits on a pose ball." : "It rests in an emote.");
        ImGui.BeginDisabled(!model.Editable || npc.HeldByOther || npc.Possessed);
        if (HavenKit.Button("Bring to me##npc-bring", ButtonKind.Secondary, new(half, 0))) command = new(RoomAction.BringNpc, i);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stand it a step in front of you, and keep it there.");
        ImGui.SameLine(0, Px(Space2));
        ImGui.BeginDisabled(npc.Away || !npc.Present);
        if (HavenKit.Button("Save its spot##npc-spot", ButtonKind.Secondary, new(half, 0))) command = new(RoomAction.SaveNpcSpot, i);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Keep it where it stands now, sitting or emoting if it is, or on its pose ball, for every visit.");
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        HavenKit.Caption("Possess it to walk it somewhere, sit it down with an emote or put it on a pose ball, then save its spot.");
    }

    private static void Dialogs(NpcPanelModel model, NpcRow npc, int i, ref RoomCommand command)
    {
        if (removing == npc.Id && HavenKit.BeginDialog("##npc-remove", $"Remove {npc.Name}?"))
        {
            HavenKit.Caption("It leaves the home for good, with its look and place. To keep it for later, send it away instead. Undo brings it back.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Remove") + Px(Space2));
            if (HavenKit.Button("Cancel", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Remove", ButtonKind.Danger)) { command = new(RoomAction.RemoveNpc, i); ImGui.CloseCurrentPopup(); }
            HavenKit.EndDialog();
        }
        if (dressing == npc.Id && HavenKit.BeginDialog("##npc-design", $"Dress {npc.Name}"))
        {
            if (designs.Count == 0) HavenKit.Caption("No Glamourer designs found. Install Glamourer and save a design there first.");
            else if (Choices("design", "Search your designs") is { } design) command = new(RoomAction.DressNpc, i, Text: design.ToString());
            HavenKit.AlignRight(HavenKit.ButtonWidth("Close"));
            if (HavenKit.Button("Close", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            HavenKit.EndDialog();
        }
        if (shaping == npc.Id && HavenKit.BeginDialog("##npc-body", $"Shape {npc.Name}"))
        {
            if (designs.Count == 0) HavenKit.Caption("No Customize+ profiles found. Install Customize+ and make a profile there first.");
            else if (Choices("body", "Search your profiles") is { } profile) command = new(RoomAction.NpcBody, i, Text: profile.ToString());
            HavenKit.AlignRight(HavenKit.ButtonWidth("Close") + (npc.HasBody ? HavenKit.ButtonWidth("Take it away") + Px(Space2) : 0));
            if (npc.HasBody)
            {
                if (HavenKit.Button("Take it away", ButtonKind.Ghost)) { command = new(RoomAction.NpcBody, i, Text: ""); ImGui.CloseCurrentPopup(); }
                ImGui.SameLine(0, Px(Space2));
            }
            if (HavenKit.Button("Close", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            HavenKit.EndDialog();
        }
        if (capturing == npc.Id && HavenKit.BeginDialog("##npc-collection", $"{npc.Name}'s mods from a collection"))
        {
            HavenKit.Caption("You wear this Penumbra collection and its Glamourer look for a moment. Haven keeps the mods you're drawn with for it, and your guests' sync plugins pick them up. Then your own look comes back.", Px(380));
            HavenKit.Caption(AutomationNote, Px(380));
            if (collections.Count == 0) HavenKit.Caption("No Penumbra collections found.");
            else HavenKit.Select("##npc-collection-pick", "Penumbra collection", collections.Select(x => x.Name).ToList(), ref collectionChoice, Px(380));
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Wear it and keep its mods") + Px(Space2));
            if (HavenKit.Button("Cancel", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2));
            ImGui.BeginDisabled(collections.Count == 0);
            if (HavenKit.Button("Wear it and keep its mods", ButtonKind.Primary) && collectionChoice < collections.Count)
            { command = new(RoomAction.ShareNpcMods, i, Text: collections[collectionChoice].Id.ToString()); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            HavenKit.EndDialog();
        }
    }

    // A searchable list of the player's designs or profiles (in `designs`); the one chosen (which closes the dialog).
    private static Guid? Choices(string id, string hint)
    {
        Guid? chosen = null;
        HavenKit.Search($"##npc-{id}-filter", ref designFilter, hint);
        if (ImGui.BeginChild($"##npc-{id}s", new(Px(360), Px(280))))
            foreach (var choice in designs.Where(x => designFilter.Length == 0 || x.Name.Contains(designFilter, StringComparison.CurrentCultureIgnoreCase)))
                if (HavenKit.ListRow($"##{id}{choice.Id}", choice.Name, "", false)) { chosen = choice.Id; ImGui.CloseCurrentPopup(); }
        ImGui.EndChild();
        return chosen;
    }
}
