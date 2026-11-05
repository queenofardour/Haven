using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Haven.Games;

/// <summary>
/// Shows a layout-drawn minigame over the game, at the game's own window positions, and gives it the mouse and keys
/// over its windows (clicks there don't reach the game). Escape leaves.
/// </summary>
internal sealed class MiniGameWindow : Window
{
    private MiniGame? game;
    private bool wasDown;
    public Action? Closed { get; set; }

    public MiniGameWindow() : base("###Haven.MiniGame", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings)
    {
        RespectCloseHotkey = false;
    }

    public bool Playing => game != null;

    // In front of other windows (a chat window under the cards would otherwise take the clicks and be dragged).
    public void Play(MiniGame minigame) { game = minigame; IsOpen = true; wasDown = false; BringToFront(); }
    // Whether PreDraw pushed its style, so PostDraw pops exactly that even when the game ends during Draw.
    private bool styled;
    public void Stop()
    {
        if (game is not { } ending) return;
        game = null; IsOpen = false;
        try { ending.Stop(); } finally { Closed?.Invoke(); }
    }
    /// <summary>Whether Escape went down this frame (read from the game's keys, which ImGui doesn't get while the game has them).</summary>
    public Func<bool>? Escape { get; set; }

    public override void PreDraw()
    {
        if (game == null) return;
        var screen = ImGui.GetMainViewport().Size;
        game.Layout(screen, Scale);
        var (min, max) = game.Bounds(Scale);
        if (max.X <= min.X) { min = Vector2.Zero; max = Vector2.One; }
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos + min);
        ImGui.SetNextWindowSize(max - min);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        styled = true;
    }
    public override void PostDraw() { if (styled) ImGui.PopStyleVar(2); styled = false; }

    private static float Scale => ImGuiHelpers.GlobalScale;

    public override void Draw()
    {
        if (game == null) { IsOpen = false; return; }
        HavenKit.KeepOnTop();
        var origin = ImGui.GetMainViewport().Pos;
        // The whole window is one invisible button, so clicks over the game's windows stay with the minigame.
        ImGui.SetCursorPos(Vector2.Zero);
        ImGui.InvisibleButton("##minigame", Vector2.Max(ImGui.GetContentRegionAvail(), Vector2.One));
        var io = ImGui.GetIO();
        var hovered = ImGui.IsItemHovered();
        var down = io.MouseDown[0] && (hovered || wasDown);
        var input = new MiniGameInput(io.MousePos, down && !wasDown, !down && wasDown, down,
            ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter) || ImGui.IsKeyPressed(ImGuiKey.Space),
            ImGui.IsKeyPressed(ImGuiKey.Escape) || Escape?.Invoke() == true, default, Math.Clamp(io.DeltaTime, 0, .1f));
        wasDown = down;
        game.Update(input);
        // Windows are laid out in screen pixels from the main viewport's corner.
        var draw = ImGui.GetForegroundDrawList();
        game.Draw(draw, Scale, origin);
        if (game.Done) Stop();
    }
}
