using System.Text.Json;
using Haven.Core;

namespace Haven;

// Custom pictures in frames (CustomPictures, Haven.Core ContentLocker). A frame names an original and its framing; the
// original is uploaded once, and every PC builds the frame's picture from it, so adjusting a frame sends only its
// framing with the home. Only a home's owner adds originals, after accepting the content agreement once; decorators
// may frame the home's own pictures for other frames. A guest is asked on entering a home with custom pictures whether
// to load them, remembered per home and changeable in the Visit panel, where any single picture can also be hidden for
// this PC alone. The owner's Haven uploads an original as soon as it is placed (a content session) and again whenever it
// hosts; a guest who agreed downloads each original once. The relay itself never carries picture data.
internal sealed partial class HousingController
{
    private CustomPictures pictures = null!;
    private string agreementPath = "", consentPath = "", hiddenPath = "";
    private HashSet<string> hidden = [];
    private int agreementVersion;
    private Dictionary<Guid, bool> consent = [];
    private readonly Dictionary<string, DateTimeOffset> uploads = [];
    private readonly HashSet<string> shared = [];
    private string sharedRoom = "";
    private House? pictureHouse;
    private string[] pictureHashes = [];
    private (string, PictureLayout?, bool)[] pictureFrames = [];
    private bool carriedOver;

    /// <summary>This PC's Haven account (Discord sign-in), presented on every relay connection.</summary>
    public HavenAccount Account { get; private set; } = null!;

