namespace Haven.Core;

/// <summary>
/// An NPC saved in a home: where it stands, which way it faces, and its look. Every PC in a visit
/// spawns its own copy; possessing one moves it for the visit, and only "Save its spot" stores the place. Housing
/// protocol 31 adds Away (kept in the home but not there now, for everyone) and Emote (a held emote it rests in, such
/// as sitting), both omitted when unset.
/// </summary>
public sealed record HomeNpc
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Npc";
    public float[] Position { get; init; } = [0, 0, 0];
    public float Yaw { get; init; }
    public NpcLook Look { get; init; } = new();
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Away { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ushort? Emote { get; init; }
    /// <summary>
    /// A pose ball it takes whenever it's in the home (housing protocol 32): saved with its spot while it was on one.
    /// Position and Yaw stay where it stands when it leaves the ball.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NpcSeat? Seat { get; init; }
    /// <summary>The level its nameplate and the target bar show (housing protocol 33), 1 to HomeNpcs.MaxLevel; none when null.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public byte? Level { get; init; }
}

/// <summary>
/// An NPC's saved pose ball: the furnishing, its set and the ball (role), and the NPC's fit there as a player's is kept
/// (PoseFit): a move in the ball's own space (metres), a turn, tilt and lean (degrees) and a scale.
/// </summary>
public sealed record NpcSeat
{
    public Guid Furnishing { get; init; }
    public Guid Set { get; init; }
    public int Role { get; init; }
    public float[] Move { get; init; } = [0, 0, 0];
    public float Turn { get; init; }
    public float Pitch { get; init; }
    public float Roll { get; init; }
    public float Scale { get; init; } = 1;

    /// <summary>The NPC's entry for this seat (the ball's own tilt and lean added, as entries carry them).</summary>
    public PoseEntry Entry(ulong key, PoseRole role) =>
        new(key, Furnishing, Set, (byte)Role, Scale, PoseTilt.Wrap(role.Pitch + Pitch), PoseTilt.Wrap(role.Roll + Roll), new(Move[0], Move[1], Move[2]), Turn);

    /// <summary>A seat for an entry an NPC holds (the ball's own tilt and lean taken out).</summary>
    public static NpcSeat From(PoseEntry entry, PoseRole role) => new()
    {
        Furnishing = entry.Furnishing, Set = entry.Set, Role = entry.Role, Move = [entry.Move.X, entry.Move.Y, entry.Move.Z], Turn = entry.Turn,
        Pitch = PoseTilt.Wrap(entry.Pitch - role.Pitch), Roll = PoseTilt.Wrap(entry.Roll - role.Roll), Scale = entry.Scale,
    };

    internal bool Valid(House house) => Furnishing != Guid.Empty && Set != Guid.Empty && Move is { Length: 3 } && PoseWire.Moves(new(Move[0], Move[1], Move[2]), Turn)
        && float.IsFinite(Pitch) && Math.Abs(Pitch) <= 360 && float.IsFinite(Roll) && Math.Abs(Roll) <= 360 && float.IsFinite(Scale) && Scale is >= PoseWire.MinScale and <= PoseWire.MaxScale
        && house.Furnishings.FirstOrDefault(f => f.Id == Furnishing)?.Poses?.FirstOrDefault(s => s.Id == Set) is { } set && Role >= 0 && Role < set.Roles.Length;
}

/// <summary>
/// Haven's own appearance record, read from a character's DrawData: the 26 customize bytes and the ten equipment
/// slots (head, body, hands, legs, feet, ears, neck, wrists, right and left ring), each the game's EquipmentModelId
/// value: model id (bits 0-15), variant (16-23), first and second dye (24-31, 32-39). Weapons (main hand, off hand) are
/// the game's WeaponModelId values: model id (0-15), type (16-31), variant (32-47), first and second dye (48-55, 56-63);
/// omitted (null) on looks saved before they were kept, which show the spawner's weapons. Design is a Glamourer state
/// (its base64 export, housing protocol 30): every PC with Glamourer applies it to its copy once drawn; the customize,
/// gear and weapons are what PCs without Glamourer see.
/// </summary>
public sealed record NpcLook
{
    public const int CustomizeBytes = 26, GearSlots = 10;
    public const int WeaponSlots = 2;
    public byte[] Customize { get; init; } = new byte[CustomizeBytes];
    public ulong[] Gear { get; init; } = new ulong[GearSlots];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ulong[]? Weapons { get; init; }
    public const int MaxDesign = 32 * 1024;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Design { get; init; }
    /// <summary>
    /// Its Customize+ profile (housing protocol 36): Customize+'s own profile JSON, as its IPC gives and takes it,
    /// gzipped and base64 (NpcBodies). Applied as a temporary profile on every PC with Customize+; others see the
    /// plain body. Not part of SameAs: a new body is applied, the NPC isn't redrawn for it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; init; }
    public bool Valid => Customize is { Length: CustomizeBytes } && Gear is { Length: GearSlots } && Gear.All(x => x < 1UL << 40)
        && Weapons is null or { Length: WeaponSlots } && (Design == null || ValidDesign(Design)) && (Body == null || ValidDesign(Body));

