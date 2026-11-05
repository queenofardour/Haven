using Haven.Core;

namespace Haven;

// Custom music: the player's song library, adding songs to it, and putting them in
// the playlist like any orchestrion track. The owner's Haven puts each song a home plays on the locker (a content
// session as it joins the playlist, and again whenever it hosts), and a guest who agreed to the home's custom content
// downloads each one once, as for custom pictures.
internal sealed partial class HousingController
{
    /// <summary>This PC's custom songs.</summary>
    public CustomMusic Songs { get; private set; } = null!;
    /// <summary>A song being converted: its name and progress (0-1), or null.</summary>
    public (string Title, float Progress)? SongImport { get; private set; }
    private CancellationTokenSource? songImport;
    // The active home's custom songs, and the locker work for them: uploads done in this gathering, and when to try a
    // failed upload or download again.
    private MusicOptions? songMusic;
    private Guid? songHome;
    private string[] songHashes = [];
    private string songRoom = "";
    private readonly HashSet<string> songsUploaded = [], songsWorking = [];
    private readonly Dictionary<string, (int Attempts, DateTimeOffset Next)> songRetries = [];
    private bool songsTidied;

    private void InitialiseCustomMusic(Dalamud.Plugin.IDalamudPluginInterface pi, Dalamud.Plugin.Services.IDataManager data)
    {
        Songs = new CustomMusic(pi, data, log) { KeepUsage = pictures.KeepUsage };
        runtime.PrepareSongs = Songs.Prepare;
        runtime.SongPlayable = Songs.Playable;
        runtime.SongWanted = SongWanted;
    }

    // A guest who declined the home's custom content hears its songs only if this PC has them anyway.
    private bool SongWanted(string hash) => active == null || OwnsActiveHouse || Songs.Has(hash) || !consent.TryGetValue(active.Id, out var load) || load;

    /// <summary>The active home's custom songs.</summary>
    public IReadOnlyList<string> HomeSongs => songHashes;

    // Each frame: songs that failed to convert or prepare are reported once; the owner uploads the home's songs while
    // hosting, and a guest who agreed fetches them.
    private void UpdateCustomMusic()
    {
        while (Songs.Failures.TryDequeue(out var message)) Failed?.Invoke(message);
        if (!songsTidied && allHouses.Count > 0 && active == null) { songsTidied = true; TidySongs(); }
        if (!ReferenceEquals(active?.Music, songMusic))
        {
            songMusic = active?.Music;
            songHashes = active?.Music.Playlist.Where(t => t.IsCustom).Select(t => t.CustomMusic!).Distinct().ToArray() ?? [];
            if (active?.Id != songHome) { songHome = active?.Id; songRetries.Clear(); }
        }
        var relay = engine.HousingRelay;
        if (songHashes.Length == 0 || active == null || !IsSharedVisit || !relay.HousingSceneReady) return;
        if (relay.RoomId != songRoom) { songRoom = relay.RoomId; songsUploaded.Clear(); songRetries.Clear(); }
        var owner = OwnsActiveHouse;
        foreach (var hash in songHashes)
        {
            if (songsWorking.Contains(hash) || songRetries.TryGetValue(hash, out var retry) && retry.Next > DateTimeOffset.UtcNow) continue;
            if (owner ? !AgreementAccepted || songsUploaded.Contains(hash) || !Songs.Has(hash) : !CustomPicturesLoaded || Songs.Has(hash)) continue;
            songsWorking.Add(hash);
            var origin = relay.ContentOrigin;
            _ = Task.Run(async () =>
            {
                try
                {
                    // A few transfers at a time: a home can list dozens of packages, each held whole while it's checked.
                    await TransferSlots.WaitAsync();
                    if (owner) await Songs.Upload(relay.RequestContentTicket, origin ?? throw new InvalidOperationException("The relay's address is unknown."), hash, CancellationToken.None);
                    else await Songs.Download(relay, hash);
                    Run(() => { songsWorking.Remove(hash); songRetries.Remove(hash); contentBroken.Remove(hash); if (owner) songsUploaded.Add(hash); });
                }
                catch (Exception ex)
                {
                    var quiet = ex is PictureNotReadyException;
                    if (!quiet) log.Warning(ex, "Haven could not {Action} custom song {Hash}.", owner ? "upload" : "download", hash[..12]);
                    Run(() =>
                    {
                        songsWorking.Remove(hash);
                        if (!quiet) contentBroken.Add(hash);
                        var attempts = songRetries.GetValueOrDefault(hash).Attempts + 1;
                        // Not on the relay yet (moments behind the owner): ask again soon. Otherwise back off, to two minutes.
                        // A guest's download that failed outright stops there: the player is told and chooses to try again or give up.
                        var stopped = !quiet && !owner;
                        songRetries[hash] = (attempts, stopped ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddSeconds(quiet ? 3 : Math.Min(120, 3 * Math.Pow(2, attempts))));
                        if (stopped) DownloadFailed(hash, ContentKind.Song, ex.Message);
                        else if (!quiet && attempts == 1) Failed?.Invoke("A custom song could not be uploaded: " + ex.Message);
                    });
                }
                finally { TransferSlots.Release(); }
            });
        }
    }

    // Once a session, outside any home: songs fetched for other people's homes that no home on this PC
    // plays are deleted; they are fetched again when needed.
    private void TidySongs()
    {
        var playing = allHouses.SelectMany(h => h.Music.Playlist).Where(t => t.IsCustom).Select(t => t.CustomMusic!).ToHashSet();
        foreach (var hash in Songs.Cached().Where(h => !playing.Contains(h))) Songs.Forget(hash);
    }

