using System.Text.RegularExpressions;
using Haven.Core;

/// <summary>Patch-day safety: the version gate, signature matching, and that every signature in the source is usable.</summary>
internal static class PatchTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("The patch gate pauses Haven's own-offset features only on an unchecked game, unless a canary or the player allows them", () =>
        {
            const string old = "2026.10.16.0000.0000", next = "2026.12.02.0000.0000";
            Assert(PatchGate.Checked(old, old) && PatchGate.Checked(old, " 2026.10.16.0000.0000 "));
            Assert(!PatchGate.Checked(old, next));
            // Nothing to compare against is no reason to switch features off.
            Assert(PatchGate.Checked("", next) && PatchGate.Checked(old, ""));
            Assert(PatchGate.Allows(old, old, null));
            Assert(!PatchGate.Allows(old, next, null) && !PatchGate.Allows(old, next, ""));
            Assert(PatchGate.Allows(old, next, null, canaryPassed: true));
            // The player's "use them anyway" holds for the game version it was given on, not the next one.
            Assert(PatchGate.Allows(old, next, next) && !PatchGate.Allows(old, "2027.01.10.0000.0000", next));
            Assert(PatchGate.DateOf(next) == new DateOnly(2026, 12, 2) && PatchGate.DateOf("nonsense") == null);
            Assert(Enum.GetValues<PatchFeature>().All(PatchGate.Names.ContainsKey));
        });

        await check("Signatures parse in Dalamud's notation and match with wildcards, leading calls noted", () =>
        {
            Assert(SignaturePattern.Parse("48 8B ?? 05") is { Length: 4 } p && p.Mask.SequenceEqual([true, true, false, true]) && !p.FollowsCall);
            Assert(SignaturePattern.Parse("E8 ?? ?? ?? ?? 44 22 F0")!.FollowsCall);
            Assert(SignaturePattern.Parse("48 8B") == null && SignaturePattern.Parse("?? ?? ?? ??") == null && SignaturePattern.Parse("48 8B 0G 05") == null);
            byte[] code = [0x90, 0x48, 0x8B, 0x11, 0x05, 0x90, 0x48, 0x8B, 0x22, 0x05, 0x48, 0x8B, 0x33, 0x06];
            var pattern = SignaturePattern.Parse("48 8B ?? 05")!;
            Assert(pattern.FindAll(code).SequenceEqual([1, 6]) && pattern.FindAll(code, 1).SequenceEqual([1]));
            // A pattern that starts with a wildcard, and one that runs off the end.
            Assert(SignaturePattern.Parse("?? 48 8B ?? 05")!.FindAll(code).SequenceEqual([0, 5]));
            Assert(SignaturePattern.Parse("48 8B 33 06 00")!.FindAll(code).Count == 0);
        });

        await check("Every signature in Haven's code is valid and belongs to a named feature (so a patch report can name it)", () =>
        {
            var source = Path.Combine(Repo(), "src", "Haven");
            var literal = new Regex("\"((?:[0-9A-Fa-f]{2}|\\?\\?)(?: (?:[0-9A-Fa-f]{2}|\\?\\?)){3,})\"");
            var count = 0;
            var unnamed = new List<string>();
            foreach (var path in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var file = Path.GetFileNameWithoutExtension(path);
                foreach (var line in File.ReadLines(path))
                {
                    var code = line.IndexOf("//", StringComparison.Ordinal) is var comment and >= 0 ? line[..comment] : line;
                    foreach (Match match in literal.Matches(code))
                    {
                        Assert(SignaturePattern.Parse(match.Groups[1].Value) != null);
                        count++;
                        var owner = file.Split('.')[0];
                        if (PatchParts.Of(owner, file.Contains('.') ? file[(file.IndexOf('.') + 1)..] : "").Name == owner) unnamed.Add(file);
                    }
                }
            }
            Assert(count > 50);
            if (unnamed.Count > 0) throw new Exception("Signatures in files with no feature name in PatchParts: " + string.Join(", ", unnamed.Distinct()));
            // The parts that gate entering a home are marked as such.
            Assert(PatchParts.Of("PacketFilterService", "").Critical && PatchParts.Of("ZoneLoadService", "").Critical && !PatchParts.Of("NativeArcade", "").Critical);
        });

        await check("The game version this Haven was checked on is recorded", () =>
        {
            var line = File.ReadLines(Path.Combine(Repo(), "src", "Haven", "verified-game-version.txt"))
                .Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith('#'));
            Assert(PatchGate.DateOf(line) != null);
        });
    }

    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Haven.slnx"))) return dir.FullName;
        throw new Exception("The repository wasn't found above the test's folder.");
    }

    private static void Assert(bool value) { if (!value) throw new Exception("Patch safety assertion failed."); }
}
