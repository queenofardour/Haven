using System.Buffers.Binary;
using System.Numerics;

namespace Haven.Core;

/// <summary>
/// The client's rotation order for layout and timeline Euler angles (radians): X first, then Y, then Z. Its conversion
/// (RVA 0x796C40, read-only research; also used when placing layout objects and by timeline commands) builds
/// q = qz·qy·qx. Matrix4x4.CreateFromYawPitchRoll applies Z, X, then Y instead: the same for a pure yaw, but members
/// authored as (π, a, π) — the Invincible II's and Tatanora's left propeller pods, mirrored chair markers — turned
/// to π + a instead of the game's π − a.
/// </summary>
public static class LayoutRotation
{
    public static Matrix4x4 Matrix(float x, float y, float z) => Matrix4x4.CreateRotationX(x) * Matrix4x4.CreateRotationY(y) * Matrix4x4.CreateRotationZ(z);
    public static Quaternion Quaternion(float x, float y, float z) => System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z)
        * System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitY, y) * System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitX, x);
    /// <summary>
    /// The angles (radians, X then Y then Z) that Matrix gives this rotation back from: the inverse of the game's order.
    /// At a quarter turn about Y (X and Z then turn about the same axis) Z is taken as 0.
    /// </summary>
    public static Vector3 Angles(System.Numerics.Quaternion rotation)
    {
        var m = Matrix4x4.CreateFromQuaternion(System.Numerics.Quaternion.Normalize(rotation));
        var y = MathF.Asin(Math.Clamp(-m.M13, -1, 1));
        if (MathF.Abs(m.M13) < .99999f) return new(MathF.Atan2(m.M23, m.M33), y, MathF.Atan2(m.M12, m.M11));
        return new(MathF.Atan2(-m.M32, m.M22), y, 0);
    }
    // An SGB member's (or C018's) scale, Euler rotation and translation.
    public static Matrix4x4 Transform(Vector3 translation, Vector3 radians, Vector3 scale) => Matrix4x4.CreateScale(scale)
        * Matrix(radians.X, radians.Y, radians.Z) * Matrix4x4.CreateTranslation(translation);
}

/// <summary>
/// One curve keyframe: frame, value, and its in and out tangents (value per frame). Linear (key +0x14 = 1): the
/// segment to the next key is a straight line and the tangents are ignored. An airship's propellers are linear,
/// and their stored tangents are stale copies that would tilt and lurch the blades.
/// </summary>
public readonly record struct LoopKey(float Frame, float Value, float Tangent, bool Linear = false)
{
    public float OutTangent { get; init; } = Tangent;
}

/// <summary>
/// A looping transform animation from an SGB timeline's C013 command: nine curves giving the target's absolute
/// local translation, rotation in degrees and scale. Plays at 30 frames per second and repeats every Frames.
/// Components (C013 +0x18: 1 translation, 2 rotation, 4 scale) says which curves apply; the rest, and any
/// channel without keys, keep the member's authored value (see <see cref="WithRest"/>).
/// </summary>
public sealed record LoopCurve(int Frames, LoopKey[][] Channels, int Components = 7) : ILoopMotion
{
    public LoopCurve WithRest(Vector3 translation, Vector3 rotationRadians, Vector3 scale)
    {
        const float degrees = 180 / MathF.PI;
        float[] rest = [translation.X, translation.Y, translation.Z, rotationRadians.X * degrees, rotationRadians.Y * degrees, rotationRadians.Z * degrees, scale.X, scale.Y, scale.Z];
        var channels = new LoopKey[9][];
        for (var i = 0; i < 9; i++)
            channels[i] = (Components & (1 << (i / 3))) != 0 && Channels[i].Length > 0 ? Channels[i] : [new(0, rest[i], 0)];
        return this with { Channels = channels };
    }

    /// <summary>
    /// A looping curve's two-key channels run at a constant rate. Two keys spanning a lap can only mean a steady spin
    /// or a held value, and the airship miniatures' such channels carry stale tangents copied between curves (0.1875
    /// on a 1,440-degree spin, -7.17 on a channel that stays at 0), with the linear flag on either key or neither. Read
    /// as Hermite, their small propellers lurched and tipped off their shafts. Longer loops keep their tangents.
    /// </summary>
    public LoopCurve Looped() => this with
    {
        Channels = Channels.Select(keys => keys.Length == 2 ? [keys[0] with { Linear = true }, keys[1]] : keys).ToArray(),
    };

