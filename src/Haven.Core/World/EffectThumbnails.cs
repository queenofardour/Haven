using System.Buffers.Binary;
using System.Numerics;
using Haven.Core.Animation;
using Haven.Core.Furniture;

namespace Haven.Core.World;

/// <summary>
/// Where an effect's drawn part is, at scale 1 (metres): Span, the width and height of the view its picture is framed
/// on; Low and High, the same along the effect's own axes (Y up). All but the faintest few hundredths of what's seen
/// falls within.
/// </summary>
public readonly record struct EffectExtent(Vector2 Span, Vector3 Low, Vector3 High)
{
    /// <summary>Its largest side, in the picture or along an axis.</summary>
    public float Size => MathF.Max(MathF.Max(Span.X, Span.Y), MathF.Max(High.X - Low.X, MathF.Max(High.Y - Low.Y, High.Z - Low.Z)));
}

/// <summary>
/// A world effect's thumbnail: the effect file (.avfx) read for what it
/// draws and drawn by a small software renderer, so the offline tool and the plugin's background thread draw the same
/// picture, from <see cref="ThumbnailRenderer"/>'s view, at its size, on a transparent background (row by row from the
/// top, RGBA): glows and additive light become colour with as much alpha as they are bright, so over a dark tile they
/// read as they would in the dark.
/// <para>
/// What is drawn: each emitter the effect's timelines start (and the emitters they start in turn), placed by its own
/// position, rotation and scale, its particles spread as it spreads them (a point; a cone; a sphere, cylinder or ring of
/// a radius; an emitter model's points) and moved as they move (their speed out of the emitter, gravity, their position
/// curve), as a snapshot of the particles alive at once (a long exposure when there are few and they move). A particle
/// is its colour texture (TC1) tinted by its colour, brightness and alpha curves and the emitter's colour, cut by its
/// other textures (TC2-TC4) as masks, a flipbook showing its first cell; a model particle is its model with the
/// model's own colours and UVs; a decal or disc lies flat; anything else faces the camera as a soft sprite. Render mode
/// 0 blends by alpha (gathered as a density, thin smoke brought up to a shape), the others add light (inferred).
/// Framed on where most of what's seen is; exposure from the bright end, a soft shoulder.
/// </para>
/// Files come through a reader (the plugin's IDataManager, the tool's Lumina). Every count and size is bounded, and
/// nothing a broken file holds escapes: <see cref="Render"/> returns null when nothing draws.
/// </summary>
public static class EffectThumbnails
{
    public const int Size = ThumbnailRenderer.Size;
    private const int Supersample = 2, TextureSide = 128;
    private const int MaxEmitters = 256, MaxParticles = 256, MaxTextures = 256, MaxModels = 256, MaxKeys = 256, MaxItems = 64;
    private const int MaxCopies = 24, MaxDraws = 2000, MaxTriangles = 400_000, MaxChildDepth = 3, MaxEmits = 4096, MaxModelVertices = 65536;
    private const long MaxShaded = 60_000_000; // samples shaded for one picture, at most (a file can't make it draw for long)
    private const float MaxDistance = 100_000;

    /// <summary>
    /// The effect's thumbnail (RGBA, <see cref="Size"/>²), or null when nothing in it draws (no textured particle, its
    /// textures unreadable, or too little left to see).
    /// </summary>
    /// <param name="colour">The colour it is placed with (RGB and alpha, 0-1; up to 4 each), or null for none.</param>
    /// <param name="textures">Textures decoded so far, by path (shared across effects by a caller drawing many; not thread-safe).</param>
    public static byte[]? Render(string avfxPath, Func<string, byte[]?> read, float[]? colour = null, Dictionary<string, ThumbnailTexture?>? textures = null)
        => Render(avfxPath, read, out _, colour, textures);

    /// <summary>
    /// The effect's thumbnail, and where what it draws is (<see cref="EffectExtent"/>): measured from what shows, not
    /// the file's declared bounds (which often reach far past it, or say nothing). Empty when nothing draws.
    /// </summary>
    public static byte[]? Render(string avfxPath, Func<string, byte[]?> read, out EffectExtent extent, float[]? colour = null, Dictionary<string, ThumbnailTexture?>? textures = null)
    {
        extent = default;
        try
        {
            if (read(avfxPath) is not { } file || AvfxNode.Parse(file) is not { } root) return null;
            var effect = Effect.Read(root);
            if (effect == null) return null;
            var tint = Vector4.One;
            if (colour is { Length: >= 3 } c && c.Take(Math.Min(4, c.Length)).All(float.IsFinite))
                tint = new(Math.Clamp(c[0], 0, 4), Math.Clamp(c[1], 0, 4), Math.Clamp(c[2], 0, 4), c.Length >= 4 ? Math.Clamp(c[3], 0, 4) : 1);
            var picture = Picture(effect, read, textures ?? new Dictionary<string, ThumbnailTexture?>(StringComparer.Ordinal), tint, Seed(avfxPath), out var framed);
            if (picture != null) extent = framed;
            return picture;
        }
        catch (Exception) { extent = default; return null; }
    }

    /// <summary>What an effect's file reads as, in a line (for the offline tool, to see why a thumbnail looks as it does).</summary>
    public static string Describe(string avfxPath, Func<string, byte[]?> read)
    {
        try
        {
            if (read(avfxPath) is not { } file) return $"{avfxPath}: missing";
            if (AvfxNode.Parse(file) is not { } root || Effect.Read(root) is not { } effect) return $"{avfxPath}: not an effect";
            var textures = new Dictionary<string, ThumbnailTexture?>(StringComparer.Ordinal);
            var builder = new Builder(effect, Seed(avfxPath));
            foreach (var r in effect.Roots) builder.Emit(r, Matrix4x4.Identity, Vector3.One, 0);
            var line = new System.Text.StringBuilder($"{avfxPath}: roots {string.Join(",", effect.Roots)}");
            for (var i = 0; i < effect.Emitters.Length; i++)
            {
                var e = effect.Emitters[i];
                line.Append($" | E{i} type {e.Type} speed {e.Speed:G3} radius {e.Radius:G3} count {e.CreateCount:G3}/{e.Interval:G3} pos {e.Position.At(0)} particles {string.Join(",", e.Particles.Select(p => p.Target))} emitters {string.Join(",", e.Emitters.Select(p => p.Target))}");
            }
            for (var i = 0; i < effect.Particles.Length; i++)
            {
                var p = effect.Particles[i];
                var draws = builder.Draws.Where(d => d.Particle == p).ToArray();
                var texture = p.Colour == null ? "no TC1" : Texture(p.Colour.Texture, read, textures) is { } t ? $"{System.IO.Path.GetFileName(p.Colour.Texture)} {t.Width}x{t.Height}" : $"{System.IO.Path.GetFileName(p.Colour.Texture)} unreadable";
                line.Append($" | P{i} type {p.Type} mode {p.Mode} life {p.Life:G3} model {p.Model} scale {p.Scale.At(0)} {texture} masks {p.Masks.Length} draws {draws.Length}"
                    + (draws.Length > 0 ? $" alpha {draws.Max(d => d.Alpha):G2} colour {draws[0].Colour}" : ""));
            }
            return line.ToString();
        }
        catch (Exception ex) { return $"{avfxPath}: {ex.GetType().Name} {ex.Message}"; }
    }

