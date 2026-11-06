using System.Buffers.Binary;
using System.Text;

namespace Haven.Core.Animation;

/// <summary>
/// An effect file's (.avfx) blocks as a tree, for reading what an effect draws (its emitters, particles, curves and
/// models). Every block is a four-letter name stored backwards, a size and its
/// data padded to four bytes; a block whose data is itself a run of such blocks is a container. Nothing is copied: a
/// node is a window on the file. Bounded for files Haven didn't make: the file's size (<see cref="AvfxFiles.MaxBytes"/>),
/// the depth and the number of blocks; a block that doesn't fit ends its list.
/// </summary>
internal sealed class AvfxNode
{
    public const int MaxDepth = 12, MaxNodes = 250_000;

    private readonly byte[] file;
    private readonly int start;
    public string Tag { get; }
    public int Length { get; }
    public List<AvfxNode>? Children { get; private set; }

    private AvfxNode(byte[] file, string tag, int start, int length) { this.file = file; Tag = tag; this.start = start; Length = length; }

    public ReadOnlySpan<byte> Data => file.AsSpan(start, Length);

    /// <summary>The block's value read as a number: four bytes little-endian, or one or two bytes; 0 when shorter.</summary>
    public int Int => Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(Data) : Length == 2 ? BinaryPrimitives.ReadInt16LittleEndian(Data) : Length == 1 ? file[start] : 0;
    public float Float => Length >= 4 ? BinaryPrimitives.ReadSingleLittleEndian(Data) : 0;

    /// <summary>The first child of this name, or null.</summary>
    public AvfxNode? this[string tag]
    {
        get
        {
            if (Children == null) return null;
            foreach (var child in Children) if (child.Tag == tag) return child;
            return null;
        }
    }

    public IEnumerable<AvfxNode> All(string tag)
    {
        if (Children == null) yield break;
        foreach (var child in Children) if (child.Tag == tag) yield return child;
    }

    // Blocks whose data is never a list of blocks, however it looks: curve keys, texture paths, model data, clips.
    private static readonly HashSet<string> Leaves = ["Keys", "Tex", "VDrw", "VIdx", "VNum", "VEmt", "VBN", "VBS", "VBP", "VBC", "Clip", "TLst"];

    /// <summary>The file's root block (its name "AVFX") with its tree, or null when it isn't an effect file.</summary>
    public static AvfxNode? Parse(byte[] avfx)
    {
        if (avfx.Length < 16 || avfx.Length > AvfxFiles.MaxBytes || NameAt(avfx, 0) != "AVFX") return null;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(avfx.AsSpan(4));
        var length = (int)Math.Min(size, (uint)(avfx.Length - 8));
        var root = new AvfxNode(avfx, "AVFX", 8, length);
        var budget = MaxNodes;
        root.Children = Read(avfx, 8, length, 0, ref budget);
        return root;
    }

    private static List<AvfxNode> Read(byte[] file, int from, int length, int depth, ref int budget)
    {
        var list = new List<AvfxNode>();
        int at = from, end = from + length;
        while (at + 8 <= end && budget > 0)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
            if (size > (uint)(end - at - 8)) break;
            var node = new AvfxNode(file, NameAt(file, at), at + 8, (int)size);
            budget--;
            list.Add(node);
            if (depth < MaxDepth && size >= 8 && !Leaves.Contains(node.Tag) && Plausible(file, at + 8, (int)size))
                node.Children = Read(file, at + 8, (int)size, depth + 1, ref budget);
            at += 8 + (int)((size + 3) & ~3u);
        }
        return list;
    }

    // Whether data is exactly a run of blocks with names of the letters the format uses.
    private static bool Plausible(byte[] file, int from, int length)
    {
        int at = from, end = from + length, count = 0;
        while (at < end)
        {
            if (at + 8 > end || ++count > 100_000) return false;
            for (var i = 0; i < 4; i++)
            {
                var c = file[at + i];
                if (!(c == 0 || c is >= (byte)'0' and <= (byte)'z')) return false;
            }
            if (file[at + 3] == 0 && file[at + 2] == 0 && file[at + 1] == 0 && file[at] == 0) return false;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
            if (size > (uint)(end - at - 8)) return false;
            at += 8 + (int)((size + 3) & ~3u);
        }
        return at == end && count > 0;
    }

    private static string NameAt(byte[] file, int at)
    {
        Span<char> name = stackalloc char[4];
        var n = 0;
        for (var i = 3; i >= 0; i--)
        {
            var c = file[at + i];
            if (c == 0 && n == 0) continue;
            name[n++] = (char)c;
        }
        return new string(name[..n]);
    }

    /// <summary>A block's data as text up to its first NUL (a texture's path), or null when it isn't plain text.</summary>
    public string? Text()
    {
        var data = Data;
        var end = data.IndexOf((byte)0);
        if (end < 0) end = data.Length;
        if (end is 0 or > 255) return null;
        foreach (var c in data[..end]) if (c is < 0x20 or > 0x7E) return null;
        return Encoding.ASCII.GetString(data[..end]);
    }
}
