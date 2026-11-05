using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Haven.Core;

/// <summary>
/// Pose balls: an animation set a furnishing offers, such as a two-person cuddle on a bed. Each
/// role is a ball placed relative to the furnishing; a player who takes a ball is held at it and, once every ball of
/// the set is taken, plays the role's animation. Roles play a game emote, or an animation from the set's package (an
/// imported Penumbra animation mod, Haven.Core.Animation), carried by one of the game's little-used loop emotes.
/// </summary>
public sealed record PoseSet
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Animation";
    /// <summary>The animation package (content locker fingerprint) its custom roles play from; null when every role plays a game emote.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Package { get; init; }
    public PoseRole[] Roles { get; init; } = [];
}

public sealed record PoseRole
{
    public string Name { get; init; } = "";
    /// <summary>A game emote this ball plays (a looping or seated one), or 0 for an animation of the set's package.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Emote { get; init; }
    /// <summary>The package's animation (its index in the package's manifest), when Emote is 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Animation { get; init; }
    /// <summary>
    /// The carrier emote (PoseCarriers.All, counted from 1) that plays the animation in this home. The owner's Haven
    /// picks it; every PC serves the animation at that carrier's files, so the same animation plays for everyone.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Carrier { get; init; }
    /// <summary>The ball's place in the furnishing's own space (turned and scaled with it).</summary>
    public float[] Position { get; init; } = [0, 0, 0];
    /// <summary>The way a player on this ball faces, in degrees, relative to the furnishing.</summary>
    public float Rotation { get; init; }
    /// <summary>A forward or backward tilt (about the player's own right axis) and a sideways lean (about their own forward axis), in degrees.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Pitch { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Roll { get; init; }
    /// <summary>The ball's colour, 0xRRGGBB.</summary>
    public uint Color { get; init; } = 0xF29CC6;
    /// <summary>
    /// The ball plays its animation without the animation's own sounds: an earlier ball of the set plays the same
    /// animation, so its music plays once however many dance (it has a carrier of its own, served a silent copy).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Silent { get; init; }
    [JsonIgnore] public Vector3 Offset => new(Position[0], Position[1], Position[2]);
    [JsonIgnore] public bool Custom => Emote == 0;
}

/// <summary>
/// The game emotes that carry custom animations: looping emotes few players use, whose loop and intro animations
/// (.pap, per race) Haven serves through Penumbra with an imported animation instead. Playing the emote then plays
/// the animation, for the player and, through Haven's own emote sync, for everyone in the gathering.
/// </summary>
public readonly record struct PoseCarrier(ushort Emote, string Loop, string Intro);