    public const double FramesPerSecond = 30;
    // Channel codes 0x40-0x45 and 0x47-0x49 in order: translation XYZ, rotation XYZ (degrees), scale XYZ.
    private static readonly float[] Rest = [0, 0, 0, 0, 0, 0, 1, 1, 1];

    public Matrix4x4 Transform(long milliseconds)
    {
        var frame = milliseconds / 1000.0 * FramesPerSecond % Frames;
        if (frame < 0) frame += Frames;
        return At(frame);
    }

    // The pose at a frame, held at the ends: a switch side's one-shot curve stops on its last frame.
    public Matrix4x4 At(double frame)
    {
        frame = Math.Clamp(frame, 0, Frames);
        Span<float> v = stackalloc float[9];
        for (var i = 0; i < 9; i++) v[i] = Sample(Channels[i], (float)frame, Rest[i]);
        const float radians = MathF.PI / 180;
        return LayoutRotation.Transform(new(v[0], v[1], v[2]), new(v[3] * radians, v[4] * radians, v[5] * radians), new(v[6], v[7], v[8]));
    }

    // Cubic Hermite between keys using the out and in tangents, or a line from a linear key; constant before the
    // first and after the last key.
    public static float Sample(LoopKey[] keys, float frame, float rest)
    {
        if (keys.Length == 0) return rest;
        if (frame <= keys[0].Frame) return keys[0].Value;
        for (var i = 0; i < keys.Length - 1; i++)
        {
            LoopKey a = keys[i], b = keys[i + 1];
            if (frame > b.Frame) continue;
            var span = b.Frame - a.Frame;
            if (span <= 0) return b.Value;
            var s = (frame - a.Frame) / span; var s2 = s * s; var s3 = s2 * s;
            if (a.Linear) return a.Value + (b.Value - a.Value) * s;
            return (2 * s3 - 3 * s2 + 1) * a.Value + (s3 - 2 * s2 + s) * span * a.OutTangent
                + (-2 * s3 + 3 * s2) * b.Value + (s3 - s2) * span * b.Tangent;
        }
        return keys[^1].Value;
    }
}

public static partial class SgbTimelines
{
    private static readonly byte[] LoopChannelCodes = [0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x47, 0x48, 0x49];

    /// <summary>
    /// Looping transform animations of an autoplay, looping timeline, by target member. C013 (+0x0C frames,
    /// +0x14 curve ID) names a TMFC curve set (+0x08 ID, channel table at +0x10 plus the +0x0C offset, +0x10 count);
    /// each 16-byte channel entry holds its code, a key offset from the entry and a key count; keys are 24 bytes
    /// (frame, 1/span, value, in and out tangent, interpolation: 1 linear, else Hermite). Anything malformed is
    /// skipped, never thrown.
    /// </summary>
    public static IReadOnlyDictionary<uint, LoopCurve> Loops(ReadOnlySpan<byte> bytes) => Loops(bytes, null);

