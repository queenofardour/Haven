using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using Haven.Core;

namespace HavenNative.Sync;

public sealed record HousingMember(ulong ContentId, string Name);
public partial class RelaySyncService
{
    public readonly ConcurrentDictionary<string, HousingMember> HousingMembers = new();
    public ulong LocalContentId => savedConnection?.ContentId ?? 0;
    public EditingState? Editing { get; private set; }
    public House? SharedHouse { get; private set; }
    public event Action<EditingState>? OnEditing;
    public event Action<EditProposal>? OnEditProposal;
    public event Action<EditResult>? OnEditResult;
    public event Action<EditApplied>? OnEditApplied;
    private long nextSceneRequest;
    private readonly SemaphoreSlim editSendGate = new(1, 1);
    public Task WithdrawEdits() => SendEditMessage(HousingWire.EditWithdraw, Guid.Empty, false);
    public Task SetEditing(EditingPermissions permissions) => SendEditMessage(HousingWire.EditPermissions, permissions, false);
    public Task SubmitEdits(EditBatch batch) => SendEditMessage(HousingWire.EditSubmit, batch, true);
    public Task StageEdits(EditBatch batch) => SendEditMessage(HousingWire.EditStage, batch, true);
    public Task ReviewStaged(EditReview review) => SendEditMessage(HousingWire.EditReview, review, true);
    public Task ReviewEdits(EditDecision decision) => SendEditMessage(HousingWire.EditDecision, decision, false);
    public Task SetSharedMusic(MusicOptions music) => SendEditMessage(HousingWire.MusicUpdate, music, true);
    private async Task SendEditMessage<T>(byte kind, T value, bool compressed)
    {
        var socket = ws;
        if (!IsConnected || !HousingSceneReady || socket == null)
        { OnEditResult?.Invoke(new(Guid.Empty, "error", "Connection unavailable. Your draft is preserved; retry after reconnection.")); return; }
        await editSendGate.WaitAsync();
        try
        {
            var bytes = await Task.Run(() => compressed ? HostedEditing.Pack(value) : JsonSerializer.SerializeToUtf8Bytes(value));
            if (ReferenceEquals(ws, socket) && IsConnected && HousingSceneReady) await SendFrame(kind, bytes, socket);
        }
        catch (Exception ex) { log.Warning(ex, "Saved decorating batch could not be sent."); OnEditResult?.Invoke(new(Guid.Empty, "error", ex.Message)); }
        finally { editSendGate.Release(); }
    }
    public async Task RequestSharedScene()
    {
        var socket = ws;
        if (!IsConnected || socket == null || Environment.TickCount64 < nextSceneRequest) return;
        nextSceneRequest = Environment.TickCount64 + 2000;
        await SendFrame(HousingWire.SceneRequest, [], socket);
    }
    private void CacheHouse(House house, bool notify = true)
    {
        SharedHouse = house;
        lastPublishedSnapshot = HouseFormat.Serialize(house);
        if (notify) OnHousingSnapshot?.Invoke(lastPublishedSnapshot);
    }
    private bool HandleEditing(byte kind, byte[] payload)
    {
        switch (kind)
        {
            case HousingWire.EditState:
                Editing = JsonSerializer.Deserialize<EditingState>(payload) ?? throw new InvalidOperationException("Missing decorating access.");
                OnEditing?.Invoke(Editing); return true;
            case HousingWire.EditProposal:
                OnEditProposal?.Invoke(HostedEditing.Unpack<EditProposal>(payload)); return true;
            case HousingWire.EditResult:
                OnEditResult?.Invoke(JsonSerializer.Deserialize<EditResult>(payload)!); return true;
            case HousingWire.EditResolved:
                OnEditResult?.Invoke(HostedEditing.Unpack<EditResult>(payload)); return true;
            case HousingWire.EditApplied:
                try
                {
                    var change = HostedEditing.Unpack<EditApplied>(payload);
                    if (SharedHouse == null) { _ = RequestSharedScene(); return true; }
                    var next = HostedEditing.Apply(SharedHouse, change.Batch);
                    if (HostedEditing.Hash(next) != change.Hash) throw new InvalidOperationException("Scene revision differs.");
                    OnEditApplied?.Invoke(change); CacheHouse(next);
                }
                catch (Exception ex) { log.Warning(ex, "Requesting a fresh shared scene after a decorating revision mismatch."); _ = RequestSharedScene(); }
                return true;
            case HousingWire.MusicUpdate:
                if (SharedHouse != null) CacheHouse(SharedHouse with { Music = HostedEditing.Unpack<MusicOptions>(payload) });
                else _ = RequestSharedScene();
                return true;
            default: return false;
        }
    }
}
