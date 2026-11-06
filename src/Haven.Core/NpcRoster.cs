using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Haven.Core;

/// <summary>
/// One of a player's saved NPCs: what defines it, without a place. Kept
/// on this PC per character (NpcRoster), brought into a home as a new HomeNpc or into someone else's gathering as a
/// visiting NPC. Its kept Penumbra mods are kept under its own Id (NpcModStore), copied from the NPC it was saved from;
/// as a visitor it keeps that Id, so every PC that kept its mods finds them again on the next visit.
/// </summary>
public sealed record RosterNpc
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Npc";
    public byte? Level { get; init; }
    public NpcLook Look { get; init; } = new();
    public ushort? Emote { get; init; }
    public DateTime Saved { get; init; }
    /// <summary>The home NPC it was saved from: added to a home on this PC, the new NPC gets a copy of the mods this PC kept for it.</summary>
    public Guid? Source { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Valid => Id != Guid.Empty && HomeNpcs.ValidName(Name) && Level is null or (>= 1 and <= HomeNpcs.MaxLevel) && Look is { Valid: true } && Emote != 0;

    public static RosterNpc From(HomeNpc npc) => new() { Name = npc.Name, Level = npc.Level, Look = npc.Look, Emote = npc.Emote, Saved = DateTime.UtcNow, Source = npc.Id };

    /// <summary>A new NPC for a home (a fresh id, so it never shares mods or state with another copy), standing where given.</summary>
    public HomeNpc ToHome(float[] position, float yaw) => new() { Name = Name, Level = Level, Look = Look, Emote = Emote, Position = position, Yaw = yaw };
}

/// <summary>
/// The players' saved NPCs on this PC, per character (HomeOwners.Key), in npc-roster.json, written atomically. A
/// damaged file reads as empty rather than stopping Haven. Also keeps which characters turn visiting NPCs away from
/// their homes.
/// </summary>
public sealed class NpcRoster
{
    public const int Max = 40;
    private sealed record Stored(Dictionary<string, RosterNpc[]> Characters, HashSet<string>? Refusing = null);
    private readonly string path;
    /// <summary>
    /// Set by the plugin: copies the mods this PC keeps for one NPC to another (saving: from the home's NPC to the new
    /// entry's id), and drops those kept under an id (an entry removed: nothing else uses its id).
    /// </summary>
    public Action<Guid, Guid>? CopyMods { get; set; }
    public Action<Guid>? DropMods { get; set; }
    private Dictionary<string, RosterNpc[]> characters = [];
    private HashSet<string> refusing = [];

    public NpcRoster(string path)
    {
        this.path = Path.GetFullPath(path);
        if (!File.Exists(this.path)) return;
        try
        {
            var read = JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(this.path));
            characters = read?.Characters?.Where(x => IsKey(x.Key) && x.Value != null)
                .ToDictionary(x => x.Key, x => x.Value.Where(n => n is { Valid: true }).Take(Max).ToArray()) ?? [];
            refusing = read?.Refusing?.Where(IsKey).ToHashSet() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { characters = []; refusing = []; }
    }

    public IReadOnlyList<RosterNpc> Of(string character) => characters.GetValueOrDefault(character) ?? [];
    /// <summary>Every character's saved NPCs on this PC.</summary>
    public IEnumerable<RosterNpc> Everyone => characters.Values.SelectMany(x => x);
    public RosterNpc? Find(string character, Guid id) => Of(character).FirstOrDefault(x => x.Id == id);

    /// <summary>Saves an NPC to a character's roster (a new entry each time).</summary>
    public RosterNpc Add(string character, HomeNpc npc)
    {
        if (!IsKey(character)) throw new InvalidOperationException("Log in to a character first.");
        if (Of(character).Count >= Max) throw new InvalidOperationException($"You can keep up to {Max} NPCs. Remove one first.");
        var saved = RosterNpc.From(npc);
        if (!saved.Valid) throw new InvalidOperationException("That NPC can't be saved.");
        characters[character] = [.. Of(character), saved];
        Save();
        CopyMods?.Invoke(npc.Id, saved.Id);
        return saved;
    }