    // Played: looping timelines a switch starts by ID (an airship's propellers), whether or not they autoplay.
    public static IReadOnlyDictionary<uint, LoopCurve> Loops(ReadOnlySpan<byte> bytes, IReadOnlySet<int>? played)
    {
        Dictionary<uint, LoopCurve> loops = [];
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return loops; }
        var data = bytes.ToArray();
        foreach (var timeline in timelines)
        {
            if (!timeline.Loop || (played == null ? !timeline.AutoPlay : !played.Contains(timeline.Id))) continue;
            foreach (var (member, tag, _, at) in timeline.Commands)
                if (tag == "C013" && !loops.ContainsKey(member) && Curve(data, at, timeline.Curves) is {} curve) loops[member] = curve.Looped();
        }
        return loops;
    }

    /// <summary>
    /// Sounds an autoplay, looping timeline starts at a frame of every lap (a Drinking Apkallu's drip, the garden
    /// bamboo's clack). The lap is the timeline's length (see Lap), so the sound keeps time with the motion on the
    /// same clock; a timeline without one is skipped.
    /// </summary>
    public static IReadOnlyList<LoopSound> LoopSounds(ReadOnlySpan<byte> bytes)
    {
        List<LoopSound> sounds = [];
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return sounds; }
        var data = bytes.ToArray();
        foreach (var timeline in timelines)
        {
            if (!timeline.AutoPlay || !timeline.Loop) continue;
            var period = Lap(timeline, data);
            if (period is < 2 or > 30 * 600) continue;
            foreach (var start in timeline.Commands.Where(c => c.Tag == "C056" && c.Value < period))
            {
                var stop = timeline.Commands.Where(s => s.Tag == "C057" && s.Member == start.Member && s.Value >= start.Value).OrderBy(s => s.Value).FirstOrDefault();
                var fade = stop.Tag == null || stop.At + 24 > data.Length ? 0 : BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(stop.At + 20, 4)) is var ms && float.IsFinite(ms) ? (int)Math.Clamp(ms, 0, 10_000) : 0;
                sounds.Add(new(period, new SwitchSound(start.Member, true, start.Value, stop.Tag == null ? null : stop.Value, fade)));
            }
        }
        return sounds.Take(16).ToArray();
    }

    /// <summary>
    /// Effects an autoplay, looping timeline shows in a window of every lap: started by C058 (or by the effect's own
    /// autoplay at the lap's start) and ended by C059 or a C019 hide, restarting every lap. A loop with no known length
    /// only starts its effect, once, and keeps it.
    /// </summary>
    public static IReadOnlyList<LoopEffect> LoopEffects(ReadOnlySpan<byte> bytes)
    {
        List<LoopEffect> effects = [];
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return effects; }
        var data = bytes.ToArray();
        foreach (var timeline in timelines)
        {
            if (!timeline.AutoPlay || !timeline.Loop) continue;
            var period = Lap(timeline, data);
            if (period is < 0 or > 30 * 600) continue;
            int Frame(int at) => at + 12 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 10, 2)) : 0;
            var members = timeline.Commands.Where(c => c.Tag is "C058" or "C059" || c.Tag == "C019" && c.Value == 0).Select(c => c.Member).Distinct();
            foreach (var member in members)
            {
                var start = timeline.Commands.Where(c => c.Tag == "C058" && c.Member == member).Select(c => (int?)Frame(c.At)).Min();
                var stop = timeline.Commands.Where(c => c.Member == member && (c.Tag == "C059" || c.Tag == "C019" && c.Value == 0) && Frame(c.At) >= (start ?? 0))
                    .Select(c => (int?)Frame(c.At)).Min();
                effects.Add(new(member, period < 2 ? 0 : period, start, stop));
            }
        }
        return effects.Take(16).ToArray();
    }

    /// <summary>
    /// Glow an autoplay, looping timeline fades on its members every lap (C110, as a switch's): the Enigma Partition's
    /// and the Metal Pipe Wall Shelf's flicker. Each member keeps its fades in frame order; a loop with no length is
    /// skipped.
    /// </summary>
    /// <summary>
    /// The glow flicker of a group's looping timelines per member (autoplay ones; or, given a SubId, that timeline
    /// whether autoplay or started by another timeline's C082, as the Ale Tap's lamp blink is).
    /// </summary>
    public static IReadOnlyDictionary<uint, LoopGlow> LoopGlows(ReadOnlySpan<byte> bytes, int? timelineId = null)
    {
        Dictionary<uint, LoopGlow> glows = [];
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return glows; }
        var data = bytes.ToArray();
        foreach (var timeline in timelines)
        {
            if (!timeline.Loop || (timelineId is { } wanted ? timeline.Id != wanted : !timeline.AutoPlay)) continue;
            var period = Lap(timeline, data);
            if (period is < 2 or > 30 * 600) continue;
            foreach (var member in timeline.Commands.Where(c => c.Tag == GlowTag).GroupBy(c => c.Member))
            {
                var fades = member.Select(c => GlowAt(data, c.At)).OfType<GlowFade>().Where(f => f.StartFrame < period).OrderBy(f => f.StartFrame).ToArray();
                if (fades.Length > 0 && !glows.ContainsKey(member.Key)) glows[member.Key] = new(period, fades);
            }
        }
        return glows;
    }

    // A loop's lap: its timeline's own length (TMDH), else its longest C013 animation. The Storm Trophy's effect-only
    // loop has no animation, only its 180-frame length.
    private static int Lap(TimelineCommands timeline, byte[] data) => timeline.Duration >= 2 ? timeline.Duration
        : timeline.Commands.Where(c => c.Tag == "C013" && c.At + 0x10 <= data.Length)
            .Select(c => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(c.At + 0x0C, 4))).DefaultIfEmpty(0).Max();

    // A switch side's one-shot curve may be a single frame (a pose set at once); a looping one needs two.
    internal static LoopCurve? Curve(byte[] data, int command, IReadOnlyDictionary<int, int> curves, int minimumFrames = 2)
    {
        bool Inside(int offset, int length) => offset >= 0 && length >= 0 && offset <= data.Length - length;
        int Int(int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        if (!Inside(command + 0x14, 4)) return null;
        if (!Inside(command + 0x18, 4)) return null;
        var frames = Int(command + 0x0C); var components = Int(command + 0x18);
        if (components is < 1 or > 7) return null;
        if (frames < minimumFrames || frames > 30 * 600 || !curves.TryGetValue(Int(command + 0x14), out var node) || !Inside(node + 0x10, 4)) return null;
        var table = node + 0x10 + Int(node + 0x0C); var count = Int(node + 0x10);
        if (count is < 1 or > 16 || !Inside(table, count * 16)) return null;
        var channels = new LoopKey[9][];
        for (var c = 0; c < 9; c++) channels[c] = [];
        for (var i = 0; i < count; i++)
        {
            var entry = table + i * 16;
            var slot = Array.IndexOf(LoopChannelCodes, data[entry]);
            var keyCount = Int(entry + 8); var keys = entry + Int(entry + 4);
            if (keyCount is < 1 or > 64 || !Inside(keys, keyCount * 24)) return null;
            if (slot < 0) continue;
            var list = new LoopKey[keyCount];
            for (var k = 0; k < keyCount; k++)
            {
                var at = keys + k * 24;
                float F(int o) => BitConverter.Int32BitsToSingle(Int(at + o));
                list[k] = new(F(0), F(8), F(12), Int(at + 20) == 1) { OutTangent = F(16) };
                if (!float.IsFinite(list[k].Frame) || !float.IsFinite(list[k].Value) || !float.IsFinite(list[k].Tangent) || !float.IsFinite(list[k].OutTangent)
                    || Math.Abs(list[k].Value) > 100_000 || k > 0 && list[k].Frame < list[k - 1].Frame) return null;
            }
            channels[slot] = list;
        }
        return new(frames, channels, components);
    }
}

