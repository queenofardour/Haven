using System.Buffers.Binary;
using System.Text;

namespace Haven.Core.Animation;

/// <summary>
/// Human animation files (.pap), as every installed one is laid out: "pap ", version 0x20001, the animation count,
/// the info table at 0x1A (40 bytes an animation: a 32-byte name, type, Havok index, face flag), the Havok tagfile
/// straight after it, then one timeline (TMB: "TMLB", its size, its item count, then items each starting with a
/// four-letter tag and their size) per animation, 4-byte aligned from the first timeline's start (installed files
/// start it on a 4-byte boundary; a mod's file may not: Geek Time's starts at 0x1D1ADA), the last running to the end.
/// The game plays a .pap only when the animation names it declares (and its timeline's C009 entries repeat) are the
/// ones the emote expects, so an animation is renamed for the emote that carries it.
/// </summary>
public static class PapFiles
{
    private const int Info = 0x1A, Entry = 40, NameBytes = 32;
    private static readonly byte[] HavokTagfile = [0x1E, 0x0D, 0xB0, 0xCA, 0xCE, 0xFA, 0x11, 0xD0];

    private static InvalidDataException Bad(string why) => new($"An animation file is not valid ({why}).");

    /// <summary>Checks a file's structure before the game parses it; anything Haven doesn't recognise is refused.</summary>
    public static void Check(ReadOnlySpan<byte> pap) => Layout(pap);

