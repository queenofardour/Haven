using System.Collections.Concurrent;
using Haven.Core;

namespace HavenNative.Sync;

// Content locker tickets (Haven.Core ContentLocker). The relay answers each request with a signed path on its own
// HTTPS origin, or an error; file data never travels over this connection.
public partial class RelaySyncService
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ContentTicket>> contentTickets = new();
    /// <summary>The character's content secret (ContentLocker.OwnerKey), sent with a join; set by Haven's pictures.</summary>
    public Func<ulong, string>? ContentSecretFor { get; set; }

    // The relay's HTTPS origin, where the Worker in front of the locker answers ticket paths.
    public string? ContentOrigin => Uri.TryCreate(ConnectedUrl, UriKind.Absolute, out var url)
        ? $"{(url.Scheme == "ws" ? "http" : "https")}://{url.Authority}" : null;

    public async Task<ContentTicket> RequestContentTicket(ContentTicketRequest request, CancellationToken token)
    {
        var socket = ws;
        if (!IsConnected || socket == null) return new(request.Kind, request.Hash, Error: "Not connected to a gathering.");
        var key = request.Kind + ":" + request.Hash;
        var pending = contentTickets.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await SendFrame(ContentWire.TicketRequest, ContentWire.Encode(request), socket);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await pending.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(request.Kind, request.Hash, Error: "The relay did not answer. It may need updating for custom pictures."); }
        finally { contentTickets.TryRemove(key, out _); }
    }

    private void ReceiveContentTicket(byte[] payload)
    {
        var ticket = ContentWire.DecodeTicket(payload);
        if (contentTickets.TryGetValue(ticket.Kind + ":" + ticket.Hash, out var pending)) pending.TrySetResult(ticket);
    }
}
