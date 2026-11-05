using System.Text.RegularExpressions;

namespace Haven.Core;

/// <summary>One line of an update note as shown: a heading, a paragraph, or a bullet at a depth (0 top level).</summary>
public sealed record ChangelogLine(ChangelogLineKind Kind, string Text, int Depth = 0);
public enum ChangelogLineKind { Heading, Paragraph, Bullet, Numbered }

/// <summary>One version's update note: its version, title and lines.</summary>
public sealed record ChangelogEntry(Version Version, string Title, IReadOnlyList<ChangelogLine> Lines);

/// <summary>
/// The "What's new" notes shown after an update: Haven's own update documents (docs/updates/UPDATE-x.y.z.md, embedded in the
/// plugin), read as simple Markdown. Shown once per version: the versions after the one last seen, newest first.
/// </summary>
public static class Changelog
{
    private static readonly Regex FileVersion = new(@"UPDATE-(\d+)\.(\d+)(?:\.(\d+))?\.md$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant);
    private static readonly Regex Numbered = new(@"^(\d+)\.\s+(.*)$", RegexOptions.CultureInvariant);

    /// <summary>The version an update document is for, from its name (UPDATE-0.44.1.md), or null.</summary>
    public static Version? VersionOf(string name)
    {
        var match = FileVersion.Match(name);
        return match.Success ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0) : null;
    }

    /// <summary>Reads one update document: the title (its first heading, without "Haven x.y.z:"), then its lines.</summary>
    public static ChangelogEntry Parse(Version version, string markdown)
    {
        var lines = new List<ChangelogLine>();
        var title = "";
        var paragraph = new List<string>();
        void Flush() { if (paragraph.Count > 0) { lines.Add(new(ChangelogLineKind.Paragraph, Clean(string.Join(" ", paragraph)))); paragraph.Clear(); } }
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var indent = raw.Length - raw.TrimStart(' ').Length;
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith('#'))
            {
                Flush();
                var heading = Clean(line.TrimStart('#').Trim());
                if (title.Length == 0) title = Regex.Replace(heading, @"^Haven\s+[\d.]+\s*:\s*", "", RegexOptions.IgnoreCase);
                else lines.Add(new(ChangelogLineKind.Heading, heading));
                continue;
            }
            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                Flush();
                lines.Add(new(ChangelogLineKind.Bullet, Clean(line[2..]), indent / 2));
                continue;
            }
            if (Numbered.Match(line) is { Success: true } number)
            {
                Flush();
                lines.Add(new(ChangelogLineKind.Numbered, number.Groups[1].Value + ". " + Clean(number.Groups[2].Value), Math.Max(1, indent / 2)));
                continue;
            }
            // A continuation of the bullet above (indented under it), or a paragraph line.
            if (indent > 0 && paragraph.Count == 0 && lines.Count > 0 && lines[^1].Kind is ChangelogLineKind.Bullet or ChangelogLineKind.Numbered)
                lines[^1] = lines[^1] with { Text = lines[^1].Text + " " + Clean(line) };
            else paragraph.Add(line);
        }
        Flush();
        return new(version, title.Length > 0 ? title : $"Haven {version}", lines);
    }

    // Markdown marks the window doesn't draw: bold, code and links (their text kept).
    private static string Clean(string text) => Link.Replace(text, "$1").Replace("**", "").Replace("`", "");

    /// <summary>
    /// The notes to show: versions after lastSeen up to current, newest first, at most max. With no version seen yet (a
    /// first install, or the update that brought this window) only the current version's, not the whole history.
    /// </summary>
    public static IReadOnlyList<ChangelogEntry> Unseen(IEnumerable<ChangelogEntry> entries, Version? lastSeen, Version current, int max = 6) =>
        lastSeen == null ? [.. entries.Where(e => e.Version == current)]
            : lastSeen >= current ? []
            : [.. entries.Where(e => e.Version > lastSeen && e.Version <= current).OrderByDescending(e => e.Version).Take(max)];
}
