using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using Haven.Core;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;

namespace Haven;

internal sealed class OrchestrionDurations(IDataManager data) : IDisposable
{
    private readonly Dictionary<uint, string> paths = data.GetExcelSheet<OrchestrionPath>().Where(x => !x.File.IsEmpty).ToDictionary(x => x.RowId, x => x.File.ToString());
    private readonly ConcurrentDictionary<uint, Task<ScdDurationReader.Track>> cache = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stop = new();
    private MusicTrack[]? playlist;
    private long[] lengths = [];
    private bool resolved;

    public bool TryResolve(MusicTrack[] tracks, out long[] durations, out string status)
    {
        if (!ReferenceEquals(playlist, tracks)) { playlist = tracks; lengths = new long[tracks.Length]; resolved = false; }
        durations = lengths; status = "";
        if (resolved) return true;
        var ready = true;
        for (var i = 0; i < tracks.Length; i++)
        {
            // A custom song carries its own length.
            if (tracks[i].IsCustom) { lengths[i] = tracks[i].Milliseconds; continue; }
            var task = cache.GetOrAdd(tracks[i].OrchestrionId, Load);
            if (task.IsCompletedSuccessfully) lengths[i] = task.Result.Milliseconds;
            else
            {
                ready = false;
                status = task.IsFaulted ? "Track information unavailable: " + task.Exception!.GetBaseException().Message : "Reading track information…";
            }
        }
        resolved = ready;
        return ready;
    }

    /// <summary>A track's length when known: a custom song's own, or a game track already read (otherwise it starts reading).</summary>
    public long? Known(MusicTrack track)
    {
        if (track.IsCustom) return track.Milliseconds;
        var task = cache.GetOrAdd(track.OrchestrionId, Load);
        return task.IsCompletedSuccessfully ? task.Result.Milliseconds : null;
    }

    public bool Loops(uint id) => cache.TryGetValue(id, out var task) && task.IsCompletedSuccessfully && task.Result.Loops;

    private Task<ScdDurationReader.Track> Load(uint id) => Task.Run(async () =>
    {
        var token = stop.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!paths.TryGetValue(id, out var path)) throw new InvalidDataException("Unknown orchestrion track.");
            var file = await data.GetFileAsync<ScdFile>(path, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return ScdDurationReader.Read(file);
        }
        finally { gate.Release(); }
    });

    public void Dispose()
    {
        stop.Cancel();
        _ = Cleanup();
    }
    private async Task Cleanup()
    {
        try { await Task.WhenAll(cache.Values).ConfigureAwait(false); }
        catch { /* Fault/cancellation is already surfaced as a metadata status. */ }
        finally { gate.Dispose(); stop.Dispose(); }
    }
}
