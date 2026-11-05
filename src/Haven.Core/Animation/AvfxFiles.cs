using System.Buffers.Binary;
using System.Text;

namespace Haven.Core.Animation;

/// <summary>
/// Effect files (.avfx), as installed ones are laid out (read from the game's Cheer emotes' glowsticks): one "AVFX"
/// block (its four-letter name stored backwards, then its size) whose children are blocks of the same form, each
/// padded to four bytes. Models are blocks inside the file ("Modl"); textures are named by path, one "Tex" block each
/// (the path and a NUL), and the effect's parts refer to them by their order. So a texture can be pointed elsewhere by
/// rewriting its block alone. The glowsticks of Cheer On are such a file: three models (the sticks) and three textures,
/// started by the animation's timeline (C012), which binds them to the hands.
/// </summary>
public static class AvfxFiles
{
    public const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxBlocks = 4096;
    private static readonly byte[] Root = "XFVA"u8.ToArray();
    private static readonly byte[] Texture = "xeT\0"u8.ToArray();

    private static InvalidDataException Bad(string why) => new($"An effect file is not valid ({why}).");

    /// <summary>Checks an effect file's structure (the root block and every child lying exactly inside it) before the game parses it.</summary>
    public static void Check(ReadOnlySpan<byte> avfx) => Children(avfx);

    // The root's children: each one's start (its name) and size (its data, unpadded).
    private static List<(int Start, int Size)> Children(ReadOnlySpan<byte> avfx)
    {
        if (avfx.Length < 16 || avfx.Length > MaxBytes || !avfx[..4].SequenceEqual(Root)) throw Bad("not an effect file");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(avfx[4..]);
        if (size != avfx.Length - 8) throw Bad("size");
        var children = new List<(int, int)>();
        var at = 8;
        while (at < avfx.Length)
        {
            if (at + 8 > avfx.Length || children.Count >= MaxBlocks) throw Bad("block");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(avfx[(at + 4)..]);
            if (length > (uint)(avfx.Length - at - 8) || !avfx.Slice(at, 4).ToArray().All(c => c == 0 || c is >= (byte)'0' and <= (byte)'z')) throw Bad("block");
            children.Add((at, (int)length));
            at += 8 + (int)((length + 3) & ~3u);
        }
        if (at != avfx.Length) throw Bad("blocks");
        return children;
    }

    /// <summary>The texture paths an effect names, in order (its parts refer to them by this order).</summary>
    public static IReadOnlyList<string> Textures(ReadOnlySpan<byte> avfx)
    {
        var textures = new List<string>();
        foreach (var (start, size) in Children(avfx))
            if (avfx.Slice(start, 4).SequenceEqual(Texture)) textures.Add(Path(avfx.Slice(start + 8, size)));
        return textures;
    }

    // A texture block's path: printable, ending at a NUL.
    private static string Path(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end <= 0 || end > 255 || data[..end].ToArray().Any(c => c is < 0x20 or > 0x7E)) throw Bad("texture path");
        return Encoding.ASCII.GetString(data[..end]);
    }

    /// <summary>The file with its textures pointed elsewhere: map gives a new path, or null to keep one. Checked again.</summary>
    public static byte[] RepointTextures(ReadOnlySpan<byte> avfx, Func<string, string?> map)
    {
        var output = new MemoryStream(avfx.Length + 256);
        output.Write(avfx[..8]);
        foreach (var (start, size) in Children(avfx))
        {
            var block = avfx.Slice(start, 8 + size);
            if (avfx.Slice(start, 4).SequenceEqual(Texture) && map(Path(avfx.Slice(start + 8, size))) is { } next)
            {
                if (next.Length is 0 or > 255 || next.Any(c => c is < ' ' or > '~')) throw new ArgumentException("A texture path is not valid.");
                var text = Encoding.ASCII.GetBytes(next + "\0");
                Span<byte> head = stackalloc byte[8];
                Texture.CopyTo(head);
                BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)text.Length);
                output.Write(head); output.Write(text);
                while (output.Length % 4 != 0) output.WriteByte(0);
                continue;
            }
            output.Write(block);
            while (output.Length % 4 != 0) output.WriteByte(0);
        }
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        Check(result);
        return result;
    }

    /// <summary>
    /// Checks a texture file for an effect (.atex, the game's texture format): a header and every mip it claims, as
    /// furniture and prop textures are checked (a short file claiming a large size would be read past its end).
    /// </summary>
    public static void CheckTexture(ReadOnlySpan<byte> atex)
    {
        if (atex.Length < 0x50 || atex.Length > MaxBytes) throw new InvalidDataException("An effect texture is not valid.");
        try { Furniture.TextureDecoding.CheckMips(atex.ToArray(), 8192); }
        catch (InvalidDataException) { throw new InvalidDataException("An effect texture is not valid."); }
    }
}
