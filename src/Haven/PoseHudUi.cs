using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;
using static Haven.HavenTokens;

namespace Haven;

// The pose ball panel, drawn from a model and returning what the player asked for, so the UI
// preview can render it. The editor (FurnishingEditor.Poses.cs) fills the model and carries the command out.
internal sealed class PoseHudState { public bool Collapsed; }
internal readonly record struct PoseHudModel(string Title, string Status, bool Playing, bool Positioning, Vector3 Move, float Turn, float Pitch, float Roll, float Scale,
    bool Sounds, bool Rest)
{
    /// <summary>Whom the controls adjust, when partners share the set: "You", each partner, "Everyone" (empty: only the player).</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];
    public int Target { get; init; }
    /// <summary>Whether partners on the set may adjust this player (on this PC).</summary>
    public bool LetPartners { get; init; } = true;
}
internal enum PoseHudAction { None, Fit, Scale, Sounds, Rest, SaveFit, Reset, Restart, StandUp, Done, Cancel, Target, LetPartners }
internal readonly record struct PoseHudCommand(PoseHudAction Action, Vector3 Move = default, float Turn = 0, float Pitch = 0, float Roll = 0, float Scale = 1, bool Flag = false, int Target = 0);

internal static class PoseHudUi
{
    public const float Width = 320;
    private static readonly Vector4 AxisX = new(.93f, .53f, .53f, 1), AxisY = new(.6f, .8f, .6f, 1), AxisZ = new(.53f, .73f, .93f, 1);