/// <summary>
/// An effect a looping timeline shows from StartFrame (null: from the lap's start, for an autoplay effect) until
/// StopFrame (null: the lap's end) in every lap of Period frames. Period 0 has no lap: the effect stays on.
/// </summary>
public readonly record struct LoopEffect(uint Member, int Period, int? StartFrame, int? StopFrame)
{
    /// <summary>The lap whose window is showing at this clock reading, or null between windows.</summary>
    public long? ShowingLap(long milliseconds, bool autoplay)
    {
        if (StartFrame == null && !autoplay) return null;
        if (Period == 0) return StartFrame == null && StopFrame != null ? null : 0;
        var frame = milliseconds / 1000.0 * SwitchSound.FramesPerSecond;
        var lap = (long)Math.Floor(frame / Period);
        var within = frame - lap * Period;
        return within >= (StartFrame ?? 0) && within < (StopFrame ?? Period) ? lap : null;
    }
}

/// <summary>
/// A member's glow through every lap of Period frames: each fade runs from the level the previous one left toward its
/// target, and a lap starts where the last fade of the lap before settled. The clock is shared, so every client
/// flickers in step with nothing sent.
/// </summary>
public sealed record LoopGlow(int Period, GlowFade[] Fades)
{
    public float Level(long milliseconds)
    {
        var frame = milliseconds / 1000.0 * SwitchSound.FramesPerSecond % Period;
        if (frame < 0) frame += Period;
        var level = Fades[^1].Target;
        for (var i = 0; i < Fades.Length && Fades[i].StartFrame <= frame; i++)
        {
            var fade = Fades[i];
            var until = i + 1 < Fades.Length && Fades[i + 1].StartFrame <= frame ? Fades[i + 1].StartFrame : frame;
            var progress = fade.Frames <= 1 ? 1 : Math.Min(1, (until - fade.StartFrame) / fade.Frames);
            level += (fade.Target - level) * (float)progress;
        }
        return level;
    }
    public byte Byte(long milliseconds) => GlowFade.ToByte(Level(milliseconds));
}