    public void Rename(string character, Guid id, string name)
    {
        name = name.Trim();
        if (!HomeNpcs.ValidName(name) || !HomeNpcs.PlayerStyleName(name))
            throw new InvalidOperationException("An NPC needs a first and last name, each starting with a capital letter (letters, ' and - only).");
        Change(character, id, x => x with { Name = name });
    }

    /// <summary>Removes a saved NPC, and the mods kept under its id (homes it was added to keep their own copies).</summary>
    public void Remove(string character, Guid id)
    {
        if (Find(character, id) == null) return;
        Change(character, id, _ => null);
        if (characters.Values.All(list => list.All(x => x.Id != id))) DropMods?.Invoke(id);
    }

    /// <summary>Whether a character turns visiting NPCs away from homes they own.</summary>
    public bool Refuses(string character) => refusing.Contains(character);
    public void SetRefusing(string character, bool refuse)
    {
        if (!IsKey(character) || (refuse ? !refusing.Add(character) : !refusing.Remove(character))) return;
        Save();
    }

    private void Change(string character, Guid id, Func<RosterNpc, RosterNpc?> change)
    {
        if (Of(character).All(x => x.Id != id)) return;
        characters[character] = [.. Of(character).Select(x => x.Id == id ? change(x) : x).OfType<RosterNpc>()];
        Save();
    }

    private static bool IsKey(string? key) => key is { Length: 16 } && key.All(Uri.IsHexDigit);

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Stored(characters, refusing))); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Who may possess a visiting NPC, set by the player who brought it.</summary>
public enum VisitRights : byte { OnlyMe = 0, MeAndHost = 1, Anyone = 2 }

/// <summary>
/// An NPC a guest brought into someone else's home for a gathering: never part of the home, never saved there. Id is
/// its roster entry's (made when it was saved, so never a home NPC's; a home that has it anyway turns it away), so
/// its mods kept on any PC follow it from visit to visit. Position and Yaw where it was brought, Rights who may possess it.
/// </summary>
public sealed record VisitingNpc
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "Npc";
    public byte? Level { get; init; }
    public NpcLook Look { get; init; } = new();
    public ushort? Emote { get; init; }
    public float[] Position { get; init; } = [0, 0, 0];
    public float Yaw { get; init; }
    public VisitRights Rights { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Valid => Id != Guid.Empty && HomeNpcs.ValidName(Name) && Level is null or (>= 1 and <= HomeNpcs.MaxLevel) && Look is { Valid: true } && Emote != 0
        && Position is { Length: 3 } && Position.All(x => float.IsFinite(x) && Math.Abs(x) <= 10000) && SafeAngles.Sane(Yaw) && Enum.IsDefined(Rights);

    /// <summary>As the NPC code draws it: an NPC of the home's kind, never away, with no saved ball.</summary>
    public HomeNpc AsHomeNpc() => new() { Id = Id, Name = Name, Level = Level, Look = Look, Emote = Emote, Position = Position, Yaw = Yaw };

    public static VisitingNpc From(RosterNpc npc, float[] position, float yaw, VisitRights rights) => new()
        { Id = npc.Id, Name = npc.Name, Level = npc.Level, Look = npc.Look, Emote = npc.Emote, Position = position, Yaw = yaw, Rights = rights };
}

