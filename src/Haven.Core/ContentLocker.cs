using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Haven.Core;

/// <summary>
/// The content locker: custom files (version 1: a frame's custom picture) stored once in Cloudflare R2 under their
/// SHA-256 fingerprint. The relay never carries file data. It hands out short-lived signed tickets: an upload ticket
/// only to a room's owner, for a fingerprint the room's current home uses, and a download ticket only to a ready
/// member of that room. The Worker in front of R2 checks the signature; this class is the one definition of what is
/// signed, and the Worker's tests share its vectors.
/// </summary>
public static class ContentLocker
{
    public const int AgreementVersion = 1;
    public const int MaxPictureBytes = 2 * 1024 * 1024;
    /// <summary>A custom song (Ogg Vorbis, Haven.Core.Music): at most this size.</summary>
    public const int MaxMusicBytes = global::Haven.Core.Music.MusicEncoder.MaxBytes;
    /// <summary>The two kinds of custom content. Pictures are the default and sign as they always have.</summary>
    /// <summary>A custom furnishing's package (Haven.Core.Furniture): at most this size.</summary>
    public const int MaxFurnitureBytes = Furniture.FurniturePackages.MaxBytes;
    public const string Picture = "picture", Music = "music", FurnitureMedia = "furniture";
    public static bool IsMedia(string? media) => media is Picture or Music or FurnitureMedia;
    public static int MaxBytes(string media) => media switch { Music => MaxMusicBytes, FurnitureMedia => MaxFurnitureBytes, _ => MaxPictureBytes };
    /// <summary>What the player is told when something is too large for its kind.</summary>
    public static string TooLarge(string media) => media switch
    {
        Music => "Custom songs are at most 8 MB.",
        FurnitureMedia => $"Custom furniture is at most {MaxFurnitureBytes / 1_000_000} MB.",
        _ => "Custom pictures are at most 2 MB.",
    };
    public const int PictureWidth = 1024, PictureHeight = 512;
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(10);
    /// <summary>
    /// A furniture-kind package larger than this goes up in parts of this size (the Worker's limits.partBytes, R2
    /// multipart), since Cloudflare takes at most 100 MB a request; a slow connection sends each part well within a
    /// request's two minutes.
    /// </summary>
    public const int PartBytes = 16 * 1024 * 1024;

    public static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Fingerprint(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // The quota key for a room's owner: the first 16 hex digits of SHA-256 of the content ID, as HomeOwners keys
    // characters, so the raw ID never reaches the Worker or R2.
    // The relay's refusal while its copy of the home does not show a picture yet: an owner's change still on its way,
    // so the client waits and asks again rather than reporting an error.
    public const string NotInHome = "This home does not use that picture.";
    /// <summary>The answer to an older Haven asking to share with the community gallery, which closed in 0.38.0.</summary>
    public const string GalleryClosed = "Haven's community gallery has closed. Your homes and guests are unaffected.";

    // A shared original is at most this many pixels on its longest side (and, like every file, at most 2 MB).
    public const int OriginalSide = 2048;

    /// <summary>
    /// The locker's owner for a character: 16 lowercase hex digits of an HMAC, under the relay's content key, of the
    /// character's content ID and its content secret. Content IDs are public (anyone nearby can read them), so before
    /// 0.32.0 anyone could claim another character's pictures; the secret is random, kept on the character's PC and
    /// sent only to the relay.
    /// </summary>
    public static string OwnerKey(string key, ulong contentId, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"owner\n{contentId}\n{secret}")))[..16].ToLowerInvariant();

    /// <summary>A content secret: 32 to 64 base64url characters (Haven makes 43, from 32 random bytes).</summary>
    public static bool IsSecret(string? secret) => secret is { Length: >= 32 and <= 64 } && secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The signed string: method, fingerprint, size, owner, agreement version and expiry, one per line. "put" covers
    /// the upload's existence check (HEAD) and the upload (PUT); "get" a download. Size, owner and agreement are 0 or
    /// empty for "get".
    /// </summary>
    /// An upload by a character the account check verified also names it (by), so the operator can see who uploaded a
    /// picture; the line is added only then, so other tickets sign exactly as before.
    /// A song's ticket ends with the line "music" and a furnishing's with "furniture"; a picture's has no such line, so
    /// picture tickets sign as before.
    public static string Canonical(string method, string hash, long size, string owner, int agreement, long expires, ulong by = 0, string media = Picture) =>
        string.Join('\n', method, hash, size.ToString(System.Globalization.CultureInfo.InvariantCulture), owner,
            agreement.ToString(System.Globalization.CultureInfo.InvariantCulture), expires.ToString(System.Globalization.CultureInfo.InvariantCulture))
        + (by == 0 ? "" : "\n" + by.ToString(System.Globalization.CultureInfo.InvariantCulture))
        + (media == Picture ? "" : "\n" + media);

    public static string Sign(string key, string canonical) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(canonical)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The ticket URL's path and query, appended to the relay's HTTPS origin by the client.</summary>
    public static string TicketPath(string key, string method, string hash, long size, string owner, int agreement, DateTimeOffset now, ulong by = 0, string media = Picture)
    {
        var expires = (now + TicketLifetime).ToUnixTimeSeconds();
        var signature = Sign(key, Canonical(method, hash, size, owner, agreement, expires, by, media));
        return $"/content/{hash}?op={method}&size={size}&owner={Uri.EscapeDataString(owner)}&agreement={agreement}&exp={expires}"
            + (by == 0 ? "" : $"&by={by}") + (media == Picture ? "" : "&media=" + media) + $"&sig={signature}";
    }
}