    // The animation count and each timeline's start and size, after checking every offset.
    private static (int Count, List<(int Start, int Size)> Timelines) Layout(ReadOnlySpan<byte> pap)
    {
        if (pap.Length < 0x42 || !pap[..4].SequenceEqual("pap "u8)) throw Bad("not a .pap");
        if (BinaryPrimitives.ReadUInt32LittleEndian(pap[4..]) != 0x20001) throw Bad("unknown version");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(pap[8..]);
        var info = BinaryPrimitives.ReadInt32LittleEndian(pap[0x0E..]);
        var havok = BinaryPrimitives.ReadInt32LittleEndian(pap[0x12..]);
        var timeline = BinaryPrimitives.ReadInt32LittleEndian(pap[0x16..]);
        if (count is 0 or > 64 || info != Info || havok != Info + Entry * count) throw Bad("animation table");
        if (timeline < havok + 16 || (long)timeline + 12 > pap.Length) throw Bad("sections");
        for (var i = 0; i < count; i++)
        {
            Name(pap, i);
            // Each entry names one of the file's Havok animations by index (+34): one past them would be read from
            // whatever the game finds there. Every mod animation surveyed (185 files) names one of its own.
            if (BinaryPrimitives.ReadInt16LittleEndian(pap[(Info + Entry * i + 34)..]) is var index && (index < 0 || index >= count)) throw Bad("animation index");
        }
        if (!pap.Slice(havok, 8).SequenceEqual(HavokTagfile)) throw Bad("animation data");
        var timelines = new List<(int, int)>();
        var at = timeline;
        while (true)
        {
            if (at + 12 > pap.Length || !pap.Slice(at, 4).SequenceEqual("TMLB"u8)) throw Bad("timeline");
            var size = BinaryPrimitives.ReadInt32LittleEndian(pap[(at + 4)..]);
            var items = BinaryPrimitives.ReadInt32LittleEndian(pap[(at + 8)..]);
            if (size < 12 || (long)at + size > pap.Length || items is < 0 or > 4096) throw Bad("timeline size");
            // Every item lies inside the timeline, each a tag of four letters or digits and a size of at least 8.
            var item = at + 12;
            for (var i = 0; i < items; i++)
            {
                if (item + 8 > at + size) throw Bad("timeline item");
                var tag = pap.Slice(item, 4);
                var length = BinaryPrimitives.ReadInt32LittleEndian(pap[(item + 4)..]);
                if (!tag.ToArray().All(c => c is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z') || length < 8 || (long)item + length > (long)at + size) throw Bad("timeline item");
                if ((tag.SequenceEqual("C009"u8) || tag.SequenceEqual("C063"u8)) && (length < 24 || Text(pap, at, size, item, item + 20) == null)) throw Bad("timeline string");
                item += length;
            }
            timelines.Add((at, size));
            if (at + size == pap.Length) break;
            at = timeline + ((at - timeline + size + 3) & ~3);
        }
        if (timelines.Count != count) throw Bad("timeline count");
        return (count, timelines);
    }

    private static string Name(ReadOnlySpan<byte> pap, int index)
    {
        var name = pap.Slice(Info + Entry * index, NameBytes);
        var end = name.IndexOf((byte)0);
        if (end <= 0 || name[..end].ToArray().Any(c => c is < 0x20 or > 0x7E)) throw Bad("animation name");
        return Encoding.ASCII.GetString(name[..end]);
    }

    // A timeline string: the offset at field, counted from the item's start plus 8, to a printable string ending
    // inside the timeline. Null if it isn't one.
    private static string? Text(ReadOnlySpan<byte> pap, int timeline, int size, int item, int field)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(pap[field..]);
        var start = (long)item + 8 + offset;
        if (offset <= 0 || start >= timeline + size) return null;
        var text = pap[(int)start..(timeline + size)];
        var end = text.IndexOf((byte)0);
        if (end < 0 || end > 255 || text[..end].ToArray().Any(c => c is < 0x20 or > 0x7E)) return null;
        return Encoding.ASCII.GetString(text[..end]);
    }

    /// <summary>
    /// The body (skeleton) a file's animations were made for, from its header (a model ID: 801 is "c0801"). The game
    /// reads a file at another body's path as if it were made for that body, so a file is served only where it fits.
    /// </summary>
    public static string Skeleton(ReadOnlySpan<byte> pap)
    {
        Layout(pap);
        return $"c{BinaryPrimitives.ReadUInt16LittleEndian(pap[0x0A..]):0000}";
    }

    /// <summary>The animation names a file declares, in order.</summary>
    public static IReadOnlyList<string> Names(ReadOnlySpan<byte> pap)
    {
        var (count, _) = Layout(pap);
        var names = new string[count];
        for (var i = 0; i < count; i++) names[i] = Name(pap, i);
        return names;
    }

    /// <summary>
    /// The file with its animations renamed to the names an emote expects, in order (the first animation takes the
    /// first name, and so on): the info table's names, and every C009 entry of each animation's timeline naming it,
    /// which is pointed at the new name added to the end of that timeline. Animations beyond the names keep theirs,
    /// and names beyond the animations are left unanswered. The result is checked again before it is returned.
    /// </summary>
    public static byte[] Rename(ReadOnlySpan<byte> pap, IReadOnlyList<string> names)
    {
        var (count, _) = Layout(pap);
        if (names.Any(n => n.Length is 0 or >= NameBytes || n.Any(c => c is < ' ' or > '~'))) throw new ArgumentException("An animation name is not valid.");
        var old = new string[count];
        for (var i = 0; i < count; i++) old[i] = Name(pap, i);
        var result = Rewrite(pap, names, (t, tag, value) => tag == "C009" && t < names.Count && t < count && value == old[t] ? names[t] : null);
        var check = Names(result);
        for (var i = 0; i < Math.Min(count, names.Count); i++)
            if (check[i] != names[i]) throw Bad("rename");
        return result;
    }

    /// <summary>The sound files the file's timelines play (C063 entries), in order, each once.</summary>
    public static IReadOnlyList<string> Sounds(ReadOnlySpan<byte> pap)
    {
        List<string> sounds = [];
        Rewrite(pap, null, (_, tag, value) => { if (tag == "C063" && !sounds.Contains(value)) sounds.Add(value); return null; });
        return sounds;
    }

    /// <summary>Every string the file's timelines name (effects, sounds and whatever else their items point at), each once.</summary>
    public static IReadOnlyList<string> TimelineStrings(ReadOnlySpan<byte> pap)
    {
        List<string> strings = [];
        Rewrite(pap, null, (_, _, value) => { if (!strings.Contains(value)) strings.Add(value); return null; });
        return strings;
    }

    /// <summary>The file with its timelines' sounds (C063 entries) pointed elsewhere: map gives a new path, or null to keep one.</summary>
    public static byte[] RepointSounds(ReadOnlySpan<byte> pap, Func<string, string?> map) =>
        Rewrite(pap, null, (_, tag, value) => tag == "C063" ? map(value) : null);

    /// <summary>
    /// The effect files the file's timelines start, in order, each once: C012 entries (an effect at a moment; the Cheer
    /// emotes start the glowsticks and their sparkles so) and C173 entries (an effect held while the animation plays:
    /// Warrior of Lift's bench and barbell), both with their path at the same field as a sound's.
    /// </summary>
    public static IReadOnlyList<string> Effects(ReadOnlySpan<byte> pap)
    {
        List<string> effects = [];
        Rewrite(pap, null, (_, tag, value) => { if (tag is "C012" or "C173" && value.EndsWith(".avfx", StringComparison.Ordinal) && !effects.Contains(value)) effects.Add(value); return null; });
        return effects;
    }

    /// <summary>The file with its timelines' effects (C012 and C173 entries) pointed elsewhere: map gives a new path, or null to keep one.</summary>
    public static byte[] RepointEffects(ReadOnlySpan<byte> pap, Func<string, string?> map) =>
        Rewrite(pap, null, (_, tag, value) => tag is "C012" or "C173" ? map(value) : null);

    /// <summary>
    /// The file with only one of its animations, by name: its info entry (which names its place in the Havok data, kept
    /// whole) and its timeline. A file of several (Warrior of Lift's treadmill holds walking back, walking, running and
    /// sprinting, and the emote's own timeline picks one) becomes the one a ball plays.
    /// </summary>
    public static byte[] Only(ReadOnlySpan<byte> pap, string name)
    {
        var (count, timelines) = Layout(pap);
        var index = -1;
        for (var i = 0; i < count && index < 0; i++) if (Name(pap, i) == name) index = i;
        if (index < 0) throw Bad("no such animation");
        if (count == 1) return pap.ToArray();
        var havok = pap[(Info + Entry * count)..timelines[0].Start];
        var (start, size) = timelines[index];
        var output = new byte[Info + Entry + havok.Length + size];
        pap[..Info].CopyTo(output);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x0E), Info);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x12), Info + Entry);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x16), Info + Entry + havok.Length);
        pap.Slice(Info + Entry * index, Entry).CopyTo(output.AsSpan(Info));
        havok.CopyTo(output.AsSpan(Info + Entry));
        pap.Slice(start, size).CopyTo(output.AsSpan(Info + Entry + havok.Length));
        Layout(output);
        return output;
    }

    /// <summary>
    /// The animations an emote's own timeline file (chara/action/….tmb) plays, by name: its C010 entries (the name at
    /// +32), in order. Empty when the file isn't a timeline.
    /// </summary>
    public static IReadOnlyList<string> TimelineAnimations(ReadOnlySpan<byte> tmb)
    {
        List<string> names = [];
        if (tmb.Length < 12 || !tmb[..4].SequenceEqual("TMLB"u8)) return names;
        var size = BinaryPrimitives.ReadInt32LittleEndian(tmb[4..]);
        var items = BinaryPrimitives.ReadInt32LittleEndian(tmb[8..]);
        if (size < 12 || size > tmb.Length || items is < 0 or > 4096) return names;
        var item = 12;
        for (var i = 0; i < items && item + 8 <= size; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(tmb[(item + 4)..]);
            if (length < 8 || (long)item + length > size) break;
            if (tmb.Slice(item, 4).SequenceEqual("C010"u8) && length >= 40 && Text(tmb, 0, size, item, item + 32) is { Length: > 0 } name && !names.Contains(name)) names.Add(name);
            item += length;
        }
        return names;
    }

    // Rewrites the info table's names (when given) and any C009, C012, C063 or C173 string replace answers for, each new string
    // added once to the end of its timeline, and lays the timelines out again (4-byte aligned between). Checked again.
    private static byte[] Rewrite(ReadOnlySpan<byte> pap, IReadOnlyList<string>? names, Func<int, string, string, string?> replace)
    {
        var (count, timelines) = Layout(pap);
        var output = new MemoryStream(pap.Length + 64 * count);
        var head = pap[..timelines[0].Start].ToArray();
        for (var i = 0; names != null && i < Math.Min(count, names.Count); i++)
        {
            var slot = head.AsSpan(Info + Entry * i, NameBytes);
            slot.Clear();
            Encoding.ASCII.GetBytes(names[i], slot);
        }
        output.Write(head);
        for (var t = 0; t < timelines.Count; t++)
        {
            var (start, size) = timelines[t];
            var index = t;
            output.Write(Retime(pap.Slice(start, size).ToArray(), (tag, value) => replace(index, tag, value)));
            if (t < timelines.Count - 1) while ((output.Length - timelines[0].Start) % 4 != 0) output.WriteByte(0);
        }
        var result = output.ToArray();
        Layout(result);
        return result;
    }

    // The field holding an item's string: +20 for a name (C009), an effect (C012, C173) or a sound (C063); +32 for the
    // animation a C010 plays. Zero for items without one.
    private static int StringField(string tag, int length) => tag switch
    {
        "C009" or "C012" or "C063" or "C173" when length >= 24 => 20,
        "C010" when length >= 40 => 32,
        _ => 0,
    };

    // One timeline (TMLB to its end) with the strings replace answers for pointed at new strings added to its end, once each.
    private static byte[] Retime(byte[] tmb, Func<string, string, string?> replace)
    {
        var added = new MemoryStream();
        var placed = new Dictionary<string, int>(StringComparer.Ordinal);
        var repoint = new List<(int Field, int Target)>();
        var items = BinaryPrimitives.ReadInt32LittleEndian(tmb.AsSpan(8));
        var item = 12;
        for (var i = 0; i < items; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(tmb.AsSpan(item + 4));
            var tag = Encoding.ASCII.GetString(tmb, item, 4);
            if (StringField(tag, length) is > 0 and var field && Text(tmb, 0, tmb.Length, item, item + field) is { } value && replace(tag, value) is { } next && next != value)
            {
                if (next.Length is 0 or > 255 || next.Any(c => c is < ' ' or > '~')) throw new ArgumentException("A timeline string is not valid.");
                if (!placed.TryGetValue(next, out var at)) { placed[next] = at = tmb.Length + (int)added.Length; added.Write(Encoding.ASCII.GetBytes(next + "\0")); }
                repoint.Add((item + field, at - (item + 8)));
            }
            item += length;
        }
        if (repoint.Count == 0) return tmb;
        var grown = new byte[tmb.Length + added.Length];
        tmb.CopyTo(grown, 0); added.ToArray().CopyTo(grown, tmb.Length);
        foreach (var (field, target) in repoint) BinaryPrimitives.WriteInt32LittleEndian(grown.AsSpan(field), target);
        BinaryPrimitives.WriteInt32LittleEndian(grown.AsSpan(4), grown.Length);
        return grown;
    }

    /// <summary>
    /// Checks an emote's own timeline file (chara/action/….tmb): "TMLB", its size the file's, and every item a tag of
    /// four letters or digits with a size inside it. Haven serves such a file only after this.
    /// </summary>
    public static void CheckTimeline(ReadOnlySpan<byte> tmb)
    {
        if (tmb.Length < 12 || tmb.Length > 64 * 1024 || !tmb[..4].SequenceEqual("TMLB"u8) || BinaryPrimitives.ReadInt32LittleEndian(tmb[4..]) != tmb.Length) throw Bad("emote timeline");
        var items = BinaryPrimitives.ReadInt32LittleEndian(tmb[8..]);
        if (items is < 0 or > 4096) throw Bad("emote timeline");
        var item = 12;
        for (var i = 0; i < items; i++)
        {
            if (item + 8 > tmb.Length) throw Bad("emote timeline item");
            var length = BinaryPrimitives.ReadInt32LittleEndian(tmb[(item + 4)..]);
            if (!tmb.Slice(item, 4).ToArray().All(c => c is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z') || length < 8 || (long)item + length > tmb.Length) throw Bad("emote timeline item");
            item += length;
        }
    }

    /// <summary>The strings an emote's own timeline file names, with their item's tag: effects, sounds and the animations it plays (checked first).</summary>
    public static IReadOnlyList<(string Tag, string Value)> TimelineEntries(ReadOnlySpan<byte> tmb)
    {
        CheckTimeline(tmb);
        List<(string, string)> entries = [];
        Retime(tmb.ToArray(), (tag, value) => { entries.Add((tag, value)); return null; });
        return entries;
    }

    /// <summary>An emote's own timeline file with strings pointed elsewhere: replace(tag, value) gives a new string, or null to keep one. Checked again.</summary>
    public static byte[] RewriteTimeline(ReadOnlySpan<byte> tmb, Func<string, string, string?> replace)
    {
        CheckTimeline(tmb);
        var result = Retime(tmb.ToArray(), replace);
        CheckTimeline(result);
        return result;
    }
}
