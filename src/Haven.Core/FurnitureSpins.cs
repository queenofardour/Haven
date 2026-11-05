using System.Buffers.Binary;
using System.Numerics;

namespace Haven.Core;

public readonly record struct SpinAction(byte Member, int Axis, float SignedSeconds, bool HasEffects);
public static class SgbSpins
{
    // SGB rotation controller type 2: member 0x10, axis 0x14, signed
    // revolution duration 0x18 in 30-Hz units. 0x21/0x26 enable companion effects.
    public static SpinAction[] Read(ReadOnlySpan<byte> source)
    {
        var data = source.ToArray();
        int Int(int p)
        {
            if (p < 0 || p > data.Length - 4) throw new InvalidDataException("Invalid SGB rotation offset.");
            return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p, 4));
        }
        int Relative(int p, int field) => checked(p + Int(checked(p + field)));
        if (data.Length < 0x54 || Int(0) != 0x31424753 || Int(12) != 0x314E4353 || Int(0x34) == 0) return [];
        var config = Relative(0x14, 0x20); var list = Relative(config, 0x20); var count = Int(checked(list + 4));
        if (count is < 0 or > 32) throw new InvalidDataException("Invalid SGB rotation count.");
        if (count == 0) return [];
        var offsets = Relative(list, 0); List<SpinAction> spins = [];
        for (var i = 0; i < count; i++)
        {
            var record = checked(offsets + Int(checked(offsets + i * 4)));
            if (Int(record) != 2) continue;
            _ = Int(checked(record + 0x24));
            if (data[record + 4] == 0) continue;
            var axis = Int(record + 0x14); var frames = BitConverter.Int32BitsToSingle(Int(record + 0x18));
            if (data[record + 0x10] == 0 || axis is < 0 or > 2 || !float.IsFinite(frames) || Math.Abs(frames) is < .01f or > 1_000_000)
                throw new InvalidDataException("Invalid SGB rotation parameters.");
            spins.Add(new(data[record + 0x10], axis, frames / 30, data[record + 0x21] != 0 || data[record + 0x26] != 0));
        }
        return spins.ToArray();
    }
}

public static class SpinMovement
{
    public static Matrix4x4 Transform(SpinAction spin, Matrix4x4 local, Matrix4x4 parent, long milliseconds)
    {
        if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var position)) throw new InvalidDataException("Invalid rotation transform.");
        rotation = Quaternion.Normalize(rotation * Rotation(spin, milliseconds));
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position) * parent;
    }

    // The turn about the part's own axis since the clock's origin.
    public static Quaternion Rotation(SpinAction spin, long milliseconds)
    {
        var period = Math.Abs((double)spin.SignedSeconds) * 1000;
        if (!double.IsFinite(period) || period <= 0 || spin.Axis is < 0 or > 2) throw new InvalidDataException("Invalid rotation period/axis.");
        var fraction = ((milliseconds % period) + period) % period / period;
        var angle = (float)(fraction * Math.Tau * Math.Sign(spin.SignedSeconds));
        var axis = spin.Axis == 0 ? Vector3.UnitX : spin.Axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        return Quaternion.CreateFromAxisAngle(axis, angle);
    }
}
