using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Haven.Core;
using static Haven.HavenTokens;

namespace Haven.Games;

/// <summary>
/// Another player in the gathering challenges you to Triple Triad: who, the rules, your deck, Accept or Decline. Shown
/// while an invitation waits; one at a time, oldest first.
/// </summary>
internal sealed class TriadInviteWindow : Window
{
    private readonly TriadLink link;
    private readonly Func<TriadRule, string> ruleName;
    private readonly Func<IReadOnlyList<TriadDeck>> decks;
    private int deck;
    /// <summary>Accepting an invitation with a deck (null: five of the player's own cards).</summary>
    public Action<TriadInvitation, TriadDeck?>? Accepted { get; set; }

    public TriadInviteWindow(TriadLink link, Func<TriadRule, string> ruleName, Func<IReadOnlyList<TriadDeck>> decks)
        : base("Triple Triad challenge###Haven.TriadInvite", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)
    {
        this.link = link; this.ruleName = ruleName; this.decks = decks;
        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        HavenKit.PushTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space4)); ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2, viewport.Size.Y * .3f), ImGuiCond.Appearing, new Vector2(.5f, 0));
    }
    public override void PostDraw() { ImGui.PopStyleVar(2); HavenKit.PopTheme(); }

    public override void Draw()
    {
        if (link.Invitations.FirstOrDefault() is not { } invitation) { IsOpen = false; return; }
        HavenKit.KeepOnTop();
        var width = Px(380);
        using (HavenFonts.Use(HavenText.Title)) ImGui.TextUnformatted("Triple Triad");
        ImGui.Dummy(Px(0, Space1));
        HavenKit.Caption($"{invitation.Name} challenges you to a match. Nothing is won or lost.", width);
        HavenKit.Caption(invitation.Rules.Count == 0 ? "No rules." : "Rules: " + string.Join(" · ", invitation.Rules.Select(ruleName)) + ".", width);
        var seconds = Math.Max(0, (invitation.Expires - Environment.TickCount64) / 1000);
        HavenKit.Caption($"{seconds} seconds to answer.", width);
        ImGui.Dummy(Px(0, Space2));
        var available = decks();
        var names = available.Select(d => d.Name).Append("Five of your own cards").ToList();
        deck = Math.Clamp(deck, 0, names.Count - 1);
        HavenKit.Select("##invite-deck", "Your deck", names, ref deck, width);
        ImGui.Dummy(Px(0, Space2));
        if (HavenKit.Button("Accept", ButtonKind.Primary)) Accepted?.Invoke(invitation, deck < available.Count ? available[deck] : null);
        ImGui.SameLine();
        if (HavenKit.Button("Decline")) link.Decline(invitation);
    }
}