public static class PoseCarriers
{
    // Survey of the installed Emote sheet (27 October 2026): every one loops (EmoteMode condition 3), draws no weapon,
    // and has its own loop and intro timeline. Cheers, Ranger poses, Simulation, the Fitness emotes and Show.
    public static readonly PoseCarrier[] All =
    [
        new(65, "emote_sp/sp10_loop", "emote_sp/sp10_start"), new(66, "emote_sp/sp11_loop", "emote_sp/sp11_start"),
        new(67, "emote_sp/sp12_loop", "emote_sp/sp12_start"), new(264, "emote_sp/sp47_loop", "emote_sp/sp47_start"),
        new(265, "emote_sp/sp48_loop", "emote_sp/sp48_start"), new(266, "emote_sp/sp49_loop", "emote_sp/sp49_start"),
        new(314, "emote_sp/sp78_loop", "emote_sp/sp78_start"), new(315, "emote_sp/sp79_loop", "emote_sp/sp79_start"),
        new(316, "emote_sp/sp80_loop", "emote_sp/sp80_start"), new(310, "emote_sp/sp73_loop", "emote_sp/sp73_start"),
        new(311, "emote_sp/sp74_loop", "emote_sp/sp74_start"), new(321, "emote_sp/sp86_loop", "emote_sp/sp86_start"),
        new(322, "emote_sp/sp87_loop", "emote_sp/sp87_start"), new(323, "emote_sp/sp88_loop", "emote_sp/sp88_start"),
        new(324, "emote_sp/sp89_loop", "emote_sp/sp89_start"),
        new(130, "emote/loop_emot01_loop", "emote/loop_emot01_start"), new(131, "emote/loop_emot03_loop", "emote/loop_emot03_start"),
        new(132, "emote/loop_emot02_loop", "emote/loop_emot02_start"), new(134, "emote/loop_emot04_loop", "emote/loop_emot04_start"),
        new(135, "emote/loop_emot06_loop", "emote/loop_emot06_start"), new(136, "emote/loop_emot05_loop", "emote/loop_emot05_start"),
        new(200, "emote_sp/sp17_loop", "emote_sp/sp17_start"), new(201, "emote_sp/sp18_loop", "emote_sp/sp18_start"),
        new(155, "emote/loop_emot10_loop", "emote/loop_emot10_start"), new(156, "emote/loop_emot08_loop", "emote/loop_emot08_start"),
        new(157, "emote/loop_emot09_loop", "emote/loop_emot09_start"), new(158, "emote/loop_emot11_loop", "emote/loop_emot11_start"),
        new(164, "emote/loop_emot12_loop", "emote/loop_emot12_start"), new(165, "emote/loop_emot13_loop", "emote/loop_emot13_start"),
        new(241, "emote_sp/sp40_loop", "emote_sp/sp40_start"), new(242, "emote_sp/sp41_loop", "emote_sp/sp41_start"),
        // 0.39.3: carriers that read more bodies' own copies (the game's papLoadTable), for animations a mod ships per
        // body. Every body reads its own copy of these first five; the rest share fewer bodies' copies than the 31
        // above (whose bodies mostly read the Hyur man's). The best-fitting free one is chosen per animation
        // (AssignCarriers); emotes players often use come last.
        new(114, "emote_sp/sp01_loop", "emote_sp/sp01_start"), new(306, "emote_sp/sp77_loop", "emote_sp/sp77_start"),
        new(301, "emote_sp/sp72_loop", "emote_sp/sp72_start"), new(302, "emote_sp/sp72_uldaha_loop", "emote_sp/sp72_uldaha_start"),
        new(303, "emote_sp/sp72_limsa_loop", "emote_sp/sp72_limsa_start"),
        new(188, "emote/loop_emot20_loop", "emote/loop_emot20_start"), new(189, "emote/loop_emot21_loop", "emote/loop_emot21_start"),
        new(196, "emote/loop_emot22_loop", "emote/loop_emot22_start"), new(248, "emote/loop_emot27_loop", "emote/loop_emot27_start"),
        new(273, "emote/loop_emot28_loop", "emote/loop_emot28_start"), new(329, "emote_sp/sp68_loop", "emote_sp/sp68_start"),
        new(167, "emote_sp/sp07_loop", "emote_sp/sp07_start"), new(279, "emote/loop_emot29_loop", "emote/loop_emot29_start"),
        new(280, "emote_sp/sp60_loop", "emote_sp/sp60_start"), new(281, "emote_sp/sp61_loop", "emote_sp/sp61_start"),
        new(283, "emote_sp/sp62_loop", "emote_sp/sp62_start"), new(294, "emote/loop_emot30_loop", "emote/loop_emot30_start"),
        new(317, "emote_sp/sp67_loop", "emote_sp/sp67_start"), new(332, "emote/loop_emot32_loop", "emote/loop_emot32_start"),
        new(328, "emote/loop_emot31_loop", "emote/loop_emot31_start"), new(331, "emote_sp/sp91_loop", "emote_sp/sp91_start"),
        new(307, "emote_sp/sp65_loop", "emote_sp/sp65_start"), new(295, "emote_sp/sp70_loop", "emote_sp/sp70_start"),
        new(274, "emote_sp/sp55_loop", "emote_sp/sp55_start"), new(272, "emote_sp/sp46_loop", "emote_sp/sp46_start"),
        new(296, "emote_sp/sp71_loop", "emote_sp/sp71_start"), new(149, "emote_sp/sp05_loop", "emote_sp/sp05_start"),
        new(170, "emote/loop_emot16_loop", "emote/loop_emot16_start"), new(181, "emote/loop_emot18_loop", "emote/loop_emot18_start"),
        new(223, "emote_sp/sp32_loop", "emote_sp/sp32_start"), new(169, "emote/loop_emot15_loop", "emote/loop_emot15_start"),
        new(103, "emote/dance_elezen_loop", "emote/dance_elezen_start"), new(120, "emote/dance02_loop", "emote/dance02_start"),
        new(126, "emote/dance05_loop", "emote/dance05_start"), new(203, "emote/loop_emot23_loop", "emote/loop_emot23_start"),
    ];

    /// <summary>The playable human races' skeleton codes (Hyur Midlander male c0101 … Viera female c1801).</summary>
    public static readonly string[] Races =
        ["c0101", "c0201", "c0301", "c0401", "c0501", "c0601", "c0701", "c0801", "c0901", "c1001", "c1101", "c1201", "c1301", "c1401", "c1501", "c1601", "c1701", "c1801"];

    public static PoseCarrier? Get(int carrier) => carrier >= 1 && carrier <= All.Length ? All[carrier - 1] : null;
    public static bool IsCarrierEmote(uint emote) => All.Any(c => c.Emote == emote);
    public static string PapPath(string race, string key) => $"chara/human/{race}/animation/a0001/bt_common/{key}.pap";

