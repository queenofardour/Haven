using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

// Refined modern component gallery: every HavenKit control in its common states,
// plus a Furnish drawer composed from them, all with sample data. The preview
// renders it (`preview-ui.ps1 -Kit`); in game `/haven kit` opens it for checking
// fonts, text input and scale before the redesigned screens use the kit.
internal static class HavenKitGallery
{
    public static readonly Dictionary<string, (Vector2 Min, Vector2 Max)> Bounds = new();
    public static readonly string[] Modes = ["Furnish", "Room", "Music", "Visit"];
    public static readonly string[] Categories = ["Recent", "Favourites", "Tables", "Seating", "Lighting", "Storage", "Decor", "Rugs", "Wall-mounted"];
    private static readonly string[] Wallpapers = ["Glade Interior Wall", "Riviera Interior Wall", "Oasis Interior Wall"];
    private static readonly (string Name, uint Color)[] Dyes = [("Original", 0x2C333D), ("Snow White", 0xE4DFD0), ("Soot Black", 0x2B2926), ("Dalamud Red", 0x9A3A33),
        ("Honey Yellow", 0xC9A14A), ("Celeste Green", 0x7FB7A0), ("Ice Blue", 0x9DC7E8), ("Royal Blue", 0x3E5AA8)];
    private static readonly string[] Items = ["Tonberry Floor Lamp", "Plasma Lamp", "Brass Pricket", "Marimo Lamp", "Riviera Lantern", "Glade Lantern",
        "Manor Candelabra", "Inferno Wall Lamp", "Carbuncle Lantern", "Caged Wisp", "Iron Torch", "Ghost Candlestand"];
    public static int Mode, Category = 4, Clicks;
    private static int wallpaper, dye, row, selectedCard, item;
    public static bool Snap = true, DrawerOpen = true;
    private static bool grid;
    private static float brightness = 4, x = 1.52f, y, z = -2.4f;
    private static string name = "Test Haven", search = "";

    public static Vector2 DrawerSize(Vector2 area) => new(Px(392), area.Y - Px(Space5) * 2);
    private static void Mark(string key) => Bounds[key] = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

