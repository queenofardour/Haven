using System.Buffers.Binary;
using System.Text;
using Haven.Core.Furniture;
using Haven.Core.World;

/// <summary>World effects' thumbnails: the one-channel textures effects use, and the effect renderer on files it must survive.</summary>
internal static class EffectTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("One-channel textures read: L8 as grey, A8 as white with that alpha", () =>
        {
            var l8 = Texture(TextureDecoding.L8, 2, 1, [10, 200]);
            var a8 = Texture(TextureDecoding.A8, 2, 1, [0, 128]);
            Assert(TextureDecoding.Readable(l8) && TextureDecoding.Readable(a8));
            var (grey, w, h) = TextureDecoding.Decode(l8);
            Assert(w == 2 && h == 1 && grey.SequenceEqual(new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 }));
            Assert(TextureDecoding.Decode(a8).Rgba.SequenceEqual(new byte[] { 255, 255, 255, 0, 255, 255, 255, 128 }));
            TextureDecoding.CheckMips(a8, 8192);
            // A texture claiming more pixels than it holds is refused, as the other formats are.
            var shortOne = Texture(TextureDecoding.A8, 4, 4, [1, 2, 3]);
            Assert(Throws(() => TextureDecoding.Decode(shortOne)));
        });

        await check("An effect thumbnail draws a small effect: a glowing quad, tinted by its colour", () =>
        {
            var files = Files(Effect());
            var picture = EffectThumbnails.Render("bgcommon/vfx/eff/test.avfx", p => files.GetValueOrDefault(p));
            Assert(picture is { Length: EffectThumbnails.Size * EffectThumbnails.Size * 4 });
            var centre = (EffectThumbnails.Size / 2 * EffectThumbnails.Size + EffectThumbnails.Size / 2) * 4;
            Assert(picture![centre + 3] > 200 && picture[centre] > picture[centre + 2]);       // lit, and warmer than blue
            Assert(picture[3] == 0);                                                              // the corner stays clear
            // The colour it's placed with tints it; a blue tint turns it blue.
            var blue = EffectThumbnails.Render("bgcommon/vfx/eff/test.avfx", p => files.GetValueOrDefault(p), [0, 0, 1, 1]);
            Assert(blue != null && blue[centre + 2] > blue[centre]);
            // The same file draws the same picture every time (random spreads are seeded by the path).
            Assert(EffectThumbnails.Render("bgcommon/vfx/eff/test.avfx", p => files.GetValueOrDefault(p))!.SequenceEqual(picture));
            // Nothing to draw without its texture.
            files.Remove("bgcommon/vfx/texture/glow.atex");
            Assert(EffectThumbnails.Render("bgcommon/vfx/eff/test.avfx", p => files.GetValueOrDefault(p)) == null);
        });

        await check("An effect thumbnail returns nothing, never throws, on broken or hostile files", () =>
        {
            var good = Effect();
            var random = new Random(5);
            List<byte[]> cases = [Array.Empty<byte>(), new byte[] { 1, 2, 3 }, new byte[64], Encoding.ASCII.GetBytes("XFVA\xFF\xFF\xFF\xFF")];
            var noise = new byte[4096]; random.NextBytes(noise); cases.Add(noise);
            var lying = (byte[])good.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(lying.AsSpan(4), uint.MaxValue); cases.Add(lying);
            for (var cut = 0; cut < good.Length; cut += 7) cases.Add(good[..cut]);
            for (var i = 0; i < 40; i++) { var bent = (byte[])good.Clone(); for (var k = 0; k < 6; k++) bent[random.Next(8, bent.Length)] = (byte)random.Next(256); cases.Add(bent); }
            // Blocks nested far deeper than any effect, and a file of a great many tiny blocks.
            var deep = Block("Data", []);
            for (var i = 0; i < 2000; i++) deep = Block("Emit", deep);
            cases.Add(Block("AVFX", deep));
            cases.Add(Block("AVFX", [.. Enumerable.Range(0, 300_000).SelectMany(_ => Block("Ptcl", []))]));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var file in cases)
            {
                var textures = Files(good);
                var picture = EffectThumbnails.Render("bgcommon/vfx/eff/x.avfx", p => p.EndsWith(".avfx") ? file : textures.GetValueOrDefault(p));
                Assert(picture == null || picture.Length == EffectThumbnails.Size * EffectThumbnails.Size * 4);
            }
            Assert(EffectThumbnails.Render("bgcommon/vfx/eff/x.avfx", _ => throw new IOException("unreadable")) == null);
            Assert(EffectThumbnails.Render("bgcommon/vfx/eff/x.avfx", p => p.EndsWith(".avfx") ? good : Texture(TextureDecoding.A8, 8192, 8192, [])) == null);
            Assert(clock.Elapsed.TotalSeconds < 60);
            Assert(EffectThumbnails.Describe("bgcommon/vfx/eff/x.avfx", _ => noise).Length > 0);
        });
    }

    private static Dictionary<string, byte[]> Files(byte[] effect)
    {
        // A soft round glow, 8×8, in A8: white, its alpha falling off from the middle.
        var glow = new byte[64];
        for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++) glow[y * 8 + x] = (byte)Math.Clamp(255 - ((x - 3.5) * (x - 3.5) + (y - 3.5) * (y - 3.5)) * 18, 0, 255);
        return new(StringComparer.Ordinal) { ["bgcommon/vfx/eff/test.avfx"] = effect, ["bgcommon/vfx/texture/glow.atex"] = Texture(TextureDecoding.A8, 8, 8, glow) };
    }

    // An effect as the game lays one out: a texture, a point emitter its timeline starts, emitting one camera-facing quad
    // (2 m, added light, warm orange) with that texture.
    private static byte[] Effect()
    {
        static byte[] Int(string tag, int value) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, value); return Block(tag, b); }
        static byte[] Float(string tag, float value) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, value); return Block(tag, b); }
        static byte[] Curve(string tag, float x, float y, float z)
        {
            var key = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(2), 1);
            BinaryPrimitives.WriteSingleLittleEndian(key.AsSpan(4), x); BinaryPrimitives.WriteSingleLittleEndian(key.AsSpan(8), y); BinaryPrimitives.WriteSingleLittleEndian(key.AsSpan(12), z);
            return Block(tag, [.. Int("KeyC", 1), .. Block("Keys", key)]);
        }
        var emitter = Block("Emit", [.. Int("EVT", 0), .. Block("Life", Float("Val", -1)), .. Curve("CrC", 1, 1, 1), .. Curve("CrI", 1, 1, 30),
            .. Block("ItPr", [.. Int("bEnb", 1), .. Int("TgtB", 0), .. Int("CrCn", 1), .. Int("CrPr", 100)])]);
        var particle = Block("Ptcl", [.. Int("PrVT", 8), .. Int("RMT", 2), .. Block("Life", Float("Val", -1)),
            .. Block("Scl", [.. Int("ACT", 1), .. Int("ACTR", 0), .. Curve("X", 1, 1, 2)]),
            .. Block("Col", Curve("RGB", 1f, 0.5f, 0.2f)),
            .. Block("TC1", [.. Int("bEna", 1), .. Int("UvSN", 0), .. Int("TxNo", 0)])]);
        var timeline = Block("TmLn", [.. Int("TICn", 1), .. Block("Item", [.. Int("bEna", 1), .. Int("StTm", 0), .. Int("EdTm", -1), .. Int("EmNo", 0)])]);
        return Block("AVFX", [.. Int("Ver", 1), .. timeline, .. emitter, .. particle, .. Block("Tex", Encoding.ASCII.GetBytes("bgcommon/vfx/texture/glow.atex\0"))]);
    }

    // A block: its name backwards (padded with NULs), its size, its data padded to four bytes.
    private static byte[] Block(string tag, byte[] data)
    {
        var name = Encoding.ASCII.GetBytes(tag.PadLeft(4, '\0')).Reverse().ToArray();
        var block = new byte[8 + (data.Length + 3) / 4 * 4];
        name.CopyTo(block, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)data.Length);
        data.CopyTo(block, 8);
        return block;
    }

    private static byte[] Texture(uint format, int width, int height, byte[] pixels)
    {
        var tex = new byte[80 + pixels.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(tex.AsSpan(4), format);
        BinaryPrimitives.WriteUInt16LittleEndian(tex.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(tex.AsSpan(10), (ushort)height);
        tex[14] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(tex.AsSpan(28), 80);
        pixels.CopyTo(tex, 80);
        return tex;
    }

    private static bool Throws(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
    private static void Assert(bool value) { if (!value) throw new Exception("Effect assertion failed."); }
}
