namespace Haven.Core;

/// <summary>
/// One file of an NPC's mods: the game paths it stands in for, its SHA-256 (lowercase hex), and (from 0.57.6) its SHA-1,
/// the name Mare-style sync plugins give it, so it can be sent again without reading it.
/// </summary>
public sealed record NpcModFile(string[] GamePaths, string Hash, long Size, string? Sha1 = null);

/// <summary>
/// An NPC's mods as this PC keeps them: the files its owner's character was
/// drawn with while dressed as it, kept by fingerprint in this PC's NPC store, and its meta changes (Penumbra's
/// manipulation string). Never part of the home: each PC builds its own from what its sync plugin downloaded.
/// </summary>
/// Mods: how many Penumbra mods the files came from (0.58.1; null for sets kept before, or when nobody could tell).
/// <summary>
/// An NPC's mods as one PC keeps them. Origin (from 0.59.0): on a guest, the owner's version of the set it came from
/// (NpcMods.Version); null on the owner's own sets and on sets kept before. From (0.60.1): whose set it is, the
/// HomeOwners.Key of the character that shared it (the owner's own key on their sets); null on sets kept before. NPC ids
/// travel with copies of a home, so another host's NPC can carry the same id: only From may update or drop a set.
/// </summary>
public sealed record NpcModSet(Guid Npc, NpcModFile[] Files, string Meta, DateTime Shared, int? Mods = null, int? Origin = null, string? From = null);

/// <summary>
/// The rules and messages for sharing an NPC's mods. The owner's PC sends the fingerprint of every file (the first
/// 64 bits of its SHA-256) as NPC messages (game "npc", Match = the NPC); guests copy only files whose fingerprint is
/// on that list from what Penumbra draws the owner with on their PC. The messages reuse kinds the relay already
/// carries for minigames, so relay 0.39.0 passes them unchanged and older Haven versions, whose NPC code ignores kinds
/// it doesn't know, ignore them: no protocol change.
/// </summary>
public static class NpcMods
{
    /// <summary>Owner (0.57.2): about to dress as the NPC (Values: share), so guests note what the owner is drawn with first.</summary>
    public const string Announce = "invite";
    /// <summary>Owner (0.57.2): fingerprints of the NPC's game paths, laid out as Prints (Values: share, number, pairs).</summary>
    public const string PathPrints = "play";
    /// <summary>Owner: a share began (Values: share, files, messages, megabytes; from 0.57.2 also paths, path messages).</summary>
    public const string Begin = "start";
    /// <summary>Owner: fingerprints (Values: share, message number, then two ints per fingerprint, low half first).</summary>
    public const string Prints = "move";
    /// <summary>Owner: the share is over and the owner wears their own look again (Values: share).</summary>
    public const string Finish = "end";
    /// <summary>
    /// Owner (0.57.2): each file's SHA-1 fingerprint, laid out as Prints. Mare-style sync plugins (Lightless, PlayerSync)
    /// name a downloaded file, and the copies they make of it (Lightless's downscaled, BC7 and decimated folders), after
    /// the SHA-1 of the uploader's original, so a guest recognises its sync plugin's copy of a file even when its bytes
    /// were changed on the way.
    /// </summary>
    public const string SyncPrints = "leave";
    /// <summary>Guest to owner (0.57.2): how far it has got (Values: share, found, wanted), every few seconds while it changes.</summary>
    public const string Progress = "rematch";
    /// <summary>Guest to owner: has the mods (Values: share, files copied, files listed).</summary>
    public const string Took = "accept";
    /// <summary>Guest to owner: couldn't get them, or not all (Values: share, files copied, files listed).</summary>
    public const string Missed = "decline";

    /// <summary>
    /// Owner (0.59.0): which mods an NPC carries now, sent as Finish with this first value (share ids are positive, so
    /// older guests ignore it): Values StateMark, version, layout (NpcMods.Version, Layout; 0 and 0 for none). Sent to
    /// a guest when they join, and to everyone when the owner's mods for an NPC change (kept, forgotten).
    /// </summary>
    public const int StateMark = -2;
    /// <summary>
    /// Guest to owner (0.59.0): its copy of an NPC's mods is older than the owner's, sent as Progress with this first
    /// value (older owners ignore it): Values StaleMark.
    /// </summary>
    public const int StaleMark = -3;

    /// <summary>
    /// A set's version: its files' fingerprints and its meta, hashed to a positive int. The owner's and a guest's copy
    /// of one set differ in bytes when a sync plugin recompressed files on the way, so a guest records the owner's
    /// version when it keeps a share (NpcModSet.Origin) rather than working its own out.
    /// </summary>
    public static int Version(IEnumerable<string> hashes, string meta) => Positive([.. hashes.Order(StringComparer.Ordinal), "meta:" + meta]);

    /// <summary>
    /// A set's layout: the game paths it changes, hashed to a positive int. The same on the owner and on a guest who
    /// kept the whole set, so it tells a guest's set kept before versions were recorded whether it is the owner's.
    /// </summary>
    public static int Layout(IEnumerable<string> gamePaths) => Positive([.. gamePaths.Select(p => p.Replace('\\', '/').ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)]);

    /// <summary>The version and layout of a kept set.</summary>
    public static (int Version, int Layout) Identify(NpcModSet set) => (set.Origin ?? Version(set.Files.Select(f => f.Hash), set.Meta), Layout(set.Files.SelectMany(f => f.GamePaths)));

