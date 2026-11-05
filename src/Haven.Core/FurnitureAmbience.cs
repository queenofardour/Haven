using System.Buffers.Binary;
using System.Numerics;

namespace Haven.Core;

/// <summary>Read which SGB members are driven by a scene timeline; never play the timelines themselves.</summary>
public static partial class SgbTimelines
{
    // FileSceneHeader.OffsetTimelines (scene + 0x10) -> FileSceneTimelineList; each 0x2C-byte
    // FileSceneTimeline lists 8-byte instances whose +4 field is the targeted member's instance ID.
    // FFCS LayoutEngine/FileFormat.cs. On/off switches and triggered loops address members here.
    public static IReadOnlySet<uint> Targets(ReadOnlySpan<byte> bytes)
    {
        var data = bytes.ToArray();
        int Int(int offset)
        {
            if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("Invalid SGB timeline offset.");
            return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        }
        int Count(int offset, int maximum)
        {
            var n = Int(offset); if (n < 0 || n > maximum) throw new InvalidDataException("Invalid SGB timeline count."); return n;
        }
        HashSet<uint> targets = [];
        if (data.Length < 0x54 || Int(0) != 0x31424753 || Int(12) != 0x314E4353) return targets;
        const int scene = 0x14;
        if (Int(scene + 0x10) <= 0) return targets;
        var list = checked(scene + Int(scene + 0x10));
        var count = Count(checked(list + 4), 128);
        if (count == 0) return targets;
        var entries = checked(list + Int(list));
        for (var n = 0; n < count; n++)
        {
            var record = checked(entries + n * 0x2C);
            _ = Int(checked(record + 0x28));
            var instances = Count(checked(record + 0xC), 256);
            if (instances == 0) continue;
            var first = checked(record + Int(checked(record + 8)));
            for (var i = 0; i < instances; i++) targets.Add(unchecked((uint)Int(checked(first + i * 8 + 4))));
        }
        return targets;
    }

    // Commands a chained timeline may carry that Haven plays: motion, the chain itself, and glow it leaves alone.
    private static readonly HashSet<string> CascadeTags = new(["C013", "C138", "C082", "C160", "C084", GlowTag], StringComparer.Ordinal);

