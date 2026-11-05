using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Haven.Core;

namespace Haven;

/// <summary>
/// Puts a furniture-kind package (a custom furnishing or pose animations) on the content locker, unless it is there
/// already: in one request up to ContentLocker.PartBytes, otherwise in parts (the Worker's multipart upload; Cloudflare
/// takes at most 100 MB a request). Each part is tried three times, and the ticket is asked for again before it runs
/// out, so a slow connection can take as long as it needs; an upload given up is abandoned on the server, which gives
/// its charge back.
/// </summary>
internal static class LockerUpload
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(4);
    private const int Tries = 3;
    /// <summary>The wait before a part is tried again, doubling (the checks shorten it).</summary>
    internal static TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private sealed record Started(string? Upload, int PartBytes, string? Outcome);
    private sealed record Part(int PartNumber, string Etag);

    public static async Task Send(HttpClient http, AskTicket ask, string origin, string hash, byte[] zip, Action<HttpResponseMessage>? keepUsage,
        Func<HttpResponseMessage, Task<string>> describe, CancellationToken token)
    {
        ContentTicket ticket = null!;
        var issued = DateTimeOffset.MinValue;
        async Task<string> Url(string extra = "")
        {
            if (DateTimeOffset.UtcNow - issued > Fresh)
            {
                ticket = await ask(new("upload", hash, zip.Length, ContentLocker.AgreementVersion, Media: ContentLocker.FurnitureMedia), token);
                if (ticket.Error == ContentLocker.NotInHome) throw new PictureNotReadyException(ticket.Error);
                if (ticket.Error != null || ticket.Path == null) throw new InvalidOperationException(ticket.Error ?? "The relay gave no ticket.");
                issued = DateTimeOffset.UtcNow;
            }
            return ticket.Url(origin, hash).AbsoluteUri + extra;
        }

        using (var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, await Url()), token))
        {
            keepUsage?.Invoke(head);
            if (head.IsSuccessStatusCode) return;
            if (head.StatusCode != HttpStatusCode.NotFound) throw new InvalidOperationException(await describe(head));
        }
        if (zip.Length <= ContentLocker.PartBytes)
        {
            using var content = Zip(zip, 0, zip.Length);
            using var put = await http.PutAsync(await Url(), content, token);
            keepUsage?.Invoke(put);
            if (!put.IsSuccessStatusCode) throw new InvalidOperationException(await describe(put));
            return;
        }

        Started started;
        using (var start = await http.PostAsync(await Url("&part=start"), null, token))
        {
            keepUsage?.Invoke(start);
            if (!start.IsSuccessStatusCode) throw new InvalidOperationException(await describe(start));
            started = JsonSerializer.Deserialize<Started>(await start.Content.ReadAsStringAsync(token), Json) ?? throw new InvalidDataException("The server's answer was empty.");
        }
        if (started.Outcome == "stored") return;
        if (started.Upload is not { Length: > 0 } upload || started.PartBytes < 5 * 1024 * 1024) throw new InvalidDataException("The server's answer was not understood.");
        var id = "&upload=" + Uri.EscapeDataString(upload);
        try
        {
            var count = (zip.Length + started.PartBytes - 1) / started.PartBytes;
            var parts = new List<Part>(count);
            for (var n = 1; n <= count; n++)
            {
                var offset = (n - 1) * started.PartBytes;
                var length = Math.Min(started.PartBytes, zip.Length - offset);
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        using var content = Zip(zip, offset, length);
                        using var put = await http.PutAsync(await Url($"&part={n}{id}"), content, token);
                        if (!put.IsSuccessStatusCode) throw new InvalidOperationException(await describe(put));
                        var part = JsonSerializer.Deserialize<Part>(await put.Content.ReadAsStringAsync(token), Json);
                        if (part is not { Etag.Length: > 0 } || part.PartNumber != n) throw new InvalidDataException("The server's answer was not understood.");
                        parts.Add(part);
                        break;
                    }
                    catch (Exception) when (attempt < Tries && !token.IsCancellationRequested)
                    {
                        await Task.Delay(RetryDelay * attempt, token);
                    }
                }
            }
            using var list = new StringContent(JsonSerializer.Serialize(new { parts }, Json), Encoding.UTF8, "application/json");
            using var done = await http.PostAsync(await Url("&part=done" + id), list, token);
            keepUsage?.Invoke(done);
            // A refused finish has already been abandoned on the server.
            if (!done.IsSuccessStatusCode) { upload = ""; throw new InvalidOperationException(await describe(done)); }
            upload = "";
        }
        finally
        {
            if (upload.Length > 0)
                try
                {
                    using var abort = await http.PostAsync(await Url("&part=abort" + id), null, CancellationToken.None);
                    keepUsage?.Invoke(abort);
                }
                catch (Exception) { }
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static ByteArrayContent Zip(byte[] zip, int offset, int length)
    {
        var content = new ByteArrayContent(zip, offset, length);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return content;
    }
}
