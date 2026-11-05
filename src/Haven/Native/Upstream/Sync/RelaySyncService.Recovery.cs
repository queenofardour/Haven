using System;
using System.Threading;
using System.Threading.Tasks;
using Haven.Core;

namespace HavenNative.Sync;

public partial class RelaySyncService
{
    private sealed record Connection(string Endpoint, string? Password, ulong ContentId, uint EntityId, string Name);
    private Connection? savedConnection;
    private CancellationTokenSource recoveryLifetime = new();
    private readonly object recoveryGate = new();
    private bool recoveryRunning;
    private TaskCompletionSource<bool>? admission;
    private string resumeToken = "";
    private byte[]? lastPublishedSnapshot;
    private long lastReceiveTick;
    private readonly SemaphoreSlim connectGate = new(1, 1);
    public long RoundTripMs { get; private set; }
    public bool IsRecovering { get; private set; }
    public bool RecoveryBlocked { get; private set; }
    public bool ResumedAdmission { get; private set; }
    public string RecoveryStatus { get; private set; } = "";
    public event Action? OnRecovered;
    public event Action<string>? OnRecoveryNotice;
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> PeerConnections = new();

    public bool IsConnecting { get; private set; }

    public async Task<bool> Connect(string relayUrl, string roomId, bool? createIfMissing, string? password, ulong[]? nearbyContentIds, ulong contentId, uint entityId, string charName)
    {
        // Flush intentional departure before opening another transport. Aborting the previous socket
        // here creates a recovery reservation which then rejects the same character's fresh join.
        Task departure;
        int version;
        lock (disconnectGate)
        {
            departure = Disconnect();
            version = disconnectVersion;
            IsConnecting = true;
        }
        await departure;
        CancellationTokenSource intent;
        Task<bool> transport;
        lock (disconnectGate)
        {
            if (version != disconnectVersion) return false;
            recoveryLifetime = intent = new CancellationTokenSource();
            savedConnection = new(relayUrl, password, contentId, entityId, charName);
            IsConnecting = true;
            transport = OpenTransport(relayUrl, roomId, createIfMissing, password, nearbyContentIds, contentId, entityId, charName, false);
        }
        try { return await transport; }
        catch (OperationCanceledException) when (intent.IsCancellationRequested) { return false; }
        finally { if (ReferenceEquals(intent, recoveryLifetime)) IsConnecting = false; }
    }
    private async Task<bool> OpenTransport(string endpoint, string room, bool? create, string? password, ulong[]? nearby,
        ulong content, uint entity, string name, bool resuming)
    {
        var lifetime = recoveryLifetime.Token;
        await connectGate.WaitAsync(lifetime);
        try { return await ConnectTransport(endpoint, room, create, password, nearby, content, entity, name, resuming, lifetime); }
        finally { connectGate.Release(); }
    }
    private void CancelRecovery()
    {
        recoveryLifetime.Cancel();
        IsConnecting = false;
        IsRecovering = false; RecoveryBlocked = false; ResumedAdmission = false;
        resumeToken = ""; savedConnection = null;
        lastPublishedSnapshot = null;
        PeerConnections.Clear();
        HousingMembers.Clear(); Editing = null; SharedHouse = null; nextSceneRequest = 0;
        admission?.TrySetCanceled();
        lock (recoveryGate) recoveryRunning = false;
    }
    private void BeginRecovery()
    {
        if (savedConnection == null || resumeToken.Length == 0 || recoveryLifetime.IsCancellationRequested) return;
        IsRecovering = true;
        RecoveryStatus = "Connection interrupted · preserving the home and reconnecting…";
        StartRecoveryWorker();
    }
    public void RetryRecovery()
    {
        if (!IsRecovering || savedConnection == null) return;
        RecoveryBlocked = false;
        StartRecoveryWorker();
    }
    private void StartRecoveryWorker()
    {
        lock (recoveryGate)
        {
            if (recoveryRunning) return;
            recoveryRunning = true;
            var owner = recoveryLifetime;
            _ = Task.Run(async () =>
            {
                try { await Recover(owner.Token); }
                finally
                {
                    lock (recoveryGate)
                    {
                        if (ReferenceEquals(owner, recoveryLifetime))
                        {
                            recoveryRunning = false;
                            if (IsRecovering && !RecoveryBlocked && !owner.IsCancellationRequested) StartRecoveryWorker();
                        }
                    }
                }
            });
        }
    }
    private async Task Recover(CancellationToken cancel)
    {
        var connection = savedConnection!;
        var attempt = 0;
        try
        {
            while (!cancel.IsCancellationRequested && IsRecovering && !RecoveryBlocked)
            {
                var delay = RecoveryPolicy.RetryDelay(++attempt, Random.Shared.NextDouble());
                RecoveryStatus = $"Reconnecting · attempt {attempt} · characters and home preserved";
                await Task.Delay(delay, cancel);
                if (cancel.IsCancellationRequested) return;
                admission = new(TaskCreationOptions.RunContinuationsAsynchronously);
                log.Information("[Haven] Recovery attempt {Attempt}; delayMs={Delay}", attempt, delay.TotalMilliseconds);
                if (!await OpenTransport(connection.Endpoint, CachedRoomId, false, connection.Password, null,
                        connection.ContentId, connection.EntityId, connection.Name, true)) continue;
                try
                {
                    if (!await admission.Task.WaitAsync(RecoveryPolicy.SilenceTimeout(RoundTripMs), cancel)) break;
                    var deadline = Environment.TickCount64 + 120000;
                    while (IsConnected && !HousingSceneReady && Environment.TickCount64 < deadline)
                        await Task.Delay(100, cancel);
                    if (HousingSceneReady) { log.Information("[Haven] Gathering recovered after {Attempts} attempts.", attempt); return; }
                }
                catch (TimeoutException) { log.Warning("[Haven] Recovery admission timed out; scene remains isolated."); }
                if (ReferenceEquals(savedConnection, connection)) { IsConnected = false; ws?.Abort(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Error(ex, "[Haven] Recovery paused after an unexpected error; local scene preserved.");
            RecoveryBlocked = true;
            RecoveryStatus = "Offline · recovery paused. Retry the connection or leave when ready.";
            OnRecoveryNotice?.Invoke(RecoveryStatus);
        }
    }
}
