using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

internal enum NpcBarAction { None, SwitchSpeech, LetGo }

/// <summary>
/// The slim bar under the toolbar while possessing: the NPC's name, who /say speaks as (one click switches), and let
/// go. The keys are shown underneath only for the first moments of each possession.
/// </summary>
internal static class NpcBarUi
{
    public static NpcBarAction Draw(string name, bool speakAsNpc, bool hints)
    {
        var action = NpcBarAction.None;
        var toolbar = PlacementUi.OwnerBounds;
        var viewport = ImGui.GetMainViewport();
        var top = toolbar.Size.Y > 0 ? new Vector2(toolbar.Position.X + toolbar.Size.X / 2, toolbar.Position.Y + toolbar.Size.Y + Px(Space2))
            : viewport.Pos + new Vector2(viewport.Size.X / 2, Px(18));
        HavenKit.PushTheme();
        ImGui.SetNextWindowPos(top, ImGuiCond.Always, new Vector2(.5f, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space2, Space2)); ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space2, Space1));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .94f });
        if (ImGui.Begin("###Haven.Npc", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove))
        {
            HavenKit.KeepOnTop();
            ImGui.Dummy(new(Px(Space1), 0)); ImGui.SameLine();
            using (HavenFonts.Use(HavenText.Strong)) { ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(name); }
            ImGui.SameLine(0, Px(Space4));
            // Green while /say speaks as the NPC, blue while it speaks as you.
            var speech = speakAsNpc ? $"Speaking as {name}" : "Speaking as you";
            if (HavenKit.Button($"{speech}##npc-speak", speakAsNpc ? ButtonKind.Positive : ButtonKind.Primary)) action = NpcBarAction.SwitchSpeech;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(speakAsNpc ? "Your /say lines and typing bubble are the NPC's. Click to speak as yourself."
                : "Your /say lines and typing bubble are yours. Click to speak as the NPC.");
            ImGui.SameLine();
            if (HavenKit.Button("Let go##npc-possess", ButtonKind.Secondary)) action = NpcBarAction.LetGo;
            if (hints)
                using (HavenFonts.Use(HavenText.Small))
                { ImGui.Dummy(new(Px(Space1), 0)); ImGui.SameLine(); ImGui.TextColored(TextSecondary, "WASD moves · Ctrl walks · Space jumps"); }
        }
        ImGui.End();
        ImGui.PopStyleColor(); ImGui.PopStyleVar(3);
        HavenKit.PopTheme();
        return action;
    }
}