    // The effect as read --------------------------------------------------------------------------------------------

    /// <summary>A curve's keys: frame, interpolation (0 holds), value (x, y, z; a scalar curve's value is z).</summary>
    private sealed class Curve
    {
        private readonly (float Time, int Type, Vector3 Value)[] keys;
        private Curve((float, int, Vector3)[] keys) { this.keys = keys; }

        public static Curve? Of(AvfxNode? node)
        {
            if (node?["Keys"] is not { } k || k.Length < 16) return null;
            var data = k.Data;
            var count = Math.Min(MaxKeys, data.Length / 16);
            var keys = new (float, int, Vector3)[count];
            for (var i = 0; i < count; i++)
            {
                var at = data.Slice(i * 16, 16);
                var value = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(at[4..]), BinaryPrimitives.ReadSingleLittleEndian(at[8..]), BinaryPrimitives.ReadSingleLittleEndian(at[12..]));
                keys[i] = (BinaryPrimitives.ReadUInt16LittleEndian(at), BinaryPrimitives.ReadUInt16LittleEndian(at[2..]), Sane(value));
            }
            return new(keys);
        }

        public Vector3 At(float t)
        {
            if (t <= keys[0].Time || keys.Length == 1) return keys[0].Value;
            for (var i = 0; i + 1 < keys.Length; i++)
            {
                var (t0, type, v0) = keys[i]; var (t1, _, v1) = keys[i + 1];
                if (t >= t1) continue;
                if (type == 0 || t1 <= t0) return v0;
                return Vector3.Lerp(v0, v1, (t - t0) / (t1 - t0));
            }
            return keys[^1].Value;
        }