    // A small window that can be dragged anywhere (kept on screen). A header names the set and ball, says what is
    // happening and hides or shows the controls; then Position and Rotation (exact values; the handles on the
    // character drag too), Size (typed or dragged, with a way back to 100 %), Sounds and Rest on surface, Save fit,
    // Reset and Restart; Stand up spans the bottom. Hidden, it keeps the title, Stand up and Show controls on one line.
    // Placing a ball shows Cancel and Done in Stand up's place.
    public static PoseHudCommand Draw(PoseHudModel model, PoseHudState state, out UiBounds bounds)
    {
        var command = default(PoseHudCommand);
        bounds = default;
        var viewport = ImGui.GetMainViewport();
        HavenKit.PushTheme();
        ImGui.SetNextWindowPos(new(viewport.Pos.X + viewport.Size.X / 2, viewport.Pos.Y + viewport.Size.Y - Px(160)), ImGuiCond.FirstUseEver, new Vector2(.5f, 1));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space4, Space3)); ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space2, Space2));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .95f });
        if (ImGui.Begin("###Haven.PoseHud", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar))
        {
            HavenKit.KeepOnTop();
            var at = ImGui.GetWindowPos(); var size = ImGui.GetWindowSize();
            var kept = Vector2.Clamp(at, viewport.Pos, Vector2.Max(viewport.Pos, viewport.Pos + viewport.Size - size));
            if (kept != at) ImGui.SetWindowPos(kept);
            bounds = new(ImGui.GetWindowPos(), size);
            var width = Px(Width);
            var toggle = state.Collapsed ? "Show controls" : "Hide controls";
            if (state.Collapsed)
            {
                using (HavenFonts.Use(HavenText.Strong)) { ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(model.Title); }
                ImGui.SameLine(0, Px(Space4));
                if (HavenKit.Button(toggle + "##pose-fold", ButtonKind.Ghost)) state.Collapsed = false;
                ImGui.SameLine();
                command = Leave(model, 0);
            }
            else command = Controls(model, state, width, toggle);
        }
        ImGui.End();
        ImGui.PopStyleColor(); ImGui.PopStyleVar(3);
        HavenKit.PopTheme();
        return command;
    }

    private static PoseHudCommand Controls(PoseHudModel model, PoseHudState state, float width, string toggle)
    {
        var command = default(PoseHudCommand);
        // The header: title and status on the left, Hide controls on the right.
        var foldWidth = HavenKit.ButtonWidth(toggle);
        ImGui.BeginGroup();
        using (HavenFonts.Use(HavenText.Strong)) ImGui.TextUnformatted(model.Title);
        using (HavenFonts.Use(HavenText.Small)) ImGui.TextColored(TextSecondary, model.Status);
        ImGui.EndGroup();
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetStyle().WindowPadding.X + width - foldWidth));
        if (HavenKit.Button(toggle + "##pose-fold", ButtonKind.Ghost)) state.Collapsed = true;
        HavenKit.Rule(width);
        // Whom the controls move: the player, one partner, or everyone together (only while partners share the set).
        var target = model.Target;
        if (model.Targets.Count > 1 && model.Playing)
        {
            SubTitle("Adjusting");
            var used = 0f;
            for (var i = 0; i < model.Targets.Count; i++)
            {
                var label = model.Targets[i].Length > 16 ? model.Targets[i][..15] + "…" : model.Targets[i];
                var pill = ImGui.CalcTextSize(label).X + Px(Space3 * 2);
                if (i > 0 && used + Px(Space2) + pill <= width) ImGui.SameLine(0, Px(Space2)); else if (i > 0) used = 0;
                if (HavenKit.Pill(label + "##pose-target-" + i, i == target) && i != target) command = new(PoseHudAction.Target, Target: i);
                used += (used > 0 ? Px(Space2) : 0) + pill;
            }
        }
        var other = model.Targets.Count > 1 && target > 0;
        ImGui.BeginDisabled(!model.Playing);
        var move = model.Move; var turn = model.Turn; var pitch = model.Pitch; var roll = model.Roll;
        var third = (width - Px(Space2) * 2) / 3;
        var everyone = other && target == model.Targets.Count - 1;
        HavenKit.Caption(!model.Playing ? "These work once the animation plays."
            : everyone ? "Moves everyone on the balls together. The values are yours; turning swings the group around its middle."
            : other ? $"Adjusts {model.Targets[target]} on their game. Each player keeps their own fit with Save fit."
            : "Drag the coloured handles on your character, or drag a value (double-click to type it). Shift: fine steps. Ctrl: past 2 m from the ball.", width);
        SubTitle("Position");
        // The fields reach as far as the wire allows; the controller keeps moves within 2 m unless Ctrl is held.
        var reach = PoseWire.FarMove;
        var changed = HavenKit.DragField("##pose-x", "Right", AxisX, ref move.X, .002f, "%.2f", third, -reach, reach);
        ImGui.SameLine(); changed |= HavenKit.DragField("##pose-y", "Up", AxisY, ref move.Y, .002f, "%.2f", third, -reach, reach);
        ImGui.SameLine(); changed |= HavenKit.DragField("##pose-z", "Fwd", AxisZ, ref move.Z, .002f, "%.2f", third, -reach, reach);
        SubTitle("Rotation");
        changed |= HavenKit.DragField("##pose-turn", "Turn", TextSecondary, ref turn, .25f, "%.0f°", third, -180, 180);
        if (ImGui.IsItemHovered()) HavenKit.Tooltip("Turn about your own up axis.");
        ImGui.SameLine(); changed |= HavenKit.DragField("##pose-pitch", "Tilt", TextSecondary, ref pitch, .25f, "%.0f°", third, -180, 180);
        if (ImGui.IsItemHovered()) HavenKit.Tooltip("Tilt forward or back, about your own right axis (pivoting at your feet).");
        ImGui.SameLine(); changed |= HavenKit.DragField("##pose-roll", "Lean", TextSecondary, ref roll, .25f, "%.0f°", third, -180, 180);
        if (ImGui.IsItemHovered()) HavenKit.Tooltip("Lean to either side, about your own forward axis (pivoting at your feet).");
        if (changed) command = new(PoseHudAction.Fit, move, turn, pitch, roll);
        // Size as a percentage: dragged, or double-clicked and typed; 100 % puts it back.
        SubTitle("Size");
        var percent = model.Scale * 100;
        var resetWidth = HavenKit.ButtonWidth("100%");
        if (HavenKit.DragField("##pose-size", "", TextSecondary, ref percent, .25f, "%.0f%%", width - resetWidth - Px(Space2), PoseWire.MinScale * 100, PoseWire.MaxScale * 100))
            command = new(PoseHudAction.Scale, Scale: Math.Clamp(percent / 100, PoseWire.MinScale, PoseWire.MaxScale));
        if (ImGui.IsItemHovered()) HavenKit.Tooltip($"Drag, or double-click and type a number ({PoseWire.MinScale * 100:0}–{PoseWire.MaxScale * 100:0}).");
        ImGui.SameLine();
        if (HavenKit.Button("100%##pose-size-reset", ButtonKind.Secondary, new(resetWidth, Px(FieldHeight)))) command = new(PoseHudAction.Scale, Scale: 1);
        if (ImGui.IsItemHovered()) HavenKit.Tooltip("Back to your own size.");
        ImGui.EndDisabled();
        var half = (width - Px(Space2)) / 2;
        var sounds = model.Sounds;
        if (HavenKit.Toggle("Sounds##pose-sounds", ref sounds, half)) command = new(PoseHudAction.Sounds, Flag: sounds);
        if (ImGui.IsItemHovered()) HavenKit.Tooltip("Play the music and sounds an imported animation brings (on this PC).");
        ImGui.SameLine();
        var rest = model.Rest;
        if (HavenKit.Toggle("Rest on surface##pose-rest", ref rest, half)) command = new(PoseHudAction.Rest, Flag: rest);
        if (ImGui.IsItemHovered()) HavenKit.Tooltip("When the animation starts without a saved fit, raise you if it would sink your body into the floor, bed or seat under you (on this PC).");
        if (!model.Positioning)
        {
            var let = model.LetPartners;
            if (HavenKit.Toggle("Partners can adjust me##pose-let", ref let, width)) command = new(PoseHudAction.LetPartners, Flag: let);
            if (ImGui.IsItemHovered()) HavenKit.Tooltip("Let the others on this animation move, turn and size you from their panel (on this PC).");
        }
        HavenKit.Rule(width);
        var count = model.Positioning ? 2 : 3;
        var each = (width - Px(Space2) * (count - 1)) / count;
        if (!model.Positioning)
        {
            if (HavenKit.Button("Save fit##pose", ButtonKind.Secondary, new(each, 0))) command = new(PoseHudAction.SaveFit);
            if (ImGui.IsItemHovered()) HavenKit.Tooltip("Keep your place, angle and size on this ball, with these partners, for next time (each player saves their own).");
            ImGui.SameLine();
        }
        if (HavenKit.Button("Reset##pose", ButtonKind.Secondary, new(each, 0))) command = new(PoseHudAction.Reset);
        if (ImGui.IsItemHovered()) HavenKit.Tooltip(everyone ? "Everyone back to their ball's own place, angle and size." : other ? $"{model.Targets[target]} back to their ball's own place, angle and size." : "Back to the ball's own place, angle and size.");
        ImGui.SameLine();
        ImGui.BeginDisabled(!model.Playing);
        if (HavenKit.Button("Restart##pose", ButtonKind.Secondary, new(each, 0))) command = new(PoseHudAction.Restart);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) HavenKit.Tooltip("Reload and start the animation again (if it shows the wrong one, or partners drifted apart).");
        ImGui.EndDisabled();
        if (model.Positioning) HavenKit.Caption("Done moves the ball here, with this facing, tilt and lean, for everyone.", width);
        if (Leave(model, width) is { Action: not PoseHudAction.None } leave) command = leave;
        return command;
    }

    private static void SubTitle(string text) { using (HavenFonts.Use(HavenText.Small)) ImGui.TextColored(TextSecondary, text); }

    // Stand up (or, placing a ball, Cancel and Done), across the given width (0: their own widths, on one line).
    private static PoseHudCommand Leave(PoseHudModel model, float width)
    {
        if (!model.Positioning) return HavenKit.Button("Stand up##pose", ButtonKind.Primary, new(width, 0)) ? new(PoseHudAction.StandUp) : default;
        var half = width > 0 ? (width - Px(Space2)) / 2 : 0;
        var command = default(PoseHudCommand);
        if (HavenKit.Button("Cancel##pose-place", ButtonKind.Ghost, new(half, 0))) command = new(PoseHudAction.Cancel);
        ImGui.SameLine();
        if (HavenKit.Button("Done##pose-place", ButtonKind.Primary, new(half, 0))) command = new(PoseHudAction.Done);
        return command;
    }
}
