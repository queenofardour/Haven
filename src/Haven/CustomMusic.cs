using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Haven.Core;
using Haven.Core.Music;
using HavenNative.Sync;
using Lumina.Excel.Sheets;

namespace Haven;

/// <summary>
/// A song in this PC's library: the name its owner gave it, its length, and when it was added; whether it came from
/// the former community gallery (closed in 0.38.0; not the player's to delete from the server), and when the locker
/// last stored it.
/// </summary>
internal sealed record SongRecord(string Title, long Milliseconds, int Channels, int SampleRate, DateTimeOffset Added,
    bool FromCommunity = false, DateTimeOffset? Stored = null);

/// <summary>
/// Custom music for the orchestrion. A song is kept as Ogg Vorbis under its fingerprint in
/// content/music, and wrapped in an SCD built from an orchestrion file of the player's own game data (ScdMusic). Penumbra
/// serves the SCD at a path the game does not have (<see cref="GamePath"/>), which the orchestrion player is given
/// like any track's file. Songs the player added are listed in records.json; songs fetched for other people's homes are
/// kept the same way without a record.
///
/// The locker (Haven.Core ContentLocker, media "music") keeps each song once under music/ on Haven's server, on the same
/// tickets and allowance as custom pictures: the owner's Haven uploads the songs its homes play, and guests who agreed
/// to a home's custom content download them.
/// </summary>
internal sealed class CustomMusic : IDisposable
{
    private const string Tag = "Haven.CustomMusic";
    private const int Priority = 99;
    private readonly IDalamudPluginInterface plugin;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly string directory, recordsPath;
    private readonly object gate = new();
    private Dictionary<string, SongRecord> records = [];
    private readonly HashSet<string> built = [], registered = [];
    private readonly ConcurrentDictionary<string, Task> building = new();
    // Songs whose SCD couldn't be built: tried again after a wait that doubles (from a minute), and reported once.
    private readonly ConcurrentDictionary<string, (long RetryAt, int Tries)> unbuilt = new();
    private byte[]? template;
    private DateTimeOffset nextRegistration;
    // Redirects are never followed: every address Haven fetches is on the relay's own origin.
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = ContentLocker.MaxMusicBytes };
    /// <summary>Usage figures from the locker's answers, kept with the pictures' (one allowance).</summary>
    public Action<HttpResponseMessage>? KeepUsage { get; set; }
    public bool PenumbraMissing { get; private set; }
    /// <summary>Messages for the player (failed imports or builds), drained by the UI thread.</summary>
    public readonly ConcurrentQueue<string> Failures = new();

    public CustomMusic(IDalamudPluginInterface plugin, IDataManager data, IPluginLog log)
    {
        this.plugin = plugin; this.data = data; this.log = log;
        directory = Path.Combine(plugin.ConfigDirectory.FullName, "content", "music");
        recordsPath = Path.Combine(directory, "records.json");
        try { if (File.Exists(recordsPath)) records = JsonSerializer.Deserialize<Dictionary<string, SongRecord>>(File.ReadAllText(recordsPath)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException) { log.Warning(ex, "[Haven] The custom music list could not be read."); }
        records = records.Where(r => ContentLocker.IsFingerprint(r.Key) && File.Exists(OggFile(r.Key))).ToDictionary();
    }

    /// <summary>The player's songs, newest first.</summary>
    public (string Hash, SongRecord Song)[] Library { get { lock (gate) return [.. records.OrderByDescending(r => r.Value.Added).Select(r => (r.Key, r.Value))]; } }
    public SongRecord? Song(string hash) { lock (gate) return records.GetValueOrDefault(hash); }

    /// <summary>The made-up game path Penumbra serves a song's SCD at.</summary>
    // Built SCDs and their game paths carry the layout's version: 0.34.0's had no loop, and a game that already played one
    // may still hold that file under its old path.
    private const string Layout = "l2";
    public static string GamePath(string hash) => $"music/haven/{hash[..32]}{Layout}.scd";
    private string OggFile(string hash) => Path.Combine(directory, hash[..2], hash + ".ogg");
    private string ScdFile(string hash) => Path.Combine(directory, hash[..2], hash + "." + Layout + ".scd");

    /// <summary>Whether the song's file is on this PC.</summary>
    // Songs are named by their fingerprint, so once a file is there it stays right until Haven deletes it: asked
    // every frame for every hash in a home, the answer is remembered instead of asking the disk again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> present = new();
    public bool Has(string hash) => present.ContainsKey(hash) || File.Exists(OggFile(hash)) && present.TryAdd(hash, 0) | true;
    /// <summary>Whether the orchestrion can play the song now: built and served by Penumbra.</summary>
    public bool Playable(string hash) { lock (gate) return registered.Contains(hash); }

    /// <summary>Converts a song the player chose and adds it to the library under the name they gave it.</summary>
    public Task<string> Import(string file, string title, IProgress<float>? progress, CancellationToken token) => Task.Run(() =>
    {
        var info = new FileInfo(file);
        if (!info.Exists) throw new InvalidOperationException("That file no longer exists.");
        if (info.Length > MusicEncoder.MaxInputBytes) throw new InvalidOperationException("This file is too large. Choose a song of at most 10 minutes.");
        PreparedSong song;
        try { song = MusicEncoder.Prepare(File.ReadAllBytes(file), token, progress); }
        catch (InvalidDataException ex) { throw new InvalidOperationException(ex.Message, ex); }
        var hash = ContentLocker.Fingerprint(song.Ogg);
        Store(hash, song.Ogg);
        lock (gate)
        {
            records[hash] = new(SafeText.Clean(title, 60).Trim() is { Length: > 0 } name ? name : "Untitled song", song.Milliseconds, song.Channels, song.SampleRate,
                records.TryGetValue(hash, out var old) ? old.Added : DateTimeOffset.UtcNow);
            Save();
        }
        return hash;
    }, token);

    /// <summary>Keeps a song's Ogg file (one the player added, or one fetched for a home), verified by its fingerprint.</summary>
    public void Store(string hash, byte[] ogg)
    {
        if (!ContentLocker.IsFingerprint(hash) || ContentLocker.Fingerprint(ogg) != hash) throw new InvalidDataException("The song does not match its fingerprint.");
        // A downloaded song Haven wouldn't play fails here, as a download, rather than each time it's prepared.
        if (MusicEncoder.Describe(ogg) is not { } song || !MusicEncoder.Playable(song)) throw new InvalidDataException("The song isn't one Haven plays (damaged, too long, too large, or an unusual format).");
        var path = OggFile(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) { var temporary = path + ".tmp"; File.WriteAllBytes(temporary, ogg); File.Move(temporary, path, overwrite: true); }
    }

    /// <summary>Songs on this PC that are not in the library (fetched for homes or previews).</summary>
    public string[] Cached()
    {
        if (!Directory.Exists(directory)) return [];
        var all = Directory.EnumerateFiles(directory, "*.ogg", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(ContentLocker.IsFingerprint);
        lock (gate) return [.. all.Where(h => !records.ContainsKey(h))];
    }

    /// <summary>Deletes a cached song's files; library songs are kept.</summary>
    public void Forget(string hash)
    {
        lock (gate) { if (records.ContainsKey(hash)) return; built.Remove(hash); registered.Remove(hash); }
        present.TryRemove(hash, out _); unbuilt.TryRemove(hash, out _);
        foreach (var file in new[] { OggFile(hash), ScdFile(hash), OldScdFile(hash) }) try { File.Delete(file); } catch (IOException ex) { log.Warning(ex, "[Haven] A song file could not be deleted."); }
        nextRegistration = default;
    }
    private string OldScdFile(string hash) => Path.Combine(directory, hash[..2], hash + ".scd");

    private void Change(string hash, Func<SongRecord, SongRecord> change)
    {
        lock (gate) { if (records.TryGetValue(hash, out var song)) { records[hash] = change(song); Save(); } }
    }

    public void Rename(string hash, string title)
    {
        lock (gate)
        {
            if (!records.TryGetValue(hash, out var song)) return;
            records[hash] = song with { Title = SafeText.Clean(title, 60).Trim() is { Length: > 0 } name ? name : song.Title };
            Save();
        }
    }

    /// <summary>Forgets a song and deletes its files from this PC. The caller checks that no home plays it.</summary>
    public void Delete(string hash)
    {
        lock (gate) { records.Remove(hash); built.Remove(hash); registered.Remove(hash); Save(); }
        present.TryRemove(hash, out _); unbuilt.TryRemove(hash, out _);
        foreach (var file in new[] { OggFile(hash), ScdFile(hash), OldScdFile(hash) }) try { File.Delete(file); } catch (IOException ex) { log.Warning(ex, "[Haven] A song file could not be deleted."); }
        nextRegistration = default;
    }

    /// <summary>
    /// Makes the songs a playlist needs playable: builds each SCD (off the framework thread) and has Penumbra serve
    /// them all. Songs not on this PC yet are skipped until they arrive. Call every frame while music may play.
    /// </summary>
    public void Prepare(IEnumerable<string> hashes)
    {
        var changed = false;
        foreach (var hash in hashes.Distinct())
        {
            bool done; lock (gate) done = built.Contains(hash);
            // A finished task still listed (its continuation ran before it was stored) doesn't count as building.
            if (done || building.TryGetValue(hash, out var working) && !working.IsCompleted || !Has(hash)) continue;
            if (unbuilt.TryGetValue(hash, out var failed) && Environment.TickCount64 < failed.RetryAt) continue;
            building[hash] = Task.Run(() => Build(hash)).ContinueWith(task =>
            {
                building.TryRemove(hash, out _);
                if (!task.IsFaulted) { unbuilt.TryRemove(hash, out _); return; }
                var tries = unbuilt.TryGetValue(hash, out var before) ? before.Tries + 1 : 1;
                unbuilt[hash] = (Environment.TickCount64 + 60_000L * (1L << Math.Min(tries - 1, 6)), tries);
                log.Warning(task.Exception!.GetBaseException(), "[Haven] A custom song could not be prepared (attempt {Tries}).", tries);
                if (tries == 1) Failures.Enqueue("A custom song could not be prepared: " + task.Exception!.GetBaseException().Message);
            });
        }
        lock (gate) changed = built.Count != registered.Count || !built.SetEquals(registered);
        if (changed && DateTimeOffset.UtcNow >= nextRegistration) Register();
    }

    private void Build(string hash)
    {
        var scd = ScdFile(hash);
        // 0.34.0's SCD had no loop and stalled the game's orchestrion player once it played to its end.
        if (File.Exists(OldScdFile(hash))) File.Delete(OldScdFile(hash));
        if (!File.Exists(scd))
        {
            var ogg = File.ReadAllBytes(OggFile(hash));
            var song = MusicEncoder.Describe(ogg) ?? throw new InvalidDataException("The song's file is damaged.");
            if (!MusicEncoder.Playable(song)) throw new InvalidDataException("The song isn't one Haven plays (too long, too large, or an unusual format).");
            var bytes = ScdMusic.Wrap(Template(), ogg, song.Channels, song.SampleRate);
            var temporary = scd + ".tmp"; File.WriteAllBytes(temporary, bytes); File.Move(temporary, scd, overwrite: true);
        }
        lock (gate) built.Add(hash);
    }

    // An orchestrion SCD from the player's own game data: everything but its audio becomes the custom song's frame.
    private byte[] Template()
    {
        if (template != null) return template;
        foreach (var row in data.GetExcelSheet<OrchestrionPath>().Where(r => !r.File.IsEmpty))
            if (data.GetFile(row.File.ToString())?.Data is { } file && ScdMusic.IsTemplate(file)) return template = file;
        throw new InvalidOperationException("No orchestrion music file in the game data can hold a custom song.");
    }

    private void Register()
    {
        Dictionary<string, string> paths;
        string[] keys;
        lock (gate) { keys = [.. built]; paths = keys.ToDictionary(GamePath, ScdFile); }
        try
        {
            var result = plugin.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5").InvokeFunc(Tag, paths, "", Priority);
            if (result is not (0 or 1)) throw new InvalidOperationException($"Penumbra refused the custom music (code {result}).");
            lock (gate) { registered.Clear(); registered.UnionWith(keys); }
            PenumbraMissing = false;
        }
        catch (Exception ex) when (ex is Dalamud.Plugin.Ipc.Exceptions.IpcError or InvalidOperationException)
        {
            if (!PenumbraMissing) { log.Warning(ex, "[Haven] Penumbra is unavailable for custom music."); Failures.Enqueue("Custom music needs Penumbra. Custom songs are skipped until it is running."); }
            PenumbraMissing = true;
            nextRegistration = DateTimeOffset.UtcNow.AddSeconds(5);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(directory);
        var temporary = recordsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(records));
        File.Move(temporary, recordsPath, overwrite: true);
    }

    // The locker --------------------------------------------------------------

    /// <summary>Puts a library song on the locker, unless it is already there. Records when it was stored.</summary>
    public async Task Upload(AskTicket ask, string origin, string hash, CancellationToken token)
    {
        var ogg = await File.ReadAllBytesAsync(OggFile(hash), token);
        var ticket = await ask(new("upload", hash, ogg.Length, ContentLocker.AgreementVersion, Media: ContentLocker.Music), token);
        if (ticket.Error == ContentLocker.NotInHome) throw new PictureNotReadyException(ticket.Error);
        if (ticket.Error != null || ticket.Path == null) throw new InvalidOperationException(ticket.Error ?? "The relay gave no ticket.");
        var url = ticket.Url(origin, hash);
        using (var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), token))
        {
            KeepUsage?.Invoke(head);
            if (head.IsSuccessStatusCode) { Change(hash, s => s with { Stored = DateTimeOffset.UtcNow }); return; }
            if (head.StatusCode != HttpStatusCode.NotFound) throw new InvalidOperationException(await Describe(head));
        }
        using var content = new ByteArrayContent(ogg);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
        using var put = await http.PutAsync(url, content, token);
        KeepUsage?.Invoke(put);
        if (!put.IsSuccessStatusCode) throw new InvalidOperationException(await Describe(put));
        Change(hash, s => s with { Stored = DateTimeOffset.UtcNow });
    }

    /// <summary>Fetches a song a home plays, through that gathering's relay (a guest who agreed to its custom content).</summary>
    public async Task Download(RelaySyncService relay, string hash)
    {
        using var transfer = ContentTransfers.Begin(ContentKind.Song, hash);
        var ticket = await relay.RequestContentTicket(new("download", hash, Media: ContentLocker.Music), CancellationToken.None);
        if (ticket.Error == ContentLocker.NotInHome) throw new PictureNotReadyException(ticket.Error);
        if (ticket.Error != null || ticket.Path == null || relay.ContentOrigin is not {} origin) throw new InvalidOperationException(ticket.Error ?? "The relay's address is unknown.");
        using var response = await http.GetAsync(ticket.Url(origin, hash), HttpCompletionOption.ResponseHeadersRead);
        // Not there yet: the owner's Haven uploads a song moments after it joins the playlist.
        if (response.StatusCode == HttpStatusCode.NotFound) throw new PictureNotReadyException("That custom song is not shared yet.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await Describe(response));
        Store(hash, await transfer.Read(response, ContentLocker.MaxMusicBytes));
    }

    /// <summary>Deletes a song from Haven's server at once. Only its uploader can.</summary>
    public async Task DeleteFromServer(AskTicket ask, string origin, string hash, CancellationToken token)
    {
        var ticket = await ask(new("delete", hash, Media: ContentLocker.Music), token);
        if (ticket.Error != null || ticket.Path == null) throw new InvalidOperationException(ticket.Error ?? "The relay gave no ticket.");
        using var response = await http.PostAsync(ticket.Url(origin, hash), null, token);
        if ((int)response.StatusCode == 403) throw new NotUploaderException("Only the character that uploaded this song can delete it from Haven's server. Log in on that character and try again.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await Describe(response));
        KeepUsage?.Invoke(response);
        Change(hash, s => s with { Stored = null });
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
            403 => "The custom song link expired. It will be retried.",
            410 => "That custom song was removed by Haven's operator.",
            415 => "Haven's server accepts only Haven's own Ogg Vorbis songs.",
            429 => "Too many custom content requests. Try again later.",
            503 => "Haven's content storage is busy. Try again later.",
            404 => "That custom song is not on Haven's server yet. The owner's Haven uploads it when they host.",
            var code => $"Haven's server could not transfer the custom song ({code}).",
        };
    }

    public void Dispose()
    {
        http.Dispose();
        try { plugin.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5").InvokeFunc(Tag, Priority); }
        catch (Exception ex) when (ex is Dalamud.Plugin.Ipc.Exceptions.IpcError) { }
    }
}
