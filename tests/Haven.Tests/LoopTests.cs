using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Haven.Core;

internal static class LoopTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Loop curves pass through their keys, ease with tangents and hold outside the keyed range", () =>
        {
            LoopKey[] keys = [new(0, 0, 0), new(12, 2, 0), new(60, 0, 0)];
            Assert(Near(LoopCurve.Sample(keys, 0, 9), 0) && Near(LoopCurve.Sample(keys, 12, 9), 2) && Near(LoopCurve.Sample(keys, 60, 9), 0));
            Assert(Near(LoopCurve.Sample(keys, 6, 9), 1));                 // zero tangents: symmetric ease, midpoint exact
            Assert(LoopCurve.Sample(keys, 3, 9) < 0.5f);                    // eases in rather than moving linearly
            Assert(Near(LoopCurve.Sample([new(0, 0, 1), new(10, 10, 1)], 5, 0), 5)); // matching tangents give a straight line
            Assert(Near(LoopCurve.Sample([], 5, 7), 7) && Near(LoopCurve.Sample(keys, -1, 9), 0) && Near(LoopCurve.Sample(keys, 99, 9), 0));
        });
        await check("Loop transforms use degrees at 30 frames per second, repeat, and compose nested levels part first", () =>
        {
            var curve = Spin(60);
            var quarter = curve.Transform(1000);                          // 30 frames of a 0-90 degree ramp over 60 frames
            Assert(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ, quarter), Vector3.TransformNormal(Vector3.UnitZ, Matrix4x4.CreateRotationY(MathF.PI / 4))) < .001f);
            Assert(Near(curve.Transform(500), curve.Transform(2500)) && Near(curve.Transform(-1500), curve.Transform(500)));
            var part = Matrix4x4.CreateTranslation(0, 0, 1); var group = Matrix4x4.CreateTranslation(5, 0, 0);
            LoopLevel[] still = [new(group, null), new(part, null)];
            Assert(Near(LoopMovement.Transform(still, 1234), part * group));
            LoopLevel[] moving = [new(group, curve), new(part, null)];
            Assert(Near(LoopMovement.Transform(moving, 1000), part * curve.Transform(1000)));
        });
        await check("Path-bound groups lap their ring at constant speed, face their travel and reject invalid paths", () =>
        {
            Vector3[] square = [new(0, 0, 0), new(2, 0, 0), new(2, 0, 2), new(0, 0, 2)];
            var offset = Matrix4x4.CreateTranslation(10, 1, 0);
            var path = new PathMotion(square, true, 300, true, offset);         // 300 frames = 10 seconds per lap
            Assert(Vector3.Distance(path.Transform(0).Translation, new(10, 1, 0)) < .001f);
            Assert(Near(path.Transform(2500), path.Transform(12500)));         // repeats every lap
            var travel = path.Transform(100).Translation - path.Transform(0).Translation;
            var facing = Vector3.TransformNormal(Vector3.UnitZ, path.Transform(50));
            Assert(Vector3.Dot(Vector3.Normalize(travel), facing) > .9f);     // +Z faces the direction of travel
            Assert(new[] { 1250L, 3750, 6250, 8750 }.All(t => path.Transform(t).Translation.Y == 1));
            var still = new PathMotion(square, true, 300, false, Matrix4x4.Identity);
            Assert(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ, still.Transform(1234)), Vector3.UnitZ) < .0001f);
            Reject(() => new PathMotion([Vector3.Zero], true, 300, true, Matrix4x4.Identity));
            Reject(() => new PathMotion([Vector3.Zero, Vector3.Zero], true, 300, true, Matrix4x4.Identity));
            Reject(() => new PathMotion(square, true, 0, true, Matrix4x4.Identity));
            Reject(() => new PathMotion([new(float.NaN, 0, 0), Vector3.One], false, 30, true, Matrix4x4.Identity));
        });
        await check("Looping C013 timelines decode their TMFC curves; non-looping or malformed data is ignored", () =>
        {
            var loops = SgbTimelines.Loops(Build(loop: true));
            Assert(loops.Count == 1 && loops[7].Frames == 60);
            var curve = loops[7];
            Assert(curve.Channels[4].Length == 2 && curve.Channels[4][1] == new LoopKey(60, 90, 0) && curve.Channels[0].Length == 0 && curve.Components == 2);
            // Components outside the mask and unkeyed channels keep the authored pose (the garden bamboo only tips).
            var rested = curve.WithRest(new(1, 2, 3), new(0, 0, MathF.PI / 2), new(2, 2, 2));
            var pose = rested.Transform(0);
            Assert(Vector3.Distance(pose.Translation, new(1, 2, 3)) < .0001f && Math.Abs(Vector3.TransformNormal(Vector3.UnitX, pose).Length() - 2) < .0001f);
            Assert(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, pose), new(0, 2, 0)) < .001f);
            Assert(SgbTimelines.Loops(Build(loop: false)).Count == 0);
            Assert(SgbTimelines.Loops(Build(loop: true, keys: 999)).Count == 0);
            Assert(SgbTimelines.Loops(Build(loop: true, frames: 0)).Count == 0);
            Assert(SgbTimelines.Loops([]).Count == 0);
        });
        await check("SGB Euler angles turn X, then Y, then Z, as the client does", () =>
        {
            // The client's conversion builds q = qz * qy * qx from half-angle sines and cosines.
            static Quaternion Client(float x, float y, float z) => new Quaternion(0, 0, MathF.Sin(z / 2), MathF.Cos(z / 2))
                * new Quaternion(0, MathF.Sin(y / 2), 0, MathF.Cos(y / 2)) * new Quaternion(MathF.Sin(x / 2), 0, 0, MathF.Cos(x / 2));
            foreach (var (x, y, z) in new[] { (0.521f, -0.314f, -0.224f), (1.125f, 0.437f, -2.486f), (-2f, 1f, 3f) })
            {
                Assert(Near(LayoutRotation.Matrix(x, y, z), Matrix4x4.CreateFromQuaternion(Client(x, y, z))));
                var q = LayoutRotation.Quaternion(x, y, z); var c = Client(x, y, z);
                Assert(MathF.Abs(MathF.Abs(Quaternion.Dot(q, c)) - 1) < 1e-5f);
            }
            // A mirrored member authored as (pi, a, pi) is a yaw of pi - a: the Invincible II's left propeller pods.
            Assert(Near(LayoutRotation.Matrix(MathF.PI, 0.3f, MathF.PI), Matrix4x4.CreateRotationY(MathF.PI - 0.3f)));
            Assert(!Near(Matrix4x4.CreateFromYawPitchRoll(0.3f, MathF.PI, MathF.PI), Matrix4x4.CreateRotationY(MathF.PI - 0.3f)));
            // Curve rotations (degrees) and C018 snaps use the same order.
            var curve = new LoopCurve(30, [[], [], [], [new(0, 180, 0)], [new(0, 30, 0)], [new(0, 180, 0)], [], [], []]);
            Assert(Near(curve.At(0), Matrix4x4.CreateRotationY(MathF.PI - MathF.PI / 6)));
            Assert(Near(SideMotion.SnapPose([1, 2, 3, MathF.PI, 0.3f, MathF.PI, 1, 1, 1]), Matrix4x4.CreateRotationY(MathF.PI - 0.3f) * Matrix4x4.CreateTranslation(1, 2, 3)));
        });
        await check("Linear curve keys ignore their stored tangents; Hermite keys use their out and in tangents", () =>
        {
            // An airship propeller: a linear key whose stale 0.75 tangent would otherwise tip the blade off zero.
            var linear = SgbTimelines.Loops(Build(loop: true, linear: true, tangent: .75f))[7].Channels[4];
            Assert(linear[0].Linear && !linear[1].Linear && Near(linear[0].OutTangent, .75f));
            Assert(Near(LoopCurve.Sample(linear, 30, 0), 45) && Near(LoopCurve.Sample(linear, 15, 0), 22.5f));
            // A two-key looping channel turns steadily even unflagged: the Invincible II's small propellers carry stale
            // tangents with no linear flag. Curves with more keys keep their tangents.
            var looped = SgbTimelines.Loops(Build(loop: true, tangent: .75f))[7].Channels[4];
            Assert(looped[0].Linear && Near(LoopCurve.Sample(looped, 15, 0), 22.5f));
            LoopKey[] three = [new(0, 0, .75f), new(30, 45, .75f), new(60, 90, .75f)];
            var curve = new LoopCurve(60, [[], [], [], [], three, [], [], [], []]).Looped();
            Assert(!curve.Channels[4][0].Linear && !Near(LoopCurve.Sample(curve.Channels[4], 10, 0), 15));
            // A music box's wind-down: the segment leaves at its out tangent and arrives at the next key's in tangent.
            LoopKey[] split = [new(0, 0, 0) { OutTangent = 3 }, new(10, 10, 0)];
            Assert(LoopCurve.Sample(split, 1, 0) > 1.5f && Near(LoopCurve.Sample(split, 10, 0), 10));
        });
    }

    private static LoopCurve Spin(int frames)
    {
        var channels = new LoopKey[9][]; for (var i = 0; i < 9; i++) channels[i] = [];
        channels[4] = [new(0, 0, 90f / frames), new(frames, 90, 90f / frames)];
        return new(frames, channels);
    }

    // One autoplay timeline: instance key 100 -> member 7, a C013 command naming TMFC curve set 8, whose single
    // channel (0x44, rotation Y) ramps 0 -> 90 degrees over 60 frames.
    private static byte[] Build(bool loop, int keys = 2, int frames = 60, bool linear = false, float tangent = 0)
    {
        var b = new byte[0x400]; Int(b, 0, 0x31424753); Int(b, 12, 0x314E4353);
        Int(b, 0x24, 0x60 - 0x14); Int(b, 0x60, 0x20); Int(b, 0x64, 1);
        const int record = 0x80; b[record + 0x20] = 1; b[record + 0x21] = (byte)(loop ? 1 : 0);
        Int(b, record + 4, 0xB0 - record); Encoding.ASCII.GetBytes("loop").CopyTo(b, 0xB0);
        Int(b, record + 8, 0xC0 - record); Int(b, record + 0xC, 1); Int(b, 0xC0, 100); Int(b, 0xC4, 7);
        const int tmlb = 0xD0; Int(b, record + 0x14, tmlb - record);
        Encoding.ASCII.GetBytes("TMLB").CopyTo(b, tmlb); Int(b, tmlb + 8, 4);
        var p = tmlb + 12;
        Node("TMAC", 28); b[p + 10] = 100; Int(b, p + 24, 1); p += 28;
        Node("TMTR", 24); Int(b, p + 16, 1); p += 24;
        Node("C013", 28); Int(b, p + 0x0C, frames); Int(b, p + 0x14, 8); Int(b, p + 0x18, 2); p += 28;
        var curves = p; Node("TMFC", 32); b[p + 8] = 8; p += 32;
        Int(b, tmlb + 4, p - tmlb);
        var table = p + 16; Int(b, curves + 0x0C, table - (curves + 0x10)); Int(b, curves + 0x10, 1);
        b[table] = 0x44; b[table + 3] = 0xFF; Int(b, table + 4, 16); Int(b, table + 8, keys);
        var key = table + 16;
        Float(b, key, 0); Float(b, key + 8, 0); Float(b, key + 24, 60); Float(b, key + 32, 90);
        foreach (var at in new[] { key, key + 24 }) { Float(b, at + 12, tangent); Float(b, at + 16, tangent); }
        if (linear) Int(b, key + 20, 1);
        return b;
        void Node(string tag, int size) { Encoding.ASCII.GetBytes(tag).CopyTo(b, p); Int(b, p + 4, size); }
    }
    private static void Int(byte[] b, int p, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p, 4), v);
    private static void Float(byte[] b, int p, float v) => Int(b, p, BitConverter.SingleToInt32Bits(v));
    private static bool Near(float a, float b) => Math.Abs(a - b) < .0001f;
    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++) if (Math.Abs(a[r, c] - b[r, c]) > .0001f) return false;
        return true;
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Furnishing loop assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("An invalid furnishing path was accepted.");
    }
}