        public float Scalar(float t) => At(t).Z;
        public bool Varies => keys.Any(k => Math.Abs(k.Value.Z - keys[0].Value.Z) > 1e-4f);
        public float Max => keys.Max(k => k.Value.Z);
        public float Span => keys[^1].Time;
        public Vector3 Mean => keys.Aggregate(Vector3.Zero, (a, k) => a + k.Value) / keys.Length;
    }

    /// <summary>Three axis curves (X, Y, Z, with their random ranges XR, YR, ZR), an axis missing following X when they are connected.</summary>
    private sealed class Axes
    {
        private readonly Curve? x, y, z, xr, yr, zr;
        private readonly bool connected;
        private readonly float rest;

        private Axes(AvfxNode? node, float rest)
        {
            this.rest = rest;
            x = Curve.Of(node?["X"]); y = Curve.Of(node?["Y"]); z = Curve.Of(node?["Z"]);
            xr = Curve.Of(node?["XR"]); yr = Curve.Of(node?["YR"]); zr = Curve.Of(node?["ZR"]);
            connected = (node?["ACT"]?.Int ?? 0) != 0;
        }

        public static Axes Of(AvfxNode? node, float rest) => new(node, rest);

        public Vector3 At(float t)
        {
            var vx = x?.Scalar(t) ?? rest;
            return new(vx, y?.Scalar(t) ?? (connected ? vx : rest), z?.Scalar(t) ?? (connected ? vx : rest));
        }

        public Vector3 Random(float t)
        {
            var vx = xr?.Scalar(t) ?? 0;
            return new(vx, yr?.Scalar(t) ?? (connected ? vx : 0), zr?.Scalar(t) ?? (connected ? vx : 0));
        }

        public bool Varies => (x?.Varies ?? false) || (y?.Varies ?? false) || (z?.Varies ?? false);
        public bool Scatters => Random(0) != Vector3.Zero;
        public float Span => Math.Max(x?.Span ?? 0, Math.Max(y?.Span ?? 0, z?.Span ?? 0));
    }

    private sealed record Layer(string Texture, Vector2 Scale, float Rotation, int UvSet);

    private sealed class Particle
    {
        public int Type, Mode;
        public float Life, Gravity, Resistance = 1;
        public Axes Position = null!, Scale = null!, Rotation = null!;
        public Curve? Rgb, Alpha, Brightness;
        public Layer? Colour;
        public Layer[] Masks = [];
        public int Model = -1;
        public bool Moves => Position.Varies || Gravity != 0;
        public bool Flat => Type is 10 or 11 or 12;
    }

    private sealed record Item(int Target, float Count, float Life);

    private sealed class Emitter
    {
        public int Type, Model = -1;
        public float Life, CreateCount = 1, Interval = 1, Gravity, Speed, Radius, Length, Angle;
        public Axes Position = null!, Rotation = null!, Scale = null!;
        public Vector3 Colour = Vector3.One;
        public Item[] Particles = [], Emitters = [];
        public bool Spreads => Type is >= 1 and <= 5 || Position.Scatters;
    }

    private sealed record Model(Vector3[] Positions, Vector4[] Colours, Vector2[][] Uvs, int[] Indices, Vector3[] Points, Vector3[] Normals);

    private sealed class Effect
    {
        public string[] Textures = [];
        public Model?[] Models = [];
        public Emitter[] Emitters = [];
        public Particle[] Particles = [];
        public int[] Roots = [];

        public static Effect? Read(AvfxNode root)
        {
            var effect = new Effect
            {
                Textures = [.. root.All("Tex").Take(MaxTextures).Select(t => t.Text() ?? "")],
                Models = [.. root.All("Modl").Take(MaxModels).Select(ReadModel)],
            };
            effect.Particles = [.. root.All("Ptcl").Take(MaxParticles).Select(p => ReadParticle(p, effect.Textures))];
            effect.Emitters = [.. root.All("Emit").Take(MaxEmitters).Select(ReadEmitter)];
            if (effect.Emitters.Length == 0 && effect.Particles.Length == 0) return null;
            // The emitters the timelines start; failing those, every emitter no other emitter starts.
            var started = new List<int>();
            foreach (var timeline in root.All("TmLn"))
                foreach (var item in timeline.All("Item"))
                    if ((item["bEna"]?.Int ?? 1) != 0 && item["EmNo"]?.Int is int e and >= 0 && e <effect.Emitters.Length && !started.Contains(e)) started.Add(e);
            if (started.Count == 0)
            {
                var children = effect.Emitters.SelectMany(m => m.Emitters).Select(i => i.Target).ToHashSet();
                started.AddRange(Enumerable.Range(0, effect.Emitters.Length).Where(i => !children.Contains(i)));
            }
            effect.Roots = [.. started];
            return effect;
        }

        private static Particle ReadParticle(AvfxNode p, string[] textures)
        {
            var colour = p["Col"];
            var particle = new Particle
            {
                Type = p["PrVT"]?.Int ?? -1, Mode = p["RMT"]?.Int ?? 0, Life = Finite(p["Life"]?["Val"]?.Float ?? -1, -1),
                Gravity = Curve.Of(p["Gra"])?.Scalar(0) ?? 0, Resistance = Math.Clamp(Curve.Of(p["ARs"])?.Scalar(0) ?? 1, 0, 1),
                Position = Axes.Of(p["Pos"], 0), Scale = Axes.Of(p["Scl"], 1), Rotation = Axes.Of(p["Rot"], 0),
                Rgb = Curve.Of(colour?["RGB"]), Alpha = Curve.Of(colour?["A"]), Brightness = Curve.Of(colour?["Bri"]),
            };
            if (p["Data"] is { } data) particle.Model = particle.Type switch { 5 => data["MdNo"]?.Int ?? -1, 13 => data["MNO"]?.Int ?? -1, _ => -1 };
            var sets = p.All("UvSt").Take(8).ToArray();
            // A flipbook (the simple animation's cell counts) shows its first cell.
            var cells = p["Smpl"] is { } simple ? new Vector2(Math.Clamp(simple["UvCU"]?.Int ?? 1, 1, 16), Math.Clamp(simple["UvCV"]?.Int ?? 1, 1, 16)) : Vector2.One;
            Layer? LayerOf(AvfxNode? t)
            {
                if (t == null || (t["bEna"]?.Int ?? 0) == 0) return null;
                var index = t["TLst"] is { Length: > 0 } list ? list.Data[0] : t["TxNo"]?.Int ?? -1;
                if (index < 0 || index >= textures.Length || textures[index].Length == 0) return null;
                var set = Math.Clamp(t["UvSN"]?.Int ?? 0, 0, 3);
                var scale = Vector2.One; var rotation = 0f;
                if (set < sets.Length)
                {
                    var uv = Axes.Of(sets[set]["Scl"], 1).At(0);
                    if (Math.Abs(uv.X) is > 0.01f and < 64) scale.X = uv.X;
                    if (Math.Abs(uv.Y) is > 0.01f and < 64) scale.Y = uv.Y;
                    rotation = Finite(Curve.Of(sets[set]["Rot"])?.Scalar(0) ?? 0, 0);
                }
                return new(textures[index], scale / cells, rotation, set);
            }
            particle.Colour = LayerOf(p["TC1"]);
            particle.Masks = [.. new[] { "TC2", "TC3", "TC4" }.Select(n => LayerOf(p[n])).OfType<Layer>()];
            return particle;
        }

        private static Emitter ReadEmitter(AvfxNode e)
        {
            var data = e["Data"];
            var colour = e["Col"];
            var rgb = Curve.Of(colour?["RGB"])?.At(0) ?? Vector3.One;
            var bri = Curve.Of(colour?["Bri"])?.Scalar(0) ?? 1;
            var emitter = new Emitter
            {
                Type = e["EVT"]?.Int ?? 0, Life = Finite(e["Life"]?["Val"]?.Float ?? -1, -1),
                CreateCount = Math.Clamp(Curve.Of(e["CrC"])?.Scalar(0) ?? 1, 0, 1000), Interval = Math.Clamp(Curve.Of(e["CrI"])?.Scalar(0) ?? 1, 1, 100_000),
                Gravity = Curve.Of(e["Gra"])?.Scalar(0) ?? 0,
                Position = Axes.Of(e["Pos"], 0), Rotation = Axes.Of(e["Rot"], 0), Scale = Axes.Of(e["Scl"], 1),
                Colour = Vector3.Clamp(rgb, Vector3.Zero, new(4)) * Math.Clamp(bri, 0, 8),
                Particles = Items(e.All("ItPr")), Emitters = Items(e.All("ItEm")),
            };
            if (data != null)
            {
                emitter.Model = emitter.Type == 5 ? data["MdNo"]?.Int ?? -1 : -1;
                emitter.Speed = Math.Clamp(Curve.Of(data["IjS"])?.Scalar(0) ?? 0, -100, 100);
                emitter.Radius = Math.Clamp(Curve.Of(data["Rads"] ?? data["Rad"])?.Scalar(0) ?? 0, 0, 10_000);
                emitter.Length = Math.Clamp(Curve.Of(data["Len"])?.Scalar(0) ?? 0, 0, 10_000);
                emitter.Angle = Math.Clamp(Curve.Of(data["IjA"])?.Scalar(0) ?? 0, -360, 360);
            }
            return emitter;
        }

        // An emitter's item list: the format keeps a growing list (each block holds every item so far), so the largest;
        // each item starts at its "bEnb".
        private static Item[] Items(IEnumerable<AvfxNode> lists)
        {
            var list = lists.Where(l => l.Children != null).MaxBy(l => l.Children!.Count);
            if (list == null) return [];
            var items = new List<Item>();
            int target = -1; float count = 1, chance = 100, life = 0; bool enabled = false, started = false, overrides = false;
            void Flush()
            {
                if (started && enabled && target >= 0 && items.Count < MaxItems)
                    items.Add(new(target, Math.Clamp(count, 0, 100) * Math.Clamp(chance / 100, 0.05f, 1), overrides ? life : 0));
            }
            foreach (var field in list.Children!)
            {
                switch (field.Tag)
                {
                    case "bEnb": Flush(); started = true; enabled = field.Int != 0; target = -1; count = 1; chance = 100; overrides = false; life = 0; break;
                    case "TgtB": target = field.Int; break;
                    case "CrCn": count = field.Int; break;
                    case "CrPr": chance = field.Int; break;
                    case "bOvr": overrides = field.Int != 0; break;
                    case "OvrV": life = field.Int; break;
                }
            }
            Flush();
            return [.. items];
        }

        // A model: its drawn vertices (36 bytes: half4 position, normal, tangent, colour bytes, four half2 UV sets),
        // indices (u16), and the points an emitter model emits from (28 bytes: float3 position, float3 normal, colour).
        private static Model? ReadModel(AvfxNode m)
        {
            Vector3[] positions = [], points = [], normals = [];
            Vector4[] colours = [];
            var uvs = new Vector2[4][];
            int[] indices = [];
            if (m["VDrw"] is { Length: >= 36 } v)
            {
                var data = v.Data;
                var n = Math.Min(MaxModelVertices, data.Length / 36);
                positions = new Vector3[n]; colours = new Vector4[n];
                for (var s = 0; s < 4; s++) uvs[s] = new Vector2[n];
                for (var i = 0; i < n; i++)
                {
                    var at = data.Slice(i * 36, 36);
                    positions[i] = Sane(new((float)BinaryPrimitives.ReadHalfLittleEndian(at), (float)BinaryPrimitives.ReadHalfLittleEndian(at[2..]), (float)BinaryPrimitives.ReadHalfLittleEndian(at[4..])));
                    colours[i] = new Vector4(at[16], at[17], at[18], at[19]) / 255f;
                    for (var s = 0; s < 4; s++)
                    {
                        var uv = new Vector2((float)BinaryPrimitives.ReadHalfLittleEndian(at[(20 + s * 4)..]), (float)BinaryPrimitives.ReadHalfLittleEndian(at[(22 + s * 4)..]));
                        uvs[s][i] = float.IsFinite(uv.X) && float.IsFinite(uv.Y) ? uv : Vector2.Zero;
                    }
                }
                if (m["VIdx"] is { } ix)
                {
                    var count = Math.Min(ix.Length / 2, MaxModelVertices * 6) / 3 * 3;
                    indices = new int[count];
                    for (var i = 0; i < count; i++) indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(ix.Data[(i * 2)..]);
                }
            }
            else for (var s = 0; s < 4; s++) uvs[s] = [];
            if (m["VEmt"] is { Length: >= 28 } emit)
            {
                var data = emit.Data;
                var n = Math.Min(MaxModelVertices, data.Length / 28);
                points = new Vector3[n]; normals = new Vector3[n];
                for (var i = 0; i < n; i++)
                {
                    var at = data.Slice(i * 28, 28);
                    points[i] = Sane(new(BinaryPrimitives.ReadSingleLittleEndian(at), BinaryPrimitives.ReadSingleLittleEndian(at[4..]), BinaryPrimitives.ReadSingleLittleEndian(at[8..])));
                    var normal = Sane(new(BinaryPrimitives.ReadSingleLittleEndian(at[12..]), BinaryPrimitives.ReadSingleLittleEndian(at[16..]), BinaryPrimitives.ReadSingleLittleEndian(at[20..])));
                    normals[i] = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitY;
                }
            }
            else if (positions.Length > 0) { points = positions; normals = [.. positions.Select(_ => Vector3.UnitY)]; }
            return positions.Length == 0 && points.Length == 0 ? null : new(positions, colours, uvs, indices, points, normals);
        }
    }

    // What to draw ---------------------------------------------------------------------------------------------------

    /// <summary>One particle as drawn: where it is (its centre, or its model's or flat shape's placed vertices), its size, colour and alpha.</summary>
    private sealed class Draw
    {
        public required Particle Particle;
        public Vector3 Centre;
        public Vector2 Half;           // a sprite's half size
        public Vector3[]? Vertices;    // a model's or flat shape's placed vertices
        public Vector2[]? Uvs;
        public Vector4[]? Colours;
        public int[]? Indices;
        public Vector3 Colour;
        public float Alpha;
    }

    private struct Random(ulong seed)
    {
        private ulong state = seed == 0 ? 0x9E3779B97F4A7C15 : seed;
        public float Next()
        {
            state ^= state << 13; state ^= state >> 7; state ^= state << 17;
            return (state >> 40) / (float)(1 << 24);
        }
        public float Signed() => Next() * 2 - 1;
        public Vector3 Signed3() => new(Signed(), Signed(), Signed());
        public Vector3 Unit()
        {
            var z = Signed(); var a = Next() * MathF.Tau; var r = MathF.Sqrt(Math.Max(0, 1 - z * z));
            return new(r * MathF.Cos(a), z, r * MathF.Sin(a));
        }
    }

    private static ulong Seed(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text) { hash ^= c; hash *= 1099511628211UL; }
        return hash;
    }

    private static ulong Mix(ulong seed, int a, int b, int c) => Seed($"{seed:X}/{a}/{b}/{c}");

    private sealed class Builder(Effect effect, ulong seed)
    {
        public readonly List<Draw> Draws = [];
        private int triangles, calls;

        public void Emit(int index, Matrix4x4 parent, Vector3 colour, int depth)
        {
            if (index < 0 || index >= effect.Emitters.Length || depth > MaxChildDepth || Draws.Count >= MaxDraws || ++calls > MaxEmits) return;
            var e = effect.Emitters[index];
            var rotation = e.Rotation.At(0); var scale = Clamp(e.Scale.At(0), 1e-3f, 1000);
            var frame = Matrix4x4.CreateScale(scale) * Turn(rotation) * Matrix4x4.CreateTranslation(Clamp(e.Position.At(0), -MaxDistance, MaxDistance)) * parent;
            colour *= e.Colour;
            for (var i = 0; i < e.Particles.Length && Draws.Count < MaxDraws; i++)
            {
                var item = e.Particles[i];
                if (item.Target < 0 || item.Target >= effect.Particles.Length) continue;
                var p = effect.Particles[item.Target];
                if (p.Colour == null) continue;
                var life = item.Life > 0 ? item.Life : p.Life;
                var moves = p.Moves || e.Speed != 0 || e.Gravity != 0;
                var alive = e.CreateCount * item.Count * (life > 0 ? life / e.Interval : 1);
                int copies; float weight = 1;
                if (!moves && !e.Spreads && !p.Position.Scatters) copies = 1;
                else
                {
                    copies = Math.Clamp((int)MathF.Ceiling(alive), 1, MaxCopies);
                    // A long exposure of a few moving particles; a field of scattered ones (motes, embers) filled in.
                    if (moves && copies < 8) { weight = Math.Clamp(alive / 8, 0.15f, 1); copies = 8; }
                    if ((e.Spreads || p.Position.Scatters) && copies < 12) { weight = Math.Max(weight * copies / 12, 0.35f); copies = 12; }
                }
                var mesh = p.Model >= 0 && p.Model < effect.Models.Length ? effect.Models[p.Model] : null;
                if (mesh != null)
                {
                    var cost = Cost(mesh);
                    copies = Math.Min(copies, Math.Max(0, (MaxTriangles - triangles) / cost));
                    if (copies == 0) continue;
                }
                var span = life > 0 ? life : Math.Max(p.Position.Span, 60);
                for (var k = 0; k < copies && Draws.Count < MaxDraws; k++)
                {
                    var random = new Random(Mix(seed, index, i, k));
                    var age = moves ? (copies == 1 ? 0.5f : (k + random.Next()) / copies) * span : 0;
                    var (point, direction) = Shape(e, ref random);
                    point += e.Position.Random(0) * random.Signed3();
                    var travel = e.Speed * Travelled(age, p.Resistance);
                    var local = point + direction * travel + p.Position.At(age) + p.Position.Random(age) * random.Signed3();
                    var centre = Vector3.Transform(Clamp(local, -MaxDistance, MaxDistance), frame) + new Vector3(0, Math.Clamp(0.5f * (p.Gravity + e.Gravity) * age * age, -1000, 1000), 0);
                    Add(p, mesh, frame, centre, age, moves, colour, weight);
                }
            }
            // Child emitters start where their parent's particles are: a few of its points.
            if (depth < MaxChildDepth)
                for (var i = 0; i < e.Emitters.Length; i++)
                {
                    var spread = e.Spreads ? 3 : 1;
                    for (var k = 0; k < spread; k++)
                    {
                        var random = new Random(Mix(seed, index, 1000 + i, k));
                        var (point, _) = Shape(e, ref random);
                        Emit(e.Emitters[i].Target, Matrix4x4.CreateTranslation(point) * frame, colour, depth + 1);
                    }
                }
        }

        // What a model copy costs against the budget: its triangles, or its vertices (each copy places them all).
        private static int Cost(Model mesh) => Math.Max(1, Math.Max(mesh.Indices.Length / 3, mesh.Positions.Length));

        // Where an emitter puts a particle and which way it sends it, in the emitter's space.
        private (Vector3 Point, Vector3 Direction) Shape(Emitter e, ref Random random)
        {
            var angle = MathF.Abs(e.Angle) > MathF.Tau ? e.Angle * MathF.PI / 180 : e.Angle;
            switch (e.Type)
            {
                case 1: // a cone from a point
                {
                    var theta = random.Next() * angle; var phi = random.Next() * MathF.Tau;
                    return (Vector3.Zero, new(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi)));
                }
                case 2: // a ring, sent out at an angle
                {
                    var phi = random.Next() * MathF.Tau; var radial = new Vector3(MathF.Cos(phi), 0, MathF.Sin(phi));
                    return (radial * e.Radius, Vector3.Normalize(radial * MathF.Sin(angle) + Vector3.UnitY * MathF.Cos(angle)));
                }
                case 3: { var unit = random.Unit(); return (unit * e.Radius, unit); } // a sphere
                case 4: // a cylinder
                {
                    var phi = random.Next() * MathF.Tau; var radial = new Vector3(MathF.Cos(phi), 0, MathF.Sin(phi));
                    return (radial * e.Radius + Vector3.UnitY * random.Signed() * e.Length / 2, radial);
                }
                case 5 when e.Model >= 0 && e.Model < effect.Models.Length && effect.Models[e.Model] is { Points.Length: > 0 } model:
                {
                    var at = Math.Min(model.Points.Length - 1, (int)(random.Next() * model.Points.Length));
                    return (model.Points[at], model.Normals[at]);
                }
                default: return (Vector3.Zero, Vector3.UnitY);
            }
        }

        // How far a particle has gone at unit speed after age frames, slowed by air resistance (a factor a frame).
        private static float Travelled(float age, float resistance) =>
            resistance >= 0.9999f ? age : resistance <= 0 ? 1 : (1 - MathF.Pow(resistance, age)) / (1 - resistance);

        private void Add(Particle p, Model? mesh, Matrix4x4 frame, Vector3 centre, float age, bool moves, Vector3 colour, float weight)
        {
            // A still particle shows its look over its life (its fades would hide it at either end); a moving one, its look at its age.
            Vector3 rgb; float brightness, alpha;
            if (moves)
            {
                rgb = p.Rgb?.At(age) ?? Vector3.One; brightness = p.Brightness?.Scalar(age) ?? 1; alpha = p.Alpha?.Scalar(age) ?? 1;
            }
            else
            {
                rgb = p.Rgb?.Mean ?? Vector3.One; brightness = p.Brightness?.Max ?? 1; alpha = p.Alpha?.Max ?? 1;
            }
            var tint = Vector3.Clamp(rgb, Vector3.Zero, new(4)) * Math.Clamp(brightness, 0, 8) * colour;
            // A particle the game draws faint because many overlap (smoke, mist) is brought up a little, so it isn't
            // lost beside a bright one in a still picture.
            alpha *= Math.Clamp(0.35f / Math.Max(p.Alpha?.Max ?? 1, 1e-3f), 1, 4);
            alpha = Math.Clamp(alpha, 0, 1) * weight;
            if (alpha <= 1e-3f || tint == Vector3.Zero && p.Mode != 0) return;
            var scale = Clamp(p.Scale.At(moves ? age : 0), -1000, 1000);
            var turn = Turn(p.Rotation.At(moves ? age : 0));
            var orientation = frame with { M41 = 0, M42 = 0, M43 = 0 };
            var draw = new Draw { Particle = p, Centre = centre, Colour = tint, Alpha = alpha };
            if (mesh is { Positions.Length: > 0, Indices.Length: > 0 })
            {
                var place = Matrix4x4.CreateScale(scale) * turn * orientation * Matrix4x4.CreateTranslation(centre);
                draw.Vertices = [.. mesh.Positions.Select(v => Vector3.Transform(v, place))];
                draw.Uvs = mesh.Uvs[Math.Clamp(p.Colour!.UvSet, 0, 3)];
                draw.Colours = mesh.Colours;
                draw.Indices = mesh.Indices;
                triangles += Cost(mesh);
            }
            else if (p.Flat)
            {
                // A decal or disc lies on the ground: a square, or for a disc a ring, its texture around it.
                var place = Matrix4x4.CreateScale(scale) * turn * orientation * Matrix4x4.CreateTranslation(centre);
                if (p.Type == 12) (draw.Vertices, draw.Uvs, draw.Indices) = Ring(place);
                else
                {
                    draw.Vertices = [Vector3.Transform(new(-0.5f, 0, -0.5f), place), Vector3.Transform(new(0.5f, 0, -0.5f), place), Vector3.Transform(new(0.5f, 0, 0.5f), place), Vector3.Transform(new(-0.5f, 0, 0.5f), place)];
                    draw.Uvs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
                    draw.Indices = [0, 1, 2, 0, 2, 3];
                }
                triangles += draw.Indices.Length / 3;
            }
            else
            {
                var size = new Vector3(frame.M11, frame.M12, frame.M13).Length();
                draw.Half = new Vector2(Math.Abs(scale.X), Math.Abs(scale.Y)) * 0.5f * (float.IsFinite(size) && size > 0 ? size : 1);
                if (!(draw.Half.X > 1e-5f && draw.Half.Y > 1e-5f)) return;
            }
            Draws.Add(draw);
        }

        private static (Vector3[], Vector2[], int[]) Ring(Matrix4x4 place)
        {
            const int Segments = 32;
            var vertices = new Vector3[(Segments + 1) * 2]; var uvs = new Vector2[vertices.Length]; var indices = new int[Segments * 6];
            for (var s = 0; s <= Segments; s++)
            {
                var a = s * MathF.Tau / Segments; var radial = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                vertices[s * 2] = Vector3.Transform(radial * 0.25f, place); vertices[s * 2 + 1] = Vector3.Transform(radial * 0.5f, place);
                uvs[s * 2] = new((float)s / Segments, 0); uvs[s * 2 + 1] = new((float)s / Segments, 1);
                if (s == Segments) break;
                var o = s * 6; var b = s * 2;
                indices[o] = b; indices[o + 1] = b + 1; indices[o + 2] = b + 3; indices[o + 3] = b; indices[o + 4] = b + 3; indices[o + 5] = b + 2;
            }
            return (vertices, uvs, indices);
        }
    }

    // Drawing ------------------------------------------------------------------------------------------------------

    private static byte[]? Picture(Effect effect, Func<string, byte[]?> read, Dictionary<string, ThumbnailTexture?> textures, Vector4 tint, ulong seed, out EffectExtent measured)
    {
        measured = default;
        var builder = new Builder(effect, seed);
        foreach (var root in effect.Roots) builder.Emit(root, Matrix4x4.Identity, new Vector3(tint.X, tint.Y, tint.Z), 0);
        // Each draw's texture first: a particle whose colour texture can't be read draws nothing.
        var draws = builder.Draws.Where(d => Texture(d.Particle.Colour!.Texture, read, textures) != null).ToList();
        if (draws.Count == 0) return null;
        var (_, right, up) = ThumbnailRenderer.View();
        // Fitted to where most of what's seen is: each draw weighted by its alpha, size and brightness, the picture
        // framing all but the faintest few hundredths (a stray particle far off doesn't shrink the rest to a speck).
        var xs = new List<(float Value, float Weight)>(); var ys = new List<(float Value, float Weight)>();
        // The same, along the effect's own axes: where it is in its space (for setting it down on a floor).
        var axes = new[] { new List<(float Value, float Weight)>(), new List<(float Value, float Weight)>(), new List<(float Value, float Weight)>() };
        foreach (var d in draws)
        {
            var points = d.Vertices ?? [d.Centre];
            float lowX = float.MaxValue, highX = float.MinValue, lowY = float.MaxValue, highY = float.MinValue;
            foreach (var v in points)
            {
                float x = Vector3.Dot(v, right), y = Vector3.Dot(v, up);
                if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
                lowX = Math.Min(lowX, x - d.Half.X); highX = Math.Max(highX, x + d.Half.X); lowY = Math.Min(lowY, y - d.Half.Y); highY = Math.Max(highY, y + d.Half.Y);
            }
            if (lowX > highX) continue;
            var weight = d.Alpha * Math.Max((highX - lowX) * (highY - lowY), 1e-6f) * (d.Particle.Mode != 0 ? Math.Clamp(Math.Max(d.Colour.X, Math.Max(d.Colour.Y, d.Colour.Z)), 0.1f, 4) : 1);
            if (!(weight > 0) || !float.IsFinite(weight)) weight = 1e-6f;
            xs.Add((lowX, weight / 2)); xs.Add((highX, weight / 2)); ys.Add((lowY, weight / 2)); ys.Add((highY, weight / 2));
            var radius = Math.Max(d.Half.X, d.Half.Y);
            var low = new Vector3(float.MaxValue); var high = new Vector3(float.MinValue);
            foreach (var v in points)
                if (float.IsFinite(v.X + v.Y + v.Z)) { low = Vector3.Min(low, v - new Vector3(radius)); high = Vector3.Max(high, v + new Vector3(radius)); }
            if (low.X <= high.X)
                for (var a = 0; a < 3; a++) { axes[a].Add((low[a], weight / 2)); axes[a].Add((high[a], weight / 2)); }
        }
        if (xs.Count == 0) return null;
        static (float Low, float High) Range(List<(float Value, float Weight)> values)
        {
            if (values.Count == 0) return (0, 0);
            values.Sort((a, b) => a.Value.CompareTo(b.Value));
            var total = values.Sum(v => (double)v.Weight);
            double run = 0; float low = values[0].Value, high = values[^1].Value; var lowFound = false;
            foreach (var (value, weight) in values)
            {
                run += weight;
                if (!lowFound && run >= total * 0.03) { low = value; lowFound = true; }
                if (run >= total * 0.97) { high = value; break; }
            }
            return (low, Math.Max(high, low));
        }
        var (minX, maxX) = Range(xs); var (minY, maxY) = Range(ys);
        var extent = Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-3f);
        if (!float.IsFinite(extent)) return null;
        var (lowX3, highX3) = Range(axes[0]); var (lowY3, highY3) = Range(axes[1]); var (lowZ3, highZ3) = Range(axes[2]);
        measured = new(new(maxX - minX, maxY - minY), new(lowX3, lowY3, lowZ3), new(highX3, highY3, highZ3));
        float cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        const int n = Size * Supersample;
        var scale = n * 0.86f / extent;
        Vector2 Screen(Vector3 p) => new((Vector3.Dot(p, right) - cx) * scale + n / 2f, n / 2f - (Vector3.Dot(p, up) - cy) * scale);
        // Added light, and alpha-blended parts as a density with its colour (so their order doesn't matter, and faint
        // smoke that builds up in the game can be brought up to a shape).
        var light = new Vector3[n * n];
        var density = new float[n * n];
        var paint = new Vector3[n * n];
        long visited = 0;
        foreach (var d in draws)
        {
            if (visited > MaxShaded) break;
            var p = d.Particle;
            var texture = Texture(p.Colour!.Texture, read, textures)!;
            var masks = p.Masks.Select(m => (Texture: Texture(m.Texture, read, textures), Layer: m)).Where(m => m.Texture != null).ToArray();
            var additive = p.Mode != 0;
            void Shade(int at, Vector2 uv, Vector4 vertex, float soft)
            {
                var texel = Sample(texture, uv, p.Colour!);
                var alpha = texel.W * d.Alpha * vertex.W * soft;
                foreach (var (mask, layer) in masks) { var m = Sample(mask!, uv, layer); alpha *= m.W * Math.Max(m.X, Math.Max(m.Y, m.Z)); }
                if (alpha <= 1e-4f) return;
                var src = new Vector3(texel.X, texel.Y, texel.Z) * d.Colour * new Vector3(vertex.X, vertex.Y, vertex.Z);
                if (additive) light[at] += src * alpha;
                else { alpha = Math.Min(alpha, 1); density[at] += alpha; paint[at] += src * alpha; }
            }
            if (d.Vertices is { } vertices)
            {
                var screen = vertices.Select(Screen).ToArray();
                var uvs = d.Uvs!; var colours = d.Colours;
                var indices = d.Indices!;
                for (var t = 0; t + 2 < indices.Length && visited <= MaxShaded; t += 3)
                {
                    int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                    if ((uint)a >= (uint)screen.Length || (uint)b >= (uint)screen.Length || (uint)c >= (uint)screen.Length) continue;
                    visited += Triangle(screen[a], screen[b], screen[c], n, (w0, w1, w2, at) =>
                    {
                        var uv = Uv(uvs, a, b, c, w0, w1, w2);
                        var vertex = colours is { } k && a < k.Length && b < k.Length && c < k.Length ? k[a] * w0 + k[b] * w1 + k[c] * w2 : Vector4.One;
                        Shade(at, uv, vertex, 1);
                    });
                }
            }
            else
            {
                // A sprite faces the camera: a square on screen, soft towards its edge (many effect textures are square clouds).
                var centre = Screen(d.Centre);
                // A mote or a streak stays a few pixels across, not nothing.
                float hx = Math.Max(d.Half.X * scale, 1.5f * Supersample), hy = Math.Max(d.Half.Y * scale, 1.5f * Supersample);
                int x0 = Math.Max(0, (int)MathF.Floor(centre.X - hx)), x1 = Math.Min(n - 1, (int)MathF.Ceiling(centre.X + hx));
                int y0 = Math.Max(0, (int)MathF.Floor(centre.Y - hy)), y1 = Math.Min(n - 1, (int)MathF.Ceiling(centre.Y + hy));
                if (!(hx > 0.05f && hy > 0.05f) || x1 < x0 || y1 < y0) continue;
                visited += (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var uv = new Vector2((x + 0.5f - (centre.X - hx)) / (2 * hx), (y + 0.5f - (centre.Y - hy)) / (2 * hy));
                        if (uv.X is < 0 or >= 1 || uv.Y is < 0 or >= 1) continue;
                        var r = (uv - new Vector2(0.5f)) * 2;
                        var soft = Math.Clamp(1 - r.LengthSquared(), 0, 1);
                        if (soft <= 0) continue;
                        Shade(y * n + x, uv, Vector4.One, soft);
                    }
            }
        }
        return Expose(light, density, paint, n);
    }

    private static Vector2 Uv(Vector2[] uvs, int a, int b, int c, float w0, float w1, float w2) =>
        a < uvs.Length && b < uvs.Length && c < uvs.Length ? uvs[a] * w0 + uvs[b] * w1 + uvs[c] * w2 : Vector2.Zero;

    // Exposure: a faint effect is brightened so its shape reads (from the bright end of what was drawn), a soft
    // shoulder, then light with as much alpha as it is bright; alpha-blended parts cover as their density builds up
    // (thin smoke brought up to a shape, at most a few times), in their own colour, the light added over them.
    private static byte[]? Expose(Vector3[] light, float[] density, Vector3[] paint, int n)
    {
        static float Percentile(List<float> values) { values.Sort(); return values.Count == 0 ? 0 : values[Math.Min(values.Count - 1, (int)(values.Count * 0.98))]; }
        var lit = new List<float>(n * n / 4); var dense = new List<float>(n * n / 4); var tone = new List<float>(n * n / 4);
        for (var i = 0; i < light.Length; i++)
        {
            var m = Math.Max(light[i].X, Math.Max(light[i].Y, light[i].Z));
            if (!float.IsFinite(m) || !float.IsFinite(density[i]) || !float.IsFinite(paint[i].X + paint[i].Y + paint[i].Z)) { light[i] = Vector3.Zero; density[i] = 0; paint[i] = Vector3.Zero; continue; }
            if (m > 1e-5f) lit.Add(m);
            if (density[i] > 1e-4f)
            {
                dense.Add(density[i]);
                var own = paint[i] / density[i];
                tone.Add(Math.Max(own.X, Math.Max(own.Y, own.Z)));
            }
        }
        if (lit.Count == 0 && dense.Count == 0) return null;
        var exposure = Math.Clamp(1.1f / Math.Max(Percentile(lit), 1e-5f), 0.3f, 48f);
        var thicken = Math.Clamp(1.6f / Math.Max(Percentile(dense), 1e-5f), 1, 6);
        // Alpha-blended colour lifted (at most a few times) so its brightest reads light, as smoke does against the sky.
        var lift = Math.Clamp(0.9f / Math.Max(Percentile(tone), 1e-5f), 1, 4);
        var rgba = new byte[Size * Size * 4];
        var seen = 0;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var c = Vector3.Zero; var covered = 0f;
                for (var sy = 0; sy < Supersample; sy++)
                    for (var sx = 0; sx < Supersample; sx++)
                    {
                        var at = (y * Supersample + sy) * n + x * Supersample + sx;
                        var cover = density[at] > 0 ? 1 - MathF.Exp(-thicken * density[at]) : 0;
                        var own = density[at] > 0 ? Vector3.Clamp(paint[at] / density[at] * lift, Vector3.Zero, Vector3.One) : Vector3.Zero;
                        var added = light[at] * exposure;
                        added = new Vector3(1 - MathF.Exp(-1.6f * added.X), 1 - MathF.Exp(-1.6f * added.Y), 1 - MathF.Exp(-1.6f * added.Z));
                        c += Vector3.Min(own * cover + added, Vector3.One);
                        covered += cover;
                    }
                c /= Supersample * Supersample; covered /= Supersample * Supersample;
                var alpha = Math.Clamp(Math.Max(covered, Math.Max(c.X, Math.Max(c.Y, c.Z))), 0, 1);
                if (alpha <= 1f / 255) continue;
                var o = (y * Size + x) * 4;
                rgba[o] = (byte)Math.Clamp(c.X / alpha * 255 + .5f, 0, 255); rgba[o + 1] = (byte)Math.Clamp(c.Y / alpha * 255 + .5f, 0, 255);
                rgba[o + 2] = (byte)Math.Clamp(c.Z / alpha * 255 + .5f, 0, 255); rgba[o + 3] = (byte)Math.Clamp(alpha * 255 + .5f, 0, 255);
                if (alpha >= 0.15f) seen++;
            }
        // A trace too faint or small to see reads as a broken picture; the plain tile says more.
        return seen >= Size * Size / 400 ? rgba : null;
    }

    // Fills a triangle's samples (the top-left rule, so two triangles sharing an edge never both fill it); how many
    // samples it looked at.
    private static long Triangle(Vector2 a, Vector2 b, Vector2 c, int n, Action<float, float, float, int> shade)
    {
        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (!(Math.Abs(area) > 1e-6f) || !float.IsFinite(area)) return 1;
        // Wound one way on screen; the weights are handed back in the corners' own order.
        var swapped = area < 0;
        if (swapped) { (b, c) = (c, b); area = -area; }
        float minX = Math.Min(a.X, Math.Min(b.X, c.X)), maxX = Math.Max(a.X, Math.Max(b.X, c.X));
        float minY = Math.Min(a.Y, Math.Min(b.Y, c.Y)), maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
        if (maxX < 0 || maxY < 0 || minX > n || minY > n) return 1;
        int x0 = Math.Max(0, (int)MathF.Floor(minX)), x1 = Math.Min(n - 1, (int)MathF.Ceiling(maxX));
        int y0 = Math.Max(0, (int)MathF.Floor(minY)), y1 = Math.Min(n - 1, (int)MathF.Ceiling(maxY));
        // With y down and the area positive the triangle winds clockwise on screen; an edge is top or left by its direction.
        static bool TopLeft(Vector2 from, Vector2 to) => (from.Y == to.Y && to.X > from.X) || to.Y < from.Y;
        bool tlA = TopLeft(b, c), tlB = TopLeft(c, a), tlC = TopLeft(a, b);
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float px = x + .5f, py = y + .5f;
                var e0 = (c.X - b.X) * (py - b.Y) - (c.Y - b.Y) * (px - b.X);
                var e1 = (a.X - c.X) * (py - c.Y) - (a.Y - c.Y) * (px - c.X);
                var e2 = (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X);
                if (e0 < 0 || e1 < 0 || e2 < 0 || (e0 == 0 && !tlA) || (e1 == 0 && !tlB) || (e2 == 0 && !tlC)) continue;
                if (swapped) shade(e0 / area, e2 / area, e1 / area, y * n + x);
                else shade(e0 / area, e1 / area, e2 / area, y * n + x);
            }
        return 1 + (long)Math.Max(0, x1 - x0 + 1) * Math.Max(0, y1 - y0 + 1);
    }

    private static Vector4 Sample(ThumbnailTexture texture, Vector2 uv, Layer layer)
    {
        // UVs as the layer gives them (its scale, a flipbook's first cell; its turn), the texture repeating.
        var u = uv.X * layer.Scale.X; var v = uv.Y * layer.Scale.Y;
        if (layer.Rotation != 0)
        {
            float cos = MathF.Cos(layer.Rotation), sin = MathF.Sin(layer.Rotation);
            (u, v) = (u * cos - v * sin, u * sin + v * cos);
        }
        if (!float.IsFinite(u) || !float.IsFinite(v) || Math.Abs(u) > 1e6f || Math.Abs(v) > 1e6f) return Vector4.Zero;
        u -= MathF.Floor(u); v -= MathF.Floor(v);
        var x = Math.Min(texture.Width - 1, (int)(u * texture.Width)); var y = Math.Min(texture.Height - 1, (int)(v * texture.Height));
        var o = (y * texture.Width + x) * 4;
        return new Vector4(texture.Rgba[o], texture.Rgba[o + 1], texture.Rgba[o + 2], texture.Rgba[o + 3]) / 255f;
    }

    private static ThumbnailTexture? Texture(string path, Func<string, byte[]?> read, Dictionary<string, ThumbnailTexture?> textures)
    {
        if (textures.TryGetValue(path, out var known)) return known;
        ThumbnailTexture? texture = null;
        try
        {
            if (read(path) is { Length: <= AvfxFiles.MaxBytes } tex && TextureDecoding.Readable(tex))
            {
                // The mip Decode would take, measured first: a header claiming a large texture with no small mips
                // isn't decoded (it would ask for its whole size before finding the file too short).
                var (_, width, height) = TextureDecoding.Header(tex);
                var mips = Math.Clamp(tex[14] & 0x7F, 1, 13);
                for (var mip = 0; mip + 1 < mips && (width > TextureSide || height > TextureSide); mip++) { width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); }
                if ((long)width * height <= 512 * 512)
                {
                    var (rgba, w, h) = TextureDecoding.Decode(tex, TextureSide);
                    texture = new(rgba, w, h);
                }
            }
        }
        catch (Exception) { texture = null; }
        return textures[path] = texture;
    }

    // Effect angles are radians, turned X then Y then Z, as the layouts' are.
    private static Matrix4x4 Turn(Vector3 radians) => LayoutRotation.Matrix(Math.Clamp(radians.X, -100, 100), Math.Clamp(radians.Y, -100, 100), Math.Clamp(radians.Z, -100, 100));

    private static Vector3 Sane(Vector3 v) => new(Finite(v.X, 0), Finite(v.Y, 0), Finite(v.Z, 0));
    private static float Finite(float v, float fallback) => float.IsFinite(v) ? v : fallback;
    private static Vector3 Clamp(Vector3 v, float low, float high) => Vector3.Clamp(Sane(v), new(low), new(high));
}
