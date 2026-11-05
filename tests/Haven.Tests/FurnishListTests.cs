using Haven.Core;

internal static class FurnishListTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Furnish lists keep newest-first Recent capped at 24, toggle Favourites and survive a bad file", () =>
        {
            var lists = new FurnishLists();
            for (var i = 0; i < 30; i++) lists.Used(i.ToString());
            lists.Used("5");
            Assert(lists.Recent.Count == FurnishLists.RecentLimit && lists.Recent[0] == "5" && lists.Recent[1] == "29" && lists.Recent.Count(x => x == "5") == 1);
            lists.ToggleFavourite("6601"); lists.ToggleFavourite("bg/house/wall"); lists.ToggleFavourite("6601");
            Assert(!lists.IsFavourite("6601") && lists.IsFavourite("bg/house/wall"));
            var path = Path.Combine(Path.GetTempPath(), "haven-furnish-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                lists.Save(path);
                var loaded = FurnishLists.Load(path);
                Assert(loaded.Recent.SequenceEqual(lists.Recent) && loaded.IsFavourite("bg/house/wall"));
                File.WriteAllText(path, "{ not json");
                Assert(FurnishLists.Load(path).Recent.Count == 0);
                Assert(FurnishLists.Load(path + ".missing").Favourites.Count == 0);
            }
            finally { File.Delete(path); }
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Furnish list assertion failed."); }
}