    /// <summary>Every timeline's chain into nested groups (see TimelineCascade); malformed data yields none.</summary>
    public static IReadOnlyList<TimelineCascade> Cascades(ReadOnlySpan<byte> bytes)
    {
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return []; }
        var data = bytes.ToArray();
        return timelines.Select(t =>
        {
            HashSet<uint> toggles = []; List<(int, uint)> plays = []; var broadcast = false;
            foreach (var (member, tag, _, at) in t.Commands)
            {
                if (at + 0x18 > data.Length) continue;
                if (tag == "C138") toggles.Add(member);
                else if (tag == "C082") plays.Add((data[at + 0x14], data[at + 0x15] is var m and not 0 ? m : member));
                else if (tag == "C160") broadcast |= BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 0x14, 4)) == 1;
            }
            var unsupported = t.Commands.Select(c => c.Tag).Where(tag => !CascadeTags.Contains(tag)).ToHashSet();
            return new TimelineCascade(t.Id, t.Type, t.AutoPlay, t.Loop, toggles, plays, broadcast, unsupported);
        }).ToArray();
    }

    /// <summary>
    /// The appearance an item starts in: members its non-looping autoplay timeline (the default side of an on/off
    /// switch) hides with C019, and effects it starts with C058.
    /// </summary>
    public static DefaultTimelineState DefaultState(ReadOnlySpan<byte> bytes)
    {
        HashSet<uint> hidden = [], started = [], stopped = [];
        foreach (var timeline in Commands(bytes))
        {
            if (!timeline.AutoPlay || timeline.Loop) continue;
            foreach (var (member, tag, value, _) in timeline.Commands)
                if (tag == "C019" && value == 0) hidden.Add(member);
                else if (tag == "C058") started.Add(member);
                else if (tag == "C059") stopped.Add(member);
        }
        started.ExceptWith(stopped); started.ExceptWith(hidden);
        return new(hidden, started);
    }

    // Timeline commands Haven plays on a switch: visibility, effects, lights, sounds and motion, and the chained
    // controllers and timelines of nested groups (C138, C082, C160; see Cascades).
    private static readonly HashSet<string> PlayedTags = new(["C013", "C018", "C019", "C056", "C057", "C058", "C059", "C109", "C138", "C082", "C160"], StringComparer.Ordinal);
    // Glow (C110): fades a part's self-illumination, BgObject +0xCD, toward 255 (lit) or 0 (dark); see SwitchGlow.
    private const string GlowTag = "C110";
    private static readonly HashSet<string> OnNames = new(["on", "light", "open"], StringComparer.Ordinal);
    private static readonly HashSet<string> OffNames = new(["off", "of", "close"], StringComparer.Ordinal);

    /// <summary>
    /// A clickable on/off pair: exactly one non-looping on-side and one off-side timeline. For each member either
    /// side addresses, whether it is active there: shown (C019), started/stopped (C058/C059) or lit (C109).
    /// Members a side does not address keep their authored state on that side.
    /// </summary>
    public static SgbSwitch? Switch(ReadOnlySpan<byte> bytes)
    {
        var data = bytes.ToArray();
        var timelines = Commands(bytes).Where(t => !t.Loop).ToArray();
        var on = timelines.Where(t => OnNames.Contains(t.Type)).ToArray();
        var off = timelines.Where(t => OffNames.Contains(t.Type)).ToArray();
        if (on.Length != 1 || off.Length != 1) return null;
        Dictionary<uint, StateActivity> members = [];
        void Apply(TimelineCommands timeline, bool onSide)
        {
            foreach (var (member, tag, value, _) in timeline.Commands)
            {
                bool? active = tag switch { "C019" or "C109" => value != 0, "C058" => true, "C059" => false, _ => null };
                if (active is not {} state) continue;
                var current = members.GetValueOrDefault(member);
                // A hide or stop on the same side wins over a show or start.
                members[member] = onSide ? current with { On = current.On == false ? false : state } : current with { Off = current.Off == false ? false : state };
            }
        }
        Apply(on[0], true); Apply(off[0], false);
        // C056 starts a sound member at a frame; a later C057 on the same side stops it there, fading over the
        // milliseconds in its float at node+0x14. A side never stops the other side's sounds by itself: changing
        // sides cuts them, as every installed pair does with C057.
        int Fade(int at) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at + 20, 4)) is var ms && float.IsFinite(ms) ? (int)Math.Clamp(ms, 0, 10_000) : 0;
        IEnumerable<SwitchSound> Sounds(TimelineCommands timeline, bool onSide) => timeline.Commands.Where(c => c.Tag == "C056").Select(c =>
        {
            var stop = timeline.Commands.Where(s => s.Tag == "C057" && s.Member == c.Member && s.Value >= c.Value).OrderBy(s => s.Value).FirstOrDefault();
            return stop.Tag == null ? new SwitchSound(c.Member, onSide, c.Value, null) : new SwitchSound(c.Member, onSide, c.Value, stop.Value, Fade(stop.At));
        });
        var sounds = Sounds(on[0], true).Concat(Sounds(off[0], false)).Distinct().Take(16).ToArray();
        var motions = Motions(data, [on[0]], [off[0]]);
        Dictionary<uint, SwitchGlow> glows = [];
        void Glow(TimelineCommands timeline, bool onSide)
        {
            foreach (var (member, tag, _, at) in timeline.Commands)
            {
                if (tag != GlowTag || GlowAt(data, at) is not {} fade) continue;
                var current = glows.GetValueOrDefault(member);
                glows[member] = onSide ? current with { On = fade } : current with { Off = fade };
            }
        }
        Glow(on[0], true); Glow(off[0], false);
        var both = on[0].Commands.Concat(off[0].Commands).ToArray();
        var offId = off[0].Id;
        return new(on[0].AutoPlay ? true : off[0].AutoPlay ? false : null, members)
        {
            Sounds = sounds,
            Motions = motions,
            OnlySound = sounds.Length > 0 && both.All(c => c.Tag is "C056" or "C057"),
            Playable = both.All(c => PlayedTags.Contains(c.Tag) || c.Tag == GlowTag),
            Glows = both.Any(c => c.Tag == GlowTag),
            GlowMembers = glows,
            OnTimeline = on[0].Id,
            OffTimeline = offId,
        };
    }

    // C110: node+0xA the start frame, +0xC the fade in frames (1 = at once), +0x14 the target (non-zero lit).
    internal static GlowFade? GlowAt(byte[] data, int at) => at < 0 || at + 0x16 > data.Length ? null
        : new GlowFade(data[at + 0x14] != 0, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 0xA, 2)),
            Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 0xC, 4)), 1, 30 * 60));

    // Each side's motion per member: a C018 snap to a fixed pose and a C013 curve played once, each from its frame.
    private static Dictionary<uint, SwitchMotion> Motions(byte[] data, IReadOnlyList<TimelineCommands> on, IReadOnlyList<TimelineCommands> off)
    {
        SideMotion? Side(TimelineCommands timeline, uint member)
        {
            var curve = timeline.Commands.FirstOrDefault(c => c.Tag == "C013" && c.Member == member);
            var snap = timeline.Commands.FirstOrDefault(c => c.Tag == "C018" && c.Member == member);
            var played = curve.Tag == null ? null : Curve(data, curve.At, timeline.Curves, minimumFrames: 1);
            float[]? pose = null;
            if (snap.Tag != null && snap.At + 0x38 <= data.Length)
            {
                pose = Enumerable.Range(0, 9).Select(i => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(snap.At + 0x14 + i * 4, 4))).ToArray();
                if (!pose.All(v => float.IsFinite(v) && MathF.Abs(v) <= 100_000)) pose = null;
            }
            if (played == null && pose == null) return null;
            int Frame(int at) => at < 0 ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 10, 2));
            return new(pose == null ? 0 : Frame(snap.At), pose, played == null ? 0 : Frame(curve.At), played);
        }
        SideMotion? First(IReadOnlyList<TimelineCommands> side, uint member) => side.Select(t => Side(t, member)).FirstOrDefault(m => m != null);
        return on.Concat(off).SelectMany(t => t.Commands).Where(c => c.Tag is "C013" or "C018").Select(c => c.Member).Distinct().Take(32)
            .Select(m => (Member: m, Motion: new SwitchMotion(First(on, m), First(off, m))))
            .Where(m => m.Motion.On != null || m.Motion.Off != null).ToDictionary(m => m.Member, m => m.Motion);
    }

    /// <summary>
    /// One-shot motion of a nested group's timelines a switch chain plays on each side (a Factory Automatic Door's
    /// open and close), by member, as SgbSwitch.Motions gives it for the root's own pair.
    /// </summary>
    public static IReadOnlyDictionary<uint, SwitchMotion> ChainMotions(ReadOnlySpan<byte> bytes, IReadOnlySet<int> on, IReadOnlySet<int> off)
    {
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return new Dictionary<uint, SwitchMotion>(); }
        var onSide = timelines.Where(t => !t.Loop && on.Contains(t.Id)).ToArray();
        var offSide = timelines.Where(t => !t.Loop && off.Contains(t.Id)).ToArray();
        return onSide.Length == 0 && offSide.Length == 0 ? new Dictionary<uint, SwitchMotion>() : Motions(bytes.ToArray(), onSide, offSide);
    }

    /// <summary>
    /// A furnishing's one-shot timelines by SubId, each as the motion it gives its members (C018 snaps, C013 curves):
    /// what an arcade machine plays for a result (a hammer swing, a target's recoil, a claw's grab).
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyDictionary<uint, ActionMotion>> Actions(ReadOnlySpan<byte> bytes)
    {
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return new Dictionary<int, IReadOnlyDictionary<uint, ActionMotion>>(); }
        var data = bytes.ToArray();
        int Frame(int at) => at < 0 || at + 12 > data.Length ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 10, 2));
        var result = new Dictionary<int, IReadOnlyDictionary<uint, ActionMotion>>();
        foreach (var timeline in timelines.Where(t => !t.Loop))
        {
            // Every snap and curve of a member, in frame order: a recoil, then its return.
            var motions = new Dictionary<uint, ActionMotion>();
            foreach (var member in timeline.Commands.Where(c => c.Tag is "C013" or "C018").Select(c => c.Member).Distinct().Take(32))
            {
                var steps = new List<ActionStep>();
                foreach (var c in timeline.Commands.Where(c => c.Member == member && c.Tag is "C013" or "C018"))
                {
                    if (c.Tag == "C013" && Curve(data, c.At, timeline.Curves, minimumFrames: 1) is { } curve) steps.Add(new(Frame(c.At), curve, null));
                    else if (c.Tag == "C018" && c.At + 0x38 <= data.Length)
                    {
                        var pose = Enumerable.Range(0, 9).Select(i => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(c.At + 0x14 + i * 4, 4))).ToArray();
                        if (pose.All(v => float.IsFinite(v) && MathF.Abs(v) <= 100_000)) steps.Add(new(Frame(c.At), null, pose));
                    }
                }
                if (steps.Count > 0) motions[member] = new([.. steps.OrderBy(s => s.Frame)]);
            }
            if (motions.Count > 0) result.TryAdd(timeline.Id, motions);
        }
        return result;
    }

    /// <summary>
    /// The glow fades (C110) each one-shot timeline plays on its members, by SubId, in frame order: an arcade machine's
    /// lamps going dark on a miss or flashing on a hit.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyDictionary<uint, GlowFade[]>> ActionGlows(ReadOnlySpan<byte> bytes)
    {
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return new Dictionary<int, IReadOnlyDictionary<uint, GlowFade[]>>(); }
        var data = bytes.ToArray();
        var result = new Dictionary<int, IReadOnlyDictionary<uint, GlowFade[]>>();
        foreach (var timeline in timelines.Where(t => !t.Loop))
        {
            var members = timeline.Commands.Where(c => c.Tag == GlowTag).GroupBy(c => c.Member).Take(32)
                .Select(g => (g.Key, Fades: g.Select(c => GlowAt(data, c.At)).OfType<GlowFade>().OrderBy(f => f.StartFrame).ToArray()))
                .Where(m => m.Fades.Length > 0).ToDictionary(m => m.Key, m => m.Fades);
            if (members.Count > 0) result.TryAdd(timeline.Id, members);
        }
        return result;
    }

    /// <summary>
    /// The members each one-shot timeline shows and hides (C104, or C019; the value at node+0x14, non-zero shown), by
    /// SubId, in frame order: Monster Toss's ball, hidden at rest, in the air for a throw.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyDictionary<uint, (int Frame, bool Shown)[]>> ActionShows(ReadOnlySpan<byte> bytes)
    {
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return new Dictionary<int, IReadOnlyDictionary<uint, (int, bool)[]>>(); }
        var data = bytes.ToArray();
        int Frame(int at) => at < 0 || at + 12 > data.Length ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 10, 2));
        var result = new Dictionary<int, IReadOnlyDictionary<uint, (int, bool)[]>>();
        foreach (var timeline in timelines.Where(t => !t.Loop))
        {
            var members = timeline.Commands.Where(c => c.Tag is "C104" or "C019" && c.At + 0x15 <= data.Length).GroupBy(c => c.Member).Take(32)
                .ToDictionary(g => g.Key, g => g.Select(c => (Frame(c.At), data[c.At + 0x14] != 0)).OrderBy(s => s.Item1).ToArray());
            if (members.Count > 0) result.TryAdd(timeline.Id, members);
        }
        return result;
    }

    // What a one-shot use may carry for Haven to play it: motion, visibility, effects, sounds, glow, and nested
    // timelines started (C082) and stopped (C133).
    private static readonly HashSet<string> UseTags = new(["C013", "C018", "C019", "C056", "C057", "C058", "C059", "C082", "C133", GlowTag], StringComparer.Ordinal);

    /// <summary>
    /// A furnishing used rather than switched: no on/off pair, one non-autoplay one-shot "on" timeline that Haven can
    /// play whole (the Ale Tap's pour, the Projection Module's display). Its SubId, or null.
    /// </summary>
    public static int? OneShotUse(ReadOnlySpan<byte> bytes)
    {
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return null; }
        if (timelines.Any(t => t.Type is "off" or "of" or "close")) return null;
        var uses = timelines.Where(t => !t.AutoPlay && !t.Loop && t.Type == "on").ToArray();
        return uses.Length == 1 && uses[0].Commands.Count > 0 && uses[0].Commands.All(c => UseTags.Contains(c.Tag)) ? uses[0].Id : null;
    }

    /// <summary>
    /// The nested timelines each one-shot timeline starts (C082: SubId at node+0x14, member at +0x15, 0 itself) and the
    /// frame a later C133 naming the same pair stops them, by the one-shot's SubId: the Ale Tap's lamp blinks while it pours.
    /// </summary>
    public static IReadOnlyDictionary<int, (uint Member, int Timeline, int Start, int? Stop)[]> ActionPlays(ReadOnlySpan<byte> bytes)
    {
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return new Dictionary<int, (uint, int, int, int?)[]>(); }
        var data = bytes.ToArray();
        int Frame(int at) => at < 0 || at + 12 > data.Length ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 10, 2));
        (int Timeline, uint Member)? Pair((uint Member, string Tag, int Value, int At) c) =>
            c.At + 0x16 > data.Length ? null : (data[c.At + 0x14], data[c.At + 0x15] is var m and not 0 ? m : c.Member);
        var result = new Dictionary<int, (uint, int, int, int?)[]>();
        foreach (var timeline in timelines.Where(t => !t.Loop))
        {
            var plays = timeline.Commands.Where(c => c.Tag == "C082").Select(c =>
            {
                if (Pair(c) is not { } pair) return default((uint, int, int, int?)?);
                var start = Frame(c.At);
                var stop = timeline.Commands.Where(s => s.Tag == "C133" && Pair(s) == pair && Frame(s.At) > start).Select(s => Frame(s.At)).DefaultIfEmpty(-1).Min();
                return (pair.Member, pair.Timeline, start, stop < 0 ? null : stop);
            }).OfType<(uint, int, int, int?)>().Take(8).ToArray();
            if (plays.Length > 0) result.TryAdd(timeline.Id, plays);
        }
        return result;
    }

    /// <summary>
    /// The colour overrides (C113) each one-shot timeline sets on its members, by SubId, in frame order: node+0xA the
    /// start frame, +0xC the frames, +0x14..+0x20 the target RGBA as floats (the gathering machines flash red on a miss).
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyDictionary<uint, (int Start, int Frames, Vector4 Rgba)[]>> ActionColours(ReadOnlySpan<byte> bytes)
    {
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return new Dictionary<int, IReadOnlyDictionary<uint, (int, int, Vector4)[]>>(); }
        var data = bytes.ToArray();
        (int, int, Vector4)? Step(int at)
        {
            if (at < 0 || at + 0x24 > data.Length) return null;
            var rgba = new Vector4(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at + 0x14, 4)), BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at + 0x18, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at + 0x1C, 4)), BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at + 0x20, 4)));
            if (!float.IsFinite(rgba.LengthSquared()) || rgba.X is < 0 or > 255 || rgba.Y is < 0 or > 255 || rgba.Z is < 0 or > 255 || rgba.W is < 0 or > 1) return null;
            return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 0xA, 2)), Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 0xC, 4)), 1, 30 * 60), rgba);
        }
        var result = new Dictionary<int, IReadOnlyDictionary<uint, (int, int, Vector4)[]>>();
        foreach (var timeline in timelines.Where(t => !t.Loop))
        {
            var members = timeline.Commands.Where(c => c.Tag == "C113").GroupBy(c => c.Member).Take(32)
                .Select(g => (g.Key, Steps: g.Select(c => Step(c.At)).OfType<(int, int, Vector4)>().OrderBy(s => s.Item1).ToArray()))
                .Where(m => m.Steps.Length > 0).ToDictionary(m => m.Key, m => m.Steps);
            if (members.Count > 0) result.TryAdd(timeline.Id, members);
        }
        return result;
    }

    /// <summary>
    /// A part's colour override at a moment of a one-shot timeline: from none (0,0,0,0), each step fading linearly from
    /// where the last left it, and kept after (the client's C113 has no end).
    /// </summary>
    public static Vector4 ColourAt((int Start, int Frames, Vector4 Rgba)[] steps, long sinceMilliseconds)
    {
        var frame = sinceMilliseconds / 1000.0 * SwitchSound.FramesPerSecond;
        var colour = Vector4.Zero;
        foreach (var (start, frames, rgba) in steps)
        {
            if (start > frame) break;
            colour = Vector4.Lerp(colour, rgba, frames <= 1 ? 1 : (float)Math.Min(1, (frame - start) / frames));
        }
        return colour;
    }

    /// <summary>
    /// The bytes the client keeps for a colour override on a BgObject (+0xCE intensity, +0xD0..+0xD2 colour, +0xD3 blend),
    /// as its setter writes them: intensity lifts colours above 1, and the renderer squares (byte ÷ 255 × intensity).
    /// </summary>
    public static (byte Intensity, byte R, byte G, byte B, byte A) ColourBytes(Vector4 rgba)
    {
        var intensity = (byte)Math.Clamp((int)MathF.Ceiling(Math.Max(1, Math.Max(rgba.X, Math.Max(rgba.Y, rgba.Z)))), 1, 255);
        byte Channel(float c) => (byte)Math.Clamp((int)MathF.Round(c * 255 / intensity), 0, 255);
        return (intensity, Channel(rgba.X), Channel(rgba.Y), Channel(rgba.Z), (byte)Math.Clamp((int)MathF.Round(rgba.W * 255), 0, 255));
    }

    /// <summary>A part's glow while a one-shot timeline plays: from the authored full glow, through its fades in order.</summary>
    public static float GlowLevel(GlowFade[] fades, long sinceMilliseconds)
    {
        var frame = sinceMilliseconds / 1000.0 * SwitchSound.FramesPerSecond;
        var level = 1f;
        for (var i = 0; i < fades.Length && fades[i].StartFrame <= frame; i++)
        {
            var fade = fades[i];
            var until = i + 1 < fades.Length && fades[i + 1].StartFrame <= frame ? fades[i + 1].StartFrame : frame;
            var progress = fade.Frames <= 1 ? 1 : Math.Min(1, (until - fade.StartFrame) / fade.Frames);
            level += (fade.Target - level) * (float)progress;
        }
        return level;
    }

    /// <summary>Whether a part is shown at a moment of a one-shot timeline: its last show or hide so far, else as it rests.</summary>
    public static bool ShownAt((int Frame, bool Shown)[] changes, long sinceMilliseconds, bool atRest)
    {
        var frame = sinceMilliseconds / 1000.0 * SwitchSound.FramesPerSecond;
        var shown = atRest;
        foreach (var change in changes) { if (change.Frame > frame) break; shown = change.Shown; }
        return shown;
    }

    /// <summary>The effects each one-shot timeline starts (C058) and stops (a later C059 or hide), by SubId, as frames.</summary>
    public static IReadOnlyDictionary<int, (uint Member, int Start, int? Stop)[]> ActionEffects(ReadOnlySpan<byte> bytes)
    {
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return new Dictionary<int, (uint, int, int?)[]>(); }
        var data = bytes.ToArray();
        int Frame(int at) => at < 0 || at + 12 > data.Length ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 10, 2));
        var result = new Dictionary<int, (uint, int, int?)[]>();
        foreach (var timeline in timelines.Where(t => !t.Loop))
        {
            var effects = timeline.Commands.Where(c => c.Tag == "C058").Select(c =>
            {
                var start = Frame(c.At);
                var stop = timeline.Commands.Where(s => s.Member == c.Member && (s.Tag == "C059" || s.Tag == "C019" && s.Value == 0) && Frame(s.At) > start)
                    .Select(s => Frame(s.At)).DefaultIfEmpty(-1).Min();
                return (c.Member, start, stop < 0 ? (int?)null : stop);
            }).Take(8).ToArray();
            if (effects.Length > 0) result.TryAdd(timeline.Id, effects);
        }
        return result;
    }

    /// <summary>The sounds each one-shot timeline starts (C056, stopped by a later C057 with its fade), by SubId.</summary>
    public static IReadOnlyDictionary<int, SwitchSound[]> ActionSounds(ReadOnlySpan<byte> bytes)
    {
        List<TimelineCommands> timelines;
        try { timelines = Commands(bytes); }
        catch (InvalidDataException) { return new Dictionary<int, SwitchSound[]>(); }
        var data = bytes.ToArray();
        int Fade(int at) => at + 24 <= data.Length && BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at + 20, 4)) is var ms && float.IsFinite(ms) ? (int)Math.Clamp(ms, 0, 10_000) : 0;
        var result = new Dictionary<int, SwitchSound[]>();
        foreach (var timeline in timelines.Where(t => !t.Loop))
        {
            var sounds = timeline.Commands.Where(c => c.Tag == "C056").Select(c =>
            {
                var stop = timeline.Commands.Where(s => s.Tag == "C057" && s.Member == c.Member && s.Value >= c.Value).OrderBy(s => s.Value).FirstOrDefault();
                return stop.Tag == null ? new SwitchSound(c.Member, true, c.Value, null) : new SwitchSound(c.Member, true, c.Value, stop.Value, Fade(stop.At));
            }).Take(8).ToArray();
            if (sounds.Length > 0) result.TryAdd(timeline.Id, sounds);
        }
        return result;
    }

    /// <summary>
    /// The timeline an index plays (SharedGroupLayoutInstance.PlayTimeline reads it from the scene's settings block:
    /// scene + [scene+0x20] + 5, 16 bytes of SubIds). Arcade machines name their result timelines by index this way.
    /// </summary>
    public static byte[] ActionTable(ReadOnlySpan<byte> bytes)
    {
        const int scene = 0x14;
        if (bytes.Length < scene + 0x24) return [];
        var settings = BinaryPrimitives.ReadInt32LittleEndian(bytes[(scene + 0x20)..]);
        var start = scene + settings + 5;
        return settings <= 0 || start + 16 > bytes.Length ? [] : bytes.Slice(start, 16).ToArray();
    }

    private sealed record TimelineCommands(string Type, bool AutoPlay, bool Loop, List<(uint Member, string Tag, int Value, int At)> Commands)
    {
        // FileSceneTimeline.SubId (record +0x00): how C082 names a timeline to play.
        public int Id { get; init; }
        // TMFC curve sets by ID, as absolute offsets of their nodes.
        public Dictionary<int, int> Curves { get; } = [];
        // The timeline's length in frames, from its TMDH header (node+0xC); a loop repeats every Duration frames.
        public int Duration { get; set; }
    }

    // Each TMAC (actor = instance key, track count) owns consecutive TMTR tracks, each owning consecutive C-nodes.
    // A timeline whose counts do not line up yields no commands (fail closed). Decoded read-only against installed
    // assets: C019 visibility byte and C109 light state at node+0x14.
    private static List<TimelineCommands> Commands(ReadOnlySpan<byte> bytes)
    {
        var data = bytes.ToArray();
        int Int(int offset)
        {
            if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("Invalid SGB timeline offset.");
            return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        }
        int Count(int offset, int maximum)
        {
            var n = Int(offset); if (n < 0 || n > maximum) throw new InvalidDataException("Invalid SGB timeline count."); return n;
        }
        string Text(int offset)
        {
            if (offset < 0 || offset >= data.Length) return "";
            var end = Array.IndexOf(data, (byte)0, offset, Math.Min(64, data.Length - offset));
            return end < 0 ? "" : System.Text.Encoding.ASCII.GetString(data, offset, end - offset);
        }
        List<TimelineCommands> result = [];
        if (data.Length < 0x54 || Int(0) != 0x31424753 || Int(12) != 0x314E4353) return result;
        const int scene = 0x14;
        if (Int(scene + 0x10) <= 0) return result;
        var list = checked(scene + Int(scene + 0x10));
        var count = Count(checked(list + 4), 128);
        var entries = count == 0 ? 0 : checked(list + Int(list));
        for (var n = 0; n < count; n++)
        {
            var record = checked(entries + n * 0x2C);
            _ = Int(checked(record + 0x28));
            var type = Int(checked(record + 4)) > 0 ? Text(checked(record + Int(checked(record + 4)))) : "";
            var timeline = new TimelineCommands(type, data[record + 0x20] != 0, data[record + 0x21] != 0, []) { Id = Int(record) };
            result.Add(timeline);
            Dictionary<int, uint> members = [];
            var instances = Count(checked(record + 0xC), 256);
            var first = instances == 0 ? 0 : checked(record + Int(checked(record + 8)));
            for (var i = 0; i < instances; i++) members[Int(checked(first + i * 8))] = unchecked((uint)Int(checked(first + i * 8 + 4)));
            var tmlb = checked(record + Int(checked(record + 0x14)));
            if (tmlb < 0 || tmlb > data.Length - 12 || Int(tmlb) != 0x424C4D54) continue;
            var end = checked(tmlb + Count(checked(tmlb + 4), data.Length - tmlb));
            var nodes = Count(checked(tmlb + 8), 512);
            List<(int Actor, int Tracks)> actors = []; List<int> tracks = []; List<(string Tag, int At)> commands = [];
            for (int i = 0, p = tmlb + 12; i < nodes; i++)
            {
                var size = Int(checked(p + 4));
                if (size < 8 || size > end - p) throw new InvalidDataException("Invalid SGB timeline node.");
                var tag = System.Text.Encoding.ASCII.GetString(data, p, 4);
                if (tag == "TMDH" && size >= 14) timeline.Duration = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(checked(p + 12), 2));
                else if (tag == "TMAC") actors.Add((BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(checked(p + 10), 2)), Count(checked(p + 24), 64)));
                else if (tag == "TMTR") tracks.Add(Count(checked(p + 16), 64));
                else if (tag == "TMFC" && size >= 0x14) timeline.Curves[BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(checked(p + 8), 2))] = p;
                else if (tag[0] == 'C') commands.Add((tag, p));
                p += size;
            }
            if (actors.Sum(a => a.Tracks) != tracks.Count || tracks.Sum() != commands.Count) continue;
            for (int a = 0, track = 0, command = 0; a < actors.Count; a++)
                for (var t = 0; t < actors[a].Tracks; t++, track++)
                    for (var c = 0; c < tracks[track]; c++, command++)
                    {
                        if (!members.TryGetValue(actors[a].Actor, out var member)) continue;
                        var (tag, at) = commands[command];
                        // C056/C057 carry the frame (30 fps) they start or stop a sound at, at node+0xA.
                        var value = tag == "C019" ? Int(checked(at + 20)) & 0xFF : tag == "C109" ? Int(checked(at + 20))
                            : tag is "C056" or "C057" ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(checked(at + 10), 2)) : 0;
                        timeline.Commands.Add((member, tag, value, at));
                    }
        }
        return result;
    }
}

