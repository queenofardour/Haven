using System.Numerics;
using Haven.Core;
using Haven.Core.World;

// The world catalogue: the path rule, names from paths, the index's round trip and the
// thumbnail renderer.
internal static class WorldTests
{
    public static async Task Run(Func<string, Action, Task> check, House sample)
    {
        await check("World pieces travel as building parts: any zone model or group, never anything else (housing protocol 40)", () =>
        {
            foreach (var world in new[] {
                "bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt01a.mdl", "bg/ffxiv/fst_f1/dun/f1d2/bgparts/f1d2_a3_arch1.mdl",
                "bg/ex5/01_xkt_x6/shared/for_bg/sgbg_x6d8_b3_gmc01.sgb", "bg/ffxiv/sea_s1/twn/common/bgparts/s1t0_if1_bac1b.mdl",
                "bgcommon/world/emp/007/bgparts/w_emp_007_01d.mdl", "bgcommon/world/itm/12/bgparts/a.mdl", "bg/ex1/01_roc_r2/common/bgparts/r200_a0_box1a.mdl" })
            {
                Assert(BuildingParts.IsWorldAsset(world) && BuildingParts.IsHousingAsset(world));
                HouseFormat.Validate(sample with { Furnishings = [new Furnishing { BuildingPart = world, Position = [1, 0, 1] }] });
            }
            foreach (var other in new[] {
                "bg/ex2/02_est_e3/twn/e3t1/bgparts/../bgparts/e3t1_a1_lnt01a.mdl", "bg/ex2/02_est_e3/twn/e3t1/bgparts/./e3t1.mdl",
                "bg/ex2/02_est_e3/twn/e3t1/level/bg.lgb", "bg/ex2/02_est_e3/twn/e3t1/texture/e3t1_a1_lnt01_d.tex", "bg/ex2/02_est_e3/twn/e3t1/material/a.mtrl",
                "bg/ex2/02_est_e3/twn/e3t1/bgparts/E3T1.mdl", "bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1 a.mdl", "/bg/ffxiv/sea_s1/bgparts/a.mdl",
                "bg/ex0/02_est_e3/bgparts/a.mdl", "bg/ffxiv/bgparts/a.mdl", "bg/ffxiv/sea_s1/shared/for_hou/x/a.mdl", "bg/ffxiv/sea_s1/shared/for_bg/a.mdl",
                "bgcommon/bgparts/a.mdl", "chara/monster/m0001/obj/body/b0001/model/m0001b0001.mdl", "vfx/common/eff/a.avfx", "sound/zingle/a.scd",
                "bg/ffxiv/sea_s1/twn/s1t1/bgparts/a.mdl\n", "bg\\ffxiv\\sea_s1\\twn\\s1t1\\bgparts\\a.mdl", "bgcommon/a/b/c/d/e/f/g/bgparts/a.mdl",
                "bg/ffxiv/" + string.Concat(Enumerable.Repeat("a", 230)) + "/bgparts/a.mdl" })
            {
                Assert(!BuildingParts.IsWorldAsset(other));
                Refused(sample with { Furnishings = [new Furnishing { BuildingPart = other, Position = [1, 0, 1] }] });
            }
            // Housing, inn and the old rule's edge cases still hold: a world path is a building part, never a furnishing with an item.
            Assert(BuildingParts.IsHousingAsset("bgcommon/hou/indoor/general/0001/asset/fun_b0_m0001.sgb") && !BuildingParts.IsWorldAsset("bgcommon/hou/indoor/general/0001/asset/fun_b0_m0001.sgb"));
            Refused(sample with { Furnishings = [new Furnishing { ItemId = 5, BuildingPart = "bg/ffxiv/fst_f1/dun/f1d2/bgparts/f1d2_a3_arch1.mdl", Position = [1, 0, 1] }] });
            Assert(HousingWire.ProtocolRevision >= 40);
        });

        await check("World pieces get names from their paths: the zone, the part code as a word, a number among the zone's own", () =>
        {
            Assert(WorldNames.Code("bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt01a.mdl") == "lnt");
            Assert(WorldNames.Code("bg/ffxiv/sea_s1/twn/s1t1/bgparts/s1t1_a1_lmp01.mdl") == "lmp");
            Assert(WorldNames.Code("bgcommon/world/emp/007/bgparts/w_emp_007_01d.mdl") == "emp");
            Assert(WorldNames.Code("bg/ex5/01_xkt_x6/shared/for_bg/sgbg_x6d8_b3_gmc01.sgb") == "gmc");
            Assert(WorldNames.Code("bg/ex1/02_dra_d2/evt/d2e2/bgparts/d2e2_v1_0201a.mdl") == "");
            Assert(WorldNames.Word("lnt") == "lantern" && WorldNames.Word("iwa") == "rock" && WorldNames.Word("zzzq") == "piece");
            Assert(WorldNames.Category("lantern") == "Lighting" && WorldNames.Category("tree") == "Nature" && WorldNames.Category("piece") == WorldNames.Other);
            Assert(WorldNames.Family("bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt01a.mdl") == WorldNames.Family("bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt02.mdl"));
            Assert(WorldNames.Family("bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt01a.mdl") != WorldNames.Family("bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_wal01.mdl"));
            var index = new WorldIndex("v1", [new("ex2/02_est_e3/twn/e3t1", "Kugane"), new("ffxiv/sea_s1/twn/s1t1", "Limsa Lominsa Upper Decks")], [
                new("bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt02.mdl", 0, 4, 1),
                new("bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt01a.mdl", 0, 9, 2),
                new("bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_tre01.mdl", 0, 1, 1),
                new("bg/ffxiv/sea_s1/twn/s1t1/bgparts/s1t1_a1_lnt01.mdl", 1, 1, 1),
                new("bg/ffxiv/sea_s1/twn/s1t1/bgparts/s1t1_a1_qqq01.mdl", 1, 1, 1),
            ]);
            var names = WorldNames.All(index);
            Assert(names["bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt01a.mdl"].Name == "Kugane lantern 1");
            Assert(names["bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt02.mdl"].Name == "Kugane lantern 2");
            Assert(names["bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_tre01.mdl"] == new WorldName("Kugane tree", "Nature"));
            Assert(names["bg/ffxiv/sea_s1/twn/s1t1/bgparts/s1t1_a1_lnt01.mdl"].Name == "Limsa Lominsa Upper Decks lantern");
            Assert(names["bg/ffxiv/sea_s1/twn/s1t1/bgparts/s1t1_a1_qqq01.mdl"] == new WorldName("Limsa Lominsa Upper Decks piece", WorldNames.Other));
            // A names table wins where it has a name; entries that aren't world pieces, or have no name, are skipped.
            var table = WorldNameTable.Parse("""
                [{ "path": "bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_tre01.mdl", "name": "Kugane cherry tree", "category": "Trees" },
                 { "path": "chara/x.mdl", "name": "Nope" }, { "path": "bg/ffxiv/sea_s1/twn/s1t1/bgparts/s1t1_a1_lnt01.mdl", "name": "" }]
                """);
            Assert(table.Count == 1 && WorldNames.All(index, table)["bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_tre01.mdl"] == new WorldName("Kugane cherry tree", "Trees"));
            // A family named once (as the contact sheets name them): its variants take the name, numbered in path order.
            var families = WorldNameTable.Parse($$"""[{ "path": "{{WorldNames.Family("bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt01a.mdl")}}", "name": "Paper lantern", "category": "Lighting" }]""");
            var byFamily = WorldNames.All(index, families);
            Assert(byFamily["bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt01a.mdl"] == new WorldName("Paper lantern", "Lighting"));
            Assert(byFamily["bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt02.mdl"] == new WorldName("Paper lantern 2", "Lighting"));
            Assert(byFamily["bg/ffxiv/sea_s1/twn/s1t1/bgparts/s1t1_a1_lnt01.mdl"].Name == "Limsa Lominsa Upper Decks lantern");
            Assert(WorldNameTable.Parse("not json").Count == 0 && WorldNameTable.Embedded().Count >= 0);
            // Pieces with nothing to see aren't offered: a light-shaft-only floor of the Future Rewritten arena, for one.
            var hidden = WorldHidden.Embedded();
            Assert(hidden.Count > 1000 && hidden.Contains("bg/ex3/01_nvt_n4/goe/n4gw/bgparts/n4gw_a0_flo01.mdl") && !hidden.Contains("bg/ex3/01_nvt_n4/goe/n4gw/bgparts/n4gw_a0_lit01.mdl"));
            Assert(WorldModels.HasSurface(0, 0, 1) && !WorldModels.HasSurface(0, 0, 0));
            // A group says it's a set, once.
            Assert(WorldNames.AsSet(new("Brass airship", "Other")).Name == "Brass airship (set)" && WorldNames.AsSet(WorldNames.AsSet(new("Brass airship", "Other"))).Name == "Brass airship (set)");
        });

        await check("The world index keeps every piece through its cache, and is built again for another game version or format", () =>
        {
            var index = new WorldIndex("ffxiv=2026.10.30.0000.0000", [new("ex2/02_est_e3/twn/e3t1", "Kugane"), new("ffxiv/fst_f1/dun/f1d2", "Haukke Manor")], [
                new("bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt01a.mdl", 0, 12, 3, WorldCollision.Mesh, "bg/ex2/02_est_e3/twn/e3t1/collision/e3t1_a1_lnt01a.pcb"),
                new("bg/ex2/02_est_e3/shared/for_bg/sgbg_e3t1_a1_lnt01.sgb", 0, 2, 1),
                new("bg/ffxiv/fst_f1/dun/f1d2/bgparts/f1d2_a3_arch1.mdl", 1, 1, 1, WorldCollision.Box),
            ]);
            using var stream = new MemoryStream();
            index.Write(stream);
            stream.Position = 0;
            var read = WorldIndex.Read(stream, "ffxiv=2026.10.30.0000.0000")!;
            Assert(read.Zones.SequenceEqual(index.Zones) && read.Pieces.SequenceEqual(index.Pieces) && read.Contains("bg/ex2/02_est_e3/shared/for_bg/sgbg_e3t1_a1_lnt01.sgb"));
            Assert(read.Find("bg/ex2/02_est_e3/twn/e3t1/bgparts/e3t1_a1_lnt01a.mdl") is { Collision: WorldCollision.Mesh, Uses: 12, Zones: 3 } lantern && read.ZoneOf(lantern).Name == "Kugane");
            Assert(read.Find("bg/ex2/02_est_e3/shared/for_bg/sgbg_e3t1_a1_lnt01.sgb")!.Group && !read.Contains("bg/ex2/02_est_e3/twn/e3t1/bgparts/other.mdl"));
            stream.Position = 0;
            Assert(WorldIndex.Read(stream, "ffxiv=2026.11.10.0000.0000") == null);            // a patch: built again
            Assert(WorldIndex.Read(new MemoryStream([1, 2, 3]), "ffxiv=2026.10.30.0000.0000") == null); // damaged
            // Saved and loaded through a file, atomically.
            var file = Path.Combine(Path.GetTempPath(), $"haven-world-{Guid.NewGuid():N}.gz");
            try { index.Save(file); Assert(WorldIndex.Load(file, index.GameVersion)!.Pieces.Count == 3 && WorldIndex.Load(file + ".missing", "x") == null); }
            finally { File.Delete(file); }
            // A piece that isn't a world path, names no zone or comes twice is refused; so is a collision path out of the game's.
            Assert(Throws(() => new WorldIndex("v", [new("a", "A")], [new("chara/x.mdl", 0, 1, 1)])));
            Assert(Throws(() => new WorldIndex("v", [new("a", "A")], [new("bg/ffxiv/fst_f1/dun/f1d2/bgparts/a.mdl", 1, 1, 1)])));
            Assert(Throws(() => new WorldIndex("v", [new("a", "A")], [new("bg/ffxiv/fst_f1/dun/f1d2/bgparts/a.mdl", 0, 1, 1), new("bg/ffxiv/fst_f1/dun/f1d2/bgparts/a.mdl", 0, 1, 1)])));
            Assert(WorldIndex.IsCollisionPath("bg/ffxiv/fst_f1/dun/f1d2/collision/a.pcb") && !WorldIndex.IsCollisionPath("bg/../a.pcb") && !WorldIndex.IsCollisionPath("bg/ffxiv/a.mdl"));
        });

        await check("An unbuilt world model costs the scene budget from the index (one object, a collider only if the game makes it solid); a group is counted by building it", () =>
        {
            Assert(new WorldPiece("bg/ffxiv/a/bgparts/a.mdl", 0, 1, 1, WorldCollision.None).UnbuiltCost == (1, 0));
            Assert(new WorldPiece("bg/ffxiv/a/bgparts/a.mdl", 0, 1, 1, WorldCollision.Box).UnbuiltCost == (1, 1));
            Assert(new WorldPiece("bg/ffxiv/a/bgparts/a.mdl", 0, 1, 1, WorldCollision.Mesh, "bg/ffxiv/a/collision/a.pcb").UnbuiltCost == (1, 1));
            Assert(new WorldPiece("bg/ffxiv/a/shared/for_bg/a.sgb", 0, 1, 1).UnbuiltCost == null);
        });

        await check("The thumbnail renderer draws a textured, lit mesh in the middle of a transparent picture", () =>
        {
            // A red-textured upright square, facing the camera's side (+Z), and a green one behind it, hidden.
            var red = new ThumbnailTexture([200, 30, 30, 255, 200, 30, 30, 255, 200, 30, 30, 255, 200, 30, 30, 255], 2, 2);
            ThumbnailMesh Square(float z, ThumbnailTexture? texture, Vector3 colour) => new(
                [new(-1, -1, z), new(1, -1, z), new(1, 1, z), new(-1, 1, z)], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [0, 1, 2, 0, 2, 3], texture, colour, 0, Matrix4x4.Identity);
            var picture = ThumbnailRenderer.Render([Square(0, red, Vector3.One), Square(-.5f, null, new(0, 1, 0))])!;
            Assert(picture.Length == ThumbnailRenderer.Size * ThumbnailRenderer.Size * 4);
            var centre = (ThumbnailRenderer.Size / 2 * ThumbnailRenderer.Size + ThumbnailRenderer.Size / 2) * 4;
            Assert(picture[centre + 3] == 255 && picture[centre] > 120 && picture[centre] > picture[centre + 1] * 2 && picture[centre] > picture[centre + 2] * 2);
            Assert(picture[3] == 0); // a corner is background
            // Lit: the same square turned away from the light is darker.
            var turned = ThumbnailRenderer.Render([Square(0, red, Vector3.One) with { Normals = [-Vector3.UnitX, -Vector3.UnitX, -Vector3.UnitX, -Vector3.UnitX] }])!;
            Assert(turned[centre] < picture[centre]);
            // An alpha cut leaves out clear texels; nothing at all has no picture.
            var clear = new ThumbnailTexture([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 2, 2);
            Assert(ThumbnailRenderer.Render([Square(0, clear, Vector3.One) with { AlphaCut = .5f }]) == null);
            Assert(ThumbnailRenderer.Render([]) == null);
        });
    }

    private static void Refused(House home)
    {
        try { HouseFormat.Validate(home); }
        catch (InvalidDataException) { return; }
        throw new Exception("A home with a bad world piece was accepted.");
    }
    private static bool Throws(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    private static void Assert(bool value) { if (!value) throw new Exception("World catalogue assertion failed."); }
}
