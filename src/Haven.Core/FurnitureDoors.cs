using System.Buffers.Binary;
using System.Numerics;

namespace Haven.Core;

public sealed record DoorAction(byte[] Panels, int Kind, float Seconds, float Radians, float Distance, int Curve, int Axis);
public sealed record DoorFileData(DoorAction[] Actions, IReadOnlyDictionary<uint, int> Shapes);

/// <summary>Read the installed SGB's door controllers; never execute its native event scripts.</summary>
public static class SgbDoors
{
    // FileSceneHeader/embedded layer offsets: FFCS LayoutEngine/FileFormat.cs.
    // Door fields: audited SGDoorActionController initialization in the installed client.
    public static DoorFileData Read(ReadOnlySpan<byte> bytes)
    {
        var data = bytes.ToArray();
        int Int(int offset)
        {
            if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("Invalid SGB door offset.");
            return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        }
        int Relative(int origin, int field) => checked(origin + Int(checked(origin + field)));
        int Count(int offset, int maximum)
        {
            var n = Int(offset); if (n < 0 || n > maximum) throw new InvalidDataException("Invalid SGB door count."); return n;
        }
        float Float(int offset) => BitConverter.Int32BitsToSingle(Int(offset));
        if (data.Length < 0x54 || Int(0) != 0x31424753 || Int(12) != 0x314E4353)
            return new([], new Dictionary<uint, int>());
        const int scene = 0x14;
        if (Int(scene + 0x20) == 0) return new([], new Dictionary<uint, int>());
        var config = Relative(scene, 0x20);
        var list = Relative(config, 0x20);
        var count = Count(list + 4, 32);
        if (count == 0) return new([], new Dictionary<uint, int>());
        var offsets = Relative(list, 0);
        List<DoorAction> actions = [];
        for (var n = 0; n < count; n++)
        {
            var action = checked(offsets + Int(checked(offsets + n * 4)));
            if (Int(action) != 1) continue; // Other animation controllers are not proximity doors.
            _ = Int(action + 0x2C); // Bounds-check the whole door record before byte reads.
            if (data[action + 4] == 0) continue;
            var panels = new byte[] { data[action + 0x10], data[action + 0x11], data[action + 0x26], data[action + 0x27] };
            var kind = Int(action + 0x14); var frames = Float(action + 0x18);
            var angle = Float(action + 0x1C); var distance = Float(action + 0x20);
            var curve = Int(action + 0x28); var axis = Int(action + 0x2C);
            if (kind is < 0 or > 2 || axis is < 0 or > 2 || curve is < 1 or > 4 || panels.All(p => p == 0)
                || !float.IsFinite(frames) || frames is <= 0 or > 3600 || !float.IsFinite(angle) || Math.Abs(angle) > 8 * MathF.PI
                || !float.IsFinite(distance) || Math.Abs(distance) > 100)
                throw new InvalidDataException("Unsupported SGB door controller.");
            actions.Add(new(panels, kind, frames / 60, angle, distance, curve, axis));
        }
        Dictionary<uint, int> shapes = [];
        if (actions.Count > 0)
        {
            var groups = Relative(scene, 0);
            for (var g = 0; g < Count(scene + 4, 16); g++)
            {
                var group = checked(groups + g * 0x10);
                var layers = Relative(group, 8);
                for (var l = 0; l < Count(group + 12, 64); l++)
                {
                    var layer = checked(layers + Int(checked(layers + l * 4)));
                    var objects = Relative(layer, 8);
                    for (var o = 0; o < Count(layer + 12, 4096); o++)
                    {
                        var item = checked(objects + Int(checked(objects + o * 4)));
                        if (Int(item) is 57 or 58) shapes[unchecked((uint)Int(item + 4))] = Int(item + 0x30);
                    }
                }
            }
        }
        return new(actions.ToArray(), shapes);
    }
}

public readonly record struct DoorVolume(int Shape, Matrix4x4 Transform)
{
    // Include the lower body: a furnishing raised slightly off the floor must not
    // require the player's feet to be inside a volume whose bottom is above them.
    public bool ContainsCharacter(Vector3 feet, Matrix4x4 furniture, float margin = 0)
        => Contains(feet, furniture, margin) || Contains(feet + new Vector3(0, .5f, 0), furniture, margin);

    public bool Contains(Vector3 player, Matrix4x4 furniture, float margin = 0)
    {
        if (!Matrix4x4.Invert(Transform * furniture, out var inverse)) return false;
        var p = Vector3.Transform(player, inverse);
        if (!float.IsFinite(p.LengthSquared())) return false;
        var r = 1 + margin;
        return Shape switch
        {
            1 => Math.Abs(p.X) <= r && Math.Abs(p.Y) <= r && Math.Abs(p.Z) <= r,
            2 => p.LengthSquared() <= r * r,
            3 => p.X * p.X + p.Z * p.Z <= r * r && Math.Abs(p.Y) <= r,
            _ => false,
        };
    }
}

public static class DoorMovement
{
    public static Matrix4x4 Transform(DoorAction action, int panel, Matrix4x4 local, Matrix4x4 parent, float progress)
    {
        if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation)) throw new InvalidDataException("Invalid door transform.");
        var t = Math.Clamp(progress, 0, 1);
        // Smooth endpoints, with the native distance, pivot and duration. Linear controllers remain linear.
        if (action.Curve != 2) t = t * t * (3 - 2 * t);
        var sign = panel % 2 == 0 ? -1 : 1;
        if (action.Kind == 1) translation.X += sign * action.Distance * t;
        else if (action.Kind == 2) translation.Y += action.Distance * t;
        else
        {
            var axis = action.Axis == 0 ? Vector3.UnitX : action.Axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            rotation = Quaternion.Normalize(rotation * Quaternion.CreateFromAxisAngle(axis, -sign * action.Radians * t));
        }
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation) * parent;
    }
}

/// <summary>Reversible animation with a small closing grace period; never persists or sends animation frames.</summary>
public sealed class DoorMotionState
{
    public float Progress { get; private set; }
    public bool TargetOpen { get; private set; }
    public bool Blocks => Progress == 0 && !TargetOpen;
    private long? previous;
    private long holdUntil;
    public void Update(bool occupied, long now, float seconds)
    {
        if (previous == null) { Progress = occupied ? 1 : 0; previous = now; }
        var elapsed = Math.Max(0, now - previous.Value) / 1000f;
        previous = Math.Max(previous.Value, now);
        if (occupied) holdUntil = now + 300;
        TargetOpen = occupied || now < holdUntil;
        var step = elapsed / Math.Max(.01f, seconds);
        Progress = Math.Clamp(Progress + (TargetOpen ? step : -step), 0, 1);
    }
}
