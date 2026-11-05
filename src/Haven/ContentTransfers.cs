using System.Collections.Concurrent;
using System.Net.Http;

namespace Haven;

/// <summary>What kind of custom content a download carries, for its wording on the download bar.</summary>
internal enum ContentKind { Picture, Song, Furniture, Animations }

/// <summary>
/// The custom content downloads under way on this PC, for the download bar and the loading screen: each download
/// reports itself from its ticket request to its last byte. Downloads run on worker threads; the bar reads a snapshot.
/// </summary>
internal static class ContentTransfers
{
    internal sealed class Transfer(ContentKind kind)
    {
        public readonly ContentKind Kind = kind;
        public long Received, Total;
    }

    private static readonly ConcurrentDictionary<string, Transfer> running = new();
    /// <summary>How long a download's body may stop arriving before it is given up.</summary>
    public const int StallSeconds = 30;
    private static int finished;

    /// <summary>A download in progress; dispose it when the download ends, whether or not it succeeded.</summary>
    public sealed class Handle : IDisposable
    {
        private readonly string key;
        private readonly Transfer transfer;
        private bool done;
        internal Handle(string key, Transfer transfer) { this.key = key; this.transfer = transfer; }

        /// <summary>
        /// Reads a response's body, counting its bytes as they arrive (the length is known from its header). The body is
        /// streamed (ResponseHeadersRead), so neither the client's buffer limit nor its timeout apply to it: the kind's
        /// limit is checked here, and a body that stops arriving for StallSeconds is given up (and retried later) rather
        /// than holding its download open for the rest of the visit.
        /// </summary>
        public async Task<byte[]> Read(HttpResponseMessage response, long limit)
        {
            var length = response.Content.Headers.ContentLength ?? 0;
            if (length > limit) throw new InvalidDataException($"The download is larger than Haven accepts ({length} bytes).");
            Interlocked.Exchange(ref transfer.Total, length);
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var body = new MemoryStream(length is > 0 and < int.MaxValue ? (int)length : 0);
            var buffer = new byte[81920];
            using var stall = new CancellationTokenSource(TimeSpan.FromSeconds(StallSeconds));
            int read;
            try
            {
                while ((read = await stream.ReadAsync(buffer, stall.Token)) > 0)
                {
                    if (body.Length + read > limit) throw new InvalidDataException("The download is larger than Haven accepts.");
                    body.Write(buffer, 0, read);
                    Interlocked.Add(ref transfer.Received, read);
                    stall.CancelAfter(TimeSpan.FromSeconds(StallSeconds));
                }
            }
            catch (OperationCanceledException) when (stall.IsCancellationRequested)
            {
                throw new TimeoutException($"The download stopped arriving for {StallSeconds} seconds.");
            }
            done = true;
            return body.ToArray();
        }

        public void Dispose()
        {
            if (!running.TryRemove(new KeyValuePair<string, Transfer>(key, transfer))) return;
            if (done) Interlocked.Increment(ref finished);
        }
    }

    /// <summary>Notes a download starting (before its ticket is asked for, so a slow answer already shows).</summary>
    public static Handle Begin(ContentKind kind, string hash)
    {
        var key = kind + ":" + hash;
        var transfer = new Transfer(kind);
        running[key] = transfer;
        return new(key, transfer);
    }

    /// <summary>
    /// The downloads now: how many run, how many finished since the bar last went quiet, and the bytes of those
    /// running (only of those whose size is known yet).
    /// </summary>
    public static ContentTransferSnapshot Snapshot()
    {
        var transfers = running.Values.ToArray();
        return new(transfers.Length, Volatile.Read(ref finished), transfers.Sum(t => Interlocked.Read(ref t.Received)),
            transfers.Sum(t => Interlocked.Read(ref t.Total)), transfers.Select(t => t.Kind).Distinct().ToArray());
    }

    /// <summary>Nothing left to fetch: the next download starts a new count.</summary>
    public static void Settle() { if (running.IsEmpty) Interlocked.Exchange(ref finished, 0); }
}

internal readonly record struct ContentTransferSnapshot(int Running, int Finished, long Received, long Total, ContentKind[] Kinds);