    /// <summary>
    /// A race's own animation, or the nearest one an animation offers: the race's base form for its body (Hyur
    /// Midlander male or female), then the other, then any. Races are in pairs, male then female.
    /// </summary>
    public static string? Nearest(string race, IReadOnlyCollection<string> offered)
    {
        if (offered.Contains(race)) return race;
        var female = Array.IndexOf(Races, race) % 2 == 1;
        foreach (var fallback in female ? new[] { "c0201", "c0101" } : ["c0101", "c0201"])
            if (offered.Contains(fallback)) return fallback;
        // Then the nearest race of the same body, then anything.
        return Races.Where(offered.Contains).OrderBy(r => Array.IndexOf(Races, r) % 2 == (female ? 1 : 0) ? 0 : 1).FirstOrDefault();
    }
}

public static class PoseSets
{
    public const int MaxSetsPerFurnishing = 8, MaxRoles = 4, MaxSetsPerHome = 128, MaxAnimations = 32;
    public const float MaxOffset = 50;
    public static readonly uint[] DefaultColors = [0xF29CC6, 0x8CC8F2, 0xB4E68C, 0xF2D27A];

    /// <summary>Checks a home's pose sets: shapes, limits, and that each animation plays on one carrier of its own.</summary>
    public static void Validate(House house)
    {
        var carriers = new Dictionary<(string, int, bool), int>();
        var used = new Dictionary<int, (string, int, bool)>();
        var total = 0;
        foreach (var f in house.Furnishings)
        {
            if (f.Poses == null) continue;
            if (f.BuildingPart != null || f.Poses.Length is 0 or > MaxSetsPerFurnishing || (total += f.Poses.Length) > MaxSetsPerHome)
                throw new InvalidDataException("Invalid pose sets.");
            var ids = new HashSet<Guid>();
            foreach (var set in f.Poses)
            {
                if (set == null || set.Id == Guid.Empty || !ids.Add(set.Id) || !Name(set.Name, 40, false) || set.Roles is not { Length: >= 1 and <= MaxRoles }
                    || set.Package != null && !ContentLocker.IsFingerprint(set.Package))
                    throw new InvalidDataException("Invalid pose set.");
                foreach (var role in set.Roles)
                {
                    if (role == null || !Name(role.Name, 30, true) || role.Position is not { Length: 3 } || !role.Position.All(x => float.IsFinite(x) && Math.Abs(x) <= MaxOffset)
                        || !SafeAngles.Sane(role.Rotation) || !Angle(role.Pitch) || !Angle(role.Roll) || role.Color > 0xFFFFFF)
                        throw new InvalidDataException("Invalid pose ball.");
                    if (!role.Custom)
                    {
                        if (role.Emote > ushort.MaxValue || role.Animation != 0 || role.Carrier != 0 || role.Silent) throw new InvalidDataException("Invalid pose ball emote.");
                        continue;
                    }
                    if (set.Package == null || role.Animation is < 0 or >= MaxAnimations || PoseCarriers.Get(role.Carrier) == null)
                        throw new InvalidDataException("Invalid pose ball animation.");
                    var key = (set.Package, role.Animation, role.Silent);
                    if (carriers.TryGetValue(key, out var carrier) ? carrier != role.Carrier : used.ContainsKey(role.Carrier))
                        throw new InvalidDataException("Two animations share a carrier emote.");
                    carriers[key] = role.Carrier; used[role.Carrier] = key;
                }
            }
        }
    }

    private static bool Angle(float degrees) => float.IsFinite(degrees) && Math.Abs(degrees) <= 360;
    private static bool Name(string? name, int length, bool empty) =>
        name != null && name.Length <= length && (empty || !string.IsNullOrWhiteSpace(name)) && !name.Any(char.IsControl);

    /// <summary>Every (package, animation) the home plays, with its carrier.</summary>
    /// <summary>A set whose carriers predate silent balls: two balls play one animation on one carrier, so its music doubles.</summary>
    public static bool NeedsSilence(PoseSet set) => set.Package != null
        && set.Roles.Where(r => r.Custom).GroupBy(r => r.Animation).Any(g => g.Count() > 1 && !g.Any(r => r.Silent));

    /// <summary>The carriers of silent balls: their animation is served without its sounds.</summary>
    public static IEnumerable<int> SilentCarriers(House house) =>
        house.Furnishings.Where(f => f.Poses != null).SelectMany(f => f.Poses!).SelectMany(s => s.Roles).Where(r => r.Custom && r.Silent).Select(r => r.Carrier).Distinct();

    public static IEnumerable<(string Package, int Animation, int Carrier)> Animations(House house) =>
        house.Furnishings.Where(f => f.Poses != null).SelectMany(f => f.Poses!)
            .SelectMany(s => s.Roles.Where(r => r.Custom).Select(r => (s.Package!, r.Animation, r.Carrier))).Distinct();

    /// <summary>The animation packages a home uses.</summary>
    public static IEnumerable<string> Packages(House house) =>
        house.Furnishings.Where(f => f.Poses != null).SelectMany(f => f.Poses!).Select(s => s.Package).OfType<string>().Distinct();

