using Haven.Core;

internal static class ChangelogTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Update notes read as headings, paragraphs and bullets, and only versions since the last seen are shown", () =>
        {
            Assert(Changelog.VersionOf("Haven.Updates.UPDATE-0.44.1.md") == new Version(0, 44, 1) && Changelog.VersionOf("UPDATE-0.9.md") == new Version(0, 9, 0) && Changelog.VersionOf("README.md") == null);
            var entry = Changelog.Parse(new(0, 44, 1), "# Haven 0.44.1: the **screen** shows\n\nIntro line one\nline two.\n\n- **First.** See [docs](x.md) and `code`.\n  - Nested one\n  More of the nested one.\n1. Step\n\n## Later\n\nClient only.\n");
            Assert(entry.Title == "the screen shows");
            Assert(entry.Lines[0] == new ChangelogLine(ChangelogLineKind.Paragraph, "Intro line one line two."));
            Assert(entry.Lines[1] == new ChangelogLine(ChangelogLineKind.Bullet, "First. See docs and code."));
            Assert(entry.Lines[2] == new ChangelogLine(ChangelogLineKind.Bullet, "Nested one More of the nested one.", 1));
            Assert(entry.Lines[3].Kind == ChangelogLineKind.Numbered && entry.Lines[3].Text == "1. Step");
            Assert(entry.Lines[4] == new ChangelogLine(ChangelogLineKind.Heading, "Later") && entry.Lines[5].Text == "Client only.");
            ChangelogEntry Note(int minor, int patch) => new(new(0, minor, patch), "", []);
            var all = new[] { Note(43, 1), Note(44, 0), Note(44, 1), Note(45, 0) };
            Assert(Changelog.Unseen(all, new(0, 43, 1), new(0, 44, 1)).Select(e => e.Version).SequenceEqual([new Version(0, 44, 1), new Version(0, 44, 0)]));
            Assert(Changelog.Unseen(all, null, new(0, 44, 1)).Single().Version == new Version(0, 44, 1) && Changelog.Unseen(all, new(0, 44, 1), new(0, 44, 1)).Count == 0);
            // Every shipped note reads, with a title.
            foreach (var file in Directory.EnumerateFiles("docs", "UPDATE-*.md"))
                Assert(Changelog.VersionOf(file) is { } version && Changelog.Parse(version, File.ReadAllText(file)).Title.Length > 0);
        });
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
}
