using System.Buffers.Binary;
using System.Numerics;
using Haven.Core;

internal static class ClockTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Furniture clock hands display quarter hours and native per-minute hour movement", () =>
        {
            var minute = ClockMovement.Transform(false, Matrix4x4.Identity, Matrix4x4.Identity, 3 * 3600 + 15 * 60);
            Assert(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY, minute), Vector3.UnitX) < .0001f);
            var hour = ClockMovement.Transform(true, Matrix4x4.Identity, Matrix4x4.Identity, 3 * 3600 + 15 * 60);
            var angle = 3.25f / 12 * MathF.Tau;
            Assert(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY, hour), new(MathF.Sin(angle), MathF.Cos(angle), 0)) < .0001f);
            Assert(Near(hour, ClockMovement.Transform(true, Matrix4x4.Identity, Matrix4x4.Identity, 3 * 3600 + 15 * 60 + 59)));
            Assert(!Near(hour, ClockMovement.Transform(true, Matrix4x4.Identity, Matrix4x4.Identity, 3 * 3600 + 16 * 60)));
            Assert(!Near(minute, ClockMovement.Transform(false, Matrix4x4.Identity, Matrix4x4.Identity, 3 * 3600 + 15 * 60 + 1)));
        });
        await check("Clock hands preserve authored pivots, scale and nested orientation", () =>
        {
            var local = Matrix4x4.CreateScale(.7f) * Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(1, 2, 3);
            var parent = Matrix4x4.CreateRotationY(.4f) * Matrix4x4.CreateTranslation(9, 5, 7);
            Assert(Near(ClockMovement.Transform(false, local, parent, 0), local * parent));
            var atQuarter = ClockMovement.Transform(false, local, parent, 15 * 60);
            Assert(Vector3.Distance(atQuarter.Translation, (local * parent).Translation) < .0001f);
            Assert(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY, atQuarter), Vector3.TransformNormal(Vector3.UnitX, local * parent)) < .0001f);
        });
        await check("Clocks wrap Eorzea days and twelve-hour faces independently of machine time", () =>
        {
            foreach (var hour in new[] { false, true })
            {
                Matrix4x4 At(long time) => ClockMovement.Transform(hour, Matrix4x4.Identity, Matrix4x4.Identity, time);
                Assert(Near(At(1234), At(86400 * 1000L + 1234)) && Near(At(-1), At(86399)));
                Assert(Near(At(0), At(12 * 3600)) && Near(At(0), At(24 * 3600)));
            }
        });
        await check("Clock metadata reads authored hand identities and ignores inactive/other controllers", () =>
        {
            var bytes = Fixture();
            Assert(SgbClocks.Read(bytes).Single() == new ClockAction(3, 2));
            bytes[0x104] = 0; Assert(SgbClocks.Read(bytes).Length == 0);
            bytes[0x104] = 1; Int(bytes, 0x100, 1); Assert(SgbClocks.Read(bytes).Length == 0);
            Assert(SgbClocks.Read([]).Length == 0 && SgbClocks.Read(new byte[200]).Length == 0);
        });
        await check("Clock metadata rejects malformed hand IDs, counts, offsets and truncated records", () =>
        {
            var bytes = Fixture(); bytes[0x111] = 3; Reject(() => SgbClocks.Read(bytes));
            bytes = Fixture(); bytes[0x110] = 0; Reject(() => SgbClocks.Read(bytes));
            bytes = Fixture(); Int(bytes, 0x94, int.MaxValue); Reject(() => SgbClocks.Read(bytes));
            bytes = Fixture(); Int(bytes, 0x34, int.MaxValue); Reject(() => SgbClocks.Read(bytes));
            bytes = Fixture()[..0x112]; Reject(() => SgbClocks.Read(bytes));
        });
    }
    private static byte[] Fixture()
    {
        var bytes = new byte[0x140];
        Int(bytes, 0, 0x31424753); Int(bytes, 12, 0x314E4353);
        Int(bytes, 0x34, 0x60 - 0x14); Int(bytes, 0x80, 0x90 - 0x60);
        Int(bytes, 0x90, 0x10); Int(bytes, 0x94, 1); Int(bytes, 0xA0, 0x100 - 0xA0);
        Int(bytes, 0x100, 4); bytes[0x104] = 1; bytes[0x110] = 3; bytes[0x111] = 2;
        return bytes;
    }
    private static void Int(byte[] data, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);
    private static bool Near(Matrix4x4 a, Matrix4x4 b) => Vector3.DistanceSquared(a.Translation, b.Translation) < .000001f
        && Vector3.DistanceSquared(Vector3.TransformNormal(Vector3.UnitX, a), Vector3.TransformNormal(Vector3.UnitX, b)) < .000001f
        && Vector3.DistanceSquared(Vector3.TransformNormal(Vector3.UnitY, a), Vector3.TransformNormal(Vector3.UnitY, b)) < .000001f;
    private static void Assert(bool value) { if (!value) throw new Exception("Clock assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; } catch (OverflowException) { return; }
        throw new Exception("Malformed clock metadata was accepted.");
    }
}