    /// <summary>
    /// Gives a set's custom roles their carriers in this home: an animation the home already plays keeps its carrier;
    /// a new one takes the carrier its fingerprint prefers when free (so an animation tends to keep one carrier across
    /// homes, and a PC rarely serves two animations at one carrier), otherwise the first free one. The set itself
    /// may already be in the home (an edit): its own old carriers count as free.
    /// </summary>
    public static PoseSet AssignCarriers(House house, PoseSet set, Func<string, int, int, int>? fit = null)
    {
        var known = new Dictionary<(string Package, int Animation, bool Silent), int>();
        foreach (var f in house.Furnishings.Where(f => f.Poses != null))
            foreach (var other in f.Poses!.Where(s => s.Id != set.Id))
                foreach (var role in other.Roles.Where(r => r.Custom)) known[(other.Package!, role.Animation, role.Silent)] = role.Carrier;
        var taken = known.Values.ToHashSet();
        var roles = new PoseRole[set.Roles.Length];
        for (var i = 0; i < roles.Length; i++)
        {
            var role = set.Roles[i];
            if (!role.Custom) { roles[i] = role with { Carrier = 0, Animation = 0, Silent = false }; continue; }
            // A ball playing the same animation as an earlier ball of the set is silent, so its sounds play once.
            var silent = set.Roles.Take(i).Any(r => r.Custom && r.Animation == role.Animation);
            var key = (Package: set.Package ?? throw new InvalidDataException("A custom animation needs its package."), role.Animation, Silent: silent);
            if (!known.TryGetValue(key, out var carrier))
            {
                var preferred = (int)(BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"{key.Item1}:{key.Animation}"))) % (uint)PoseCarriers.All.Length) + 1;
                // The free carrier that fits the animation best (fit: how many bodies would draw it on bones it was
                // made for), then the one its fingerprint prefers, then the first in the pool's order (rarest first).
                carrier = Enumerable.Range(1, PoseCarriers.All.Length).Where(c => !taken.Contains(c))
                    .OrderByDescending(c => fit?.Invoke(key.Item1, key.Animation, c) ?? 0).ThenBy(c => c == preferred ? 0 : 1).ThenBy(c => c).DefaultIfEmpty(0).First();
                if (carrier == 0) throw new InvalidOperationException($"A home can play at most {PoseCarriers.All.Length} different custom animations.");
                known[key] = carrier; taken.Add(carrier);
            }
            roles[i] = role with { Carrier = carrier, Silent = silent };
        }
        return set with { Roles = roles };
    }

    /// <summary>A ball's place and facing in the home's own space: its offset through the furnishing's transform, and the furnishing's turn plus its own.</summary>
    public static (Vector3 Position, float Degrees) Place(Furnishing item, PoseRole role) =>
        (Vector3.Transform(role.Offset, PlacementMath.ItemMatrix(item)), item.Rotation + role.Rotation);

    /// <summary>The offset in the furnishing's space for a place in the home's space (the inverse of Place's position).</summary>
    public static Vector3 OffsetFor(Furnishing item, Vector3 position) =>
        Matrix4x4.Invert(PlacementMath.ItemMatrix(item), out var inverse) ? Vector3.Transform(position, inverse) : Vector3.Zero;

    /// <summary>Default balls for a new set: side by side across the top of the furnishing's bounds, facing its front.</summary>
    public static float[][] DefaultPositions(int count, Vector3 minimum, Vector3 maximum)
    {
        var top = Math.Clamp(maximum.Y, 0, 3);
        var width = Math.Max(maximum.X - minimum.X, .6f);
        var center = (minimum + maximum) / 2;
        return Enumerable.Range(0, count).Select(i => new[] { center.X + (count == 1 ? 0 : (i / (count - 1f) - .5f) * Math.Min(width * .6f, .5f * count)), top, center.Z }).ToArray();
    }
}

public static class PoseTilt
{
    /// <summary>
    /// A drawn character's rotation: its facing (yaw, radians, about the world's up), then a tilt about its own right
    /// axis and a lean about its own forward axis (degrees), applied lean first. The pivot is the character's feet.
    /// </summary>
    public static Quaternion Rotation(float yaw, float pitchDegrees, float rollDegrees) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitchDegrees * MathF.PI / 180)
        * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rollDegrees * MathF.PI / 180);
    public static float Wrap(float degrees) => ((degrees % 360) + 540) % 360 - 180;
}

