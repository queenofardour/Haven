using System.Buffers.Binary;
using System.IO.Compression;
using Haven.Core;
using Haven.Core.Furniture;
using Haven.Core.Music;
using Haven.Core.Odyssey;

// Checks for what the overnight review of 30-31 October 2026 found and fixed.
internal static class ReviewTests
{
    public static async Task Run(Func<string, Action, Task> check, House sample)
    {
        await check("Game data paths name their expansion, and a missing one is named in the refusal (free trials lack Endwalker and Dawntrail)", () =>
        {
            Assert(GameDataPaths.Expansion("bg/ex5/02_ykt_y6/twn/y6ti/level/y6ti.lvb") == 5);
            Assert(GameDataPaths.Expansion("music/ex4/BGM_EX4_Field_01.scd") == 4);
            Assert(GameDataPaths.Expansion("bg/ffxiv/roc_r1/ind/r1i1/level/r1i1.lvb") == 0);
            Assert(GameDataPaths.Expansion("music/ffxiv/Orchestrion/BGM_ORCH_769.scd") == 0);
            Assert(GameDataPaths.Expansion("bgcommon/hou/indoor/general/0001/bgparts/fun_b0_m0001.mdl") == 0);
            Assert(GameDataPaths.Expansion("ex5") == 0 && GameDataPaths.Expansion("") == 0 && GameDataPaths.Expansion("bg/exx/a") == 0);
            Assert(GameDataPaths.TerritoryFile("ex5/02_ykt_y6/twn/y6ti/level/y6ti") == "bg/ex5/02_ykt_y6/twn/y6ti/level/y6ti.lvb");
            // A trial install: through Shadowbringers.
            bool Trial(string path) => GameDataPaths.Expansion(path) <= 3;
            Assert(GameDataPaths.Needs(["bg/ex3/01_nvt_n4/twn/n4ti/level/n4ti.lvb", "music/ffxiv/a.scd"], Trial) == null);
            Assert(GameDataPaths.Needs(["bg/ex4/03_kld_k5/twn/k5ti/level/k5ti.lvb", "bg/ex5/02_ykt_y6/twn/y6ti/level/y6ti.lvb", "bg/ffxiv/x.lvb"], Trial) == 5);
            var message = GameDataPaths.Missing("The For'ard Cabins", 5);
            Assert(message.Contains("The For'ard Cabins") && message.Contains("Dawntrail") && message.Contains("free trial"));
            // A guest who leaves for that says which expansion; the host is told who and what.
            Assert(GameDataPaths.LeaveReason(5) is [5] && GameDataPaths.LeaveReason(0).Length == 0 && GameDataPaths.LeaveReason(99).Length == 0);
            Assert(GameDataPaths.LeaveExpansion([5]) == 5 && GameDataPaths.LeaveExpansion([]) == 0 && GameDataPaths.LeaveExpansion([5, 1]) == 0 && GameDataPaths.LeaveExpansion([200]) == 0);
            var told = GameDataPaths.GuestMissing("Aria Venn", 4);
            Assert(told.StartsWith("Aria Venn tried to join your gathering") && told.Contains("needs Endwalker") && GameDataPaths.GuestMissing("", 5).StartsWith("Someone"));
        });
        await check("A texture must hold the mips its header claims: a short file claiming a large size is refused", () =>
        {
            static byte[] Texture(uint format, int side, int mips, int dataBytes)
            {
                var tex = new byte[80 + dataBytes];
                BinaryPrimitives.WriteUInt32LittleEndian(tex.AsSpan(4), format);
                BinaryPrimitives.WriteUInt16LittleEndian(tex.AsSpan(8), (ushort)side);
                BinaryPrimitives.WriteUInt16LittleEndian(tex.AsSpan(10), (ushort)side);
                tex[14] = (byte)mips;
                var at = 80u;
                for (var i = 0; i < mips; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(tex.AsSpan(28 + i * 4), Math.Min(at, (uint)tex.Length - 1));
                    at += (uint)(TextureDecoding.MipBytes(format, side >> i, side >> i) ?? 1);
                }
                return tex;
            }
            // Whole: 8×8 BC1 with its 4×4, 2×2 and 1×1 mips (each at least one block) is 32 + 8 + 8 + 8 bytes.
            TextureDecoding.CheckMips(Texture(TextureDecoding.Bc1, 8, 4, 56), 8192);
            TextureDecoding.CheckMips(Texture(0x1450, 16, 1, 16 * 16 * 4), 8192);
            FurnitureFiles.Check("a.tex", Texture(TextureDecoding.Bc7, 8, 1, 4 * 16));
            void Refused(byte[] tex) { try { TextureDecoding.CheckMips(tex, 8192); throw new Exception("A short texture was accepted."); } catch (InvalidDataException) { } }
            Refused(Texture(TextureDecoding.Bc1, 8192, 1, 20));                 // claims 32 MB, holds 20 bytes
            Refused(Texture(0x1450, 64, 1, 64 * 63 * 4));                        // one row short
            Refused(Texture(TextureDecoding.Bc1, 8, 4, 48));                     // the last mip runs past the end
            Assert(TextureDecoding.MipBytes(0x9999, 64, 64) == null);
            Refused(Texture(0x9999, 64, 1, 4));                                  // an unlisted format can't be checked
            var cube = Texture(0x1450, 4, 1, 4 * 4 * 4 * 6);                     // a cube needs six faces
            BinaryPrimitives.WriteUInt32LittleEndian(cube, 0x2000000);
            TextureDecoding.CheckMips(cube, 8192);
            Refused(cube[..^1]);
            var volume = Texture(0x1450, 4, 1, 4 * 4 * 4);                       // a 3D texture of 64 slices in one slice's bytes
            BinaryPrimitives.WriteUInt32LittleEndian(volume, 0x1000000); BinaryPrimitives.WriteUInt16LittleEndian(volume.AsSpan(12), 64);
            Refused(volume);
            Assert(TextureDecoding.MipBytes(TextureDecoding.Bc1, 1, 1) == 8 && TextureDecoding.MipBytes(TextureDecoding.Bc5, 5, 5) == 64);
        });

        await check("NPC looks the game can't draw are kept out of the game but still open, and the check isn't written to the home", () =>
        {
            byte[] Body(byte race, byte sex, byte clan) { var c = new byte[NpcLook.CustomizeBytes]; c[0] = race; c[1] = sex; c[2] = 1; c[4] = clan; return c; }
            Assert(new NpcLook { Customize = Body(1, 0, 1) }.Drawable && new NpcLook { Customize = Body(8, 1, 16) }.Drawable && new NpcLook { Customize = Body(4, 1, 7) }.Drawable);
            Assert(!new NpcLook { Customize = Body(0, 0, 1) }.Drawable && !new NpcLook { Customize = Body(9, 0, 17) }.Drawable);
            Assert(!new NpcLook { Customize = Body(1, 2, 1) }.Drawable && !new NpcLook { Customize = Body(1, 0, 3) }.Drawable);
            var odd = new HomeNpc { Name = "Odd", Position = [0, 0, 0], Look = new NpcLook { Customize = Body(42, 7, 99) } };
            var text = System.Text.Encoding.UTF8.GetString(HouseFormat.Serialize(sample with { Npcs = [odd] }));
            Assert(!text.Contains("drawable", StringComparison.OrdinalIgnoreCase));
            Assert(HouseFormat.Parse(System.Text.Encoding.UTF8.GetBytes(text)).Npcs is [{ Look.Drawable: false }]);
        });

        await check("Angles from a shared home must be finite and sane: a huge rotation is refused, a few turns are kept", () =>
        {
            var piece = new Furnishing { ItemId = 6500 };
            HouseFormat.Validate(sample with { Furnishings = [piece with { Rotation = 720 }] });
            HouseFormat.Validate(sample with { Furnishings = [piece with { Rotation = -1080 }] });
            void Refused(House house) { try { HouseFormat.Validate(house); throw new Exception("A huge angle was accepted."); } catch (InvalidDataException) { } }
            Refused(sample with { Furnishings = [piece with { Rotation = 1e37f }] });
            Refused(sample with { Furnishings = [piece with { Rotation = float.NaN }] });
            Refused(sample with { Npcs = [new HomeNpc { Name = "Spin", Position = [0, 0, 0], Yaw = -3e30f, Look = new() }] });
        });

        await check("A Glamourer design from a shared home inflates to at most 1 MB", () =>
        {
            using var packed = new MemoryStream();
            packed.WriteByte(6);
            using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var zeros = new byte[1024 * 1024];
                gzip.WriteByte((byte)'{');
                for (var i = 0; i < 3; i++) gzip.Write(zeros);
            }
            var bomb = Convert.ToBase64String(packed.ToArray());
            Assert(bomb.Length < 64 * 1024 && GlamourerStates.Decode(bomb) == null);
            var state = new System.Text.Json.Nodes.JsonObject { ["FileVersion"] = 1 };
            Assert(GlamourerStates.Decode(GlamourerStates.Encode(state))?["FileVersion"]?.GetValue<int>() == 1);
        });

