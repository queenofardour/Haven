using System.Buffers.Binary;
using System.Numerics;

namespace Haven.Core;

/// <summary>
/// One channel (translation, rotation or scale) of a repeating transform. End is the authored offset, Random the
/// fraction by which each cycle may fall short of it, Frames a half swing at 30 per second, Easing the curve of each
/// half and Mode the shape: 0 runs to End and starts again, 1 goes out and back, 2 swings out, back, out the other
/// way and back.
/// </summary>
public sealed record TransformTimer(Vector3 End, float Random, int Frames, int Easing, int Mode);
public sealed record OscillationAction(byte[] Members, TransformTimer? Translation, TransformTimer? Rotation, TransformTimer? Scale);
public sealed record OscillationFile(bool HasTransform, OscillationAction[] Actions);
public static class SgbOscillations
{
    // SGB action type 5 (SGTransformActionController). Record: +0x04 enabled, +0x10/+0x14 member bytes and count,
    // +0x18 loop, +0x20/+0x24/+0x28 timers for translation, rotation and scale. Timer: +0x00 on, +0x04 vector,
    // +0x10 random fraction, +0x14 frames, +0x18 ease-in frames, +0x1C easing, +0x20 mode. Read from the client's
    // loader read-only; looping actions only, since a one-shot ends where it started or needs its trigger.
    public static OscillationFile Read(ReadOnlySpan<byte> source)
    {
        var b = source.ToArray();
        int Int(int p)
        {
            if (p < 0 || p > b.Length - 4) throw new InvalidDataException("Invalid SGB oscillation offset.");
            return BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p, 4));
        }
        int Relative(int p, int field) => checked(p + Int(checked(p + field)));
        float Float(int p) => BitConverter.Int32BitsToSingle(Int(p));
        if (b.Length < 0x54 || Int(0) != 0x31424753 || Int(12) != 0x314E4353 || Int(0x34) == 0) return new(false, []);
        var config = Relative(0x14, 0x20); var list = Relative(config, 0x20); var count = Int(checked(list + 4));
        if (count is < 0 or > 32) throw new InvalidDataException("Invalid SGB oscillation count.");
        if (count == 0) return new(false, []);
        var offsets = Relative(list, 0); var found = false; List<OscillationAction> result = [];
        for (var i = 0; i < count; i++)
        {
            var record = checked(offsets + Int(checked(offsets + i * 4)));
            if (Int(record) != 5) continue;
            _ = Int(checked(record + 0x28));
            if (b[record + 4] == 0) continue;
            found = true;
            if (b[record + 0x18] == 0) continue; // Non-looping activation stays unsupported.
            TransformTimer? Timer(int field)
            {
                var timer = Relative(record, field); _ = Int(checked(timer + 0x20));
                if (b[timer] == 0) return null;
                var end = new Vector3(Float(timer + 4), Float(timer + 8), Float(timer + 12));
                var random = Float(timer + 0x10); var frames = Int(timer + 0x14); var easing = Int(timer + 0x1C); var mode = Int(timer + 0x20);
                if (!float.IsFinite(end.LengthSquared()) || end.LengthSquared() > 100 || !float.IsFinite(random) || random is < 0 or > 10
                    || frames is < 1 or > 36000 || easing is < 0 or > 3 || mode is < 0 or > 2)
                    throw new InvalidDataException("Invalid SGB oscillation parameters.");
                return new(end, random, frames, easing, mode);
            }
            var translation = Timer(0x20); var rotation = Timer(0x24); var scale = Timer(0x28);
            if (translation == null && rotation == null && scale == null) continue;
            var memberCount = Int(record + 0x14); var members = Relative(record, 0x10);
            if (memberCount is < 1 or > 16 || members < 0 || members > b.Length - memberCount)
                throw new InvalidDataException("Invalid SGB oscillation members.");
            var ids = b.AsSpan(members, memberCount).ToArray();
            if (ids.Contains((byte)0) || ids.Distinct().Count() != ids.Length) throw new InvalidDataException("Invalid oscillation member IDs.");
            result.Add(new(ids, translation, rotation, scale));
        }
        return new(found, result.ToArray());
    }
}

