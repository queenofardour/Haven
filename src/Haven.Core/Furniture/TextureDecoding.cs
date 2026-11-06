using System.Buffers.Binary;

namespace Haven.Core.Furniture;

/// <summary>
/// Reads the first mip of a game texture (.tex) into RGBA bytes: uncompressed B8G8R8A8, the one-channel L8 (grey) and A8
/// (white with that alpha, as effects' masks are), and the block formats character mods use (BC1, BC2, BC3, BC4, BC5,
/// BC7). Used to bake a character material's colour table into scenery textures, and for thumbnails.
/// </summary>
public static class TextureDecoding
{
    public const uint L8 = 0x1130, A8 = 0x1131, B8G8R8A8 = 0x1450, Bc1 = 0x3420, Bc2 = 0x3430, Bc3 = 0x3431, Bc4 = 0x6120, Bc5 = 0x6230, Bc7 = 0x6432;

    /// <summary>The texture's format, width and height, read from its header.</summary>
    public static (uint Format, int Width, int Height) Header(byte[] tex)
    {
        if (tex.Length < 80) throw new InvalidDataException("A texture file is too short.");
        return (BinaryPrimitives.ReadUInt32LittleEndian(tex.AsSpan(4)), BinaryPrimitives.ReadUInt16LittleEndian(tex.AsSpan(8)), BinaryPrimitives.ReadUInt16LittleEndian(tex.AsSpan(10)));
    }

    /// <summary>
    /// The bytes one mip of a texture takes in a format the game uses (a block format rounds up to whole 4×4 blocks), or
    /// null for a format not listed here.
    /// </summary>
    public static long? MipBytes(uint format, int width, int height)
    {
        long w = Math.Max(1, width), h = Math.Max(1, height), blocks = (w + 3) / 4 * ((h + 3) / 4);
        return format switch
        {
            0x1130 or 0x1131 => w * h,                                   // L8, A8
            0x1440 or 0x1441 or 0x1140 or 0x4140 or 0x5140 => w * h * 2, // B4G4R4A4, B5G5R5A1, R16, D16, Shadow16
            0x1450 or 0x1451 or 0x2150 or 0x2250 or 0x4250 or 0x5150 => w * h * 4, // B8G8R8A8, B8G8R8X8, R32F, R16G16F, D24S8, Shadow24
            0x2260 or 0x2460 => w * h * 8,                               // R32G32F, R16G16B16A16F
            0x2470 => w * h * 16,                                        // R32G32B32A32F
            Bc1 or Bc4 => blocks * 8,
            Bc2 or Bc3 or Bc5 or Bc7 or 0x6330 => blocks * 16,          // and BC6H
            _ => null,
        };
    }

    private const uint Type3D = 0x1000000, TypeCube = 0x2000000, Type2DArray = 0x10000000;

