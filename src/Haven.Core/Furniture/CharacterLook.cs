using System.Buffers.Binary;
using System.Numerics;

namespace Haven.Core.Furniture;

/// <summary>
/// What a character material's colour table does to each texel, for a minion prop drawn with a scenery material:
/// the tint it multiplies the colour texture by, the glow it adds and, for older tables, its shine. A Dawntrail table
/// has 32 rows of 32 halves, an older one 16 rows of 16; both keep diffuse at halves 0-2 and emissive at 8-10, and an
/// older row also keeps its specular strength (3), specular colour (4-6) and gloss (7, a Blinn-Phong power). The row
/// comes from the index texture (red picks a pair of rows, red·15/255; green blends them, 0 being the pair's second
/// row), or, without one, from the normal map's alpha (alpha·15/255, blended with the next row).
/// </summary>
public sealed class CharacterLook
{
    private const uint SamplerNormal = 0x0C5EC1F1, SamplerIndex = 0x565F8FD8, SamplerSpecular = 0x2B99E025;
    /// <summary>The emissive strength (largest channel) above which a texel counts as glowing.</summary>
    public const float GlowThreshold = 0.05f;
    // Specular (strength × colour brightness) above which an older row shines.
    private const float ShineThreshold = 0.02f;

    private readonly record struct Row(Vector3 Diffuse, Vector3 Emissive, Vector3 Specular, float Strength, float Gloss)
    {
        public static Row Lerp(Row a, Row b, float t) => new(Vector3.Lerp(a.Diffuse, b.Diffuse, t), Vector3.Lerp(a.Emissive, b.Emissive, t),
            Vector3.Lerp(a.Specular, b.Specular, t), a.Strength + (b.Strength - a.Strength) * t, a.Gloss + (b.Gloss - a.Gloss) * t);
    }

    private readonly Row[] rows;
    private readonly bool older;
    private readonly byte[] selector;
    private readonly int width, height;
    private readonly bool index;
    private readonly (byte[] Rgba, int Width, int Height)? specularMap;

