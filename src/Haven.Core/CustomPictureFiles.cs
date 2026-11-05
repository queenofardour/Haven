using System.Buffers.Binary;
using System.Text;

namespace Haven.Core;

/// <summary>
/// The game files a custom picture is drawn from: a texture built from the picture's pixels and a copy of the frame's
/// placeholder material (pic_ta_20000a.mtrl) pointing at it. Both live at synthetic game paths only Haven uses, which
/// Penumbra redirects to the local files, so no real game file is replaced for anyone.
/// </summary>
public static class CustomPictureFiles
{
    public const string Placeholder = "bgcommon/hou/indoor/pic/ta/0000/material/pic_ta_20000a.mtrl";
    // A vanilla painting texture whose header the custom texture copies (attributes and LOD fields).
    public const string TemplateTexture = "bgcommon/hou/indoor/pic/ta/0001/texture/pic_ta_20001a_d.tex";
    private static string Folder(string hash) => $"bgcommon/hou/indoor/pic/hv/{hash[..32]}";
    public static string MaterialPath(string hash) => $"{Folder(hash)}/material/pic_hv_{hash[..32]}a.mtrl";
    public static string TexturePath(string hash) => $"{Folder(hash)}/texture/pic_hv_{hash[..32]}a_d.tex";

    private const int HeaderSize = 80;
    private const uint B8G8R8A8 = 0x1450;

    /// <summary>
    /// An uncompressed B8G8R8A8 texture with a full mip chain (box-filtered halvings) from RGBA pixels. The header's
    /// attribute and LOD fields come from a vanilla painting's texture; format, size, mip count and surface offsets
    /// are written for this one.
    /// </summary>
    public static byte[] Texture(ReadOnlySpan<byte> rgba, int width, int height, ReadOnlySpan<byte> template)
    {
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || rgba.Length != width * height * 4)
            throw new InvalidDataException("Invalid picture pixels.");
        if (template.Length < HeaderSize) throw new InvalidDataException("Invalid template texture.");
        List<byte[]> levels = [];
        var level = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4) { level[i] = rgba[i + 2]; level[i + 1] = rgba[i + 1]; level[i + 2] = rgba[i]; level[i + 3] = rgba[i + 3]; }
        int w = width, h = height;
        while (true)
        {
            levels.Add(level);
            if ((w == 1 && h == 1) || levels.Count == 13) break;
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * 4];
            for (var y = 0; y < nh; y++)
                for (var x = 0; x < nw; x++)
                    for (var c = 0; c < 4; c++)
                    {
                        int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1), y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                        next[(y * nw + x) * 4 + c] = (byte)((level[(y0 * w + x0) * 4 + c] + level[(y0 * w + x1) * 4 + c]
                            + level[(y1 * w + x0) * 4 + c] + level[(y1 * w + x1) * 4 + c] + 2) / 4);
                    }
            (level, w, h) = (next, nw, nh);
        }
        var output = new byte[HeaderSize + levels.Sum(l => l.Length)];
        template[..HeaderSize].CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), B8G8R8A8);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(10), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(12), 1);
        output[14] = (byte)((output[14] & 0x80) | levels.Count);
        output[15] = 1;
        output.AsSpan(28, 52).Clear();
        var at = HeaderSize;
        for (var i = 0; i < levels.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(28 + i * 4), (uint)at);
            levels[i].CopyTo(output, at);
            at += levels[i].Length;
        }
        return output;
    }

    /// <summary>
    /// The placeholder material with its painting texture path replaced: the new path is appended to the string table
    /// and the texture entry pointed at it; everything after the table moves down by the added length, and the file
    /// size grows to match. Layout (16-byte header): version, file size, data set size, string table size, shader
    /// package offset (u16 each after the u32 version), then texture, UV set and colour set counts and the additional
    /// data size (u8 each); then 4 bytes per texture, UV set and colour set entry; then the string table.
    /// </summary>
    public static byte[] Material(byte[] placeholder, string texturePath)
    {
        if (placeholder.Length < 16) throw new InvalidDataException("Invalid placeholder material.");
        int textures = placeholder[12], uvSets = placeholder[13], colorSets = placeholder[14];
        var tableSize = BinaryPrimitives.ReadUInt16LittleEndian(placeholder.AsSpan(8));
        var tableStart = 16 + 4 * (textures + uvSets + colorSets);
        if (tableStart + tableSize > placeholder.Length || textures == 0) throw new InvalidDataException("Invalid placeholder material.");
        string Text(int offset)
        {
            var bytes = placeholder.AsSpan(tableStart + offset, tableSize - offset);
            var end = bytes.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
        }
        var target = -1;
        for (var i = 0; i < textures; i++)
        {
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(placeholder.AsSpan(16 + i * 4));
            if (offset < tableSize && Text(offset).StartsWith("bgcommon/hou/indoor/pic/", StringComparison.Ordinal) && Text(offset).EndsWith("_d.tex", StringComparison.Ordinal)) { target = i; break; }
        }
        if (target < 0) throw new InvalidDataException("The placeholder material has no painting texture.");
        var added = Encoding.UTF8.GetBytes(texturePath + '\0');
        var padded = (added.Length + 3) & ~3;
        var output = new byte[placeholder.Length + padded];
        placeholder.AsSpan(0, tableStart + tableSize).CopyTo(output);
        added.CopyTo(output, tableStart + tableSize);
        placeholder.AsSpan(tableStart + tableSize).CopyTo(output.AsSpan(tableStart + tableSize + padded));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8), checked((ushort)(tableSize + padded)));
        var fileSize = BinaryPrimitives.ReadUInt16LittleEndian(placeholder.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), checked((ushort)(fileSize + padded)));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(16 + target * 4), tableSize);
        return output;
    }
}
