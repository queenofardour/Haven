using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

// District loading art for a card, or a flat tint where there is none (custom
// layouts, the preview). The game's art is 16:9 with letterbox bands.
internal readonly record struct LauncherArt(ImTextureID Texture, bool HasTexture, Vector4 Tint);
internal sealed record LauncherHome(Guid Id, string Name, string Shell, string Detail, LauncherArt Art, string Badge, bool Deletable)
{
    // Saved before homes belonged to characters: every character sees it until one keeps it.
    public bool Unsorted { get; init; }
}
// Unavailable: why this PC can't use the shell (its zone's data isn't installed, as an Endwalker inn on a free trial);
// its Detail then says which expansion it needs.
internal sealed record LauncherShell(int Index, string Title, string Detail, int Size, LauncherArt Art, string? Unavailable = null);
internal sealed record LauncherVisit(string Title, string Detail, bool Hosting, string Code, bool CanDecorate, bool CanReturnToPlatform, bool CanSaveCopy, bool CanRetry, bool Leaving, string LeaveLabel);
internal sealed record LauncherModel(IReadOnlyList<LauncherHome> Homes, IReadOnlyList<LauncherShell> Shells, LauncherVisit? Visit, bool CanStart, string Status)
{
    // The real house or apartment the player stands in, whose layout can be saved; null elsewhere.
    public string? CopySource { get; init; }
    public IReadOnlyList<LauncherLayout> Layouts { get; init; } = [];
    // This PC's Haven account, for the sign-in asked for before hosting or joining.
    public LauncherAccount Account { get; init; } = new(true, false, null);
}
internal sealed record LauncherAccount(bool SignedIn, bool SigningIn, string? Problem, string? Code = null);
// A saved layout: its name, the shell it was made in, and where it came from.
internal sealed record LauncherLayout(Guid Id, string Name, string Shell, string Detail);

internal enum LauncherAction { None, Enter, Host, Rename, Duplicate, Export, Delete, Create, Join, Settings, Decorate, Leave, ReturnToPlatform, SaveCopy, CopyCode, RetryEntry, Close, SaveLayout, ImportLayout, CreateFromLayout, DeleteLayout, RenameLayout, KeepHome, KeepAllHomes, SignIn, CancelSignIn }
internal readonly record struct LauncherCommand(LauncherAction Action, Guid Home = default, string Text = "", int Shell = -1);

// Per-window launcher state: which view is showing and what each dialog edits.
internal sealed class LauncherState
{
    public bool Creating;
    public string Name = "My haven", JoinCode = "", RenameText = "";
    public int Size = 2, Shell = -1;   // Cottage
    // The saved layout a new home starts from; empty for a bare shell.
    public Guid Layout;
    // Naming a layout being saved, or renaming a saved one (RenameLayout).
    public string LayoutText = "";
    public Guid RenameLayout;
    public bool OpenSaveLayout, OpenRenameLayout;
    public Guid MenuHome, RenameHome, DeleteHome, EnterHome;
    public bool OpenJoin, OpenRename, OpenDelete, OpenEnter, OpenSignIn;
    // Hosting or joining waiting for a Discord sign-in; carried out once signed in.
    public LauncherCommand? AfterSignIn;
    public DateTime CopiedUntil;
}

// The Haven launcher: home cards with district
// art, click to enter, an overflow menu per home, a visual shell picker and Join.
// Draws inside a window that has pushed HavenKit's theme; returns one command.
internal static class LauncherUi
{
    public const float ArtHeight = 132, InfoHeight = 88;
    public static readonly string[] Sizes = ["Inn", "Apartment", "Cottage", "House", "Mansion", "Custom"];
    public const int CustomSize = 5;   // the custom canvas (HomeLayoutPickerUi.Custom)
    // For the standalone preview's click checks, like PlacementUi's bounds.
    public static (Vector2 Min, Vector2 Max) FirstCardBounds { get; private set; }
    public static (Vector2 Min, Vector2 Max) FirstMenuBounds { get; private set; }
    public static (Vector2 Min, Vector2 Max) SecondShellBounds { get; private set; }
    public static (Vector2 Min, Vector2 Max) CreateBounds { get; private set; }
    public static (Vector2 Min, Vector2 Max) SoloBounds { get; private set; }

