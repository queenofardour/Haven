using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

/// <summary>
/// The loading screen over the game while a home loads (or while Haven returns the player): the district's loading art,
/// dimmed, with the home's name, what is happening and a bar. Progress null draws a moving bar (nothing to count yet).
/// Content is the custom content line, shown while a home's downloads hold the screen.
/// </summary>
internal readonly record struct LoadingModel(string Eyebrow, string Title, string Status, float? Progress, LauncherArt Art, float Alpha,
    string? Content = null, string? ContentDetail = null, float ContentProgress = 0, bool CanSkip = false);

/// <summary>The download bar under the room's toolbar: what is downloading, how far it is, or that it finished.</summary>
internal readonly record struct DownloadBarModel(string Title, string Detail, string Count, float? Progress, bool Done, float Alpha);   // Progress null: a moving bar

internal static class LoadingScreenUi
{
    /// <summary>Draws the loading screen over the whole game; returns true when "Continue without waiting" was chosen.</summary>
    public static bool Draw(LoadingModel model, bool inputs)
    {
        if (model.Alpha <= 0) return false;
        var skip = false;
        var viewport = ImGui.GetMainViewport();
        HavenKit.PushTheme();
        ImGui.SetNextWindowPos(viewport.Pos, ImGuiCond.Always); ImGui.SetNextWindowSize(viewport.Size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero); ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(model.Alpha, 0, 1));
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollWithMouse;
        if (!inputs) flags |= ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("###Haven.Loading", flags))
        {
            HavenKit.KeepOnTop();
            var draw = ImGui.GetWindowDrawList();
            var min = viewport.Pos; var max = viewport.Pos + viewport.Size;
            draw.AddRectFilled(min, max, HavenUi.U(new Vector4(.02f, .025f, .03f, 1)));
            if (model.Art.HasTexture) Cover(draw, model.Art.Texture, min, viewport.Size);
            // Darker towards the bottom, where the words sit.
            var fade = min.Y + viewport.Size.Y * .45f;
            draw.AddRectFilledMultiColor(new(min.X, fade), max, HavenUi.U(new Vector4(0, 0, 0, 0)), HavenUi.U(new Vector4(0, 0, 0, 0)),
                HavenUi.U(new Vector4(0, 0, 0, .85f)), HavenUi.U(new Vector4(0, 0, 0, .85f)));

            var width = Math.Min(Px(520), viewport.Size.X - Px(64));
            var left = min.X + Px(64); var bottom = max.Y - Px(72);
            float eyebrow, title, status;
            using (HavenFonts.Use(HavenText.Caption)) eyebrow = ImGui.GetTextLineHeight();
            using (HavenFonts.Use(HavenText.Display)) title = ImGui.GetTextLineHeight();
            using (HavenFonts.Use(HavenText.Body)) status = ImGui.GetTextLineHeight();
            var contentHeight = model.Content == null ? 0 : Px(Space5) + Px(18) + Px(Space2) + Px(4) + Px(Space2) + Px(14) + (model.CanSkip ? Px(Space3 + ControlHeight) : 0);
            var y = bottom - (eyebrow + Px(Space1) + title + Px(Space2) + status + Px(Space3) + Px(4) + contentHeight);
            using (HavenFonts.Use(HavenText.Caption)) draw.AddText(new(left, y), HavenUi.U(TextTertiary), model.Eyebrow.ToUpperInvariant());
            y += eyebrow + Px(Space1);
            using (HavenFonts.Use(HavenText.Display)) draw.AddText(new(left, y), HavenUi.U(Text), model.Title);
            y += title + Px(Space2);
            using (HavenFonts.Use(HavenText.Body)) draw.AddText(new(left, y), HavenUi.U(TextSecondary), model.Status);
            y += status + Px(Space3);
            Bar(draw, new(left, y), width, model.Progress);
            y += Px(4);
            if (model.Content != null)
            {
                y += Px(Space5);
                HavenUi.DrawIcon(draw, HavenIcon.Download, new(left + Px(8), y + Px(9)), Px(16), Accent);
                using (HavenFonts.Use(HavenText.Strong)) draw.AddText(new(left + Px(24), y + (Px(18) - ImGui.GetTextLineHeight()) / 2), HavenUi.U(Text), model.Content);
                y += Px(18) + Px(Space2);
                Bar(draw, new(left, y), width, model.ContentProgress);
                y += Px(4) + Px(Space2);
                using (HavenFonts.Use(HavenText.Small)) draw.AddText(new(left, y), HavenUi.U(TextTertiary), model.ContentDetail ?? "");
                y += Px(14);
                if (model.CanSkip)
                {
                    ImGui.SetCursorScreenPos(new(left, y + Px(Space3)));
                    skip = HavenKit.Button("Continue without waiting##loading-skip", ButtonKind.Secondary);
                }
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        HavenKit.PopTheme();
        return skip;
    }

    /// <summary>The download bar, centred at the top of the given point (under the room's toolbar).</summary>
    public static void DrawDownloads(DownloadBarModel model, Vector2 top, string id = "###Haven.Downloads")
    {
        if (model.Alpha <= 0) return;
        HavenKit.PushTheme();
        var width = Px(360);
        ImGui.SetNextWindowPos(top, ImGuiCond.Always, new Vector2(.5f, 0));
        ImGui.SetNextWindowSize(new(width, 0), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space3, Space3)); ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(model.Alpha, 0, 1));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .94f });
        if (ImGui.Begin(id, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs))
        {
            HavenKit.KeepOnTop();
            var draw = ImGui.GetWindowDrawList(); var min = ImGui.GetCursorScreenPos(); var inner = width - Px(Space3 * 2);
            var line = Px(20);
            HavenUi.DrawIcon(draw, model.Done ? HavenIcon.Check : HavenIcon.Download, min + new Vector2(Px(9), line / 2), Px(16), model.Done ? Success : Accent);
            using (HavenFonts.Use(HavenText.Strong)) draw.AddText(min + new Vector2(Px(26), (line - ImGui.GetTextLineHeight()) / 2), HavenUi.U(Text), model.Title);
            using (HavenFonts.Use(HavenText.Small))
            {
                var count = ImGui.CalcTextSize(model.Count);
                draw.AddText(min + new Vector2(inner - count.X, (line - ImGui.GetTextLineHeight()) / 2), HavenUi.U(TextSecondary), model.Count);
            }
            var y = line + Px(Space2);
            Bar(draw, min + new Vector2(0, y), inner, model.Progress, model.Done ? Success : Accent);
            y += Px(4) + Px(Space2);
            using (HavenFonts.Use(HavenText.Caption)) draw.AddText(min + new Vector2(0, y), HavenUi.U(TextTertiary), model.Detail);
            using (HavenFonts.Use(HavenText.Caption)) y += ImGui.GetTextLineHeight();
            ImGui.Dummy(new(inner, y));
        }
        ImGui.End();
        ImGui.PopStyleColor(); ImGui.PopStyleVar(3);
        HavenKit.PopTheme();
    }

    // A thin bar: filled to the progress, or a moving stretch when there is nothing to count.
    private static void Bar(ImDrawListPtr draw, Vector2 at, float width, float? progress, Vector4? colour = null)
    {
        var height = Px(4); var ink = HavenUi.U(colour ?? Accent);
        draw.AddRectFilled(at, at + new Vector2(width, height), HavenUi.U(new Vector4(1, 1, 1, .14f)), height / 2);
        if (progress is { } p)
        {
            if (p > 0) draw.AddRectFilled(at, at + new Vector2(width * Math.Clamp(p, 0, 1), height), ink, height / 2);
            return;
        }
        var phase = (float)(ImGui.GetTime() % 1.6 / 1.6); var span = width * .3f;
        var start = -span + (width + span) * phase;
        var from = Math.Max(0, start); var to = Math.Min(width, start + span);
        if (to > from) draw.AddRectFilled(at + new Vector2(from, 0), at + new Vector2(to, height), ink, height / 2);
    }

    // The district art filling the screen: cropped inside its letterbox bands (about 7 % top and bottom), dimmed.
    private static void Cover(ImDrawListPtr draw, ImTextureID texture, Vector2 min, Vector2 size)
    {
        const float imageAspect = 16f / 9, usable = .86f;
        var aspect = size.X / size.Y; var height = imageAspect / aspect; var width = 1f;
        if (height > usable) { width = usable * aspect / imageAspect; height = usable; }
        var uv0 = new Vector2((1 - width) / 2, (1 - height) / 2);
        draw.AddImage(texture, min, min + size, uv0, uv0 + new Vector2(width, height), HavenUi.U(new Vector4(.62f, .62f, .62f, 1)));
    }
}
