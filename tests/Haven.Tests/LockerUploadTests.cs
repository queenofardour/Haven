using System.Net;
using System.Text;
using System.Text.Json;
using Haven.Core;

namespace Haven
{
    // What LockerUpload needs from the plugin (CustomPictures.cs, which needs Dalamud).
    public delegate Task<ContentTicket> AskTicket(ContentTicketRequest request, CancellationToken token);
    internal sealed class PictureNotReadyException(string message) : InvalidOperationException(message);
}

namespace Haven.Tests
{
    /// <summary>LockerUpload against a stand-in for the Worker's locker (content.ts): one request, or parts.</summary>
    internal static class LockerUploadTests
    {
        public static async Task Run(Func<string, Func<Task>, Task> check)
        {
            LockerUpload.RetryDelay = TimeSpan.Zero;
            var hash = new string('a', 64);
            Task<ContentTicket> Ask(ContentTicketRequest request, CancellationToken token) =>
                Task.FromResult(new ContentTicket("upload", hash, $"/content/{hash}?op=put&size={request.Size}&sig=x"));
            async Task<FakeLocker> Send(byte[] zip, Action<FakeLocker>? set = null)
            {
                var locker = new FakeLocker();
                set?.Invoke(locker);
                using var http = new HttpClient(locker);
                await LockerUpload.Send(http, Ask, "https://relay.example", hash, zip, null, r => Task.FromResult($"refused ({(int)r.StatusCode})"), CancellationToken.None);
                return locker;
            }
            static byte[] Zip(int size) { var b = new byte[size]; for (var i = 0; i < size; i++) b[i] = (byte)(i * 7); return b; }

            await check("A small package goes up in one request; a large one in parts, a failed part tried again", async () =>
            {
                var small = Zip(1000);
                var one = await Send(small);
                Assert(one.Stored!.SequenceEqual(small) && one.Calls.SequenceEqual(["HEAD", "PUT"]));
                var large = Zip(ContentLocker.PartBytes * 2 + 12345);
                var parts = await Send(large, l => l.Fail = (2, 1));
                Assert(parts.Stored!.SequenceEqual(large) && !parts.Aborted
                    && parts.Calls.SequenceEqual(["HEAD", "POST start", "PUT 1", "PUT 2", "PUT 2", "PUT 3", "POST done"]));
                // Already on the server: nothing is sent.
                var stored = await Send(large, l => l.AlreadyStored = true);
                Assert(stored.Stored == null && stored.Calls.SequenceEqual(["HEAD", "POST start"]));
            });

            await check("A part that keeps failing gives the upload up, abandoned on the server", async () =>
            {
                FakeLocker? seen = null;
                try { await Send(Zip(ContentLocker.PartBytes + 10), l => { l.Fail = (1, 5); seen = l; }); Assert(false); }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("refused")) { }
                Assert(seen!.Aborted && seen.Stored == null && seen.Calls.Count(c => c == "PUT 1") == 3);
            });
        }

        private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }

        private sealed class FakeLocker : HttpMessageHandler
        {
            private const string Upload = "u+1/=";
            public readonly List<string> Calls = [];
            private readonly Dictionary<int, byte[]> parts = [];
            public byte[]? Stored;
            public bool Aborted, AlreadyStored;
            public (int Part, int Times) Fail;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                    .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
                var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(token);
                query.TryGetValue("part", out var part);
                Calls.Add(request.Method.Method + (part is null ? "" : " " + part));
                if (part is not (null or "start") && query.GetValueOrDefault("upload") != Upload) return new(HttpStatusCode.BadRequest);
                switch (request.Method.Method, part)
                {
                    case ("HEAD", null): return new(HttpStatusCode.NotFound);
                    case ("PUT", null): Stored = body; return new(HttpStatusCode.Created);
                    case ("POST", "start"):
                        return Json(AlreadyStored ? new { outcome = "stored" } : new { upload = Upload, partBytes = ContentLocker.PartBytes } as object);
                    case ("POST", "abort"): Aborted = true; return new(HttpStatusCode.OK);
                    case ("POST", "done"):
                        var listed = JsonDocument.Parse(body).RootElement.GetProperty("parts").EnumerateArray()
                            .Select(p => (N: p.GetProperty("partNumber").GetInt32(), Etag: p.GetProperty("etag").GetString())).ToList();
                        if (listed.Where((p, i) => p.N != i + 1 || p.Etag != $"e{p.N}").Any()) return new(HttpStatusCode.BadRequest);
                        Stored = listed.SelectMany(p => parts[p.N]).ToArray();
                        return new(HttpStatusCode.Created);
                    case ("PUT", var n):
                        var number = int.Parse(n!);
                        if (Fail.Part == number && Fail.Times-- > 0) return new(HttpStatusCode.InternalServerError);
                        parts[number] = body;
                        return Json(new { partNumber = number, etag = $"e{number}" });
                }
                return new(HttpStatusCode.MethodNotAllowed);
            }

            private static HttpResponseMessage Json(object value) =>
                new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        }
    }
}