/// <summary>A sound a looping timeline starts once per lap of Period frames, with its timing within the lap.</summary>
public readonly record struct LoopSound(int Period, SwitchSound Timing)
{
    /// <summary>Whether the lap's frame passed Timing.StartFrame between two clock readings in milliseconds.</summary>
    public bool Crossed(long fromMilliseconds, long toMilliseconds)
    {
        if (toMilliseconds <= fromMilliseconds) return false;
        double Frame(long ms) => ms / 1000.0 * SwitchSound.FramesPerSecond;
        // The first lap boundary at or after the start frame that falls in (from, to].
        var lap = Math.Floor((Frame(fromMilliseconds) - Timing.StartFrame) / Period) + 1;
        return lap * Period + Timing.StartFrame <= Frame(toMilliseconds);
    }
}

/// <summary>A continuously running motion that replaces one level's local transform.</summary>
public interface ILoopMotion
{
    Matrix4x4 Transform(long milliseconds);
    // Seed: the placed item's, for motion that varies between copies (a wind chime's random swing).
    Matrix4x4 Transform(long milliseconds, int seed) => Transform(milliseconds);
}

/// <summary>One level between a furnishing's root and a model part: its authored local transform and, when a
/// looping timeline, a bound path or a repeating controller animates that level, the motion that replaces it.
/// WhenOn: the item's switch starts and stops that motion (an airship's propellers, a Gordian Chair's gears); while
/// off it holds the pose it stopped in, or its authored one until it has ever run.</summary>
public readonly record struct LoopLevel(Matrix4x4 Local, ILoopMotion? Motion, SwitchMotion? Switch = null, bool WhenOn = false);

/// <summary>
/// The client's repeating controllers on one prefab member, a part or a whole nested group (a mobile's arm): spins
/// (SGRotationActionController) and repeating transforms (SGTransformActionController) on top of its authored local
/// transform. Salt tells members of one item apart, so their random swings differ.
/// </summary>
public sealed class ControllerMotion : ILoopMotion
{
    private readonly Vector3 scale, position;
    private readonly Quaternion rotation;
    public IReadOnlyList<SpinAction> Spins { get; }
    public IReadOnlyList<OscillationAction> Transforms { get; }
    private readonly int salt;
    public ControllerMotion(Matrix4x4 rest, IReadOnlyList<SpinAction> spins, IReadOnlyList<OscillationAction> transforms, int salt)
    {
        if (!Matrix4x4.Decompose(rest, out scale, out rotation, out position)) throw new InvalidDataException("Invalid controller rest transform.");
        Spins = spins; Transforms = transforms; this.salt = salt;
    }
    public Matrix4x4 Transform(long milliseconds) => Transform(milliseconds, 0);
    public Matrix4x4 Transform(long milliseconds, int seed)
    {
        var s = scale; var r = rotation; var p = position;
        foreach (var spin in Spins) r = Quaternion.Normalize(r * SpinMovement.Rotation(spin, milliseconds));
        foreach (var action in Transforms) OscillationMovement.Apply(action, milliseconds, unchecked(seed * 31 + salt), ref s, ref r, ref p);
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(p);
    }
}