    /// <summary>
    /// Refuses a texture whose mips run past the end of the file: the offsets alone being inside it let a short file
    /// claim a large size, and the game would read past its end. Each mip from its offset must fit whole, with every
    /// slice a 3D, cube or array texture's header claims, and only in a format whose size is known (the game sizes an
    /// unknown one by rules Haven can't check, so it isn't handed on).
    /// </summary>
    public static void CheckMips(byte[] tex, int maxSide)
    {
        if (tex.Length < 80) throw new InvalidDataException("A texture file is too short.");
        var (format, width, height) = Header(tex);
        var attribute = BinaryPrimitives.ReadUInt32LittleEndian(tex);
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(tex.AsSpan(12)), mips = tex[14] & 0x7F, array = tex[15];
        if (width is 0 || width > maxSide || height is 0 || height > maxSide || mips is 0 or > 13) throw new InvalidDataException("A texture's size is not one the game uses.");
        if ((attribute & Type3D) != 0 && depth is 0 or > 256 || (attribute & Type2DArray) != 0 && array is 0) throw new InvalidDataException("A texture's size is not one the game uses.");
        if (MipBytes(format, 1, 1) == null) throw new InvalidDataException($"A texture's format ({format:X}) is not one Haven can check.");
        long slices = ((attribute & Type2DArray) != 0 ? array : 1) * ((attribute & TypeCube) != 0 ? 6 : 1);
        for (var i = 0; i < mips; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tex.AsSpan(28 + i * 4));
            if (offset < 80 || offset >= tex.Length) throw new InvalidDataException("A texture's data runs past its end.");
            var layers = slices * ((attribute & Type3D) != 0 ? Math.Max(1, depth >> i) : 1);
            if (offset + MipBytes(format, width >> i, height >> i)!.Value * layers > tex.Length) throw new InvalidDataException("A texture's data runs past its end.");
        }
    }

    /// <summary>Whether <see cref="Decode"/> reads this texture.</summary>
    public static bool Readable(byte[] tex) => tex.Length >= 80 && Header(tex).Format is B8G8R8A8 or L8 or A8 or Bc1 or Bc2 or Bc3 or Bc4 or Bc5 or Bc7;

    /// <summary>
    /// The first mip as RGBA bytes, row by row; or, given <paramref name="maxSide"/>, the largest mip no wider or taller
    /// (a thumbnail needs only a small one: decoding less is quicker, and a small mip is already averaged).
    /// </summary>
    public static (byte[] Rgba, int Width, int Height) Decode(byte[] tex, int maxSide = int.MaxValue)
    {
        var (format, width, height) = Header(tex);
        if (width is 0 or > 8192 || height is 0 or > 8192) throw new InvalidDataException("A texture's size is not one the game uses.");
        var mips = Math.Clamp(tex[14] & 0x7F, 1, 13); var mip = 0;
        while (mip + 1 < mips && (width > maxSide || height > maxSide)) { mip++; width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); }
        var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(tex.AsSpan(28 + mip * 4));
        if (at < 80 || at >= tex.Length) throw new InvalidDataException("A texture's data runs past its end.");
        var rgba = new byte[width * height * 4];
        if (format == B8G8R8A8)
        {
            if (at + rgba.Length > tex.Length) throw new InvalidDataException("A texture's data runs past its end.");
            for (var i = 0; i < width * height; i++)
            {
                rgba[i * 4] = tex[at + i * 4 + 2]; rgba[i * 4 + 1] = tex[at + i * 4 + 1]; rgba[i * 4 + 2] = tex[at + i * 4]; rgba[i * 4 + 3] = tex[at + i * 4 + 3];
            }
            return (rgba, width, height);
        }
        if (format is L8 or A8)
        {
            if (at + (long)width * height > tex.Length) throw new InvalidDataException("A texture's data runs past its end.");
            for (var i = 0; i < width * height; i++)
            {
                var value = tex[at + i];
                if (format == L8) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = value; rgba[i * 4 + 3] = 255; }
                else { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = value; }
            }
            return (rgba, width, height);
        }
        var blockSize = format is Bc1 or Bc4 ? 8 : format is Bc2 or Bc3 or Bc5 or Bc7 ? 16 : throw new InvalidDataException($"Haven can't read texture format {format:X}.");
        int across = (width + 3) / 4, down = (height + 3) / 4;
        if (at + (long)across * down * blockSize > tex.Length) throw new InvalidDataException("A texture's data runs past its end.");
        Span<byte> block = stackalloc byte[64];
        for (var by = 0; by < down; by++)
            for (var bx = 0; bx < across; bx++)
            {
                var src = tex.AsSpan(at + (by * across + bx) * blockSize, blockSize);
                block.Clear();
                switch (format)
                {
                    case Bc1: Colour(src, block, true); break;
                    case Bc2: Colour(src[8..], block, false); for (var i = 0; i < 16; i++) block[i * 4 + 3] = (byte)(((src[i / 2] >> (i % 2 * 4)) & 15) * 17); break;
                    case Bc3: Colour(src[8..], block, false); Channel(src, block, 3); break;
                    case Bc4: Channel(src, block, 0); for (var i = 0; i < 16; i++) { block[i * 4 + 1] = block[i * 4 + 2] = block[i * 4]; block[i * 4 + 3] = 255; } break;
                    case Bc5: Channel(src, block, 0); Channel(src[8..], block, 1); for (var i = 0; i < 16; i++) block[i * 4 + 3] = 255; break;
                    case Bc7: Bc7Block(src, block); break;
                }
                for (var y = 0; y < 4; y++)
                {
                    var py = by * 4 + y;
                    if (py >= height) break;
                    for (var x = 0; x < 4; x++)
                    {
                        var px = bx * 4 + x;
                        if (px >= width) break;
                        block.Slice((y * 4 + x) * 4, 4).CopyTo(rgba.AsSpan((py * width + px) * 4));
                    }
                }
            }
        return (rgba, width, height);
    }

    // BC1 colour block: two RGB565 ends and 2-bit indices; three-colour mode with transparent black only for BC1.
    private static void Colour(ReadOnlySpan<byte> src, Span<byte> block, bool punchThrough)
    {
        int c0 = BinaryPrimitives.ReadUInt16LittleEndian(src), c1 = BinaryPrimitives.ReadUInt16LittleEndian(src[2..]);
        int[] r = new int[4], g = new int[4], b = new int[4], a = new int[4];
        void End(int i, int c) { r[i] = (c >> 11 & 31) * 255 / 31; g[i] = (c >> 5 & 63) * 255 / 63; b[i] = (c & 31) * 255 / 31; a[i] = 255; }
        End(0, c0); End(1, c1);
        if (c0 > c1 || !punchThrough)
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; b[2] = (2 * b[0] + b[1]) / 3; a[2] = 255;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; b[3] = (b[0] + 2 * b[1]) / 3; a[3] = 255;
        }
        else
        {
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; b[2] = (b[0] + b[1]) / 2; a[2] = 255;
            r[3] = g[3] = b[3] = a[3] = 0;
        }
        var bits = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
        for (var i = 0; i < 16; i++)
        {
            var k = (int)(bits >> (i * 2) & 3);
            block[i * 4] = (byte)r[k]; block[i * 4 + 1] = (byte)g[k]; block[i * 4 + 2] = (byte)b[k]; block[i * 4 + 3] = (byte)a[k];
        }
    }

    // BC3 alpha / BC4 / BC5 channel block: two 8-bit ends and 3-bit indices, into one channel of the block.
    private static void Channel(ReadOnlySpan<byte> src, Span<byte> block, int channel)
    {
        Span<int> v = stackalloc int[8];
        v[0] = src[0]; v[1] = src[1];
        if (v[0] > v[1]) for (var i = 1; i < 7; i++) v[i + 1] = ((7 - i) * v[0] + i * v[1]) / 7;
        else { for (var i = 1; i < 5; i++) v[i + 1] = ((5 - i) * v[0] + i * v[1]) / 5; v[6] = 0; v[7] = 255; }
        ulong bits = 0;
        for (var i = 0; i < 6; i++) bits |= (ulong)src[2 + i] << (8 * i);
        for (var i = 0; i < 16; i++) block[i * 4 + channel] = (byte)v[(int)(bits >> (i * 3) & 7)];
    }

    // BC7 ------------------------------------------------------------------------------------------------------------

    private static readonly byte[] Subsets = [3, 2, 3, 2, 1, 1, 1, 2];
    private static readonly byte[] PartitionBits = [4, 6, 6, 6, 0, 0, 0, 6];
    private static readonly byte[] RotationBits = [0, 0, 0, 0, 2, 2, 0, 0];
    private static readonly byte[] IndexModeBits = [0, 0, 0, 0, 1, 0, 0, 0];
    private static readonly byte[] ColourBits = [4, 6, 5, 7, 5, 7, 7, 5];
    private static readonly byte[] AlphaBits = [0, 0, 0, 0, 6, 8, 7, 5];
    private static readonly byte[] EndpointPBits = [1, 0, 0, 1, 0, 0, 1, 1];
    private static readonly byte[] SharedPBits = [0, 1, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] IndexBits = [3, 3, 2, 2, 2, 2, 4, 2];
    private static readonly byte[] SecondIndexBits = [0, 0, 0, 0, 3, 2, 0, 0];

    private static readonly int[] Weights2 = [0, 21, 43, 64];
    private static readonly int[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];
    private static readonly int[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    // Partition tables (64 shapes, 16 pixels each), as in the BC7 specification.
    private static readonly uint[] Partitions2 =
    [
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
        0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
        0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
    ];
    private static readonly string[] Partitions3 =
    [
        "0011001102212222", "0001001122112221", "0000200122112211", "0222002200110111", "0000000011221122", "0011001100220022", "0022002211111111", "0011001122112211",
        "0000000011112222", "0000111111112222", "0000111122222222", "0012001200120012", "0112011201120112", "0122012201220122", "0011011211221222", "0011200122002220",
        "0001001101121122", "0111001120012200", "0000112211221122", "0022002200221111", "0111011102220222", "0001000122212221", "0000001101220122", "0000110022102210",
        "0122012200110000", "0012001211222222", "0110122112210110", "0000011012211221", "0022110211020022", "0110011020022222", "0011012201220011", "0000200022112221",
        "0000000211221222", "0222002200120011", "0011001200220222", "0120012001200120", "0000111122220000", "0120120120120120", "0120201212010120", "0011220011220011",
        "0011112222000011", "0101010122222222", "0000000021212121", "0022112200221122", "0022001100220011", "0220122102201221", "0101222222220101", "0000212121212121",
        "0101010101012222", "0222011102220111", "0002111200021112", "0000211221122112", "0222011101110222", "0002111211120002", "0110011001102222", "0000000021122112",
        "0110011022222222", "0022001100110022", "0022112211220022", "0000000000002112", "0002000100020001", "0222122202221222", "0101222222222222", "0111201122012220",
    ];
    // Anchor (fix-up) pixels of the second subset of two-subset shapes, and of the second and third of three-subset shapes.
    private static readonly byte[] Anchor2 =
    [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    ];
    private static readonly byte[] Anchor3a =
    [
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
    ];
    private static readonly byte[] Anchor3b =
    [
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    ];

    private ref struct Bits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int at;
        public int Take(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++, at++) value |= (data[at >> 3] >> (at & 7) & 1) << i;
            return value;
        }
    }

    private static int Subset(int mode, int partition, int pixel) => Subsets[mode] switch
    {
        2 => (int)(Partitions2[partition] >> pixel & 1),
        3 => Partitions3[partition][pixel] - '0',
        _ => 0,
    };

    // An anchor pixel stores one index bit fewer: pixel 0, and the fix-up pixel of each later subset.
    private static bool IsAnchor(int mode, int partition, int pixel) => pixel == 0 || Subsets[mode] switch
    {
        2 => pixel == Anchor2[partition],
        3 => pixel == Anchor3a[partition] || pixel == Anchor3b[partition],
        _ => false,
    };

    private static int Interpolate(int a, int b, int index, int bits)
    {
        var w = bits == 2 ? Weights2[index] : bits == 3 ? Weights3[index] : Weights4[index];
        return ((64 - w) * a + w * b + 32) >> 6;
    }

    private static void Bc7Block(ReadOnlySpan<byte> src, Span<byte> block)
    {
        var mode = 0;
        while (mode < 8 && (src[0] >> mode & 1) == 0) mode++;
        if (mode == 8) return;     // reserved: transparent black
        var bits = new Bits(src);
        bits.Take(mode + 1);
        var partition = bits.Take(PartitionBits[mode]);
        var rotation = bits.Take(RotationBits[mode]);
        var indexMode = bits.Take(IndexModeBits[mode]);
        int subsets = Subsets[mode], ends = subsets * 2;
        Span<int> e = stackalloc int[6 * 4];        // endpoint, channel
        for (var c = 0; c < 3; c++)
            for (var i = 0; i < ends; i++) e[i * 4 + c] = bits.Take(ColourBits[mode]);
        for (var i = 0; i < ends; i++) e[i * 4 + 3] = AlphaBits[mode] > 0 ? bits.Take(AlphaBits[mode]) : 255;
        int colourBits = ColourBits[mode], alphaBits = AlphaBits[mode];
        if (EndpointPBits[mode] == 1)
        {
            for (var i = 0; i < ends; i++)
            {
                var p = bits.Take(1);
                for (var c = 0; c < 4; c++) if (c < 3 || alphaBits > 0) e[i * 4 + c] = e[i * 4 + c] << 1 | p;
            }
            colourBits++; if (alphaBits > 0) alphaBits++;
        }
        else if (SharedPBits[mode] == 1)
        {
            for (var s = 0; s < subsets; s++)
            {
                var p = bits.Take(1);
                for (var i = s * 2; i < s * 2 + 2; i++) for (var c = 0; c < 3; c++) e[i * 4 + c] = e[i * 4 + c] << 1 | p;
            }
            colourBits++;
        }
        for (var i = 0; i < ends; i++)
        {
            for (var c = 0; c < 3; c++) e[i * 4 + c] = Expand(e[i * 4 + c], colourBits);
            if (alphaBits > 0) e[i * 4 + 3] = Expand(e[i * 4 + 3], alphaBits);
        }
        Span<int> first = stackalloc int[16], second = stackalloc int[16];
        int firstBits = IndexBits[mode], secondBits = SecondIndexBits[mode];
        for (var i = 0; i < 16; i++) first[i] = bits.Take(IsAnchor(mode, partition, i) ? firstBits - 1 : firstBits);
        if (secondBits > 0) for (var i = 0; i < 16; i++) second[i] = bits.Take(i == 0 ? secondBits - 1 : secondBits);
        for (var i = 0; i < 16; i++)
        {
            var s = Subset(mode, partition, i);
            int a = s * 2, b = s * 2 + 1;
            int colourIndex = first[i], colourIndexBits = firstBits, alphaIndex = first[i], alphaIndexBits = firstBits;
            if (secondBits > 0)
            {
                if (indexMode == 0) { alphaIndex = second[i]; alphaIndexBits = secondBits; }
                else { colourIndex = second[i]; colourIndexBits = secondBits; }
            }
            int r = Interpolate(e[a * 4], e[b * 4], colourIndex, colourIndexBits), g = Interpolate(e[a * 4 + 1], e[b * 4 + 1], colourIndex, colourIndexBits),
                bl = Interpolate(e[a * 4 + 2], e[b * 4 + 2], colourIndex, colourIndexBits), al = Interpolate(e[a * 4 + 3], e[b * 4 + 3], alphaIndex, alphaIndexBits);
            switch (rotation)
            {
                case 1: (al, r) = (r, al); break;
                case 2: (al, g) = (g, al); break;
                case 3: (al, bl) = (bl, al); break;
            }
            block[i * 4] = (byte)r; block[i * 4 + 1] = (byte)g; block[i * 4 + 2] = (byte)bl; block[i * 4 + 3] = (byte)al;
        }
    }

    // An n-bit endpoint (n at least 5 in every mode) to 8 bits, its top bits repeated below.
    private static int Expand(int value, int bits) => bits >= 8 ? value : value << (8 - bits) | value >> (2 * bits - 8);
}
