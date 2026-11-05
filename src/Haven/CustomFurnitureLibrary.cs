using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Haven.Core;
using Haven.Core.Furniture;
using HavenNative.Sync;

namespace Haven;

/// <summary>
/// A custom furnishing in this PC's library: the name its owner gave it, the vanilla item it is built on, when it was
/// added, and when the locker last stored it.
/// </summary>
internal sealed record FurnitureRecord(string Name, uint BaseItem, DateTimeOffset Added, DateTimeOffset? Stored = null);

/// <summary>
/// Custom furniture. A package (Haven.Core.Furniture) is kept as its .zip under its
/// fingerprint in content/furniture/packages, unpacked into content/furniture/files/&lt;id&gt;, and its files served by
/// Penumbra at their own game paths (bgcommon/hou/hv/&lt;id&gt;/…), which nothing in the game uses, so they replace
/// nothing for anyone. Packages the player imported are listed in records.json; packages fetched for other people's
/// homes are kept the same way without a record. Every package is checked file by file before it is unpacked.
/// </summary>
internal sealed class CustomFurnitureLibrary : IDisposable
{
    private const string Tag = "Haven.CustomFurniture";
    private const int Priority = 99;
    private readonly IDalamudPluginInterface plugin;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly string packages, unpacked, recordsPath;
    private readonly object gate = new();
    private Dictionary<string, FurnitureRecord> records = [];
    // Unpacked and checked packages, by fingerprint, and those Penumbra serves.
    private readonly Dictionary<string, FurnitureManifest> ready = [];
    private readonly HashSet<string> registered = [];
    private readonly ConcurrentDictionary<string, Task> working = new();
    // Packages that failed to unpack (a clash, a file that won't write) aren't tried again this session.
    private readonly ConcurrentDictionary<string, byte> unusable = new();
    private readonly object unpacking = new();
    private DateTimeOffset nextRegistration;
    public bool PenumbraMissing { get; private set; }
    public readonly ConcurrentQueue<string> Failures = new();
    // Redirects are never followed: every address Haven fetches is on the relay's own origin.
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(120), MaxResponseContentBufferSize = ContentLocker.MaxFurnitureBytes };
    /// <summary>Usage figures from the locker's answers, kept with the pictures' (one allowance).</summary>
    public Action<HttpResponseMessage>? KeepUsage { get; set; }

    public CustomFurnitureLibrary(IDalamudPluginInterface plugin, IDataManager data, IPluginLog log)
    {
        this.plugin = plugin; this.data = data; this.log = log;
        var directory = Path.Combine(plugin.ConfigDirectory.FullName, "content", "furniture");
        packages = Path.Combine(directory, "packages");
        unpacked = Path.Combine(directory, "files");
        recordsPath = Path.Combine(directory, "records.json");
        try { if (File.Exists(recordsPath)) records = JsonSerializer.Deserialize<Dictionary<string, FurnitureRecord>>(File.ReadAllText(recordsPath)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException) { log.Warning(ex, "[Haven] The custom furniture list could not be read."); }
        records = records.Where(r => ContentLocker.IsFingerprint(r.Key) && File.Exists(PackageFile(r.Key))).ToDictionary();
    }

    /// <summary>The player's custom furniture, newest first.</summary>
    public (string Hash, FurnitureRecord Item)[] Library { get { lock (gate) return [.. records.OrderByDescending(r => r.Value.Added).Select(r => (r.Key, r.Value))]; } }
    public FurnitureRecord? Item(string hash) { lock (gate) return records.GetValueOrDefault(hash); }
    private string PackageFile(string hash) => Path.Combine(packages, hash[..2], hash + ".zip");
    // Packages are named by their fingerprint, so once a file is there it stays right until Haven deletes it: asked
    // every frame for every hash in a home, the answer is remembered instead of asking the disk again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> present = new();
    public bool Has(string hash) => present.ContainsKey(hash) || File.Exists(PackageFile(hash)) && present.TryAdd(hash, 0) | true;
    public byte[] Package(string hash) => File.ReadAllBytes(PackageFile(hash));

    /// <summary>
    /// The base item's model paths this package replaces, and their new paths, once its files are unpacked and served
    /// by Penumbra; null until then (the furnishing draws as its base item meanwhile).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Models(string hash)
    {
        lock (gate) return registered.Contains(hash) && ready.TryGetValue(hash, out var manifest) ? manifest.Models : null;
    }

    /// <summary>A served model of a package, read from where it is unpacked; null for other paths or a file not there.</summary>
    public Lumina.Data.Files.MdlFile? ModelFile(string servedPath)
    {
        if (!servedPath.StartsWith(FurniturePackages.Root, StringComparison.Ordinal)) return null;
        var end = servedPath.IndexOf('/', FurniturePackages.Root.Length);
        if (end <= FurniturePackages.Root.Length) return null;
        var file = Path.Combine(unpacked, servedPath[FurniturePackages.Root.Length..end], Path.GetFileName(servedPath));
        return File.Exists(file) ? data.GameData.GetFileFromDisk<Lumina.Data.Files.MdlFile>(file) : null;
    }

    // Props' prefabs, built once their model is unpacked: the model, and a box collider from its bounds.
    private readonly Dictionary<string, FurniturePrefab> props = [];
    /// <summary>A prop's prefab once its package is served; null until then (nothing shows meanwhile).</summary>
    public FurniturePrefab? Prop(string hash)
    {
        lock (gate)
        {
            if (!registered.Contains(hash) || !ready.TryGetValue(hash, out var manifest) || !manifest.Prop) return null;
            if (props.TryGetValue(hash, out var known)) return known;
            var model = manifest.Models.Values.Single();
            // A model Lumina can't read, or whose bounds aren't a prop's size (they become its collider: NaN or a huge box
            // would reach the game's collision), shows nothing, remembered so it isn't read again every frame.
            FurniturePrefab prefab;
            try
            {
                var file = data.GameData.GetFileFromDisk<Lumina.Data.Files.MdlFile>(Path.Combine(unpacked, manifest.Id, Path.GetFileName(model)));
                var min = new System.Numerics.Vector3(file.BoundingBoxes.Min[0], file.BoundingBoxes.Min[1], file.BoundingBoxes.Min[2]);
                var max = new System.Numerics.Vector3(file.BoundingBoxes.Max[0], file.BoundingBoxes.Max[1], file.BoundingBoxes.Max[2]);
                static bool Sane(System.Numerics.Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && System.Numerics.Vector3.Abs(v).Length() <= 50;
                if (!Sane(min) || !Sane(max) || min.X > max.X || min.Y > max.Y || min.Z > max.Z) throw new InvalidDataException("A prop's bounds are not a furnishing's size.");
                prefab = new FurniturePrefab([new PrefabModel(model, System.Numerics.Matrix4x4.Identity, min, max)],
                    [new PrefabCollider(null, System.Numerics.Matrix4x4.Identity, min, max)], [], []);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.Warning(ex, "[Haven] A custom prop's model could not be read; it shows nothing.");
                prefab = new FurniturePrefab([], [], [], []);
            }
            return props[hash] = prefab;
        }
    }

    /// <summary>A package's name: the player's own for their furniture, otherwise the uploader's from its manifest once unpacked.</summary>
    public string? Name(string hash)
    {
        lock (gate) return records.TryGetValue(hash, out var item) ? item.Name : ready.TryGetValue(hash, out var manifest) ? manifest.Name : null;
    }

    /// <summary>The installed game's own file at a path (never Penumbra's), for the files a mod leaves alone.</summary>
    public byte[]? GameFile(string path) => data.FileExists(path) ? data.GetFile(path)?.Data : null;

    /// <summary>Builds a prop from a minion model a mod changes, keeps it, and adds it to the library under the given name.</summary>
    public string ImportProp(PenumbraMod mod, string model, string name, int materialSet = 1)
    {
        var (zip, manifest) = FurniturePackages.BuildProp(mod, model, GameFile, name, materialSet);
        var hash = ContentLocker.Fingerprint(zip);
        Store(hash, zip);
        lock (gate)
        {
            records[hash] = new(manifest.Name, 0, records.TryGetValue(hash, out var old) ? old.Added : DateTimeOffset.UtcNow);
            Save();
        }
        return hash;
    }

    /// <summary>Builds a package from a mod for one base item, keeps it, and adds it to the library under the given name.</summary>
    public string Import(PenumbraMod mod, uint baseItem, IReadOnlyList<string> baseModels, string name)
    {
        var (zip, manifest) = FurniturePackages.Build(mod, baseItem, baseModels, GameFile, name);
        var hash = ContentLocker.Fingerprint(zip);
        Store(hash, zip);
        lock (gate)
        {
            records[hash] = new(manifest.Name, baseItem, records.TryGetValue(hash, out var old) ? old.Added : DateTimeOffset.UtcNow);
            Save();
        }
        return hash;
    }

    /// <summary>Keeps a package (imported, or fetched for a home), verified by its fingerprint and checked in full.</summary>
    public void Store(string hash, byte[] zip)
    {
        if (!ContentLocker.IsFingerprint(hash) || ContentLocker.Fingerprint(zip) != hash) throw new InvalidDataException("The custom furnishing does not match its fingerprint.");
        FurniturePackages.Read(zip);
        var path = PackageFile(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) { var temporary = path + ".tmp"; File.WriteAllBytes(temporary, zip); File.Move(temporary, path, overwrite: true); }
    }

    public void Rename(string hash, string name)
    {
        lock (gate)
        {
            if (!records.TryGetValue(hash, out var item)) return;
            records[hash] = item with { Name = SafeText.Clean(name, 60).Trim() is { Length: > 0 } clean ? clean : item.Name };
            Save();
        }
    }

    /// <summary>
    /// Forgets a furnishing and deletes its files from this PC. The caller checks that no home uses it. Its unpacked
    /// folder stays while another loaded package shares its id (the same import kept under another name).
    /// </summary>
    public void Delete(string hash)
    {
        FurnitureManifest? manifest;
        bool shared;
        lock (gate)
        {
            records.Remove(hash); ready.Remove(hash, out manifest); registered.Remove(hash); props.Remove(hash); Save();
            shared = manifest != null && ready.Values.Any(m => m.Id == manifest.Id);
        }
        try
        {
            present.TryRemove(hash, out _);
            unusable.TryRemove(hash, out _);
            File.Delete(PackageFile(hash));
            if (manifest != null && !shared && Directory.Exists(Path.Combine(unpacked, manifest.Id))) Directory.Delete(Path.Combine(unpacked, manifest.Id), true);
        }
        catch (IOException ex) { log.Warning(ex, "[Haven] A custom furnishing's files could not be deleted."); }
        nextRegistration = default;
    }

    public void Change(string hash, Func<FurnitureRecord, FurnitureRecord> change)
    {
        lock (gate) { if (records.TryGetValue(hash, out var item)) { records[hash] = change(item); Save(); } }
    }

    /// <summary>
    /// Makes the packages a home uses drawable: unpacks each (off the framework thread) after checking it, then has
    /// Penumbra serve every unpacked package's files. Packages not on this PC yet are skipped until they arrive.
    /// </summary>
    public void Prepare(IEnumerable<string> hashes)
    {
        foreach (var hash in hashes.Distinct())
        {
            bool done; lock (gate) done = ready.ContainsKey(hash);
            // A finished task still listed (its continuation ran before it was stored) doesn't count as working.
            if (done || working.TryGetValue(hash, out var busy) && !busy.IsCompleted || unusable.ContainsKey(hash) || !Has(hash)) continue;
            working[hash] = Task.Run(() => Unpack(hash)).ContinueWith(task =>
            {
                working.TryRemove(hash, out _);
                if (task.IsFaulted)
                {
                    unusable[hash] = 0;
                    log.Warning(task.Exception!.GetBaseException(), "[Haven] A custom furnishing could not be prepared.");
                    Failures.Enqueue("A custom furnishing could not be prepared: " + task.Exception!.GetBaseException().Message);
                }
            });
        }
        bool changed; lock (gate) changed = !registered.SetEquals(ready.Keys);
        if (changed && DateTimeOffset.UtcNow >= nextRegistration) Register();
    }

    // A package's id names its folder and its game paths, and comes from its content, so two honest packages with one
    // id (the same import kept under two names) hold the same files. One whose files differ from a loaded package's
    // with its id is refused rather than overwriting them.
    private void Unpack(string hash)
    {
        lock (unpacking) UnpackOne(hash);
    }
    private void UnpackOne(string hash)
    {
        var package = FurniturePackages.Read(Package(hash));
        var folder = Path.Combine(unpacked, package.Manifest.Id);
        lock (gate)
        {
            if (ready.Any(r => r.Key != hash && r.Value.Id == package.Manifest.Id))
            {
                foreach (var (path, bytes) in package.Files)
                {
                    var file = Path.Combine(folder, Path.GetFileName(path));
                    if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes))
                        throw new InvalidDataException($"The custom furnishing \"{package.Manifest.Name}\" clashes with another one already shown, so it shows as its original furnishing.");
                }
                ready[hash] = package.Manifest;
                return;
            }
        }
        Directory.CreateDirectory(folder);
        foreach (var (path, bytes) in package.Files)
        {
            var file = Path.Combine(folder, Path.GetFileName(path));
            if (File.Exists(file) && new FileInfo(file).Length == bytes.Length && File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes)) continue;
            var temporary = file + ".tmp"; File.WriteAllBytes(temporary, bytes); File.Move(temporary, file, overwrite: true);
        }
        lock (gate) ready[hash] = package.Manifest;
    }

    private void Register()
    {
        Dictionary<string, string> paths;
        string[] keys;
        lock (gate)
        {
            keys = [.. ready.Keys];
            // Two packages can share an id only with the same files (Unpack refuses others), so either one stands.
            paths = [];
            foreach (var m in ready.Values) foreach (var f in m.Files) paths[f] = Path.Combine(unpacked, m.Id, Path.GetFileName(f));
        }
        try
        {
            var result = plugin.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5").InvokeFunc(Tag, paths, "", Priority);
            if (result is not (0 or 1)) throw new InvalidOperationException($"Penumbra refused the custom furniture (code {result}).");
            lock (gate) { registered.Clear(); registered.UnionWith(keys); }
            PenumbraMissing = false;
        }
        catch (Exception ex) when (ex is Dalamud.Plugin.Ipc.Exceptions.IpcError or InvalidOperationException)
        {
            if (!PenumbraMissing) { log.Warning(ex, "[Haven] Penumbra is unavailable for custom furniture."); Failures.Enqueue("Custom furniture needs Penumbra. It shows as the original furnishing until Penumbra is running."); }
            PenumbraMissing = true;
            nextRegistration = DateTimeOffset.UtcNow.AddSeconds(5);
        }
    }

    // The locker --------------------------------------------------------------

    /// <summary>Puts a package on the locker, unless it is already there, and notes when it was stored.</summary>
    public async Task Upload(AskTicket ask, string origin, string hash, CancellationToken token)
    {
        var zip = await File.ReadAllBytesAsync(PackageFile(hash), token);
        await LockerUpload.Send(http, ask, origin, hash, zip, KeepUsage, Describe, token);
        Change(hash, f => f with { Stored = DateTimeOffset.UtcNow });
    }

    /// <summary>
    /// Deletes a package from Haven's server at once. Only the character that uploaded it can;
    /// already gone counts as done. The answer carries the allowance with its bytes given back.
    /// </summary>
    public async Task DeleteFromServer(AskTicket ask, string origin, string hash, CancellationToken token)
    {
        var ticket = await ask(new("delete", hash, Media: ContentLocker.FurnitureMedia), token);
        if (ticket.Error != null || ticket.Path == null) throw new InvalidOperationException(ticket.Error ?? "The relay gave no ticket.");
        using var response = await http.PostAsync(ticket.Url(origin, hash), null, token);
        if ((int)response.StatusCode == 403) throw new NotUploaderException("Only the character that uploaded it can delete it from Haven's server.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await Describe(response));
        KeepUsage?.Invoke(response);
    }

    /// <summary>Fetches a package a home uses through that gathering's relay (a guest who agreed to its custom content).</summary>
    public async Task Download(RelaySyncService relay, string hash)
    {
        using var transfer = ContentTransfers.Begin(ContentKind.Furniture, hash);
        var ticket = await relay.RequestContentTicket(new("download", hash, Media: ContentLocker.FurnitureMedia), CancellationToken.None);
        if (ticket.Error == ContentLocker.NotInHome) throw new PictureNotReadyException(ticket.Error);
        if (ticket.Error != null || ticket.Path == null || relay.ContentOrigin is not {} origin) throw new InvalidOperationException(ticket.Error ?? "The relay's address is unknown.");
        using var response = await http.GetAsync(ticket.Url(origin, hash), HttpCompletionOption.ResponseHeadersRead);
        // Not there yet: the owner's Haven uploads a piece moments after it is placed.
        if (response.StatusCode == HttpStatusCode.NotFound) throw new PictureNotReadyException("That custom furnishing is not shared yet.");
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
            403 => "The custom furniture link expired. It will be retried.",
            410 => "That custom furnishing was removed by Haven's operator.",
            415 => "Haven's server accepts only Haven's own furniture packages.",
            429 => "Too many custom content requests. Try again later.",
            503 => "Haven's content storage is busy. Try again later.",
            var code => $"Haven's server could not transfer the custom furnishing ({code}).",
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
        try { plugin.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5").InvokeFunc(Tag, Priority); }
        catch (Exception ex) when (ex is Dalamud.Plugin.Ipc.Exceptions.IpcError) { }
    }
}