public sealed record DefaultTimelineState(IReadOnlySet<uint> Hidden, IReadOnlySet<uint> Started);

/// <summary>
/// What one timeline starts beyond its own parts, as switch scripts chain into nested groups: C138 toggles the
/// repeating controllers (spins and transforms) of the group itself (member uint.MaxValue) or of a nested member;
/// C082 plays a timeline of the group (+0x14 its ID, +0x15 the member, 0 for the group itself); C160 with state 1
/// (+0x14) passes "on" to every nested group. Unsupported lists the other commands. Decoded read-only from installed
/// housing prefabs: an airship miniature's on side passes "on" to its groups, each of which plays its propellers'
/// loop and toggles its gear; a Gordian Chair's on side toggles its two gears.
/// </summary>
public sealed record TimelineCascade(int Id, string Type, bool AutoPlay, bool Loop, IReadOnlySet<uint> Toggles, IReadOnlyList<(int Timeline, uint Member)> Plays,
    bool Broadcast, IReadOnlySet<string> Unsupported);
/// <summary>Whether a member is active on each side of a switch; null where that side leaves it untouched.</summary>
public readonly record struct StateActivity(bool? On, bool? Off)
{
    public bool Active(bool on, bool authored) => (on ? On : Off) ?? authored;
    public bool Varies(bool authored) => Active(true, authored) != Active(false, authored);
}
/// <summary>One side's C110: the glow it fades to (lit or dark), from which frame, over how many frames.</summary>
public readonly record struct GlowFade(bool Lit, int StartFrame, int Frames)
{
    public float Target => Lit ? 1 : 0;
    // The byte the client keeps at BgObject +0xCD: 255 is the authored glow, 0 none.
    public static byte ToByte(float level) => (byte)Math.Clamp((int)MathF.Floor(level * 255 + .5f), 0, 255);
}

