using System.Numerics;

namespace Haven.Core.World;

/// <summary>A decoded texture: RGBA bytes, row by row.</summary>
public sealed record ThumbnailTexture(byte[] Rgba, int Width, int Height);

/// <summary>
/// One mesh to draw: its vertices in its model's space, placed by <see cref="Transform"/>; its texture (or none: drawn
/// in <see cref="Colour"/> alone), tinted by <see cref="Colour"/>, with texels under <see cref="AlphaCut"/> left out
/// (0: opaque, as most materials are; foliage cuts its leaves out by alpha).
/// </summary>
public sealed record ThumbnailMesh(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, int[] Indices, ThumbnailTexture? Texture, Vector3 Colour,
    float AlphaCut, Matrix4x4 Transform);

/// <summary>
/// A small software renderer for world piece thumbnails: no GPU and no game, so the offline
/// tool and the plugin's background thread draw the same picture. Every piece is seen from the same three-quarter view
/// (in front, a little to the right and above: the game's models face +Z), fitted to the picture, lit by one light
/// from the upper left and an ambient fill, on a transparent background. Orthographic, so a piece's size never skews
/// it; drawn at <c>supersample</c> times the size and averaged down for smooth edges.
/// </summary>
public static class ThumbnailRenderer
{
    public const int Size = 128;
    private const float Azimuth = 35 * MathF.PI / 180, Elevation = 28 * MathF.PI / 180, Fill = 0.9f;

