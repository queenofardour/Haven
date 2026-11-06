using System.Numerics;
using Haven.Core.Furniture;

namespace Haven.Core.World;

/// <summary>
/// A world piece's thumbnail from its game files: each model's full-detail meshes, each
/// drawn with its material's colour texture, through <see cref="ThumbnailRenderer"/>. Files come through a reader (the
/// plugin's IDataManager, the tool's Lumina), so nothing here touches the game itself. Whatever can't be read is left
/// out; a piece with nothing left has no thumbnail (the drawer shows a plain icon).
/// </summary>
public static class WorldThumbnails
{
    // The shaders' ids for the samplers and constants read here (as materials name them; Penumbra lists the same):
    // g_SamplerColorMap0, a character's g_SamplerDiffuse, g_AlphaThreshold and g_DiffuseColor.
    private const uint ColourMap = 0x1E6FEF9C, Diffuse = 0x115306BE, AlphaThreshold = 0x29AC0223, DiffuseColour = 0x2C2A34DD;
    // Shaders that draw no surface of their own in a picture: light shafts, fog volumes, shadows.
    private static readonly string[] Unseen = ["lightshaft.shpk", "verticalfog.shpk", "shadow", "lightshaft"];
    private static readonly string[] Water = ["water.shpk", "river.shpk", "waterfall"];
    // Small mips are enough: a thumbnail shows a texture a few dozen pixels across.
    private const int TextureSide = 128;

    /// <summary>The meshes of the given models (each with its place in the piece), ready to draw.</summary>
    /// <param name="textures">Textures decoded so far, by path (shared across pieces by a caller drawing many).</param>
    public static List<ThumbnailMesh> Meshes(IEnumerable<(string Model, Matrix4x4 Transform)> models, Func<string, byte[]?> read,
        Dictionary<string, ThumbnailTexture?>? textures = null)
    {
        textures ??= [];
        var materials = new Dictionary<string, (ThumbnailTexture? Texture, Vector3 Colour, float Cut, bool Skip)>(StringComparer.Ordinal);
        var result = new List<ThumbnailMesh>();
        foreach (var (path, transform) in models)
        {
            IReadOnlyList<ModelMeshes.Geometry> meshes;
            try { meshes = read(path) is { } mdl ? ModelMeshes.ReadGeometry(mdl) : []; }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException) { continue; }
            foreach (var mesh in meshes)
            {
                var look = mesh.Material is { } material ? materials.TryGetValue(material, out var known) ? known : materials[material] = Look(material, read, textures)
                    : (null, new Vector3(.7f), 0, false);
                if (look.Skip) continue;
                result.Add(new(mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, look.Texture, look.Colour, look.Cut, transform));
            }
        }
        return result;
    }

    /// <summary>The thumbnail (RGBA, <see cref="ThumbnailRenderer.Size"/>²), or null when nothing in it can be drawn.</summary>
    public static byte[]? Render(IEnumerable<(string Model, Matrix4x4 Transform)> models, Func<string, byte[]?> read, Dictionary<string, ThumbnailTexture?>? textures = null)
        => ThumbnailRenderer.Render(Meshes(models, read, textures));

    // How a material looks in a picture: its colour texture (the colour map a background shader samples, else a
    // character's diffuse, else the first texture that isn't a normal, specular or mask map), its tint, its alpha cut,
    // or that it draws no surface (a light shaft's or fog's); water is drawn plain blue.
    private static (ThumbnailTexture? Texture, Vector3 Colour, float Cut, bool Skip) Look(string material, Func<string, byte[]?> read, Dictionary<string, ThumbnailTexture?> textures)
    {
        try
        {
            if (read(material) is not { } mtrl) return (null, new(.7f), 0, false);
            if (Invisible(material, mtrl)) return (null, default, 0, true);
            var shader = FurnitureFiles.MaterialShader(mtrl);
            if (Unseen.Any(s => shader.Contains(s, StringComparison.Ordinal))) return (null, default, 0, true);
            if (Water.Any(s => shader.Contains(s, StringComparison.Ordinal))) return (null, new(.33f, .52f, .66f), 0, false);
            var samplers = FurnitureFiles.MaterialSamplers(mtrl);
            var texture = samplers.Where(s => s.Id == ColourMap).Concat(samplers.Where(s => s.Id == Diffuse)).Select(s => s.Texture).FirstOrDefault()
                ?? FurnitureFiles.MaterialTextures(mtrl).FirstOrDefault(t => !IsDetailMap(t));
            var colour = Vector3.One;
            if (FurnitureFiles.MaterialConstant(mtrl, DiffuseColour) is [var r, var g, var b, ..] && float.IsFinite(r + g + b) && r + g + b > 0)
                colour = Vector3.Clamp(new Vector3(MathF.Sqrt(Math.Max(0, r)), MathF.Sqrt(Math.Max(0, g)), MathF.Sqrt(Math.Max(0, b))), Vector3.Zero, new(1.5f));
            var cut = FurnitureFiles.MaterialConstant(mtrl, AlphaThreshold) is [var threshold, ..] && threshold is > 0 and < 1 ? threshold : 0;
            return (texture == null ? null : Texture(texture, read, textures), texture == null ? colour * .7f : colour, cut, false);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException) { return (null, new(.7f), 0, false); }
    }

    /// <summary>
    /// Whether a material is one the game draws nothing with: a collision surface's (bgcommon/collision/material, the
    /// invisible walls and ramps a zone's layout keeps beside what's seen) or a placeholder's, whose every texture is the
    /// game's blank dummy or a collision texture (tinted yellow, tan or grey in the files, but never drawn).
    /// </summary>
    public static bool Invisible(string material, byte[] mtrl)
    {
        if (material.StartsWith("bgcommon/collision/", StringComparison.Ordinal)) return true;
        var textures = FurnitureFiles.MaterialTextures(mtrl);
        return textures.Count > 0 && textures.All(t => t.StartsWith("bgcommon/texture/dummy", StringComparison.Ordinal) || t.StartsWith("bgcommon/collision/", StringComparison.Ordinal));
    }

    private static bool IsDetailMap(string texture)
    {
        var name = Path.GetFileNameWithoutExtension(texture);
        return name.EndsWith("_n", StringComparison.Ordinal) || name.EndsWith("_s", StringComparison.Ordinal) || name.EndsWith("_m", StringComparison.Ordinal)
            || name.Contains("_norm", StringComparison.Ordinal) || name.Contains("_mask", StringComparison.Ordinal) || name.Contains("dummy", StringComparison.Ordinal);
    }

    private static ThumbnailTexture? Texture(string path, Func<string, byte[]?> read, Dictionary<string, ThumbnailTexture?> textures)
    {
        if (textures.TryGetValue(path, out var known)) return known;
        ThumbnailTexture? texture = null;
        try
        {
            if (read(path) is { } tex && TextureDecoding.Readable(tex))
            {
                var (rgba, width, height) = TextureDecoding.Decode(tex, TextureSide);
                texture = new(rgba, width, height);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException) { texture = null; }
        return textures[path] = texture;
    }
}

/// <summary>
/// Whether a world model has anything to see. Many of the game's world models hold only a light shaft (the glow of a
/// sunbeam), a fog volume or a shadow caster: drawn in a home they show nothing, so they aren't offered. Counted from
/// the model's first level of detail: its ordinary and water meshes and its extra glass, dye and crest meshes.
/// </summary>
public static class WorldModels
{
    public static bool HasSurface(int meshes, int water, int extra) => meshes + water + extra > 0;
}
