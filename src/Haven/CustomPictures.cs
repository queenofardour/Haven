using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Haven.Core;
using HavenNative.Sync;
using StbImageSharp;
using StbImageWriteSharp;

namespace Haven;

// An image being framed, decoded.
internal sealed record PictureSource(byte[] Rgba, int Width, int Height);
internal sealed record PictureThumb(byte[] Rgba, int Width, int Height);
// A picture that is not on the relay or the locker yet: the owner's upload or change is still on its way. Asked again
// shortly, without telling anyone.
internal sealed class PictureNotReadyException(string message) : InvalidOperationException(message);
// The owner's allowance and Haven's shared pool, as the locker last reported them (with every upload and delete).
internal sealed record PictureUsage(long OwnerBytes, long OwnerLimit, int UploadsToday, int UploadsPerDay, long PoolBytes, long PoolLimit, DateTimeOffset At);
// What Settings → Custom content shows about one of the owner's originals.
internal sealed record PictureDetails(long Size, int Width, int Height, DateTimeOffset AddedAt, bool FromCommunity, DateTimeOffset? LastStored, string[] Homes);
// An original: an image the owner uploaded (or, before 0.38.0, took from the community gallery), a JPEG of at most 2048 pixels a side
// and 2 MB, uploaded once and framed by every PC for each frame that shows it.
internal sealed record OriginalInfo(int Width, int Height, DateTimeOffset AddedAt);
// 0.30.0's framed pictures, kept only to carry homes over: the original and framing each was made from.
internal sealed record FramedFrom(string Original, PictureLayout Layout);
// This PC's own records: the owner's originals, which of them are shared and how labelled, when each was last stored
// on the locker, and the last usage figures.
internal sealed class PictureRecords
{
    // 3: shared originals, framed by every PC (0.31.0). 2: originals plus framed pictures (0.30.0).
    public int Version { get; set; }
    public Dictionary<string, OriginalInfo> Originals { get; set; } = [];
    public Dictionary<string, FramedFrom> Framed { get; set; } = [];
    // Originals re-encoded to the shared size by the 0.31.0 carry-over: old key → new key.
    public Dictionary<string, string> Converted { get; set; } = [];
    public HashSet<string> Library { get; set; } = [];
    public HashSet<string> FromCommunity { get; set; } = [];
    public Dictionary<string, DateTimeOffset> Stored { get; set; } = [];
    public PictureUsage? Usage { get; set; }
}
public delegate Task<ContentTicket> AskTicket(ContentTicketRequest request, CancellationToken token);