    private void InitialiseCustomPictures(Dalamud.Plugin.IDalamudPluginInterface pi, Dalamud.Plugin.Services.IDataManager data)
    {
        pictures = new CustomPictures(pi, data, log);
        engine.HousingContentSecret = pictures.SecretFor;
        Account = new HavenAccount(pi.ConfigDirectory.FullName, log);
        engine.HousingPrepareConnection = Account.Apply;
        Catalogue.CustomPictureMaterial = ShownMaterial;
        agreementPath = Path.Combine(pi.ConfigDirectory.FullName, "content-agreement.json");
        consentPath = Path.Combine(pi.ConfigDirectory.FullName, "content-consent.json");
        hiddenPath = Path.Combine(pi.ConfigDirectory.FullName, "content-hidden.json");
        try { if (File.Exists(agreementPath)) agreementVersion = JsonSerializer.Deserialize<int>(File.ReadAllText(agreementPath)); }
        catch (Exception ex) when (ex is IOException or JsonException) { log.Warning(ex, "The custom content agreement record could not be read."); }
        try { if (File.Exists(consentPath)) consent = JsonSerializer.Deserialize<Dictionary<Guid, bool>>(File.ReadAllText(consentPath)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException) { log.Warning(ex, "Custom picture choices could not be read."); }
        try { if (File.Exists(hiddenPath)) hidden = (JsonSerializer.Deserialize<string[]>(File.ReadAllText(hiddenPath)) ?? []).Where(ContentLocker.IsFingerprint).ToHashSet(); }
        catch (Exception ex) when (ex is IOException or JsonException) { log.Warning(ex, "Hidden custom pictures could not be read."); }
    }

    // A frame shows its picture once built, unless this PC declined the home's pictures or hid that one.
    private string? ShownMaterial(Furnishing item) => item.CustomPicture is not {} original || !CustomPicturesLoaded || hidden.Contains(original) && !OwnsActiveHouse
        ? null : pictures.Material(PictureCanvas.FrameKey(original, item.Framing, item.Portrait));
    public bool CustomPicturesLoaded => active != null && (OwnsActiveHouse || consent.GetValueOrDefault(active.Id));
    public bool PictureHidden(string hash) => hidden.Contains(hash);
    public void HidePicture(string hash, bool hide)
    {
        if (!ContentLocker.IsFingerprint(hash) || !(hide ? hidden.Add(hash) : hidden.Remove(hash))) return;
        try { WriteAtomically(hiddenPath, JsonSerializer.Serialize(hidden.Order().ToArray())); }
        catch (IOException ex) { log.Warning(ex, "Hidden custom pictures could not be saved."); }
    }

    // Content uploads and downloads running at once (furniture, animation and song packages), across the three kinds.
    private static readonly SemaphoreSlim TransferSlots = new(3);
    public bool AgreementAccepted => agreementVersion >= ContentLocker.AgreementVersion;
    public void AcceptAgreement()
    {
        agreementVersion = ContentLocker.AgreementVersion;
        WriteAtomically(agreementPath, JsonSerializer.Serialize(agreementVersion));
    }
    // A temporary file, then a move over the old one: a crash mid-write keeps the last choices instead of an empty file.
    private static void WriteAtomically(string path, string json)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    // The owner adds pictures; anyone who may decorate frames the home's pictures, which needs nothing uploaded.
    public bool CanAddCustomPictures => CanFurnish && OwnsActiveHouse;
    public IReadOnlyList<string> HomePictures => pictureHashes;
    public bool CustomPictureReady(Furnishing item) => ShownMaterial(item) != null;
    public bool CustomPicturesNeedPenumbra => pictures.PenumbraMissing;
    public PictureThumb? PictureThumbnail(string hash) => pictures.Thumbnail(hash);
    public bool CanReframe(Furnishing item) => CanFurnish && item.CustomPicture is {} original && pictures.HasOriginal(original);

    // Asked once per home: a guest in someone else's gathering whose home shows custom pictures.
    public bool CustomConsentPending => active != null && IsSharedVisit && !OwnsActiveHouse && IsReady
        && (pictureHashes.Length > 0 || songHashes.Length > 0 || furnitureHashes.Length > 0 || HomeAnimations > 0) && !consent.ContainsKey(active.Id);
    public bool CustomConsentPictures => pictureHashes.Length > 0;
    public bool CustomConsentSongs => songHashes.Length > 0;
    public bool CustomConsentFurniture => furnitureHashes.Length > 0;
    public bool CustomConsentAnimations => HomeAnimations > 0;
    // The animation packages the home's pose sets play (custom content like the rest: a guest downloads them only
    // after agreeing), counted again only when the home changes.
    private House? animationCountHouse;
    private int animationCount;
    public int HomeAnimations
    {
        get
        {
            if (!ReferenceEquals(active, animationCountHouse)) { animationCountHouse = active; animationCount = active == null ? 0 : PoseSets.Packages(active).Count(); }
            return animationCount;
        }
    }
    public string CustomConsentHome => active?.Name ?? "This home";
    public void DecideCustomPictures(bool load)
    {
        if (active == null) return;
        consent[active.Id] = load;
        try { WriteAtomically(consentPath, JsonSerializer.Serialize(consent)); }
        catch (IOException ex) { log.Warning(ex, "Custom picture choices could not be saved."); }
    }

    /// <summary>The pictures a frame may show: the owner's originals; for a decorator, the home's own pictures this PC has.</summary>
    public IReadOnlyList<string> PictureOriginals() => OwnsActiveHouse ? pictures.Originals() : pictureHashes.Where(pictures.HasOriginal).ToArray();

    /// <summary>The owner's chosen image file, kept as an original and decoded for framing, off the game thread.</summary>
    public Task<(string Original, PictureSource Source, PictureLayout? Layout)> ImportOriginal(string path)
    {
        RequireUpload();
        return Task.Run(() =>
        {
            var original = pictures.AddOriginal(CustomPictures.LoadSource(path));
            return (original, pictures.LoadOriginal(original), (PictureLayout?)null);
        });
    }

    /// <summary>An original to frame for a frame, with the framing that frame already has of it, if any.</summary>
    public Task<(string Original, PictureSource Source, PictureLayout? Layout)> FrameOriginal(string original, Furnishing item)
    {
        if (!CanFurnish) throw new InvalidOperationException("You need decorating access to frame pictures.");
        if (!OwnsActiveHouse && !pictureHashes.Contains(original)) throw new InvalidOperationException("Only the home's owner can add new custom pictures.");
        if (OwnsActiveHouse) RequireAgreement();
        var layout = item.CustomPicture == original ? item.Framing : null;
        return Task.Run(() => (original, pictures.LoadOriginal(original), layout));
    }

    public OriginalInfo? OriginalOf(string hash) => pictures.OriginalDetails(hash);
    // The owner's own originals, wherever they are (Settings → Custom content).
    public IReadOnlyList<string> MyPictures() => pictures.Originals();
    public long OriginalSize(string original) => pictures.OriginalSize(original);

    /// <summary>
    /// A frame's new picture or framing. Every PC builds the frame from its original; the owner's Haven makes sure the
    /// original is on the locker (a content session, so this works alone too). A decorator's framing needs nothing
    /// uploaded.
    /// </summary>
    public void PlaceCustomPicture(Guid furnishing, string original, PictureLayout layout)
    {
        SetCustomPicture(furnishing, original, layout);
        log.Information("[PICTURE] framed original {Original} on {Furnishing}.", original[..12], furnishing);
        if (!OwnsActiveHouse) return;
        RequireAgreement();
        // Always made sure of: the locker may have been cleared or the picture expired since this PC last uploaded it.
        var relayUrl = engine.HousingRelayUrl; var character = engine.HousingCharacterId;
        _ = Task.Run(() => Publish(relayUrl, character, original));
    }

    // Uploads an original through a content session.
    private async Task Publish(string relayUrl, ulong character, string hash)
    {
        try
        {
            await using var session = await ContentSessionClient.Open(relayUrl, character, pictures.SecretFor(character), CancellationToken.None, o => Account.Apply(o, character, ""));
            await pictures.Upload(session.Request, session.Origin, hash, CancellationToken.None);
            Run(() => Status = "Picture uploaded.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or TaskCanceledException or System.Net.WebSockets.WebSocketException)
        {
            log.Warning(ex, "Haven could not upload custom picture {Hash}.", hash[..12]);
            // A gathering's own upload tries again.
            Run(() => throw new InvalidOperationException("The picture was placed but not uploaded yet: " + ex.Message, ex));
        }
    }

    /// <summary>
    /// Checks which of the owner's pictures Haven's server holds (Settings → Custom content says so), at most every ten minutes (each check uses a ticket),
    /// and with them the allowance; with no pictures, the allowance alone (CustomPictures.RefreshUsage). Nothing is
    /// uploaded; a picture missing there is uploaded again when it is next placed or hosted.
    /// </summary>
    public void CheckPicturesOnServer()
    {
        if (!AgreementAccepted || DateTimeOffset.UtcNow < nextServerCheck) return;
        nextServerCheck = DateTimeOffset.UtcNow.AddMinutes(10);
        var relayUrl = engine.HousingRelayUrl; var character = engine.HousingCharacterId; var mine = pictures.Originals().ToArray();
        _ = Task.Run(async () =>
        {
            try
            {
                await using var session = await ContentSessionClient.Open(relayUrl, character, pictures.SecretFor(character), CancellationToken.None, o => Account.Apply(o, character, ""));
                foreach (var original in mine) await pictures.OnServer(session.Request, session.Origin, original, CancellationToken.None);
                if (mine.Length == 0) await pictures.RefreshUsage(session.Request, session.Origin, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or TaskCanceledException or System.Net.WebSockets.WebSocketException)
            { log.Warning(ex, "Haven could not check which pictures are on its server."); }
        });
    }
    private DateTimeOffset nextServerCheck;


    // For Settings → Custom content.
    public PictureUsage? PictureUsage => pictures.Usage;
    public PictureDetails Details(string original) => pictures.OriginalDetails(original) is var info
        ? new(pictures.OriginalSize(original), info?.Width ?? 0, info?.Height ?? 0, info?.AddedAt ?? default,
            pictures.IsFromCommunity(original), pictures.LastStored(original), HomesShowing(original))
        : throw new InvalidOperationException();

    private void RequireUpload()
    {
        if (!CanAddCustomPictures) throw new InvalidOperationException("Only the home's owner can add custom pictures.");
        RequireAgreement();
    }
    private void RequireAgreement()
    {
        if (!AgreementAccepted) throw new InvalidOperationException("Accept the custom content agreement first.");
    }

    // The homes on this PC that show an original: the one open now as it stands (with any change not yet published),
    // and every other saved home.
    private string[] HomesShowing(string original) => allHouses.Where(h => h.Id != active?.Id).Append(active).OfType<House>()
        .Where(h => h.Furnishings.Any(f => f.CustomPicture == original)).Select(h => h.Name).Distinct().ToArray();

    /// <summary>Deletes every original of the owner's that no home on this PC shows.</summary>
    public void DeleteUnusedPictures()
    {
        var unused = pictures.Originals().Where(o => HomesShowing(o).Length == 0).ToArray();
        foreach (var original in unused) pictures.DeleteOriginal(original);
        Status = unused.Length == 1 ? "Deleted 1 unused picture." : $"Deleted {unused.Length} unused pictures.";
    }

    /// <summary>Deletes an original. Refused while a home on this PC shows it, since every frame is built from it.</summary>
    public void DeleteOriginal(string original, bool fromServer = false)
    {
        if (!ContentLocker.IsFingerprint(original)) return;
        var homes = HomesShowing(original);
        if (homes.Length > 0) throw new InvalidOperationException($"This picture is still in {string.Join(", ", homes)}. Remove it from those frames first.");
        // At once from Haven's server too, when the player asks (a picture taken from the old community gallery isn't
        // theirs to delete there); otherwise the server copy expires after 30 days unused.
        if (fromServer && !pictures.IsFromCommunity(original))
        {
            var relayUrl = engine.HousingRelayUrl; var character = engine.HousingCharacterId;
            Status = "Deleting the picture from Haven's server…";
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var session = await ContentSessionClient.Open(relayUrl, character, pictures.SecretFor(character), CancellationToken.None, o => Account.Apply(o, character, ""));
                    await pictures.DeleteFromServer(session.Request, session.Origin, original, CancellationToken.None);
                    Run(() => { pictures.DeleteOriginal(original); Status = "Picture deleted from this PC and Haven's server."; });
                }
                // Not reachable now: deleted here, and from the server later.
                catch (Exception ex) when (Retryable(ex))
                { Run(() => { pictures.DeleteOriginal(original); QueueServerDelete(ServerContent.Picture, original, character, ex); Status = "Picture deleted. Haven deletes its copy on the server as soon as it can."; }); }
                catch (NotUploaderException ex) { Run(() => throw new InvalidOperationException("The picture could not be deleted from Haven's server, so it was kept: " + ex.Message, ex)); }
            });
            return;
        }
        pictures.DeleteOriginal(original);
        Status = "Picture deleted.";
    }

    /// <summary>Stands a wall picture frame upright, or lays it back on its side.</summary>
    public void SetPortrait(Guid furnishing, bool portrait)
    {
        if (active?.Furnishings.FirstOrDefault(f => f.Id == furnishing) is not {} item || item.Portrait == portrait) return;
        ChangeFurniture(item with { Portrait = portrait });
    }

    public void SetCustomPicture(Guid furnishing, string? original, PictureLayout? framing = null)
    {
        if (active?.Furnishings.FirstOrDefault(f => f.Id == furnishing) is not {} item) return;
        if (original != null && !CanAddCustomPictures && !pictureHashes.Contains(original)) throw new InvalidOperationException("Only the home's owner can add new custom pictures.");
        ChangeFurniture(item with { CustomPicture = original, Framing = original == null ? null : framing, Picture = 0, SurfaceFinish = 0 });
        Status = original == null ? "Custom picture removed." : "Custom picture placed.";
    }

    // Once, after the picture store carried 0.30.0's framed pictures over: every home on this PC whose frame shows one
    // is turned into that picture's original and framing.
    private void CarryHomesOver()
    {
        var changed = 0;
        foreach (var house in allHouses.Where(h => h.Id != active?.Id).ToArray())
        {
            var any = false;
            var furnishings = house.Furnishings.Select(f =>
            {
                if (f.CustomPicture is not {} framed || f.Framing != null || pictures.CarriedOver(framed) is not {} from) return f;
                any = true;
                return f with { CustomPicture = from.Original, Framing = from.Layout };
            }).ToArray();
            if (!any) continue;
            Store.Save(house with { Furnishings = furnishings });
            changed++;
        }
        if (changed > 0) { Refresh(); log.Information("[PICTURE] carried {Count} home(s) over to originals and framings.", changed); }
    }

    // Every frame: build the home's pictures, download a guest's agreed originals, and share the owner's while hosting.
    private void UpdateCustomPictures()
    {
        if (!carriedOver && pictures.Migrating.IsCompleted && allHouses.Count > 0) { carriedOver = true; CarryHomesOver(); }
        if (!ReferenceEquals(active, pictureHouse))
        {
            if (active?.Id != pictureHouse?.Id) pictures.ResetRetries();
            pictureHouse = active;
            var framed = active?.Furnishings.Where(f => f.CustomPicture != null).ToArray() ?? [];
            pictureHashes = framed.Select(f => f.CustomPicture!).Distinct().ToArray();
            pictureFrames = framed.Select(f => (f.CustomPicture!, f.Framing, f.Portrait)).Distinct().ToArray();
        }
        var relay = engine.HousingRelay;
        if (pictureHashes.Length > 0 && active != null)
        {
            var owner = OwnsActiveHouse;
            var agreed = owner || consent.GetValueOrDefault(active.Id);
            if (agreed) pictures.Ensure(pictureFrames, IsSharedVisit && relay.HousingSceneReady ? relay : null);
            if (owner && IsSharedVisit && relay.HousingSceneReady && AgreementAccepted)
            {
                if (relay.RoomId != sharedRoom) { sharedRoom = relay.RoomId; shared.Clear(); uploads.Clear(); }
                foreach (var hash in pictureHashes)
                {
                    if (shared.Contains(hash) || !pictures.HasOriginal(hash) || uploads.GetValueOrDefault(hash) > DateTimeOffset.UtcNow) continue;
                    uploads[hash] = DateTimeOffset.MaxValue;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await pictures.Upload(relay.RequestContentTicket, relay.ContentOrigin ?? throw new InvalidOperationException("The relay's address is unknown."), hash, CancellationToken.None);
                            Run(() => { shared.Add(hash); uploads.Remove(hash); });
                        }
                        // The relay has not received this change yet (it is moments behind the owner): ask again soon.
                        catch (PictureNotReadyException) { Run(() => uploads[hash] = DateTimeOffset.UtcNow.AddSeconds(3)); }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or TaskCanceledException)
                        {
                            log.Warning(ex, "Haven could not share custom picture {Hash}.", hash[..12]);
                            Run(() => { uploads[hash] = DateTimeOffset.UtcNow.AddMinutes(1); throw new InvalidOperationException(ex.Message, ex); });
                        }
                        // Anything else (a closed connection, an answer that doesn't parse) must not leave the picture marked
                        // as uploading, which kept it from guests for the rest of the gathering: try again in a minute.
                        catch (Exception ex)
                        {
                            log.Warning(ex, "Haven could not share custom picture {Hash}.", hash[..12]);
                            Run(() => uploads[hash] = DateTimeOffset.UtcNow.AddMinutes(1));
                        }
                    });
                }
            }
        }
        while (pictures.TakeFailure() is {} failure) Run(() => throw new InvalidOperationException(failure));
        while (pictures.TakeDownloadFailure() is { } download) DownloadFailed(download.Id, ContentKind.Picture, download.Message);
    }
}
