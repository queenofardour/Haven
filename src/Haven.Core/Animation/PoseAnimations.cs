using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Haven.Core.Furniture;

namespace Haven.Core.Animation;

/// <summary>
/// One animation of a package: its name, what it replaced in its mod (for the player), and the file for each race
/// the mod gave it (race code → file name in the package). Races it lacks borrow the nearest (PoseCarriers.Nearest).
/// </summary>
public sealed record PoseAnimation(string Name, string Source, Dictionary<string, string> Races)
{
    /// <summary>(Format 5) the animation's start, played once before it loops (race code → file name): where a mod's props pop in.</summary>
    public Dictionary<string, string>? Intro { get; init; }
    /// <summary>
    /// (Format 6) the emote timeline file its mod option gives (a file name), when it starts effects or sounds of its
    /// own: Warrior of Lift's treadmill screen. Served at the carrier's own timeline path, naming the carrier's animation.
    /// </summary>
    public string? Timeline { get; init; }
}

/// <summary>
/// An animation package's manifest: a name, its animations in order (PoseRole.Animation indexes them), (format 2)
/// the sound files their timelines play, each served at sound/haven/&lt;file&gt;, (format 3) the props its
/// animations use as gear (PoseProps), and (format 4) the effect files their timelines start and the textures those
/// use, each served at vfx/haven/&lt;file&gt;.
/// </summary>
public sealed record PoseAnimationManifest(int Format, string Name, PoseAnimation[] Animations)
{
    public string[]? Sounds { get; init; }
    public PoseProp[]? Props { get; init; }
    public string[]? Effects { get; init; }
}

/// <summary>A package in hand: its manifest and its files (file name → bytes).</summary>
public sealed record PoseAnimationPackage(PoseAnimationManifest Manifest, IReadOnlyDictionary<string, byte[]> Files);

/// <summary>
/// An animation a mod offers: the emote animation (timeline key) it replaces, its file per race (the mod's own file
/// or a file it swaps in), and the option it comes from when the mod offers it in more than one way (a variant).
/// </summary>
public sealed record PoseAnimationCandidate(string Key, string Label, IReadOnlyDictionary<string, ModFile> Races, string Option = "")
{
    /// <summary>The mod's start for this loop (the same emote's _start), played once before it: Warrior of Lift's bench press sits down and its bench pops in.</summary>
    public IReadOnlyDictionary<string, ModFile>? Intro { get; init; }
    /// <summary>The emote's own timeline file the option gives (chara/action/&lt;key&gt;.tmb), which names the animation to play when the file holds several.</summary>
    public ModFile? Action { get; init; }
    public string FileName => Key[(Key.LastIndexOf('/') + 1)..];
}