    /// <summary>
    /// Whether the game can draw this body: a race it has (1-8), a sex (0-1) and one of that race's two clans. A home
    /// shared by someone else could carry anything here; an NPC whose look isn't drawable keeps its last look instead.
    /// Not part of Valid, so a home saved with such a look still opens; not written to the home file either.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Drawable => Customize is { Length: CustomizeBytes } c && c[0] is >= 1 and <= 8 && c[1] <= 1 && (c[4] == c[0] * 2 - 1 || c[4] == c[0] * 2);

    /// <summary>A Glamourer state as Haven stores it: base64 text within the size limit.</summary>
    public static bool ValidDesign(string design) => design.Length is > 0 and <= MaxDesign
        && design.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=');

    /// <summary>Same body, gear, weapons and Glamourer state.</summary>
    public bool SameAs(NpcLook other) => Customize.SequenceEqual(other.Customize) && Gear.SequenceEqual(other.Gear)
        && (Weapons ?? []).SequenceEqual(other.Weapons ?? []) && Design == other.Design;
}

/// <summary>Customize+ profiles as an NPC's look keeps them (NpcLook.Body): gzipped JSON in base64, like Glamourer's states.</summary>
public static class NpcBodies
{
    /// <summary>The JSON a body unpacks to may be at most this long (a profile of every bone is far smaller).</summary>
    public const int MaxJson = 512 * 1024;

    public static string? Pack(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJson) return null;
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.SmallestSize, true))
            zip.Write(System.Text.Encoding.UTF8.GetBytes(json));
        var body = Convert.ToBase64String(buffer.ToArray());
        return NpcLook.ValidDesign(body) ? body : null;
    }

    /// <summary>A body's JSON, or null when it isn't one (bad base64, not gzip, too large when unpacked, not JSON).</summary>
    public static string? Unpack(string body)
    {
        try
        {
            using var zip = new System.IO.Compression.GZipStream(new MemoryStream(Convert.FromBase64String(body)), System.IO.Compression.CompressionMode.Decompress);
            var bytes = new byte[MaxJson + 1];
            int total = 0, read;
            while (total < bytes.Length && (read = zip.Read(bytes, total, bytes.Length - total)) > 0) total += read;
            if (total > MaxJson) return null;
            var json = System.Text.Encoding.UTF8.GetString(bytes, 0, total);
            using var _ = System.Text.Json.JsonDocument.Parse(json);
            return json;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or System.Text.Json.JsonException) { return null; }
    }
}

public static class HomeNpcs
{
    public const int Max = 8, MaxName = 20, MaxLevel = 100;

    /// <summary>The home with saved pose balls it no longer has let go (a furnishing or set removed): those NPCs stand at their spots.</summary>
    public static House DropLostSeats(House house) => house.Npcs is { } npcs && npcs.Any(n => n?.Seat is { } seat && !seat.Valid(house))
        ? house with { Npcs = [.. npcs.Select(n => n?.Seat is { } seat && !seat.Valid(house) ? n with { Seat = null } : n)] } : house;

    /// <summary>
    /// A copy of a home (Duplicate, Import, a visitor's saved copy) with new ids for its NPCs, so the copy's NPCs keep
    /// their own mods and state on this PC rather than sharing the original's; renamed maps each old id to its new one.
    /// </summary>
    public static House Renumber(House house, out IReadOnlyDictionary<Guid, Guid> renamed)
    {
        var map = new Dictionary<Guid, Guid>();
        renamed = map;
        if (house.Npcs is not { Length: > 0 } npcs) return house;
        return house with { Npcs = [.. npcs.Select(n => n == null ? n : n with { Id = map[n.Id] = Guid.NewGuid() })] };
    }

    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= MaxName && !name.Any(char.IsControl);