        await check("Downloaded songs are held to the upload rules before the orchestrion plays them", () =>
        {
            // One Ogg page beginning its stream (Playable also wants a single stream of whole pages).
            var ogg = new byte[27]; "OggS"u8.CopyTo(ogg); ogg[5] = 0x02;
            Assert(MusicEncoder.Playable(new PreparedSong(ogg, 180_000, 2, 48000)) && MusicEncoder.Playable(new PreparedSong(ogg, 60_000, 1, 44100)));
            Assert(!MusicEncoder.Playable(new PreparedSong(ogg, 180_000, 6, 48000)) && !MusicEncoder.Playable(new PreparedSong(ogg, 180_000, 2, 1)));
            Assert(!MusicEncoder.Playable(new PreparedSong(ogg, MusicEncoder.MaxMilliseconds + 1, 2, 48000)) && !MusicEncoder.Playable(new PreparedSong(new byte[MusicEncoder.MaxBytes + 1], 1000, 2, 48000)));
        });

        await check("Odyssey: a card taken back out of the deck gives its Potion back", () =>
        {
            var o = new Odyssey(OdysseyTests.Synthetic(), (_, _) => new OdysseyTests.PlainBrain());
            o.NewRun(31); o.Begin(); o.SkipReward(); o.EnterPath(); o.ChoosePath(0);
            foreach (var card in o.Binder().OrderByDescending(c => c.Power)) if (o.Run!.Prep.Deck.Count < 5 && !o.Run.Prep.Deck.Contains(card.Uid) && o.DeckRefusal(card.Uid) == null) o.ToggleDeckCard(card.Uid);
            var run = o.Run!;
            run.Items.Clear(); run.Items.Add("potion");
            var target = run.Prep.Deck[0];
            o.UseItem(0, target);
            Assert(run.Items.Count == 0 && run.Prep.Potions.Contains(target) && run.Prep.Used.Contains("potion"));
            o.ToggleDeckCard(target);
            Assert(run.Items.SequenceEqual(["potion"]) && !run.Prep.Potions.Contains(target) && !run.Prep.Used.Contains("potion"));
        });
    }
    private static void Assert(bool value, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) { if (!value) throw new Exception($"Review assertion failed (ReviewTests.cs line {line})."); }
}