/// <summary>
/// A client's request for a locker ticket (relay message 0x78). In a gathering: "upload" (the room's owner) or
/// "download". In a content session: "upload" or "delete", for the session's own character. "share" and "unshare"
/// (the community gallery, closed in 0.38.0) still decode, so an older Haven's request is refused with a message.
/// </summary>
public sealed record ContentTicketRequest(string Kind, string Hash, long Size = 0, int Agreement = 0, string Media = ContentLocker.Picture);

/// <summary>
/// Opens a content session (relay message 0x7A, sent instead of joining a room): a short connection with no room,
/// in which the owner's Haven uploads or deletes its own content while not hosting.
/// </summary>
public sealed record ContentSession(ulong ContentId, int Revision, string? Secret = null);

/// <summary>The relay's answer (0x79): a path to append to the relay's origin, or an error to show.</summary>
public sealed record ContentTicket(string Kind, string Hash, string? Path = null, string? Error = null)
{
    private static readonly System.Text.RegularExpressions.Regex PathShape =
        new(@"^/content/[0-9a-f]{64}\?[A-Za-z0-9_\-.=&%]{1,600}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The ticket's address on the relay's own origin. The path comes from the relay, so it must be exactly a locker
    /// path for this picture: "@evil.example/x" appended to "https://relay" would otherwise name another host.
    /// </summary>
    public Uri Url(string origin, string hash)
    {
        if (Path is not { } path || !ContentLocker.IsFingerprint(hash) || !PathShape.IsMatch(path) || !path.StartsWith("/content/" + hash + "?", StringComparison.Ordinal)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var root) || root.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(root.UserInfo)
            || root.AbsolutePath != "/" || !Uri.TryCreate(root, path, out var url) || url.Scheme != root.Scheme || url.Authority != root.Authority
            || !string.IsNullOrEmpty(url.UserInfo))
            throw new InvalidDataException("The relay sent an invalid content ticket.");
        return url;
    }
}

public static class ContentWire
{
    public const byte TicketRequest = 0x78, Ticket = 0x79, Session = 0x7A;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    public static ContentTicketRequest DecodeRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 512) throw new InvalidDataException("Invalid content ticket request.");
        var request = JsonSerializer.Deserialize<ContentTicketRequest>(payload, Json) ?? throw new InvalidDataException("Invalid content ticket request.");
        if (request.Kind is not ("upload" or "download" or "share" or "unshare" or "delete") || !ContentLocker.IsFingerprint(request.Hash)
            || !ContentLocker.IsMedia(request.Media) || request.Size < 0 || request.Size > ContentLocker.MaxBytes(request.Media) || request.Agreement < 0)
            throw new InvalidDataException("Invalid content ticket request.");
        return request;
    }
    public static ContentSession DecodeSession(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 256) throw new InvalidDataException("Invalid content session.");
        var session = JsonSerializer.Deserialize<ContentSession>(payload, Json) ?? throw new InvalidDataException("Invalid content session.");
        if (session.ContentId == 0) throw new InvalidDataException("Invalid content session.");
        return session;
    }
    public static ContentTicket DecodeTicket(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<ContentTicket>(payload, Json) ?? throw new InvalidDataException("Invalid content ticket.");
}
