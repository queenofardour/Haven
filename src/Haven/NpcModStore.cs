using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Haven.Core;

namespace Haven;

/// <summary>A modded file a character is drawn with: its game paths, where it is on disk, and its fingerprints.</summary>
internal sealed record NpcModFound(string[] GamePaths, string File, long Size, string Hash, string Sha1);

/// <summary>
/// What this PC keeps of NPCs' mods: each NPC's set (`npc-mods/<npc>.json`: game paths, fingerprints,
/// meta) and the files themselves, by fingerprint, in `npc-mods/files`. Also reads which modded files a character is
/// drawn with (hashing them), and the Penumbra collection the owner chose for an NPC, if any. Sets and choices are read
/// on the framework thread; Read and Keep run in the background, one at a time (NpcModShare's busy flag).
/// </summary>
internal sealed class NpcModStore(IDalamudPluginInterface plugin, IPluginLog log)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly Dictionary<Guid, NpcModSet?> sets = [];
    private Dictionary<Guid, Guid>? choices;
    // Fingerprints by file, size and time written, so polling doesn't read a file twice (background only).
    private readonly Dictionary<string, (long Size, DateTime Written, string Hash, string Sha1)> hashes = [];

    public string Root => Path.Combine(plugin.ConfigDirectory.FullName, "npc-mods");
    private string FilesRoot => Path.Combine(Root, "files");
    private string ChoicesPath => Path.Combine(Root, "collections.json");
    private string SetPath(Guid npc) => Path.Combine(Root, $"{npc:N}.json");
    // Marks an NPC whose mods the player forgot here (NpcModShare.Forget): what tells guests to drop their copy, as
    // against a PC that merely keeps no set for it (another PC of the owner's, a fresh install, a set unreadable).
    private string ForgottenPath(Guid npc) => Path.Combine(Root, $"{npc:N}.forgotten");
    private string StorePath(string hash, string gamePath) => Path.Combine(FilesRoot, hash[..2], $"{hash}.{NpcMods.Extension(gamePath)}");

    // The Penumbra collection chosen for an NPC ---------------------------------

    /// <summary>The Penumbra collection last used to capture an NPC's mods on this PC, if any.</summary>
    public Guid? CollectionOf(Guid npc) => Choices().TryGetValue(npc, out var id) ? id : null;

    public void Choose(Guid npc, Guid? collection)
    {
        var all = Choices();
        if (collection is { } id) all[npc] = id; else all.Remove(npc);
        Directory.CreateDirectory(Root);
        File.WriteAllText(ChoicesPath + ".tmp", JsonSerializer.Serialize(all, Json));
        File.Move(ChoicesPath + ".tmp", ChoicesPath, true);
    }

    private Dictionary<Guid, Guid> Choices()
    {
        if (choices != null) return choices;
        try { choices = File.Exists(ChoicesPath) ? JsonSerializer.Deserialize<Dictionary<Guid, Guid>>(File.ReadAllText(ChoicesPath)) ?? [] : []; }
        catch (Exception ex) { log.Warning("[NPC-MODS] the NPCs' collections couldn't be read ({Message}).", ex.Message); choices = []; }
        return choices;
    }

    // Sets ----------------------------------------------------------------

    /// <summary>The mods this PC keeps for an NPC, or null.</summary>
    public NpcModSet? Stored(Guid npc)
    {
        if (sets.TryGetValue(npc, out var known)) return known;
        NpcModSet? set = null;
        var path = SetPath(npc);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<NpcModSet>(File.ReadAllText(path)) is { } read && read.Npc == npc && NpcMods.Valid(read)) set = read;
        }
        catch (Exception ex) { log.Warning("[NPC-MODS] {Path} couldn't be read ({Message}).", path, ex.Message); }
        return sets[npc] = set;
    }

    /// <summary>An NPC's game paths and the files in the store standing in for them, and its meta; null without a whole set.</summary>
    public (Dictionary<string, string> Paths, string Meta)? CollectionFiles(Guid npc)
    {
        if (Stored(npc) is not { } set) return null;
        var paths = new Dictionary<string, string>();
        foreach (var file in set.Files)
        {
            var stored = StorePath(file.Hash, file.GamePaths[0]);
            // A set with a file gone from the store would draw the NPC broken (its meta may point at that file).
            if (!File.Exists(stored)) { log.Warning("[NPC-MODS] a file of the NPC's mods is missing from the store; it keeps the look it had."); return null; }
            foreach (var game in file.GamePaths) paths[game] = stored;
        }
        return paths.Count == 0 ? null : (paths, set.Meta);
    }

    /// <summary>The folder in Penumbra's mod folder holding an NPC's files while they are sent to guests (ShareFolder).</summary>
    public static string ShareMod(Guid npc) => $"Haven NPC {npc:N}"[..18];

    /// <summary>
    /// An NPC's files laid out in Penumbra's own mod folder, as a mod of their own (ShareMod), to send to guests. Sync
    /// plugins send only files they have indexed, which are those in Penumbra's mod folder or their own cache (Lightless
    /// 3.3.1 FileCacheManager.CreateFileEntry/CreateCacheEntry), never Haven's store. Dressed in the store's copies,
    /// guests got the NPC's Glamourer look and none of its files (0.58.7, in game). The mod redirects nothing
    /// itself, so switching it on in Penumbra changes nothing: the share wears its files through a temporary
    /// collection. A marker skips copying again. Returns its game paths and files, and the meta. Background only.
    /// </summary>
    public (Dictionary<string, string> Paths, string Meta) ShareFolder(NpcModSet set, string penumbraRoot, string name)
    {
        var folder = Path.Combine(penumbraRoot, ShareMod(set.Npc));
        var marker = Path.Combine(folder, "haven-npc.json");
        var key = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", set.Files.Select(f => f.Hash + ":" + string.Join(",", f.GamePaths))))));
        var files = Path.Combine(folder, "files");
        var paths = new Dictionary<string, string>();
        var fresh = !File.Exists(marker) || File.ReadAllText(marker).Trim() != key;
        if (fresh && Directory.Exists(files)) Directory.Delete(files, recursive: true);
        foreach (var file in set.Files)
        {
            var to = Path.Combine(files, file.GamePaths[0].Replace('/', Path.DirectorySeparatorChar));
            if (fresh || !File.Exists(to))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(StorePath(file.Hash, file.GamePaths[0]), to, overwrite: true);
            }
            foreach (var game in file.GamePaths) paths[game] = to;
        }
        if (fresh)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var meta = new System.Text.Json.Nodes.JsonObject
            {
                ["FileVersion"] = 3, ["Name"] = $"{name} (Haven NPC)", ["Author"] = "Haven",
                ["Description"] = "A copy of a Haven NPC's mods, so your sync plugin can send them to your guests. Haven wears it for a moment while you share the NPC's mods; it changes nothing switched on. Forget the NPC's mods in Haven to remove it.",
                ["Version"] = "1", ["Website"] = "", ["ModTags"] = new System.Text.Json.Nodes.JsonArray("Haven"),
            };
            var data = new System.Text.Json.Nodes.JsonObject { ["Name"] = "", ["Priority"] = 0, ["Files"] = new System.Text.Json.Nodes.JsonObject(), ["FileSwaps"] = new System.Text.Json.Nodes.JsonObject(), ["Manipulations"] = new System.Text.Json.Nodes.JsonArray() };
            File.WriteAllText(Path.Combine(folder, "meta.json"), meta.ToJsonString(options));
            File.WriteAllText(Path.Combine(folder, "default_mod.json"), data.ToJsonString(options));
            File.WriteAllText(marker, key);
        }
        return (paths, set.Meta);
    }

    /// <summary>Every set this PC keeps (framework thread; each read once, as Stored does).</summary>
    public IReadOnlyList<NpcModSet> All()
    {
        if (!Directory.Exists(Root)) return [];
        var found = new List<NpcModSet>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var npc) && Stored(npc) is { } set) found.Add(set);
        return found;
    }

    /// <summary>Deletes store files no kept set uses any more. Only while nothing is being kept (NpcModShare's busy flag).</summary>
    public void TidyFiles() => Tidy();

    /// <summary>A set Keep wrote (framework thread).</summary>
    public void Remember(NpcModSet set) => sets[set.Npc] = set;

    /// <summary>
    /// For sets kept before their mods were counted (0.58.0): how many mods in Penumbra's mod folder hold their files,
    /// matched by size, then fingerprint, against every file there. Sets with no file found there (a guest's, from a
    /// sync plugin) get no count. Background only; reads files, writes nothing.
    /// </summary>
    public Dictionary<Guid, int> CountMods(IReadOnlyList<NpcModSet> uncounted, string modDirectory)
    {
        var bySize = new Dictionary<long, HashSet<string>>();
        foreach (var file in uncounted.SelectMany(s => s.Files))
            (bySize.TryGetValue(file.Size, out var hashes) ? hashes : bySize[file.Size] = []).Add(file.Hash);
        var folders = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // fingerprint → mod folders
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in Directory.EnumerateFiles(modDirectory, "*", options))
        {
            var info = new FileInfo(path);
            if (!bySize.TryGetValue(info.Length, out var wanted)) continue;
            string hash;
            try { using var stream = File.OpenRead(path); hash = Convert.ToHexStringLower(SHA256.HashData(stream)); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (!wanted.Contains(hash)) continue;
            var folder = Path.GetRelativePath(modDirectory, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (folder.StartsWith(ShareMod(Guid.Empty)[..10], StringComparison.Ordinal)) continue;   // Haven's own copies for sharing
            (folders.TryGetValue(hash, out var known) ? known : folders[hash] = new(StringComparer.OrdinalIgnoreCase)).Add(folder);
        }
        var counts = new Dictionary<Guid, int>();
        foreach (var set in uncounted)
        {
            // A file several mods hold alike counts once, under the first of them.
            var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in set.Files)
                if (folders.TryGetValue(file.Hash, out var holding) && !holding.Overlaps(mods)) mods.Add(holding.Order(StringComparer.OrdinalIgnoreCase).First());
            if (mods.Count > 0) counts[set.Npc] = Math.Min(mods.Count, NpcMods.MaxFiles);
        }
        return counts;
    }

    /// <summary>Writes an NPC's set again with its mods counted (framework thread).</summary>
    public void SetCount(Guid npc, int mods)
    {
        if (Stored(npc) is not { Mods: null } set) return;
        var counted = set with { Mods = mods };
        try
        {
            var path = SetPath(npc);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(counted, Json));
            File.Move(path + ".tmp", path, true);
            sets[npc] = counted;
        }
        catch (IOException ex) { log.Warning("[NPC-MODS] {Npc}'s mod count couldn't be saved ({Message}).", npc, ex.Message); }
    }

    /// <summary>
    /// Gives a copied home's NPC the set and collection choice its original has here (HomeNpcs.Renumber). The files are
    /// shared by fingerprint, so only the set is written; it becomes this player's own (no sharer, no owner's version).
    /// </summary>
    public void Copy(Guid from, Guid to)
    {
        if (CollectionOf(from) is { } collection) Choose(to, collection);
        if (Stored(from) is not { } set) return;
        var copy = set with { Npc = to, Origin = null, From = null };
        var path = SetPath(to);
        Directory.CreateDirectory(Root);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(copy, Json));
        File.Move(path + ".tmp", path, true);
        sets[to] = copy;
    }

    /// <summary>
    /// Forgets an NPC's set (its files go at the next tidy, unless another set uses them); forgotten: the player chose
    /// to, so guests who join later are told to drop theirs too (Forgotten).
    /// </summary>
    public void Drop(Guid npc, bool forgotten = false)
    {
        try { File.Delete(SetPath(npc)); } catch (IOException) { }
        sets[npc] = null;
        if (!forgotten) return;
        try { Directory.CreateDirectory(Root); File.WriteAllText(ForgottenPath(npc), ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warning("[NPC-MODS] couldn't note that {Npc}'s mods were forgotten ({Message}).", npc, ex.Message); }
    }

    /// <summary>Whether the player forgot this NPC's mods here (and hasn't kept new ones since).</summary>
    public bool Forgotten(Guid npc) => File.Exists(ForgottenPath(npc));

    // Reading and keeping (background) -----------------------------------------

    /// <summary>
    /// The modded files a character is drawn with that an NPC may carry (Penumbra's resource paths: disk path → game
    /// paths), fingerprinted; with npcPaths, only files at those game paths (by fingerprint). A file that can't be read
    /// now (held open, just deleted) is left out, not the whole look.
    /// </summary>
    public List<NpcModFound> Read(Dictionary<string, HashSet<string>> paths, HashSet<ulong>? npcPaths = null)
    {
        var found = new Dictionary<string, NpcModFound>();
        long total = 0;
        foreach (var (actual, games) in paths)
        {
            if (!Path.IsPathRooted(actual)) continue;   // the game's own file, or one swapped for another game file
            var shareable = games.Select(g => g.Replace('\\', '/').ToLowerInvariant()).Where(NpcMods.Shareable).Distinct().ToArray();
            if (shareable.Length == 0 || npcPaths != null && !shareable.Any(g => npcPaths.Contains(NpcMods.PathPrint(g)))) continue;
            var info = new FileInfo(actual);
            if (!info.Exists || info.Length is 0 or > NpcMods.MaxFileBytes) continue;
            if (!hashes.TryGetValue(actual, out var known) || known.Size != info.Length || known.Written != info.LastWriteTimeUtc)
            {
                if (Fingerprints(actual) is not { } prints) continue;
                if (hashes.Count > 20000) hashes.Clear();
                hashes[actual] = known = (info.Length, info.LastWriteTimeUtc, prints.Sha256, prints.Sha1);
            }
            if (found.TryGetValue(known.Hash, out var same)) { found[known.Hash] = same with { GamePaths = [.. same.GamePaths.Union(shareable)] }; continue; }
            if (found.Count >= NpcMods.MaxFiles || (total += info.Length) > NpcMods.MaxBytes)
            {
                // A whole look (no npcPaths: the owner's own) cut short would be kept and shared as if whole, its meta
                // pointing at models left out: the broken NPC a guest's all-or-nothing keep avoids.
                if (npcPaths == null) throw new InvalidDataException($"your look has more modded files than an NPC can carry ({NpcMods.MaxFiles} files or {NpcMods.MaxBytes >> 30} GB at most).");
                break;
            }
            found[known.Hash] = new NpcModFound(shareable, actual, info.Length, known.Hash, known.Sha1);
        }
        return [.. found.Values];
    }

    /// <summary>An NPC's kept files as they are sent again (SHA-1 read from the store for sets kept before 0.57.6).</summary>
    public List<NpcModFound> Sent(NpcModSet set)
        => [.. set.Files.Select(f =>
        {
            var stored = StorePath(f.Hash, f.GamePaths[0]);
            var sha1 = f.Sha1 ?? Fingerprints(stored)?.Sha1 ?? new string('0', 40);
            return new NpcModFound(f.GamePaths, stored, f.Size, f.Hash, sha1);
        })];

    // SHA-256 checks a copy; SHA-1 is what Mare-style sync plugins name a file by (NpcMods.SyncPrints).
    private (string Sha256, string Sha1)? Fingerprints(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var buffer = new byte[1 << 16];
            int read;
            while ((read = stream.Read(buffer)) > 0) { sha256.AppendData(buffer, 0, read); sha1.AppendData(buffer, 0, read); }
            return (Convert.ToHexStringLower(sha256.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Debug("[NPC-MODS] couldn't read {File}: {Message}", file, ex.Message); return null; }
    }

    /// <summary>
    /// Copies every file into the store (checked against its fingerprint) and writes the NPC's set, then tidies the
    /// store. All or nothing: part of a set can carry meta for models it lacks, which draws a broken NPC.
    /// </summary>
    public NpcModSet Keep(Guid npc, string name, IReadOnlyList<NpcModFound> found, string meta, int? mods, int? origin = null, string? from = null)
    {
        var missed = 0;
        foreach (var file in found)
        {
            var stored = StorePath(file.Hash, file.GamePaths[0]);
            if (File.Exists(stored)) continue;
            var temp = stored + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
                File.Copy(file.File, temp, true);
                // A file changed while it was copied isn't the one fingerprinted.
                bool same;
                using (var check = File.OpenRead(temp)) same = Convert.ToHexStringLower(SHA256.HashData(check)) == file.Hash;
                if (same) File.Move(temp, stored, true);
                else { File.Delete(temp); missed++; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                missed++;
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
        if (missed > 0) throw new IOException($"{missed} of {found.Count} file(s) couldn't be copied");
        var set = new NpcModSet(npc, [.. found.Select(f => new NpcModFile(f.GamePaths, f.Hash, f.Size, f.Sha1))], meta, DateTime.UtcNow, mods, origin, from);
        if (!NpcMods.Valid(set)) throw new InvalidDataException($"{name}'s mods couldn't be kept.");
        var path = SetPath(npc);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(set, Json));
        File.Move(path + ".tmp", path, true);
        try { File.Delete(ForgottenPath(npc)); } catch (IOException) { }
        Tidy();
        return set;
    }

    // Deletes store files no kept set uses any more (an NPC's mods replaced or forgotten), and copies left half-done.
    // Runs only in Keep, after the new set is written, so it never races a copy.
    private void Tidy()
    {
        try
        {
            if (!Directory.Exists(FilesRoot)) return;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var setFile in Directory.EnumerateFiles(Root, "*.json"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(setFile), "N", out _)) continue;   // collections, the share guard
                try
                {
                    if (JsonSerializer.Deserialize<NpcModSet>(File.ReadAllText(setFile)) is { } kept)
                        foreach (var f in kept.Files) used.Add(Path.GetFileName(StorePath(f.Hash, f.GamePaths[0])));
                }
                catch (Exception ex) when (ex is IOException or JsonException) { return; }   // unsure what's used: delete nothing
            }
            var removed = 0;
            foreach (var file in Directory.EnumerateFiles(FilesRoot, "*", SearchOption.AllDirectories))
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || !used.Contains(Path.GetFileName(file)))
                    try { File.Delete(file); removed++; } catch (IOException) { }
            if (removed > 0) log.Information("[NPC-MODS] removed {Count} file(s) no NPC uses any more.", removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Debug("[NPC-MODS] the store couldn't be tidied: {Message}", ex.Message); }
    }
}
