using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Haven.Core;
using HavenNative;
using static Haven.HavenTokens;

namespace Haven;

// "What's new": Haven's update notes (docs/updates/UPDATE-*.md, embedded), shown once after an update for every version
// since the one last seen, and on demand (/haven changelog, Settings → General).
internal sealed class ChangelogWindow : Window
{
    private readonly NativeEngine engine;
    private readonly Version current;
    private readonly Lazy<IReadOnlyList<ChangelogEntry>> all;
    private IReadOnlyList<ChangelogEntry> shown = [];

    public ChangelogWindow(NativeEngine engine)
        : base("What's new in Haven###Haven.Changelog", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.engine = engine;
        var version = typeof(ChangelogWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        current = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        Size = new Vector2(620, 640); SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new() { MinimumSize = new Vector2(460, 360), MaximumSize = new Vector2(float.MaxValue) };
        all = new(Load);
    }

    // The embedded update notes, by version.
    private static IReadOnlyList<ChangelogEntry> Load()
    {
        var assembly = typeof(ChangelogWindow).Assembly;
        var entries = new List<ChangelogEntry>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Haven.Updates.", StringComparison.Ordinal)))
        {
            if (Changelog.VersionOf(name) is not { } version) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            entries.Add(Changelog.Parse(version, reader.ReadToEnd()));
        }
        return entries;
    }

    /// <summary>At load: shows the notes of every version since the one last seen (nothing on a first install), and remembers this one.</summary>
    public void ShowIfUpdated()
    {
        Version.TryParse(engine.HousingChangelogSeen, out var seen);
        var unseen = Changelog.Unseen(all.Value, seen, current);
        engine.HousingChangelogSeen = current.ToString();
        if (unseen.Count == 0) return;
        shown = unseen;
        IsOpen = true;
    }

    /// <summary>On demand: the latest few versions' notes.</summary>
    public void ShowRecent()
    {
        shown = [.. all.Value.Where(e => e.Version <= current).OrderByDescending(e => e.Version).Take(8)];
        IsOpen = true; BringToFront();
    }

    public override void PreDraw()
    {
        HavenKit.PushTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space4)); ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow));
    }
    public override void PostDraw() { ImGui.PopStyleVar(2); HavenKit.PopTheme(); }

    public override void Draw()
    {
        var start = ImGui.GetCursorScreenPos();
        using (HavenFonts.Use(HavenText.Title)) ImGui.GetWindowDrawList().AddText(start + Px(0, 6), HavenUi.U(Text), "What's new in Haven");
        ImGui.SetCursorScreenPos(new(start.X + ImGui.GetContentRegionAvail().X - Px(ControlHeight), start.Y));
        if (HavenKit.IconButton("##changelog-close", HavenIcon.Close, "Close")) IsOpen = false;
        ImGui.SetCursorScreenPos(start + new Vector2(0, Px(ControlHeight + Space2)));
        HavenKit.Caption(shown.Count == 0 ? "No notes for this version." : $"You're on Haven {current}. Notes for the latest versions, newest first.");
        ImGui.Dummy(Px(0, Space1));
        var footer = Px(ControlHeight + Space3);
        if (HavenKit.BeginScrollBody("##changelog-body", new(0, -footer)))
        {
            var width = ImGui.GetContentRegionAvail().X;
            foreach (var entry in shown)
            {
                HavenKit.SectionTitle($"{entry.Version} · {entry.Title}", width: width);
                foreach (var line in entry.Lines) DrawLine(line, width);
                ImGui.Dummy(Px(0, Space3));
            }
        }
        ImGui.EndChild();
        HavenKit.AlignRight(HavenKit.ButtonWidth("Got it"));
        if (HavenKit.Button("Got it##changelog", ButtonKind.Primary)) IsOpen = false;
    }

    // A heading, paragraph or bullet, wrapped to the body's width; bullets hang under their mark.
    private static void DrawLine(ChangelogLine line, float width)
    {
        switch (line.Kind)
        {
            case ChangelogLineKind.Heading:
                ImGui.Dummy(Px(0, Space1));
                using (HavenFonts.Use(HavenText.Strong)) Wrapped(line.Text, 0, width);
                break;
            case ChangelogLineKind.Paragraph:
                using (HavenFonts.Use(HavenText.Body)) Wrapped(line.Text, 0, width);
                break;
            default:
                var indent = Px(Space4) * (line.Depth + 1);
                var mark = line.Kind == ChangelogLineKind.Bullet ? "•" : "";
                using (HavenFonts.Use(HavenText.Body))
                {
                    if (mark.Length > 0) ImGui.GetWindowDrawList().AddText(ImGui.GetCursorScreenPos() + new Vector2(indent - Px(Space3), 0), HavenUi.U(TextSecondary), mark);
                    Wrapped(line.Text, indent, width);
                }
                break;
        }
        ImGui.Dummy(Px(0, 2));
    }

    private static void Wrapped(string text, float indent, float width)
    {
        if (indent > 0) ImGui.Indent(indent);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - indent);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
        if (indent > 0) ImGui.Unindent(indent);
    }
}
