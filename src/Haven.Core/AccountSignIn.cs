using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Haven.Core;

/// <summary>This PC's Haven account: the key the Worker issued on Discord sign-in, and the Discord name it belongs to.</summary>
public sealed record HavenAccountFile(string Key, string Name, DateTimeOffset SignedInAt);

/// <summary>
/// Signing in with Discord (see infra/cloudflare/src/accounts.ts). Haven makes a random verifier and opens
/// /auth/discord?state=<see cref="StateOf"/>(verifier) in the browser; once Discord has confirmed the user, Haven
/// collects the key from /auth/claim with the verifier. The key never appears in a URL. The Worker's page asks the
/// player to confirm the code Haven shows meanwhile (<see cref="CodeOf"/>), so a sign-in link someone else made can't
/// sign their Discord account in for that someone.
/// </summary>
public static class AccountSignIn
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10), PollInterval = TimeSpan.FromSeconds(2);

    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string StateOf(string verifier) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
    /// <summary>
    /// The short code Haven shows while it waits and the Worker's confirmation page names (accounts.ts signInCode):
    /// SHA-256 of "haven sign-in\n" and the state, its first six bytes each one of 32 letters and digits, as "ABC-DEF".
    /// </summary>
    public static string CodeOf(string state)
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("haven sign-in\n" + state));
        var code = new string([.. digest.Take(6).Select(b => letters[b % 32])]);
        return code[..3] + "-" + code[3..];
    }
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The relay's web origin (wss://host → https://host), or null for an invalid address.</summary>
    public static string? Origin(string relayUrl) => Uri.TryCreate(relayUrl, UriKind.Absolute, out var url) && url.Scheme is "ws" or "wss"
        ? $"{(url.Scheme == "ws" ? "http" : "https")}://{url.Authority}" : null;

    /// <summary>
    /// Whether a relay refuses players who haven't signed in: the hosted relay does (HAVEN_ACCOUNTS=required);
    /// a self-hosted relay is taken not to, and refuses with its own message if it does.
    /// </summary>
    public static bool Required(string relayUrl) => Origin(relayUrl) is { } origin && origin == Origin(RelayConnectionFailure.HostedEndpoint);

    public static string StartUrl(string origin, string state) => $"{origin}/auth/discord?state={state}";

    /// <summary>A key as the Worker issues them: 43 base64url characters.</summary>
    public static bool IsKey(string? key) => key is { Length: 43 } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static HavenAccountFile? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<HavenAccountFile>(json) is { } file && IsKey(file.Key) ? file with { Name = SafeText.Clean(file.Name, 40) } : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>What to tell the player when the edge refused a connection (its X-Haven-Refusal code, or the status).</summary>
    public static string? Refusal(string? code, int status) => code switch
    {
        "sign_in" => "Sign in with Discord in Haven's settings (Settings → Connection) to join gatherings and share your own content.",
        "signed_out" => "Your Haven sign-in has expired. Sign in with Discord again in Settings → Connection.",
        "banned" => "This Haven account can't connect. Ask the person who runs your Haven relay.",
        "taken" => "This character is tied to another Haven account. If it's yours, ask the person who runs your Haven relay to release it.",
        "too_many" => "Your Haven account has reached its character limit. Ask the person who runs your Haven relay.",
        _ => status == 401 ? "Sign in with Discord in Haven's settings (Settings → Connection) to join gatherings and share your own content." : null,
    };
}
