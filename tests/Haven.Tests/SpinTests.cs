using System.Buffers.Binary;
using System.Numerics;
using Haven.Core;

internal static class SpinTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Passive spin metadata preserves axis, signed native 30-Hz period and companion-effect flags", () =>
        {
            var bytes = Fixture(); var spin = SgbSpins.Read(bytes).Single();
            Assert(spin == new SpinAction(2, 1, 4, false));
            Float(bytes, 0x118, -120); Assert(SgbSpins.Read(bytes).Single().SignedSeconds == -4);
            bytes[0x121] = 1; Assert(SgbSpins.Read(bytes).Single().HasEffects);
            bytes[0x121] = 0; bytes[0x126] = 1; Assert(SgbSpins.Read(bytes).Single().HasEffects);
            bytes[0x104] = 0; Assert(SgbSpins.Read(bytes).Length == 0);
            Int(bytes, 0x100, 4); Assert(SgbSpins.Read(bytes).Length == 0);
        });
        await check("Passive spins turn at the authored rate/direction around X, Y and Z", () =>
        {
            foreach (var axis in new[] { 0, 1, 2 })
            {
                var spin = new SpinAction(2, axis, 4, false);
                var vector = axis == 0 ? Vector3.UnitY : Vector3.UnitX;
                var expected = axis == 0 ? Vector3.UnitZ : axis == 1 ? -Vector3.UnitZ : Vector3.UnitY;
                var quarter = SpinMovement.Transform(spin, Matrix4x4.Identity, Matrix4x4.Identity, 1000);
                Assert(Vector3.Distance(Vector3.TransformNormal(vector, quarter), expected) < .0001f);
                var reverse = SpinMovement.Transform(spin with { SignedSeconds = -4 }, Matrix4x4.Identity, Matrix4x4.Identity, 1000);
                Assert(Vector3.Distance(Vector3.TransformNormal(vector, reverse), -expected) < .0001f);
            }
        });
        await check("Passive spins keep scaled nested pivots and wrap absolute time without drift", () =>
        {
            var spin = new SpinAction(2, 2, 4, false);
            var local = Matrix4x4.CreateScale(.7f) * Matrix4x4.CreateRotationY(.4f) * Matrix4x4.CreateTranslation(2, 3, 4);
            var parent = Matrix4x4.CreateRotationX(.5f) * Matrix4x4.CreateTranslation(10, 0, -5);
            Matrix4x4 At(long ms) => SpinMovement.Transform(spin, local, parent, ms);
            Assert(Near(At(0), local * parent) && Near(At(1000), At(1_000_000_001_000)) && Near(At(-1000), At(3000)));
            var posed = At(1000);
            Assert(Vector3.Distance(posed.Translation, (local * parent).Translation) < .0001f);
            Assert(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, posed), Vector3.TransformNormal(Vector3.UnitY, local * parent)) < .0001f);
        });
        await check("Passive rotation parser rejects truncated/overflowed records and invalid periods/axes", () =>
        {
            Assert(SgbSpins.Read([]).Length == 0);
            var bytes = Fixture(); Float(bytes, 0x118, float.NaN); Reject(() => SgbSpins.Read(bytes));
            bytes = Fixture(); Float(bytes, 0x118, 0); Reject(() => SgbSpins.Read(bytes));
            bytes = Fixture(); Int(bytes, 0x114, 4); Reject(() => SgbSpins.Read(bytes));
            bytes = Fixture(); Int(bytes, 0x94, 100); Reject(() => SgbSpins.Read(bytes));
            bytes = Fixture(); Int(bytes, 0x34, int.MaxValue); Reject(() => SgbSpins.Read(bytes));
            bytes = Fixture()[..0x125]; Reject(() => SgbSpins.Read(bytes));
        });
    }
    private static byte[] Fixture()
    {
        var b = new byte[0x140]; Int(b, 0, 0x31424753); Int(b, 12, 0x314E4353);
        Int(b, 0x34, 0x60 - 0x14); Int(b, 0x80, 0x30); Int(b, 0x90, 0x10); Int(b, 0x94, 1); Int(b, 0xA0, 0x60);
        Int(b, 0x100, 2); b[0x104] = 1; b[0x110] = 2; Int(b, 0x114, 1); Float(b, 0x118, 120); return b;
    }
    private static void Int(byte[] b, int p, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p, 4), v);
    private static void Float(byte[] b, int p, float v) => Int(b, p, BitConverter.SingleToInt32Bits(v));
    private static bool Near(Matrix4x4 a, Matrix4x4 b) => Vector3.DistanceSquared(a.Translation, b.Translation) < .000001f
        && Vector3.DistanceSquared(Vector3.TransformNormal(Vector3.UnitX, a), Vector3.TransformNormal(Vector3.UnitX, b)) < .000001f
        && Vector3.DistanceSquared(Vector3.TransformNormal(Vector3.UnitY, a), Vector3.TransformNormal(Vector3.UnitY, b)) < .000001f;
    private static void Assert(bool value) { if (!value) throw new Exception("Passive rotation assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; } catch (OverflowException) { return; }
        throw new Exception("Invalid passive rotation was accepted.");
    }
}
