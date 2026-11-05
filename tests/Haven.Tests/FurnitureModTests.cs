using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Haven.Core.Furniture;

internal static class FurnitureModTests
{
    const string Model = "bgcommon/hou/indoor/general/0013/bgparts/fun_b0_m0013.mdl";
    const string MaterialA = "bgcommon/hou/indoor/general/0013/material/fun_b0_m0013_0a.mtrl";
    const string MaterialB = "bgcommon/hou/indoor/general/0013/material/fun_b0_m0013_0b.mtrl";
    const string TextureA = "bgcommon/hou/indoor/general/0013/texture/fun_b0_m0013_0a_d.tex";
    const string TextureB = "bgcommon/hou/indoor/general/0013/texture/fun_b0_m0013_0b_d.tex";

    public static async Task Run(Func<string, Action, Task> check)
    {
        var game = new Dictionary<string, byte[]>
        {
            [Model] = Mdl(MaterialA, MaterialB),
            [MaterialA] = Mtrl(TextureA), [MaterialB] = Mtrl(TextureB),
            [TextureA] = Tex(4), [TextureB] = Tex(4),
        };
        byte[]? Game(string path) => game.GetValueOrDefault(path);

        await check("Penumbra mods read with their default options, priorities and swaps, and keep their files inside the mod", () =>
        {
            using var mod = PenumbraMod.Open(Pmp(new()
            {
                ["meta.json"] = Json("{\"Name\":\"Toilet\",\"Author\":\"Someone\"}"),
                ["default_mod.json"] = Json($"{{\"Files\":{{\"{TextureA}\":\"files\\\\a.tex\"}},\"FileSwaps\":{{\"{MaterialA.ToUpperInvariant()}\":\"{MaterialB}\"}}}}"),
                ["group_001_colour.json"] = Json($"{{\"Name\":\"Colour\",\"Type\":\"Single\",\"DefaultSettings\":1,\"Priority\":1,\"Options\":[{{\"Name\":\"Red\",\"Files\":{{\"{TextureB}\":\"red.tex\"}}}},{{\"Name\":\"Blue\",\"Files\":{{\"{TextureB}\":\"blue.tex\"}}}}]}}"),
                ["group_002_extras.json"] = Json($"{{\"Name\":\"Extras\",\"Type\":\"Multi\",\"DefaultSettings\":2,\"Priority\":0,\"Options\":[{{\"Name\":\"No\",\"Files\":{{\"a/b.tex\":\"no.tex\"}}}},{{\"Name\":\"Yes\",\"Files\":{{\"{TextureB}\":\"lower.tex\",\"c/d.tex\":\"yes.tex\"}}}}]}}"),
                ["files/a.tex"] = Tex(8), ["blue.tex"] = Tex(16), ["yes.tex"] = Tex(2),
            }));
            Assert(mod.Name == "Toilet" && mod.Author == "Someone");
            Assert(mod.Files[TextureA].Entry == "files/a.tex" && mod.Files[MaterialA].SwapTo == MaterialB);
            Assert(mod.Files[TextureB].Entry == "blue.tex");                  // the Single option chosen, over a lower-priority group
            Assert(mod.Files.ContainsKey("c/d.tex") && mod.Files.ContainsKey("a/b.tex"));            // every Multi option, whatever its default
            // Newer Penumbra: groups in meta.json, a Multi group with no default (Basic Urinal), option priorities.
            using var urinal = PenumbraMod.Open(Pmp(new()
            {
                ["meta.json"] = Json($"{{\"FileVersion\":4,\"Name\":\"Basic Urinal\",\"Groups\":[{{\"Type\":\"Multi\",\"Name\":\"Options\",\"Options\":[" +
                    $"{{\"Name\":\"Urinal\",\"Files\":{{\"{TextureA}\":\"urinal.tex\",\"{TextureB}\":\"low.tex\"}}}}," +
                    $"{{\"Name\":\"Tiles\",\"Priority\":1,\"Files\":{{\"{TextureB}\":\"tiles.tex\"}}}}]}}]}}"),
                ["urinal.tex"] = Tex(4), ["tiles.tex"] = Tex(4), ["low.tex"] = Tex(4),
            }));
            Assert(urinal.Name == "Basic Urinal" && urinal.Files[TextureA].Entry == "urinal.tex" && urinal.Files[TextureB].Entry == "tiles.tex");
            // Choosing options: a Single group's other option, or Multi add-ons switched off; every option at once to find items.
            // Groups are listed in the order they apply: lower priority first (Extras, then Colour).
            Assert(mod.Groups.Count == 2 && !mod.Groups[0].Single && mod.Groups[1].Single && mod.Groups[1].Options.Select(o => o.Name).SequenceEqual(["Red", "Blue"]));
            mod.Select(new ModSelection(new Dictionary<int, ulong> { [1] = 0, [0] = 0 }));
            Assert(mod.Files[TextureB].Entry == "red.tex" && !mod.Files.ContainsKey("c/d.tex") && mod.Files.ContainsKey(TextureA));
            mod.Select(new ModSelection(new Dictionary<int, ulong> { [0] = 1 }));
            Assert(mod.Files[TextureB].Entry == "blue.tex" && mod.Files.ContainsKey("a/b.tex") && !mod.Files.ContainsKey("c/d.tex"));
            mod.SelectAll();
            Assert(mod.Files.ContainsKey("a/b.tex") && mod.Files.ContainsKey("c/d.tex"));
            mod.Select(ModSelection.Default);
            Assert(mod.Files[TextureB].Entry == "blue.tex");
            Assert(mod.Read("files/a.tex") is { Length: > 0 } && mod.Read("../outside.tex") == null && mod.Read("files/../../x") == null);
        });

        await check("A furniture mod's changed files get paths of their own; the model's material paths keep their length", () =>
        {
            // The mod changes only texture B: B's material and the model need new paths too; material A stays vanilla.
            using var mod = PenumbraMod.Open(Pmp(new()
            {
                ["default_mod.json"] = Json($"{{\"Files\":{{\"{TextureB}\":\"toilet_d.tex\"}}}}"),
                ["toilet_d.tex"] = Tex(32),
            }));
            Assert(FurniturePackages.Changes(mod, [Model], Game));
            Assert(!FurniturePackages.Changes(mod, ["bgcommon/hou/indoor/general/0099/bgparts/fun_b0_m0099.mdl"], Game));
            var (zip, manifest) = FurniturePackages.Build(mod, 13, [Model], Game, "  Toilet  ");
            Assert(manifest.Name == "Toilet" && manifest.BaseItem == 13 && manifest.Files.Length == 3 && manifest.Models.Keys.Single() == Model);
            var package = FurniturePackages.Read(zip);
            var model = package.Files[manifest.Models[Model]];
            var materials = FurnitureFiles.ModelMaterials(model);
            Assert(materials[0] == MaterialA && materials[1] != MaterialB && materials[1].Length == MaterialB.Length && materials[1].StartsWith(FurniturePackages.Root + manifest.Id + "/"));
            Assert(model.Length == game[Model].Length);
            var texture = FurnitureFiles.MaterialTextures(package.Files[materials[1]]).Single();
            Assert(texture.StartsWith(FurniturePackages.Root + manifest.Id + "/") && package.Files[texture].SequenceEqual(Tex(32)));
            // The same import gives the same paths.
            Assert(FurniturePackages.Build(mod, 13, [Model], Game, "Toilet").Manifest.Id == manifest.Id);
            // A mod that changes nothing the item draws with is refused.
            using var unrelated = PenumbraMod.Open(Pmp(new() { ["default_mod.json"] = Json("{\"Files\":{\"x/y.tex\":\"y.tex\"}}"), ["y.tex"] = Tex(2) }));
            RejectOperation(() => FurniturePackages.Build(unrelated, 13, [Model], Game, "x"));
        });

        await check("A furnishing carries its custom furniture package beside its base item", () =>
        {
            var hash = new string('f', 64);
            var item = new Haven.Core.Furnishing { Id = Guid.NewGuid(), ItemId = 13, Position = [0, 0, 0], CustomFurniture = hash };
            var house = new Haven.Core.House { Name = "Furniture test", TerritoryId = 282, Stage = System.Text.Json.JsonDocument.Parse("{\"FormatVersion\":2,\"Objects\":{}}").RootElement.Clone(), Furnishings = [item] };
            Haven.Core.HouseFormat.Validate(house);
            Assert(Haven.Core.HouseFormat.Parse(Haven.Core.HouseFormat.Serialize(house)).Furnishings[0].CustomFurniture == hash);
            Assert(!Encoding.UTF8.GetString(Haven.Core.HouseFormat.Serialize(house with { Furnishings = [item with { CustomFurniture = null }] })).Contains("customFurniture", StringComparison.OrdinalIgnoreCase));
            RejectData(() => Haven.Core.HouseFormat.Validate(house with { Furnishings = [item with { CustomFurniture = "not-a-fingerprint" }] }));
            RejectData(() => Haven.Core.HouseFormat.Validate(house with { CustomLayout = true, Furnishings = [item with { ItemId = 0, BuildingPart = "bgcommon/hou/indoor/x.sgb" }] }));
        });

        await check("A minion model becomes a prop: its material names become full paths and everything after the strings moves", () =>
        {
            const string Minion = "chara/monster/m8045/obj/body/b0001/model/m8045b0001.mdl";
            const string CharaMaterial = "chara/monster/m8045/obj/body/b0001/material/v0001/mt_m8045b0001_a.mtrl";
            const string Diffuse = "chara/monster/m8045/obj/body/b0001/texture/v01_m8045b0001_d.tex";
            const string Normal = "chara/monster/m8045/obj/body/b0001/texture/v01_m8045b0001_n.tex";
            var model = CharaMdl("n_root", "/mt_m8045b0001_a.mtrl");
            // The rewrite alone: the new name reads in sequence, offsets move, the data after is untouched.
            var rewritten = FurnitureFiles.RewriteModelMaterials(model, n => n == "/mt_m8045b0001_a.mtrl" ? "bgcommon/hou/hv/abcdefabcdef/m0.mtrl" : null);
            Assert(FurnitureFiles.ModelMaterials(rewritten).Contains("bgcommon/hou/hv/abcdefabcdef/m0.mtrl"));
            var delta = rewritten.Length - model.Length;
            Assert(delta > 0 && delta % 8 == 0);
            uint Read(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
            Assert(Read(rewritten, 0x10) == Read(model, 0x10) + delta && Read(rewritten, 0x1C) == Read(model, 0x1C) + delta && Read(rewritten, 0x08) == Read(model, 0x08) + delta);
            Assert(rewritten.AsSpan((int)Read(rewritten, 0x10)).SequenceEqual(model.AsSpan((int)Read(model, 0x10))));
            // The skeleton goes (a scenery object would try to animate it): no bones, the data moves back, unchanged.
            Assert(FurnitureFiles.ModelBones(model) == 1);
            var stripped = FurnitureFiles.StripSkeleton(model);
            Assert(FurnitureFiles.ModelBones(stripped) == 0 && stripped.Length == model.Length - 4 - 32);
            Assert(Read(stripped, 0x10) == Read(model, 0x10) - 36 && stripped.AsSpan((int)Read(stripped, 0x10)).SequenceEqual(model.AsSpan((int)Read(model, 0x10))));
            Assert(FurnitureFiles.ModelMaterials(stripped).SequenceEqual(FurnitureFiles.ModelMaterials(model)));
            // Version 6 (Dawntrail) models list their bone tables differently; they go too.
            var six = CharaMdl("n_root", "/mt_m8045b0001_a.mtrl", 6);
            var sixStripped = FurnitureFiles.StripSkeleton(six);
            Assert(FurnitureFiles.ModelBones(sixStripped) == 0 && sixStripped.Length == six.Length - 4 - 8 - 32);
            Assert(sixStripped.AsSpan((int)Read(sixStripped, 0x10)).SequenceEqual(six.AsSpan((int)Read(six, 0x10))));
            // A layout that doesn't add up is refused rather than guessed at.
            var odd = (byte[])model.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(odd.AsSpan(0x10), Read(model, 0x10) + 8);
            RejectData(() => FurnitureFiles.StripSkeleton(odd));
            // A whole prop: the character material becomes a scenery one on the prop's own textures.
            var game = new Dictionary<string, byte[]> { [FurniturePackages.PropMaterialTemplate] = Mtrl3("bg.shpk", "bgcommon/hou/indoor/general/0112/texture/fun_b0_m0112_0a_d.tex",
                "bgcommon/hou/indoor/general/0112/texture/fun_b0_m0112_0a_n.tex", "bgcommon/hou/indoor/general/0112/texture/fun_b0_m0112_0a_s.tex") };
            using var mod = PenumbraMod.Open(Pmp(new()
            {
                ["default_mod.json"] = Json($"{{\"Files\":{{\"{Minion}\":\"chair.mdl\",\"{CharaMaterial}\":\"chair.mtrl\",\"{Diffuse}\":\"d.tex\",\"{Normal}\":\"n.tex\"}}}}"),
                ["chair.mdl"] = model, ["chair.mtrl"] = Mtrl3("character.shpk", Diffuse, Normal, "chara/common/texture/missing_s.tex"), ["d.tex"] = Tex(8), ["n.tex"] = Tex(8),
            }));
            Assert(FurniturePackages.PropModels(mod).SequenceEqual([Minion]));
            var (zip, manifest) = FurniturePackages.BuildProp(mod, Minion, p => game.GetValueOrDefault(p) ?? (p.EndsWith("_s.tex") ? Tex(4) : null), "Chair of Motivation");
            var package = FurniturePackages.Read(zip);
            Assert(manifest.Prop && manifest.BaseItem == 0 && manifest.Models.Keys.Single() == Minion && FurnitureFiles.ModelBones(package.Files[manifest.Models[Minion]]) == 0);
            var material = FurnitureFiles.ModelMaterials(package.Files[manifest.Models[Minion]]).Single(m => m.StartsWith(FurniturePackages.Root));
            Assert(FurnitureFiles.MaterialShader(package.Files[material]) == "bg.shpk");
            var textures = FurnitureFiles.MaterialTextures(package.Files[material]);
            Assert(textures.Take(2).All(x => x.StartsWith(FurniturePackages.Root + manifest.Id + "/")) && package.Files[textures[0]].SequenceEqual(Tex(8)));
            // A prop has no base item; a furniture package must have one.
            RejectData(() => FurniturePackages.Read(Repack(zip, m => m with { BaseItem = 13 })));
        });

        await check("A minion prop's textures come from its material's samplers, and its material set from the minion's variant", () =>
        {
            const uint Diffuse = 0x115306BE, Normal = 0x0C5EC1F1, Mask = 0x8A4E82B6, Index = 0x565F8FD8;
            // Spooky Pumpkin Set: textures named unknown_d_<n>, with a mask and no specular map.
            var pumpkin = MtrlSampled(["t/unknown_id_1.tex", "t/unknown_n_2.tex", "t/unknown_m_3.tex", "t/unknown_d_4.tex"], (Index, 0), (Normal, 1), (Mask, 2), (Diffuse, 3));
            Assert(FurnitureFiles.MaterialSamplers(pumpkin).Count == 4);
            Assert(FurnitureFiles.CharacterTextures(pumpkin) == ("t/unknown_d_4.tex", "t/unknown_n_2.tex", null));
            // The samplers decide, whatever the names say.
            var swapped = MtrlSampled(["t/a_n.tex", "t/a_d.tex"], (Diffuse, 0), (Normal, 1));
            Assert(FurnitureFiles.CharacterTextures(swapped) == ("t/a_n.tex", "t/a_d.tex", null));
            // Without samplers, names: at the end or in the middle.
            var named = MtrlSampled(["t/unknown_s_9.tex", "t/unknown_d_7.tex", "t/b_norm.tex"]);
            Assert(FurnitureFiles.MaterialSamplers(named).Count == 0 && FurnitureFiles.CharacterTextures(named) == ("t/unknown_d_7.tex", "t/b_norm.tex", "t/unknown_s_9.tex"));
            // The .imc file: one part, the default entry, then variants 1 (Wayward Hatchling, v0001) and 2 (Black Chocobo Chick, v0002).
            byte[] imc = [2, 0, 1, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0];
            Assert(FurnitureFiles.ImcMaterialSet(imc, 1) == 1 && FurnitureFiles.ImcMaterialSet(imc, 2) == 2 && FurnitureFiles.ImcMaterialSet(imc, 3) == null);
            // A prop takes the minion's own material set, and each set is a separate piece.
            const string Minion = "chara/monster/m8002/obj/body/b0001/model/m8002b0001.mdl";
            string Material(int set) => $"chara/monster/m8002/obj/body/b0001/material/v{set:0000}/mt_m8002b0001_a.mtrl";
            var game = new Dictionary<string, byte[]> { [FurniturePackages.PropMaterialTemplate] = Mtrl3("bg.shpk", "bgcommon/x_d.tex", "bgcommon/x_n.tex", "bgcommon/x_s.tex"), ["bgcommon/x_s.tex"] = Tex(4) };
            using var mod = PenumbraMod.Open(Pmp(new()
            {
                ["default_mod.json"] = Json($"{{\"Files\":{{\"{Minion}\":\"m.mdl\",\"{Material(1)}\":\"chick.mtrl\",\"{Material(2)}\":\"pumpkin.mtrl\",\"t/chick_d.tex\":\"chick.tex\",\"t/unknown_d_4.tex\":\"pumpkin.tex\"}}}}"),
                ["m.mdl"] = CharaMdl("n_root", "/mt_m8002b0001_a.mtrl"),
                ["chick.mtrl"] = MtrlSampled(["t/chick_d.tex"], (Diffuse, 0)), ["pumpkin.mtrl"] = MtrlSampled(["t/unknown_d_4.tex"], (Diffuse, 0)),
                ["chick.tex"] = Tex(4), ["pumpkin.tex"] = Tex(8),
            }));
            byte[] Colour(int set)
            {
                var (zip, manifest) = FurniturePackages.BuildProp(mod, Minion, game.GetValueOrDefault, "Jack-o-Lantern", set);
                var package = FurniturePackages.Read(zip);
                var material = FurnitureFiles.ModelMaterials(package.Files[manifest.Models[Minion]]).Single(m => m.StartsWith(FurniturePackages.Root));
                return package.Files[FurnitureFiles.MaterialTextures(package.Files[material])[0]];
            }
            Assert(Colour(1).SequenceEqual(Tex(4)) && Colour(2).SequenceEqual(Tex(8)));
            Assert(FurniturePackages.BuildProp(mod, Minion, game.GetValueOrDefault, "x", 1).Manifest.Id != FurniturePackages.BuildProp(mod, Minion, game.GetValueOrDefault, "x", 2).Manifest.Id);
        });

        await check("Character textures decode: uncompressed, BC1, BC3, BC5 and BC7", () =>
        {
            var plain = TextureDecoding.Decode(TexPixels(2, 1, (x, y) => x == 0 ? (10, 20, 30, 40) : (50, 60, 70, 80)));
            Assert(plain.Width == 2 && plain.Rgba.SequenceEqual(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 }));
            // BC1: red and blue ends; pixel 0 the first end, pixel 1 the second, pixel 2 two thirds of the way to red.
            byte[] bc1 = [0x00, 0xF8, 0x1F, 0x00, 0b100100, 0, 0, 0];
            var (one, _, _) = TextureDecoding.Decode(TexBlocks(TextureDecoding.Bc1, 4, 4, bc1));
            Assert(one.AsSpan(0, 4).SequenceEqual(new byte[] { 255, 0, 0, 255 }) && one.AsSpan(4, 4).SequenceEqual(new byte[] { 0, 0, 255, 255 }) && one.AsSpan(8, 4).SequenceEqual(new byte[] { 170, 0, 85, 255 }));
            // BC3: alpha ends 255 and 0, every pixel index 1 (the second end).
            byte[] bc3 = [255, 0, 0b01001001, 0b10010010, 0b00100100, 0b01001001, 0b10010010, 0b00100100, .. bc1];
            Assert(TextureDecoding.Decode(TexBlocks(TextureDecoding.Bc3, 4, 4, bc3)).Rgba.Where((_, i) => i % 4 == 3).All(a => a == 0));
            // BC5: red 200, green 100.
            byte[] bc5 = [200, 100, 0, 0, 0, 0, 0, 0, 200, 100, 0b01001001, 0b10010010, 0b00100100, 0b01001001, 0b10010010, 0b00100100];
            var (five, _, _) = TextureDecoding.Decode(TexBlocks(TextureDecoding.Bc5, 4, 4, bc5));
            Assert(five[0] == 200 && five[1] == 100 && five[3] == 255);
            // BC7 mode 6, both ends equal: 7-bit ends plus their p-bit, so 0x40 → 0x81.
            var bits = new System.Collections.BitArray(128);
            var at = 0;
            void Put(int value, int count) { for (var i = 0; i < count; i++) bits[at++] = (value >> i & 1) == 1; }
            Put(1 << 6, 7);
            foreach (var v in new[] { 0x40, 0x40, 0x20, 0x20, 0x10, 0x10, 0x7F, 0x7F }) Put(v, 7);
            Put(1, 1); Put(1, 1);
            var bc7 = new byte[16]; bits.CopyTo(bc7, 0);
            var (seven, _, _) = TextureDecoding.Decode(TexBlocks(TextureDecoding.Bc7, 4, 4, bc7));
            Assert(seven.AsSpan(0, 4).SequenceEqual(new byte[] { 0x81, 0x41, 0x21, 0xFF }) && seven.AsSpan(60, 4).SequenceEqual(new byte[] { 0x81, 0x41, 0x21, 0xFF }));
            RejectData(() => TextureDecoding.Decode(TexBlocks(TextureDecoding.Bc7, 8, 8, bc7)));       // data runs short
        });

        await check("A character material's colour table gives each texel its tint and glow, by index texture or normal alpha", () =>
        {
            const uint Diffuse = 0x115306BE, Normal = 0x0C5EC1F1, Index = 0x565F8FD8;
            // Rows: 0 plain, 1 glowing orange, 2 red tint, 3 plain.
            var table = ColourTable(32, 32, (0, 1, 1, 1, 0, 0, 0), (1, 1, 1, 1, 1, 0.11f, 0), (2, 1, 0.25f, 0.25f, 0, 0, 0), (3, 1, 1, 1, 0, 0, 0));
            var textures = new Dictionary<string, byte[]>
            {
                // x 0: pair 0, green 0 (row 1, glowing); x 1: pair 1, green 255 (row 2, red); x 2: pair 0, green 255 (row 0).
                ["t/id.tex"] = TexPixels(3, 1, (x, y) => x == 0 ? (0, 0, 0, 255) : x == 1 ? (17, 255, 0, 255) : (0, 255, 0, 255)),
                ["t/d.tex"] = TexPixels(3, 1, (x, y) => (200, 200, 200, 255)),
            };
            var material = MtrlFull("characterlegacy.shpk", ["t/d.tex", "t/n.tex", "t/id.tex"], table, [(Diffuse, 0), (Normal, 1), (Index, 2)], []);
            var look = CharacterLook.Read(material, textures.GetValueOrDefault)!;
            Assert(look.Tints && look.Glows);
            Assert(look.At(0.1f, 0.5f).Glow.X == 1 && look.At(0.5f, 0.5f).Glow == System.Numerics.Vector3.Zero && look.At(0.5f, 0.5f).Tint.Y == 0.25f);
            Assert(look.At(1.1f, -0.5f).Glow.X == 1);                                                  // coordinates wrap
            var (tinted, w, _) = look.Tinted(textures["t/d.tex"]);
            Assert(w == 3 && tinted[4] == 200 && tinted[5] == 100 && tinted[8] == 200 && tinted[9] == 200);   // √0.25 = half
            // An older material: 16 rows, chosen by the normal map's alpha (row = alpha·15/255).
            var legacy = MtrlFull("character.shpk", ["t/d.tex", "t/n.tex"], ColourTable(16, 16, (0, 1, 1, 1, 0, 0, 0), (15, 1, 1, 1, 0, 1, 0)), [(Diffuse, 0), (Normal, 1)], []);
            textures["t/n.tex"] = TexPixels(2, 1, (x, y) => (128, 128, 255, x == 0 ? 0 : 255));
            var old = CharacterLook.Read(legacy, textures.GetValueOrDefault)!;
            Assert(old.Glows && !old.Tints && old.At(0.25f, 0).Glow == System.Numerics.Vector3.Zero && old.At(0.75f, 0).Glow.Y == 1);
            // No table: nothing to read.
            Assert(CharacterLook.Read(MtrlFull("character.shpk", ["t/d.tex"], null, [(Diffuse, 0)], []), textures.GetValueOrDefault) == null);
        });

        await check("An older table's shine becomes a scenery specular map: dark glossy texels turn metal, bright ones stay paint", () =>
        {
            const uint Diffuse = 0x115306BE, Normal = 0x0C5EC1F1;
            // Every row: white specular at full strength, gloss 20 (the [FFZ] chair's); the normal map's alpha picks row 15.
            var table = new byte[512];
            for (var r = 0; r < 16; r++)
            {
                float[] v = [1, 1, 1, 1, 1, 1, 1, 20];
                for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteHalfLittleEndian(table.AsSpan((r * 16 + i) * 2), (Half)v[i]);
            }
            var textures = new Dictionary<string, byte[]>
            {
                ["t/d.tex"] = TexPixels(2, 1, (x, y) => x == 0 ? (34, 34, 34, 255) : (200, 0, 40, 255)),     // near black, bright red
                ["t/n.tex"] = TexPixels(1, 1, (x, y) => (128, 128, 255, 255)),
            };
            var look = CharacterLook.Read(MtrlFull("character.shpk", ["t/d.tex", "t/n.tex"], table, [(Diffuse, 0), (Normal, 1)], []), textures.GetValueOrDefault)!;
            Assert(look.Shines && !look.Tints && !look.Glows);
            var ((colour, w, _), specular) = look.Baked(textures["t/d.tex"]);
            Assert(w == 2 && specular != null);
            Assert(specular![2] == 255 && colour[0] == 255 && colour[1] == 255);                            // black under white shine: polished metal
            Assert(specular[6] == 0 && colour[4] == 200 && colour[5] == 0 && colour[6] == 40);              // red: glossy paint, unchanged
            Assert(specular[1] == specular[5] && Math.Abs(specular[1] - 140) <= 1 && specular[0] == 255 && specular[3] == 255);   // roughness from gloss 20
            // A Dawntrail table never shines this way (its shading is already physically based).
            var modern = CharacterLook.Read(MtrlFull("characterlegacy.shpk", ["t/d.tex", "t/n.tex"], ColourTable(32, 32, (0, 1, 1, 1, 0, 0, 0)), [(Diffuse, 0), (Normal, 1)], []), textures.GetValueOrDefault);
            Assert(modern is { Shines: false } && modern.Baked(textures["t/d.tex"]).Specular == null);
        });

        await check("A material constant's values are replaced in place; a missing or mismatched one is refused", () =>
        {
            var material = MtrlFull("bg.shpk", ["t/d.tex"], null, [(0x1E6FEF9C, 0)], [(0x8981D4D9, [9]), (0x38A64362, [0, 0, 0])]);
            var glowing = FurnitureFiles.WithConstant(material, 0x38A64362, 1, 0.5f, 0)!;
            Assert(glowing.Length == material.Length && FurnitureFiles.WithConstant(glowing, 0x38A64362, 1, 0.5f, 0)!.SequenceEqual(glowing));
            Assert(Encoding.ASCII.GetString(glowing).Contains("bg.shpk") && !glowing.SequenceEqual(material));
            Assert(FurnitureFiles.WithConstant(material, 0x12345678, 1) == null && FurnitureFiles.WithConstant(material, 0x38A64362, 1) == null);
            Assert(FurnitureFiles.MaterialSamplers(glowing).Single().Texture == "t/d.tex");
        });

        await check("A model's triangles move into a new mesh with its own material; vertices and the rest stay put", () =>
        {
            System.Numerics.Vector2[] uvs = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
            var model = MeshMdl("x.mtrl", uvs, [0, 1, 2, 1, 3, 2]);
            var before = ModelMeshes.Read(model).Single();
            Assert(before.Material == 0 && before.Indices.SequenceEqual([0, 1, 2, 1, 3, 2]) && before.Uvs[3] == new System.Numerics.Vector2(1, 1));
            var split = ModelMeshes.AddMeshes(model, ["bgcommon/hou/hv/abc/glow.mtrl"], [new(0, [false, true], 1)]);
            FurnitureFiles.Check("x.mdl", split);
            var after = ModelMeshes.Read(split);
            Assert(after.Count == 2 && after[0].Indices.SequenceEqual([0, 1, 2, 1, 1, 1]) && after[1].Material == 1 && after[1].Indices.SequenceEqual([1, 3, 2]));
            Assert(after[1].Uvs.SequenceEqual(uvs));
            Assert(FurnitureFiles.ModelMaterialSlots(split).SequenceEqual(["x.mtrl", "bgcommon/hou/hv/abc/glow.mtrl"]));
            uint Read(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
            Assert(split.AsSpan((int)Read(split, 0x10), 80).SequenceEqual(model.AsSpan((int)Read(model, 0x10), 80)));      // vertices unchanged
            Assert(Read(split, 0x34) == Read(model, 0x34) + 16 && BinaryPrimitives.ReadUInt16LittleEndian(split.AsSpan(0x0C)) == 2);
            // Nothing to move: the model comes back as it was.
            Assert(ModelMeshes.AddMeshes(model, [], [new(0, [false, false], 0)]).SequenceEqual(model) || ModelMeshes.Read(ModelMeshes.AddMeshes(model, [], [new(0, [false, false], 0)])).Count == 1);
            RejectOperation(() => ModelMeshes.AddMeshes(model, [], [new(0, [true, true], 3)]));               // no such material
        });

        await check("A minion prop glows where its colour table glows, has no detail texture, and is offered only for looks the mod changes", () =>
        {
            const string Minion = "chara/monster/m8119/obj/body/b0001/model/m8119b0001.mdl";
            const string Material = "chara/monster/m8119/obj/body/b0001/material/v0001/mt_m8119b0001_a.mtrl";
            const uint Diffuse = 0x115306BE, Index = 0x565F8FD8, Emissive = 0x38A64362, Detail = 0x8981D4D9;
            var template = MtrlFull("bg.shpk", ["bgcommon/x_d.tex", "bgcommon/x_n.tex", "bgcommon/x_s.tex"], null, [(0x1E6FEF9C, 0), (0xAAB4D9E9, 1), (0x1BBC2F12, 2)], [(Detail, [9]), (Emissive, [0, 0, 0])]);
            var game = new Dictionary<string, byte[]> { [FurniturePackages.PropMaterialTemplate] = template, ["bgcommon/texture/dummy_s.tex"] = Tex(4), ["bgcommon/texture/dummy_n.tex"] = Tex(4) };
            // The right half of the index texture glows (row 1).
            var table = ColourTable(32, 32, (0, 1, 1, 1, 0, 0, 0), (1, 1, 1, 1, 1, 0.11f, 0));
            using var mod = PenumbraMod.Open(Pmp(new()
            {
                ["default_mod.json"] = Json($"{{\"Files\":{{\"{Minion}\":\"m.mdl\",\"{Material}\":\"m.mtrl\",\"t/d.tex\":\"d.tex\",\"t/id.tex\":\"id.tex\"}}}}"),
                ["m.mdl"] = MeshMdl("/mt_m8119b0001_a.mtrl", [new(0.1f, 0.1f), new(0.2f, 0.1f), new(0.1f, 0.2f), new(0.9f, 0.1f), new(0.8f, 0.1f), new(0.9f, 0.2f)], [0, 1, 2, 3, 4, 5]),
                ["m.mtrl"] = MtrlFull("characterlegacy.shpk", ["t/d.tex", "t/id.tex"], table, [(Diffuse, 0), (Index, 1)], []),
                ["d.tex"] = Tex(4), ["id.tex"] = TexPixels(2, 2, (x, y) => x == 0 ? (255, 255, 0, 255) : (0, 0, 0, 255)),
            }));
            var (zip, manifest) = FurniturePackages.BuildProp(mod, Minion, game.GetValueOrDefault, "Pumpkin");
            var package = FurniturePackages.Read(zip);
            var model = package.Files[manifest.Models[Minion]];
            var meshes = ModelMeshes.Read(model);
            var slots = FurnitureFiles.ModelMaterialSlots(model);
            Assert(meshes.Count == 2 && meshes[0].Indices.Skip(3).Distinct().Count() == 1 && meshes[1].Indices.SequenceEqual([3, 4, 5]) && slots.Count == 2);
            Assert(FurnitureFiles.WithConstant(package.Files[slots[0]], Detail, 0)!.SequenceEqual(package.Files[slots[0]]));          // no detail texture
            Assert(FurnitureFiles.WithConstant(package.Files[slots[1]], Emissive, 1, 0.11f, 0) is { } lit && Close(lit, package.Files[slots[1]]));
            Assert(FurnitureFiles.WithConstant(package.Files[slots[0]], Emissive, 0, 0, 0)!.SequenceEqual(package.Files[slots[0]]));  // the rest doesn't glow
            // Only the looks whose material the mod changes: v0002's is the game's own here.
            const string Shared = "chara/monster/m8002/obj/body/b0001/model/m8002b0001.mdl";
            string Set(int n) => $"chara/monster/m8002/obj/body/b0001/material/v{n:0000}/mt_m8002b0001_a.mtrl";
            var vanilla = MtrlFull("characterlegacy.shpk", ["t/d.tex"], null, [(Diffuse, 0)], []);
            game[Set(2)] = vanilla;
            using var pumpkin = PenumbraMod.Open(Pmp(new()
            {
                ["default_mod.json"] = Json($"{{\"Files\":{{\"{Shared}\":\"m.mdl\",\"{Set(1)}\":\"one.mtrl\",\"{Set(2)}\":\"two.mtrl\"}}}}"),
                ["m.mdl"] = MeshMdl("/mt_m8002b0001_a.mtrl", [new(0, 0), new(1, 0), new(0, 1)], [0, 1, 2]),
                ["one.mtrl"] = MtrlFull("characterlegacy.shpk", ["t/other_d.tex"], null, [(Diffuse, 0)], []), ["two.mtrl"] = vanilla,
            }));
            Assert(FurniturePackages.ChangedMaterialSets(pumpkin, Shared, game.GetValueOrDefault, [1, 2]).SequenceEqual([1]));
            game[Set(1)] = MtrlFull("characterlegacy.shpk", ["t/other_d.tex"], null, [(Diffuse, 0)], []);
            Assert(FurniturePackages.ChangedMaterialSets(pumpkin, Shared, game.GetValueOrDefault, [1, 2]).SequenceEqual([1, 2]));   // none changed: all
        });

        await check("A furnishing with no base item is only a custom prop", () =>
        {
            var prop = new Haven.Core.Furnishing { Id = Guid.NewGuid(), ItemId = 0, Position = [0, 0, 0], CustomFurniture = new string('a', 64) };
            var house = new Haven.Core.House { Name = "Props", TerritoryId = 282, Stage = System.Text.Json.JsonDocument.Parse("{\"FormatVersion\":2,\"Objects\":{}}").RootElement.Clone(), Furnishings = [prop] };
            Haven.Core.HouseFormat.Validate(house);
            RejectData(() => Haven.Core.HouseFormat.Validate(house with { Furnishings = [prop with { CustomFurniture = null }] }));
        });

        await check("A downloaded furnishing is refused unless every file is its own, listed and well formed", () =>
        {
            using var mod = PenumbraMod.Open(Pmp(new() { ["default_mod.json"] = Json($"{{\"Files\":{{\"{TextureB}\":\"t.tex\"}}}}"), ["t.tex"] = Tex(8) }));
            var (zip, manifest) = FurniturePackages.Build(mod, 13, [Model], Game, "Chair");
            byte[] Edit(Action<ZipArchive> change)
            {
                var copy = new MemoryStream(); copy.Write(zip);
                using (var archive = new ZipArchive(copy, ZipArchiveMode.Update, leaveOpen: true)) change(archive);
                return copy.ToArray();
            }
            void Put(ZipArchive archive, string name, byte[] bytes) { archive.GetEntry(name)?.Delete(); using var s = archive.CreateEntry(name).Open(); s.Write(bytes); }
            RejectData(() => FurniturePackages.Read(Edit(a => Put(a, "bgcommon/hou/hv/other/x.tex", Tex(2)))));              // unlisted file
            RejectData(() => FurniturePackages.Read(Edit(a => Put(a, manifest.Files[0], [1, 2, 3]))));                        // damaged file
            RejectData(() => FurniturePackages.Read(Edit(a => Put(a, "manifest.json", Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(manifest with { Files = [.. manifest.Files, "chara/x.tex"] }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))))));
            RejectData(() => FurniturePackages.Read(Edit(a => a.GetEntry("manifest.json")!.Delete())));
            RejectData(() => FurnitureFiles.Check("x.avfx", [0]));
            RejectData(() => FurnitureFiles.Check("x.tex", Tex(4)[..40]));
        });
    }

