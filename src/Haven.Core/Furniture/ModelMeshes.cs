using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Haven.Core.Furniture;

/// <summary>
/// A model's meshes read for their texture coordinates and triangles, and new meshes added beside them. A mesh draws
/// with one material, so giving part of a mesh another material (a minion prop's glowing eyes) means moving those
/// triangles into a mesh of their own: the original keeps its index count with the moved triangles made degenerate
/// (never drawn), and the new mesh shares its vertices and draws only them. Layout as in Lumina's MdlStructs: a mesh
/// (0x24 bytes) has its vertex count (+0), index count (+4), material (+8), submesh start and count (+10, +12), first
/// index (+16), one vertex buffer offset per stream (+20), strides (+32) and stream count (+35); a submesh (0x10) its
/// first index, index count, attribute mask and bones; each vertex declaration (0x88, one per mesh) lists elements of
/// stream, offset, type and usage.
/// </summary>
public static class ModelMeshes
{
    /// <summary>A mesh of a level of detail: its material, each vertex's first texture coordinate and its triangles.</summary>
    public sealed record Mesh(int Index, int Lod, int Material, Vector2[] Uvs, int[] Indices);

    /// <summary>Triangles of one mesh (by triangle number) moved into a new mesh drawn with the given material.</summary>
    public sealed record Split(int Mesh, bool[] Moved, int Material);

    private sealed record Layout(int Strings, int StringSize, int Header, int Lods, int LodCount, int Meshes, int MeshCount,
        int Submeshes, int SubmeshCount, int MaterialOffsets, int MaterialCount, int End);