/// <summary>
/// Animation packages: the human animations (.pap) a Penumbra animation mod replaces, chosen by
/// the player, packed as a zip with a manifest. Animations travel to guests through the content locker like a
/// custom furnishing's package (the locker's furniture kind: a zip, up to the player's 250 MB allowance, the same expiry), and every PC checks
/// each file before Penumbra serves it.
/// </summary>
public static class PoseAnimations
{
    /// <summary>
    /// The newest format: 6 when a package carries an emote timeline file, 5 an animation's start, 4 effects, 3 a prop,
    /// 2 otherwise (so earlier Havens still read what they can).
    /// </summary>
    public const int Format = 6;
    private static readonly Regex TimelineName = new(@"^t[0-9a-f]{1,2}\.tmb$", RegexOptions.CultureInvariant);
    /// <summary>Where the game reads an emote's own timeline for an animation key.</summary>
    public static string TimelinePath(string key) => $"chara/action/{key}.tmb";
    /// <summary>Where a package's effects and their textures are served: vfx/haven/&lt;first 16 hex digits of the file's SHA-256&gt;.avfx or .atex.</summary>
    public const string EffectRoot = "vfx/haven/";
    private static readonly Regex EffectName = new(@"^[0-9a-f]{16}\.(avfx|atex)$", RegexOptions.CultureInvariant);
    private const int MaxEffects = 64;
    /// <summary>Where a package's sounds are served: sound/haven/&lt;first 16 hex digits of the file's SHA-256&gt;.scd.</summary>
    public const string SoundRoot = "sound/haven/";
    public const int MaxSoundBytes = 16 * 1024 * 1024;
    private static readonly Regex SoundName = new(@"^[0-9a-f]{16}\.scd$", RegexOptions.CultureInvariant);
    public const int MaxBytes = FurniturePackages.MaxBytes;
    public const long MaxUnpackedBytes = 1024L * 1024 * 1024;
    private const int MaxFiles = 128;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly Regex HumanAnimation = new(@"^chara/human/(c\d{4})/animation/a0001/bt_common/([a-z0-9_/]+)\.pap$", RegexOptions.CultureInvariant);
    private static readonly Regex FileName = new(@"^p[0-9a-f]{1,3}\.pap$", RegexOptions.CultureInvariant);
    private static readonly Regex ActionTimeline = new(@"^chara/action/([a-z0-9_/]+)\.tmb$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The options a package's other files (effects, props) are read with: a group that changes animations with every
    /// option (each animation already knows its own files), any other group as the mod sets it by default. Warrior of
    /// Lift's "Solid Color" option, off by default, swaps its colour-shifting equipment for plain ones.
    /// </summary>
    public static ModSelection EffectSelection(PenumbraMod mod)
    {
        var picks = new Dictionary<int, ulong>();
        for (var g = 0; g < mod.Groups.Count; g++)
        {
            var group = mod.Groups[g];
            var animated = group.Options.Any(o => o.Files.Keys.Any(k => HumanAnimation.IsMatch(k) || ActionTimeline.IsMatch(k)));
            picks[g] = animated && !group.Single ? ulong.MaxValue : group.DefaultSettings;
        }
        return new(picks);
    }

    /// <summary>
    /// The animations a mod changes: every human emote animation it replaces, grouped by the animation it replaces
    /// (one per race at most). Races outside the playable eighteen (children, NPC bodies) are left out.
    /// </summary>
    /// Each option of a group that changes animations is its own candidate (a "Leg Lock" and a "Normal" option that
    /// replace the same animation are two animations to keep); files outside every group are candidates of their own.
    public static IReadOnlyList<PoseAnimationCandidate> Candidates(PenumbraMod mod, Func<string, string?>? label = null)
    {
        List<PoseAnimationCandidate> found = [];
        void Gather(IEnumerable<KeyValuePair<string, ModFile>> files, string option)
        {
            var byKey = new SortedDictionary<string, Dictionary<string, ModFile>>(StringComparer.Ordinal);
            var actions = new Dictionary<string, ModFile>(StringComparer.Ordinal);
            foreach (var (path, file) in files)
            {
                if (ActionTimeline.Match(path) is { Success: true } action) { actions[action.Groups[1].Value] = file; continue; }
                var match = HumanAnimation.Match(path);
                if (!match.Success || !PoseCarriers.Races.Contains(match.Groups[1].Value) || Movement(match.Groups[2].Value)) continue;
                if (!byKey.TryGetValue(match.Groups[2].Value, out var races)) byKey[match.Groups[2].Value] = races = [];
                races[match.Groups[1].Value] = file;
            }
            // An emote's start and loop (…_start, …_loop) are one animation: the start plays once, then the loop.
            foreach (var (key, races) in byKey)
            {
                if (key.EndsWith("_start", StringComparison.Ordinal) && byKey.ContainsKey(key[..^"_start".Length] + "_loop")) continue;
                var intro = key.EndsWith("_loop", StringComparison.Ordinal) && byKey.TryGetValue(key[..^"_loop".Length] + "_start", out var start) ? start : null;
                found.Add(new(key, label?.Invoke(key) ?? key, races, option) { Intro = intro, Action = actions.GetValueOrDefault(key) });
            }
        }
        var inGroups = mod.Groups.SelectMany(g => g.Options).SelectMany(o => o.Files.Keys).ToHashSet(StringComparer.Ordinal);
        mod.SelectAll();
        Gather(mod.Files.Where(f => !inGroups.Contains(f.Key)), "");
        foreach (var group in mod.Groups)
            foreach (var option in group.Options)
                Gather(option.Files, group.Options.Count(o => o.Files.Keys.Any(k => HumanAnimation.IsMatch(k))) > 1 ? option.Name : "");
        // The same animation offered twice (Warrior of Lift lists its curls under Curls and under Treadmill) is listed once.
        static string Same(IReadOnlyDictionary<string, ModFile>? races) => races == null ? "" : string.Join(";", races.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}={r.Value.Entry}|{r.Value.SwapTo}"));
        return [.. found.DistinctBy(c => $"{c.Key}\n{Same(c.Races)}\n{Same(c.Intro)}\n{c.Action?.Entry}|{c.Action?.SwapTo}")];
    }

    // Walking, running, jumping and standing (resident/move_a, resident/idle …): files of several movements the game
    // picks between as the player moves. A ball plays one looping animation, so these aren't offered.
    private static bool Movement(string key) => key.StartsWith("resident/", StringComparison.Ordinal);

    /// <summary>Whether a mod changes only movement or standing animations (walking, idling), which balls can't play.</summary>
    public static bool MovementOnly(PenumbraMod mod)
    {
        mod.SelectAll();
        var keys = mod.Files.Keys.Select(k => HumanAnimation.Match(k)).Where(m => m.Success).Select(m => m.Groups[2].Value).ToArray();
        return keys.Length > 0 && keys.All(Movement);
    }

    /// <summary>
    /// Builds a package from chosen animations of a mod (the mod's options already selected), each under a name the
    /// player gave. Identical files are stored once. Returns the zip and its manifest.
    /// </summary>
    public static (byte[] Zip, PoseAnimationManifest Manifest) Build(PenumbraMod mod, string name, IReadOnlyList<(PoseAnimationCandidate Candidate, string Name)> chosen, Func<string, byte[]?> game,
        IReadOnlyList<PosePropCandidate>? props = null)
    {
        if (chosen.Count is 0 or > PoseSets.MaxAnimations) throw new InvalidDataException($"Choose between 1 and {PoseSets.MaxAnimations} animations.");
        mod.Select(EffectSelection(mod));
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var animations = new List<PoseAnimation>();
        var sounds = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        // A sound the mod supplies (music for a dance) travels with the animation, at a path of Haven's own so it
        // replaces nothing; a game sound the timeline names stays as it is.
        string? Sound(string path)
        {
            if (PenumbraMod.GamePath(path) is not { } key || !mod.Files.ContainsKey(key) || !key.EndsWith(".scd", StringComparison.Ordinal)) return null;
            var bytes = Resolve(mod, key, game);
            if (bytes == null) return null;
            bytes = MendSound(bytes);
            CheckSound(bytes);
            var name = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + ".scd";
            sounds[name] = bytes;
            return SoundRoot + name;
        }
        // An effect the mod supplies, or whose textures it supplies, travels with the animation at a path of Haven's own,
        // its textures likewise (an effect's models are inside it); the game's own effects stay as they are.
        var effects = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        string? Stored(byte[] bytes, string extension)
        {
            var name = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + extension;
            if (!effects.ContainsKey(name) && effects.Count >= MaxEffects) throw new InvalidDataException("The animations start too many effects.");
            effects[name] = bytes;
            return EffectRoot + name;
        }
        string? Effect(string path)
        {
            if (PenumbraMod.GamePath(path) is not { } key || !key.EndsWith(".avfx", StringComparison.Ordinal)) return null;
            var bytes = Resolve(mod, key, game);
            if (bytes == null) return null;
            AvfxFiles.Check(bytes);
            var textures = AvfxFiles.Textures(bytes).Select(t => PenumbraMod.GamePath(t)).OfType<string>().Where(t => mod.Files.ContainsKey(t)).ToHashSet(StringComparer.Ordinal);
            if (!mod.Files.ContainsKey(key) && textures.Count == 0) return null;
            if (textures.Count > 0)
                bytes = AvfxFiles.RepointTextures(bytes, texture =>
                {
                    if (PenumbraMod.GamePath(texture) is not { } t || !textures.Contains(t) || Resolve(mod, t, game) is not { } data) return null;
                    AvfxFiles.CheckTexture(data);
                    return Stored(data, ".atex");
                });
            return Stored(bytes, ".avfx");
        }
        var intros = false;
        var timelines = 0;
        foreach (var (candidate, title) in chosen)
        {
            // A file of several animations plays the one the option's own timeline names (the treadmill's walk or sprint).
            var tmb = candidate.Action is { } action ? action.Entry != null ? mod.Read(action.Entry) : Resolve(mod, action.SwapTo!, game) : null;
            var only = tmb != null ? PapFiles.TimelineAnimations(tmb).FirstOrDefault() : null;
            // That timeline travels too when it starts effects or sounds of its own (the treadmill's screen), pointed at Haven's copies.
            string? timeline = null;
            if (tmb != null && only != null && PapFiles.TimelineEntries(tmb).Any(e => e.Tag is "C012" or "C173" or "C063"))
            {
                if (files.Count >= MaxFiles) throw new InvalidDataException("The animations hold too many files.");
                timeline = $"t{timelines++:x}.tmb";
                files[timeline] = PapFiles.RewriteTimeline(tmb, (tag, value) => tag is "C012" or "C173" ? Effect(value) : tag == "C063" ? Sound(value) : null);
            }
            Dictionary<string, string> Files(IReadOnlyDictionary<string, ModFile> sources, string? pick)
            {
                var races = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (race, source) in sources.OrderBy(r => r.Key, StringComparer.Ordinal))
                {
                    var bytes = (source.Entry != null ? mod.Read(source.Entry) : Resolve(mod, source.SwapTo!, game))
                        ?? throw new InvalidDataException($"The mod is missing the {race} animation for {candidate.FileName}.");
                    Check(bytes);
                    if (pick != null && PapFiles.Names(bytes) is { Count: > 1 } names && names.Contains(pick)) bytes = PapFiles.Only(bytes, pick);
                    if (PapFiles.Sounds(bytes).Count > 0) bytes = PapFiles.RepointSounds(bytes, Sound);
                    if (PapFiles.Effects(bytes).Count > 0) bytes = PapFiles.RepointEffects(bytes, Effect);
                    var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    if (!byHash.TryGetValue(hash, out var file))
                    {
                        file = $"p{files.Count:x}.pap";
                        if (files.Count >= MaxFiles) throw new InvalidDataException("The animations hold too many files.");
                        files[file] = bytes; byHash[hash] = file;
                    }
                    races[race] = file;
                }
                return races;
            }
            var races = Files(candidate.Races, only);
            if (races.Count == 0) throw new InvalidDataException($"{candidate.FileName} has no animation Haven can use.");
            var intro = candidate.Intro is { Count: > 0 } start ? Files(start, null) : null;
            intros |= intro != null;
            animations.Add(new(Clean(title, candidate.FileName), Clean(candidate.Label, candidate.Key), races) { Intro = intro, Timeline = timeline });
        }
        // The props the player kept: each piece of gear's files (as the game would load them with the mod on) and its
        // own manipulations, identical files stored once.
        var kept = new List<PoseProp>();
        var propFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in (props ?? []).Take(PoseProps.MaxProps))
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in prop.Paths.Where(p => PoseProps.ValidPath(prop.Slot, prop.Set, p)))
            {
                var bytes = Resolve(mod, path, game) ?? throw new InvalidDataException($"The mod is missing its prop file {path}.");
                PoseProps.CheckFile(path, bytes);
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)) + Path.GetExtension(path);
                if (!propFiles.TryGetValue(hash, out var file))
                {
                    if (files.Count + propFiles.Count >= MaxFiles) throw new InvalidDataException("The animations hold too many files.");
                    file = PoseProps.NameFor(propFiles.Count, path);
                    propFiles[hash] = file;
                    files[file] = bytes;
                }
                paths[path] = file;
            }
            if (paths.Count > 0) kept.Add(new(prop.Slot, prop.Set, paths, PoseProps.MetaFor(mod, prop.Slot, prop.Set)));
        }
        var manifest = new PoseAnimationManifest(timelines > 0 ? 6 : intros ? 5 : effects.Count > 0 ? 4 : kept.Count > 0 ? 3 : 2, Clean(name, "Animations"), [.. animations])
            { Sounds = sounds.Count == 0 ? null : [.. sounds.Keys], Props = kept.Count == 0 ? null : [.. kept], Effects = effects.Count == 0 ? null : [.. effects.Keys] };
        foreach (var (file, bytes) in sounds) files[file] = bytes;
        foreach (var (file, bytes) in effects) files[file] = bytes;
        var zip = Zip(manifest, files);
        if (zip.Length > MaxBytes) throw new InvalidDataException($"These animations come to {zip.Length / 1e6:0.0} MB, more than the {MaxBytes / 1_000_000} MB a package may be.");
        return (zip, manifest);
    }

    // What the game would load at a path with the mod on: the mod's file, or a file it swaps in (the mod's or the game's).
    private static byte[]? Resolve(PenumbraMod mod, string path, Func<string, byte[]?> game)
    {
        for (var depth = 0; depth < 4; depth++)
        {
            if (!mod.Files.TryGetValue(path, out var file)) return game(path);
            if (file.Entry != null) return mod.Read(file.Entry);
            path = file.SwapTo!;
        }
        return null;
    }

    private static string Clean(string? text, string fallback) => SafeText.Clean(text, 40) is { Length: > 0 } clean ? clean : SafeText.Clean(fallback, 40);

    // A deterministic zip: the manifest, then the files in name order, all with one fixed time.
    private static byte[] Zip(PoseAnimationManifest manifest, SortedDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string entryName, byte[] bytes)
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.SmallestSize);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open(); stream.Write(bytes);
            }
            Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
            foreach (var (file, bytes) in files) Add(file, bytes);
        }
        return output.ToArray();
    }

    /// <summary>Reads a package from anyone, checking its manifest and every file.</summary>
    public static PoseAnimationPackage Read(byte[] package)
    {
        if (package.Length > MaxBytes) throw new InvalidDataException("An animation package is larger than Haven accepts.");
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxFiles + 1) throw new InvalidDataException("An animation package has too many files.");
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("This is not a Haven animation package.");
        PoseAnimationManifest manifest;
        try { manifest = JsonSerializer.Deserialize<PoseAnimationManifest>(Bytes(manifestEntry, 256 * 1024), Json) ?? throw new InvalidDataException("An animation package's manifest is empty."); }
        catch (JsonException) { throw new InvalidDataException("An animation package's manifest could not be read."); }
        if (manifest.Format is not (1 or 2 or 3 or 4 or 5 or Format)) throw new InvalidDataException("This animation package needs a newer Haven.");
        if (manifest.Animations is not { Length: > 0 and <= PoseSets.MaxAnimations }) throw new InvalidDataException("An animation package lists no animations.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var animation in manifest.Animations)
        {
            if (animation?.Races is not { Count: > 0 } races || races.Keys.Any(r => !PoseCarriers.Races.Contains(r))
                || animation.Intro is { } start && (manifest.Format < 5 || start.Count == 0 || start.Keys.Any(r => !PoseCarriers.Races.Contains(r))))
                throw new InvalidDataException("An animation package's races are not listed properly.");
            if (animation.Timeline is { } timeline)
            {
                if (manifest.Format < 6 || !TimelineName.IsMatch(timeline)) throw new InvalidDataException("An animation package names a timeline badly.");
                if (!files.ContainsKey(timeline))
                {
                    var entry = zip.GetEntry(timeline) ?? throw new InvalidDataException("An animation package is missing a timeline.");
                    if ((total += entry.Length) > MaxUnpackedBytes) throw new InvalidDataException("An animation package unpacks too large.");
                    var bytes = Bytes(entry, 64 * 1024);
                    PapFiles.CheckTimeline(bytes);
                    files[timeline] = bytes;
                }
            }
            foreach (var file in races.Values.Concat(animation.Intro?.Values ?? Enumerable.Empty<string>()))
            {
                if (file == null || !FileName.IsMatch(file)) throw new InvalidDataException("An animation package names a file badly.");
                if (files.ContainsKey(file)) continue;
                var entry = zip.GetEntry(file) ?? throw new InvalidDataException("An animation package is missing a file.");
                if ((total += entry.Length) > MaxUnpackedBytes) throw new InvalidDataException("An animation package unpacks too large.");
                var bytes = Bytes(entry, MaxUnpackedBytes);
                Check(bytes);
                files[file] = bytes;
            }
        }
        foreach (var sound in manifest.Sounds ?? [])
        {
            if (sound == null || !SoundName.IsMatch(sound) || files.ContainsKey(sound) || manifest.Format < 2) throw new InvalidDataException("An animation package names a sound badly.");
            var entry = zip.GetEntry(sound) ?? throw new InvalidDataException("An animation package is missing a sound.");
            if ((total += entry.Length) > MaxUnpackedBytes) throw new InvalidDataException("An animation package unpacks too large.");
            var bytes = Bytes(entry, MaxSoundBytes);
            CheckSound(bytes);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + ".scd" != sound) throw new InvalidDataException("An animation package's sound doesn't match its name.");
            files[sound] = bytes;
        }
        if (manifest.Effects is { } effectNames)
        {
            if (manifest.Format < 4 || effectNames.Length is 0 or > MaxEffects) throw new InvalidDataException("An animation package lists its effects badly.");
            foreach (var effect in effectNames)
            {
                if (effect == null || !EffectName.IsMatch(effect) || files.ContainsKey(effect)) throw new InvalidDataException("An animation package names an effect badly.");
                var entry = zip.GetEntry(effect) ?? throw new InvalidDataException("An animation package is missing an effect.");
                if ((total += entry.Length) > MaxUnpackedBytes) throw new InvalidDataException("An animation package unpacks too large.");
                var bytes = Bytes(entry, AvfxFiles.MaxBytes);
                if (effect.EndsWith(".avfx", StringComparison.Ordinal)) AvfxFiles.Check(bytes); else AvfxFiles.CheckTexture(bytes);
                if (Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + Path.GetExtension(effect) != effect) throw new InvalidDataException("An animation package's effect doesn't match its name.");
                files[effect] = bytes;
            }
        }
        if (manifest.Props is { } props)
        {
            if (manifest.Format < 3 || props.Length is 0 or > PoseProps.MaxProps) throw new InvalidDataException("An animation package lists its props badly.");
            foreach (var prop in props)
            {
                PoseProps.Check(prop);
                foreach (var file in prop.Files.Values.Distinct())
                {
                    if (files.ContainsKey(file)) continue;
                    var entry = zip.GetEntry(file) ?? throw new InvalidDataException("An animation package is missing a prop file.");
                    if ((total += entry.Length) > MaxUnpackedBytes) throw new InvalidDataException("An animation package unpacks too large.");
                    var bytes = Bytes(entry, PoseProps.MaxFileBytes);
                    PoseProps.CheckFile(file, bytes);
                    files[file] = bytes;
                }
            }
        }
        if (zip.Entries.Any(e => e.FullName != "manifest.json" && !files.ContainsKey(e.FullName))) throw new InvalidDataException("An animation package holds files its manifest doesn't list.");
        var animations = manifest.Animations.Select(a => a with { Name = Clean(a.Name, "Animation"), Source = Clean(a.Source, "") }).ToArray();
        return new(manifest with { Name = Clean(manifest.Name, "Animations"), Animations = animations }, files);
    }

    private static byte[] Bytes(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("An animation package's file is too large.");
        using var stream = entry.Open();
        var buffer = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        stream.CopyTo(buffer);
        if (buffer.Length != entry.Length) throw new InvalidDataException("An animation package's file is damaged.");
        return buffer.ToArray();
    }

    /// <summary>Checks an animation file's structure (PapFiles).</summary>
    public static void Check(ReadOnlySpan<byte> pap) => PapFiles.Check(pap);

    /// <summary>
    /// A sound file's header: "SEDBSSCF", version 3, and the file size it records lying within the file. Some tools
    /// pad a file past the size it records (Mittens' Romantic Cowgirl: 5,664 recorded, 5,680 on disk), which the game
    /// plays, so the padding is allowed; a file shorter than it claims is not.
    /// Its audio entries are checked too, as the game's sound thread reads them (it divides by their header values; a
    /// divide by zero there crashed the game in 0.52.3, and these files come from a home's owner): each entry, its sub-info and its audio inside the file,
    /// 1-8 channels, a sample rate the game uses, a format it plays (Vorbis, MS-ADPCM, HCA), a loop within the audio, and
    /// ADPCM blocks that hold their channels. An entry without audio is an empty slot and is left alone.
    /// </summary>
    public static void CheckSound(ReadOnlySpan<byte> scd)
    {
        // (A sound from a mod is mended first: MendSound.)
        if (scd.Length < 0x40 || scd.Length > MaxSoundBytes || !scd[..8].SequenceEqual("SEDBSSCF"u8) || BinaryPrimitives.ReadUInt32LittleEndian(scd[8..]) != 3
            || BinaryPrimitives.ReadUInt32LittleEndian(scd[0x10..]) is var recorded && (recorded < 0x30 || recorded > scd.Length))
            throw new InvalidDataException("A sound file in the animations is not valid.");
        var tables = BinaryPrimitives.ReadUInt16LittleEndian(scd[0x0E..]);
        if (tables < 0x30 || tables + 16L > scd.Length) Bad("its tables");
        int audioCount = BinaryPrimitives.ReadUInt16LittleEndian(scd[(tables + 4)..]);
        long table = BinaryPrimitives.ReadUInt32LittleEndian(scd[(tables + 12)..]);
        if (audioCount is 0 or > 256 || table < tables || table + audioCount * 4L > scd.Length) Bad("its audio table");
        for (var i = 0; i < audioCount; i++)
        {
            long entry = BinaryPrimitives.ReadUInt32LittleEndian(scd[(int)(table + i * 4)..]);
            if (entry < 0x30 || entry + 0x20 > scd.Length) Bad("an audio entry's place");
            var at = scd[(int)entry..];
            long size = BinaryPrimitives.ReadUInt32LittleEndian(at), channels = BinaryPrimitives.ReadUInt32LittleEndian(at[4..]),
                rate = BinaryPrimitives.ReadUInt32LittleEndian(at[8..]), format = BinaryPrimitives.ReadUInt32LittleEndian(at[12..]),
                loopStart = BinaryPrimitives.ReadUInt32LittleEndian(at[16..]), loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(at[20..]),
                subInfo = BinaryPrimitives.ReadUInt32LittleEndian(at[24..]);
            if (size == 0) continue;
            if (entry + 0x20 + subInfo + size > scd.Length || channels is 0 or > 8 || rate is < 4000 or > 192000 || format is not (0x06 or 0x0C or 0x1A)
                || loopStart > loopEnd
                // Vorbis loops are bytes of the audio; MS-ADPCM's are samples (at most two a byte); HCA's are its own blocks.
                || format == 0x06 && loopEnd > size || format == 0x0C && loopEnd > size * 2 + 16) Bad($"audio {i}: {size} bytes, {channels} channels, {rate} Hz, format {format:X}, loop {loopStart}-{loopEnd}, sub-info {subInfo}");
            if (format == 0x0C)
            {
                // MS-ADPCM: its WAVEFORMATEX, block alignment at +12 and samples per block at +18 (after any marker chunk).
                var sub = at.Slice(0x20, (int)subInfo);
                if ((BinaryPrimitives.ReadUInt32LittleEndian(at[28..]) & 1) != 0)
                {
                    if (sub.Length < 8 || !sub[..4].SequenceEqual("MARK"u8) || BinaryPrimitives.ReadUInt32LittleEndian(sub[4..]) is var marks && marks > sub.Length) Bad("its ADPCM markers");
                    sub = sub[(int)BinaryPrimitives.ReadUInt32LittleEndian(sub[4..])..];
                }
                if (sub.Length < 20 || BinaryPrimitives.ReadUInt16LittleEndian(sub[2..]) != channels || BinaryPrimitives.ReadUInt16LittleEndian(sub[12..]) < 7 * channels
                    || BinaryPrimitives.ReadUInt16LittleEndian(sub[18..]) == 0) Bad("its ADPCM format");
            }
        }
        static void Bad(string what) => throw new InvalidDataException($"A sound file in the animations is not valid ({what}).");
    }

    /// <summary>
    /// A sound file whose MS-ADPCM audio claims more bytes than the file holds, with each such entry cut to the whole
    /// blocks it does hold. Some tool writes an entry's size as the file's length less the entry's place, so the audio
    /// runs 124 bytes past the end ([BDG] Prone-Bone's six sounds, 3 November 2026: 8,060 claimed, 7,936 there, 31
    /// blocks of 256); the game plays them, reading only whole blocks. Anything else comes back unchanged, for
    /// CheckSound to judge.
    /// </summary>
    public static byte[] MendSound(byte[] scd)
    {
        if (scd.Length < 0x40 || !scd.AsSpan(0, 8).SequenceEqual("SEDBSSCF"u8)) return scd;
        var tables = BinaryPrimitives.ReadUInt16LittleEndian(scd.AsSpan(0x0E));
        if (tables < 0x30 || tables + 16L > scd.Length) return scd;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(scd.AsSpan(tables + 4));
        long table = BinaryPrimitives.ReadUInt32LittleEndian(scd.AsSpan(tables + 12));
        if (count is 0 or > 256 || table < tables || table + count * 4L > scd.Length) return scd;
        byte[]? mended = null;
        for (var i = 0; i < count; i++)
        {
            long entry = BinaryPrimitives.ReadUInt32LittleEndian(scd.AsSpan((int)(table + i * 4)));
            if (entry < 0x30 || entry + 0x20 > scd.Length) continue;
            var at = scd.AsSpan((int)entry);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(at), format = BinaryPrimitives.ReadUInt32LittleEndian(at[12..]),
                subInfo = BinaryPrimitives.ReadUInt32LittleEndian(at[24..]), audio = entry + 0x20 + subInfo;
            if (format != 0x0C || size == 0 || audio + size <= scd.Length || audio + 20 > scd.Length) continue;
            var sub = at.Slice(0x20, (int)subInfo);
            if ((BinaryPrimitives.ReadUInt32LittleEndian(at[28..]) & 1) != 0)
            {
                if (sub.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(sub[4..]) is var marks && marks > sub.Length) continue;
                sub = sub[(int)BinaryPrimitives.ReadUInt32LittleEndian(sub[4..])..];
            }
            if (sub.Length < 20 || BinaryPrimitives.ReadUInt16LittleEndian(sub[12..]) is var block && block == 0) continue;
            var held = (scd.Length - audio) / block * block;
            if (held <= 0) continue;
            mended ??= (byte[])scd.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(mended.AsSpan((int)entry), (uint)held);
        }
        return mended ?? scd;
    }

    /// <summary>
    /// The emote timeline files a home's animations bring, each at its carrier's own timeline path (the loop's): file(package,
    /// animation, key) gives the path on this PC of the timeline made to play the carrier's animation at that key.
    /// </summary>
    public static Dictionary<string, string> TimelineRedirections(IEnumerable<(string Package, int Animation, int Carrier)> animations,
        Func<string, PoseAnimationManifest?> manifest, Func<string, int, string, string?> file)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (package, index, carrierIndex) in animations)
            if (PoseCarriers.Get(carrierIndex) is { } carrier && manifest(package) is { } known && index >= 0 && index < known.Animations.Length
                && known.Animations[index].Timeline != null && file(package, index, carrier.Loop) is { } disk)
                paths[TimelinePath(carrier.Loop)] = disk;
        return paths;
    }

    /// <summary>The effect files (and their textures) of packages a home plays, at their own paths: file(package, name) gives the path on this PC.</summary>
    public static Dictionary<string, string> EffectRedirections(IEnumerable<string> packages, Func<string, PoseAnimationManifest?> manifest, Func<string, string, string?> file)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var package in packages.Distinct())
            foreach (var effect in manifest(package)?.Effects ?? [])
                if (file(package, effect) is { } disk) paths[EffectRoot + effect] = disk;
        return paths;
    }

    /// <summary>The sound files of packages a home plays, at their own paths: file(package, name) gives the path on this PC.</summary>
    public static Dictionary<string, string> SoundRedirections(IEnumerable<string> packages, Func<string, PoseAnimationManifest?> manifest, Func<string, string, string?> file)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var package in packages.Distinct())
            foreach (var sound in manifest(package)?.Sounds ?? [])
                if (file(package, sound) is { } disk) paths[SoundRoot + sound] = disk;
        return paths;
    }

    /// <summary>
    /// The file of an animation that fits a body's path: the game reads the file at a body's path as made for that
    /// body (papLoadTable decides whose path each body reads), so it must have been made for that body (skeleton), or
    /// for the Hyur man's, which every body is fitted from. The mod's own file for that body comes first. Null when the
    /// animation has none (that body would draw it on the wrong bones, and T-pose).
    /// </summary>
    public static string? FileFor(PoseAnimation animation, string body, Func<string, string?> skeleton)
    {
        if (animation.Races.TryGetValue(body, out var own) && skeleton(own) is var made && (made == body || made == "c0101" || made == null)) return own;
        var files = animation.Races.Values.Distinct().ToArray();
        if (files.FirstOrDefault(f => skeleton(f) == body) is { } exact) return exact;
        if (files.FirstOrDefault(f => skeleton(f) == "c0101") is { } root) return root;
        // Skeletons not known yet (files not unpacked): the nearest body's file, as before 0.39.3.
        return files.All(f => skeleton(f) == null) && PoseCarriers.Nearest(body, animation.Races.Keys) is { } near ? animation.Races[near] : null;
    }

    /// <summary>
    /// How many of the eighteen bodies would draw an animation on bones it was made for at a carrier: each body reads
    /// the carrier's loop at the path reads gives (its own, or another body's), which needs a file that fits there.
    /// </summary>
    public static int Fit(PoseAnimation animation, PoseCarrier carrier, Func<string, string, string?> reads, Func<string, string?> skeleton) =>
        PoseCarriers.Races.Count(race => FileFor(animation, reads(race, carrier.Loop) ?? race, skeleton) != null);

    /// <summary>
    /// The files Penumbra serves for a home's custom animations: for each animation at its carrier, the carrier's loop
    /// and intro at every path a body reads (reads: papLoadTable), each from a file of the animation that fits that
    /// path (FileFor). file(package, fileName, key) gives the path on this PC of that file renamed for the carrier's
    /// animation at that key, or null while it isn't ready. Paths no file fits are returned in standIns, for the game's
    /// own standing pose (a player there stands still rather than T-posing).
    /// </summary>
    public static Dictionary<string, string> Redirections(IEnumerable<(string Package, int Animation, int Carrier)> animations,
        Func<string, PoseAnimationManifest?> manifest, Func<string, string, string, string?> file, Func<string, string, string?> reads,
        Func<string, string, string?> skeleton, List<(string Path, string Key)>? standIns = null)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (package, index, carrierIndex) in animations)
        {
            if (PoseCarriers.Get(carrierIndex) is not { } carrier || manifest(package) is not { } known || index < 0 || index >= known.Animations.Length) continue;
            foreach (var key in new[] { carrier.Loop, carrier.Intro })
            {
                // The carrier's start plays the animation's own start when it has one (props popping in), else the loop.
                var animation = key == carrier.Intro && known.Animations[index].Intro is { Count: > 0 } intro ? known.Animations[index] with { Races = intro } : known.Animations[index];
                foreach (var body in PoseCarriers.Races.Select(race => reads(race, key) ?? race).Distinct())
                {
                    var path = PoseCarriers.PapPath(body, key);
                    if (FileFor(animation, body, f => skeleton(package, f)) is not { } fits) { standIns?.Add((path, key)); continue; }
                    if (file(package, fits, key) is { } disk) paths[path] = disk;
                }
            }
        }
        return paths;
    }
}