/// <summary>
/// One participant's pose state in a gathering (relay message 0x7D): which set's balls they have open, or which ball
/// they hold, and their fit there: the scale, the tilt and lean in all (the ball's plus theirs), and their own move
/// (in the ball's space, metres) and turn (degrees), so a partner can see and adjust it (PoseAdjust). Role 255 is a
/// viewer (the balls are open, none taken); an empty furnishing clears the entry. The relay stamps the sender's
/// character and refuses a ball someone else holds.
/// </summary>
public readonly record struct PoseEntry(ulong Character, Guid Furnishing, Guid Set, byte Role, float Scale, float Pitch = 0, float Roll = 0, Vector3 Move = default, float Turn = 0)
{
    public const byte Viewing = 255;
    public bool Empty => Furnishing == Guid.Empty;
    public bool Holds => !Empty && Role != Viewing;
    public static PoseEntry None(ulong character) => new(character, Guid.Empty, Guid.Empty, Viewing, 1);
}

/// <summary>
/// Pose entries on the wire: u16 count, then per entry the character (u64), furnishing and set IDs, the role, and the
/// scale, tilt and lean the holder chose, then their move (x, y, z) and turn (f32 each; degrees for the angles). The
/// tilt and lean aren't part of the engine's own sync (only facing is), so every PC draws them from here.
/// </summary>
public static class PoseWire
{
    public const int Protocol = 4;
    public const int MaxEntries = 64;
    public const float MinScale = .25f, MaxScale = 2.5f;
    /// <summary>
    /// How far a player moves from their ball along each of its axes, in metres: MaxMove unless they hold Ctrl (or are
    /// already past it), FarMove at most (what the wire and saved fits accept).
    /// </summary>
    public const float MaxMove = 2, FarMove = 20;
    private const int Entry = 8 + 16 + 16 + 1 + 4 + 4 + 4 + 12 + 4;

    public static byte[] Encode(IReadOnlyCollection<PoseEntry> entries)
    {
        if (entries.Count is 0 or > MaxEntries) throw new InvalidDataException("Invalid pose entry count.");
        var bytes = new byte[2 + entries.Count * Entry];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)entries.Count);
        var at = 2;
        foreach (var entry in entries)
        {
            Check(entry);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), entry.Character);
            entry.Furnishing.TryWriteBytes(bytes.AsSpan(at + 8, 16));
            entry.Set.TryWriteBytes(bytes.AsSpan(at + 24, 16));
            bytes[at + 40] = entry.Role;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 41), entry.Scale);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 45), entry.Pitch);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 49), entry.Roll);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 53), entry.Move.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 57), entry.Move.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 61), entry.Move.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 65), entry.Turn);
            at += Entry;
        }
        return bytes;
    }

    public static PoseEntry[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) throw new InvalidDataException("Truncated pose entries.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (count is 0 or > MaxEntries || payload.Length != 2 + count * Entry) throw new InvalidDataException("Invalid pose entry length.");
        var entries = new PoseEntry[count];
        for (var i = 0; i < count; i++)
        {
            var e = payload.Slice(2 + i * Entry, Entry);
            entries[i] = new(BinaryPrimitives.ReadUInt64LittleEndian(e), new Guid(e.Slice(8, 16)), new Guid(e.Slice(24, 16)), e[40], BinaryPrimitives.ReadSingleLittleEndian(e[41..]),
                BinaryPrimitives.ReadSingleLittleEndian(e[45..]), BinaryPrimitives.ReadSingleLittleEndian(e[49..]),
                new(BinaryPrimitives.ReadSingleLittleEndian(e[53..]), BinaryPrimitives.ReadSingleLittleEndian(e[57..]), BinaryPrimitives.ReadSingleLittleEndian(e[61..])),
                BinaryPrimitives.ReadSingleLittleEndian(e[65..]));
            Check(entries[i]);
        }
        return entries;
    }

    private static void Check(PoseEntry entry)
    {
        if (!float.IsFinite(entry.Scale) || entry.Scale is < MinScale or > MaxScale || !float.IsFinite(entry.Pitch) || !float.IsFinite(entry.Roll)
            || Math.Abs(entry.Pitch) > 360 || Math.Abs(entry.Roll) > 360 || !Moves(entry.Move, entry.Turn)
            || (entry.Empty ? entry.Set != Guid.Empty || entry.Role != PoseEntry.Viewing : entry.Set == Guid.Empty || entry.Role >= PoseSets.MaxRoles && entry.Role != PoseEntry.Viewing))
            throw new InvalidDataException("Invalid pose entry.");
    }

    /// <summary>A move within reach of the ball and a turn in degrees.</summary>
    public static bool Moves(Vector3 move, float turn) =>
        float.IsFinite(move.X) && float.IsFinite(move.Y) && float.IsFinite(move.Z) && Math.Abs(move.X) <= FarMove + .001f && Math.Abs(move.Y) <= FarMove + .001f
        && Math.Abs(move.Z) <= FarMove + .001f && float.IsFinite(turn) && Math.Abs(turn) <= 360;
    /// <summary>A move kept within reach of the ball along each of its axes.</summary>
    public static Vector3 Limit(Vector3 move, bool far = false) => Vector3.Clamp(move, new(-(far ? FarMove : MaxMove)), new(far ? FarMove : MaxMove));
    /// <summary>Whether a move is past MaxMove on any axis (the cap then stays lifted until it's back within).</summary>
    public static bool Beyond(Vector3 move) => Math.Abs(move.X) > MaxMove + .001f || Math.Abs(move.Y) > MaxMove + .001f || Math.Abs(move.Z) > MaxMove + .001f;
}

