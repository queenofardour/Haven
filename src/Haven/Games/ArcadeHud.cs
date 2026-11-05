using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Haven.Games;

/// <summary>
/// Monster Toss's count and clock, which the Gold Saucer shows in the duty list (its event's objective: shots made and
/// the time left): drawn in the game's font with an edge, right-aligned just above the duty list, while a game runs.
/// </summary>
internal sealed unsafe class ArcadeHud(ArcadeSession arcade, IGameGui gameGui, IFontHandle title, IFontHandle body)
{
    public void Draw()
    {
        if (arcade.Objective is not { } objective) return;
        var viewport = ImGui.GetMainViewport();
        // Above the duty list (its window's top-right corner), else under the top-right corner of the screen.
        var anchor = viewport.Pos + new Vector2(viewport.Size.X - 40, 260);
        var list = (AtkUnitBase*)gameGui.GetAddonByName("_ToDoList").Address;
        if (list != null && list->IsVisible && list->RootNode != null)
            anchor = new Vector2(list->X + list->RootNode->Width * list->Scale, list->Y - 6);
        var draw = ImGui.GetForegroundDrawList();
        string[] lines = [$"Shots Made: {objective.Shots}", $"Time Remaining: {objective.Seconds / 60}:{objective.Seconds % 60:00}"];
        Vector2 titleSize, lineSize;
        using (title.Push()) titleSize = ImGui.CalcTextSize(objective.Name);
        using (body.Push()) lineSize = ImGui.CalcTextSize(lines[0]);
        var y = anchor.Y - titleSize.Y - lineSize.Y * lines.Length - 4;
        using (title.Push()) Edged(draw, new(anchor.X - titleSize.X, y), 0xFF7DD5FFu, objective.Name);   // the duty list's golden heading
        y += titleSize.Y + 2;
        foreach (var line in lines)
        {
            using (body.Push()) { var size = ImGui.CalcTextSize(line); Edged(draw, new(anchor.X - size.X, y), 0xFFFFFFFFu, line); }
            y += lineSize.Y;
        }
    }

    private static void Edged(ImDrawListPtr draw, Vector2 at, uint colour, string text)
    {
        at = new(MathF.Round(at.X), MathF.Round(at.Y));
        for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++)
            if (x != 0 || y != 0) draw.AddText(at + new Vector2(x, y), 0xD0101010u, text);
        draw.AddText(at, colour, text);
    }
}
