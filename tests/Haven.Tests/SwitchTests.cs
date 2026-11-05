using System.Buffers.Binary;
using System.Text;
using Haven.Core;

internal static class SwitchTests
{
    private sealed record Command(int Key, uint Member, string Tag, int Value = 0, ushort Frame = 0, int Word = 0);
    private sealed record Timeline(string Type, bool AutoPlay, bool Loop, params Command[] Commands)
    {
        // Frames in the TMDH header; 0 writes none.
        public ushort Duration { get; init; }
        // The timeline's SubId, by which C082 plays it.
        public int Id { get; init; }
    }

    public static async Task Run(Func<string, Action, Task> check)
    {
        await check("Furnishing switch pairs record each part's shown, started and lit state on both sides", () =>
        {
            var lantern = Build(
                new Timeline("off", false, false, new Command(100, 7, "C110"), new Command(101, 14, "C109", 0), new Command(102, 21, "C059")),
                new Timeline("on", false, false, new Command(100, 7, "C110", 1), new Command(101, 14, "C109", 1), new Command(102, 21, "C058")));
            var state = SgbTimelines.Switch(lantern)!;
            Assert(state.DefaultOn == null && state.Members.Count == 2);
            Assert(state.Members[14] == new StateActivity(true, false) && state.Members[21] == new StateActivity(true, false));
            var fruit = Build(
                new Timeline("on", true, false, new Command(100, 5, "C019", 1), new Command(101, 6, "C019", 0)),
                new Timeline("off", false, false, new Command(100, 5, "C019", 0), new Command(101, 6, "C019", 1)));
            var served = SgbTimelines.Switch(fruit)!;
            Assert(served.DefaultOn == true && served.Members[5] == new StateActivity(true, false) && served.Members[6] == new StateActivity(false, true));
            Assert(SgbTimelines.Switch(Build(new Timeline("of", true, false, new Command(100, 5, "C019", 0)), new Timeline("on", false, false)))!.DefaultOn == false);
        });
        await check("A switch's glow fades between lit and dark over its frames, as the client writes it", () =>
        {
            // A lamp shade: lit on, dark off, over 30 frames (1 s).
            var shade = new SwitchGlow(new GlowFade(true, 0, 30), new GlowFade(false, 0, 30));
            Assert(shade.Byte(true, long.MaxValue) == 255 && shade.Byte(false, long.MaxValue) == 0);
            Assert(shade.Byte(false, 0) == 255 && shade.Byte(false, 500) == 128 && shade.Byte(false, 1000) == 0 && shade.Byte(true, 500) == 128);
            // A side that does not address the part leaves the authored full glow; an inverted pair goes dark when on.
            Assert(new SwitchGlow(new GlowFade(true, 0, 25), null).Byte(false, long.MaxValue) == 255);
            Assert(new SwitchGlow(new GlowFade(false, 0, 10), new GlowFade(true, 0, 10)).Byte(true, long.MaxValue) == 0);
            // A later start holds the previous level until its frame; one frame is at once.
            var late = new SwitchGlow(new GlowFade(true, 27, 23), new GlowFade(false, 0, 23));
            Assert(late.Byte(true, 800) == 0 && late.Byte(true, 2000) == 255);
            Assert(new SwitchGlow(new GlowFade(true, 0, 1), new GlowFade(false, 0, 1)).Byte(true, 0) == 255);
        });
        await check("A looping glow flickers every lap from where its last fade settled", () =>
        {
            // The Enigma Partition's lamp: dark over 3 frames at 0, lit at once at 3, again at 49/52 and 65/68, in 90.
            var flicker = new LoopGlow(90, [new(false, 0, 3), new(true, 3, 1), new(false, 49, 3), new(true, 52, 1), new(false, 65, 3), new(true, 68, 1)]);
            long Ms(double frame) => (long)(frame * 1000 / 30);
            Assert(flicker.Byte(Ms(30)) == 255 && flicker.Byte(Ms(52.5)) == 255 && flicker.Byte(Ms(80)) == 255);
            Assert(flicker.Byte(0) == 255 && flicker.Byte(Ms(1.5)) == 128 && flicker.Byte(Ms(2.9)) < 16);
            Assert(flicker.Byte(Ms(50.5)) == 128 && flicker.Byte(Ms(90 + 30)) == 255 && flicker.Byte(Ms(-60)) == 255);
        });
        await check("Switch sounds keep each side's start frame and the frame that side stops them at", () =>
        {
            // A Carbuncle Lamp: on plays a chime stopped at frame 72 and starts a hum; off stops both and plays nothing.
            var lamp = SgbTimelines.Switch(Build(
                new Timeline("on", false, false, new Command(100, 16, "C056"), new Command(101, 16, "C057", Frame: 72), new Command(102, 17, "C056", Frame: 3)),
                new Timeline("off", false, false, new Command(100, 16, "C057"), new Command(102, 17, "C057"))))!;
            Assert(lamp.Sounds.SequenceEqual([new SwitchSound(16, true, 0, 72), new SwitchSound(17, true, 3, null)]) && lamp.OnlySound);
            // A pair that also lights or moves something is not a sound-only switch.
            Assert(!SgbTimelines.Switch(Build(new Timeline("on", false, false, new Command(100, 5, "C056"), new Command(101, 6, "C013")), new Timeline("off", false, false)))!.OnlySound);
            // A stop before the start on the same side does not end it, and each side keeps its own sounds.
            var click = SgbTimelines.Switch(Build(
                new Timeline("on", false, false, new Command(100, 5, "C057"), new Command(101, 5, "C056", Frame: 10)),
                new Timeline("off", false, false, new Command(100, 6, "C056"), new Command(101, 6, "C057", Frame: 28))))!;
            Assert(click.Sounds.SequenceEqual([new SwitchSound(5, true, 10, null), new SwitchSound(6, false, 0, 28)]));
            Assert(SgbTimelines.Switch(Build(new Timeline("on", false, false), new Timeline("off", false, false)))!.Sounds.Length == 0);
            // The stop's fade (a float in milliseconds at node+0x14) is kept.
            var plasma = SgbTimelines.Switch(Build(
                new Timeline("on", false, false, new Command(100, 18, "C056"), new Command(101, 18, "C057", BitConverter.SingleToInt32Bits(1000f), Frame: 14)),
                new Timeline("off", false, false)))!;
            Assert(plasma.Sounds.Single() == new SwitchSound(18, true, 0, 14, 1000));
        });
        await check("A switch cue plays to its natural end or its stop and fade, whichever is first", () =>
        {
            // Tonberry Floor Lamp's 151 ms click is stopped at frame 57 with a 500 ms fade: it must end after 151 ms,
            // or a player that restarts finished sounds clicks again.
            Assert(new SwitchSound(12, true, 0, 57, 500).PlayMilliseconds(151) == 151);
            // Plasma Lamp's 1,124 ms buzz stops at frame 14 but fades for a second: it plays out.
            Assert(new SwitchSound(18, true, 0, 14, 1000).PlayMilliseconds(1124) == 1124);
            // A Carbuncle chime is stopped exactly at frame 72 with no fade.
            Assert(new SwitchSound(16, true, 0, 72).PlayMilliseconds(5000) == 2400);
            // A stop counts from the sound's own start; an unknown length leaves the stop, and neither leaves nothing.
            Assert(new SwitchSound(5, true, 30, 60).PlayMilliseconds(null) == 1000);
            Assert(new SwitchSound(5, true, 0, null).PlayMilliseconds(null) == null && new SwitchSound(5, true, 0, null).PlayMilliseconds(800) == 800);
        });
        await check("Looping animations' lap sounds keep their frame in the lap and fire once per lap on the clock", () =>
        {
            // A Drinking Apkallu: a 120-frame lap that plays a hit at frame 60 and a drop at 91, stopped at 100 with a fade.
            var apkallu = SgbTimelines.LoopSounds(Build(new Timeline("loop", true, true, new Command(100, 3, "C013", Word: 120),
                new Command(101, 10, "C056", Frame: 60), new Command(102, 11, "C056", Frame: 91), new Command(103, 11, "C057", BitConverter.SingleToInt32Bits(250f), Frame: 100))));
            Assert(apkallu.SequenceEqual([new LoopSound(120, new SwitchSound(10, true, 60, null)), new LoopSound(120, new SwitchSound(11, true, 91, 100, 250))]));
            // Without an animation there is no lap, and a timeline that does not autoplay is not running.
            Assert(SgbTimelines.LoopSounds(Build(new Timeline("loop", true, true, new Command(101, 10, "C056")))).Count == 0);
            Assert(SgbTimelines.LoopSounds(Build(new Timeline("loop", false, true, new Command(100, 3, "C013", Word: 120), new Command(101, 10, "C056")))).Count == 0);
            // Frame 60 of a 120-frame lap is at 2 s, 6 s, 10 s...
            var hit = apkallu[0];
            Assert(hit.Crossed(1900, 2100) && !hit.Crossed(2000, 2100) && !hit.Crossed(2100, 5900) && hit.Crossed(5900, 6000) && !hit.Crossed(100, 100));
        });
        await check("Switch motion plays each side's curve once from the change, holds its end and settles on load", () =>
        {
            // A lid: on turns it 90 degrees about Y over 30 frames; off snaps it back to the authored pose.
            LoopKey[] Keys(params (float Frame, float Value)[] k) => k.Select(x => new LoopKey(x.Frame, x.Value, 0)).ToArray();
            var channels = Enumerable.Range(0, 9).Select(i => i == 4 ? Keys((0, 0), (30, 90)) : Array.Empty<LoopKey>()).ToArray();
            var lid = new SwitchMotion(new SideMotion(0, null, 0, new LoopCurve(30, channels, 2)), new SideMotion(0, [0, 0, 0, 0, 0, 0, 1, 1, 1], 0, null))
                .WithRest(System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);
            var rest = System.Numerics.Matrix4x4.Identity;
            float Yaw(System.Numerics.Matrix4x4 m) => MathF.Round(MathF.Atan2(m.M13, m.M11) * 180 / MathF.PI);
            Assert(Yaw(lid.Pose(true, 0, rest)) == 0 && MathF.Abs(Yaw(lid.Pose(true, 500, rest))) is > 30 and < 60 && MathF.Abs(Yaw(lid.Pose(true, 5000, rest))) == 90);
            Assert(MathF.Abs(Yaw(lid.Pose(true, long.MaxValue, rest))) == 90 && Yaw(lid.Pose(false, 0, rest)) == 0 && Yaw(lid.Pose(false, long.MaxValue, rest)) == 0);
            // Off ends at the authored pose, so the lid is modelled closed: it starts on the off side.
            Assert(lid.RestingSide(rest) == false);
            // A curve that starts later holds the other side's final pose until then.
            var late = new SwitchMotion(new SideMotion(0, null, 30, new LoopCurve(30, channels, 2)), new SideMotion(0, [0, 0, 0, 0, MathF.PI / 4, 0, 1, 1, 1], 0, null))
                .WithRest(System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);
            Assert(MathF.Abs(Yaw(late.Pose(true, 500, rest))) == 45);
        });
        await check("Loop effects show in a window of each lap, a new lap each time, or stay on without a lap", () =>
        {
            // A Drinking Apkallu: its autoplay drip shows for frames 0-60 of a 120-frame lap and is stopped at 60.
            var drip = SgbTimelines.LoopEffects(Build(new Timeline("loop", true, true, new Command(100, 3, "C013", Word: 120), new Command(101, 9, "C059", Frame: 60)))).Single();
            Assert(drip == new LoopEffect(9, 120, null, 60));
            Assert(drip.ShowingLap(0, true) == 0 && drip.ShowingLap(1900, true) == 0 && drip.ShowingLap(2100, true) == null && drip.ShowingLap(4100, true) == 1);
            Assert(drip.ShowingLap(1000, false) == null);
            // A garden splash: started at 0, hidden at 139 of 140.
            var splash = SgbTimelines.LoopEffects(Build(new Timeline("loop", true, true, new Command(100, 3, "C013", Word: 140), new Command(101, 11, "C058"),
                new Command(102, 11, "C019", 0, Frame: 139)))).Single();
            Assert(splash == new LoopEffect(11, 140, 0, 139) && splash.ShowingLap(4650, false) == null && splash.ShowingLap(4700, false) == 1);
            // Without a lap length the effect starts once and stays.
            var still = SgbTimelines.LoopEffects(Build(new Timeline("loop", true, true, new Command(101, 7, "C058")))).Single();
            Assert(still == new LoopEffect(7, 0, 0, null) && still.ShowingLap(123_456, true) == 0);
            // A Storm Trophy (Left) has no curve; its 180-frame TMDH header is the lap, so its splash starts anew every 6 s.
            var storm = SgbTimelines.LoopEffects(Build(new Timeline("loop", true, true, new Command(101, 7, "C058")) { Duration = 180 })).Single();
            Assert(storm == new LoopEffect(7, 180, 0, null) && storm.ShowingLap(5900, true) == 0 && storm.ShowingLap(6100, true) == 1);
        });
        await check("Switch chains toggle controllers, play nested timelines and pass the switch on to nested groups", () =>
        {
            // An airship-like on side: toggle its own and member 12's controllers, play its own timeline 20 and member
            // 7's timeline 3, and pass "on" to every nested group. The off side passes nothing on and changes a glow.
            var bytes = Build(new Timeline("on", false, false, new Command(100, uint.MaxValue, "C138"), new Command(101, 12, "C138"),
                    new Command(102, uint.MaxValue, "C082", 20), new Command(103, 7, "C082", 3 | 7 << 8), new Command(104, uint.MaxValue, "C160", 1)) { Id = 5 },
                new Timeline("off", false, false, new Command(105, uint.MaxValue, "C160", 0), new Command(106, 3, "C110")) { Id = 6 });
            var cascades = SgbTimelines.Cascades(bytes);
            var on = cascades.Single(c => c.Id == 5); var off = cascades.Single(c => c.Id == 6);
            Assert(on.Toggles.SetEquals([uint.MaxValue, 12u]) && on.Plays.SequenceEqual([(20, uint.MaxValue), (3, 7u)]) && on.Broadcast && on.Unsupported.Count == 0);
            Assert(!off.Broadcast && off.Unsupported.Count == 0 && !off.AutoPlay);
            var toggle = SgbTimelines.Switch(bytes)!;
            Assert(toggle.OnTimeline == 5 && toggle.OffTimeline == 6 && toggle.Playable && toggle.Glows);
            // A command the chain cannot play is reported.
            Assert(SgbTimelines.Cascades(Build(new Timeline("on", false, false, new Command(100, 3, "C104")))).Single().Unsupported.SetEquals(["C104"]));
        });
        await check("Only one non-looping on/off pair is a switch; loops and ambiguous names are not", () =>
        {
            Assert(SgbTimelines.Switch(Build(new Timeline("on", false, false))) == null);
            Assert(SgbTimelines.Switch(Build(new Timeline("loop", true, true), new Timeline("timeline3", true, true))) == null);
            Assert(SgbTimelines.Switch(Build(new Timeline("on", false, true), new Timeline("off", false, false))) == null);
            Assert(SgbTimelines.Switch(Build(new Timeline("on", false, false), new Timeline("off", false, false), new Timeline("light", false, false))) == null);
            // A hide on one side wins over a show of the same part on that side.
            var both = SgbTimelines.Switch(Build(new Timeline("on", false, false, new Command(100, 5, "C019", 1), new Command(100, 5, "C019", 0)), new Timeline("off", false, false)))!;
            Assert(both.Members[5] == new StateActivity(false, null));
        });
        await check("Shared switch messages round-trip and reject empty, duplicate, truncated or out-of-range entries", () =>
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid();
            Assert(SwitchWire.Decode(SwitchWire.Encode([new(a, true), new(b, false)])).SequenceEqual([new SwitchState(a, true), new SwitchState(b, false)]));
            Reject(() => SwitchWire.Encode(Array.Empty<SwitchState>()));
            Reject(() => SwitchWire.Encode([new(Guid.Empty, true)]));
            var bytes = SwitchWire.Encode([new(a, true)]);
            Reject(() => SwitchWire.Decode(bytes[..^1]));
            var bad = (byte[])bytes.Clone(); bad[^1] = 2; Reject(() => SwitchWire.Decode(bad));
            var twice = SwitchWire.Encode([new(a, true), new(b, true)]); a.TryWriteBytes(twice.AsSpan(19, 16)); Reject(() => SwitchWire.Decode(twice));
            var huge = (byte[])bytes.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(huge, ushort.MaxValue); Reject(() => SwitchWire.Decode(huge));
        });
        await check("Switch activity keeps untouched sides authored and phrases the vanilla prompt", () =>
        {
            Assert(new StateActivity(null, false).Active(true, true) && !new StateActivity(null, false).Active(false, true));
            Assert(new StateActivity(true, null).Varies(false) && !new StateActivity(true, null).Varies(true));
            Assert(FurnishingInteraction.Action(false, true, true, true) == "Turn off" && FurnishingInteraction.Action(false, false, true, true) == "Turn on");
            Assert(FurnishingInteraction.Action(true, true, true, true) == "Take a serving" && FurnishingInteraction.Action(true, false, true, true) == "Replenish");
            Assert(FurnishingInteraction.Action(false, true, true, false) == "Move closer");
            Assert(FurnishingInteraction.Action(false, false, false, true, SwitchVerb.Play) == "Play" && FurnishingInteraction.Action(false, true, false, true, SwitchVerb.Play) == "Stop");
            Assert(FurnishingInteraction.Action(false, false, false, true, SwitchVerb.Open) == "Open" && FurnishingInteraction.Action(false, true, false, true, SwitchVerb.Open) == "Close");
            // A Mhachi Coffin's lids rest closed on its on side.
            Assert(FurnishingInteraction.Action(false, true, false, true, SwitchVerb.Open, closedOn: true) == "Open"
                && FurnishingInteraction.Action(false, false, false, true, SwitchVerb.Open, closedOn: true) == "Close");
            // Food keeps its serving prompts whatever its verb.
            Assert(FurnishingInteraction.Action(true, true, true, true, SwitchVerb.Open) == "Take a serving");
            Assert(FurnishingInteraction.Orchestrion(true, true) == "Choose music" && FurnishingInteraction.Orchestrion(false, true) == "See what's playing"
                && FurnishingInteraction.Orchestrion(true, false) == "Move closer");
        });
    }

    // SGB1/SCN1 whose timeline list holds the given timelines. Each command gets its own TMAC/TMTR/C-node, laid
    // out as all TMACs, then all TMTRs, then all commands, which is how installed housing timelines are ordered.
    private static byte[] Build(params Timeline[] timelines)
    {
        var b = new byte[0x2000];
        Int(b, 0, 0x31424753); Int(b, 12, 0x314E4353);
        const int list = 0x60, entries = 0x80;
        Int(b, 0x24, list - 0x14); Int(b, list, entries - list); Int(b, list + 4, timelines.Length);
        var cursor = entries + timelines.Length * 0x2C;
        for (var t = 0; t < timelines.Length; t++)
        {
            var timeline = timelines[t]; var record = entries + t * 0x2C;
            Int(b, record, timeline.Id); Int(b, record + 4, cursor - record); Encoding.ASCII.GetBytes(timeline.Type).CopyTo(b, cursor); cursor += 16;
            var keys = timeline.Commands.Select(c => (c.Key, c.Member)).Distinct().ToArray();
            Int(b, record + 8, cursor - record); Int(b, record + 0xC, keys.Length);
            foreach (var (key, member) in keys) { Int(b, cursor, key); Int(b, cursor + 4, (int)member); cursor += 8; }
            Int(b, record + 0x14, cursor - record); b[record + 0x20] = (byte)(timeline.AutoPlay ? 1 : 0); b[record + 0x21] = (byte)(timeline.Loop ? 1 : 0);
            var tmlb = cursor; var n = timeline.Commands.Length;
            Encoding.ASCII.GetBytes("TMLB").CopyTo(b, tmlb); Int(b, tmlb + 8, n * 3 + (timeline.Duration > 0 ? 1 : 0));
            var p = tmlb + 12;
            if (timeline.Duration > 0) { Node("TMDH", 16); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 12), timeline.Duration); p += 16; }
            foreach (var command in timeline.Commands) { Node("TMAC", 28); b[p + 10] = (byte)command.Key; Int(b, p + 24, 1); p += 28; }
            foreach (var _ in timeline.Commands) { Node("TMTR", 24); Int(b, p + 16, 1); p += 24; }
            foreach (var command in timeline.Commands) { Node(command.Tag, 24); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 10), command.Frame); Int(b, p + 12, command.Word); Int(b, p + 20, command.Value); p += 24; }
            Int(b, tmlb + 4, p - tmlb);
            cursor = p + 16;
            void Node(string tag, int size) { Encoding.ASCII.GetBytes(tag).CopyTo(b, p); Int(b, p + 4, size); }
        }
        return b;
    }
    private static void Int(byte[] b, int p, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p, 4), v);
    private static void Assert(bool value) { if (!value) throw new Exception("Furnishing switch assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("An invalid switch message was accepted.");
    }
}
