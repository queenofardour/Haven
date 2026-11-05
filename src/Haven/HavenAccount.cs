using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Haven.Core;

namespace Haven;

/// <summary>
/// This PC's Haven account (Discord sign-in, AccountSignIn): kept in account.json, shared by every character, and
/// presented with the character whenever Haven connects to the relay, which ties the character to the account.
/// </summary>
internal sealed class HavenAccount : IDisposable
{
    private readonly string path;
    private readonly IPluginLog log;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? signingIn;
    private HavenAccountFile? file;

    public HavenAccount(string configDirectory, IPluginLog log)
    {
        path = Path.Combine(configDirectory, "account.json");
        this.log = log;
        try { if (File.Exists(path)) file = AccountSignIn.Parse(File.ReadAllText(path)); }
        catch (IOException ex) { log.Warning(ex, "[Haven] The Haven account could not be read."); }
    }

    public bool SignedIn => file != null;
    public string Name => file?.Name ?? "";
    public bool SigningIn => signingIn != null;
    /// <summary>The code the browser's page asks the player to compare (AccountSignIn.CodeOf), while signing in.</summary>
    public string? Code => signingIn != null ? code : null;
    private string? code;
    public string? Problem { get; private set; }

    /// <summary>Opens Discord's sign-in in the browser and collects the key once the player has approved it.</summary>
    public void SignIn(string relayUrl, Action<string> openLink)
    {
        if (AccountSignIn.Origin(relayUrl) is not { } origin) { Problem = "Set a relay address first."; return; }
        Cancel();
        Problem = null;
        var verifier = AccountSignIn.NewVerifier();
        var state = AccountSignIn.StateOf(verifier);
        code = AccountSignIn.CodeOf(state);
        var cancel = signingIn = new CancellationTokenSource(AccountSignIn.Timeout);
        openLink(AccountSignIn.StartUrl(origin, state));
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    await Task.Delay(AccountSignIn.PollInterval, cancel.Token);
                    using var answer = await http.PostAsJsonAsync($"{origin}/auth/claim", new { state, verifier }, cancel.Token);
                    if ((int)answer.StatusCode == 410) { Problem = "The sign-in expired. Try again."; return; }
                    if (!answer.IsSuccessStatusCode) continue; // The relay may be waking; keep asking until the time runs out.
                    var result = await answer.Content.ReadFromJsonAsync<JsonElement>(cancel.Token);
                    if (result.GetProperty("status").GetString() != "ready") continue;
                    var key = result.GetProperty("key").GetString();
                    if (!AccountSignIn.IsKey(key)) { Problem = "Discord sign-in returned an unexpected answer. Try again."; return; }
                    Save(new HavenAccountFile(key!, SafeText.Clean(result.GetProperty("name").GetString() ?? "", 40), DateTimeOffset.UtcNow));
                    log.Information("[Haven] Signed in to Haven with Discord.");
                    return;
                }
            }
            catch (OperationCanceledException) { if (!SignedIn && signingIn == cancel) Problem = "Signing in took too long. Try again."; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
            { log.Warning(ex, "[Haven] Discord sign-in failed."); Problem = "Couldn't reach the Haven relay to finish signing in. Try again."; }
            finally { if (signingIn == cancel) signingIn = null; cancel.Dispose(); }
        });
    }

    public void Cancel() { try { signingIn?.Cancel(); } catch (ObjectDisposedException) { } signingIn = null; }

    /// <summary>Forgets this PC's key; the next gathering needs a new sign-in.</summary>
    public void SignOut()
    {
        Cancel();
        file = null; Problem = null;
        try { File.Delete(path); } catch (IOException ex) { log.Warning(ex, "[Haven] The Haven account file could not be removed."); }
    }

    // Each character's name as last seen in game, so connections opened off the framework thread (content sessions:
    // uploads, deletes, the allowance check) name it too. Before 0.60.0 they didn't, and a character first bound by one
    // stayed "(unnamed)" on the admin page until it hosted or joined a gathering.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> names = new();
    public void Note(ulong contentId, string characterName) { if (contentId != 0 && characterName.Length > 0) names[contentId] = characterName; }

    /// <summary>The headers a relay connection presents: the key and the character it plays (accounts.ts verifyConnection).</summary>
    public void Apply(ClientWebSocketOptions options, ulong contentId, string characterName)
    {
        options.CollectHttpResponseDetails = true;
        if (file == null || contentId == 0) return;
        if (characterName.Length == 0) characterName = names.GetValueOrDefault(contentId, "");
        options.SetRequestHeader(AccountStamp.KeyHeader, "Bearer " + file.Key);
        options.SetRequestHeader(AccountStamp.CharacterHeader, contentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (characterName.Length > 0) options.SetRequestHeader(AccountStamp.NameHeader, Uri.EscapeDataString(SafeText.Clean(characterName, 40)));
    }

    /// <summary>The same for a request to the Worker (gathering invitations); false when this PC isn't signed in.</summary>
    public bool Apply(HttpRequestMessage request, ulong contentId, string characterName)
    {
        if (file == null || contentId == 0) return false;
        if (characterName.Length == 0) characterName = names.GetValueOrDefault(contentId, "");
        request.Headers.TryAddWithoutValidation(AccountStamp.KeyHeader, "Bearer " + file.Key);
        request.Headers.TryAddWithoutValidation(AccountStamp.CharacterHeader, contentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (characterName.Length > 0) request.Headers.TryAddWithoutValidation(AccountStamp.NameHeader, Uri.EscapeDataString(SafeText.Clean(characterName, 40)));
        return true;
    }

    private void Save(HavenAccountFile account)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(account));
        File.Move(temporary, path, overwrite: true);
        file = account;
    }

    public void Dispose() { Cancel(); http.Dispose(); }
}