    // Fills area (from origin) with the gallery page and the sample drawer.
    public static void Draw(Vector2 origin, Vector2 area, ref bool open, bool closable)
    {
        HavenKit.PushTheme();
        var margin = Px(Space5); var drawer = DrawerSize(area);
        ImGui.SetNextWindowPos(origin + new Vector2(margin)); ImGui.SetNextWindowSize(new(area.X - drawer.X - margin * 3, area.Y - margin * 2));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space5));
        if (ImGui.Begin("Components###Haven.Kit", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse))
        {
            if (closable)
            {
                var top = ImGui.GetCursorPos();
                ImGui.SetCursorPos(new(ImGui.GetWindowContentRegionMax().X - Px(32), top.Y));
                if (HavenKit.IconButton("##kit-close", HavenIcon.Close, "Close the gallery", size: 32)) open = false;
                ImGui.SetCursorPos(top);
            }
            HavenKit.PageTitle("Haven components", $"Refined modern · Phase 0 kit · {HavenUi.S * 100:0}% scale");
            ImGui.Dummy(Px(0, Space2));
            var column = (ImGui.GetContentRegionAvail().X - Px(Space5)) / 2;
            ImGui.BeginGroup(); ImGui.PushItemWidth(column);
            Left(column);
            ImGui.PopItemWidth(); ImGui.EndGroup();
            ImGui.SameLine(0, Px(Space5));
            ImGui.BeginGroup(); Right(column); ImGui.EndGroup();
        }
        ImGui.End(); ImGui.PopStyleVar();
        if (DrawerOpen) Drawer(origin + new Vector2(area.X - margin - drawer.X, margin), drawer);
        HavenKit.PopTheme();
    }

    private static void Left(float width)
    {
        HavenKit.SectionTitle("Buttons", width: width);
        if (HavenKit.Button("Enter home", ButtonKind.Primary)) Clicks++;
        Mark("primary"); ImGui.SameLine(); HavenKit.Button("New home"); ImGui.SameLine(); HavenKit.Button("Cancel", ButtonKind.Ghost);
        ImGui.SameLine(); HavenKit.Button("Delete…", ButtonKind.Danger);
        HavenKit.Button("Join a friend", ButtonKind.Primary, icon: HavenIcon.Friends); ImGui.SameLine();
        ImGui.BeginDisabled(); HavenKit.Button("Publish changes", ButtonKind.Primary); ImGui.EndDisabled();
        HavenKit.IconButton("##kit-undo", HavenIcon.Undo, "Undo"); Mark("hover"); ImGui.SameLine(0, Px(Space1));
        HavenKit.IconButton("##kit-redo", HavenIcon.Redo, "Redo"); ImGui.SameLine(0, Px(Space1));
        HavenKit.IconButton("##kit-wrench", HavenIcon.Wrench, "Edit your home", active: true); ImGui.SameLine(0, Px(Space1));
        HavenKit.IconButton("##kit-search", HavenIcon.Search, "Search");
        ImGui.Dummy(Px(0, Space2));
        HavenKit.SectionTitle("Toolbar and filters", width: width);
        HavenKit.Segmented("kit-modes", Modes, ref Mode); Mark("segmented");
        HavenKit.PillRow("kit-categories", Categories, ref Category, width); Mark("pill");
        HavenKit.Badge("Hosting · 2 guests", Accent); ImGui.SameLine(); HavenKit.Badge("Saved", Success);
        ImGui.Dummy(Px(0, Space2));
        HavenKit.SectionTitle("Fields", width: width);
        HavenKit.Field("##kit-name", "Home name", ref name, "My haven", width);
        HavenKit.Search("##kit-find", ref search, "Search 1,733 furnishings", width);
        HavenKit.Select("##kit-wallpaper", "Wallpaper", Wallpapers, ref wallpaper, width);
        var axis = (width - Px(Space2 * 2)) / 3;
        HavenKit.DragField("##kit-x", "X", AxisX, ref x, .01f, "%.2f", axis); ImGui.SameLine(0, Px(Space2));
        HavenKit.DragField("##kit-y", "Y", AxisY, ref y, .01f, "%.2f", axis); ImGui.SameLine(0, Px(Space2));
        HavenKit.DragField("##kit-z", "Z", AxisZ, ref z, .01f, "%.2f", axis);
        HavenKit.Slider("##kit-brightness", "House brightness", ref brightness, 0, 5, $"{brightness:0} / 5", width);
        HavenKit.Toggle("Snap to grid", ref Snap, width); Mark("toggle");
        HavenKit.Toggle("Show grid", ref grid, width);
        for (var i = 0; i < Dyes.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, Px(6));
            if (HavenKit.Swatch("##kit-dye-" + i, HavenUi.Rgb(Dyes[i].Color), dye == i, Dyes[i].Name)) dye = i;
        }
    }

    private static void Right(float width)
    {
        HavenKit.SectionTitle("Feedback", width: width);
        HavenKit.Banner(BannerKind.Info, "Visit code copied", "Share it with a friend so they can join your gathering.", width);
        HavenKit.Banner(BannerKind.Success, "Home saved", "", width);
        HavenKit.Banner(BannerKind.Warning, "Relay reconnecting", "Your home and guests stay put while Haven retries.", width);
        HavenKit.Banner(BannerKind.Danger, "Could not enter home", "Leave the duty queue first.", width);
        ImGui.Dummy(Px(0, Space2));
        HavenKit.SectionTitle("Cards and lists", "2 homes", width);
        var card = new Vector2((width - Px(Space3)) / 2, Px(104));
        string[] homes = ["Test Haven", "A quiet corner"]; string[] shells = ["Private Mansion · Shirogane", "Private Cottage · The Lavender Beds"];
        for (var i = 0; i < homes.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, Px(Space3));
            if (HavenKit.Card("##kit-card-" + i, card, selectedCard == i, out var min)) selectedCard = i;
            var draw = ImGui.GetWindowDrawList();
            using (HavenFonts.Use(HavenText.Title)) draw.AddText(min + Px(14, 14), HavenUi.U(Text), homes[i]);
            using (HavenFonts.Use(HavenText.Small)) draw.AddText(min + Px(14, 43), HavenUi.U(TextOnRaised), shells[i]);
            using (HavenFonts.Use(HavenText.Caption)) draw.AddText(min + Px(14, 68), HavenUi.U(TextTertiary), "2 floors · 36 furnishings");
        }
        if (HavenKit.ListRow("##kit-row-0", "Twilight Over Thanalan", "", row == 0, HavenIcon.Music, width)) row = 0;
        if (HavenKit.ListRow("##kit-row-1", "A Sailor Never Sleeps", "Patch 2.0 · 3:41", row == 1, HavenIcon.Music, width)) row = 1;
        ImGui.Dummy(Px(0, Space2));
        HavenKit.SectionTitle("Empty state", width: width);
        HavenKit.EmptyState(HavenIcon.Home, "No homes yet", "Choose a shell and make it yours. Your homes stay on this PC.", "New home", width);
        HavenKit.Rule(width);
        HavenKit.Caption("Fields render display-only in this preview; text editing is checked in game.", width);
    }

    private static void Drawer(Vector2 position, Vector2 size)
    {
        if (HavenKit.BeginPanel("Furnish", "Haven.Kit.Furnish", position, size, ref DrawerOpen))
        {
            HavenKit.Search("##kit-drawer-search", ref search, "Search 1,733 furnishings");
            HavenKit.PillRow("kit-drawer-categories", Categories, ref Category);
            ImGui.Dummy(Px(0, Space1));
            HavenKit.SectionTitle(Categories[Category], "148 items");
            var columns = 4; var gap = Px(Space2); var tile = (ImGui.GetContentRegionAvail().X - gap * (columns - 1)) / columns;
            for (var i = 0; i < Items.Length; i++)
            {
                if (i % columns != 0) ImGui.SameLine(0, gap);
                if (HavenKit.Card("##kit-item-" + i, new(tile, tile + Px(24)), item == i, out var min)) item = i;
                var draw = ImGui.GetWindowDrawList(); var icon = new Vector2(Px(48));
                var at = min + new Vector2((tile - icon.X) / 2, Px(Space2));
                draw.AddRectFilled(at, at + icon, HavenUi.U(IconWell), Px(RadiusControl));
                using (HavenFonts.Use(HavenText.Caption))
                {
                    draw.PushClipRect(min, min + new Vector2(tile - Px(6), tile + Px(24)), true);
                    var name = Items[i]; var x = Math.Max(Px(6), (tile - ImGui.CalcTextSize(name).X) / 2);
                    draw.AddText(min + new Vector2(x, Px(Space2 + 48 + 6)), HavenUi.U(TextOnRaised), name); draw.PopClipRect();
                }
                HavenKit.Tooltip(Items[i]);
            }
        }
        HavenKit.EndPanel();
    }
}