    // Walks the sections of a model without a skeleton, as StripSkeleton leaves it.
    private static Layout Walk(byte[] mdl)
    {
        var (start, size) = FurnitureFiles.ModelStrings(mdl);
        var header = start + size;
        if (header + FurnitureFiles.ModelHeaderSize > mdl.Length) throw new InvalidDataException("A model file is damaged.");
        int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(at));
        int meshes = U16(header + 4), attributes = U16(header + 6), submeshes = U16(header + 8), materials = U16(header + 10), bones = U16(header + 12), boneTables = U16(header + 14);
        int shapes = U16(header + 16), shapeMeshes = U16(header + 18), shapeValues = U16(header + 20), elementIds = U16(header + 24), terrainShadowMeshes = mdl[header + 26], flags2 = mdl[header + 27];
        int terrainShadowSubmeshes = U16(header + 38);
        if (bones != 0 || boneTables != 0) throw new InvalidDataException("Only a model without a skeleton can be given new meshes.");
        var lods = header + FurnitureFiles.ModelHeaderSize + elementIds * FurnitureFiles.ElementIdSize;
        var meshesAt = lods + 3 * FurnitureFiles.LodSize + ((flags2 & 0x10) != 0 ? 3 * FurnitureFiles.ExtraLodSize : 0);
        var submeshesAt = meshesAt + meshes * FurnitureFiles.MeshSize + attributes * 4 + terrainShadowMeshes * FurnitureFiles.TerrainShadowMeshSize;
        var materialsAt = submeshesAt + submeshes * FurnitureFiles.SubmeshSize + terrainShadowSubmeshes * FurnitureFiles.TerrainShadowSubmeshSize;
        var boneMapAt = materialsAt + materials * 4 + shapes * 16 + shapeMeshes * 12 + shapeValues * 4;
        if (boneMapAt + 4 > mdl.Length) throw new InvalidDataException("A model file is damaged.");
        var paddingAt = boneMapAt + 4 + BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(boneMapAt));
        if (paddingAt >= mdl.Length) throw new InvalidDataException("A model file is damaged.");
        var end = paddingAt + 1 + mdl[paddingAt] + 4 * 32;
        if (end != DataStart(mdl)) throw new InvalidDataException("This model's layout isn't one Haven can change safely.");
        if (U16(0x0C) != meshes) throw new InvalidDataException("This model's vertex declarations don't match its meshes.");
        return new(start, size, header, lods, mdl[0x40], meshesAt, meshes, submeshesAt, submeshes, materialsAt, materials, end);
    }

    private static uint DataStart(byte[] mdl) => Enumerable.Range(0, 3)
        .SelectMany(lod => new[] { BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(0x10 + lod * 4)), BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(0x1C + lod * 4)) })
        .Where(o => o != 0).DefaultIfEmpty((uint)mdl.Length).Min();

    /// <summary>Each level of detail's meshes with their first texture coordinates and triangles (16-bit indices).</summary>
    public static IReadOnlyList<Mesh> Read(byte[] mdl)
    {
        var layout = Walk(mdl);
        var result = new List<Mesh>();
        for (var lod = 0; lod < Math.Min(3, layout.LodCount); lod++)
        {
            var lodAt = layout.Lods + lod * FurnitureFiles.LodSize;
            int first = BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(lodAt)), count = BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(lodAt + 2));
            var vertexData = BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(0x10 + lod * 4));
            var indexData = BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(0x1C + lod * 4));
            for (var m = first; m < first + count && m < layout.MeshCount; m++)
            {
                var at = layout.Meshes + m * FurnitureFiles.MeshSize;
                int vertices = BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(at)), indices = (int)BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(at + 4));
                int material = BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(at + 8));
                var startIndex = BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(at + 16));
                var uvs = new Vector2[vertices];
                var uv = Element(mdl, m, 4);
                if (uv is { } element)
                {
                    var stride = mdl[at + 32 + element.Stream];
                    var bufferAt = vertexData + BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(at + 20 + element.Stream * 4));
                    for (var v = 0; v < vertices; v++)
                    {
                        var p = checked((int)(bufferAt + v * stride + element.Offset));
                        if (p + 8 > mdl.Length) throw new InvalidDataException("A model's vertices run past its end.");
                        uvs[v] = element.Type switch
                        {
                            1 or 2 or 3 => new Vector2(BinaryPrimitives.ReadSingleLittleEndian(mdl.AsSpan(p)), BinaryPrimitives.ReadSingleLittleEndian(mdl.AsSpan(p + 4))),
                            13 or 14 => new Vector2((float)BinaryPrimitives.ReadHalfLittleEndian(mdl.AsSpan(p)), (float)BinaryPrimitives.ReadHalfLittleEndian(mdl.AsSpan(p + 2))),
                            _ => Vector2.Zero,
                        };
                    }
                }
                var list = new int[indices];
                if (indexData + (startIndex + (long)indices) * 2 > mdl.Length) throw new InvalidDataException("A model's indices run past its end.");
                for (var i = 0; i < indices; i++) list[i] = BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan((int)(indexData + (startIndex + i) * 2)));
                result.Add(new(m, lod, material, uv is { Type: 1 or 2 or 3 or 13 or 14 } ? uvs : [], list));
            }
        }
        return result;
    }

    /// <summary>A full-detail mesh for drawing: positions, normals (empty when the file has none), first texture
    /// coordinates (empty when none), triangles and the material it draws with (a game path, or null).</summary>
    public sealed record Geometry(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, int[] Indices, string? Material);

    /// <summary>
    /// The full-detail meshes of any game model, read only (a world piece's thumbnail): unlike
    /// <see cref="Read"/> it needs no exact layout, since nothing is written back, but every read stays inside the file.
    /// Only the level's own meshes are taken (its water, shadow and fog meshes are counted apart in the file).
    /// </summary>
    public static IReadOnlyList<Geometry> ReadGeometry(byte[] mdl)
    {
        var (start, size) = FurnitureFiles.ModelStrings(mdl);
        var header = start + size;
        if (header + FurnitureFiles.ModelHeaderSize > mdl.Length) throw new InvalidDataException("A model file is damaged.");
        int U16(int at) => at + 2 <= mdl.Length ? BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(at)) : throw new InvalidDataException("A model file is damaged.");
        uint U32(int at) => at + 4 <= mdl.Length ? BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(at)) : throw new InvalidDataException("A model file is damaged.");
        int meshes = U16(header + 4), attributes = U16(header + 6), submeshes = U16(header + 8), materials = U16(header + 10);
        int elementIds = U16(header + 24), terrainShadowMeshes = mdl[header + 26], flags2 = mdl[header + 27], terrainShadowSubmeshes = U16(header + 38);
        var lods = header + FurnitureFiles.ModelHeaderSize + elementIds * FurnitureFiles.ElementIdSize;
        var meshesAt = lods + 3 * FurnitureFiles.LodSize + ((flags2 & 0x10) != 0 ? 3 * FurnitureFiles.ExtraLodSize : 0);
        var submeshesAt = meshesAt + meshes * FurnitureFiles.MeshSize + attributes * 4 + terrainShadowMeshes * FurnitureFiles.TerrainShadowMeshSize;
        var materialsAt = submeshesAt + submeshes * FurnitureFiles.SubmeshSize + terrainShadowSubmeshes * FurnitureFiles.TerrainShadowSubmeshSize;
        var names = new string?[materials];
        for (var i = 0; i < materials; i++)
        {
            var offset = (int)U32(materialsAt + i * 4);
            if (offset < 0 || offset >= size) continue;
            var end = Array.IndexOf(mdl, (byte)0, start + offset, size - offset);
            names[i] = Encoding.ASCII.GetString(mdl, start + offset, (end < 0 ? start + size : end) - start - offset);
        }
        if (U16(0x0C) < meshes) throw new InvalidDataException("This model's vertex declarations don't match its meshes.");
        int first = U16(lods), count = U16(lods + 2);
        var vertexData = U32(0x10); var indexData = U32(0x1C);
        var result = new List<Geometry>();
        for (var m = first; m < first + count && m < meshes; m++)
        {
            var at = meshesAt + m * FurnitureFiles.MeshSize;
            int vertices = U16(at), indices = (int)U32(at + 4), material = U16(at + 8);
            var startIndex = U32(at + 16);
            if (indices <= 0 || vertices == 0) continue;
            if (indexData + (startIndex + (long)indices) * 2 > mdl.Length) throw new InvalidDataException("A model's indices run past its end.");
            Vector4[]? Attribute(int usage)
            {
                if (Element(mdl, m, usage) is not { } element) return null;
                var stride = mdl[at + 32 + element.Stream];
                var bufferAt = vertexData + U32(at + 20 + element.Stream * 4);
                var values = new Vector4[vertices];
                for (var v = 0; v < vertices; v++)
                {
                    var p = bufferAt + (long)v * stride + element.Offset;
                    if (p < 0 || p + ElementSize(element.Type) > mdl.Length) throw new InvalidDataException("A model's vertices run past its end.");
                    values[v] = ReadElement(mdl, (int)p, element.Type);
                }
                return values;
            }
            var positions = Attribute(0) ?? throw new InvalidDataException("A model's mesh has no positions.");
            var normals = Attribute(3); var uvs = Attribute(4);
            var list = new int[indices];
            for (var i = 0; i < indices; i++)
            {
                list[i] = BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan((int)(indexData + (startIndex + i) * 2)));
                if (list[i] >= vertices) throw new InvalidDataException("A model's triangles name a vertex it doesn't have.");
            }
            result.Add(new([.. positions.Select(p => new Vector3(p.X, p.Y, p.Z))], normals == null ? [] : [.. normals.Select(n => new Vector3(n.X, n.Y, n.Z))],
                uvs == null ? [] : [.. uvs.Select(u => new Vector2(u.X, u.Y))], list, material < names.Length ? names[material] : null));
        }
        return result;
    }

    private static int ElementSize(int type) => type switch { 0 => 4, 1 => 8, 2 => 12, 3 => 16, 5 or 8 or 6 or 9 or 13 => 4, 7 or 10 or 14 or 16 or 17 => 8, _ => 16 };

    // One vertex element as four numbers (Lumina's VertexType): floats, halves, bytes (8: 0-1) and shorts (9, 10: -1-1).
    private static Vector4 ReadElement(byte[] mdl, int p, int type)
    {
        float F(int i) => BinaryPrimitives.ReadSingleLittleEndian(mdl.AsSpan(p + i * 4));
        float H(int i) => (float)BinaryPrimitives.ReadHalfLittleEndian(mdl.AsSpan(p + i * 2));
        float S(int i) => BinaryPrimitives.ReadInt16LittleEndian(mdl.AsSpan(p + i * 2));
        return type switch
        {
            0 => new(F(0), 0, 0, 0), 1 => new(F(0), F(1), 0, 0), 2 => new(F(0), F(1), F(2), 0), 3 => new(F(0), F(1), F(2), F(3)),
            5 => new(mdl[p], mdl[p + 1], mdl[p + 2], mdl[p + 3]), 8 => new Vector4(mdl[p], mdl[p + 1], mdl[p + 2], mdl[p + 3]) / 255f,
            6 => new(S(0), S(1), 0, 0), 7 => new(S(0), S(1), S(2), S(3)),
            9 => new Vector4(S(0), S(1), 0, 0) / 32767f, 10 => new Vector4(S(0), S(1), S(2), S(3)) / 32767f,
            13 => new(H(0), H(1), 0, 0), 14 => new(H(0), H(1), H(2), H(3)),
            16 => new(BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(p)), BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(p + 2)), 0, 0),
            17 => new(BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(p)), BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(p + 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(p + 4)), BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(p + 6))),
            _ => Vector4.Zero,
        };
    }

    // A mesh's vertex element of a usage (4: texture coordinates), first of its kind; null when it has none.
    private static (int Stream, int Offset, int Type)? Element(byte[] mdl, int mesh, int usage)
    {
        var at = 0x44 + mesh * 0x88;
        for (var i = 0; i < 17; i++)
        {
            var e = at + i * 8;
            if (mdl[e] == 255) break;
            if (mdl[e + 3] == usage && mdl[e + 4] == 0) return (mdl[e], mdl[e + 1], mdl[e + 2]);
        }
        return null;
    }

    /// <summary>
    /// A copy of a model with new materials named and some meshes' triangles moved into new meshes. Each new mesh goes
    /// at the end of its level of detail's meshes (later mesh numbers move up), copies its source's vertex declaration
    /// and vertex buffers, and draws its triangles from indices added after that level's index data; it has one submesh.
    /// Every offset after a change moves with it.
    /// </summary>
    public static byte[] AddMeshes(byte[] mdl, IReadOnlyList<string> newMaterials, IReadOnlyList<Split> splits)
    {
        var layout = Walk(mdl);
        if (splits.Count == 0 && newMaterials.Count == 0) return (byte[])mdl.Clone();
        if (mdl[0x42] != 0) throw new InvalidDataException("A model with edge geometry can't be given new meshes.");
        var meshes = Read(mdl).ToDictionary(m => m.Index);
        var lodOf = new Dictionary<int, int>();
        var ends = new int[3];
        for (var lod = 0; lod < 3; lod++)
        {
            var at = layout.Lods + lod * FurnitureFiles.LodSize;
            int first = BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(at)), count = BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(at + 2));
            ends[lod] = first + count;
            for (var m = first; m < first + count; m++) lodOf.TryAdd(m, lod);
        }
        foreach (var split in splits)
            if (!meshes.TryGetValue(split.Mesh, out var known) || split.Moved.Length * 3 > known.Indices.Length || split.Material >= layout.MaterialCount + newMaterials.Count)
                throw new InvalidOperationException("A mesh split doesn't fit the model.");
        var ordered = splits.Where(s => s.Moved.Any(m => m)).OrderBy(s => lodOf[s.Mesh]).ThenBy(s => s.Mesh).ToArray();
        // Where each new mesh goes (old numbering: before the mesh after its level's last), and old meshes' new numbers.
        int NewIndex(int old) => old + ordered.Count(s => ends[lodOf[s.Mesh]] <= old);

        var output = new MemoryStream();
        var w = new BinaryWriter(output);
        // File header and vertex declarations, with the new meshes' copies inserted.
        output.Write(mdl, 0, 0x44);
        var oldMeshCount = layout.MeshCount;
        for (var m = 0; m <= oldMeshCount; m++)
        {
            foreach (var s in ordered.Where(s => ends[lodOf[s.Mesh]] == m)) output.Write(mdl, 0x44 + s.Mesh * 0x88, 0x88);
            if (m < oldMeshCount) output.Write(mdl, 0x44 + m * 0x88, 0x88);
        }
        var declarationsGrowth = ordered.Length * 0x88;
        // Strings, with the new material names after the last string; the table grows by a multiple of 8.
        var stringsAt = (int)output.Length + 8;
        var used = layout.StringSize;
        while (used > 0 && mdl[layout.Strings + used - 1] == 0) used--;
        if (used < layout.StringSize) used++;
        var names = new MemoryStream();
        var nameOffsets = new List<int>();
        foreach (var name in newMaterials)
        {
            if (name.Any(c => c is < ' ' or >= (char)127)) throw new InvalidOperationException("A material path must be plain text.");
            nameOffsets.Add(used + (int)names.Length);
            names.Write(Encoding.ASCII.GetBytes(name + '\0'));
        }
        var stringSize = used + (int)names.Length;
        while ((stringSize - layout.StringSize) % 8 != 0 || stringSize < layout.StringSize) stringSize++;
        w.Write((ushort)(BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(layout.Strings - 8)) + newMaterials.Count));
        w.Write(BinaryPrimitives.ReadUInt16LittleEndian(mdl.AsSpan(layout.Strings - 6)));
        w.Write(stringSize);
        output.Write(mdl, layout.Strings, used);
        names.WriteTo(output);
        output.Write(new byte[stringSize - used - (int)names.Length]);
        // Model header, element ids, levels of detail (rewritten below), and everything up to the meshes.
        var headerAt = (int)output.Length;
        output.Write(mdl, layout.Header, layout.Meshes - layout.Header);
        // Meshes: the old ones, and each new mesh at the end of its level.
        var newMeshesAt = new Dictionary<Split, int>();
        var submeshNext = layout.SubmeshCount;
        for (var m = 0; m <= oldMeshCount; m++)
        {
            foreach (var s in ordered.Where(s => ends[lodOf[s.Mesh]] == m))
            {
                newMeshesAt[s] = (int)output.Length;
                output.Write(mdl, layout.Meshes + s.Mesh * FurnitureFiles.MeshSize, FurnitureFiles.MeshSize);
            }
            if (m < oldMeshCount) output.Write(mdl, layout.Meshes + m * FurnitureFiles.MeshSize, FurnitureFiles.MeshSize);
        }
        // Attributes and terrain shadow meshes, then the submeshes with one more per new mesh.
        var afterMeshes = layout.Meshes + oldMeshCount * FurnitureFiles.MeshSize;
        output.Write(mdl, afterMeshes, layout.Submeshes - afterMeshes);
        output.Write(mdl, layout.Submeshes, layout.SubmeshCount * FurnitureFiles.SubmeshSize);
        var newSubmeshesAt = (int)output.Length;
        output.Write(new byte[ordered.Length * FurnitureFiles.SubmeshSize]);
        // Terrain shadow submeshes, then material name offsets with the new names'.
        var afterSubmeshes = layout.Submeshes + layout.SubmeshCount * FurnitureFiles.SubmeshSize;
        output.Write(mdl, afterSubmeshes, layout.MaterialOffsets - afterSubmeshes);
        output.Write(mdl, layout.MaterialOffsets, layout.MaterialCount * 4);
        foreach (var offset in nameOffsets) w.Write(offset);
        // Shapes, the (empty) submesh bone map, padding and bounding boxes, unchanged.
        var afterMaterials = layout.MaterialOffsets + layout.MaterialCount * 4;
        output.Write(mdl, afterMaterials, layout.End - afterMaterials);
        var headerGrowth = (int)output.Length - layout.End;
        // Data: each level's vertex data and index data, with the moved triangles' indices added after its own.
        var head = output.ToArray();
        var dataStart = (int)DataStart(mdl);
        var added = new int[3];
        var newStarts = new Dictionary<Split, int>();
        var lodRanges = Enumerable.Range(0, 3).Select(lod => (Index: (int)BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(0x1C + lod * 4)), Size: (int)BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(0x34 + lod * 4)))).ToArray();
        var body = mdl.AsSpan(dataStart).ToArray();
        // Original indices, with moved triangles made degenerate.
        foreach (var s in ordered)
        {
            var lod = lodOf[s.Mesh];
            var meshAt = layout.Meshes + s.Mesh * FurnitureFiles.MeshSize;
            var start = (int)BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(meshAt + 16));
            for (var t = 0; t < s.Moved.Length; t++)
            {
                if (!s.Moved[t]) continue;
                var p = lodRanges[lod].Index - dataStart + (start + t * 3) * 2;
                var keep = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(p));
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p + 2), keep);
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p + 4), keep);
            }
        }
        var rebuilt = new MemoryStream();
        var cursor = 0;
        var insertions = new List<(int At, int Size)>();       // in old file offsets
        for (var lod = 0; lod < 3; lod++)
        {
            var mine = ordered.Where(s => lodOf[s.Mesh] == lod).ToArray();
            if (mine.Length == 0) continue;
            var endOfIndex = lodRanges[lod].Index + lodRanges[lod].Size - dataStart;
            rebuilt.Write(body, cursor, endOfIndex - cursor);
            cursor = endOfIndex;
            var indexCount = lodRanges[lod].Size / 2;
            var extra = new MemoryStream();
            foreach (var s in mine)
            {
                newStarts[s] = indexCount + (int)extra.Length / 2;
                var mesh = meshes[s.Mesh];
                for (var t = 0; t < s.Moved.Length; t++)
                    if (s.Moved[t])
                        for (var k = 0; k < 3; k++) { var v = (ushort)mesh.Indices[t * 3 + k]; extra.WriteByte((byte)v); extra.WriteByte((byte)(v >> 8)); }
                while (extra.Length % 16 != 0) extra.WriteByte(0);              // each mesh's indices start 16-byte aligned
            }
            added[lod] = (int)extra.Length;
            extra.WriteTo(rebuilt);
            insertions.Add((lodRanges[lod].Index + lodRanges[lod].Size, (int)extra.Length));
        }
        rebuilt.Write(body, cursor, body.Length - cursor);
        var result = new byte[head.Length + rebuilt.Length];
        head.CopyTo(result, 0);
        rebuilt.ToArray().CopyTo(result, head.Length);
        // Absolute offsets: everything in the data moves by the header's growth, and from an insertion on by its size.
        uint Shift(uint old) => old == 0 ? 0 : checked(old + (uint)headerGrowth + (uint)insertions.Where(i => i.At <= old).Sum(i => i.Size));
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at), value);
        uint R32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
        void U16(int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(at), checked((ushort)value));
        int R16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
        U32(0x04, R32(mdl, 0x04) + (uint)declarationsGrowth);                                          // vertex declarations' size
        U32(0x08, R32(mdl, 0x08) + (uint)(headerGrowth - declarationsGrowth));                         // runtime size
        U16(0x0C, R16(mdl, 0x0C) + ordered.Length);
        U16(0x0E, R16(mdl, 0x0E) + newMaterials.Count);
        for (var lod = 0; lod < 3; lod++)
        {
            U32(0x10 + lod * 4, Shift(R32(mdl, 0x10 + lod * 4)));
            U32(0x1C + lod * 4, Shift(R32(mdl, 0x1C + lod * 4)));
            U32(0x34 + lod * 4, R32(mdl, 0x34 + lod * 4) + (uint)added[lod]);
        }
        var header = headerAt;
        U16(header + 4, layout.MeshCount + ordered.Length);
        U16(header + 8, layout.SubmeshCount + ordered.Length);
        U16(header + 10, layout.MaterialCount + newMaterials.Count);
        var lodsAt = header + (layout.Lods - layout.Header);
        for (var lod = 0; lod < 3; lod++)
        {
            var at = lodsAt + lod * FurnitureFiles.LodSize;
            var old = layout.Lods + lod * FurnitureFiles.LodSize;
            U16(at, NewIndex(R16(mdl, old)));
            U16(at + 2, R16(mdl, old + 2) + ordered.Count(s => lodOf[s.Mesh] == lod));
            for (var field = 12; field <= 24; field += 4)
                if (field != 20) U16(at + field, NewIndex(R16(mdl, old + field)));                       // water, shadow, fog meshes
            U32(at + 32, Shift(R32(mdl, old + 32)));
            U32(at + 48, R32(mdl, old + 48) + (uint)added[lod]);
            U32(at + 52, Shift(R32(mdl, old + 52)));
            U32(at + 56, Shift(R32(mdl, old + 56)));
        }
        var n = 0;
        foreach (var s in ordered)
        {
            var at = newMeshesAt[s];
            var count = s.Moved.Count(m => m) * 3;
            U32(at + 4, (uint)count);
            U16(at + 8, s.Material);
            U16(at + 10, layout.SubmeshCount + n);
            U16(at + 12, 1);
            U32(at + 16, (uint)newStarts[s]);
            var sub = newSubmeshesAt + n * FurnitureFiles.SubmeshSize;
            U32(sub, (uint)newStarts[s]);
            U32(sub + 4, (uint)count);
            n++;
        }
        return result;
    }
}
