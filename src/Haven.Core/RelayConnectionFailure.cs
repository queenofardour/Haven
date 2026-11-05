using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Haven.Core;

public static class RelayConnectionFailure
{
    public const string HostedEndpoint = "wss://haven-relay.eevicat.workers.dev";

    public static string Describe(string endpoint, Exception error, string? refusal = null, int refusedStatus = 0)
    {
        // An account refusal from the hosted relay's edge (accounts.ts) explains itself.
        if (refusedStatus is 401 or 403 && AccountSignIn.Refusal(refusal, refusedStatus) is { } account) return account;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
            return "Invalid relay address. Use a ws:// or wss:// address in Visits.";
        var server = uri.Authority; // No user info, query parameters or visit credentials.
        if (uri.IsLoopback)
            return $"Cannot connect to local relay {server}. Localhost points to this PC; use the shared relay address in Visits.";
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
        {
            if (cause is SocketException socket)
            {
                if (socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
                    return $"Cannot resolve {server}. Check the relay address and DNS connection.";
                if (socket.SocketErrorCode == SocketError.ConnectionRefused)
                    return $"Connection refused by {server}. Check the relay address and port.";
                if (socket.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable)
                    return $"Cannot reach {server} from this network.";
            }
            var status = cause is HttpRequestException { StatusCode: {} code } ? (int)code : 0;
            if (status == 0 && Regex.Match(cause.Message, @"status code\s*'?(\d{3})", RegexOptions.IgnoreCase) is { Success: true } match)
                status = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (status is 502 or 503 or 504)
                return $"Relay {server} returned HTTP {status}. It may still be starting; retry shortly.";
            if (status is 400 or 404 or 426)
                return $"WebSocket upgrade failed at {server} (HTTP {status}). Use the relay address itself, without /health.";
            if (status != 0) return $"WebSocket connection to {server} was rejected (HTTP {status}). Check the relay address and network access.";
        }
        if (error is OperationCanceledException)
            return $"Connection to {server} timed out. Retry shortly; if it persists, check network access to WebSockets.";
        return $"Could not open a WebSocket connection to {server}. The Dalamud log contains the detailed error.";
    }
}
