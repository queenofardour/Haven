using System.Buffers.Binary;
using System.Text;

namespace Haven.Core.Furniture;

/// <summary>
/// The paths inside furniture files, read and rewritten. A furniture model (.mdl) names its
/// materials by full game path in its string table; a material (.mtrl) names its textures the same way. Read from the
/// installed game: the model header is 0x44 bytes, then its vertex declarations (0x88 bytes each, count at 0x0C), then
/// the string count (u16), padding, the string table's size (u32) and the strings. A material's 16-byte header gives
/// the string table's size (+8) and the texture, UV set and colour set counts (+12..+14); texture offsets (u16, plus
/// u16 flags) follow, then the UV and colour set entries, then the strings.
/// </summary>
public static class FurnitureFiles
{
    public const int MaxTextureSide = 8192;

    // Models ------------------------------------------------------------------

    internal static (int Start, int Size) ModelStrings(byte[] mdl)
    {
        if (mdl.Length < 0x44 + 8) throw new InvalidDataException("A model file is too short.");
        var declarations = BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(0x0C));
        var at = 0x44 + declarations * 0x88;
        if (at + 8 > mdl.Length) throw new InvalidDataException("A model file is damaged.");
        var size = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(at + 4));
        if (size < 0 || at + 8 + size > mdl.Length) throw new InvalidDataException("A model file is damaged.");
        return (at + 8, size);
    }

    /// <summary>The materials a model names (game paths, as written in the file).</summary>
    public static IReadOnlyList<string> ModelMaterials(byte[] mdl)
    {
        var (start, size) = ModelStrings(mdl);
        return Encoding.ASCII.GetString(mdl, start, size).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>
    /// A copy of a model whose material paths are replaced where <paramref name="replace"/> gives a new one. Each new path
    /// must be exactly as long as the one it replaces, so it is written in place and nothing else in the file moves.
    /// </summary>
    public static byte[] RepathModel(byte[] mdl, Func<string, string?> replace)
    {
        var (start, size) = ModelStrings(mdl);
        var output = (byte[])mdl.Clone();
        var at = start;
        while (at < start + size)
        {
            var end = Array.IndexOf(output, (byte)0, at, start + size - at);
            if (end < 0) end = start + size;
            var text = Encoding.ASCII.GetString(mdl, at, end - at);
            if (text.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) && replace(text) is { } next)
            {
                if (next.Length != text.Length || next.Any(c => c is < ' ' or >= (char)127)) throw new InvalidOperationException("A replacement material path must be as long as the original.");
                Encoding.ASCII.GetBytes(next, output.AsSpan(at));
            }
            at = end + 1;
        }
        return output;
    }

    // Materials ---------------------------------------------------------------

    internal static (int Table, int Size, int Textures) MaterialLayout(byte[] mtrl)
    {
        if (mtrl.Length < 16) throw new InvalidDataException("A material file is too short.");
        int textures = mtrl[12], uvSets = mtrl[13], colorSets = mtrl[14];
        var size = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(8));
        var table = 16 + 4 * (textures + uvSets + colorSets);
        if (table + size > mtrl.Length) throw new InvalidDataException("A material file is damaged.");
        return (table, size, textures);
    }

    private static string MaterialText(byte[] mtrl, int table, int size, int offset)
    {
        if (offset >= size) throw new InvalidDataException("A material file is damaged.");
        var bytes = mtrl.AsSpan(table + offset, size - offset);
        var end = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
    }

    /// <summary>The textures a material names (game paths).</summary>
    public static IReadOnlyList<string> MaterialTextures(byte[] mtrl)
    {
        var (table, size, textures) = MaterialLayout(mtrl);
        return Enumerable.Range(0, textures).Select(i => MaterialText(mtrl, table, size, BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(16 + i * 4)))).ToArray();
    }

    /// <summary>
    /// A copy of a material whose texture paths are replaced where <paramref name="replace"/> gives a new one: each new
    /// path is added to the end of the string table and its texture pointed at it, as custom picture frames do.
    /// </summary>
    public static byte[] RepathMaterial(byte[] mtrl, Func<string, string?> replace)
    {
        var (table, size, textures) = MaterialLayout(mtrl);
        var added = new MemoryStream();
        var offsets = new Dictionary<int, int>();
        for (var i = 0; i < textures; i++)
        {
            var path = MaterialText(mtrl, table, size, BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(16 + i * 4)));
            if (replace(path) is not { } next) continue;
            offsets[i] = size + (int)added.Length;
            added.Write(Encoding.ASCII.GetBytes(next + '\0'));
        }
        if (offsets.Count == 0) return (byte[])mtrl.Clone();
        while (added.Length % 4 != 0) added.WriteByte(0);
        var grow = (int)added.Length;
        var output = new byte[mtrl.Length + grow];
        mtrl.AsSpan(0, table + size).CopyTo(output);
        added.ToArray().CopyTo(output, table + size);
        mtrl.AsSpan(table + size).CopyTo(output.AsSpan(table + size + grow));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8), checked((ushort)(size + grow)));
        var fileSize = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), checked((ushort)(fileSize + grow)));
        foreach (var (texture, offset) in offsets) BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(16 + texture * 4), checked((ushort)offset));
        return output;
    }

    // Minion props ------------------------------------------------------------

    // The model header that follows the string table (0x38 bytes): the counts that size what comes before the material
    // name offsets. Field positions as Lumina's MdlStructs.ModelHeader reads them.
    internal const int ModelHeaderSize = 0x38, ElementIdSize = 0x20, LodSize = 0x3C, ExtraLodSize = 0x28, MeshSize = 0x24, TerrainShadowMeshSize = 0x14, SubmeshSize = 0x10, TerrainShadowSubmeshSize = 0x0A;

    /// <summary>
    /// A copy of a model whose material names are replaced where <paramref name="replace"/> gives a new one, of any
    /// length: a character model names its materials relatively (/mt_m8045b0001_a.mtrl), and a furnishing needs full
    /// paths. The new names are added to the end of the string table (every other string keeps its offset), the
    /// material name offsets point at them, and every absolute offset after the table (the vertex and index data, each
    /// level of detail's data and edge geometry) moves by the table's growth. Where the model's material offsets are is
    /// worked out from its header, and checked against its strings before anything is changed.
    /// </summary>
    public static byte[] RewriteModelMaterials(byte[] mdl, Func<string, string?> replace)
    {
        var (output, _) = RewriteModelMaterialsAndRead(mdl, replace);
        return output;
    }

    /// <summary>A model's material names by material number, as its meshes refer to them.</summary>
    public static IReadOnlyList<string> ModelMaterialSlots(byte[] mdl) => RewriteModelMaterialsAndRead(mdl, _ => null).Names;

    private static (byte[] Output, string[] Names) RewriteModelMaterialsAndRead(byte[] mdl, Func<string, string?> replace)
    {
        var (start, size) = ModelStrings(mdl);
        var header = start + size;
        if (header + ModelHeaderSize > mdl.Length) throw new InvalidDataException("A model file is damaged.");
        int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(at));
        int meshes = U16(header + 4), attributes = U16(header + 6), submeshes = U16(header + 8), materials = U16(header + 10);
        int elementIds = U16(header + 24), terrainShadowMeshes = mdl[header + 26], flags2 = mdl[header + 27], terrainShadowSubmeshes = U16(header + 38);
        var lods = header + ModelHeaderSize + elementIds * ElementIdSize;
        var offsets = lods + 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * ExtraLodSize : 0)
            + meshes * MeshSize + attributes * 4 + terrainShadowMeshes * TerrainShadowMeshSize + submeshes * SubmeshSize + terrainShadowSubmeshes * TerrainShadowSubmeshSize;
        if (offsets + materials * 4 > mdl.Length) throw new InvalidDataException("A model file is damaged.");
        string At(int offset)
        {
            if (offset < 0 || offset >= size) throw new InvalidDataException("A model's material names could not be found.");
            var end = Array.IndexOf(mdl, (byte)0, start + offset, size - offset);
            return Encoding.ASCII.GetString(mdl, start + offset, (end < 0 ? start + size : end) - start - offset);
        }
        var names = Enumerable.Range(0, materials).Select(i => At(BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(offsets + i * 4)))).ToArray();
        if (names.Any(n => !n.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("A model's material names could not be found.");
        // New names follow the last string (not the table's zero padding), so they read in sequence like the others.
        var used = size;
        while (used > 0 && mdl[start + used - 1] == 0) used--;
        if (used < size) used++;                                          // keep the last string's terminator
        var added = new MemoryStream();
        var next = new int[materials];
        var count = 0;
        for (var i = 0; i < materials; i++)
        {
            next[i] = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(offsets + i * 4));
            if (replace(names[i]) is not { } path) continue;
            if (path.Any(c => c is < ' ' or >= (char)127)) throw new InvalidOperationException("A material path must be plain text.");
            next[i] = used + (int)added.Length;
            added.Write(Encoding.ASCII.GetBytes(path + '\0'));
            count++;
        }
        // The table grows by a multiple of 8, so everything after it keeps its alignment.
        var newSize = used + (int)added.Length;
        while ((newSize - size) % 8 != 0 || newSize < size) newSize++;
        var delta = newSize - size;
        var output = new byte[mdl.Length + delta];
        mdl.AsSpan(0, start + used).CopyTo(output);
        added.ToArray().CopyTo(output, start + used);
        mdl.AsSpan(header).CopyTo(output.AsSpan(header + delta));
        void Grow(int at) { var value = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(at)); if (value != 0) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), checked(value + (uint)delta)); }
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(start - 4), size + delta);                // the string table's size
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(start - 8), checked((ushort)(U16(start - 8) + count)));
        Grow(0x08);                                                                                       // runtime size
        for (var lod = 0; lod < 3; lod++) { Grow(0x10 + lod * 4); Grow(0x1C + lod * 4); }               // vertex and index data
        for (var lod = 0; lod < 3; lod++)
        {
            var at = lods + delta + lod * LodSize;
            Grow(at + 32); Grow(at + 52); Grow(at + 56);                                                 // edge geometry, vertex and index data
        }
        for (var i = 0; i < materials; i++) BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(offsets + delta + i * 4), next[i]);
        return (output, names);
    }

    /// <summary>
    /// A copy of a model with its skeleton removed: no bones, no bone tables, no per-bone bounding boxes, every mesh and
    /// submesh pointing at no bones. A scenery object (BGObject.UpdateRender, read-only from the client) loads a
    /// skeleton and animation for any model whose bone count is above zero, and for a converted minion model that
    /// crashed the game (0.37.0, 28 October 2026). Vanilla furniture has no bones. Every section after the string table
    /// is located from the model header's counts (Lumina's MdlStructs layout), and the walk must end exactly where the
    /// vertex data begins, or nothing is changed. The vertices keep their (unused) blend weights; a rigid prop's
    /// vertices are already in their resting pose.
    /// </summary>
    public static byte[] StripSkeleton(byte[] mdl)
    {
        var (start, size) = ModelStrings(mdl);
        var header = start + size;
        if (header + ModelHeaderSize > mdl.Length) throw new InvalidDataException("A model file is damaged.");
        int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(at));
        int meshes = U16(header + 4), attributes = U16(header + 6), submeshes = U16(header + 8), materials = U16(header + 10), bones = U16(header + 12), boneTables = U16(header + 14);
        int shapes = U16(header + 16), shapeMeshes = U16(header + 18), shapeValues = U16(header + 20), elementIds = U16(header + 24), terrainShadowMeshes = mdl[header + 26], flags2 = mdl[header + 27];
        int terrainShadowSubmeshes = U16(header + 38);
        if (bones == 0 && boneTables == 0) return (byte[])mdl.Clone();
        var version = BinaryPrimitives.ReadUInt32LittleEndian(mdl) & 0xFF;
        var lods = header + ModelHeaderSize + elementIds * ElementIdSize;
        var meshesAt = lods + 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * ExtraLodSize : 0);
        var submeshesAt = meshesAt + meshes * MeshSize + attributes * 4 + terrainShadowMeshes * TerrainShadowMeshSize;
        var boneNamesAt = submeshesAt + submeshes * SubmeshSize + terrainShadowSubmeshes * TerrainShadowSubmeshSize + materials * 4;
        var boneTablesAt = boneNamesAt + bones * 4;
        int boneTablesSize;
        if (version == 5) boneTablesSize = boneTables * 132;
        else if (version == 6)
        {
            // Version 6: an offset and count per table, then each table's bone indices, padded to an even count.
            boneTablesSize = boneTables * 4;
            for (var i = 0; i < boneTables; i++) { var count = U16(boneTablesAt + i * 4 + 2); boneTablesSize += (count + count % 2) * 2; }
        }
        else throw new InvalidDataException($"Haven doesn't know model version {version}.");
        var shapesAt = boneTablesAt + boneTablesSize;
        var boneMapAt = shapesAt + shapes * 16 + shapeMeshes * 12 + shapeValues * 4;
        if (boneMapAt + 4 > mdl.Length) throw new InvalidDataException("A model file is damaged.");
        var boneMapSize = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(boneMapAt));
        var paddingAt = boneMapAt + 4 + boneMapSize;
        if (boneMapSize < 0 || paddingAt >= mdl.Length) throw new InvalidDataException("A model file is damaged.");
        var boxesAt = paddingAt + 1 + mdl[paddingAt];
        var boneBoxesAt = boxesAt + 4 * 32;
        var end = boneBoxesAt + bones * 32;
        var dataAt = Enumerable.Range(0, 3).SelectMany(lod => new[] { BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(0x10 + lod * 4)), BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(0x1C + lod * 4)) })
            .Where(o => o != 0).DefaultIfEmpty((uint)mdl.Length).Min();
        if (end != dataAt) throw new InvalidDataException("This model's layout isn't one Haven can convert safely.");
        // Keep everything but the bone names, the bone tables, the submesh bone map and the per-bone boxes.
        var removed = bones * 4 + boneTablesSize + boneMapSize + bones * 32;
        var output = new byte[mdl.Length - removed];
        var o = 0;
        void Copy(int from, int to) { mdl.AsSpan(from, to - from).CopyTo(output.AsSpan(o)); o += to - from; }
        Copy(0, boneNamesAt);
        Copy(shapesAt, boneMapAt);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(o), 0); o += 4;     // an empty submesh bone map
        Copy(paddingAt, boneBoxesAt);
        Copy(end, mdl.Length);
        void Shrink(int at) { var value = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(at)); if (value != 0) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), checked(value - (uint)removed)); }
        Shrink(0x08);
        for (var lod = 0; lod < 3; lod++) { Shrink(0x10 + lod * 4); Shrink(0x1C + lod * 4); Shrink(lods + lod * LodSize + 32); Shrink(lods + lod * LodSize + 52); Shrink(lods + lod * LodSize + 56); }
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(header + 12), 0);         // bones
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(header + 14), 0);         // bone tables
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(header + 44), 0);         // bone table entries in all
        for (var m = 0; m < meshes; m++) BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(meshesAt + m * MeshSize + 14), 255);   // no bone table
        for (var s = 0; s < submeshes; s++) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(submeshesAt + s * SubmeshSize + 12), 0);  // bone start and count
        return output;
    }

    /// <summary>The number of bones a model names (above zero, a scenery object would try to animate it).</summary>
    public static int ModelBones(byte[] mdl)
    {
        var (start, size) = ModelStrings(mdl);
        return start + size + 14 <= mdl.Length ? BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(start + size + 12)) : 0;
    }

    /// <summary>The name of the shader package a material uses (character.shpk, bg.shpk…).</summary>
    public static string MaterialShader(byte[] mtrl)
    {
        var (table, size, _) = MaterialLayout(mtrl);
        return MaterialText(mtrl, table, size, BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(10)));
    }

    // Character shader samplers (CRC of their names), as character and characterlegacy materials name them.
    private const uint SamplerDiffuse = 0x115306BE, SamplerNormal = 0x0C5EC1F1, SamplerSpecular = 0x2B99E025;

    /// <summary>
    /// A material's samplers: each sampler's id (a CRC of its name) and the texture it reads. They follow the strings,
    /// the additional data and the colour table: a 12-byte shader header (value list size, then the shader key,
    /// constant and sampler counts), the keys and constants (8 bytes each), then 12 bytes per sampler (id, flags,
    /// texture index).
    /// </summary>
    public static IReadOnlyList<(uint Id, string Texture)> MaterialSamplers(byte[] mtrl)
    {
        var (table, size, _) = MaterialLayout(mtrl);
        var textures = MaterialTextures(mtrl);
        var at = table + size + mtrl[15] + BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(6));
        if (at + 12 > mtrl.Length) return [];
        int keys = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at + 2)), constants = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at + 4)), samplers = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at + 6));
        var start = at + 12 + keys * 8 + constants * 8;
        if (start + samplers * 12 > mtrl.Length) return [];
        return [.. Enumerable.Range(0, samplers).Select(i => (Id: BinaryPrimitives.ReadUInt32LittleEndian(mtrl.AsSpan(start + i * 12)), Index: (int)mtrl[start + i * 12 + 8]))
            .Where(s => s.Index < textures.Count).Select(s => (s.Id, textures[s.Index]))];
    }

    /// <summary>
    /// A character material's diffuse, normal and specular textures, the three a furnishing's material takes. The
    /// material's own samplers say which texture is which; names are the fallback (_d or _base, _n or _norm, _s, also
    /// in the middle of a name, as in unknown_d_1192830025.tex). Null for one it doesn't have; a mask is not a specular
    /// map, so a material with only a mask gets none.
    /// </summary>
    public static (string? Diffuse, string? Normal, string? Specular) CharacterTextures(byte[] mtrl)
    {
        var samplers = MaterialSamplers(mtrl);
        string? Sampler(uint id) => samplers.Where(s => s.Id == id).Select(s => s.Texture).FirstOrDefault();
        var textures = MaterialTextures(mtrl);
        string? Named(params string[] roles) => textures.FirstOrDefault(t =>
        {
            var name = Path.GetFileNameWithoutExtension(t).ToLowerInvariant();
            return roles.Any(r => name.EndsWith("_" + r, StringComparison.Ordinal) || name.Contains("_" + r + "_", StringComparison.Ordinal));
        });
        return (Sampler(SamplerDiffuse) ?? Named("d", "base"), Sampler(SamplerNormal) ?? Named("n", "norm"), Sampler(SamplerSpecular) ?? (samplers.Count == 0 ? Named("s") : null));
    }

    /// <summary>
    /// The material set (the vNNNN folder) a model's variant uses, from its .imc file: a count and a part mask, then six
    /// bytes per part for the default entry and for each variant, the first byte being the material set. Null when the
    /// file doesn't cover that variant.
    /// </summary>
    public static int? ImcMaterialSet(byte[] imc, int variant)
    {
        if (imc.Length < 4 || variant < 0) return null;
        var parts = System.Numerics.BitOperations.PopCount((uint)BinaryPrimitives.ReadUInt16LittleEndian(imc.AsSpan(2)));
        var at = 4 + variant * Math.Max(1, parts) * 6;
        return at + 6 <= imc.Length && imc[at] > 0 ? imc[at] : null;
    }

    /// <summary>
    /// A furnishing material for a minion prop: a copy of a vanilla furniture material (bg.shpk, with diffuse, normal and
    /// specular textures) pointed at the prop's own textures.
    /// </summary>
    public static byte[] SceneryMaterial(byte[] template, string diffuse, string normal, string specular)
    {
        if (!MaterialShader(template).Equals("bg.shpk", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The furniture material template is not a scenery material.");
        return RepathMaterial(template, path => path.EndsWith("_d.tex", StringComparison.OrdinalIgnoreCase) ? diffuse
            : path.EndsWith("_n.tex", StringComparison.OrdinalIgnoreCase) ? normal : path.EndsWith("_s.tex", StringComparison.OrdinalIgnoreCase) ? specular : null);
    }

    /// <summary>
    /// A copy of a material with one shader constant's values replaced (a constant is its id, then the offset and size
    /// in bytes of its values in the value list that follows the samplers). Null when the material has no such
    /// constant or the values don't fit it.
    /// </summary>
    /// <summary>A material constant's values (laid out as <see cref="WithConstant"/> reads them), or null when it has none.</summary>
    public static float[]? MaterialConstant(byte[] mtrl, uint id)
    {
        var (table, size, _) = MaterialLayout(mtrl);
        var at = table + size + mtrl[15] + BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(6));
        if (at + 12 > mtrl.Length) return null;
        int listSize = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at)), keys = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at + 2));
        int constants = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at + 4)), samplers = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at + 6));
        var constantsAt = at + 12 + keys * 8;
        var valuesAt = constantsAt + constants * 8 + samplers * 12;
        if (valuesAt + listSize > mtrl.Length) return null;
        for (var i = 0; i < constants; i++)
        {
            var c = constantsAt + i * 8;
            if (BinaryPrimitives.ReadUInt32LittleEndian(mtrl.AsSpan(c)) != id) continue;
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(c + 4)), bytes = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(c + 6));
            if (offset + bytes > listSize || bytes % 4 != 0) return null;
            return [.. Enumerable.Range(0, bytes / 4).Select(v => BinaryPrimitives.ReadSingleLittleEndian(mtrl.AsSpan(valuesAt + offset + v * 4)))];
        }
        return null;
    }

    /// <summary>A material's shader flags and the ids of its shader keys (with their values) and constants.</summary>
    public static (uint Flags, IReadOnlyList<(uint Id, uint Value)> Keys, IReadOnlyList<uint> Constants) MaterialShaderValues(byte[] mtrl)
    {
        var (table, size, _) = MaterialLayout(mtrl);
        var at = table + size + mtrl[15] + BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(6));
        if (at + 12 > mtrl.Length) return (0, [], []);
        int keys = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at + 2)), constants = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at + 4));
        if (at + 12 + keys * 8 + constants * 8 > mtrl.Length) return (0, [], []);
        return (BinaryPrimitives.ReadUInt32LittleEndian(mtrl.AsSpan(at + 8)),
            [.. Enumerable.Range(0, keys).Select(i => (BinaryPrimitives.ReadUInt32LittleEndian(mtrl.AsSpan(at + 12 + i * 8)), BinaryPrimitives.ReadUInt32LittleEndian(mtrl.AsSpan(at + 16 + i * 8))))],
            [.. Enumerable.Range(0, constants).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(mtrl.AsSpan(at + 12 + keys * 8 + i * 8)))]);
    }

    public static byte[]? WithConstant(byte[] mtrl, uint id, params float[] values)
    {
        var (table, size, _) = MaterialLayout(mtrl);
        var at = table + size + mtrl[15] + BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(6));
        if (at + 12 > mtrl.Length) return null;
        int listSize = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at)), keys = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at + 2));
        int constants = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at + 4)), samplers = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(at + 6));
        var constantsAt = at + 12 + keys * 8;
        var valuesAt = constantsAt + constants * 8 + samplers * 12;
        if (valuesAt + listSize > mtrl.Length) return null;
        for (var i = 0; i < constants; i++)
        {
            var c = constantsAt + i * 8;
            if (BinaryPrimitives.ReadUInt32LittleEndian(mtrl.AsSpan(c)) != id) continue;
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(c + 4)), bytes = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(c + 6));
            if (bytes != values.Length * 4 || offset + bytes > listSize) return null;
            var output = (byte[])mtrl.Clone();
            for (var v = 0; v < values.Length; v++) BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(valuesAt + offset + v * 4), values[v]);
            return output;
        }
        return null;
    }

    // Checks ------------------------------------------------------------------

    /// <summary>
    /// Refuses a file whose structure is not what its kind should be, before the game is ever given it: a model's string
    /// table and buffer offsets within the file, a material's table, a texture's size, mip offsets and data.
    /// </summary>
    public static void Check(string path, byte[] bytes)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".mdl":
                ModelStrings(bytes);
                for (var lod = 0; lod < 3; lod++)
                {
                    long vertex = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x10 + lod * 4)), index = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x1C + lod * 4));
                    long vertexSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x28 + lod * 4)), indexSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x34 + lod * 4));
                    if (vertex + vertexSize > bytes.Length || index + indexSize > bytes.Length) throw new InvalidDataException("A model file's buffers run past its end.");
                }
                break;
            case ".mtrl":
                MaterialTextures(bytes);
                break;
            case ".tex":
                TextureDecoding.CheckMips(bytes, MaxTextureSide);
                break;
            default:
                throw new InvalidDataException($"Haven doesn't take {Path.GetExtension(path)} files in custom furniture yet.");
        }
    }
}