    // A character model as the converter reads it: the file header with vertex and index data offsets, a string table,
    // the model header (one material, one bone), three levels of detail, the material name offset, then the data.
    // Version 6 adds one bone table: its offset and count, then its one bone index, padded to two.
    static byte[] CharaMdl(string bone, string material, int version = 5)
    {
        var tables = version == 6 ? 8 : 0;
        var strings = Encoding.ASCII.GetBytes(bone + '\0' + material + '\0');
        var tableSize = (strings.Length + 7) & ~7;                                    // padded, like the game's
        var stringsAt = 0x44 + 8;
        var header = stringsAt + tableSize;
        var lods = header + 0x38;
        var offsets = lods + 3 * 0x3C;
        // Material offset, bone name offset, an empty submesh bone map, no padding, four bounding boxes, one bone's box.
        var data = offsets + 8 + tables + 4 + 1 + 4 * 32 + 32;
        var b = new byte[data + 64];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x01000000u | (uint)version);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x0E), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x08), (uint)(data - 0x44));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x10), (uint)data);          // vertex data
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x1C), (uint)(data + 32));   // index data
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x28), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x34), 32);
        b[0x40] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x44), 2);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x48), tableSize);
        strings.CopyTo(b, stringsAt);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(header + 10), 1);              // materials
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(header + 12), 1);              // bones
        if (tables > 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(header + 14), 1);          // bone tables
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(offsets + 8 + 2), 1);      // one bone in it
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(lods + 52), (uint)data);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(lods + 56), (uint)(data + 32));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offsets), bone.Length + 1);     // the material's name
        for (var i = data; i < b.Length; i++) b[i] = (byte)(i * 3);
        return b;
    }
    // A material naming three textures, with its shader.
    static byte[] Mtrl3(string shader, string d, string n, string s)
    {
        var strings = Encoding.ASCII.GetBytes($"{d}\0{n}\0{s}\0{shader}\0"); var size = (strings.Length + 3) & ~3;
        var b = new byte[16 + 12 + size + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x01030000);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)b.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)(d.Length + n.Length + s.Length + 3));
        b[12] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), (ushort)(d.Length + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(24), (ushort)(d.Length + n.Length + 2));
        strings.CopyTo(b, 28);
        return b;
    }
    // A material naming its textures, with a sampler block after the strings: (sampler id, texture index) pairs.
    static byte[] MtrlSampled(string[] textures, params (uint Id, int Texture)[] samplers)
    {
        var strings = Encoding.ASCII.GetBytes(string.Join('\0', textures) + "\0characterlegacy.shpk\0"); var size = (strings.Length + 3) & ~3;
        var table = 16 + 4 * textures.Length;
        var b = new byte[table + size + 12 + samplers.Length * 12];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x01030000);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)b.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)textures.Sum(x => x.Length + 1));
        b[12] = (byte)textures.Length;
        for (int i = 0, at = 0; i < textures.Length; at += textures[i].Length + 1, i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16 + i * 4), (ushort)at);
        strings.CopyTo(b, table);
        var block = table + size;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(block + 6), (ushort)samplers.Length);
        for (var i = 0; i < samplers.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(block + 12 + i * 12), samplers[i].Id);
            b[block + 12 + i * 12 + 8] = (byte)samplers[i].Texture;
        }
        return b;
    }
    // Two materials equal but for float rounding in their values.
    static bool Close(byte[] a, byte[] b) => a.Length == b.Length && a.Zip(b).Count(p => p.First != p.Second) <= 12;

    // A material with textures, an optional colour table (data set), samplers and shader constants, laid out like the
    // game's: header, texture offsets, strings, colour table, then the shader block (sizes and counts, keys, constants,
    // samplers, values).
    static byte[] MtrlFull(string shader, string[] textures, byte[]? table, (uint Id, int Texture)[] samplers, (uint Id, float[] Values)[] constants)
    {
        var strings = Encoding.ASCII.GetBytes(string.Join('\0', textures) + "\0" + shader + "\0"); var size = (strings.Length + 3) & ~3;
        var head = 16 + 4 * textures.Length;
        var values = constants.SelectMany(c => c.Values).ToArray();
        var block = head + size + (table?.Length ?? 0);
        var b = new byte[block + 12 + constants.Length * 8 + samplers.Length * 12 + values.Length * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x01030000);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)b.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)(table?.Length ?? 0));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)textures.Sum(x => x.Length + 1));
        b[12] = (byte)textures.Length;
        for (int i = 0, at = 0; i < textures.Length; at += textures[i].Length + 1, i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16 + i * 4), (ushort)at);
        strings.CopyTo(b, head);
        table?.CopyTo(b, head + size);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(block), (ushort)(values.Length * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(block + 4), (ushort)constants.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(block + 6), (ushort)samplers.Length);
        var c0 = block + 12; var offset = 0;
        for (var i = 0; i < constants.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(c0 + i * 8), constants[i].Id);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(c0 + i * 8 + 4), (ushort)offset);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(c0 + i * 8 + 6), (ushort)(constants[i].Values.Length * 4));
            offset += constants[i].Values.Length * 4;
        }
        var s0 = c0 + constants.Length * 8;
        for (var i = 0; i < samplers.Length; i++) { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(s0 + i * 12), samplers[i].Id); b[s0 + i * 12 + 8] = (byte)samplers[i].Texture; }
        var v0 = s0 + samplers.Length * 12;
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(v0 + i * 4), values[i]);
        return b;
    }
    // A colour table of rows × halves per row, with (row, diffuse r g b, emissive r g b) set and the rest zero.
    static byte[] ColourTable(int rows, int halves, params (int Row, float R, float G, float B, float Er, float Eg, float Eb)[] set)
    {
        var b = new byte[rows * halves * 2 + (rows == 32 ? 128 : 0)];
        foreach (var r in set)
        {
            float[] v = [r.R, r.G, r.B, 0, 0, 0, 0, 0, r.Er, r.Eg, r.Eb];
            for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteHalfLittleEndian(b.AsSpan((r.Row * halves + i) * 2), (Half)v[i]);
        }
        return b;
    }
    // An uncompressed texture from RGBA pixels (stored B, G, R, A).
    static byte[] TexPixels(int w, int h, Func<int, int, (int R, int G, int B, int A)> pixel)
    {
        var b = new byte[80 + w * h * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0x1450);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)h);
        b[14] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 80);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, bl, a) = pixel(x, y);
                var at = 80 + (y * w + x) * 4;
                (b[at], b[at + 1], b[at + 2], b[at + 3]) = ((byte)bl, (byte)g, (byte)r, (byte)a);
            }
        return b;
    }
    // A block-compressed texture of one mip.
    static byte[] TexBlocks(uint format, int w, int h, byte[] blocks)
    {
        var b = new byte[80 + blocks.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), format);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)h);
        b[14] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 80);
        blocks.CopyTo(b, 80);
        return b;
    }
    // A model without a skeleton: one vertex declaration (position, then texture coordinates), one material, one level
    // of detail with one mesh and one submesh, then its vertices (20 bytes each) and 16-bit indices padded to 16 bytes.
    static byte[] MeshMdl(string material, System.Numerics.Vector2[] uvs, ushort[] indices)
    {
        var strings = Encoding.ASCII.GetBytes(material + '\0');
        var tableSize = (strings.Length + 7) & ~7;
        var stringsAt = 0x44 + 0x88 + 8;
        var header = stringsAt + tableSize;
        var lods = header + 0x38;
        var meshAt = lods + 3 * 0x3C;
        var submeshAt = meshAt + 0x24;
        var materialsAt = submeshAt + 0x10;
        var data = materialsAt + 4 + 4 + 1 + 4 * 32;
        var indexAt = data + uvs.Length * 20;
        var indexSize = (indices.Length * 2 + 15) & ~15;
        var b = new byte[indexAt + indexSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x01000005);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x04), 0x88);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x08), (uint)(data - 0x44 - 0x88));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x0C), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x0E), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x10), (uint)data);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x1C), (uint)indexAt);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x28), (uint)(indexAt - data));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x34), (uint)indexSize);
        b[0x40] = 1;
        byte[] declaration = [0, 0, 2, 0, 0, 0, 0, 0, 0, 12, 1, 4, 0, 0, 0, 0, 255];
        declaration.CopyTo(b, 0x44);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(stringsAt - 8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(stringsAt - 4), tableSize);
        strings.CopyTo(b, stringsAt);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(header + 4), 1);      // meshes
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(header + 8), 1);      // submeshes
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(header + 10), 1);     // materials
        b[header + 22] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(lods + 2), 1);
        foreach (var field in new[] { 12, 16, 24 }) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(lods + field), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(lods + 32), (uint)indexAt);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(lods + 44), (uint)(indexAt - data));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(lods + 48), (uint)indexSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(lods + 52), (uint)data);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(lods + 56), (uint)indexAt);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(meshAt), (ushort)uvs.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(meshAt + 4), (uint)indices.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(meshAt + 12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(meshAt + 14), 255);
        b[meshAt + 32] = 20; b[meshAt + 35] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(submeshAt + 4), (uint)indices.Length);
        for (var v = 0; v < uvs.Length; v++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(data + v * 20), v);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(data + v * 20 + 12), uvs[v].X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(data + v * 20 + 16), uvs[v].Y);
        }
        for (var i = 0; i < indices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(indexAt + i * 2), indices[i]);
        return b;
    }
    static byte[] Repack(byte[] zip, Func<FurnitureManifest, FurnitureManifest> change)
    {
        var copy = new MemoryStream(); copy.Write(zip);
        using (var archive = new ZipArchive(copy, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry("manifest.json")!;
            FurnitureManifest manifest;
            using (var s = entry.Open()) manifest = System.Text.Json.JsonSerializer.Deserialize<FurnitureManifest>(s, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            entry.Delete();
            using var w = archive.CreateEntry("manifest.json").Open();
            w.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(change(manifest), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        }
        return copy.ToArray();
    }

    // A furniture model with the given materials and no geometry: the header, no vertex declarations, the string table.
    static byte[] Mdl(params string[] materials)
    {
        var strings = Encoding.ASCII.GetBytes(string.Join('\0', materials) + '\0');
        var b = new byte[0x44 + 8 + strings.Length + 16];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x0E), (ushort)materials.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x44), (ushort)materials.Length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x48), strings.Length);
        strings.CopyTo(b, 0x4C);
        return b;
    }
    // A material naming one texture.
    static byte[] Mtrl(string texture)
    {
        var strings = Encoding.ASCII.GetBytes(texture + "\0bg.shpk\0"); var size = (strings.Length + 3) & ~3;
        var b = new byte[16 + 4 + size + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x01030000);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)b.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)(texture.Length + 1));
        b[12] = 1;
        strings.CopyTo(b, 20);
        return b;
    }
    // A texture of side n, one mip.
    static byte[] Tex(int side)
    {
        var b = new byte[80 + side * side * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0x1450);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)side);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)side);
        b[14] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 80);
        for (var i = 80; i < b.Length; i++) b[i] = (byte)(i * 7);
        return b;
    }
    static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);
    static byte[] Pmp(Dictionary<string, byte[]> files)
    {
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in files) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
        return output.ToArray();
    }
    static void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
    static void RejectData(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected the file to be refused.");
    }
    static void RejectOperation(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected the import to be refused.");
    }
}
