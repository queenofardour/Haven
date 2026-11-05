using System.Collections.Concurrent;
using System.Numerics;
using Lumina.Data.Files;
using Lumina.Models.Models;

namespace Haven;

/// <summary>
/// The triangles furnishings are picked by. A model file's bounding box can be far larger than what it draws (a shelf's
/// box swallowed every piece standing on it), so the editor and interactions pick the drawn triangles under the
/// pointer, as the game does, and use the box only to skip models the ray can't reach. Each model's triangles (its
/// full-detail meshes that draw a surface: no shadow, fog or light-shaft meshes) are read once, one model at a time on a
/// background task; until then, or when a model can't be read, it is picked by its box. A model that draws no surface
/// at all (a window's or a Phasmascape's light shaft, 28 furnishings) is never picked: its box is the shaft's volume,
/// metres of empty room in front of the piece, which swallowed every click there (3 November 2026: nothing
/// under a Starry Sky Phasmascape, a Triple Triad Board included, could be chosen).
/// </summary>
internal sealed class FurniturePickMeshes(Func<string, MdlFile?> readModel, Action<Exception, string> warn)
{
    // The triangles, and the box that tightly holds them (a declared box can be loose, or miss a few vertices).
    internal sealed record PickMesh(Vector3[] Positions, int[] Triangles, Vector3 Minimum, Vector3 Maximum)
    {
        /// <summary>Whether the model draws nothing the pointer can rest on, so the ray passes through it.</summary>
        public bool Empty => Triangles.Length < 3;
    }
    private static readonly PickMesh Nothing = new([], [], Vector3.Zero, Vector3.Zero);

    private static readonly Mesh.MeshType[] Drawn = [Mesh.MeshType.Main, Mesh.MeshType.Water, Mesh.MeshType.Glass, Mesh.MeshType.MaterialChange, Mesh.MeshType.CrestChange];
    private readonly ConcurrentDictionary<string, PickMesh?> read = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> queue = new();
    private readonly ConcurrentDictionary<string, byte> queued = new(StringComparer.Ordinal);
    private int working;

    /// <summary>
    /// A model's triangles (Empty when it draws no surface), or null while they are read or when they can't be (pick by
    /// its box then).
    /// </summary>
    public PickMesh? Get(string path)
    {
        if (read.TryGetValue(path, out var mesh)) return mesh;
        if (queued.TryAdd(path, 0)) { queue.Enqueue(path); if (Interlocked.Exchange(ref working, 1) == 0) Task.Run(Work); }
        return null;
    }

    private void Work()
    {
        while (true)
        {
            while (queue.TryDequeue(out var path)) read[path] = Read(path);
            Volatile.Write(ref working, 0);
            // A path queued after the loop emptied but before the flag dropped would otherwise wait for the next one.
            if (queue.IsEmpty || Interlocked.Exchange(ref working, 1) != 0) return;
        }
    }

    private PickMesh? Read(string path)
    {
        try
        {
            if (readModel(path) is not { } file) return null;
            var model = new Model(file, Model.ModelLod.High);
            var positions = new List<Vector3>(); var triangles = new List<int>();
            foreach (var mesh in model.Meshes)
            {
                if (!mesh.Types.Any(Drawn.Contains) || mesh.Types.Contains(Mesh.MeshType.Shadow) || mesh.Types.Contains(Mesh.MeshType.TerrainShadow)) continue;
                var first = positions.Count;
                foreach (var vertex in mesh.Vertices) positions.Add(vertex.Position is { } p ? new(p.X, p.Y, p.Z) : Vector3.Zero);
                foreach (var index in mesh.Indices) triangles.Add(first + index);
            }
            if (triangles.Count < 3) return Nothing;
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var index in triangles) { min = Vector3.Min(min, positions[index]); max = Vector3.Max(max, positions[index]); }
            return new([.. positions], [.. triangles], min, max);
        }
        catch (Exception ex) { warn(ex, path); return null; }
    }
}
