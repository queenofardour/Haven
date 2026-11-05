using System.Text.Json;
using Dalamud.Interface;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Haven;

/// <summary>
/// Haven's own "update available" notice. Dalamud's notice leaves out plugins from custom repositories when its
/// auto-update setting is "Auto-update main repository plugins", and plugins can't trigger it; so Haven reads its own
/// repository's feed (the address it was installed from) an hour apart, and once per newer version says so, with a
/// click opening the installer's updates page. Nothing is sent but the feed request itself.
/// </summary>
internal sealed class UpdateWatch : IDisposable
{
    private readonly IDalamudPluginInterface plugin;
    private readonly IPluginLog log;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource stop = new();
    private readonly Version current;
    private string? announced;
    /// <summary>A newer version was found: its version. Raised on a worker thread.</summary>
    public event Action<Version>? Found;

    public UpdateWatch(IDalamudPluginInterface plugin, IPluginLog log)
    {
        this.plugin = plugin; this.log = log;
        var version = typeof(UpdateWatch).Assembly.GetName().Version ?? new Version(0, 0, 0);
        current = new(version.Major, version.Minor, Math.Max(0, version.Build));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Dalamud");
        if (plugin.IsDev || !Uri.TryCreate(plugin.SourceRepository, UriKind.Absolute, out var feed) || feed.Scheme != Uri.UriSchemeHttps) return;
        _ = Task.Run(() => Watch(feed, stop.Token));
    }

    private async Task Watch(Uri feed, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), token);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var response = await http.GetAsync(feed, token);
                    if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is null or < 256 * 1024
                        && Newest(await response.Content.ReadAsStringAsync(token)) is { } newest && newest > current && newest.ToString() != announced)
                    {
                        announced = newest.ToString();
                        log.Information("[Haven] Haven {Version} is available (this is {Current}).", newest, current);
                        Found?.Invoke(newest);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested)
                {
                    log.Debug("[Haven] The update feed could not be read: " + ex.Message);
                }
                await Task.Delay(TimeSpan.FromHours(1), token);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Haven's version in a Dalamud repository feed (an array of plugin manifests), or null.
    internal static Version? Newest(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in document.RootElement.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("InternalName", out var name) && name.GetString() == "Haven"
                && entry.TryGetProperty("AssemblyVersion", out var version) && Version.TryParse(version.GetString(), out var parsed))
                return new(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return null;
    }

    /// <summary>Opens Dalamud's installer on its updates page, filtered to Haven.</summary>
    public void OpenInstaller() => plugin.OpenPluginInstallerTo(PluginInstallerOpenKind.UpdateablePlugins, "Haven");

    public void Dispose() { stop.Cancel(); http.Dispose(); }
}