    public static LauncherCommand Draw(LauncherState state, LauncherModel model)
    {
        var command = Header();
        var footer = model.Status.Length > 0 ? Px(Space6) : 0;
        if (HavenKit.BeginScrollBody("##launcher-body", new(0, -footer)))
        {
            var body = state.Creating ? NewHome(state, model) : Homes(state, model);
            if (body.Action != LauncherAction.None) command = body;
        }
        ImGui.EndChild();
        if (footer > 0) HavenKit.Caption(model.Status);
        var dialog = Dialogs(state, model);
        return dialog.Action != LauncherAction.None ? dialog : command;
    }

    private static LauncherCommand Header()
    {
        var command = default(LauncherCommand); var start = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        var tile = new Vector2(Px(28));
        draw.AddRectFilled(start + Px(0, 4), start + Px(0, 4) + tile, HavenUi.U(AccentTint), Px(RadiusField));
        HavenUi.DrawIcon(draw, HavenIcon.Home, start + Px(0, 4) + tile / 2, Px(16), Accent);
        using (HavenFonts.Use(HavenText.Title)) draw.AddText(start + new Vector2(tile.X + Px(10), Px(4) + (tile.Y - ImGui.GetTextLineHeight()) / 2), HavenUi.U(Text), "Haven");
        const float button = 44;
        ImGui.SetCursorScreenPos(new(start.X + ImGui.GetContentRegionAvail().X - Px(button * 2 + Space1), start.Y - Px(4)));
        if (HavenKit.IconButton("##launcher-settings", HavenIcon.Settings, "Settings, files and connection", size: button)) command = new(LauncherAction.Settings);
        ImGui.SameLine(0, Px(Space1));
        if (HavenKit.IconButton("##launcher-close", HavenIcon.Close, "Close", size: button)) command = new(LauncherAction.Close);
        ImGui.Dummy(Px(0, Space2));
        return command;
    }

    // Homes ----------------------------------------------------------------

    private static LauncherCommand Homes(LauncherState state, LauncherModel model)
    {
        var command = default(LauncherCommand);
        if (model.Visit is {} visit) { var strip = VisitStrip(state, visit); if (strip.Action != LauncherAction.None) command = strip; ImGui.Dummy(Px(0, Space2)); }
        // Inside a real house: its layout can be saved, to start a home from or apply to a home on the same shell.
        else if (model.CopySource is {} source)
        {
            HavenKit.Banner(BannerKind.Info, "You're in " + source, "Save its layout to start a new home from it, or to apply it to a Haven home on the same shell.");
            if (HavenKit.Button("Save this layout…##launcher-save-layout", ButtonKind.Secondary, icon: HavenIcon.Home)) { state.LayoutText = source; state.OpenSaveLayout = true; }
            ImGui.Dummy(Px(0, Space2));
        }
        var top = ImGui.GetCursorPos();
        HavenKit.PageTitle("Your homes", model.Homes.Count == 0 ? "Choose a shell to begin" : $"{model.Homes.Count} saved · choose a home to visit or host");
        var bottom = ImGui.GetCursorPos();
        // With no homes yet, the empty state below carries New home; the header keeps only Join.
        var empty = model.Homes.Count == 0;
        var buttons = (empty ? 0 : HavenKit.ButtonWidth("New home") + Px(Space2)) + HavenKit.ButtonWidth("Join a friend", HavenIcon.Friends);
        ImGui.SetCursorPos(new(top.X, top.Y + Px(Space3))); HavenKit.AlignRight(buttons);
        if (!empty) { if (HavenKit.Button("New home")) { state.Creating = true; state.Shell = -1; } ImGui.SameLine(0, Px(Space2)); }
        ImGui.BeginDisabled(!model.CanStart);
        if (HavenKit.Button("Join a friend", ButtonKind.Primary, icon: HavenIcon.Friends)) state.OpenJoin = true;
        ImGui.EndDisabled();
        ImGui.SetCursorPos(bottom); ImGui.Dummy(Px(0, Space3));

        if (model.Homes.Count == 0)
        {
            ImGui.Dummy(Px(0, Space6));
            if (HavenKit.EmptyState(HavenIcon.Home, "No homes yet", "Choose a shell and make it yours. Your homes are saved on this PC.", "New home"))
            { state.Creating = true; state.Shell = -1; }
            return command;
        }
        // Homes from before they belonged to characters: each is kept by the character who should have it.
        var unsorted = model.Homes.Count(x => x.Unsorted);
        if (unsorted > 0)
        {
            HavenKit.Banner(BannerKind.Info, $"{unsorted} unsorted home{(unsorted == 1 ? "" : "s")}",
                "Homes now belong to the character who makes them. These were saved before that, so every character sees them until one keeps them: open a home's More options and choose Keep for this character.");
            if (HavenKit.Button($"Keep {(unsorted == 1 ? "it" : "all " + unsorted)} for this character##launcher-keep-all", ButtonKind.Secondary)) command = new(LauncherAction.KeepAllHomes);
            ImGui.Dummy(Px(0, Space3));
        }
        var width = ImGui.GetContentRegionAvail().X; var gap = Px(Space4);
        var columns = Math.Max(1, (int)((width + gap) / (Px(280) + gap)));
        var card = new Vector2((width - gap * (columns - 1)) / columns, Px(ArtHeight + InfoHeight));
        for (var i = 0; i <= model.Homes.Count; i++)
        {
            if (i % columns != 0) ImGui.SameLine(0, gap); else if (i > 0) ImGui.Dummy(new(0, Math.Max(0, gap - 2 * ImGui.GetStyle().ItemSpacing.Y)));
            if (i == model.Homes.Count)
            {
                if (HavenKit.GhostCard("##launcher-new", card, HavenIcon.Plus, "New home", "Choose a shell from the gallery")) { state.Creating = true; state.Shell = -1; }
                break;
            }
            var home = model.Homes[i];
            var result = HomeCard(state, home, card, model.CanStart, i == 0);
            if (result.Action != LauncherAction.None) command = result;
        }
        var menu = HomeMenu(state, model);
        return menu.Action != LauncherAction.None ? menu : command;
    }