/// <summary>
/// Hides a set's balls for everyone (relay message 0x7F): the furnishing and the set. The relay drops the set's
/// entries and passes it on; each PC stands up its player if they were on one of its balls.
/// </summary>
public readonly record struct PoseClose(Guid Furnishing, Guid Set)
{
    public byte[] Encode()
    {
        if (Furnishing == Guid.Empty || Set == Guid.Empty) throw new InvalidDataException("Invalid pose close.");
        var bytes = new byte[32];
        Furnishing.TryWriteBytes(bytes.AsSpan(0, 16)); Set.TryWriteBytes(bytes.AsSpan(16, 16));
        return bytes;
    }
    public static PoseClose Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 32) throw new InvalidDataException("Invalid pose close length.");
        var close = new PoseClose(new Guid(payload[..16]), new Guid(payload[16..]));
        if (close.Furnishing == Guid.Empty || close.Set == Guid.Empty) throw new InvalidDataException("Invalid pose close.");
        return close;
    }
}

/// <summary>
/// One player on a set's ball asking another on the same set to take a fit (relay message 0x7E): the move, turn, tilt
/// and lean (their own, as on the HUD) and scale. The relay stamps the sender, forwards it only to the target, and only
/// while both hold balls of that set; the target's PC applies it unless its player turned that off.
/// </summary>
public readonly record struct PoseAdjust(ulong Sender, ulong Target, Guid Furnishing, Guid Set, byte Role, Vector3 Move, float Turn, float Pitch, float Roll, float Scale)
{
    private const int Size = 8 + 8 + 16 + 16 + 1 + 12 + 4 + 4 + 4 + 4;

    public byte[] Encode()
    {
        Check(this);
        var bytes = new byte[Size];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, Sender);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), Target);
        Furnishing.TryWriteBytes(bytes.AsSpan(16, 16));
        Set.TryWriteBytes(bytes.AsSpan(32, 16));
        bytes[48] = Role;
        var at = 49;
        foreach (var value in new[] { Move.X, Move.Y, Move.Z, Turn, Pitch, Roll, Scale }) { BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value); at += 4; }
        return bytes;
    }

    public static PoseAdjust Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != Size) throw new InvalidDataException("Invalid pose adjustment length.");
        var f = new float[7];
        for (var i = 0; i < f.Length; i++) f[i] = BinaryPrimitives.ReadSingleLittleEndian(payload[(49 + i * 4)..]);
        var adjust = new PoseAdjust(BinaryPrimitives.ReadUInt64LittleEndian(payload), BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]), new Guid(payload.Slice(16, 16)),
            new Guid(payload.Slice(32, 16)), payload[48], new(f[0], f[1], f[2]), f[3], f[4], f[5], f[6]);
        Check(adjust);
        return adjust;
    }

    private static void Check(PoseAdjust adjust)
    {
        if (adjust.Target == 0 || adjust.Furnishing == Guid.Empty || adjust.Set == Guid.Empty || adjust.Role >= PoseSets.MaxRoles || !PoseWire.Moves(adjust.Move, adjust.Turn)
            || !float.IsFinite(adjust.Pitch) || !float.IsFinite(adjust.Roll) || Math.Abs(adjust.Pitch) > 360 || Math.Abs(adjust.Roll) > 360
            || !float.IsFinite(adjust.Scale) || adjust.Scale is < PoseWire.MinScale or > PoseWire.MaxScale)
            throw new InvalidDataException("Invalid pose adjustment.");
    }
}

/// <summary>
/// The relay's table of pose entries for one room, one per participant: a ball can be held by one participant at a
/// time, and a claim on a ball someone holds becomes a viewer's entry instead.
/// </summary>
public sealed class PoseTable
{
    private readonly Dictionary<ulong, PoseEntry> entries = [];
    public IReadOnlyCollection<PoseEntry> Entries => entries.Values;