/// <summary>
/// A shared group travelling a ClientPath (the Eastern Indoor Pond's fish). The group's MovePathSettings give
/// autoplay, loop, Time (frames per lap at 30 per second) and rotation; ring paths close back on their first
/// point. Travel follows a uniform Catmull-Rom spline through the control points at constant speed, facing the
/// direction of travel about Y when the rotation type is YAxisOnly. Points are in the path's space (PathLocal).
/// </summary>
public sealed class PathMotion : ILoopMotion
{
    private const int Steps = 32;
    private readonly Vector3[] samples;
    private readonly float[] lengths;
    public int Frames { get; }
    public bool FaceTravel { get; }
    public Matrix4x4 PathLocal { get; }

    public PathMotion(IReadOnlyList<Vector3> points, bool ring, int frames, bool faceTravel, Matrix4x4 pathLocal)
    {
        if (points.Count is < 2 or > 64 || points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
            throw new InvalidDataException("Invalid furnishing path.");
        if (frames is < 1 or > 30 * 3600) throw new InvalidDataException("Invalid furnishing path time.");
        Frames = frames; FaceTravel = faceTravel; PathLocal = pathLocal;
        var count = points.Count; var segments = ring ? count : count - 1;
        Vector3 At(int i) => ring ? points[(i % count + count) % count] : points[Math.Clamp(i, 0, count - 1)];
        samples = new Vector3[segments * Steps + 1];
        for (var s = 0; s < segments; s++)
            for (var k = 0; k < Steps; k++)
                samples[s * Steps + k] = CatmullRom(At(s - 1), At(s), At(s + 1), At(s + 2), k / (float)Steps);
        samples[^1] = ring ? samples[0] : At(count - 1);
        lengths = new float[samples.Length];
        for (var i = 1; i < samples.Length; i++) lengths[i] = lengths[i - 1] + Vector3.Distance(samples[i - 1], samples[i]);
        if (lengths[^1] <= 1e-4f) throw new InvalidDataException("Furnishing path has no length.");
    }

    public Matrix4x4 Transform(long milliseconds)
    {
        var frame = milliseconds / 1000.0 * LoopCurve.FramesPerSecond % Frames;
        if (frame < 0) frame += Frames;
        var distance = (float)(frame / Frames) * lengths[^1];
        var i = Array.BinarySearch(lengths, distance);
        if (i < 0) i = Math.Clamp(~i, 1, lengths.Length - 1); else i = Math.Clamp(i, 1, lengths.Length - 1);
        var span = lengths[i] - lengths[i - 1];
        var position = Vector3.Lerp(samples[i - 1], samples[i], span > 0 ? (distance - lengths[i - 1]) / span : 0);
        var direction = samples[i] - samples[i - 1];
        var facing = FaceTravel && direction.X * direction.X + direction.Z * direction.Z > 1e-10f
            ? Matrix4x4.CreateRotationY(MathF.Atan2(direction.X, direction.Z)) : Matrix4x4.Identity;
        return facing * Matrix4x4.CreateTranslation(position) * PathLocal;
    }

    private static Vector3 CatmullRom(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
    {
        var t2 = t * t; var t3 = t2 * t;
        return 0.5f * (2 * b + (c - a) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (3 * b - a - 3 * c + d) * t3);
    }
}

public static class LoopMovement
{
    // Levels run from the outermost nested group to the part itself; row-vector order multiplies the part first.
    // A level a switch moves takes its pose from the item's side and the time since it changed (long.MaxValue: settled).
    public static Matrix4x4 Transform(IReadOnlyList<LoopLevel> levels, long milliseconds, bool on = true, long sinceSwitch = long.MaxValue, int seed = 0)
    {
        var matrix = Matrix4x4.Identity;
        for (var i = levels.Count - 1; i >= 0; i--)
        {
            var level = levels[i];
            matrix *= level.Motion is {} motion
                ? !level.WhenOn || on ? motion.Transform(milliseconds, seed)
                    : sinceSwitch is >= 0 and < long.MaxValue ? motion.Transform(milliseconds - sinceSwitch, seed) : level.Local
                : level.Switch is {} s ? s.Pose(on, sinceSwitch, level.Local) : level.Local;
        }
        return matrix;
    }
}