    internal static void Validate(House house)
    {
        if (house.Npcs is not { } npcs) return;
        if (npcs.Length > Max) throw new InvalidDataException($"A home holds at most {Max} NPCs.");
        var ids = new HashSet<Guid>();
        foreach (var npc in npcs)
            if (npc == null || npc.Id == Guid.Empty || !ids.Add(npc.Id) || !ValidName(npc.Name)
                || npc.Position is not { Length: 3 } || !npc.Position.All(x => float.IsFinite(x) && Math.Abs(x) <= 10000)
                || !SafeAngles.Sane(npc.Yaw) || npc.Look is not { Valid: true } || npc.Emote == 0 || npc.Seat != null && !npc.Seat.Valid(house) || npc.Level is 0 or > MaxLevel)
                throw new InvalidDataException("Invalid NPC identity, name, place, look, pose ball or level.");
    }

    // Default names, in player-name form (see GameName), so a new NPC's name is the same everywhere.
    private static readonly string[] Numbers = ["One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve"];

    /// <summary>A name for a new NPC that no NPC in the home has yet ("Npc One", "Npc Two"…).</summary>
    public static string NewName(IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var number in Numbers) if (!used.Contains($"Npc {number}")) return $"Npc {number}";
        for (var i = 1; ; i++) if (!used.Contains($"Npc {i}")) return $"Npc {i}";
    }

    /// <summary>
    /// The game's player-name form: a forename and a surname, each 2-15 characters, starting with a capital letter and
    /// otherwise lowercase letters, hyphens and apostrophes (no hyphen beside another or after an apostrophe), at most
    /// 21 characters with the space. Glamourer (through Penumbra's actor identification) recognises a character only by
    /// such a name and its home world.
    /// </summary>
    public static bool PlayerStyleName(string name)
    {
        if (name.Length is < 5 or > 21) return false;
        var space = name.IndexOf(' ');
        if (space < 0 || name.IndexOf(' ', space + 1) >= 0) return false;
        return Part(name[..space]) && Part(name[(space + 1)..]);

        static bool Part(string part)
        {
            if (part.Length is < 2 or > 15 || part[0] is < 'A' or > 'Z') return false;
            for (var i = 1; i < part.Length; i++)
            {
                var c = part[i];
                if (c is not ('\'' or '-' or >= 'a' and <= 'z')) return false;
                if (c == '-' && part[i - 1] is '-' or '\'' || c == '\'' && part[i - 1] == '-') return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The name each NPC's game character carries, in home order. A display name in player-name form is used as it is
    /// (so it reads the same in the target bar); any other is turned into one ("Nora" → "Nora Haven"). Names stay unique
    /// among the home's NPCs and never equal one of <paramref name="avoid"/> (the players in the world here), so
    /// Glamourer and Penumbra never mix an NPC up with a real character. The same list gives the same names on every PC
    /// unless a player nearby shares an NPC's name.
    /// </summary>
    public static string[] GameNames(IReadOnlyList<HomeNpc> npcs, IEnumerable<string>? avoid = null)
    {
        var used = new HashSet<string>(avoid ?? [], StringComparer.Ordinal);
        var names = new string[npcs.Count];
        for (var i = 0; i < npcs.Count; i++)
        {
            var display = npcs[i].Name.Trim();
            if (PlayerStyleName(display) && used.Add(display)) { names[i] = display; continue; }
            // The first run of letters, as a forename.
            var start = display.TakeWhile(c => !char.IsAsciiLetter(c)).Count();
            var letters = new string(display.Skip(start).TakeWhile(char.IsAsciiLetter).ToArray());
            var first = letters.Length >= 2 ? char.ToUpperInvariant(letters[0]) + letters[1..].ToLowerInvariant() : "Npc";
            for (var n = 0; ; n++)
            {
                // Haven, Havena, Havenb… (then Havenaa…): lowercase letters keep the surname in player-name form.
                var surname = "Haven" + Suffix(n);
                var forename = first[..Math.Min(first.Length, Math.Min(15, 20 - surname.Length))];
                if (used.Add($"{forename} {surname}")) { names[i] = $"{forename} {surname}"; break; }
            }
        }
        return names;

        static string Suffix(int n)
        {
            var suffix = "";
            while (n > 0) { n--; suffix = (char)('a' + n % 26) + suffix; n /= 26; }
            return suffix;
        }
    }

    /// <summary>The list as stored: null when empty, so homes without NPCs read as before.</summary>
    public static HomeNpc[]? Stored(IEnumerable<HomeNpc> npcs) => npcs.ToArray() is { Length: > 0 } list ? list : null;
}