    /// <summary>Takes a participant's entry, refusing a held ball, and returns what stands (to forward and echo).</summary>
    public PoseEntry Apply(PoseEntry entry)
    {
        if (entry.Holds && entries.Values.Any(e => e.Character != entry.Character && e.Holds && e.Furnishing == entry.Furnishing && e.Set == entry.Set && e.Role == entry.Role))
            entry = entry with { Role = PoseEntry.Viewing };
        if (entry.Empty) entries.Remove(entry.Character); else entries[entry.Character] = entry;
        return entry;
    }
    /// <summary>Drops every entry for a set (its balls hidden for everyone), returning whose entries went.</summary>
    public ulong[] Close(Guid furnishing, Guid set)
    {
        var gone = entries.Values.Where(e => e.Furnishing == furnishing && e.Set == set).Select(e => e.Character).ToArray();
        foreach (var character in gone) entries.Remove(character);
        return gone;
    }
    /// <summary>
    /// Whether a participant may hide a set's balls without being allowed to decorate: they hold one of its balls or
    /// have the set open.
    /// </summary>
    public bool OnSet(ulong character, Guid furnishing, Guid set) => Of(character) is { Empty: false } entry && entry.Furnishing == furnishing && entry.Set == set;
    /// <summary>A participant's entry, or null when they have none.</summary>
    public PoseEntry? Of(ulong character) => entries.TryGetValue(character, out var entry) ? entry : null;
    /// <summary>
    /// Whether one participant may adjust another's fit: both hold different balls of the same set (as the relay's
    /// table has them), and the target's is the ball named.
    /// </summary>
    public bool MayAdjust(ulong sender, PoseAdjust adjust) =>
        sender != adjust.Target && Of(sender) is { Holds: true } from && Of(adjust.Target) is { Holds: true } to
        && from.Furnishing == to.Furnishing && from.Set == to.Set && to.Furnishing == adjust.Furnishing && to.Set == adjust.Set && to.Role == adjust.Role;
    /// <summary>Forgets a participant (they left), returning the empty entry to tell the others, or null if they had none.</summary>
    public PoseEntry? Remove(ulong character) => entries.Remove(character) ? PoseEntry.None(character) : null;
    /// <summary>Drops entries for furnishings the home no longer has, or sets they no longer offer.</summary>
    public void Keep(House house)
    {
        var sets = house.Furnishings.Where(f => f.Poses != null).SelectMany(f => f.Poses!.Select(s => (f.Id, s.Id))).ToHashSet();
        foreach (var key in entries.Where(e => !sets.Contains((e.Value.Furnishing, e.Value.Set))).Select(e => e.Key).ToArray()) entries.Remove(key);
    }
}

/// <summary>
/// NPCs on pose balls: a possessed NPC takes a ball like a player, and stays on it when let go. Its entry is a pose entry
/// whose character is the NPC's key (never a content ID: those don't set the top byte), sent as an NPC message (game
/// "npc", kind "pose", Match the NPC) rather than through the relay's pose table, so the relay doesn't hold NPC balls:
/// every PC merges the NPCs' entries with the people's, and a person keeps a ball an NPC also claimed.
/// </summary>
public static class PoseNpcs
{
    private const ulong Mark = 0xFF00_0000_0000_0000;
    /// <summary>The values of a "pose" message: furnishing and set (four ints each), role, scale ×1000, tilt and lean ×100, move ×1000, turn ×100.</summary>
    public const int Values = 16;

    /// <summary>An NPC's key in the pose entries.</summary>
    public static ulong Key(Guid npc)
    {
        Span<byte> bytes = stackalloc byte[16];
        npc.TryWriteBytes(bytes);
        return Mark | BinaryPrimitives.ReadUInt64LittleEndian(bytes) & ~Mark;
    }
    public static bool IsNpc(ulong character) => (character & Mark) == Mark;

    public static int[] Encode(PoseEntry entry)
    {
        var values = new int[Values];
        Ints(entry.Furnishing, values, 0); Ints(entry.Set, values, 4);
        values[8] = entry.Role;
        values[9] = (int)MathF.Round(entry.Scale * 1000); values[10] = (int)MathF.Round(entry.Pitch * 100); values[11] = (int)MathF.Round(entry.Roll * 100);
        values[12] = (int)MathF.Round(entry.Move.X * 1000); values[13] = (int)MathF.Round(entry.Move.Y * 1000); values[14] = (int)MathF.Round(entry.Move.Z * 1000);
        values[15] = (int)MathF.Round(entry.Turn * 100);
        return values;
    }

