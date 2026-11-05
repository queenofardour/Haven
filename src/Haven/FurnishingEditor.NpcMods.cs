using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

// Sharing NPCs' mods (NpcModShare): the owner's reminder before they're dressed as an NPC, and a guest's choice to keep
// a share. Both sit under the toolbar like the custom content prompt.
internal sealed partial class FurnishingEditor
{
    // The NPC whose mods the owner asked to send, waiting for them to confirm.
    private Guid? confirmNpcMods;

    private void AskToSendNpcMods(Guid npc) => confirmNpcMods = npc;

    private void DrawNpcModsPrompts()
    {
        if (NpcMods == null) return;
        if (confirmNpcMods is { } npc)
        {
            if ((housing.Npcs.FirstOrDefault(x => x.Id == npc) ?? Visitors?.Find(npc)) is not { } sent) confirmNpcMods = null;
            else PromptWindow("Haven.NpcModsSend", $"Send {sent.Name}'s mods?",
                [$"You'll wear {sent.Name}'s look for a few seconds while your guests' sync plugins download its mods. Each guest is asked whether to keep them.", NpcModShare.AutomationNote],
                "Cancel", "Send", send =>
                {
                    confirmNpcMods = null;
                    if (send && NpcMods.Send(npc) is { } notSent) housing.Run(() => throw new InvalidOperationException(notSent));
                });
        }
        if (NpcMods.Asking is { } asking)
            PromptWindow("Haven.NpcModsKeep", $"{asking.Owner} is sharing {asking.Npc}'s mods",
                [$"They show {asking.Npc}'s look for a moment, so your sync plugin downloads its mods; this can take a few seconds. Keep them to see {asking.Npc} as they made it, here and on later visits."],
                "No thanks", "Keep its mods", keep => NpcMods.Answer(keep));
    }

    // A small window under the toolbar: a title, captions and two buttons (the second primary); the answer, once.
    private void PromptWindow(string id, string title, string[] captions, string no, string yes, Action<bool> answer)
    {
        var viewport = ImGui.GetMainViewport();
        HavenKit.PushTheme();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2, Px(96)), ImGuiCond.Always, new Vector2(.5f, 0));
        ImGui.SetNextWindowSize(new(Px(440), 0), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space5)); ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space2, Space3));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow)); ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .96f });
        bool? chosen = null;
        if (ImGui.Begin($"{title}###{id}", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar))
        {
            HavenKit.KeepOnTop();
            using (HavenFonts.Use(HavenText.Title)) ImGui.TextWrapped(title);
            foreach (var caption in captions) HavenKit.Caption(caption);
            HavenKit.AlignRight(HavenKit.ButtonWidth(no) + HavenKit.ButtonWidth(yes) + Px(Space2));
            if (HavenKit.Button($"{no}##{id}-no", ButtonKind.Ghost)) chosen = false;
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button($"{yes}##{id}-yes", ButtonKind.Primary)) chosen = true;
        }
        ImGui.End(); ImGui.PopStyleColor(); ImGui.PopStyleVar(3); HavenKit.PopTheme();
        if (chosen is { } yesOrNo) answer(yesOrNo);
    }
}