    private static LauncherCommand HomeCard(LauncherState state, LauncherHome home, Vector2 size, bool canStart, bool first)
    {
        var command = default(LauncherCommand); var id = home.Id.ToString("N");
        // The home you are in goes straight back to decorating; any other asks how to enter.
        if (HavenKit.Card("##home-" + id, size, false, out var min))
        {
            if (home.Badge.Length > 0) command = new(LauncherAction.Enter, home.Id);
            else { state.EnterHome = home.Id; state.OpenEnter = true; }
        }
        ImGui.SetItemAllowOverlap();
        var hovered = ImGui.IsItemHovered(); var draw = ImGui.GetWindowDrawList();
        // A name too long for its card shows in full on hover.
        if (hovered && HavenFonts.Measure(HavenText.Title, home.Name).X > size.X - Px(Space4 + 44)) HavenKit.Tooltip(home.Name);
        Art(draw, home.Art, min + Px(1, 1), new(size.X - Px(2), Px(ArtHeight)), hovered);
        var x = min.X + Px(Space4); var y = min.Y + Px(ArtHeight + Space3);
        draw.PushClipRect(new(x, y), new(min.X + size.X - Px(44), min.Y + size.Y), true);
        using (HavenFonts.Use(HavenText.Title)) { draw.AddText(new(x, y), HavenUi.U(Text), home.Name); y += ImGui.GetTextLineHeight() + Px(5); }
        using (HavenFonts.Use(HavenText.Small)) { draw.AddText(new(x, y), HavenUi.U(TextOnRaised), home.Shell); y += ImGui.GetTextLineHeight() + Px(5); }
        using (HavenFonts.Use(HavenText.Caption)) draw.AddText(new(x, y), HavenUi.U(TextTertiary), home.Detail);
        draw.PopClipRect();
        if (home.Badge.Length > 0) { ImGui.SetCursorScreenPos(min + Px(Space3, Space3)); HavenKit.Badge(home.Badge, Accent); }
        else if (home.Unsorted) { ImGui.SetCursorScreenPos(min + Px(Space3, Space3)); HavenKit.Badge("Unsorted", TextSecondary); }
        ImGui.SetCursorScreenPos(new(min.X + size.X - Px(Space2 + 32), min.Y + Px(ArtHeight + Space2)));
        if (HavenKit.IconButton("##home-more-" + id, HavenIcon.More, "More options", size: 32)) { state.MenuHome = home.Id; ImGui.OpenPopup("##home-menu"); }
        if (first) { FirstMenuBounds = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax()); FirstCardBounds = (min, min + size); }
        if (hovered && !canStart) HavenKit.Tooltip("Leave your current home first.");
        // End on the card's own rectangle so the grid's SameLine places the next card.
        ImGui.SetCursorScreenPos(min); ImGui.Dummy(size);
        return command;
    }

    private static LauncherCommand HomeMenu(LauncherState state, LauncherModel model)
    {
        var command = default(LauncherCommand);
        if (!HavenKit.BeginMenu("##home-menu")) return command;
        var home = model.Homes.FirstOrDefault(x => x.Id == state.MenuHome);
        if (home != null)
        {
            if (home.Unsorted && HavenKit.MenuItem("Keep for this character")) command = new(LauncherAction.KeepHome, home.Id);
            if (HavenKit.MenuItem("Rename…", enabled: home.Deletable)) { state.RenameHome = home.Id; state.RenameText = home.Name; state.OpenRename = true; }
            if (HavenKit.MenuItem("Duplicate")) command = new(LauncherAction.Duplicate, home.Id);
            if (HavenKit.MenuItem("Export…")) command = new(LauncherAction.Export, home.Id);
            HavenKit.MenuRule();
            if (HavenKit.MenuItem("Delete…", danger: true, enabled: home.Deletable)) { state.DeleteHome = home.Id; state.OpenDelete = true; }
        }
        ImGui.EndPopup();
        return command;
    }

    // The current visit, above the cards: where you are and what you can do there.
    private static LauncherCommand VisitStrip(LauncherState state, LauncherVisit visit)
    {
        var command = default(LauncherCommand); var width = ImGui.GetContentRegionAvail().X; var height = Px(64);
        var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + new Vector2(width, height), HavenUi.U(AccentTint), Px(RadiusCard));
        draw.AddRect(min, min + new Vector2(width, height), HavenUi.U(Accent with { W = .45f }), Px(RadiusCard));
        draw.AddCircleFilled(min + new Vector2(Px(Space4 + 4), height / 2), Px(4), HavenUi.U(visit.Hosting ? Accent : Success));
        var x = min.X + Px(Space4 + 16);
        float titleHeight;
        using (HavenFonts.Use(HavenText.Strong)) { titleHeight = ImGui.GetTextLineHeight(); draw.AddText(new(x, min.Y + Px(12)), HavenUi.U(Text), visit.Title); }
        using (HavenFonts.Use(HavenText.Small)) draw.AddText(new(x, min.Y + Px(14) + titleHeight), HavenUi.U(TextSecondary), visit.Detail);
        var actions = new List<(string Label, LauncherAction Action, ButtonKind Kind, bool Enabled)>();
        if (visit.Hosting && visit.Code.Length > 0) actions.Add((DateTime.UtcNow < state.CopiedUntil ? "Copied" : "Copy visit code", LauncherAction.CopyCode, ButtonKind.Ghost, true));
        if (visit.CanSaveCopy) actions.Add(("Save a copy", LauncherAction.SaveCopy, ButtonKind.Ghost, true));
        if (visit.CanReturnToPlatform) actions.Add(("Return to platform", LauncherAction.ReturnToPlatform, ButtonKind.Ghost, true));
        if (visit.CanRetry) actions.Add(("Retry entry", LauncherAction.RetryEntry, ButtonKind.Secondary, true));
        if (visit.CanDecorate) actions.Add(("Decorate", LauncherAction.Decorate, ButtonKind.Primary, true));
        actions.Add((visit.LeaveLabel, LauncherAction.Leave, ButtonKind.Secondary, !visit.Leaving));
        var total = actions.Sum(a => HavenKit.ButtonWidth(a.Label)) + Px(Space2) * (actions.Count - 1);
        ImGui.SetCursorScreenPos(new(min.X + width - Px(Space3) - total, min.Y + (height - Px(ControlHeight)) / 2));
        for (var i = 0; i < actions.Count; i++)
        {
            if (i > 0) ImGui.SameLine(0, Px(Space2));
            ImGui.BeginDisabled(!actions[i].Enabled);
            if (HavenKit.Button(actions[i].Label + "##visit-" + i, actions[i].Kind)) command = new(actions[i].Action);
            if (command.Action == LauncherAction.CopyCode && actions[i].Action == LauncherAction.CopyCode) state.CopiedUntil = DateTime.UtcNow.AddSeconds(2);
            ImGui.EndDisabled();
        }
        ImGui.SetCursorScreenPos(min); ImGui.Dummy(new(width, height));
        return command;
    }

    // New home: a visual shell picker -------------------------------------

    private static LauncherCommand NewHome(LauncherState state, LauncherModel model)
    {
        var command = default(LauncherCommand);
        if (HavenKit.Button("Your homes##launcher-back", ButtonKind.Ghost, icon: HavenIcon.Back)) state.Creating = false;
        ImGui.Dummy(Px(0, Space1));
        var available = Sizes.Select((_, size) => model.Shells.Any(x => x.Size == size)).ToArray();
        var shells = model.Shells.Where(x => x.Size == state.Size).ToArray();
        if (state.Shell < 0 || shells.All(x => x.Index != state.Shell || x.Unavailable != null)) state.Shell = shells.FirstOrDefault(x => x.Unavailable == null)?.Index ?? -1;
        // The title row carries Create, so it stays in view however long the gallery is.
        var top = ImGui.GetCursorPos();
        HavenKit.PageTitle("New home", "Choose a shell. You can furnish it once you're inside.");
        var bottom = ImGui.GetCursorPos();
        const string label = "Create home";
        ImGui.SetCursorPos(new(top.X, top.Y + Px(Space3))); HavenKit.AlignRight(HavenKit.ButtonWidth(label));
        var layout = model.Layouts.FirstOrDefault(x => x.Id == state.Layout);
        if (layout == null) state.Layout = Guid.Empty;
        ImGui.BeginDisabled(layout == null && state.Shell < 0 || string.IsNullOrWhiteSpace(state.Name));
        if (HavenKit.Button(label, ButtonKind.Primary))
            command = layout != null ? new(LauncherAction.CreateFromLayout, layout.Id, state.Name.Trim()) : new(LauncherAction.Create, Text: state.Name.Trim(), Shell: state.Shell);
        CreateBounds = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        ImGui.EndDisabled();
        ImGui.SetCursorPos(bottom); ImGui.Dummy(Px(0, Space2));
        HavenKit.Field("##launcher-name", "Name", ref state.Name, "My haven", Math.Min(Px(360), ImGui.GetContentRegionAvail().X));
        ImGui.Dummy(Px(0, Space1));
        // Start from a bare shell or a saved layout (a real house's, saved from inside it, or a MakePlace file's). A
        // layout brings its own shell; a MakePlace file that does not say which house it is for takes the one below.
        var starts = model.Layouts.Select(x => $"{x.Name} · {x.Shell}").Prepend("An empty shell").ToArray();
        var start = layout == null ? 0 : model.Layouts.ToList().IndexOf(layout) + 1;
        if (HavenKit.Select("##launcher-start", "Start from", starts, ref start, Math.Min(Px(360), ImGui.GetContentRegionAvail().X)))
            state.Layout = start == 0 ? Guid.Empty : model.Layouts[start - 1].Id;
        if (HavenKit.Button("Import MakePlace layout…##launcher-makeplace", ButtonKind.Secondary)) command = new(LauncherAction.ImportLayout, Shell: state.Shell);
        if (layout != null)
        {
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Rename…##launcher-rename-layout", ButtonKind.Ghost)) { state.RenameLayout = layout.Id; state.LayoutText = layout.Name; state.OpenRenameLayout = true; }
            ImGui.SameLine(0, Px(Space1));
            if (HavenKit.Button("Delete…##launcher-delete-layout", ButtonKind.Ghost)) ImGui.OpenPopup("##launcher-layout-delete");
            HavenKit.Caption($"{layout.Shell} · {layout.Detail}");
            if (HavenKit.BeginDialog("##launcher-layout-delete", "Delete this layout?"))
            {
                HavenKit.Caption($"\"{layout.Name}\" leaves your saved layouts. Homes made from it are kept, and a recovery copy stays in the layouts folder.");
                HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Delete") + Px(Space2));
                if (HavenKit.Button("Keep", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Delete", ButtonKind.Danger)) { command = new(LauncherAction.DeleteLayout, layout.Id); ImGui.CloseCurrentPopup(); }
                HavenKit.EndDialog();
            }
            return command;
        }
        ImGui.Dummy(Px(0, Space2));
        var size = state.Size;
        if (HavenKit.Segmented("launcher-size", Sizes, ref size) && available[size]) { state.Size = size; state.Shell = -1; }
        ImGui.Dummy(Px(0, Space2));
        if (state.Size == CustomSize)
        {
            HavenKit.Banner(BannerKind.Warning, "Experimental", "An 8 × 8 starter platform for building with lofts, partitions and steps. Falling returns you to the platform.");
            ImGui.Dummy(Px(0, Space2));
        }
        var width = ImGui.GetContentRegionAvail().X; var gap = Px(Space3);
        var columns = Math.Max(1, (int)((width + gap) / (Px(220) + gap)));
        var card = new Vector2((width - gap * (columns - 1)) / columns, Px(104 + 64));
        for (var i = 0; i < shells.Length; i++)
        {
            if (i % columns != 0) ImGui.SameLine(0, gap); else if (i > 0) ImGui.Dummy(new(0, Math.Max(0, gap - 2 * ImGui.GetStyle().ItemSpacing.Y)));
            var shell = shells[i];
            ImGui.BeginDisabled(shell.Unavailable != null);
            if (HavenKit.Card("##shell-" + shell.Index, card, state.Shell == shell.Index, out var min)) state.Shell = shell.Index;
            ImGui.EndDisabled();
            if (shell.Unavailable != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) HavenKit.Tooltip(shell.Unavailable);
            if (i == 1) SecondShellBounds = (min, min + card);
            var draw = ImGui.GetWindowDrawList();
            Art(draw, shell.Art, min + Px(1, 1), new(card.X - Px(2), Px(104)), ImGui.IsItemHovered());
            var y = min.Y + Px(104 + 10); var x = min.X + Px(Space3);
            draw.PushClipRect(new(x, y), min + card - Px(Space2, 0), true);
            using (HavenFonts.Use(HavenText.Strong)) { draw.AddText(new(x, y), HavenUi.U(shell.Unavailable != null ? TextTertiary : Text), shell.Title); y += ImGui.GetTextLineHeight() + Px(4); }
            using (HavenFonts.Use(HavenText.Caption)) draw.AddText(new(x, y), HavenUi.U(shell.Unavailable != null ? Warning : TextTertiary), shell.Detail);
            draw.PopClipRect();
        }
        return command;
    }

    // One way to enter a home: an icon, a name and a line of explanation, as a large card.
    private static bool EnterOption(string id, HavenIcon icon, string title, string detail, Vector2 size)
    {
        var clicked = HavenKit.Card(id, size, false, out var min); var draw = ImGui.GetWindowDrawList();
        var center = min + new Vector2(Px(Space4 + 20), Px(Space4 + 20));
        draw.AddCircleFilled(center, Px(20), HavenUi.U(AccentTint), 28);
        HavenUi.DrawIcon(draw, icon, center, Px(20), Accent);
        var y = min.Y + Px(Space4 + 40 + Space3); var x = min.X + Px(Space4); var wrap = size.X - Px(Space4 * 2);
        using (HavenFonts.Use(HavenText.Strong)) { draw.AddText(new(x, y), HavenUi.U(Text), title); y += ImGui.GetTextLineHeight() + Px(4); }
        using (HavenFonts.Use(HavenText.Small)) draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new(x, y), HavenUi.U(TextSecondary), detail, wrap);
        ImGui.SetCursorScreenPos(min); ImGui.Dummy(size);
        return clicked;
    }

    // Draws art cropped to fill the area, rounded at the top like its card.
    private static void Art(ImDrawListPtr draw, LauncherArt art, Vector2 min, Vector2 size, bool hovered)
    {
        var max = min + size; var rounding = Px(RadiusCard - 1);
        if (!art.HasTexture) { draw.AddRectFilled(min, max, HavenUi.U(art.Tint), rounding, ImDrawFlags.RoundCornersTop); return; }
        // Keep inside the letterbox bands (about 7 % top and bottom) and fill the width.
        const float imageAspect = 16f / 9, usable = .86f;
        var aspect = size.X / size.Y; var height = imageAspect / aspect; var width = 1f;
        if (height > usable) { width = usable * aspect / imageAspect; height = usable; }
        var uv0 = new Vector2((1 - width) / 2, (1 - height) / 2);
        draw.AddImageRounded(art.Texture, min, max, uv0, uv0 + new Vector2(width, height), HavenUi.U(hovered ? Vector4.One : new Vector4(.88f, .88f, .88f, 1)), rounding, ImDrawFlags.RoundCornersTop);
    }

    // Dialogs ----------------------------------------------------------------

    // Gatherings on the hosted relay need a Haven account: hosting or joining without one asks for a Discord
    // sign-in here first, then goes ahead by itself once Discord confirms. Closing the dialog drops the visit (the
    // sign-in itself carries on in the browser).
    private static LauncherCommand? SignIn(LauncherState state, LauncherModel model)
    {
        var account = model.Account;
        if (!HavenKit.BeginDialog("##launcher-signin", "Sign in with Discord", 500)) { state.AfterSignIn = null; return null; }
        LauncherCommand? result = null;
        var hosting = state.AfterSignIn?.Action == LauncherAction.Host;
        if (account.SignedIn && state.AfterSignIn is { } pending) { result = pending; state.AfterSignIn = null; ImGui.CloseCurrentPopup(); }
        HavenKit.Caption((hosting ? "Hosting a gathering" : "Joining a gathering") + " needs a Haven account. Sign in with Discord once on this PC: it ties your characters to you, so nobody else can use them. Haven is invite-only for now: a new account needs the access code you were given, entered on the sign-in page.");
        ImGui.Dummy(Px(0, Space1));
        if (account.SigningIn)
        {
            HavenKit.Banner(BannerKind.Info, "Finish in your browser", "Approve Haven on Discord's page, then confirm the code below on Haven's page. " + (hosting ? "Your gathering opens" : "You join") + " by itself when you have.");
            ImGui.Dummy(Px(0, Space1));
            SignInCode(account.Code);
        }
        else if (account.Problem is { } problem)
        {
            HavenKit.Banner(BannerKind.Warning, "Not signed in", problem);
            ImGui.Dummy(Px(0, Space1));
        }
        var action = account.SigningIn ? "Open Discord again" : "Sign in with Discord";
        HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth(action) + Px(Space2));
        if (HavenKit.Button("Cancel##signin", ButtonKind.Ghost))
        { result = account.SigningIn ? new(LauncherAction.CancelSignIn) : result; state.AfterSignIn = null; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button(action + "##signin", ButtonKind.Primary)) result = new(LauncherAction.SignIn);
        HavenKit.EndDialog();
        return result;
    }

    // The sign-in code, large and centred, for comparing with the browser's page.
    internal static void SignInCode(string? code)
    {
        if (code == null) return;
        HavenKit.Caption("Your code");
        using (HavenFonts.Use(HavenText.Display))
            ImGui.TextColored(HavenTokens.Text, code);
        ImGui.Dummy(Px(0, Space1));
    }

    private static LauncherCommand Dialogs(LauncherState state, LauncherModel model)
    {
        var command = default(LauncherCommand);
        if (state.OpenJoin) { ImGui.OpenPopup("##launcher-join"); state.OpenJoin = false; }
        if (state.OpenRename) { ImGui.OpenPopup("##launcher-rename"); state.OpenRename = false; }
        if (state.OpenDelete) { ImGui.OpenPopup("##launcher-delete"); state.OpenDelete = false; }
        if (state.OpenEnter) { ImGui.OpenPopup("##launcher-enter"); state.OpenEnter = false; }
        if (state.OpenSaveLayout) { ImGui.OpenPopup("##launcher-save-layout"); state.OpenSaveLayout = false; }
        if (state.OpenRenameLayout) { ImGui.OpenPopup("##launcher-rename-layout"); state.OpenRenameLayout = false; }
        if (state.OpenSignIn) { ImGui.OpenPopup("##launcher-signin"); state.OpenSignIn = false; }
        // Saving a real house's layout under a name, and renaming a saved layout, share one name field.
        LauncherCommand? Named(string id, string title, string caption, string action, Func<string, LauncherCommand> make)
        {
            if (!HavenKit.BeginDialog(id, title)) return null;
            LauncherCommand? result = null;
            if (caption.Length > 0) HavenKit.Caption(caption);
            var submit = HavenKit.SubmitField(id + "-text", "Name", ref state.LayoutText, "Layout name");
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere(-1);
            var valid = !string.IsNullOrWhiteSpace(state.LayoutText);
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth(action) + Px(Space2));
            if (HavenKit.Button("Cancel" + id, ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2)); ImGui.BeginDisabled(!valid);
            if ((HavenKit.Button(action + id, ButtonKind.Primary) || submit) && valid) { result = make(state.LayoutText.Trim()); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            HavenKit.EndDialog();
            return result;
        }
        if (Named("##launcher-save-layout", "Save this layout", "Its furniture, dyes, finishes and brightness are saved to your layouts. The real house is not changed.", "Save",
            name => new(LauncherAction.SaveLayout, Text: name)) is {} saved) command = saved;
        if (Named("##launcher-rename-layout", "Rename layout", "", "Save", name => new(LauncherAction.RenameLayout, state.RenameLayout, name)) is {} renamed) command = renamed;
        var entering = model.Homes.FirstOrDefault(x => x.Id == state.EnterHome);
        if (HavenKit.BeginDialog("##launcher-enter", entering?.Name ?? "Enter home", 560))
        {
            if (entering != null) HavenKit.Caption(entering.Shell);
            if (!model.CanStart) HavenKit.Banner(BannerKind.Warning, "You're already in a home", "Leave it first, from the strip at the top of the launcher.");
            ImGui.Dummy(Px(0, Space1));
            ImGui.BeginDisabled(!model.CanStart || entering == null);
            var option = new Vector2((ImGui.GetContentRegionAvail().X - Px(Space3)) / 2, Px(132));
            if (EnterOption("##enter-solo", HavenIcon.Home, "Just me", "A private visit. Decorate in peace.", option))
            { command = new(LauncherAction.Enter, state.EnterHome); ImGui.CloseCurrentPopup(); }
            SoloBounds = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            ImGui.SameLine(0, Px(Space3));
            if (EnterOption("##enter-host", HavenIcon.Friends, "Host a gathering", "Friends nearby join with your visit code.", option))
            { command = new(LauncherAction.Host, state.EnterHome); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            ImGui.Dummy(Px(0, Space1));
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel"));
            if (HavenKit.Button("Cancel##enter", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            HavenKit.EndDialog();
        }
        if (SignIn(state, model) is { } signIn) command = signIn;
        if (HavenKit.BeginDialog("##launcher-join", "Join a friend"))
        {
            HavenKit.Caption("Paste the visit code your host shared. Meet them in the same zone, within sight, before joining.");
            var submit = HavenKit.SubmitField("##join-code", "Visit code", ref state.JoinCode, "Visit code");
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere(-1);
            var canJoin = model.CanStart && !string.IsNullOrWhiteSpace(state.JoinCode);
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Join") + Px(Space2));
            if (HavenKit.Button("Cancel##join", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2)); ImGui.BeginDisabled(!canJoin);
            if ((HavenKit.Button("Join", ButtonKind.Primary) || submit) && canJoin) { command = new(LauncherAction.Join, Text: state.JoinCode.Trim()); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            HavenKit.EndDialog();
        }
        if (HavenKit.BeginDialog("##launcher-rename", "Rename home"))
        {
            var rename = HavenKit.SubmitField("##rename-text", "Name", ref state.RenameText, "My haven");
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere(-1);
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Save") + Px(Space2));
            if (HavenKit.Button("Cancel##rename", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2)); ImGui.BeginDisabled(string.IsNullOrWhiteSpace(state.RenameText));
            if ((HavenKit.Button("Save", ButtonKind.Primary) || rename) && !string.IsNullOrWhiteSpace(state.RenameText)) { command = new(LauncherAction.Rename, state.RenameHome, state.RenameText.Trim()); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            HavenKit.EndDialog();
        }
        if (HavenKit.BeginDialog("##launcher-delete", "Delete home?"))
        {
            var home = model.Homes.FirstOrDefault(x => x.Id == state.DeleteHome);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextUnformatted($"\"{home?.Name}\" will be removed from your homes.");
            ImGui.PopTextWrapPos();
            HavenKit.Caption("A recovery copy is kept in houses/deleted, and you can import it later.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("Delete home") + Px(Space2));
            if (HavenKit.Button("Cancel##delete", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
            ImGui.SameLine(0, Px(Space2)); ImGui.BeginDisabled(home is not { Deletable: true });
            if (HavenKit.Button("Delete home", ButtonKind.Danger)) { command = new(LauncherAction.Delete, state.DeleteHome); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled();
            HavenKit.EndDialog();
        }
        return command;
    }
}