// The owner's own pictures shown in frames (Haven.Core ContentLocker). A frame names an original (by its fingerprint)
// and its framing; the original is uploaded once, and every PC (owner and guests alike) builds the frame's texture from
// it (PictureCanvas), so adjusting a frame sends nothing but its framing. Originals live in content/originals; frames'
// textures and materials in content/frames, named by PictureCanvas.FrameKey at Haven's own game paths
// (CustomPictureFiles), which Penumbra redirects for every collection. Nothing in the game uses those paths, so the
// redirect replaces nothing for anyone. Images are only ever decoded by StbImageSharp's managed decoders, after their
// size is read from the header.
internal sealed class CustomPictures : IDisposable
{
    private const string Tag = "Haven.CustomPictures";
    private const int Priority = 99;
    private const long MaxSourceBytes = 50L * 1024 * 1024;
    private const int MaxSourceSide = 8192;
    private readonly IDalamudPluginInterface plugin;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly string pictures, sources, originals, frames, recordsPath;
    private readonly object recordsGate = new();
    private PictureRecords records = new();
    // Redirects are never followed: every address Haven fetches is on the relay's own origin.
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = ContentLocker.MaxPictureBytes };
    // Frames built, by frame key, and work in progress, by original (downloads) and frame key (builds).
    private readonly ConcurrentDictionary<string, bool> built = new();
    private readonly ConcurrentDictionary<string, Task> working = new();
    private readonly ConcurrentQueue<string> failures = new();
    // Work that failed, and when to try again: 3 s, doubling to at most 2 minutes.
    private readonly ConcurrentDictionary<string, (int Attempts, DateTimeOffset Next)> retries = new();
    // Touched on the framework thread only.
    private readonly HashSet<string> registered = [];
    private DateTimeOffset nextRegistration;

    public CustomPictures(IDalamudPluginInterface plugin, IDataManager data, IPluginLog log)
    {
        this.plugin = plugin; this.data = data; this.log = log;
        var content = Path.Combine(plugin.ConfigDirectory.FullName, "content");
        pictures = Path.Combine(content, "pictures");
        sources = Path.Combine(content, "sources");
        originals = Path.Combine(content, "originals");
        frames = Path.Combine(content, "frames");
        Directory.CreateDirectory(originals); Directory.CreateDirectory(frames);
        recordsPath = Path.Combine(content, "records.json");
        try
        {
            if (System.IO.File.Exists(recordsPath)) records = System.Text.Json.JsonSerializer.Deserialize<PictureRecords>(System.IO.File.ReadAllBytes(recordsPath)) ?? new();
            // Before 0.28.2 every cached picture counted as the owner's.
            else records.Library = Directory.Exists(pictures)
                ? new DirectoryInfo(pictures).EnumerateFiles("*.jpg", SearchOption.AllDirectories).Select(f => Path.GetFileNameWithoutExtension(f.Name)).Where(ContentLocker.IsFingerprint).ToHashSet()
                : [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { log.Warning(ex, "Custom picture records could not be read."); }
        // Upload notes for pictures that are no longer the owner's (older framed pictures, deleted ones) are dropped.
        foreach (var gone in records.Stored.Keys.Where(k => !records.Originals.ContainsKey(k)).ToArray()) records.Stored.Remove(gone);
        if (records.Version < 3) Migrating = Task.Run(Migrate);
    }

    // Older versions kept framed pictures: before 0.30.0 with each one's source beside it, in 0.30.0 with originals of
    // up to 4096 pixels. Each source or framed picture becomes an original (a framed one cut from what it shows), and
    // every original is brought to the shared size (2048 pixels, 2 MB). Framed records stay, so the controller can turn
    // each home's frame into its original and framing.
    public Task Migrating { get; private set; } = Task.CompletedTask;
    private void Migrate()
    {
        try
        {
            if (records.Version < 2)
            {
                if (Directory.Exists(sources))
                    foreach (var layoutFile in Directory.EnumerateFiles(sources, "*.json", SearchOption.AllDirectories))
                    {
                        var framed = Path.GetFileNameWithoutExtension(layoutFile);
                        var sourceFile = Path.ChangeExtension(layoutFile, ".jpg");
                        if (!ContentLocker.IsFingerprint(framed) || !System.IO.File.Exists(sourceFile)) continue;
                        var layout = System.Text.Json.JsonSerializer.Deserialize<PictureLayout>(System.IO.File.ReadAllBytes(layoutFile));
                        if (layout is not { Valid: true }) continue;
                        var original = KeepOriginal(System.IO.File.ReadAllBytes(sourceFile), System.IO.File.GetLastWriteTimeUtc(sourceFile), true);
                        Record(r => r.Framed[framed] = new(original, layout));
                    }
                string[] mine; lock (recordsGate) mine = records.Library.ToArray();
                foreach (var framed in mine.Where(h => System.IO.File.Exists(FramedFile(h)) && FramingOf(h) == null)) CutOriginal(framed);
            }
            KeyValuePair<string, OriginalInfo>[] all; lock (recordsGate) all = records.Originals.ToArray();
            foreach (var (old, info) in all)
            {
                if (!HasOriginal(old) || new FileInfo(OriginalFile(old)).Length <= ContentLocker.MaxPictureBytes && Math.Max(info.Width, info.Height) <= ContentLocker.OriginalSide) continue;
                var shared = AddOriginal(LoadOriginal(old, MaxSourceSide));
                Record(r =>
                {
                    r.Originals.Remove(old); r.Originals[shared] = r.Originals[shared] with { AddedAt = info.AddedAt };
                    r.Converted[old] = shared;
                    if (r.FromCommunity.Remove(old)) r.FromCommunity.Add(shared);
                });
                System.IO.File.Delete(OriginalFile(old));
            }
            Record(r => r.Version = 3);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or IndexOutOfRangeException or ArgumentException)
        { log.Warning(ex, "Custom pictures could not all be carried over to originals."); }
    }

    /// <summary>The original and framing a 0.30.0 framed picture was made from, after any re-encoding; null if none.</summary>
    public (string Original, PictureLayout Layout)? CarriedOver(string framed)
    {
        lock (recordsGate)
        {
            if (!records.Framed.TryGetValue(framed, out var from)) return null;
            return (records.Converted.GetValueOrDefault(from.Original, from.Original), from.Layout);
        }
    }
    private FramedFrom? FramingOf(string framed) { lock (recordsGate) return records.Framed.GetValueOrDefault(framed); }
    private string FramedFile(string framed) => Path.Combine(pictures, framed[..2], framed + ".jpg");

    // A framed picture without a record: an original cut from what its frame shows, framed to fill.
    private void CutOriginal(string framed)
    {
        var picture = System.IO.File.ReadAllBytes(FramedFile(framed));
        if (ContentLocker.Fingerprint(picture) != framed) return;
        var texture = Decode(picture, ContentLocker.PictureWidth);
        if (texture.Width != PictureCanvas.Width || texture.Height != PictureCanvas.Height) return;
        var portrait = PictureCanvas.IsPortrait(picture);
        int across = (int)Math.Round((PictureCanvas.Right - PictureCanvas.Left) * PictureCanvas.Width), down = (int)Math.Round((PictureCanvas.Bottom - PictureCanvas.Top) * PictureCanvas.Height);
        var (w, h) = portrait ? (down, across) : (across, down);
        var original = AddOriginal(new(PictureCanvas.Visible(texture.Data, portrait, w, h), w, h));
        Record(r => { r.Framed[framed] = new(original, new()); if (r.FromCommunity.Remove(framed)) r.FromCommunity.Add(original); });
    }

    // Originals ---------------------------------------------------------------

    private string OriginalFile(string hash) => Path.Combine(originals, hash[..2], hash + ".jpg");
    public bool HasOriginal(string hash) => System.IO.File.Exists(OriginalFile(hash));

    // Stores an original's JPEG bytes under their fingerprint (once); recorded as the owner's when mine.
    private string KeepOriginal(byte[] jpeg, DateTimeOffset addedAt, bool mine)
    {
        var hash = ContentLocker.Fingerprint(jpeg);
        var path = OriginalFile(hash);
        if (!System.IO.File.Exists(path)) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); WriteAtomic(path, jpeg); }
        if (mine)
        {
            ImageInfo? info; using (var stream = new MemoryStream(jpeg)) info = ImageInfo.FromStream(stream);
            Record(r => { if (!r.Originals.ContainsKey(hash)) r.Originals[hash] = new(info?.Width ?? 0, info?.Height ?? 0, addedAt); });
        }
        return hash;
    }

    /// <summary>
    /// Keeps an image as one of the owner's originals: at most 2048 pixels a side, re-encoded (which leaves out every
    /// metadata block, camera and location) at the highest quality that fits 2 MB. Returns its fingerprint.
    /// </summary>
    public string AddOriginal(PictureSource source)
    {
        source = Shrink(source, ContentLocker.OriginalSide);
        foreach (var quality in new[] { 92, 85, 75, 65, 50 })
        {
            using var output = new MemoryStream();
            new ImageWriter().WriteJpg(source.Rgba, source.Width, source.Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output, quality);
            if (output.Length > ContentLocker.MaxPictureBytes) continue;
            var hash = KeepOriginal(output.ToArray(), DateTimeOffset.UtcNow, true);
            Record(r => r.Originals[hash] = r.Originals[hash] with { AddedAt = DateTimeOffset.UtcNow });
            return hash;
        }
        throw new InvalidDataException("That picture could not be made small enough to share.");
    }

    public PictureSource LoadOriginal(string hash) => LoadOriginal(hash, ContentLocker.OriginalSide);
    private PictureSource LoadOriginal(string hash, int maxSide)
    {
        var image = Decode(System.IO.File.ReadAllBytes(OriginalFile(hash)), maxSide);
        return new(image.Data, image.Width, image.Height);
    }

    /// <summary>The owner's originals, newest first.</summary>
    public IReadOnlyList<string> Originals()
    {
        KeyValuePair<string, OriginalInfo>[] all; lock (recordsGate) all = records.Originals.ToArray();
        return all.Where(o => HasOriginal(o.Key)).OrderByDescending(o => o.Value.AddedAt).Select(o => o.Key).ToArray();
    }
    public bool IsMine(string hash) { lock (recordsGate) return records.Originals.ContainsKey(hash); }
    public OriginalInfo? OriginalDetails(string hash) { lock (recordsGate) return records.Originals.GetValueOrDefault(hash); }
    public long OriginalSize(string hash) => HasOriginal(hash) ? new FileInfo(OriginalFile(hash)).Length : 0;

    /// <summary>Deletes one of the owner's originals from this PC.</summary>
    public void DeleteOriginal(string hash)
    {
        if (System.IO.File.Exists(OriginalFile(hash))) System.IO.File.Delete(OriginalFile(hash));
        thumbnails.TryRemove(hash, out _);
        Record(r => { r.Originals.Remove(hash); r.FromCommunity.Remove(hash); r.Stored.Remove(hash); });
    }

    public bool PenumbraMissing { get; private set; }
    public string? TakeFailure() => failures.TryDequeue(out var failure) ? failure : null;
    public bool IsFromCommunity(string hash) { lock (recordsGate) return records.FromCommunity.Contains(hash); }
    public DateTimeOffset? LastStored(string hash) { lock (recordsGate) return records.Stored.TryGetValue(hash, out var at) ? at : null; }
    public PictureUsage? Usage { get { lock (recordsGate) return records.Usage; } }
    private void Record(Action<PictureRecords> change)
    {
        byte[] bytes;
        lock (recordsGate) { change(records); bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(records); }
        try { WriteAtomic(recordsPath, bytes); }
        catch (IOException ex) { log.Warning(ex, "Custom picture records could not be saved."); }
    }

    // An original's thumbnail, in its own shape, at most 160 pixels a side.
    private readonly ConcurrentDictionary<string, PictureThumb?> thumbnails = new();
    public PictureThumb? Thumbnail(string hash) => thumbnails.GetOrAdd(hash, h =>
    {
        try { return HasOriginal(h) ? Small(LoadOriginal(h)) : null; }
        // A picture that won't decode (a downloaded one included: the image decoder throws more than these) has no
        // thumbnail, remembered, rather than failing again each time it's asked for.
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    });
    private static PictureThumb Small(PictureSource source)
    {
        var factor = Math.Max(1, (int)Math.Ceiling(Math.Max(source.Width, source.Height) / 160.0));
        int w = source.Width, h = source.Height; var small = source.Rgba;
        if (factor > 1) small = PictureCanvas.Reduce(source.Rgba, source.Width, source.Height, factor, out w, out h);
        return new(small, w, h);
    }

    // Reads an image's size from its header before decoding it, so an oversized or hostile file is refused cheaply.
    private static ImageResult Decode(byte[] bytes, int maxSide)
    {
        ImageInfo? info;
        using (var stream = new MemoryStream(bytes)) info = ImageInfo.FromStream(stream);
        if (info is not { } header) throw new InvalidDataException("That file is not a picture Haven can read (PNG, JPEG, BMP, GIF or TGA).");
        if (header.Width is <= 0 || header.Height is <= 0 || header.Width > maxSide || header.Height > maxSide)
            throw new InvalidDataException($"That picture is too large. Use one at most {maxSide:N0} pixels a side.");
        return ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
    }

    /// <summary>An image file decoded, at most 8192 pixels a side; AddOriginal brings it to the shared size.</summary>
    public static PictureSource LoadSource(string path)
    {
        if (new FileInfo(path).Length > MaxSourceBytes) throw new InvalidDataException("That file is over 50 MB.");
        var image = Decode(System.IO.File.ReadAllBytes(path), MaxSourceSide);
        return new(image.Data, image.Width, image.Height);
    }
    private static PictureSource Shrink(PictureSource source, int side)
    {
        var factor = (int)Math.Ceiling(Math.Max(source.Width, source.Height) / (double)side);
        if (factor <= 1) return source;
        var pixels = PictureCanvas.Reduce(source.Rgba, source.Width, source.Height, factor, out var w, out var h);
        return new(pixels, w, h);
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        System.IO.File.WriteAllBytes(path + ".tmp", bytes);
        System.IO.File.Move(path + ".tmp", path, true);
    }

    // Frames ------------------------------------------------------------------

    private string FrameFile(string key, string extension) => Path.Combine(frames, key[..2], key + extension);
    // The material a frame loads, once its picture is built and Penumbra redirects its paths; null keeps it blank.
    public string? Material(string key) => registered.Contains(key) ? CustomPictureFiles.MaterialPath(key) : null;

    // A frame's texture and material, built from its original and framing (kept on disk by frame key).
    private void Build(string key, string original, PictureLayout? layout, bool portrait)
    {
        if (!System.IO.File.Exists(FrameFile(key, ".tex")) || !System.IO.File.Exists(FrameFile(key, ".mtrl")))
        {
            var picture = System.IO.File.ReadAllBytes(OriginalFile(original));
            if (ContentLocker.Fingerprint(picture) != original) { System.IO.File.Delete(OriginalFile(original)); throw new InvalidDataException("A cached custom picture was damaged and has been removed."); }
            if (picture.Length < 4 || picture[0] != 0xFF || picture[1] != 0xD8 || picture[2] != 0xFF) throw new InvalidDataException("A custom picture was not a JPEG image.");
            var image = Decode(picture, ContentLocker.OriginalSide);
            var pixels = PictureCanvas.Compose(image.Data, image.Width, image.Height, layout ?? new(), portrait);
            var template = data.GetFile(CustomPictureFiles.TemplateTexture)?.Data ?? throw new InvalidDataException("The game's painting texture is unavailable.");
            var placeholder = data.GetFile(CustomPictureFiles.Placeholder)?.Data ?? throw new InvalidDataException("The game's frame material is unavailable.");
            Directory.CreateDirectory(Path.GetDirectoryName(FrameFile(key, ".tex"))!);
            WriteAtomic(FrameFile(key, ".tex"), CustomPictureFiles.Texture(pixels, PictureCanvas.Width, PictureCanvas.Height, template));
            WriteAtomic(FrameFile(key, ".mtrl"), CustomPictureFiles.Material(placeholder, CustomPictureFiles.TexturePath(key)));
        }
        built[key] = true;
    }

    /// <summary>
    /// Makes each listed frame's picture: fetches its original when this PC lacks it and a relay is given (a guest who
    /// agreed to load this home's pictures), builds the frame's texture from it, and has Penumbra redirect every
    /// built frame's paths. Called every frame on the framework thread; it starts work only for what it has not tried.
    /// </summary>
    public void Ensure(IReadOnlyCollection<(string Original, PictureLayout? Layout, bool Portrait)> wanted, RelaySyncService? relay)
    {
        foreach (var (original, layout, portrait) in wanted)
        {
            var key = PictureCanvas.FrameKey(original, layout, portrait);
            if (built.ContainsKey(key) || Busy(key) || Busy(original)) continue;
            if (!HasOriginal(original))
            {
                if (relay == null || Waiting(original)) continue;
                Start(original, async () => { KeepOriginal(await Download(relay, original), DateTimeOffset.UtcNow, false); retries.TryRemove(original, out _); }, download: true);
            }
            else if (!Waiting(key)) Start(key, () => { Build(key, original, layout, portrait); return Task.CompletedTask; });
        }
        if (built.Keys.Any(k => !registered.Contains(k)) && DateTimeOffset.UtcNow >= nextRegistration) Register();
    }
    private bool Busy(string id) => working.TryGetValue(id, out var running) && !running.IsCompleted;
    private bool Waiting(string id) => retries.TryGetValue(id, out var retry) && retry.Next > DateTimeOffset.UtcNow;
    private void Start(string id, Func<Task> work, bool download = false) => working[id] = Task.Run(async () =>
    {
        try { await work(); broken.TryRemove(id, out _); }
        // Anything at all: a picture whose decoder throws something unexpected must back off like any other failure,
        // not be retried on the next frame.
        catch (Exception ex)
        {
            var attempts = retries.GetValueOrDefault(id).Attempts + 1;
            retries[id] = (attempts, DateTimeOffset.UtcNow.AddSeconds(Math.Min(120, 3 * Math.Pow(2, attempts - 1))));
            if (ex is PictureNotReadyException) log.Debug("Haven custom picture {Id} is not shared yet; asking again.", id[..12]);
            else
            {
                broken[id] = true;
                log.Warning(ex, "Haven custom picture {Id} unavailable.", id[..12]);
                // A download that failed outright stops there until the player asks to try again (RetryDownload).
                if (download) { retries[id] = (attempts, DateTimeOffset.MaxValue); downloadFailures.Enqueue((id, ex.Message)); }
                else if (attempts == 1) failures.Enqueue(ex.Message);
            }
        }
        finally { working.TryRemove(id, out _); }
    });

    /// <summary>Entering a home asks again at once for pictures that failed before.</summary>
    public void ResetRetries() { retries.Clear(); broken.Clear(); }
    private readonly ConcurrentQueue<(string Id, string Message)> downloadFailures = new();
    /// <summary>An original whose download failed outright, and why; Haven waits for the player before trying it again.</summary>
    public (string Id, string Message)? TakeDownloadFailure() => downloadFailures.TryDequeue(out var failure) ? failure : null;
    /// <summary>Lets a failed download be tried again at once.</summary>
    public void RetryDownload(string id) { retries.TryRemove(id, out _); broken.TryRemove(id, out _); }
    // Pictures whose last try failed for a reason other than not being shared yet: the download bar leaves them out.
    private readonly ConcurrentDictionary<string, bool> broken = new();
    /// <summary>Whether fetching or building this picture last failed outright (not merely "not shared yet").</summary>
    public bool Broken(string id) => broken.ContainsKey(id);

    private void Register()
    {
        var keys = built.Keys.ToArray();
        var paths = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            paths[CustomPictureFiles.MaterialPath(key)] = FrameFile(key, ".mtrl");
            paths[CustomPictureFiles.TexturePath(key)] = FrameFile(key, ".tex");
        }
        try
        {
            var result = plugin.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5").InvokeFunc(Tag, paths, "", Priority);
            if (result is not (0 or 1)) throw new InvalidOperationException($"Penumbra refused the custom pictures (code {result}).");
            registered.UnionWith(keys);
            PenumbraMissing = false;
        }
        catch (Exception ex) when (ex is Dalamud.Plugin.Ipc.Exceptions.IpcError or InvalidOperationException)
        {
            if (!PenumbraMissing) { log.Warning(ex, "Penumbra is unavailable for Haven custom pictures."); failures.Enqueue("Custom pictures need Penumbra. Frames stay blank until it is running."); }
            PenumbraMissing = true;
            nextRegistration = DateTimeOffset.UtcNow.AddSeconds(5);
        }
    }

    // The locker --------------------------------------------------------------

    // Each character's content secret (ContentLocker.OwnerKey), made once and kept only on this PC, keyed by the
    // character key (a hash of the content ID). Losing it only means older pictures can no longer be shared again.
    private Dictionary<string, string>? secrets;
    private readonly object secretsGate = new();
    public string SecretFor(ulong contentId)
    {
        if (contentId == 0) return "";
        var path = Path.Combine(Path.GetDirectoryName(recordsPath)!, "secrets.json");
        lock (secretsGate)
        {
            if (secrets == null)
            {
                try { secrets = System.IO.File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(path)) : null; }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { log.Warning(ex, "Custom picture secrets could not be read; new ones are made."); }
                secrets ??= [];
            }
            var key = HomeOwners.Key(contentId);
            if (secrets.TryGetValue(key, out var secret) && ContentLocker.IsSecret(secret)) return secret;
            secrets[key] = secret = ContentLocker.NewSecret();
            try
            {
                var temporary = path + ".tmp";
                System.IO.File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(secrets));
                System.IO.File.Move(temporary, path, true);
            }
            catch (IOException ex) { log.Warning(ex, "A custom picture secret could not be saved."); }
            return secret;
        }
    }

    private async Task<byte[]> Download(RelaySyncService relay, string hash)
    {
        using var transfer = ContentTransfers.Begin(ContentKind.Picture, hash);
        var ticket = await relay.RequestContentTicket(new("download", hash), CancellationToken.None);
        if (ticket.Error == ContentLocker.NotInHome) throw new PictureNotReadyException(ticket.Error);
        if (ticket.Error != null || ticket.Path == null || relay.ContentOrigin is not {} origin) throw new InvalidOperationException(ticket.Error ?? "The relay's address is unknown.");
        using var response = await http.GetAsync(ticket.Url(origin, hash), HttpCompletionOption.ResponseHeadersRead);
        // Not there yet: the owner's Haven shares a new picture moments after placing it.
        if (response.StatusCode == HttpStatusCode.NotFound) throw new PictureNotReadyException("That custom picture is not shared yet.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await Describe(response));
        var picture = await transfer.Read(response, ContentLocker.MaxPictureBytes);
        if (ContentLocker.Fingerprint(picture) != hash) throw new InvalidDataException("A custom picture did not match its fingerprint and was discarded.");
        return picture;
    }

    /// <summary>
    /// Shares an original with the locker through a gathering's relay connection or a content session: nothing is
    /// sent when it is already there. Records when it was stored and the usage figures the locker reports.
    /// </summary>
    public async Task Upload(AskTicket ask, string origin, string hash, CancellationToken token)
    {
        var picture = await System.IO.File.ReadAllBytesAsync(OriginalFile(hash), token);
        var ticket = await ask(new("upload", hash, picture.Length, ContentLocker.AgreementVersion), token);
        if (ticket.Error == ContentLocker.NotInHome) throw new PictureNotReadyException(ticket.Error);
        if (ticket.Error != null || ticket.Path == null) throw new InvalidOperationException(ticket.Error ?? "The relay gave no ticket.");
        var url = ticket.Url(origin, hash);
        using (var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), token))
        {
            KeepUsage(head);
            if (head.IsSuccessStatusCode) { Record(r => r.Stored[hash] = DateTimeOffset.UtcNow); return; }
            if (head.StatusCode != HttpStatusCode.NotFound) throw new InvalidOperationException(await Describe(head));
        }
        using var content = new ByteArrayContent(picture);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var put = await http.PutAsync(url, content, token);
        KeepUsage(put);
        if (!put.IsSuccessStatusCode) throw new InvalidOperationException(await Describe(put));
        Record(r => r.Stored[hash] = DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Asks the locker whether it holds an original, without sending it, and corrects this PC's note of what was
    /// uploaded: the operator may have cleared the locker, and unused pictures expire.
    /// </summary>
    public async Task<bool> OnServer(AskTicket ask, string origin, string hash, CancellationToken token)
    {
        var ticket = await ask(new("upload", hash, new FileInfo(OriginalFile(hash)).Length, ContentLocker.AgreementVersion), token);
        if (ticket.Error != null || ticket.Path == null) throw new InvalidOperationException(ticket.Error ?? "The relay gave no ticket.");
        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, ticket.Url(origin, hash)), token);
        KeepUsage(head);
        if (head.IsSuccessStatusCode) { Record(r => { if (!r.Stored.ContainsKey(hash)) r.Stored[hash] = DateTimeOffset.UtcNow; }); return true; }
        if (head.StatusCode == HttpStatusCode.NotFound) { Record(r => r.Stored.Remove(hash)); return false; }
        throw new InvalidOperationException(await Describe(head));
    }

    /// <summary>
    /// Asks the locker for the allowance as it stands now, without storing anything: a HEAD on an upload ticket for a
    /// fingerprint nothing has (all zeros), which answers "not here" with the usage. So Settings shows what the server
    /// holds even with nothing uploaded since (after the operator cleared it, or after deletes on another PC).
    /// </summary>
    public async Task RefreshUsage(AskTicket ask, string origin, CancellationToken token)
    {
        var probe = new string('0', 64);
        var ticket = await ask(new("upload", probe, 1, ContentLocker.AgreementVersion), token);
        if (ticket.Error != null || ticket.Path == null) throw new InvalidOperationException(ticket.Error ?? "The relay gave no ticket.");
        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, ticket.Url(origin, probe)), token);
        KeepUsage(head);
    }

    /// <summary>
    /// Deletes an original from Haven's server at once. Only the character that
    /// uploaded it can: its owner key is that character's. Already gone counts as done.
    /// </summary>
    public async Task DeleteFromServer(AskTicket ask, string origin, string hash, CancellationToken token)
    {
        var ticket = await ask(new("delete", hash), token);
        if (ticket.Error != null || ticket.Path == null) throw new InvalidOperationException(ticket.Error ?? "The relay gave no ticket.");
        using var response = await http.PostAsync(ticket.Url(origin, hash), null, token);
        if ((int)response.StatusCode == 403) throw new NotUploaderException("Only the character that uploaded this picture can delete it from Haven's server. Log in on that character and try again.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await Describe(response));
        KeepUsage(response);                                      // the allowance with the deleted bytes given back
        Record(r => r.Stored.Remove(hash));
    }

    // "ownerBytes,ownerLimit,uploadsToday,uploadsPerDay,poolBytes,poolLimit" on every upload answer, and on a delete's.
    public void KeepUsage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Haven-Usage", out var values)) return;
        var parts = values.First().Split(',');
        if (parts.Length != 6 || !parts.All(p => long.TryParse(p, System.Globalization.CultureInfo.InvariantCulture, out _))) return;
        var n = parts.Select(p => long.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Record(r => r.Usage = new(n[0], n[1], (int)n[2], (int)n[3], n[4], n[5], DateTimeOffset.UtcNow));
    }

    // The locker's limits explain themselves in a short plain-text body (the daily and storage allowances, the
    // account-wide budget); other refusals are described here.
    private static async Task<string> Describe(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is 403 or 409 or 429 or 503 && response.Content.Headers.ContentType?.MediaType is null or "text/plain")
        {
            var text = (await response.Content.ReadAsStringAsync()).Trim();
            if (text.Length is > 0 and <= 200 && text.All(c => !char.IsControl(c))) return text;
        }
        return Describe(response.StatusCode);
    }
    private static string Describe(HttpStatusCode status) => (int)status switch
    {
        403 => "The custom picture link expired. It will be retried.",
        410 => "That custom picture was removed by the relay's operator.",
        413 => "Your custom content storage is full. Remove some pictures, songs, furniture or animations you no longer use.",
        415 => "The relay accepts only Haven's own JPEG pictures.",
        429 => "Too many custom picture requests. Try again later.",
        503 => "The picture locker is busy. Try again later.",
        404 => "That custom picture is not on the relay yet. The owner's Haven shares it when they host.",
        _ => $"The relay could not transfer the custom picture ({(int)status}).",
    };

    public void Dispose()
    {
        try { plugin.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5").InvokeFunc(Tag, Priority); }
        catch (Exception ex) when (ex is Dalamud.Plugin.Ipc.Exceptions.IpcError) { }
        http.Dispose();
    }
}