/// <summary>
/// The rules and messages for visiting NPCs. They travel as NPC messages (game "npc", kind "say", Match = the visitor)
/// with a negative first value no older Haven reads: relay 0.42.x passes them unchanged, so no relay deploy. Kinds
/// are a closed list at the relay and every one is taken (NpcMods, NpcPossession); "say" carries text, which a look
/// needs. Possession, movement, emotes, speech and pose balls then use the NPC messages home NPCs use.
///   Part (Values Part, version, index, count; Text a piece): the bringer's definition, JSON gzipped in base64, to
///     everyone (and to each late joiner).
///   Welcome (Values Welcome): the home's owner lets it in; every PC shows a visitor only once welcomed.
///   Away (Values Away): the home's owner sends it away (or turns it away).
///   Gone (Values Gone): its bringer took it back.
///   Ask (Values Ask, version): to the bringer for a definition not complete after a while, or to the owner for a
///     welcome not heard.
/// </summary>
public static class VisitingNpcs
{
    public const string Kind = "say";
    public const int Part = -20, Welcome = -21, Away = -22, Gone = -23, Ask = -24;
    /// <summary>Visitors one guest may bring at once, and visitors a home takes in total (besides its own NPCs).</summary>
    public const int PerGuest = 2, Max = 4;
    public const int PieceChars = 400, MaxPieces = 200, MaxJson = 160 * 1024;

    public static bool IsVisit(GameMessage message) => message.Game == "npc" && message.Kind == Kind && message.Values is [<= Part and >= Ask, ..];

    /// <summary>A visitor's definition in pieces, each sent as Part with this version.</summary>
    public static IReadOnlyList<(int[] Values, string Text)> Encode(VisitingNpc npc, int version)
    {
        if (!npc.Valid) throw new InvalidOperationException("That NPC can't be brought along.");
        using var buffer = new MemoryStream();
        using (var zip = new GZipStream(buffer, CompressionLevel.SmallestSize, true)) zip.Write(JsonSerializer.SerializeToUtf8Bytes(npc));
        var text = Convert.ToBase64String(buffer.ToArray());
        var count = (text.Length + PieceChars - 1) / PieceChars;
        if (count > MaxPieces) throw new InvalidOperationException("That NPC's look is too large to bring along.");
        return [.. Enumerable.Range(0, count).Select(i => (new[] { Part, version, i, count }, text.Substring(i * PieceChars, Math.Min(PieceChars, text.Length - i * PieceChars))))];
    }