    /// <summary>An NPC's entry from a "pose" message, or null when it isn't a valid one.</summary>
    public static PoseEntry? Decode(Guid npc, int[]? values)
    {
        if (values is not { Length: Values } v || v[8] is < 0 or > 255) return null;
        var entry = new PoseEntry(Key(npc), Guid(v, 0), Guid(v, 4), (byte)v[8], v[9] / 1000f, v[10] / 100f, v[11] / 100f, new(v[12] / 1000f, v[13] / 1000f, v[14] / 1000f), v[15] / 100f);
        if (entry.Empty) return PoseEntry.None(entry.Character);
        try { PoseWire.Encode([entry]); return entry; }   // the wire's own checks: scale, angles, reach, role
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// The people's entries and the NPCs': an NPC on a ball a person holds (or a lower-keyed NPC holds) only views it,
    /// so a ball has one holder on every PC whatever order the claims arrived in.
    /// </summary>
    public static IEnumerable<PoseEntry> Merge(IReadOnlyCollection<PoseEntry> people, IEnumerable<PoseEntry> npcs)
    {
        var held = people.Where(e => e.Holds).Select(e => (e.Furnishing, e.Set, e.Role)).ToHashSet();
        foreach (var person in people) yield return person;
        foreach (var npc in npcs.OrderBy(e => e.Character))
            if (npc.Holds && !held.Add((npc.Furnishing, npc.Set, npc.Role))) yield return npc with { Role = PoseEntry.Viewing };
            else yield return npc;
    }

    private static void Ints(Guid id, int[] values, int at)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        for (var i = 0; i < 4; i++) values[at + i] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 4)..]);
    }
    private static Guid Guid(int[] values, int at)
    {
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes[(i * 4)..], values[at + i]);
        return new Guid(bytes);
    }
}

/// <summary>Where a set stands, from the entries of the people present: who holds each ball, and whether it plays.</summary>
public static class PoseSessions
{
    /// <summary>The character on each of the set's balls (0 for a free one).</summary>
    public static ulong[] Holders(IEnumerable<PoseEntry> entries, Guid furnishing, PoseSet set)
    {
        var holders = new ulong[set.Roles.Length];
        foreach (var e in entries)
            if (e.Holds && e.Furnishing == furnishing && e.Set == set.Id && e.Role < holders.Length && holders[e.Role] == 0) holders[e.Role] = e.Character;
        return holders;
    }
    /// <summary>A set plays once every ball is held.</summary>
    public static bool Playing(IEnumerable<PoseEntry> entries, Guid furnishing, PoseSet set) => Holders(entries, furnishing, set).All(h => h != 0);
    /// <summary>A set's balls show while anyone has it open or holds one of its balls.</summary>
    public static bool Open(IEnumerable<PoseEntry> entries, Guid furnishing, Guid set) => entries.Any(e => !e.Empty && e.Furnishing == furnishing && e.Set == set);
}

/// <summary>
/// A player's own fit on a ball, kept on their PC: how far they moved, turned and scaled from the ball. A fit is kept
/// for the set's role, the character, and who else was in the set, so a pair's cuddle is ready the next time.
/// </summary>
public sealed record PoseFit
{
    public string Character { get; init; } = "";
    public Guid Set { get; init; }
    public int Role { get; init; }
    /// <summary>The others in the set (PoseFits.PartnersKey), or empty for a fit kept without partners.</summary>
    public string Partners { get; init; } = "";
    public float[] Offset { get; init; } = [0, 0, 0];
    public float Turn { get; init; }
    public float Pitch { get; init; }
    public float Roll { get; init; }
    public float Scale { get; init; } = 1;
    public DateTimeOffset Saved { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public Vector3 Move => new(Offset[0], Offset[1], Offset[2]);
}

public static class PoseFits
{
    public const int MaxFits = 2000;
    /// <summary>The partners in a set, as a key that keeps no content ID: each one's HomeOwners key, sorted.</summary>
    public static string PartnersKey(IEnumerable<ulong> characters) => string.Join(",", characters.Where(c => c != 0).Distinct().Select(HomeOwners.Key).Order(StringComparer.Ordinal));
    /// <summary>The fit to use: kept with these partners, else the latest kept on this ball by this character.</summary>
    public static PoseFit? Find(IEnumerable<PoseFit> fits, string character, Guid set, int role, string partners)
    {
        var mine = fits.Where(f => f.Character == character && f.Set == set && f.Role == role).ToArray();
        return mine.FirstOrDefault(f => f.Partners == partners) ?? mine.OrderByDescending(f => f.Saved).FirstOrDefault();
    }
    /// <summary>Keeps a fit, replacing one for the same ball and partners, and keeps the list bounded (oldest go first).</summary>
    public static List<PoseFit> Keep(IEnumerable<PoseFit> fits, PoseFit fit)
    {
        var kept = fits.Where(f => !(f.Character == fit.Character && f.Set == fit.Set && f.Role == fit.Role && f.Partners == fit.Partners)).Append(fit)
            .OrderByDescending(f => f.Saved).Take(MaxFits).ToList();
        return kept;
    }
    public static bool Valid(PoseFit fit) => fit.Offset is { Length: 3 } && fit.Offset.All(x => float.IsFinite(x) && Math.Abs(x) <= PoseWire.FarMove) && float.IsFinite(fit.Turn)
        && float.IsFinite(fit.Pitch) && float.IsFinite(fit.Roll)
        && float.IsFinite(fit.Scale) && fit.Scale is >= PoseWire.MinScale and <= PoseWire.MaxScale && fit.Role is >= 0 and < PoseSets.MaxRoles;
}