    private CharacterLook(Row[] rows, bool older, byte[] selector, int width, int height, bool index, (byte[], int, int)? specularMap)
    {
        (this.rows, this.older, this.selector, this.width, this.height, this.index, this.specularMap) = (rows, older, selector, width, height, index, specularMap);
        // Only the rows the selector reaches count: unused rows are often left empty.
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var row = Texel(x, y);
                Tints |= Math.Abs(row.Diffuse.X - 1) > 0.004f || Math.Abs(row.Diffuse.Y - 1) > 0.004f || Math.Abs(row.Diffuse.Z - 1) > 0.004f;
                Glows |= Max(row.Emissive) > GlowThreshold;
                Shines |= older && row.Strength * Luminance(row.Specular) > ShineThreshold;
            }
    }

    /// <summary>Whether any texel's row tints the colour texture (a diffuse other than white).</summary>
    public bool Tints { get; }
    /// <summary>Whether any texel's row glows.</summary>
    public bool Glows { get; }
    /// <summary>Whether an older table gives any texel a specular highlight, so the prop needs a specular map of its own.</summary>
    public bool Shines { get; }

    /// <summary>
    /// The material's colour table and its row selector (the index texture, or the normal map), or null when it has
    /// no table or the selector can't be read. <paramref name="texture"/> gives a texture's bytes by game path.
    /// </summary>
    public static CharacterLook? Read(byte[] mtrl, Func<string, byte[]?> texture)
    {
        var (table, size, _) = FurnitureFiles.MaterialLayout(mtrl);
        var dataSet = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(6));
        var at = table + size + mtrl[15];
        int count, stride;
        if (dataSet >= 2048) (count, stride) = (32, 32);
        else if (dataSet >= 512) (count, stride) = (16, 16);
        else return null;
        if (at + count * stride * 2 > mtrl.Length) return null;
        var older = count == 16;
        float H(int row, int i) => (float)BinaryPrimitives.ReadHalfLittleEndian(mtrl.AsSpan(at + (row * stride + i) * 2));
        var rows = new Row[count];
        for (var r = 0; r < count; r++)
            rows[r] = new(Finite(new(H(r, 0), H(r, 1), H(r, 2))), Finite(new(H(r, 8), H(r, 9), H(r, 10))),
                older ? Finite(new(H(r, 4), H(r, 5), H(r, 6))) : Vector3.Zero, older ? Clean(H(r, 3)) : 0, older ? Clean(H(r, 7)) : 0);
        var samplers = FurnitureFiles.MaterialSamplers(mtrl);
        string? Sampler(uint id) => samplers.Where(s => s.Id == id).Select(s => s.Texture).FirstOrDefault();
        var indexPath = Sampler(SamplerIndex);
        var path = indexPath ?? Sampler(SamplerNormal) ?? FurnitureFiles.CharacterTextures(mtrl).Normal;
        if (path == null || texture(path) is not { } bytes || !TextureDecoding.Readable(bytes)) return null;
        var (rgba, width, height) = TextureDecoding.Decode(bytes);
        var specularPath = older ? Sampler(SamplerSpecular) ?? FurnitureFiles.CharacterTextures(mtrl).Specular : null;
        var specular = specularPath != null && texture(specularPath) is { } map && TextureDecoding.Readable(map) ? TextureDecoding.Decode(map) : ((byte[], int, int)?)null;
        return new(rows, older, rgba, width, height, indexPath != null, specular);
    }

    private static Vector3 Finite(Vector3 v) => new(Clean(v.X), Clean(v.Y), Clean(v.Z));
    private static float Clean(float f) => float.IsFinite(f) ? Math.Clamp(f, 0, 64) : 0;
    private static float Max(Vector3 v) => Math.Max(v.X, Math.Max(v.Y, v.Z));
    private static float Luminance(Vector3 v) => 0.299f * v.X + 0.587f * v.Y + 0.114f * v.Z;

    /// <summary>The tint and glow at a texture coordinate (wrapping), from the selector's nearest texel.</summary>
    public (Vector3 Tint, Vector3 Glow) At(float u, float v)
    {
        var row = Texel(Wrap(u, width), Wrap(v, height));
        return (row.Diffuse, row.Emissive);
    }

    private Row Texel(int x, int y)
    {
        var p = (y * width + x) * 4;
        int a, b; float blend;
        if (index)
        {
            var pair = Math.Min(15, (selector[p] * 15 + 127) / 255);
            (a, b, blend) = (pair * 2 + 1, pair * 2, selector[p + 1] / 255f);      // green 0: the second row
        }
        else
        {
            var row = selector[p + 3] * 15 / 255f;
            a = (int)row; b = a + 1; blend = row - a;
        }
        a = Math.Min(a, rows.Length - 1); b = Math.Min(b, rows.Length - 1);
        return Row.Lerp(rows[a], rows[b], blend);
    }

    private static int Wrap(float t, int size)
    {
        if (!float.IsFinite(t)) return 0;
        var f = t - MathF.Floor(t);
        return Math.Clamp((int)(f * size), 0, size - 1);
    }

    /// <summary>
    /// The colour texture with each texel's tint applied (the table's linear tint as a factor on sRGB colour, √tint),
    /// at the colour texture's size, or the selector's when there is no colour texture (plain white then).
    /// </summary>
    public (byte[] Rgba, int Width, int Height) Tinted(byte[]? colour) => Baked(colour).Colour;

    /// <summary>
    /// The colour texture and, for a table that <see cref="Shines"/>, a scenery specular map, at the colour texture's
    /// size. Scenery shading is physically based: the lighting pass (directionallighting.shpk, read from its compiled
    /// shader) takes the specular map's green as roughness and its blue as metalness, with base reflectance
    /// lerp(0.04, albedo², metalness), and bg.shpk multiplies the glow by its alpha. An older character table is
    /// Blinn-Phong instead: a specular colour and strength (scaled by the specular map's red) over the diffuse, with a
    /// gloss power. So: roughness = √√(2 / (gloss + 2)); a dark, glossy texel under a strong specular
    /// becomes metal: full below a linear diffuse brightness of 0.03, none above 0.15 (a bright colour with white highlights
    /// is glossy paint, not metal), scaled by the specular up to 0.5 and faded in between gloss 8 and 20; and as a metal
    /// reflects its colour, its colour moves towards the specular colour by that much. Red keeps the specular
    /// strength, alpha is 255.
    /// </summary>
    public ((byte[] Rgba, int Width, int Height) Colour, byte[]? Specular) Baked(byte[]? colour)
    {
        byte[] rgba; int w, h;
        if (colour != null && TextureDecoding.Readable(colour)) (rgba, w, h) = TextureDecoding.Decode(colour);
        else { (w, h) = (width, height); rgba = new byte[w * h * 4]; Array.Fill(rgba, (byte)255); }
        var specular = Shines ? new byte[w * h * 4] : null;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var row = Texel(Math.Min(width - 1, x * width / w), Math.Min(height - 1, y * height / h));
                var p = (y * w + x) * 4;
                var albedo = new Vector3(Scale(rgba[p], row.Diffuse.X), Scale(rgba[p + 1], row.Diffuse.Y), Scale(rgba[p + 2], row.Diffuse.Z));
                if (specular != null)
                {
                    var mask = specularMap is var (map, mw, mh) ? map[(Math.Min(mh - 1, y * mh / h) * mw + Math.Min(mw - 1, x * mw / w)) * 4] / 255f : 1;
                    var strength = row.Strength * mask;
                    var shine = strength * Luminance(row.Specular);
                    var linear = albedo / 255f; linear *= linear;
                    var metal = shine > ShineThreshold ? Math.Clamp((0.15f - Luminance(linear)) / 0.12f, 0, 1) * Math.Clamp(shine / 0.5f, 0, 1) * Math.Clamp((row.Gloss - 8) / 12, 0, 1) : 0;
                    var reflection = new Vector3(MathF.Sqrt(Math.Clamp(row.Specular.X * strength, 0, 1)), MathF.Sqrt(Math.Clamp(row.Specular.Y * strength, 0, 1)), MathF.Sqrt(Math.Clamp(row.Specular.Z * strength, 0, 1))) * 255;
                    albedo = Vector3.Lerp(albedo, reflection, metal);
                    var roughness = MathF.Sqrt(MathF.Sqrt(2 / (Math.Max(row.Gloss, 0) + 2)));
                    specular[p] = Byte(Math.Min(1, strength) * 255); specular[p + 1] = Byte(roughness * 255); specular[p + 2] = Byte(metal * 255); specular[p + 3] = 255;
                }
                rgba[p] = Byte(albedo.X); rgba[p + 1] = Byte(albedo.Y); rgba[p + 2] = Byte(albedo.Z);
            }
        return ((rgba, w, h), specular);
    }

    private static float Scale(byte value, float tint) => value * MathF.Sqrt(tint);
    private static byte Byte(float value) => (byte)Math.Clamp(MathF.Round(value), 0, 255);
}
