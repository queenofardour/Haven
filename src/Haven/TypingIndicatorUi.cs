using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;

namespace Haven;

internal static class TypingIndicatorUi
{
    public static bool Draw(Vector2 screen, TypingState state, double time, Vector2 viewport, Vector2 size, float scale)
    {
        if (state == TypingState.Idle) return false;
        var layout = TypingBubbleLayout.At(screen, viewport, size, scale);
        if (layout == null) return false;
        var bubble = layout.Value;
        var active = state == TypingState.Active;
        var fill = active ? 0xF2EDE6DBu : 0xD9D4CEC3u;
        var ink = active ? 0xFF51483Au : 0xFF847C70u;
        var draw = ImGui.GetBackgroundDrawList();
        draw.AddRectFilled(bubble.Min + new Vector2(0, 2 * scale), bubble.Max + new Vector2(0, 2 * scale), 0x48000000u, 12 * scale);
        draw.AddTriangleFilled(bubble.TailLeft, bubble.TailRight, bubble.TailTip, fill);
        draw.AddRectFilled(bubble.Min, bubble.Max, fill, 12 * scale);
        for (var dot = 0; dot < 3; dot++)
        {
            var phase = (float)(time * 5 - dot * 0.8);
            var lift = active ? MathF.Max(0, MathF.Sin(phase)) * 2.5f * scale : 0;
            draw.AddCircleFilled(bubble.Dot(dot) - new Vector2(0, lift), 2.2f * scale, ink);
        }
        return true;
    }
}
