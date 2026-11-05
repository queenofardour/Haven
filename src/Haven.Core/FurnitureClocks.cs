using System.Buffers.Binary;
using System.Numerics;

namespace Haven.Core;

public readonly record struct ClockAction(byte Hour, byte Minute);

public static class SgbClocks
{
    // Same SCN1 animation-list layout as SgbDoors. Clock controller (type 4)
    // stores the hour/minute instance IDs at 0x10/0x11; remaining fields control
    // features such as chimes, which are deliberately not executed here.
    public static ClockAction[] Read(ReadOnlySpan<byte> source)
    {
        var data = source.ToArray();
        int Int(int offset)
        {
            if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("Invalid SGB clock offset.");
            return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        }
        int Relative(int origin, int field) => checked(origin + Int(checked(origin + field)));
        if (data.Length < 0x54 || Int(0) != 0x31424753 || Int(12) != 0x314E4353 || Int(0x34) == 0) return [];
        var config = Relative(0x14, 0x20); var list = Relative(config, 0x20);
        var count = Int(checked(list + 4));
        if (count is < 0 or > 32) throw new InvalidDataException("Invalid SGB clock count.");
        if (count == 0) return [];
        var offsets = Relative(list, 0); List<ClockAction> clocks = [];
        for (var i = 0; i < count; i++)
        {
            var record = checked(offsets + Int(checked(offsets + i * 4)));
            if (Int(record) != 4) continue;
            _ = Int(checked(record + 0x14)); // Validate before reading bytes.
            if (data[record + 4] == 0) continue;
            var hour = data[record + 0x10]; var minute = data[record + 0x11];
            if (hour == 0 || minute == 0 || hour == minute) throw new InvalidDataException("Invalid SGB clock hands.");
            clocks.Add(new(hour, minute));
        }
        return clocks.ToArray();
    }
}

public static class ClockMovement
{
    public static Matrix4x4 Transform(bool hour, Matrix4x4 local, Matrix4x4 parent, long eorzeaSeconds)
    {
        if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation)) throw new InvalidDataException("Invalid clock transform.");
        var day = ((eorzeaSeconds % 86400) + 86400) % 86400;
        // Native SGClockActionController advances the hour hand per Eorzean
        // minute, and the minute hand with seconds. Both turn around local -Z.
        var fraction = hour ? (day / 3600 + day % 3600 / 60 / 60f) / 12 : day % 3600 / 3600f;
        var delta = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.Tau * fraction);
        rotation = Quaternion.Normalize(rotation * delta);
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation) * parent;
    }
}
