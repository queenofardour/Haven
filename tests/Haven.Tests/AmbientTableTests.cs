using System.Text.Json;
using Haven.Core.World;

/// <summary>The embedded ambience catalogue the tool's --ambient mode writes.</summary>
internal static class AmbientTableTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("The embedded ambience catalogue lists the world's effects and sounds, each valid, named once and categorised", () =>
        {
            var table = WorldAmbientTable.Embedded();
            // Every entry the tool wrote reads back: the table drops nothing it can't hold.
            using (var stream = typeof(WorldAmbientTable).Assembly.GetManifestResourceStream("Haven.Core.World.world-ambient.json")!)
                Assert(JsonDocument.Parse(stream).RootElement.GetArrayLength() == table.Count);
            var effects = table.Entries.Where(e => e.Kind == AmbientKind.Effect).ToArray();
            var sounds = table.Entries.Where(e => e.Kind == AmbientKind.Sound).ToArray();
            Assert(effects.Length > 1000 && sounds.Length > 600);
            Assert(table.Entries.All(e => e.Valid && e.Name.Length > 0 && e.Zone.Length > 0 && e.Uses > 0));
            foreach (var kind in new[] { effects, sounds })
                Assert(kind.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == kind.Length);
            Assert(effects.All(e => WorldAmbient.EffectCategories.Contains(e.Category) && e.Size is >= 0 and <= 200 && e.Colour is { Length: 4 } && e.Repeat == AmbientRepeat.Loop));
            // Effects are measured from what they draw: usable at some scale (0.05x-10x), and set down by a finite offset.
            Assert(effects.All(e => e.Size is >= .15f and <= 150 && e.Offset is { Length: 3 } offset && offset.All(float.IsFinite)));
            // No name describes a thumbnail rather than the thing (the first names said "card" for a wall of fire).
            Assert(!effects.Any(e => System.Text.RegularExpressions.Regex.IsMatch(e.Name, @"\b(card|blob|smudge)s?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
            Assert(sounds.All(e => WorldAmbient.SoundCategories.Contains(e.Category) && e.Repeat is AmbientRepeat.Loop or AmbientRepeat.Cycle));
            // Every category but the catch-all has something in it, and the catch-all isn't most of the list.
            Assert(WorldAmbient.EffectCategories.All(c => effects.Any(e => e.Category == c)) && effects.Count(e => e.Category == "Other") < effects.Length / 4);
            Assert(WorldAmbient.SoundCategories.All(c => sounds.Any(e => e.Category == c)) && sounds.Count(e => e.Category == "Other") < sounds.Length / 10);
        });
    }

    private static void Assert(bool value) { if (!value) throw new Exception("Ambience catalogue assertion failed."); }
}