/// <summary>
/// A part's glow on each side of a switch, as the client plays C110 (read-only from the installed executable: the
/// command's update at RVA 0x1921500 writes <c>BgObject</c> +0xCD through RVA 0x73F6F0 as round(level × 255)). The
/// level fades linearly from the other side's to this side's over the side's frames; a side that does not address
/// the part leaves it at the authored full glow. A lamp shade's emissive is drawn at this level.
/// </summary>
public readonly record struct SwitchGlow(GlowFade? On, GlowFade? Off)
{
    public float Level(bool on, long sinceChangeMilliseconds)
    {
        static float Target(GlowFade? fade) => fade is { Lit: false } ? 0 : 1;
        var side = on ? On : Off;
        float to = Target(side), from = Target(on ? Off : On);
        if (side is not {} f || f.Frames <= 1 || sinceChangeMilliseconds == long.MaxValue) return to;
        var frames = sinceChangeMilliseconds / 1000.0 * SwitchSound.FramesPerSecond - f.StartFrame;
        if (frames <= 0) return from;
        return from + (to - from) * (float)Math.Min(1, frames / f.Frames);
    }
    public byte Byte(bool on, long sinceChangeMilliseconds) => GlowFade.ToByte(Level(on, sinceChangeMilliseconds));
}

public sealed record SgbSwitch(bool? DefaultOn, IReadOnlyDictionary<uint, StateActivity> Members)
{
    public SwitchSound[] Sounds { get; init; } = [];
    // Both sides only start and stop sounds (the Manor instruments): sound alone is the whole interaction.
    public bool OnlySound { get; init; }
    public IReadOnlyDictionary<uint, SwitchMotion> Motions { get; init; } = new Dictionary<uint, SwitchMotion>();
    // Every command on both sides is one Haven plays, so a pair that only moves or sounds can still be offered.
    // Glows: a side also fades a part's glow (C110), per member in GlowMembers.
    public bool Playable { get; init; }
    public bool Glows { get; init; }
    public IReadOnlyDictionary<uint, SwitchGlow> GlowMembers { get; init; } = new Dictionary<uint, SwitchGlow>();
    // Each side's timeline ID, where its chain into nested groups starts.
    public int OnTimeline { get; init; }
    public int OffTimeline { get; init; }
}
/// <summary>
/// A sound member one side of a switch starts at a frame, and the frame that side stops it at, if any, fading out
/// over FadeMs.
/// </summary>
public readonly record struct SwitchSound(uint Member, bool On, int StartFrame, int? StopFrame, int FadeMs = 0)
{
    public const int FramesPerSecond = 30;
    // How long a cue plays: until its natural end, or its stop and fade if those come first. A sound player that
    // restarts a finished sound must be stopped at exactly this point. Null when neither is known.
    public long? PlayMilliseconds(long? natural)
    {
        long? stopped = StopFrame is int frame ? (frame - StartFrame) * 1000L / FramesPerSecond + FadeMs : null;
        return natural is long n && stopped is long s ? Math.Min(n, s) : natural ?? stopped;
    }
}