    /// <summary>Converts a song the player chose (off the framework thread) and adds it to their library.</summary>
    public void ImportSong(string file, string title)
    {
        if (songImport != null) throw new InvalidOperationException("A song is already being added. Wait for it to finish.");
        var cancel = songImport = new CancellationTokenSource();
        SongImport = (title, 0);
        Status = $"Converting {title}…";
        _ = Songs.Import(file, title, new SyncProgress(p => SongImport = (title, p)), cancel.Token).ContinueWith(task => Run(() =>
        {
            songImport = null; SongImport = null; cancel.Dispose();
            if (task.IsCanceled) { Status = "Adding the song was cancelled."; return; }
            if (task.IsFaulted) throw new InvalidOperationException("The song couldn't be added: " + task.Exception!.GetBaseException().Message, task.Exception);
            Status = $"Added {Songs.Song(task.Result)?.Title ?? title} to your music.";
        }));
    }
    public void CancelSongImport() => songImport?.Cancel();

    /// <summary>
    /// Adds one of the player's songs to the end of the playlist. In the owner's home it is put on the locker at once
    /// (a content session), so guests can hear it whenever they visit; that needs the content agreement.
    /// </summary>
    public void AddSongToPlaylist(string hash)
    {
        if (active == null || Songs.Song(hash) is not { } song) return;
        if (active.Music.Playlist.Length >= MusicPanelUi.MaxTracks) throw new InvalidOperationException("The playlist is full.");
        if (OwnsActiveHouse) RequireAgreement();
        SetMusic(active.Music with { Playlist = [.. active.Music.Playlist, new MusicTrack { CustomMusic = hash, Title = song.Title, Milliseconds = song.Milliseconds }] });
        if (OwnsActiveHouse && song.Stored == null) PublishSong(hash);
    }

    // Uploads a song through a content session.
    private void PublishSong(string hash)
    {
        var relayUrl = engine.HousingRelayUrl; var character = engine.HousingCharacterId;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var session = await ContentSessionClient.Open(relayUrl, character, pictures.SecretFor(character), CancellationToken.None, o => Account.Apply(o, character, ""));
                await Songs.Upload(session.Request, session.Origin, hash, CancellationToken.None);
                Run(() => Status = "Song uploaded for your guests.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or TaskCanceledException or System.Net.WebSockets.WebSocketException)
            {
                log.Warning(ex, "Haven could not upload custom song {Hash}.", hash[..12]);
                // A gathering's own upload tries again.
                Run(() => throw new InvalidOperationException("The song is in your playlist but not uploaded yet: " + ex.Message, ex));
            }
        });
    }

    /// <summary>Renames one of the player's songs, and its entries in the open home's playlist.</summary>
    public void RenameSong(string hash, string title)
    {
        Songs.Rename(hash, title);
        if (active == null || Songs.Song(hash) is not { } song || !active.Music.Playlist.Any(t => t.CustomMusic == hash)) return;
        SetMusic(active.Music with { Playlist = [.. active.Music.Playlist.Select(t => t.CustomMusic == hash ? t with { Title = song.Title } : t)] });
    }

    // The homes on this PC that play a song: the one open now as it stands, and every other saved home.
    private string[] HomesPlaying(string hash) => allHouses.Where(h => h.Id != active?.Id).Append(active).OfType<House>()
        .Where(h => h.Music.Playlist.Any(t => t.CustomMusic == hash)).Select(h => h.Name).Distinct().ToArray();
    public string[] SongHomes(string hash) => HomesPlaying(hash);

    /// <summary>
    /// Deletes a song from this PC, refused while a home here still plays it. Asked to, it is deleted from Haven's
    /// server at once too (not a song taken from the former community gallery, which isn't the player's to delete
    /// there); otherwise the server copy expires after 30 days unused.
    /// </summary>
    public void DeleteSong(string hash, bool fromServer = false)
    {
        var homes = HomesPlaying(hash);
        if (homes.Length > 0) throw new InvalidOperationException($"This song is still in the playlist of {string.Join(", ", homes)}. Remove it there first.");
        if (Songs.Song(hash) is not { } song) return;
        if (fromServer && song.Stored != null && !song.FromCommunity)
        {
            var relayUrl = engine.HousingRelayUrl; var character = engine.HousingCharacterId;
            Status = "Deleting the song from Haven's server…";
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var session = await ContentSessionClient.Open(relayUrl, character, pictures.SecretFor(character), CancellationToken.None, o => Account.Apply(o, character, ""));
                    await Songs.DeleteFromServer(session.Request, session.Origin, hash, CancellationToken.None);
                    Run(() => { Songs.Delete(hash); Status = "Song deleted from this PC and Haven's server."; });
                }
                // Not reachable now: deleted here, and from the server later.
                catch (Exception ex) when (Retryable(ex))
                { Run(() => { Songs.Delete(hash); QueueServerDelete(ServerContent.Song, hash, character, ex); Status = "Song deleted. Haven deletes its copy on the server as soon as it can."; }); }
                catch (NotUploaderException ex) { Run(() => throw new InvalidOperationException("The song could not be deleted from Haven's server, so it was kept: " + ex.Message, ex)); }
            });
            return;
        }
        Songs.Delete(hash);
        Status = "Song deleted.";
    }

    // Progress reported on the converting thread itself: Progress<T> would post to a context the plugin doesn't have.
    private sealed class SyncProgress(Action<float> report) : IProgress<float> { public void Report(float value) => report(value); }
}
