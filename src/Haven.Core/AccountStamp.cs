using System.Security.Cryptography;
using System.Text;

namespace Haven.Core;

/// <summary>A connection the edge verified: its character, the Discord account it belongs to, and its bound name.</summary>
public sealed record VerifiedAccount(ulong ContentId, string Account, string Name);

/// <summary>
/// Haven accounts (Discord sign-in). The relay container has no internet, so accounts are checked at Cloudflare's
/// edge: the Worker reads the connecting client's account key and character, ties the character to that account the
/// first time it appears, refuses it for any other account, and only then forwards the connection, stamped with the
/// verified character, its account and the name it was first tied with (<see cref="Header"/>). The stamp is an HMAC
/// under the relay's recovery key, which both hold; the relay trusts only a stamp it can check, refuses a join or
/// content session for any other character, names the character by the stamp's name, and limits connections and
/// gatherings per account.
/// </summary>
public static class AccountStamp
{
    public const string Header = "X-Haven-Verified";
    // Headers the client sends at the edge: its account key and the character it is playing (the name binds once).
    public const string KeyHeader = "Authorization", CharacterHeader = "X-Haven-Character", NameHeader = "X-Haven-Character-Name";
    /// <summary>How long a stamp is accepted after the edge made it; it is checked once, as the connection opens.</summary>
    public const long LifetimeMs = 60_000;
    private const int MaxName = 40, MaxAccount = 20;

    public static string Canonical(ulong contentId, long expires, string account, string name) => $"verified2\n{contentId}\n{expires}\n{account}\n{name}";

    public static string Sign(string key, ulong contentId, long expires, string account, string name)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(Canonical(contentId, expires, account, name)));
        return $"{contentId}.{expires}.{account}.{Url(Encoding.UTF8.GetBytes(name))}.{Url(mac)}";
    }

    /// <summary>The verified connection, or null when the stamp is missing, malformed, expired or not signed with key.</summary>
    public static VerifiedAccount? Read(string? stamp, string? key, long nowMs)
    {
        if (string.IsNullOrEmpty(stamp) || string.IsNullOrEmpty(key) || stamp.Length > 400) return null;
        var parts = stamp.Split('.');
        if (parts.Length != 5 || !ulong.TryParse(parts[0], out var contentId) || contentId == 0 || !long.TryParse(parts[1], out var expires)) return null;
        if (expires < nowMs || expires > nowMs + 2 * LifetimeMs) return null;
        var account = parts[2];
        if (account.Length is 0 or > MaxAccount || !account.All(char.IsAsciiDigit)) return null;
        string name;
        try { name = new UTF8Encoding(false, true).GetString(FromUrl(parts[3])); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return null; }
        if (name.Length > MaxName || name.Any(char.IsControl)) return null;
        var expected = Encoding.ASCII.GetBytes(Sign(key, contentId, expires, account, name));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(stamp)) ? new(contentId, account, name) : null;
    }

    private static string Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromUrl(string text)
    {
        if (text.Length > 80) throw new FormatException("Name too long.");
        var b64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64 + new string('=', (4 - b64.Length % 4) % 4));
    }
}
