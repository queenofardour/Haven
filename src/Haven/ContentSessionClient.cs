using System.Net.WebSockets;
using Haven.Core;
using HavenNative.Wire;

namespace Haven;

// A content session with the relay (Haven.Core ContentSession): a short connection with no room, in which the owner's
// Haven gets tickets to upload and share their own pictures while not hosting. One request at a time.
internal sealed class ContentSessionClient : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private ContentSessionClient(string origin) => Origin = origin;
    /// <summary>The relay's HTTPS origin, where ticket paths are answered.</summary>
    public string Origin { get; }

    public static string? OriginOf(string relayUrl) => Uri.TryCreate(relayUrl, UriKind.Absolute, out var url) && url.Scheme is "ws" or "wss"
        ? $"{(url.Scheme == "ws" ? "http" : "https")}://{url.Authority}" : null;

    public static async Task<ContentSessionClient> Open(string relayUrl, ulong contentId, string secret, CancellationToken token,
        Action<System.Net.WebSockets.ClientWebSocketOptions>? prepare = null)
    {
        if (OriginOf(relayUrl) is not {} origin) throw new InvalidOperationException("Set a relay under Settings → Connection to upload your own content.");
        if (contentId == 0) throw new InvalidOperationException("Log in to a character to upload your own content.");
        var client = new ContentSessionClient(origin);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            prepare?.Invoke(client.socket.Options);
            try { await client.socket.ConnectAsync(new Uri(relayUrl), timeout.Token); }
            catch (System.Net.WebSockets.WebSocketException) when ((int)client.socket.HttpStatusCode is 401 or 403)
            {
                IEnumerable<string>? codes = null;
                client.socket.HttpResponseHeaders?.TryGetValue("X-Haven-Refusal", out codes);
                throw new InvalidOperationException(AccountSignIn.Refusal(codes?.FirstOrDefault(), (int)client.socket.HttpStatusCode)
                    ?? "The Haven relay refused this character. Sign in again in Settings → Connection.");
            }
            await client.Send(ContentWire.Session, ContentWire.Encode(new ContentSession(contentId, HousingWire.ProtocolRevision, secret)), timeout.Token);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public async Task<ContentTicket> Request(ContentTicketRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await Send(ContentWire.TicketRequest, ContentWire.Encode(request), timeout.Token);
            var buffer = new byte[4096];
            while (true)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return new(request.Kind, request.Hash, Error: "The relay closed the connection.");
                    frame.Write(buffer, 0, result.Count);
                    if (frame.Length > 16 * 1024) throw new InvalidDataException("The relay sent an oversized answer.");
                } while (!result.EndOfMessage);
                var parsed = FrameHeader.ParseDownleg(frame.ToArray());
                if (!parsed.Ok || parsed.Kind != ContentWire.Ticket) continue;
                var ticket = ContentWire.DecodeTicket(parsed.Payload);
                if (ticket.Kind == "" || ticket.Kind == request.Kind && ticket.Hash == request.Hash) return ticket;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(request.Kind, request.Hash, Error: "The relay did not answer. It may need updating."); }
        catch (WebSocketException) { return new(request.Kind, request.Hash, Error: "The connection to the relay was lost."); }
    }

    private Task Send(byte kind, byte[] payload, CancellationToken token) =>
        socket.SendAsync(FrameHeader.BuildUpleg(kind, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payload), WebSocketMessageType.Binary, true, token);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", closing.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        socket.Dispose();
    }
}
