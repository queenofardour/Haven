using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using static Haven.HavenTokens;

namespace Haven;

/// <summary>
/// Cinematic mode, for a player on a pose ball: every game window and every plugin window goes, except the chat (the
/// game's chat log, and Chat 2 if its "Hide when the UI is hidden" is off) and a button to come back. It uses the game's
/// own hide-UI toggle, so Dalamud hides other plugins' windows as it does for Scroll Lock, after marking the chat log's
/// windows to stay drawn (AtkUnitBase.IgnoreUIDisplayMode, the game's own "visible when the UI is hidden" flag). Haven's
/// own windows draw only the way back (Hiding). It ends when the player leaves the ball, the UI comes back some other
/// way (Scroll Lock), or Haven unloads.
/// </summary>
internal sealed unsafe class CinematicMode(IDalamudPluginInterface pluginInterface, IPluginLog log) : IDisposable
{
    /// <summary>Whether Haven's own windows are hidden (only the button back draws).</summary>
    public static bool Hiding { get; private set; }
    public bool Active => Hiding;
    private static readonly string[] Chat = ["ChatLog", "ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3"];
    private readonly Dictionary<string, bool> kept = [];
    private bool hidByUs, wanted, chatTwo;
    /// <summary>Whether the player dismissed the Chat 2 hint for good (kept in Haven's settings).</summary>
    public Func<bool> ChatTwoHintSeen { get; set; } = () => false;
    public Action? ChatTwoHintDismissed { get; set; }

    /// <summary>Asks for cinematic mode on or off; applied on the next framework update.</summary>
    public void Request(bool on) => wanted = on;

    /// <summary>Each framework update: enters or leaves as asked, and leaves when the player is off their ball.</summary>
    public void Update(bool onBall)
    {
        if (!onBall) wanted = false;
        if (wanted && !Hiding) Enter();
        else if (!wanted && Hiding) Exit();
        else if (Hiding)
        {
            var atk = RaptureAtkModule.Instance();
            // The UI came back another way (Scroll Lock, the game's own force-show): cinematic mode is over.
            if (atk == null || atk->IsUiVisible) { wanted = false; Exit(); return; }
            KeepChat(atk);
        }
    }

    private void Enter()
    {
        var atk = RaptureAtkModule.Instance();
        if (atk == null || !atk->IsUiVisible) { wanted = false; return; }
        Hiding = true;
        pluginInterface.UiBuilder.DisableUserUiHide = true;
        KeepChat(atk);
        // A toggle (whatever the argument), through Dalamud's hook, which then hides other plugins' windows too.
        atk->SetUiVisibility(false);
        hidByUs = !atk->IsUiVisible;
        log.Information("[Haven] Cinematic mode on.");
        chatTwo = pluginInterface.InstalledPlugins.Any(p => p.InternalName == "ChatTwo" && p.IsLoaded);
    }

    // The chat log's windows stay drawn while the UI is hidden; their own flag is put back when cinematic mode ends.
    private void KeepChat(RaptureAtkModule* atk)
    {
        foreach (var name in Chat)
        {
            var unit = atk->RaptureAtkUnitManager.GetAddonByName(name);
            if (unit == null) continue;
            kept.TryAdd(name, unit->IgnoreUIDisplayMode);
            unit->IgnoreUIDisplayMode = true;
        }
    }

    private void Exit()
    {
        var atk = RaptureAtkModule.Instance();
        if (atk != null)
        {
            foreach (var (name, was) in kept)
            {
                var unit = atk->RaptureAtkUnitManager.GetAddonByName(name);
                if (unit != null) unit->IgnoreUIDisplayMode = was;
            }
            if (hidByUs && !atk->IsUiVisible) atk->SetUiVisibility(false);
        }
        kept.Clear(); hidByUs = false; Hiding = false;
        pluginInterface.UiBuilder.DisableUserUiHide = false;
        log.Information("[Haven] Cinematic mode off.");
    }

    /// <summary>The way back: a small button at the top of the screen, the only Haven window while hiding.</summary>
    public void Draw()
    {
        if (!Hiding) return;
        var viewport = ImGui.GetMainViewport();
        HavenKit.PushTheme();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2, Px(18)), ImGuiCond.Always, new Vector2(.5f, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(6))); ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .6f });
        if (ImGui.Begin("###Haven.Cinematic", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar))
        {
            HavenKit.KeepOnTop();
            if (HavenKit.IconButton("##cinematic-off", HavenIcon.Camera, "Leave cinematic mode", true, ToolbarHeight)) wanted = false;
            // Chat 2 hides itself with the game UI unless told not to; said here (Dalamud's notifications hide too).
            if (chatTwo && !ChatTwoHintSeen())
            {
                ImGui.SameLine(0, Px(Space3));
                ImGui.BeginGroup();
                HavenKit.Caption("Chat 2 hides with the game UI. To keep it here, untick \"Hide when the UI is hidden\" in Chat 2's Display settings.", Px(320));
                if (HavenKit.Button("Got it##cinematic-chat2", ButtonKind.Ghost)) ChatTwoHintDismissed?.Invoke();
                ImGui.EndGroup();
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(); ImGui.PopStyleVar(2);
        HavenKit.PopTheme();
    }

    public void Dispose()
    {
        wanted = false;
        if (Hiding) Exit();
    }
}