public static class OscillationMovement
{
    public static Matrix4x4 Transform(OscillationAction action, Matrix4x4 local, Matrix4x4 parent, long milliseconds, int seed = 0)
    {
        if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var position)) throw new InvalidDataException("Invalid oscillation transform.");
        Apply(action, milliseconds, seed, ref scale, ref rotation, ref position);
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position) * parent;
    }

    // Translation adds in the parent's space, rotation turns about the part's own axes, and scale multiplies.
    public static void Apply(OscillationAction action, long milliseconds, int seed, ref Vector3 scale, ref Quaternion rotation, ref Vector3 position)
    {
        if (action.Translation is {} t) position += Value(t, milliseconds, seed, 1);
        if (action.Rotation is {} r)
        {
            var angles = Value(r, milliseconds, seed, 2);
            rotation = Quaternion.Normalize(rotation * LayoutRotation.Quaternion(angles.X, angles.Y, angles.Z));
        }
        if (action.Scale is {} s) scale *= Value(s, milliseconds, seed, 3);
    }

    // The client advances a fraction through each half swing (Frames long), easing it, and after every full cycle
    // (one half for mode 0, two otherwise) rolls a new end: each positive component may fall short by up to Random
    // times itself. Haven rolls from the item's seed and the cycle number, so every client sees the same swing.
    // The client's ease-in when a controller first starts is not reproduced: Haven's motion runs on a shared clock.
    public static Vector3 Value(TransformTimer timer, long milliseconds, int seed, int channel)
    {
        var half = timer.Frames * 1000.0 / 30;
        var position = milliseconds / half;
        var index = (long)Math.Floor(position); var fraction = (float)(position - index);
        var back = timer.Mode != 0 && (index & 1) != 0;
        var negative = timer.Mode == 2 && (((index % 4) + 4) % 4) >= 2;
        var cycle = timer.Mode == 0 ? index : index >> 1;
        var eased = Ease(back ? 1 - fraction : fraction, timer.Easing, back);
        var end = timer.End;
        if (timer.Random != 0)
        {
            float Roll(float c, int component)
            {
                var range = (int)(c * timer.Random * 1000);
                return range <= 0 ? c : c - Hash(seed, channel, component, cycle) % (uint)range / 1000f;
            }
            end = new(Roll(end.X, 0), Roll(end.Y, 1), Roll(end.Z, 2));
        }
        // Scale eases from 1 towards its end; translation and rotation from 0, the second swing of mode 2 negated.
        return channel == 3 ? Vector3.One + (end - Vector3.One) * eased : end * (negative ? -eased : eased);
    }

    // The client's curves: 0 linear; 1 cubic ease-in-out; 2 eases in on the way out and out on the way back;
    // 3 the reverse. Each blends fully into a cubic (exponent 3).
    public static float Ease(float t, int easing, bool back)
    {
        var amount = easing switch { 1 => 1f, 2 => back ? 1f : -1f, 3 => back ? -1f : 1f, _ => 0f };
        if (amount == 0) return t;
        var c = Math.Clamp(t, 0, 1);
        float curve;
        if (easing == 1) curve = c < .5f ? MathF.Pow(c + c, 3) * .5f : (1 - MathF.Pow(MathF.Abs((c - .5f) * 2 - 1), 3)) * .5f + .5f;
        else curve = amount > 0 ? 1 - MathF.Pow(MathF.Abs(c - 1), 3) : MathF.Pow(c, 3);
        return t + MathF.Abs(amount) * (curve - t);
    }

    private static uint Hash(int seed, int channel, int component, long cycle)
    {
        var x = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)cycle * 0xBF58476D1CE4E5B9UL ^ (ulong)(channel * 4 + component) * 0x94D049BB133111EBUL);
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL; x ^= x >> 27; x *= 0x94D049BB133111EBUL; x ^= x >> 31;
        return (uint)x;
    }
}
