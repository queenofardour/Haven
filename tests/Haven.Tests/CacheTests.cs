using Haven.Core;

/// <summary>What this PC keeps of custom content: what its homes use, and what goes first past the download limit.</summary>
internal static class CacheTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        static string H(char c) => new(c, 64);

        await check("Content in use covers every home's pictures, frames, furniture, animations, songs (moods too) and NPCs, and saved groups", () =>
        {
            var framing = new PictureLayout(1, 1.5f, .4f, .6f, true);
            var npc = new HomeNpc { Name = "Aria Venn" };
            var home = new House
            {
                Furnishings =
                [
                    new Furnishing { CustomPicture = H('a'), Framing = framing, Portrait = true },
                    new Furnishing { CustomFurniture = H('b') },
                    new Furnishing { Poses = [new PoseSet { Package = H('c') }, new PoseSet()] },
                ],
                Music = new() { Playlist = [new MusicTrack { CustomMusic = H('d') }, new MusicTrack { OrchestrionId = 5 }] },
                Interior = new() { Moods = [new HomeMood { Music = new() { Playlist = [new MusicTrack { CustomMusic = H('e') }] } }] },
                Npcs = [npc],
            };
            var group = new SavedGroup { Pieces = [new Furnishing { CustomFurniture = H('f') }, new Furnishing { CustomPicture = H('g') }] };
            var use = ContentUse.Of([home], [group]);
            Assert(use.Pictures.SetEquals([H('a'), H('g')]));
            Assert(use.Frames.SetEquals([PictureCanvas.FrameKey(H('a'), framing, true), PictureCanvas.FrameKey(H('g'), null, false)]));
            // A frame of the same picture framed another way is another frame.
            Assert(!use.Frames.Contains(PictureCanvas.FrameKey(H('a'), framing, false)));
            Assert(use.Furniture.SetEquals([H('b'), H('f')]) && use.Animations.SetEquals([H('c')]));
            Assert(use.Songs.SetEquals([H('d'), H('e')]) && use.Npcs.SetEquals([npc.Id]));
            Assert(ContentUse.Of([], []).Pictures.Count == 0);
        });

        await check("Past the download limit, the items a home used longest ago go first, until the rest fits", () =>
        {
            var day = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            CachedContent[] cached =
            [
                new("furniture", H('1'), 600, day.AddDays(3)),
                new("picture", H('2'), 300, day),
                new("animations", H('3'), 500, day.AddDays(1)),
                new("picture", H('4'), 100, day.AddDays(5)),
            ];
            // 1500 in all: a limit of 1000 clears the oldest (300), then the next (500): 700 is left.
            Assert(ContentCache.Over(cached, 1000).Select(c => c.Hash).SequenceEqual([H('2'), H('3')]));
            Assert(ContentCache.Over(cached, 1500).Count == 0 && ContentCache.Over(cached, 5000).Count == 0);
            Assert(ContentCache.Over(cached, 0).Count == 4);
            Assert(ContentCache.Over([], 0).Count == 0);
            // Only the limits Settings offers are kept; anything else is the default.
            Assert(ContentCache.Sane(5_000_000_000) == 5_000_000_000 && ContentCache.Sane(123) == ContentCache.DefaultLimit && ContentCache.Sane(-1) == ContentCache.DefaultLimit);
            Assert(ContentCache.Limits.Contains(ContentCache.DefaultLimit));
        });
    }

    private static void Assert(bool value) { if (!value) throw new Exception("Cache assertion failed."); }
}
