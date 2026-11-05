using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;

namespace Haven;

// The inspector's controls for vases and flowerpots (flowers), Angler's Canvases (a fish print) and aquariums (fish and decoration),
// chosen as the game offers them: every flower and every print, and fish that fit the tank.
internal sealed partial class FurnishingEditor
{
    private string displaySearch = "";

    // A vase's flowers or a canvas's print: the current choice opens a searchable list of item icons.
    private void DrawFlowersOrPrint(Furnishing item, FurnitureEntry entry)
    {
        var current = item.Display == 0 ? null : Catalogue.Display(item.Display);
        HavenKit.Caption(entry.Flowerpot ? "Planted · shown in bloom" : entry.Vase ? "Flowers" : "Angler's print");
        if (HavenKit.Button((current?.Name ?? "None") + "##display", ButtonKind.Secondary)) { displaySearch = ""; ImGui.OpenPopup("##display-picker"); }
        if (!HavenKit.BeginMenu("##display-picker")) return;
        var width = HavenTokens.Px(360);
        HavenKit.Search("##display-search", ref displaySearch, entry.Vase ? "Search flowers" : "Search fish", width);
        var shown = Catalogue.Displays.Where(x => x.Kind == entry.DisplayKind && (displaySearch.Length == 0 || x.Name.Contains(displaySearch, StringComparison.OrdinalIgnoreCase))).ToArray();
        ImGui.BeginChild("##display-list", new Vector2(width, HavenTokens.Px(330)));
        if (displaySearch.Length == 0 && DisplayRow("None##display-none", 0, item.Display == 0)) { housing.Run(() => housing.ChangeFurniture(item with { Display = 0 })); ImGui.CloseCurrentPopup(); }
        foreach (var choice in shown)
            if (DisplayRow(choice.Name + "##display-" + choice.Row, choice.Icon, choice.Row == item.Display))
            {
                var row = choice.Row;
                housing.Run(() => housing.ChangeFurniture(item with { Display = row }));
                ImGui.CloseCurrentPopup();
            }
        if (shown.Length == 0) HavenKit.Caption(entry.Vase ? "No flowers match." : "No fish matches.");
        ImGui.EndChild();
        ImGui.EndPopup();
    }

    // One row of a picker: an item icon (none for "None") and its name; true when clicked.
    // width 0 fills the line; a fish row leaves room for its remove button, which a full-width row would cover.
    private bool DisplayRow(string label, uint icon, bool selected, float width = 0)
    {
        var size = HavenTokens.Px(32);
        var start = ImGui.GetCursorScreenPos();
        var clicked = ImGui.Selectable("##" + label, selected, ImGuiSelectableFlags.None, new Vector2(width, size));
        var draw = ImGui.GetWindowDrawList();
        if (icon != 0 && textures.GetFromGameIcon(icon).TryGetWrap(out var wrap, out _))
            draw.AddImageRounded(wrap!.Handle, start, start + new Vector2(size), Vector2.Zero, Vector2.One, HavenUi.U(Vector4.One), HavenTokens.Px(HavenTokens.RadiusField));
        var name = label[..label.IndexOf("##", StringComparison.Ordinal)];
        draw.AddText(start + new Vector2(size + HavenTokens.Px(HavenTokens.Space2), (size - ImGui.GetTextLineHeight()) / 2), HavenUi.U(HavenTokens.Text), name);
        return clicked;
    }

    // An aquarium: its fish (each removable), Add fish… with those that still fit, and its decoration.
    private void DrawAquarium(Furnishing item, FurnitureEntry entry)
    {
        var tank = item.Aquarium ?? new AquariumSetup();
        var fish = tank.Fish.Select(f => Catalogue.FishOf(f)).OfType<AquariumFishEntry>().ToArray();
        var used = fish.Sum(f => f.Size);
        HavenKit.Caption($"Fish · {used} of {entry.AquariumTier} space used" + (fish.Length > 0 ? fish[0].Water == 1 ? " · freshwater" : " · saltwater" : ""));
        for (var i = 0; i < fish.Length; i++)
        {
            ImGui.PushID("fish-" + i);
            var index = i;
            var button = HavenTokens.Px(HavenTokens.ControlHeight);
            DisplayRow($"{fish[i].Name} ({SizeName(fish[i].Size)})##fish", fish[i].Icon, false, ImGui.GetContentRegionAvail().X - button - HavenTokens.Px(HavenTokens.Space2));
            ImGui.SameLine(0, HavenTokens.Px(HavenTokens.Space2));
            if (HavenKit.IconButton("##remove-fish", HavenIcon.Close, "Take this fish out"))
                housing.Run(() => housing.ChangeFurniture(item with { Aquarium = Tidy(tank with { Fish = [.. tank.Fish.Where((_, n) => n != index)] }) }));
            ImGui.PopID();
        }
        var room = entry.AquariumTier - used;
        ImGui.BeginDisabled(room <= 0 || fish.Length >= entry.AquariumTier);
        if (HavenKit.Button("Add fish…##add-fish", ButtonKind.Secondary)) { displaySearch = ""; ImGui.OpenPopup("##fish-picker"); }
        ImGui.EndDisabled();
        if (HavenKit.BeginMenu("##fish-picker"))
        {
            var width = HavenTokens.Px(380);
            HavenKit.Search("##fish-search", ref displaySearch, "Search fish", width);
            var water = fish.Length > 0 ? fish[0].Water : 0;
            var fits = Catalogue.Fish.Where(f => f.Size <= room && (water == 0 || f.Water == water)
                && (displaySearch.Length == 0 || f.Name.Contains(displaySearch, StringComparison.OrdinalIgnoreCase))).ToArray();
            HavenKit.Caption(water == 0 ? "Fish of one water share a tank. Small fish take 1 space, medium 2, large 3, extra large 4."
                : $"Only {(water == 1 ? "freshwater" : "saltwater")} fish join these, up to {room} space.", width);
            ImGui.BeginChild("##fish-list", new Vector2(width, HavenTokens.Px(320)));
            foreach (var choice in fits)
                if (DisplayRow($"{choice.Name} ({SizeName(choice.Size)}, {(choice.Water == 1 ? "fresh" : "salt")})##fish-{choice.Row}", choice.Icon, false))
                {
                    var row = choice.Row;
                    housing.Run(() => housing.ChangeFurniture(item with { Aquarium = Tidy(tank with { Fish = [.. tank.Fish, row] }) }));
                    ImGui.CloseCurrentPopup();
                }
            if (fits.Length == 0) HavenKit.Caption("No fish fits.");
            ImGui.EndChild();
            ImGui.EndPopup();
        }
        var decor = (int)tank.Decor;
        if (HavenKit.Select("##aquarium-decor", "Decoration", ["None", "Style 1", "Style 2", "Style 3", "Style 4", "Style 5", "Style 6"], ref decor))
            housing.Run(() => housing.ChangeFurniture(item with { Aquarium = Tidy(tank with { Decor = (uint)decor }) }));
    }

    // An empty tank with no decoration is stored as no setup at all.
    private static AquariumSetup? Tidy(AquariumSetup tank) => tank.Fish.Length == 0 && tank.Decor == 0 ? null : tank;
    private static string SizeName(int size) => size switch { 1 => "small", 2 => "medium", 3 => "large", _ => "extra large" };
}