    /// <summary>The camera's directions: toward the camera, screen right and screen up.</summary>
    public static (Vector3 Toward, Vector3 Right, Vector3 Up) View()
    {
        var toward = new Vector3(MathF.Sin(Azimuth) * MathF.Cos(Elevation), MathF.Sin(Elevation), MathF.Cos(Azimuth) * MathF.Cos(Elevation));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toward));
        return (toward, right, Vector3.Cross(toward, right));
    }

    /// <summary>
    /// The picture as <paramref name="size"/>² RGBA bytes, row by row from the top; null when there is nothing to draw
    /// (no triangles, or no extent).
    /// </summary>
    public static byte[]? Render(IReadOnlyList<ThumbnailMesh> meshes, int size = Size, int supersample = 3)
    {
        var (toward, right, up) = View();
        var light = Vector3.Normalize(-0.35f * right + 0.75f * up + 0.55f * toward);
        // Placed vertices and their place on screen (before fitting).
        var placed = meshes.Select(m => m.Positions.Select(p => Vector3.Transform(p, m.Transform)).ToArray()).ToArray();
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        var any = false;
        for (var i = 0; i < meshes.Count; i++)
            foreach (var index in meshes[i].Indices)
            {
                if ((uint)index >= (uint)placed[i].Length) continue;
                var p = placed[i][index];
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
                float x = Vector3.Dot(p, right), y = Vector3.Dot(p, up);
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); any = true;
            }
        var extent = Math.Max(maxX - minX, maxY - minY);
        if (!any || !(extent > 1e-5f) || !float.IsFinite(extent)) return null;
        var n = size * supersample;
        var scale = n * Fill / extent;
        float centreX = (minX + maxX) / 2, centreY = (minY + maxY) / 2;
        var depth = new float[n * n]; Array.Fill(depth, float.NegativeInfinity);
        var colour = new Vector4[n * n];
        var drawn = false;
        for (var i = 0; i < meshes.Count; i++)
        {
            var mesh = meshes[i]; var points = placed[i];
            var normals = mesh.Normals.Length == mesh.Positions.Length
                ? mesh.Normals.Select(v => Vector3.TransformNormal(v, mesh.Transform)).ToArray() : null;
            var uvs = mesh.Uvs.Length == mesh.Positions.Length ? mesh.Uvs : null;
            var screen = points.Select(p => new Vector3((Vector3.Dot(p, right) - centreX) * scale + n / 2f, n / 2f - (Vector3.Dot(p, up) - centreY) * scale, Vector3.Dot(p, toward))).ToArray();
            for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
            {
                int a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
                if ((uint)a >= (uint)points.Length || (uint)b >= (uint)points.Length || (uint)c >= (uint)points.Length || a == b || b == c || a == c) continue;
                var face = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
                if (!(face.LengthSquared() > 0) || !float.IsFinite(face.X)) continue;
                face = Vector3.Normalize(face);
                drawn |= Triangle(screen[a], screen[b], screen[c], n, depth, colour, (w0, w1, w2) =>
                {
                    var normal = normals == null ? face : Vector3.Normalize(normals[a] * w0 + normals[b] * w1 + normals[c] * w2);
                    if (!float.IsFinite(normal.X)) normal = face;
                    if (Vector3.Dot(normal, toward) < 0) normal = -normal; // both sides of a surface face the camera when seen
                    var albedo = new Vector4(mesh.Colour, 1);
                    if (mesh.Texture is { } texture && uvs != null)
                    {
                        var texel = Sample(texture, uvs[a] * w0 + uvs[b] * w1 + uvs[c] * w2);
                        if (mesh.AlphaCut > 0 && texel.W < mesh.AlphaCut) return null;
                        albedo = new(texel.X * mesh.Colour.X, texel.Y * mesh.Colour.Y, texel.Z * mesh.Colour.Z, 1);
                    }
                    var shade = 0.42f + 0.68f * Math.Max(0, Vector3.Dot(normal, light));
                    return new Vector4(Math.Min(1, albedo.X * shade), Math.Min(1, albedo.Y * shade), Math.Min(1, albedo.Z * shade), 1);
                });
            }
        }
        if (!drawn) return null;
        // Averaged down: colour weighted by coverage, so edges fade into the transparent background.
        var rgba = new byte[size * size * 4];
        var solid = 0;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var sum = Vector4.Zero;
                for (var sy = 0; sy < supersample; sy++)
                    for (var sx = 0; sx < supersample; sx++)
                    {
                        var s = colour[(y * supersample + sy) * n + x * supersample + sx];
                        sum += new Vector4(s.X * s.W, s.Y * s.W, s.Z * s.W, s.W);
                    }
                var o = (y * size + x) * 4;
                if (sum.W <= 0) continue;
                rgba[o] = (byte)Math.Clamp(sum.X / sum.W * 255 + .5f, 0, 255); rgba[o + 1] = (byte)Math.Clamp(sum.Y / sum.W * 255 + .5f, 0, 255);
                rgba[o + 2] = (byte)Math.Clamp(sum.Z / sum.W * 255 + .5f, 0, 255); rgba[o + 3] = (byte)Math.Clamp(sum.W / (supersample * supersample) * 255 + .5f, 0, 255);
                if (rgba[o + 3] >= 128) solid++;
            }
        // A piece that leaves only a faint trace (a rope, a scatter of alpha-cut specks) reads as a broken picture; the
        // plain tile says more.
        return solid >= size * size / 400 ? rgba : null;
    }

    // Fills a triangle's samples nearer than what's there; shade gives a sample's colour from its barycentric weights,
    // or null to leave it out (an alpha-cut texel). True when any sample was drawn.
    private static bool Triangle(Vector3 a, Vector3 b, Vector3 c, int n, float[] depth, Vector4[] colour, Func<float, float, float, Vector4?> shade)
    {
        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (Math.Abs(area) < 1e-9f) return false;
        int x0 = Math.Max(0, (int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X)))), x1 = Math.Min(n - 1, (int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)))), y1 = Math.Min(n - 1, (int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        var any = false;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float px = x + .5f, py = y + .5f;
                var w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                var w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                var w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                var z = a.Z * w0 + b.Z * w1 + c.Z * w2;
                var at = y * n + x;
                if (z <= depth[at]) continue;
                if (shade(w0, w1, w2) is not { } sample) continue;
                depth[at] = z; colour[at] = sample; any = true;
            }
        return any;
    }

    // A texel, the texture repeating (as the game's samplers wrap), RGBA 0-1.
    private static Vector4 Sample(ThumbnailTexture texture, Vector2 uv)
    {
        if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) uv = Vector2.Zero;
        var u = uv.X - MathF.Floor(uv.X); var v = uv.Y - MathF.Floor(uv.Y);
        var x = Math.Min(texture.Width - 1, (int)(u * texture.Width)); var y = Math.Min(texture.Height - 1, (int)(v * texture.Height));
        var o = (y * texture.Width + x) * 4;
        return new Vector4(texture.Rgba[o], texture.Rgba[o + 1], texture.Rgba[o + 2], texture.Rgba[o + 3]) / 255f;
    }
}