// Key stays stable for one furnishing member so a stage update keeps already-playing emitters.
// Volume applies to sounds only, as a multiple of the authored volume.
public readonly record struct AmbientEmitter(string Key, string Path, Matrix4x4 World, Vector4 Color, float Volume = 1);

/// <summary>Bounds a scene's passive furnishing effects and positional sounds.</summary>
public static class AmbientBudget
{
    public const int MaxEffects = 256;
    public const int MaxSounds = 24;
    // Copies of one loop started at different moments phase against each other; six adjacent Eternity
    // Cakes were reported as a harsh, glitchy soundscape. Nearby copies share one emitter, and each
    // distinct sound plays from a few places at most, so distant rooms can still each have their own.
    public const float SoundMergeDistance = 8f;
    public const int MaxCopiesPerSound = 3;

    public static AmbientEmitter[] Effects(IEnumerable<AmbientEmitter> candidates) => candidates.Take(MaxEffects).ToArray();

    // A silenced copy takes no place, so it never masks a louder copy nearby. A merged emitter plays at the
    // loudest volume among the copies it stands for.
    public static AmbientEmitter[] Sounds(IEnumerable<AmbientEmitter> candidates)
    {
        List<AmbientEmitter> kept = [];
        foreach (var sound in candidates)
        {
            if (sound.Volume <= 0) continue;
            var near = kept.FindIndex(k => k.Path == sound.Path && Vector3.Distance(k.World.Translation, sound.World.Translation) < SoundMergeDistance);
            if (near >= 0) { kept[near] = kept[near] with { Volume = Math.Max(kept[near].Volume, sound.Volume) }; continue; }
            if (kept.Count >= MaxSounds || kept.Count(k => k.Path == sound.Path) >= MaxCopiesPerSound) continue;
            kept.Add(sound);
        }
        return kept.ToArray();
    }
}
