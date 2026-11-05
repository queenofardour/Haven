using System.Numerics;
using System.Text;
using Haven.Core;

internal static class LayoutTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        // A MakePlace layout as its plugin writes one: location (x, z, y), rotation a roll of −r about Z, dye RRGGBBAA.
        static string Rotation(float gameRadians)
        {
            var q = Quaternion.CreateFromYawPitchRoll(0, 0, -gameRadians);
            return FormattableString.Invariant($"[{q.X}, {q.Y}, {q.Z}, {q.W}]");
        }
        var json = $$"""
        {
          "playerTransform": { "location": [0, 0, 0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] },
          "houseSize": "Small",
          "interiorScale": 1,
          "interiorFixture": [
            { "level": "Ground Floor", "type": "Wall", "name": "Riviera Interior Wall", "itemId": 6532 },
            { "level": "Ground Floor", "type": "Window", "name": "Riviera Interior Window", "itemId": 6533 },
            { "level": "", "type": "District", "name": "Mist", "itemId": 0 }
          ],
          "interiorFurniture": [
            { "name": "Riviera Round Table", "itemId": 6601,
              "transform": { "location": [1.5, -2, 0.25], "rotation": {{Rotation(MathF.PI / 2)}}, "scale": [1, 1, 1] },
              "properties": { "color": "9E3B39FF" },
              "attachments": [ { "name": "Brass Pricket", "itemId": -5,
                "transform": { "location": [1.5, -2, 1.1], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] }, "properties": [], "attachments": [] } ] },
            { "name": "Blank Minimalist Partition", "itemId": 48134,
              "transform": { "location": [-4.72, 6.05, -0.9], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] },
              "properties": { "material": { "name": "Garlean Concrete Inner Wall", "itemId": 35809 } } },
            { "name": "Broken", "itemId": 1, "transform": { "location": [1, 2] } }
          ],
          "exteriorFurniture": [ { "name": "Yard Lamp", "itemId": 9, "transform": { "location": [0, 0, 0] } } ]
        }
        """;
        await check("MakePlace layouts read furnishings in the interior's coordinates, with dyes, attachments and the shell", () =>
        {
            var layout = MakePlaceLayout.Read(Encoding.UTF8.GetBytes(json));
            Assert(layout.Pieces.Length == 3 && layout.OutdoorPieces == 1);
            var table = layout.Pieces[0];
            // MakePlace's (x, z, y) becomes the game's (x, y, z); a quarter turn comes back as 90 degrees.
            Assert(table.ItemId == 6601 && table.Position == new Vector3(1.5f, .25f, -2) && MathF.Abs(table.Yaw - 90) < .01f && table.Color == 0x9E3B39);
            // An attachment stands on its own transform; a negative ID can only be found by name.
            var candle = layout.Pieces[1];
            Assert(candle.ItemId == 0 && candle.Name == "Brass Pricket" && candle.Position == new Vector3(1.5f, 1.1f, -2) && candle.Yaw == 0 && candle.Color == null);
            // A material names the item a piece shows (here a partition's wallpaper); the others show none.
            Assert(layout.Pieces[2].Material == 35809 && table.Material == 0 && candle.Material == 0);
            Assert(layout.District == "Mist" && layout.HouseSize == "Small" && HomeLayouts.Shell(layout) == ("s1", 2));
            Assert(layout.Fixtures.Length == 2 && layout.Fixtures[0] == new LayoutFixture("Wall", "Ground Floor", "Riviera Interior Wall", 6532));
            // No lightLevel: the shell's own brightness. One given is the game's room brightness, 0 to 5.
            Assert(layout.Brightness == null);
            float? Light(string level) => MakePlaceLayout.Read(Encoding.UTF8.GetBytes(json.Replace("\"interiorScale\": 1,", $"\"interiorScale\": 1, \"lightLevel\": {level},"))).Brightness;
            Assert(Light("1") == 1 && Light("0") == 0 && Light("3") == 3 && Light("2.6") == 3 && Light("9") == 5 && Light("-1") == null);
        });
        await check("MakePlace layouts honour interiorScale, every rotation and every shell; other files are refused", () =>
        {
            var scaled = MakePlaceLayout.Read(Encoding.UTF8.GetBytes(json.Replace("\"interiorScale\": 1", "\"interiorScale\": 100")));
            Assert(Vector3.Distance(scaled.Pieces[0].Position, new Vector3(.015f, .0025f, -.02f)) < 1e-6f);
            foreach (var degrees in new[] { -179f, -90f, -1f, 0f, 45f, 135f, 180f })
            {
                var q = Quaternion.CreateFromYawPitchRoll(0, 0, -degrees * MathF.PI / 180);
                var yaw = HomeLayouts.YawFromMakePlace(q);
                Assert(MathF.Abs(yaw - degrees) < .01f || MathF.Abs(MathF.Abs(yaw) - 180) < .01f && MathF.Abs(MathF.Abs(degrees) - 180) < .01f);
            }
            Assert(HomeLayouts.Degrees(MathF.PI * 3) is var d && MathF.Abs(MathF.Abs(d) - 180) < .01f && HomeLayouts.Degrees(-MathF.PI / 2) == -90);
            Assert(HomeLayouts.Shell(new(null, "Empyreum", [], [])) == null);
            Assert(HomeLayouts.Shell(new("Apartment", "The Lavender Beds", [], [])) == ("f1", 1) && HomeLayouts.Shell(new("Large", "Shirogane", [], [])) == ("e1", 4));
            Reject(() => MakePlaceLayout.Read(Encoding.UTF8.GetBytes("{\"name\": \"not a layout\"}")));
            Reject(() => MakePlaceLayout.Read(Encoding.UTF8.GetBytes("not json")));
        });
        await check("Saved layouts round-trip, copy with fresh identities, and are stored with a recoverable delete", () =>
        {
            var layout = new SavedLayout
            {
                Name = "Kobai Goten Apartment", Source = "Saved in Kobai Goten Apartment", TerritoryId = 655,
                Furnishings = [new Furnishing { ItemId = 6601, Position = [1, 0, 2], Rotation = 90, StainId = 12 }],
                Interior = new InteriorOptions { Brightness = 0 },
            };
            var read = LayoutFormat.Parse(LayoutFormat.Serialize(layout));
            Assert(read.Name == layout.Name && read.TerritoryId == 655 && read.Furnishings.Single().StainId == 12 && read.Interior.Brightness == 0);
            var copy = LayoutFormat.Copy(read);
            Assert(copy.Single().Id != read.Furnishings.Single().Id && copy.Single() with { Id = read.Furnishings.Single().Id } == read.Furnishings.Single());
            Reject(() => LayoutFormat.Validate(layout with { Name = " " }));
            Reject(() => LayoutFormat.Validate(layout with { TerritoryId = 0 }));
            Reject(() => LayoutFormat.Validate(layout with { Furnishings = [new Furnishing { ItemId = 1, Position = [float.NaN, 0, 0] }] }));
            Reject(() => LayoutFormat.Parse(Encoding.UTF8.GetBytes("{\"name\": \"x\", \"unknown\": 1}")));
            var folder = Path.Combine(Path.GetTempPath(), "haven-layout-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new LayoutStore(folder);
                Assert(store.List().Count == 0);
                store.Save(layout); store.Save(layout with { Id = Guid.NewGuid(), Name = "Second", SavedAt = layout.SavedAt.AddMinutes(1) });
                File.WriteAllText(Path.Combine(folder, "broken.layout.json"), "{");
                var listed = store.List();
                Assert(listed.Count == 2 && listed[0].Name == "Second");
                store.Delete(layout.Id);
                Assert(store.List().Single().Name == "Second" && Directory.GetFiles(Path.Combine(folder, "deleted")).Length == 1);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });
        await check("Homes belong to the character who keeps them; unsorted homes show to every character", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "haven-owner-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var file = Path.Combine(folder, "home-owners.json");
                string me = HomeOwners.Key(0x0040000112345678), alt = HomeOwners.Key(0x00400001ABCDEF01);
                Assert(me.Length == 16 && me != alt && HomeOwners.Key(0) == "" && !me.Contains("12345678"));
                Guid mine = Guid.NewGuid(), old = Guid.NewGuid();
                var owners = new HomeOwners(file);
                Assert(owners.Unsorted(old) && owners.Visible(old, me) && owners.Visible(old, alt) && owners.Visible(old, ""));
                owners.Assign(mine, me);
                Assert(owners.Visible(mine, me) && !owners.Visible(mine, alt) && !owners.Visible(mine, ""));
                // The index survives a reload; forgetting a deleted home removes it.
                owners = new HomeOwners(file);
                Assert(!owners.Unsorted(mine) && owners.Visible(mine, me) && !owners.Visible(mine, alt));
                owners.Assign(mine, ""); Assert(owners.Visible(mine, me) && !owners.Visible(mine, alt));
                owners.Forget(mine); Assert(new HomeOwners(file).Unsorted(mine));
                // A damaged index leaves every home unsorted rather than hidden.
                File.WriteAllText(file, "{");
                Assert(new HomeOwners(file).Visible(mine, alt));
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });
        await check("A layout's colour becomes the nearest housing dye", () =>
        {
            (uint, uint)[] dyes = [(0, 0xFFFFFF), (12, 0x9E3B39), (36, 0x1E1E1E), (1, 0xE4DFD0)];
            Assert(HomeLayouts.NearestDye(0xA03A3A, dyes) == 12 && HomeLayouts.NearestDye(0x101010, dyes) == 36 && HomeLayouts.NearestDye(0xFFFFFF, dyes) == 1);
            Assert(HomeLayouts.NearestDye(0x123456, []) == 0);
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Layout assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("An invalid layout was accepted.");
    }
}
