using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Haven.Core;
using Haven.Core.Animation;
using Haven.Core.Furniture;
using HavenNative.Sync;

namespace Haven;

/// <summary>An animation package in this PC's library: the name its owner gave it, when it was added, and when the locker last stored it.</summary>
internal sealed record AnimationRecord(string Name, DateTimeOffset Added, DateTimeOffset? Stored = null);

/// <summary>
/// Custom animations for pose balls. A package (Haven.Core.Animation) is kept as its .zip under
/// its fingerprint in content/animations/packages, unpacked into content/animations/files/&lt;hash&gt;, and served by
/// Penumbra at the files of the carrier emote each animation plays on in the current home (PoseCarriers), renamed to
/// the names that emote's own animation declares, since the game plays only a file declaring them. Served files go
/// for everyone (AddTemporaryModAll) but only while a home that uses them is open, so a carrier emote is its usual self
/// elsewhere.
/// </summary>
internal sealed class PoseAnimationLibrary : IDisposable
{
    private const string Tag = "Haven.PoseAnimations";
    private const int Priority = 99;
    private readonly IDalamudPluginInterface plugin;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly string packages, unpacked, recordsPath;
    private readonly object gate = new();
    private Dictionary<string, AnimationRecord> records = [];
    // Read and unpacked packages, by fingerprint.
    private readonly Dictionary<string, PoseAnimationManifest> ready = [];
    private readonly ConcurrentDictionary<string, Task> working = new();
    // Packages that couldn't be unpacked: tried again after a wait that doubles (from a minute), and reported once.
    private readonly ConcurrentDictionary<string, (long RetryAt, int Tries)> unpackFailed = new();
    // Packages that need game data this game lacks (PackageNeeds): never served here, not tried again this session.
    private readonly ConcurrentDictionary<string, byte> missingData = new();
    /// <summary>Whether a package needs game data this game doesn't have (a free trial): its balls stand still here.</summary>
    public bool LacksGameData(string hash) => missingData.ContainsKey(hash);
    // The names each carrier key's own animation declares (null: the game has no file there, so nothing to serve).
    private readonly Dictionary<string, IReadOnlyList<string>?> carrierNames = [];
    // What Penumbra serves now (game path → file), and what each carrier has served in this game session, by the
    // package, animation and file each path came from. A carrier once bound to other content keeps it bound in a
    // character until it is redrawn (the game caches a character's animations), so a change is reported for a redraw.
    private Dictionary<string, string> served = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> carrierContent = [];
    private DateTimeOffset nextRegistration;
    // Prepare runs every frame in a home; the redirect map (18 races per key, file checks for sounds and effects) is
    // rebuilt only when what it is built from changed: the inputs, or packages unpacked, renamed or forgotten (generation).
    private int generation;
    private int preparedGeneration = -1;
    private (string Package, int Animation, int Carrier)[] preparedAnimations = [];
    private int[] preparedStandIns = [], preparedSilent = [];
    private bool preparedSounds;
    private void Changed() => Interlocked.Increment(ref generation);
    public bool PenumbraMissing { get; private set; }
    public readonly ConcurrentQueue<string> Failures = new();
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(120), MaxResponseContentBufferSize = ContentLocker.MaxFurnitureBytes };
    public Action<HttpResponseMessage>? KeepUsage { get; set; }

    public PoseAnimationLibrary(IDalamudPluginInterface plugin, IDataManager data, IPluginLog log)
    {
        this.plugin = plugin; this.data = data; this.log = log;
        var directory = Path.Combine(plugin.ConfigDirectory.FullName, "content", "animations");
        packages = Path.Combine(directory, "packages");
        unpacked = Path.Combine(directory, "files");
        recordsPath = Path.Combine(directory, "records.json");
        try { if (File.Exists(recordsPath)) records = JsonSerializer.Deserialize<Dictionary<string, AnimationRecord>>(File.ReadAllText(recordsPath)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException) { log.Warning(ex, "[Haven] The custom animation list could not be read."); }
        records = records.Where(r => ContentLocker.IsFingerprint(r.Key) && File.Exists(PackageFile(r.Key))).ToDictionary();
    }

    /// <summary>The player's animation packages, newest first.</summary>
    public (string Hash, AnimationRecord Item)[] Library { get { lock (gate) return [.. records.OrderByDescending(r => r.Value.Added).Select(r => (r.Key, r.Value))]; } }
    public AnimationRecord? Item(string hash) { lock (gate) return records.GetValueOrDefault(hash); }
    private string PackageFile(string hash) => Path.Combine(packages, hash[..2], hash + ".zip");
    private string Folder(string hash) => Path.Combine(unpacked, hash[..16]);
    // Packages are named by their fingerprint, so once a file is there it stays right until Haven deletes it: asked
    // every frame for every hash in a home, the answer is remembered instead of asking the disk again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> present = new();
    public bool Has(string hash) => present.ContainsKey(hash) || File.Exists(PackageFile(hash)) && present.TryAdd(hash, 0) | true;
    /// <summary>A file of a package as unpacked on this PC, or null while it isn't.</summary>
    public string? UnpackedFile(string hash, string name) => Manifest(hash) != null && File.Exists(Path.Combine(Folder(hash), name)) ? Path.Combine(Folder(hash), name) : null;

    /// <summary>A package's manifest once it is read on this PC; null until then.</summary>
    public PoseAnimationManifest? Manifest(string hash) { lock (gate) return ready.GetValueOrDefault(hash); }
    /// <summary>A package's name: the player's own for their packages, otherwise its manifest's once read.</summary>
    public string? Name(string hash) { lock (gate) return records.TryGetValue(hash, out var item) ? item.Name : ready.TryGetValue(hash, out var m) ? m.Name : null; }

    /// <summary>Reads one of the player's packages now (for choosing its animations), keeping it ready.</summary>
    public PoseAnimationManifest Load(string hash)
    {
        if (Manifest(hash) is { } known) return known;
        Unpack(hash);
        return Manifest(hash) ?? throw new InvalidOperationException("That animation package could not be read.");
    }

    /// <summary>Builds a package from chosen animations of a mod, keeps it, and adds it to the library under the given name.</summary>
    public string Import(PenumbraMod mod, string name, IReadOnlyList<(PoseAnimationCandidate Candidate, string Name)> chosen, PosePropCandidate? prop = null)
    {
        var (zip, manifest) = PoseAnimations.Build(mod, name, chosen, path => data.FileExists(path) ? data.GetFile(path)?.Data : null, prop is { } kept ? [kept] : null);
        var hash = ContentLocker.Fingerprint(zip);
        Store(hash, zip);
        lock (gate)
        {
            records[hash] = new(manifest.Name, records.TryGetValue(hash, out var old) ? old.Added : DateTimeOffset.UtcNow);
            Save();
        }
        return hash;
    }

    /// <summary>Keeps a package (imported, or fetched for a home), verified by its fingerprint and checked in full.</summary>
    public void Store(string hash, byte[] zip)
    {
        if (!ContentLocker.IsFingerprint(hash) || ContentLocker.Fingerprint(zip) != hash) throw new InvalidDataException("The animations do not match their fingerprint.");
        PoseAnimations.Read(zip);
        var path = PackageFile(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) { var temporary = path + ".tmp"; File.WriteAllBytes(temporary, zip); File.Move(temporary, path, overwrite: true); }
    }

    public void Rename(string hash, string name)
    {
        lock (gate)
        {
            if (!records.TryGetValue(hash, out var item)) return;
            records[hash] = item with { Name = SafeText.Clean(name, 40).Trim() is { Length: > 0 } clean ? clean : item.Name };
            Save();
        }
    }

    /// <summary>Forgets a package and deletes its files from this PC. The caller checks that no home uses it.</summary>
    public void Delete(string hash)
    {
        lock (gate) { records.Remove(hash); ready.Remove(hash); Save(); }
        foreach (var key in renamed.Keys.Where(k => k.StartsWith(hash + "|", StringComparison.Ordinal)).ToArray()) renamed.TryRemove(key, out _);
        Changed();
        try
        {
            present.TryRemove(hash, out _);
            File.Delete(PackageFile(hash));
            if (Directory.Exists(Folder(hash))) Directory.Delete(Folder(hash), true);
        }
        catch (IOException ex) { log.Warning(ex, "[Haven] A custom animation's files could not be deleted."); }
    }

    // Downloaded for other people's homes (ContentCache) ------------------------

    /// <summary>Packages on this PC that aren't the player's own: fetched for other people's homes, with their unpacked files.</summary>
    public List<CachedContent> Downloaded()
    {
        var found = new List<CachedContent>();
        if (!Directory.Exists(packages)) return found;
        HashSet<string> mine; lock (gate) mine = [.. records.Keys];
        foreach (var file in new DirectoryInfo(packages).EnumerateFiles("*.zip", SearchOption.AllDirectories))
        {
            var hash = Path.GetFileNameWithoutExtension(file.Name);
            if (!ContentLocker.IsFingerprint(hash) || mine.Contains(hash)) continue;
            found.Add(new("animations", hash, file.Length + FolderSize(hash), file.LastWriteTimeUtc));
        }
        return found;
    }

    // An unpacked folder's size, remembered once it has files. Served copies are added to it as carriers need them, so
    // it is read again after a while.
    private readonly ConcurrentDictionary<string, (long Size, long At)> folderSizes = new();
    private long FolderSize(string hash)
    {
        if (folderSizes.TryGetValue(hash, out var known) && Environment.TickCount64 - known.At < 60_000) return known.Size;
        try
        {
            var size = Directory.Exists(Folder(hash)) ? new DirectoryInfo(Folder(hash)).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
            folderSizes[hash] = (size, Environment.TickCount64);
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Notes that a home uses a downloaded package now, so the cache keeps it longest.</summary>
    public void Touch(string hash)
    {
        if (!ContentLocker.IsFingerprint(hash) || Item(hash) != null || !Has(hash)) return;
        try { File.SetLastWriteTimeUtc(PackageFile(hash), DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Deletes a downloaded package (never one of the player's own) and its unpacked files; it is fetched again when a
    /// home needs it. Outside a home only, when nothing is served (HousingController clears the library there).
    /// </summary>
    public long ForgetDownloaded(string hash)
    {
        if (!ContentLocker.IsFingerprint(hash) || Item(hash) != null || !File.Exists(PackageFile(hash))
            || working.TryGetValue(hash, out var busy) && !busy.IsCompleted) return 0;
        folderSizes.TryRemove(hash, out _);
        var size = new FileInfo(PackageFile(hash)).Length + FolderSize(hash);
        Delete(hash);
        unpackFailed.TryRemove(hash, out _); folderSizes.TryRemove(hash, out _);
        return size;
    }

    public void Change(string hash, Func<AnimationRecord, AnimationRecord> change)
    {
        lock (gate) { if (records.TryGetValue(hash, out var item)) { records[hash] = change(item); Save(); } }
    }

    /// <summary>
    /// Serves a home's custom animations: unpacks each package it uses (off the framework thread) after checking it,
    /// renames each needed file for its carrier, and has Penumbra serve them. Returns the carriers whose content
    /// changed from something served earlier in this game session: characters must be redrawn to drop the old one.
    /// An empty list serves nothing (a home without custom animations, or none open). Carriers in standIns (animations
    /// this PC doesn't show: a guest who declined the home's custom content) play the game's own standing pose
    /// instead, so a partner on a ball is seen standing still rather than doing the carrier's cheer or exercise.
    /// </summary>
    /// The sounds the animations' timelines play (music for a dance) are served too when sounds is set; otherwise they
    /// stay silent.
    public int[] Prepare(IReadOnlyList<(string Package, int Animation, int Carrier)> animations, IReadOnlyCollection<int>? standIns = null, bool sounds = true,
        IReadOnlyCollection<int>? silent = null)
    {
        foreach (var package in animations.Select(a => a.Package).Distinct())
        {
            bool done; lock (gate) done = ready.ContainsKey(package);
            // A finished task still listed (its continuation ran before it was stored) doesn't count as working.
            if (done || working.TryGetValue(package, out var busy) && !busy.IsCompleted || !Has(package) || missingData.ContainsKey(package)) continue;
            if (unpackFailed.TryGetValue(package, out var failed) && Environment.TickCount64 < failed.RetryAt) continue;
            working[package] = Task.Run(() => Unpack(package)).ContinueWith(task =>
            {
                working.TryRemove(package, out _);
                if (!task.IsFaulted) { unpackFailed.TryRemove(package, out _); return; }
                var tries = unpackFailed.TryGetValue(package, out var before) ? before.Tries + 1 : 1;
                unpackFailed[package] = (Environment.TickCount64 + 60_000L * (1L << Math.Min(tries - 1, 6)), tries);
                log.Warning(task.Exception!.GetBaseException(), "[Haven] Custom animations could not be prepared (attempt {Tries}).", tries);
                if (tries == 1) Failures.Enqueue("Custom animations could not be prepared: " + task.Exception!.GetBaseException().Message);
            });
        }
        if (DateTimeOffset.UtcNow < nextRegistration) return [];
        // Nothing it is built from changed since the last frame: the map, and which animation each carrier plays, are as they were.
        var now = Volatile.Read(ref generation);
        if (now == preparedGeneration && sounds == preparedSounds && animations.SequenceEqual(preparedAnimations)
            && (standIns ?? []).SequenceEqual(preparedStandIns) && (silent ?? []).SequenceEqual(preparedSilent)) return [];
        // The carrier animation keys of silent balls: their files are served without the animation's sounds.
        silentKeys = (silent ?? []).Select(PoseCarriers.Get).OfType<PoseCarrier>().SelectMany(c => new[] { c.Loop, c.Intro }).ToHashSet(StringComparer.Ordinal);
        List<(string Path, string Key)> unfit = [];
        var wanted = PoseAnimations.Redirections(animations, Manifest, Renamed, Reads, Skeleton, unfit);
        // Paths no file of the animation fits (a body the mod has no version for): the game's own standing pose, so
        // a player there stands still instead of T-posing.
        foreach (var (path, key) in unfit) if (!wanted.ContainsKey(path) && StandInFile(key) is { } still) wanted[path] = still;
        if (sounds)
            foreach (var (path, disk) in PoseAnimations.SoundRedirections(animations.Select(a => a.Package), Manifest,
                (package, name) => File.Exists(Path.Combine(Folder(package), name)) ? Path.Combine(Folder(package), name) : null))
                wanted[path] = disk;
        // The emote timeline files some animations bring (effects their option starts there: the treadmill's screen).
        foreach (var (path, disk) in PoseAnimations.TimelineRedirections(animations, Manifest, RenamedTimeline)) wanted[path] = disk;
        // The effects their timelines start (a mod's own glowsticks, sparkles) and those effects' textures.
        foreach (var (path, disk) in PoseAnimations.EffectRedirections(animations.Select(a => a.Package), Manifest,
            (package, name) => File.Exists(Path.Combine(Folder(package), name)) ? Path.Combine(Folder(package), name) : null))
            wanted[path] = disk;
        foreach (var carrier in (standIns ?? []).Select(PoseCarriers.Get).OfType<PoseCarrier>())
            foreach (var key in new[] { carrier.Loop, carrier.Intro })
                if (StandInFile(key) is { } disk)
                    foreach (var race in PoseCarriers.Races) wanted[PoseCarriers.PapPath(race, key)] = disk;
        // The same files as now: nothing to register, but which animation each carrier plays may still have changed (a
        // ball's animation swapped for another made of the same files, as Warrior of Lift's curls listed twice). Not
        // noting it left that ball "Preparing the animation…" for good.
        if (wanted.Count == served.Count && wanted.All(w => served.TryGetValue(w.Key, out var s) && s == w.Value)) { Note(animations, standIns, silent); Prepared(now, animations, standIns, silent, sounds); return []; }
        if (!Register(wanted)) return [];
        served = wanted;
        Prepared(now, animations, standIns, silent, sounds);
        return Note(animations, standIns, silent);
    }
    private void Prepared(int at, IReadOnlyList<(string Package, int Animation, int Carrier)> animations, IReadOnlyCollection<int>? standIns, IReadOnlyCollection<int>? silent, bool sounds)
    {
        preparedGeneration = at; preparedSounds = sounds;
        preparedAnimations = [.. animations]; preparedStandIns = [.. standIns ?? []]; preparedSilent = [.. silent ?? []];
    }

    // Notes which animation (or the stand-in) each carrier now plays; returns the carriers whose content changed.
    private int[] Note(IReadOnlyList<(string Package, int Animation, int Carrier)> animations, IReadOnlyCollection<int>? standIns, IReadOnlyCollection<int>? silent)
    {
        List<int> changed = [];
        foreach (var (package, animation, carrier) in animations)
        {
            if (Manifest(package) == null) continue;
            var content = $"{package}:{animation}" + (silent?.Contains(carrier) == true ? ":silent" : "");
            if (carrierContent.TryGetValue(carrier, out var before) && before != content) changed.Add(carrier);
            carrierContent[carrier] = content;
        }
        foreach (var carrier in standIns ?? [])
        {
            if (carrierContent.TryGetValue(carrier, out var before) && before != StandIn) changed.Add(carrier);
            carrierContent[carrier] = StandIn;
        }
        return [.. changed.Distinct()];
    }

    /// <summary>Whether a carrier serves this animation now.</summary>
    public bool Serving(string package, int animation, int carrier, bool silent = false) =>
        carrierContent.TryGetValue(carrier, out var content) && content == $"{package}:{animation}" + (silent ? ":silent" : "")
        && PoseCarriers.Get(carrier) is { } c && served.ContainsKey(PoseCarriers.PapPath("c0101", c.Loop));

    /// <summary>Stops serving every animation (the home closed), so each carrier emote is its usual self again.</summary>
    public void Clear()
    {
        Changed();
        if (served.Count == 0) return;
        Unregister();
        served = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, object> unpacking = new();
    // The editor (reading a package to choose its animations) and Prepare can ask at once: one unpacks, the other waits.
    private void Unpack(string hash)
    {
        lock (unpacking.GetOrAdd(hash, _ => new object()))
        {
            if (Manifest(hash) != null) return;
            UnpackNow(hash);
        }
    }
    private void UnpackNow(string hash)
    {
        var package = PoseAnimations.Read(File.ReadAllBytes(PackageFile(hash)));
        // A game file it points at without carrying it (an effect's texture, a sound), from an expansion this game lacks
        // (a free trial), would close the game when played: the package isn't served here, and its balls stand still.
        if (PackageNeeds.Missing(package.Files.Select(f => (f.Key, f.Value)), data.FileExists) is { } needs)
        {
            log.Warning("[Haven] The custom animations {Name} aren't played: they need {Path}, which this game doesn't have.", package.Manifest.Name, needs.Path);
            missingData[hash] = 0;
            throw new InvalidDataException(PackageNeeds.Message($"The animations \"{package.Manifest.Name}\"", needs.Expansion));
        }
        var folder = Folder(hash);
        Directory.CreateDirectory(folder);
        foreach (var (name, bytes) in package.Files)
        {
            var file = Path.Combine(folder, name);
            if (File.Exists(file) && new FileInfo(file).Length == bytes.Length) continue;
            var temporary = file + ".tmp"; File.WriteAllBytes(temporary, bytes); File.Move(temporary, file, overwrite: true);
        }
        lock (gate) ready[hash] = package.Manifest;
        Changed();
    }

    // A package's file renamed for a carrier key's animation, written beside it once; null when the package isn't
    // unpacked yet, the game has no animation at that key to stand in for, or the copy is still being written. Writing
    // one reads and rewrites a whole animation file (a dance's is 10 MB, and a package may hold many for many carriers),
    // so it runs off the framework thread; the map is rebuilt once it's written (Changed). Keyed "package|what|key".
    private readonly ConcurrentDictionary<string, Task<string?>> renamed = new();
    private string? Made(string id, Func<string?> make)
    {
        var task = renamed.GetOrAdd(id, _ => Task.Run(() => { var made = make(); if (made != null) Changed(); return made; }));
        return task.IsCompletedSuccessfully ? task.Result : null;
    }
    // The game's animation load table (whose copy of each animation each body reads), read once.
    private PapLoadTable? loadTable;
    private bool loadTableRead;
    /// <summary>Whose path a body reads a timeline key's animation from (papLoadTable), or null when the table doesn't say.</summary>
    public string? Reads(string race, string key)
    {
        if (!loadTableRead)
        {
            loadTableRead = true;
            try { loadTable = data.GetFile(PapLoadTable.GamePath)?.Data is { } bytes ? PapLoadTable.Parse(bytes) : null; }
            catch (Exception ex) { log.Warning(ex, "[Haven] The game's animation load table could not be read."); }
            if (loadTable == null) log.Warning("[Haven] The game's animation load table is unreadable; custom animations are served at every body's own path.");
        }
        return loadTable?.Reads(race, key);
    }

    // The body each unpacked file was made for, from its header (model ID at 0x0A), read once per file.
    private readonly ConcurrentDictionary<(string, string), string> skeletons = new();
    /// <summary>The body (skeleton) one of a package's files was made for, or null until it is unpacked.</summary>
    public string? Skeleton(string package, string file)
    {
        if (skeletons.TryGetValue((package, file), out var known)) return known;
        var path = Path.Combine(Folder(package), file);
        try
        {
            if (!File.Exists(path)) return null;
            Span<byte> header = stackalloc byte[0x0C];
            using (var stream = File.OpenRead(path)) if (stream.Read(header) < header.Length) return null;
            return skeletons[(package, file)] = $"c{System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[0x0A..]):0000}";
        }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// How well a carrier suits one of a package's animations: how many of the eighteen bodies would draw it on bones
    /// it was made for (PoseAnimations.Fit). The player's own packages are read for this when first asked.
    /// </summary>
    public int CarrierFit(string package, int animation, int carrier)
    {
        PoseAnimationManifest? manifest;
        try { manifest = Item(package) != null ? Load(package) : Manifest(package); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { return 0; }
        if (manifest == null || animation < 0 || animation >= manifest.Animations.Length || PoseCarriers.Get(carrier) is not { } c) return 0;
        // Bodies fitted first; between carriers that fit as many, the one sharing more bodies' copies, so carriers
        // where every body reads its own stay free for animations a mod made per body.
        var distinct = PoseCarriers.Races.Select(r => Reads(r, c.Loop) ?? r).Distinct().Count();
        return PoseAnimations.Fit(manifest.Animations[animation], c, Reads, f => Skeleton(package, f)) * 32 + (32 - distinct);
    }

    /// <summary>Whether a body would draw one of a package's animations at a carrier on bones it was made for (else it plays the standing pose).</summary>
    public bool Fits(string package, int animation, int carrier, string body) =>
        Manifest(package) is { } manifest && animation >= 0 && animation < manifest.Animations.Length && PoseCarriers.Get(carrier) is { } c
        && PoseAnimations.FileFor(manifest.Animations[animation], Reads(body, c.Loop) ?? body, f => Skeleton(package, f)) != null;

    private HashSet<string> silentKeys = [];
    private string? Renamed(string package, string file, string key)
    {
        var silent = silentKeys.Contains(key);
        return Made($"{package}|{file}|{key}{(silent ? ":silent" : "")}", () => Rename(package, file, key, silent));
    }
    // A silent copy points its timelines' sounds at a path nothing serves, so they play nothing.
    private const string NoSound = "sound/haven/silent.scd";
    private string? Rename(string package, string file, string key, bool silent)
    {
        if (CarrierNames(key) is not { Count: > 0 } names || Manifest(package) == null) return null;
        var source = Path.Combine(Folder(package), file);
        var target = Path.Combine(Folder(package), "served", Path.GetFileNameWithoutExtension(file) + "." + string.Join("+", names) + (silent ? ".silent" : "") + ".pap");
        if (File.Exists(target)) return target;
        if (!File.Exists(source)) return null;
        try
        {
            var bytes = PapFiles.Rename(File.ReadAllBytes(source), names);
            if (silent && PapFiles.Sounds(bytes).Count > 0) bytes = PapFiles.RepointSounds(bytes, _ => NoSound);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".tmp"; File.WriteAllBytes(temporary, bytes); File.Move(temporary, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            log.Warning(ex, "[Haven] A custom animation could not be prepared for its emote.");
            return null;
        }
    }

    // An animation's emote timeline file made for a carrier key: the animation it plays (named as the package's files
    // name it) becomes the carrier's, and a silent ball's sounds go nowhere. Written beside the package once.
    private string? RenamedTimeline(string package, int index, string key)
    {
        var silent = silentKeys.Contains(key);
        return Made($"{package}|timeline {index}|{key}{(silent ? ":silent" : "")}", () => MakeTimeline(package, index, key, silent));
    }
    private string? MakeTimeline(string package, int index, string key, bool silent)
    {
        if (Manifest(package) is not { } manifest || index < 0 || index >= manifest.Animations.Length || manifest.Animations[index].Timeline is not { } file
            || CarrierNames(key) is not { Count: > 0 } names) return null;
        var source = Path.Combine(Folder(package), file);
        var target = Path.Combine(Folder(package), "served", $"{Path.GetFileNameWithoutExtension(file)}.{names[0]}{(silent ? ".silent" : "")}.tmb");
        if (File.Exists(target)) return target;
        if (!File.Exists(source)) return null;
        try
        {
            var own = manifest.Animations[index].Races.Values.Distinct().Select(f => Path.Combine(Folder(package), f)).Where(File.Exists)
                .SelectMany(f => PapFiles.Names(File.ReadAllBytes(f))).ToHashSet(StringComparer.Ordinal);
            var bytes = PapFiles.RewriteTimeline(File.ReadAllBytes(source), (tag, value) => tag == "C010" && own.Contains(value) ? names[0] : tag == "C063" && silent ? NoSound : null);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".tmp"; File.WriteAllBytes(temporary, bytes); File.Move(temporary, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            log.Warning(ex, "[Haven] A custom animation's timeline could not be prepared for its emote.");
            return null;
        }
    }

    // The game's own standing pose (the second /cpose), renamed for a carrier key: what a PC that doesn't show an animation plays.
    private const string StandIn = "emote/pose01_loop";
    private string? StandInFile(string key)
    {
        if (CarrierNames(key) is not { Count: > 0 } names) return null;
        var target = Path.Combine(unpacked, "standin", string.Join("+", names) + ".pap");
        if (File.Exists(target)) return target;
        try
        {
            if (data.GetFile(PoseCarriers.PapPath("c0101", StandIn))?.Data is not { } bytes) return null;
            var renamed = PapFiles.Rename(bytes, names);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".tmp"; File.WriteAllBytes(temporary, renamed); File.Move(temporary, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            log.Warning(ex, "[Haven] The standing pose could not be prepared for a carrier emote.");
            return null;
        }
    }

    // The names the game's own animation at a carrier key declares, read once from the installed game (they are the
    // same for every race; the base Hyur Midlander file is the one every race falls back to).
    private IReadOnlyList<string>? CarrierNames(string key)
    {
        lock (gate)
        {
            if (carrierNames.TryGetValue(key, out var known)) return known;
            IReadOnlyList<string>? names = null;
            var path = PoseCarriers.PapPath("c0101", key);
            try { if (data.FileExists(path) && data.GetFile(path)?.Data is { } bytes) names = PapFiles.Names(bytes); }
            catch (InvalidDataException ex) { log.Warning(ex, "[Haven] The game's animation at {Path} could not be read.", path); }
            return carrierNames[key] = names;
        }
    }

    private bool Register(Dictionary<string, string> paths)
    {
        try
        {
            if (paths.Count == 0) { Unregister(); return true; }
            var result = plugin.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5").InvokeFunc(Tag, paths, "", Priority);
            if (result is not (0 or 1)) throw new InvalidOperationException($"Penumbra refused the custom animations (code {result}).");
            PenumbraMissing = false;
            return true;
        }
        catch (Exception ex) when (ex is Dalamud.Plugin.Ipc.Exceptions.IpcError or InvalidOperationException)
        {
            if (!PenumbraMissing) { log.Warning(ex, "[Haven] Penumbra is unavailable for custom animations."); Failures.Enqueue("Custom animations need Penumbra. Pose balls with them play nothing until Penumbra is running."); }
            PenumbraMissing = true;
            nextRegistration = DateTimeOffset.UtcNow.AddSeconds(5);
            return false;
        }
    }

    private void Unregister()
    {
        try { plugin.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5").InvokeFunc(Tag, Priority); }
        catch (Exception ex) when (ex is Dalamud.Plugin.Ipc.Exceptions.IpcError) { }
    }

    // The locker: animation packages travel as the furniture kind (a zip, the same limits and expiry) -----------

    /// <summary>Puts a package on the locker, unless it is already there, and notes when it was stored.</summary>
    public async Task Upload(AskTicket ask, string origin, string hash, CancellationToken token)
    {
        var zip = await File.ReadAllBytesAsync(PackageFile(hash), token);
        await LockerUpload.Send(http, ask, origin, hash, zip, KeepUsage, Describe, token);
        Change(hash, f => f with { Stored = DateTimeOffset.UtcNow });
    }

    /// <summary>Deletes a package from Haven's server at once (only the character that uploaded it can).</summary>
    public async Task DeleteFromServer(AskTicket ask, string origin, string hash, CancellationToken token)
    {
        var ticket = await ask(new("delete", hash, Media: ContentLocker.FurnitureMedia), token);
        if (ticket.Error != null || ticket.Path == null) throw new InvalidOperationException(ticket.Error ?? "The relay gave no ticket.");
        using var response = await http.PostAsync(ticket.Url(origin, hash), null, token);
        if ((int)response.StatusCode == 403) throw new NotUploaderException("Only the character that uploaded them can delete them from Haven's server.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await Describe(response));
        KeepUsage?.Invoke(response);
    }

    /// <summary>Fetches a package a home uses through that gathering's relay (a guest who agreed to its custom content).</summary>
    public async Task Download(RelaySyncService relay, string hash)
    {
        using var transfer = ContentTransfers.Begin(ContentKind.Animations, hash);
        var ticket = await relay.RequestContentTicket(new("download", hash, Media: ContentLocker.FurnitureMedia), CancellationToken.None);
        if (ticket.Error == ContentLocker.NotInHome) throw new PictureNotReadyException(ticket.Error);
        if (ticket.Error != null || ticket.Path == null || relay.ContentOrigin is not {} origin) throw new InvalidOperationException(ticket.Error ?? "The relay's address is unknown.");
        using var response = await http.GetAsync(ticket.Url(origin, hash), HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new PictureNotReadyException("Those animations are not shared yet.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await Describe(response));
        Store(hash, await transfer.Read(response, ContentLocker.MaxFurnitureBytes));
    }

    private static async Task<string> Describe(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is 403 or 409 or 429 or 503 && response.Content.Headers.ContentType?.MediaType is null or "text/plain")
        {
            var text = (await response.Content.ReadAsStringAsync()).Trim();
            if (text.Length is > 0 and <= 200 && text.All(c => !char.IsControl(c))) return text;
        }
        return (int)response.StatusCode switch
        {
            403 => "The custom animation link expired. It will be retried.",
            410 => "Those animations were removed by Haven's operator.",
            415 => "Haven's server accepts only Haven's own packages.",
            429 => "Too many custom content requests. Try again later.",
            503 => "Haven's content storage is busy. Try again later.",
            var code => $"Haven's server could not transfer the animations ({code}).",
        };
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(recordsPath)!);
        var temporary = recordsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(records));
        File.Move(temporary, recordsPath, overwrite: true);
    }

    public void Dispose()
    {
        http.Dispose();
        Unregister();
    }
}
