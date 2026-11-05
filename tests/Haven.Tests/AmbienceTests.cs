using System.Buffers.Binary;
using System.Numerics;
using Haven.Core;

internal static class AmbienceTests
{
    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Timeline targets mark switch/triggered members so only untimed autoplay effects stay passive", () =>
        {
            var targets = SgbTimelines.Targets(Fixture());
            Assert(targets.Count == 3 && targets.Contains(7) && targets.Contains(14) && targets.Contains(21) && !targets.Contains(8));
            var bytes = Fixture(); Int(bytes, 0x24, 0); Assert(SgbTimelines.Targets(bytes).Count == 0);
            bytes = Fixture(); Int(bytes, 0x64, 0); Assert(SgbTimelines.Targets(bytes).Count == 0);
            bytes = Fixture(); Int(bytes, 0x8C, 0); Assert(SgbTimelines.Targets(bytes).SetEquals([21u]));
            Assert(SgbTimelines.Targets([]).Count == 0);
            bytes = Fixture(); Int(bytes, 0, 0); Assert(SgbTimelines.Targets(bytes).Count == 0);
        });
        await check("Timeline parser rejects overflowed counts, out-of-range instances and truncated records", () =>
        {
            var bytes = Fixture(); Int(bytes, 0x64, 129); Reject(() => SgbTimelines.Targets(bytes));
            bytes = Fixture(); Int(bytes, 0x8C, 257); Reject(() => SgbTimelines.Targets(bytes));
            bytes = Fixture(); Int(bytes, 0x88, 0x7FFF0000); Reject(() => SgbTimelines.Targets(bytes));
            bytes = Fixture(); Int(bytes, 0x24, int.MaxValue); Reject(() => SgbTimelines.Targets(bytes));
            bytes = Fixture()[..0xD0]; Reject(() => SgbTimelines.Targets(bytes));
        });
        await check("Default switch state hides the parts and starts the effects of its autoplay timeline only", () =>
        {
            var state = SgbTimelines.DefaultState(Switch());
            Assert(state.Hidden.SetEquals([5u]) && state.Started.SetEquals([6u]));
            foreach (var ignored in new[] { Switch(autoplay: false), Switch(loop: true), Switch(tracks: 2) })
            {
                var none = SgbTimelines.DefaultState(ignored);
                Assert(none.Hidden.Count == 0 && none.Started.Count == 0);
            }
            var stopped = SgbTimelines.DefaultState(Switch(second: "C059"));
            Assert(stopped.Hidden.SetEquals([5u]) && stopped.Started.Count == 0);
            var bytes = Switch(); Int(bytes, 0xC4, 0x7FFFFFFF); Reject(() => SgbTimelines.DefaultState(bytes));
            bytes = Switch(); Int(bytes, 0xD0, 4); Reject(() => SgbTimelines.DefaultState(bytes));
            Assert(SgbTimelines.DefaultState(Fixture()).Hidden.Count == 0);
        });
        await check("Ambient budget keeps scene order, caps effects and merges nearby copies of one sound", () =>
        {
            static AmbientEmitter At(string key, string path, float x) => new(key, path, Matrix4x4.CreateTranslation(x, 0, 0), Vector4.One);
            var effects = AmbientBudget.Effects(Enumerable.Range(0, AmbientBudget.MaxEffects + 10).Select(i => At("fx-" + i, "a.avfx", i)));
            Assert(effects.Length == AmbientBudget.MaxEffects && effects[0].Key == "fx-0" && effects[^1].Key == "fx-" + (AmbientBudget.MaxEffects - 1));
            var sounds = AmbientBudget.Sounds([At("a", "fire.scd", 0), At("b", "fire.scd", 3), At("c", "water.scd", 1), At("d", "fire.scd", 7.9f), At("e", "fire.scd", 9)]);
            Assert(sounds.Select(s => s.Key).SequenceEqual(["a", "c", "e"]));
            // Six adjacent copies of one cake loop play once.
            Assert(AmbientBudget.Sounds(Enumerable.Range(0, 6).Select(i => At("cake-" + i, "cake.scd", i * 1.5f))).Length == 1);
            sounds = AmbientBudget.Sounds(Enumerable.Range(0, 10).Select(i => At("far-" + i, "fire.scd", i * 20)));
            Assert(sounds.Select(s => s.Key).SequenceEqual(["far-0", "far-1", "far-2"]));
            sounds = AmbientBudget.Sounds(Enumerable.Range(0, 40).Select(i => At("se-" + i, i + ".scd", 0)));
            Assert(sounds.Length == AmbientBudget.MaxSounds && sounds[^1].Key == "se-" + (AmbientBudget.MaxSounds - 1));
        });
        await check("Ambient sound volume: silenced copies take no place and merged copies keep the loudest", () =>
        {
            static AmbientEmitter At(string key, string path, float x, float volume) => new(key, path, Matrix4x4.CreateTranslation(x, 0, 0), Vector4.One, volume);
            var sounds = AmbientBudget.Sounds([At("muted", "fire.scd", 0, 0), At("quiet", "fire.scd", 2, .3f), At("loud", "fire.scd", 4, .8f), At("far", "fire.scd", 20, .5f)]);
            Assert(sounds.Select(s => (s.Key, s.Volume)).SequenceEqual([("quiet", .8f), ("far", .5f)]));
            // A silenced copy does not use one of a sound's three places.
            sounds = AmbientBudget.Sounds(Enumerable.Range(0, 3).Select(i => At("off-" + i, "fire.scd", i * 20, 0)).Concat(Enumerable.Range(0, 3).Select(i => At("on-" + i, "fire.scd", 100 + i * 20, 1))));
            Assert(sounds.Select(s => s.Key).SequenceEqual(["on-0", "on-1", "on-2"]));
        });
    }
    // One autoplay, non-looping switch timeline: instance key 100 -> member 5 (C019 hide), 101 -> member 6 (C058 start).
    private static byte[] Switch(bool autoplay = true, bool loop = false, string second = "C058", int tracks = 1)
    {
        var b = new byte[0x180]; Int(b, 0, 0x31424753); Int(b, 12, 0x314E4353);
        Int(b, 0x24, 0x60 - 0x14); Int(b, 0x60, 0x20); Int(b, 0x64, 1);
        Int(b, 0x88, 0xB0 - 0x80); Int(b, 0x8C, 2); Int(b, 0x94, 0xC0 - 0x80); b[0xA0] = (byte)(autoplay ? 1 : 0); b[0xA1] = (byte)(loop ? 1 : 0);
        Int(b, 0xB0, 100); Int(b, 0xB4, 5); Int(b, 0xB8, 101); Int(b, 0xBC, 6);
        var p = 0xC0; Tag(b, p, "TMLB"); Int(b, p + 4, 172); Int(b, p + 8, 6); p += 12;
        void Node(string tag, int size) { Tag(b, p, tag); Int(b, p + 4, size); }
        Node("TMAC", 28); b[p + 10] = 100; Int(b, p + 24, tracks); p += 28;
        Node("TMAC", 28); b[p + 10] = 101; Int(b, p + 24, 1); p += 28;
        Node("TMTR", 24); Int(b, p + 16, 1); p += 24;
        Node("TMTR", 24); Int(b, p + 16, 1); p += 24;
        Node("C019", 24); b[p + 20] = 0; p += 24;
        Node(second, 32);
        return b;
    }
    private static void Tag(byte[] b, int p, string tag) => System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(b, p);
    // SGB1/SCN1 with a two-entry timeline list: the first targets members 7 and 14, the second member 21.
    private static byte[] Fixture()
    {
        var b = new byte[0x120]; Int(b, 0, 0x31424753); Int(b, 12, 0x314E4353);
        Int(b, 0x24, 0x60 - 0x14); Int(b, 0x60, 0x20); Int(b, 0x64, 2);
        Int(b, 0x80, 1); Int(b, 0x88, 0x100 - 0x80); Int(b, 0x8C, 2);
        Int(b, 0xAC, 2); Int(b, 0xB4, 0x110 - 0xAC); Int(b, 0xB8, 1);
        Int(b, 0x104, 7); Int(b, 0x10C, 14); Int(b, 0x114, 21); return b;
    }
    private static void Int(byte[] b, int p, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p, 4), v);
    private static void Assert(bool value) { if (!value) throw new Exception("Furnishing ambience assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; } catch (OverflowException) { return; }
        throw new Exception("Invalid furnishing timeline data was accepted.");
    }
}