    /// <summary>A whole definition read back, or null when it isn't a valid visitor for that id.</summary>
    public static VisitingNpc? Decode(Guid id, string text)
    {
        try
        {
            using var zip = new GZipStream(new MemoryStream(Convert.FromBase64String(text)), CompressionMode.Decompress);
            var bytes = new byte[MaxJson + 1];
            int total = 0, read;
            while (total < bytes.Length && (read = zip.Read(bytes, total, bytes.Length - total)) > 0) total += read;
            if (total > MaxJson) return null;
            var npc = JsonSerializer.Deserialize<VisitingNpc>(bytes.AsSpan(0, total));
            return npc is { Valid: true } && npc.Id == id ? npc : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Whether a character may possess a visitor: its bringer always; the home's owner too with MeAndHost; anyone in
    /// the gathering with Anyone.
    /// </summary>
    public static bool MayPossess(VisitRights rights, ulong character, ulong bringer, ulong homeOwner, bool member) =>
        character != 0 && (character == bringer || rights == VisitRights.MeAndHost && character == homeOwner && homeOwner != 0 || rights == VisitRights.Anyone && member);

    /// <summary>
    /// Whose word counts for an NPC's mods (NpcModShare: shares, state, the older-copy alert): a home NPC's, the home's
    /// owner's; a visitor's (bringer set), its bringer's only, never the home's owner's or anyone else's.
    /// </summary>
    public static bool ModsFrom(ulong sender, ulong? bringer, ulong homeOwner) =>
        sender != 0 && (bringer is { } brought ? sender == brought : sender == homeOwner);

    /// <summary>
    /// Whether a share or state from a sender may change the set this PC keeps for an NPC: a set someone else shared
    /// (NpcModSet.From) is theirs alone. Sets kept before sharers were recorded (no From) may be.
    /// </summary>
    public static bool MayReplace(NpcModSet? kept, ulong sender) => kept?.From is not { } from || from == HomeOwners.Key(sender);

    /// <summary>
    /// The home owner's answer to a visitor: null to let it in, else why not. Visitors already in (id → bringer), the
    /// home's own NPC ids, whether the owner turns visitors away.
    /// </summary>
    public static string? Refusal(VisitingNpc npc, ulong bringer, ulong homeOwner, IReadOnlyDictionary<Guid, ulong> visitors, IEnumerable<Guid> homeNpcs, bool refusing)
    {
        if (bringer == 0 || bringer == homeOwner) return "The home's owner adds NPCs to the home instead.";
        if (refusing) return "This home doesn't take visiting NPCs.";
        if (homeNpcs.Contains(npc.Id) || visitors.TryGetValue(npc.Id, out var other) && other != bringer) return "That NPC's id is taken here.";
        if (visitors.ContainsKey(npc.Id)) return null;
        if (visitors.Values.Count(x => x == bringer) >= PerGuest) return $"Each guest can bring up to {PerGuest} NPCs.";
        if (visitors.Count >= Max) return $"This home has {Max} visiting NPCs already.";
        return null;
    }

    /// <summary>
    /// Whether a guest's PC (not the owner's) should ignore one more new visitor from a bringer: the owner's limits
    /// (PerGuest, Max) applied to what is held, welcomed or not, so a guest sending endless ids can't fill every
    /// other PC's list (each unwelcomed one asks the owner again every few seconds).
    /// </summary>
    public static bool Crowded(ulong bringer, IEnumerable<ulong> heldBringers) =>
        heldBringers.Count(x => x == bringer) >= PerGuest || heldBringers.Count() >= Max;

    /// <summary>
    /// Pieces of definitions as they arrive, per sender and visitor. A new version starts again; at most a few
    /// definitions per sender are kept waiting, and those quiet for a minute are dropped.
    /// </summary>
    public sealed class Assembler
    {
        private sealed class Partial(int version, int count, DateTime started)
        {
            public readonly int Version = version, Count = count;
            public readonly string?[] Pieces = new string?[count];
            public int Have;
            public DateTime Started = started, Heard = started;
        }
        private readonly Dictionary<(ulong Sender, Guid Id), Partial> partials = [];
        private const int PerSender = 4;

        /// <summary>A piece; the whole visitor once its last piece is in (null until then, or when it's no visitor).</summary>
        public VisitingNpc? Add(ulong sender, Guid id, int[]? values, string? text, DateTime now)
        {
            if (values is not [Part, var version, var index, var count] || count is < 1 or > MaxPieces || index < 0 || index >= count
                || text is not { Length: > 0 and <= PieceChars } || sender == 0 || id == Guid.Empty) return null;
            var key = (sender, id);
            if (!partials.TryGetValue(key, out var partial) || partial.Version != version || partial.Count != count)
            {
                if (!partials.ContainsKey(key) && partials.Keys.Count(k => k.Sender == sender) >= PerSender) return null;
                partials[key] = partial = new Partial(version, count, now);
            }
            partial.Heard = now;
            if (partial.Pieces[index] == null) { partial.Pieces[index] = text; partial.Have++; }
            if (partial.Have < partial.Count) return null;
            partials.Remove(key);
            return Decode(id, string.Concat(partial.Pieces));
        }

        /// <summary>Definitions still missing pieces after a while (to ask again), and forgets those quiet for a minute.</summary>
        public IReadOnlyList<(ulong Sender, Guid Id, int Version)> Waiting(DateTime now, double after)
        {
            foreach (var key in partials.Where(x => (now - x.Value.Heard).TotalSeconds > 60).Select(x => x.Key).ToList()) partials.Remove(key);
            return [.. partials.Where(x => (now - x.Value.Heard).TotalSeconds > after).Select(x => (x.Key.Sender, x.Key.Id, x.Value.Version))];
        }

        public void Forget(Guid id) { foreach (var key in partials.Keys.Where(k => k.Id == id).ToList()) partials.Remove(key); }
        public void Clear() => partials.Clear();
    }
}
