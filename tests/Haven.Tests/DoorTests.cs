using System.Buffers.Binary;
using System.Numerics;
using Haven.Core;

internal static class DoorTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Door triggers respect rotated/scaled furniture and separate floors", () =>
        {
            var volume = new DoorVolume(1, Matrix4x4.CreateScale(2, 2, 3) * Matrix4x4.CreateTranslation(1, 2, 0));
            var house = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateRotationY(.8f) * Matrix4x4.CreateTranslation(12, 5, -30);
            Vector3 World(Vector3 p) => Vector3.Transform(p, volume.Transform * house);
            Assert(volume.Contains(World(Vector3.Zero), house), "Center must trigger.");
            Assert(volume.Contains(World(new(.9f, -.99f, .9f)), house), "Both sides and feet must trigger.");
            Assert(!volume.Contains(World(new(0, 2, 0)), house), "A different floor must not trigger.");
            Assert(!volume.Contains(World(new(0, 0, 1.2f)), house), "Outside the doorway must not trigger.");
            Assert(volume.Contains(World(new(0, 0, 1.04f)), house, .08f), "Open-door hysteresis must tolerate edge jitter.");
            Assert(!volume.Contains(new(float.NaN, 0, 0), house) && !volume.Contains(Vector3.Zero, default), "Nonfinite and singular transforms must fail closed.");
            Assert(new DoorVolume(2, Matrix4x4.Identity).Contains(new(.5f), Matrix4x4.Identity) && !new DoorVolume(2, Matrix4x4.Identity).Contains(new(.9f), Matrix4x4.Identity), "Spherical trigger boundary.");
            Assert(new DoorVolume(3, Matrix4x4.Identity).Contains(new(.5f, .9f, .5f), Matrix4x4.Identity) && !new DoorVolume(3, Matrix4x4.Identity).Contains(new(.9f, 0, .9f), Matrix4x4.Identity), "Cylindrical trigger boundary.");
        });
        await check("Door proximity includes lower body when the furniture is slightly above floor height", () =>
        {
            var volume = new DoorVolume(1, Matrix4x4.CreateScale(2, 2, 3) * Matrix4x4.CreateTranslation(1, 2, 0));
            var placed = Matrix4x4.CreateRotationY(.6f) * Matrix4x4.CreateTranslation(5, .1f, -4);
            var feet = Vector3.Transform(new Vector3(1, -.1f, 0), placed);
            Assert(!volume.Contains(feet, placed, .01f), "This case must reproduce feet below the raised trigger.");
            Assert(volume.ContainsCharacter(feet, placed, .01f), "The lower body must still open the door.");
            Assert(!volume.ContainsCharacter(feet + new Vector3(0, 6, 0), placed, .08f)
                && !volume.ContainsCharacter(feet - new Vector3(0, 6, 0), placed, .08f), "Other floors must remain excluded.");
        });
        await check("Sliding leaves use group axes, preserve scale/pivot and move opposite partners", () =>
        {
            var action = new DoorAction([4, 5, 0, 0], 1, .5f, MathF.PI / 2, 1.75f, 1, 1);
            var local = Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(3, 2, 1);
            var parent = Matrix4x4.CreateRotationY(.5f) * Matrix4x4.CreateTranslation(10, 0, -2);
            var closed = DoorMovement.Transform(action, 0, local, parent, 0);
            var open = DoorMovement.Transform(action, 0, local, parent, 1);
            Assert(Near(closed, local * parent), "Rest pose changed.");
            var expected = local; expected.Translation += new Vector3(-1.75f, 0, 0);
            Assert(Near(open, expected * parent), "Leaf rotation must not reverse the group-local slide direction.");
            var partner = DoorMovement.Transform(action, 1, Matrix4x4.Identity, Matrix4x4.Identity, 1);
            Assert(Vector3.Distance(partner.Translation, new(1.75f, 0, 0)) < .0001f, "Partner slides in the opposite direction.");
            Assert(Near(DoorMovement.Transform(action, 0, local, parent, 2), open), "Overshoot is clamped.");
            var vertical = DoorMovement.Transform(action with { Kind = 2 }, 0, local, parent, 1);
            expected = local; expected.Translation += new Vector3(0, 1.75f, 0);
            Assert(Near(vertical, expected * parent), "Vertical leaf motion.");
        });
        await check("Hinged doors rotate around their authored pivots and mirrored leaves oppose", () =>
        {
            var action = new DoorAction([1, 2, 0, 0], 0, .5f, MathF.PI / 2, 0, 2, 1);
            var local = Matrix4x4.CreateTranslation(2, 0, 0);
            var opened = DoorMovement.Transform(action, 0, local, Matrix4x4.Identity, 1);
            var other = DoorMovement.Transform(action, 1, local, Matrix4x4.Identity, 1);
            Assert(opened.Translation == local.Translation && other.Translation == local.Translation, "Hinges must not orbit around the furniture origin.");
            Assert(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, opened), -Vector3.UnitZ) < .0001f
                && Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, other), Vector3.UnitZ) < .0001f, "Opposing hinge directions.");
        });
        await check("Door collision releases immediately and returns only after a complete close", () =>
        {
            var door = new DoorMotionState(); door.Update(false, 0, .5f);
            Assert(door.Blocks && door.Progress == 0, "Initial closed door blocks.");
            door.Update(true, 100, .5f);
            Assert(!door.Blocks && door.TargetOpen && Math.Abs(door.Progress - .2f) < .0001f, "Opening must release the blocker immediately.");
            door.Update(true, 500, .5f); Assert(door.Progress == 1, "Full native duration.");
            door.Update(false, 750, .5f); Assert(door.TargetOpen && door.Progress == 1, "Short closing grace period.");
            door.Update(false, 850, .5f); Assert(!door.TargetOpen && !door.Blocks && door.Progress > 0, "Closing stays passable.");
            door.Update(false, 1400, .5f); Assert(door.Blocks && door.Progress == 0, "Fully closed restores collision.");
        });
        await check("Doors reverse without jumping and late arrivals cannot be trapped inside a blocker", () =>
        {
            var door = new DoorMotionState(); door.Update(true, 1000, .5f);
            Assert(door.Progress == 1 && !door.Blocks, "An occupied newly loaded door starts open.");
            door.Update(false, 1400, .5f); var closing = door.Progress;
            Assert(closing > 0 && closing < 1, "Door began closing.");
            door.Update(true, 1450, .5f); Assert(door.Progress > closing && door.Progress < 1 && !door.Blocks, "Reopen from the current pose.");
            var before = door.Progress; door.Update(true, 1400, .5f);
            Assert(door.Progress == before, "An out-of-order clock sample must not reverse motion.");
        });
        await check("SGB door metadata retains panel IDs, native timing, motion and range shapes", () =>
        {
            var read = SgbDoors.Read(Synthetic()); var action = read.Actions.Single();
            Assert(action.Panels.SequenceEqual(new byte[] { 4, 0, 0, 0 }) && action.Kind == 1 && action.Seconds == .5f
                && action.Distance == 1.75f && action.Axis == 1 && action.Curve == 1, "Controller fields were lost.");
            Assert(read.Shapes[5] == 1 && read.Shapes[6] == 1, "Trigger and blocker shapes were lost.");
            var unknown = Synthetic(); Int(unknown, 0x100, 99);
            Assert(SgbDoors.Read(unknown).Actions.Length == 0 && SgbDoors.Read(unknown).Shapes.Count == 0, "Other controller types must be ignored.");
            var disabled = Synthetic(); disabled[0x104] = 0;
            Assert(SgbDoors.Read(disabled).Actions.Length == 0, "Disabled door controller must remain disabled.");
            Assert(SgbDoors.Read([]).Actions.Length == 0 && SgbDoors.Read(new byte[200]).Actions.Length == 0, "Non-SGB input is not a controller.");
        });
        await check("SGB door metadata rejects truncated/overflowing offsets and unbounded controls", () =>
        {
            var truncated = Synthetic()[..0x120]; Reject(() => SgbDoors.Read(truncated));
            var overflow = Synthetic(); Int(overflow, 0x34, int.MaxValue); Reject(() => SgbDoors.Read(overflow));
            var count = Synthetic(); Int(count, 0x94, 50000); Reject(() => SgbDoors.Read(count));
            var invalidFrames = Synthetic(); Float(invalidFrames, 0x118, float.NaN); Reject(() => SgbDoors.Read(invalidFrames));
            var invalidAxis = Synthetic(); Int(invalidAxis, 0x12C, 8); Reject(() => SgbDoors.Read(invalidAxis));
            var badObjects = Synthetic(); Int(badObjects, 0x18C, 1000000); Reject(() => SgbDoors.Read(badObjects));
        });
    }

    // Construct only the minimum format fields needed by the parser; no game assets.
    private static byte[] Synthetic()
    {
        var bytes = new byte[0x280];
        Int(bytes, 0, 0x31424753); Int(bytes, 12, 0x314E4353);
        Int(bytes, 0x14, 0xC0 - 0x14); Int(bytes, 0x18, 1); Int(bytes, 0x34, 0x60 - 0x14);
        Int(bytes, 0x80, 0x90 - 0x60); Int(bytes, 0x90, 0x10); Int(bytes, 0x94, 1); Int(bytes, 0xA0, 0x100 - 0xA0);
        Int(bytes, 0xC8, 0x10); Int(bytes, 0xCC, 1); Int(bytes, 0xD0, 0x180 - 0xD0);
        Int(bytes, 0x188, 0x20); Int(bytes, 0x18C, 2); Int(bytes, 0x1A0, 0x200 - 0x1A0); Int(bytes, 0x1A4, 0x240 - 0x1A0);
        Int(bytes, 0x200, 58); Int(bytes, 0x204, 5); Int(bytes, 0x230, 1);
        Int(bytes, 0x240, 57); Int(bytes, 0x244, 6); Int(bytes, 0x270, 1);
        Int(bytes, 0x100, 1); bytes[0x104] = 1; bytes[0x110] = 4;
        Int(bytes, 0x114, 1); Float(bytes, 0x118, 30); Float(bytes, 0x11C, MathF.PI / 2); Float(bytes, 0x120, 1.75f);
        Int(bytes, 0x128, 1); Int(bytes, 0x12C, 1);
        return bytes;
    }
    private static void Int(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static void Float(byte[] bytes, int offset, float value) => Int(bytes, offset, BitConverter.SingleToInt32Bits(value));
    private static bool Near(Matrix4x4 a, Matrix4x4 b) => Vector3.DistanceSquared(a.Translation, b.Translation) < .000001f
        && Vector3.DistanceSquared(Vector3.TransformNormal(Vector3.UnitX, a), Vector3.TransformNormal(Vector3.UnitX, b)) < .000001f
        && Vector3.DistanceSquared(Vector3.TransformNormal(Vector3.UnitY, a), Vector3.TransformNormal(Vector3.UnitY, b)) < .000001f
        && Vector3.DistanceSquared(Vector3.TransformNormal(Vector3.UnitZ, a), Vector3.TransformNormal(Vector3.UnitZ, b)) < .000001f;
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; } catch (OverflowException) { return; }
        throw new Exception("Malformed door metadata was accepted.");
    }
}
