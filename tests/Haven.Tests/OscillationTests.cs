using System.Buffers.Binary;
using System.Numerics;
using Haven.Core;

internal static class OscillationTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Oscillation metadata reads each looping channel's end, random fraction, timing, easing and mode", () =>
        {
            var b = Fixture(); var action = SgbOscillations.Read(b).Actions.Single();
            Assert(action.Translation == new TransformTimer(new(0, .2f, 0), 0, 60, 0, 1) && action.Rotation == null && action.Scale == null
                && action.Members.SequenceEqual(new byte[] { 1 }));
            Float(b, 0x150, 3); Int(b, 0x15C, 1); Int(b, 0x160, 2);
            Assert(SgbOscillations.Read(b).Actions.Single().Translation == new TransformTimer(new(0, .2f, 0), 3, 60, 1, 2));
            b[0x180] = 1; Float(b, 0x184, .1f); Int(b, 0x1A0, 2); // A rotation channel beside it.
            Assert(SgbOscillations.Read(b).Actions.Single().Rotation == new TransformTimer(new(.1f, 0, 0), 0, 30, 0, 2));
            b[0x118] = 0; Assert(SgbOscillations.Read(b).HasTransform && SgbOscillations.Read(b).Actions.Length == 0); // One-shot.
        });
        await check("Bobbing returns to rest and pendulums alternate signed authored amplitudes", () =>
        {
            var bob = new OscillationAction([1], new(new(0, .2f, 0), 0, 60, 0, 1), null, null);
            Matrix4x4 At(OscillationAction a, long time) => OscillationMovement.Transform(a, Matrix4x4.Identity, Matrix4x4.Identity, time);
            Assert(At(bob, 0).Translation == Vector3.Zero && At(bob, 2000).Translation == new Vector3(0, .2f, 0) && At(bob, 4000).Translation == Vector3.Zero);
            var swing = new OscillationAction([1], null, new(new(0, 0, .1f), 0, 60, 0, 2), null);
            var positive = Vector3.TransformNormal(Vector3.UnitX, At(swing, 2000));
            var negative = Vector3.TransformNormal(Vector3.UnitX, At(swing, 6000));
            Assert(positive.Y > 0 && negative.Y < 0 && Math.Abs(positive.Y + negative.Y) < .00001f);
            Assert(Near(At(swing, 0), At(swing, 4000)) && Near(At(swing, 0), At(swing, 8000)));
            // Mode 0 runs to its end and starts again: a mobile's full turn.
            var turn = new TransformTimer(new(0, MathF.Tau, 0), 0, 480, 0, 0);
            Assert(Math.Abs(OscillationMovement.Value(turn, 8000, 0, 2).Y - MathF.Tau / 2) < .0001f && OscillationMovement.Value(turn, 16000, 0, 2).Y == 0);
            // Scale eases from 1 (a jellyfish lamp's pulse).
            var pulse = new TransformTimer(new(1, 1.2f, 1), 0, 60, 0, 1);
            Assert(OscillationMovement.Value(pulse, 0, 0, 3) == Vector3.One && Vector3.Distance(OscillationMovement.Value(pulse, 2000, 0, 3), new(1, 1.2f, 1)) < .0001f);
        });
        await check("Oscillation easing follows the client's cubic curves on each half swing", () =>
        {
            Assert(OscillationMovement.Ease(.5f, 0, false) == .5f && OscillationMovement.Ease(.5f, 1, false) == .5f);
            Assert(Math.Abs(OscillationMovement.Ease(.25f, 1, false) - .0625f) < .0001f); // In-out: slow at both ends.
            Assert(Math.Abs(OscillationMovement.Ease(.5f, 2, false) - .125f) < .0001f && Math.Abs(OscillationMovement.Ease(.5f, 2, true) - .875f) < .0001f);
            Assert(Math.Abs(OscillationMovement.Ease(.5f, 3, false) - .875f) < .0001f && Math.Abs(OscillationMovement.Ease(.5f, 3, true) - .125f) < .0001f);
            foreach (var easing in new[] { 0, 1, 2, 3 })
                Assert(OscillationMovement.Ease(0, easing, false) == 0 && Math.Abs(OscillationMovement.Ease(1, easing, true) - 1) < .0001f);
        });
        await check("A wind chime's random swing is rolled per cycle, the same for every client, different between copies", () =>
        {
            var chime = new TransformTimer(new(.07f, 0, .087f), 3, 30, 0, 2);
            // Peaks of the positive swing, one per two cycles.
            var peaks = Enumerable.Range(0, 8).Select(cycle => OscillationMovement.Value(chime, cycle * 4000 + 1000, 42, 2)).ToArray();
            Assert(peaks.Distinct().Count() > 4 && peaks.All(p => p.X <= .07f + 1e-6f && p.X > -.15f && p.Y == 0));
            Assert(peaks.SequenceEqual(Enumerable.Range(0, 8).Select(cycle => OscillationMovement.Value(chime, cycle * 4000 + 1000, 42, 2))));
            Assert(!peaks.SequenceEqual(Enumerable.Range(0, 8).Select(cycle => OscillationMovement.Value(chime, cycle * 4000 + 1000, 7, 2))));
            // Without a random fraction the end is exact.
            Assert(OscillationMovement.Value(chime with { Random = 0 }, 1000, 42, 2) == new Vector3(.07f, 0, .087f));
        });
        await check("Oscillation preserves nested pivots/scale and wraps long absolute timestamps", () =>
        {
            var action = new OscillationAction([1], null, new(new(0, 0, .1f), 0, 60, 0, 2), null);
            var local = Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationY(.5f) * Matrix4x4.CreateTranslation(1, 2, 3);
            var parent = Matrix4x4.CreateRotationX(.8f) * Matrix4x4.CreateTranslation(10, 3, 5);
            Matrix4x4 At(long ms) => OscillationMovement.Transform(action, local, parent, ms);
            Assert(Near(At(0), local * parent) && Near(At(1000), At(1_000_000_001_000)) && Near(At(-1000), At(7000)));
            Assert(Vector3.Distance(At(2000).Translation, (local * parent).Translation) < .00001f);
        });
        await check("Controllers combine spins and swings on one member, and a switch holds or starts them", () =>
        {
            var rest = Matrix4x4.CreateTranslation(1, 2, 3);
            var motion = new ControllerMotion(rest, [new SpinAction(1, 1, 4, false)], [new OscillationAction([1], new(new(0, .5f, 0), 0, 30, 0, 1), null, null)], 1);
            var posed = motion.Transform(1000);
            Assert(Vector3.Distance(posed.Translation, new(1, 2.5f, 3)) < .0001f);
            Assert(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, posed), -Vector3.UnitZ) < .0001f);
            Assert(Near(motion.Transform(0), rest));
            LoopLevel[] levels = [new(rest, motion, WhenOn: true)];
            // Off and never run: authored pose. Off after running: held where it stopped. On: running.
            Assert(Near(LoopMovement.Transform(levels, 1000, on: false), rest));
            Assert(Near(LoopMovement.Transform(levels, 5000, on: false, sinceSwitch: 4000), posed));
            Assert(Near(LoopMovement.Transform(levels, 1000, on: true), posed));
        });
        await check("Oscillation parser rejects malformed offsets/members/timing without using game assets", () =>
        {
            Assert(!SgbOscillations.Read([]).HasTransform);
            var b = Fixture(); Int(b, 0x34, int.MaxValue); Reject(() => SgbOscillations.Read(b));
            b = Fixture(); Int(b, 0x114, 100); Reject(() => SgbOscillations.Read(b));
            b = Fixture(); Float(b, 0x148, float.NaN); Reject(() => SgbOscillations.Read(b));
            b = Fixture(); Int(b, 0x154, 0); Reject(() => SgbOscillations.Read(b));
            b = Fixture(); Int(b, 0x160, 3); Reject(() => SgbOscillations.Read(b));
            b = Fixture(); Float(b, 0x150, -1); Reject(() => SgbOscillations.Read(b));
            b = Fixture()[..0x158]; Reject(() => SgbOscillations.Read(b));
        });
    }
    // One looping type-5 action on member 1: translation timer at 0x140 (on, +0.2 Y, 60 frames, mode 1); rotation
    // timer at 0x180 and scale timer at 0x1B0 off.
    private static byte[] Fixture()
    {
        var b = new byte[0x200]; Int(b, 0, 0x31424753); Int(b, 12, 0x314E4353);
        Int(b, 0x34, 0x60 - 0x14); Int(b, 0x80, 0x30); Int(b, 0x90, 0x10); Int(b, 0x94, 1); Int(b, 0xA0, 0x60);
        Int(b, 0x100, 5); b[0x104] = 1; Int(b, 0x110, 0xE0); Int(b, 0x114, 1); b[0x118] = 1;
        Int(b, 0x120, 0x40); Int(b, 0x124, 0x80); Int(b, 0x128, 0xB0); b[0x1E0] = 1;
        b[0x140] = 1; Float(b, 0x148, .2f); Int(b, 0x154, 60); Int(b, 0x160, 1);
        Int(b, 0x194, 30); Int(b, 0x1C4, 30); return b;
    }
    private static void Int(byte[] b, int p, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p, 4), v);
    private static void Float(byte[] b, int p, float v) => Int(b, p, BitConverter.SingleToInt32Bits(v));
    private static bool Near(Matrix4x4 a, Matrix4x4 b) => Vector3.DistanceSquared(a.Translation, b.Translation) < .000001f
        && Vector3.DistanceSquared(Vector3.TransformNormal(Vector3.UnitX, a), Vector3.TransformNormal(Vector3.UnitX, b)) < .000001f;
    private static void Assert(bool value) { if (!value) throw new Exception("Oscillation assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; } catch (OverflowException) { return; }
        throw new Exception("Invalid oscillation accepted.");
    }
}