    /// <summary>
    /// Whether a guest's set is the one the owner says the NPC carries: by the version it recorded, or (a set kept
    /// before versions were recorded) by its layout.
    /// </summary>
    public static bool Current(NpcModSet guest, int version, int layout) => guest.Origin is { } origin ? origin == version : Layout(guest.Files.SelectMany(f => f.GamePaths)) == layout;

    /// <summary>A state message's values for an NPC's set (or none), and reading them back.</summary>
    public static int[] State(NpcModSet? set) => set is null ? [StateMark, 0, 0] : Identify(set) is var (version, layout) ? [StateMark, version, layout] : [];
    public static (int Version, int Layout)? ReadState(int[]? values) => values is [StateMark, var version and >= 0, var layout and >= 0] && (version == 0) == (layout == 0) ? (version, layout) : null;

    private static int Positive(string[] items)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", items)));
        var value = BitConverter.ToInt32(hash, 0) & int.MaxValue;
        return value == 0 ? 1 : value;
    }

    public const int PrintsPerMessage = (GameMessage.MaxValues - 2) / 2;
    public const int MaxFiles = 2000;
    public const long MaxFileBytes = 512L << 20, MaxBytes = 3L << 30;

    // Files a character is drawn with that an NPC may carry. Anything else (UI, scenery, sound banks for the world) stays out.
    private static readonly HashSet<string> Extensions =
        ["mdl", "mtrl", "tex", "atex", "sklb", "skp", "phyb", "pbd", "eid", "pap", "tmb", "avfx", "scd", "shpk", "imc"];
    private static readonly string[] Roots = ["chara/", "vfx/", "sound/", "shader/"];

    /// <summary>Whether a game path is one an NPC's mods may change.</summary>
    public static bool Shareable(string gamePath)
        => gamePath.Length is > 6 and <= 260 && Roots.Any(r => gamePath.StartsWith(r, StringComparison.Ordinal))
            && !gamePath.Contains("..", StringComparison.Ordinal) && gamePath.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.' or '/')
            && Extensions.Contains(Extension(gamePath));

    public static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    /// <summary>A file's fingerprint: the first 64 bits of its SHA-256 (lowercase hex).</summary>
    public static ulong Print(string hash) => Convert.ToUInt64(hash[..16], 16);

    /// <summary>A game path's fingerprint: the first 64 bits of the SHA-256 of its text.</summary>
    public static ulong PathPrint(string gamePath)
        => Print(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(gamePath))));

    /// <summary>How many SHA-1 fingerprint messages a share's opening announces (0.57.2 and later); 0 when none.</summary>
    public static int ReadSyncMessages(int[]? values) => values is [_, _, var messages, _, _, _, var sync, ..] && sync == messages ? sync : 0;

    /// <summary>The SHA-1 a sync plugin's file is named after (40 hex digits before the extension), or null.</summary>
    public static string? SyncName(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        return stem.Length == 40 && stem.All(char.IsAsciiHexDigit) ? stem.ToLowerInvariant() : null;
    }

    /// <summary>The game paths a share's opening announces (0.57.2 and later), or null.</summary>
    public static (int Paths, int Messages)? ReadPaths(int[]? values)
        => values is [_, _, _, _, var paths and > 0 and <= MaxFiles * 4, var messages, ..] && messages == (paths + PrintsPerMessage - 1) / PrintsPerMessage
            ? (paths, messages) : null;

    public static bool ValidHash(string hash) => hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>The fingerprint messages' values, in order.</summary>
    public static List<int[]> Chunks(int share, IReadOnlyList<ulong> prints)
    {
        var chunks = new List<int[]>();
        for (var at = 0; at < prints.Count; at += PrintsPerMessage)
        {
            var part = prints.Skip(at).Take(PrintsPerMessage).ToArray();
            var values = new int[2 + part.Length * 2];
            values[0] = share; values[1] = chunks.Count;
            for (var i = 0; i < part.Length; i++) { values[2 + i * 2] = unchecked((int)(uint)part[i]); values[3 + i * 2] = unchecked((int)(uint)(part[i] >> 32)); }
            chunks.Add(values);
        }
        return chunks;
    }

    /// <summary>A fingerprint message's number and fingerprints, or null when it isn't one of this share's.</summary>
    public static (int Number, ulong[] Prints)? Read(int share, int[]? values)
    {
        if (values is not { Length: >= 4 } v || v[0] != share || v[1] < 0 || (v.Length - 2) % 2 != 0) return null;
        var prints = new ulong[(v.Length - 2) / 2];
        for (var i = 0; i < prints.Length; i++) prints[i] = (uint)v[2 + i * 2] | (ulong)(uint)v[3 + i * 2] << 32;
        return (v[1], prints);
    }

    /// <summary>A share's opening values, or null when they are out of bounds.</summary>
    public static (int Share, int Files, int Messages)? ReadBegin(int[]? values)
        => values is [var share, var files and > 0 and <= MaxFiles, var messages, ..] && messages == (files + PrintsPerMessage - 1) / PrintsPerMessage
            ? (share, files, messages) : null;

    /// <summary>Whether a stored set is one this PC may build an NPC's collection from.</summary>
    public static bool Valid(NpcModSet set)
        => set.Npc != Guid.Empty && set.Files.Length is > 0 and <= MaxFiles && set.Meta.Length <= 1 << 20 && set.Mods is null or (>= 0 and <= MaxFiles)
            && set.Files.All(f => ValidHash(f.Hash) && f.Size is > 0 and <= MaxFileBytes && f.GamePaths.Length > 0 && f.GamePaths.All(Shareable)
                && (f.Sha1 == null || f.Sha1.Length == 40 && f.Sha1.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            && set.Files.Sum(f => f.Size) <= MaxBytes;
}
