using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using Haven.Core;

namespace HavenNative;

internal sealed unsafe class HousingMusicStream
{
    // Native counterpart of SoundManager.PlayOrchestrionSound. Cancels its
    // pending slot too, or marks a playing slot for manager-owned release.
    // API 15 client: void(SoundManager*, SoundData*, uint), verified offline.
    internal const string StopSignature = "48 85 D2 0F 84 ?? ?? ?? ?? 57 48 83 EC 20 48 89 5C 24 30 48 8B FA 48 8D 99 A8 1B 00 00";
    private delegate void StopOrchestrion(SoundManager* manager, SoundData* sound, uint flags);
    private readonly StopOrchestrion? stop;
    private readonly IPluginLog log;
    private SoundManager* manager;
    private SoundData* sound;
    private string path = "";
    private byte[] previousPath = [];
    private (float X, float Y, float Z) previousPosition;
    public bool Available => stop != null;
    /// <summary>Haven's stream is still reading its file.</summary>
    public bool Loading => OwnsSound() && sound->IsLoadingSoundResource;
    private bool beganPlaying;
    private long requestedAt;
    public MusicStreamState State
    {
        get
        {
            if (!OwnsSound()) return MusicStreamState.Missing;
            if (sound->IsPlaying()) { beganPlaying = true; return MusicStreamState.Playing; }
            if ((sound->IsLoadingSoundResource || !beganPlaying) && Environment.TickCount64 - requestedAt < 15000)
                return MusicStreamState.Pending;
            return MusicStreamState.Ended;
        }
    }

    // Every Haven stream's current request, so each knows the shared orchestrion path may name another Haven stream.
    private static readonly List<HousingMusicStream> streams = [];
    private static bool Requested(string asked) { lock (streams) return streams.Any(s => s.sound != null && s.path == asked); }
    // A native slot the game gave a later request of Haven's other stream is that stream's.
    private bool TakenOver() { lock (streams) return streams.Any(s => s != this && s.sound == sound && s.request > request); }
    private static long requests;
    private long request;

    public HousingMusicStream(ISigScanner scanner, IPluginLog log)
    {
        lock (streams) streams.Add(this);
        this.log = log;
        try { stop = Marshal.GetDelegateForFunctionPointer<StopOrchestrion>(scanner.ScanText(StopSignature)); }
        catch (Exception ex) { log.Warning(ex, "[Haven] Orchestrion playback disabled: native queue cleanup is unavailable."); }
    }

    public bool Play(string file, float x, float y, float z)
    {
        Stop();
        var instance = SoundManager.Instance();
        if (stop == null || instance == null) return false;
        manager = instance; previousPath = instance->OrchestrionPath.ToArray();
        previousPosition = (instance->OrchestrionPosX, instance->OrchestrionPosY, instance->OrchestrionPosZ);
        path = file;
        beganPlaying = false; requestedAt = Environment.TickCount64; request = Interlocked.Increment(ref requests);
        sound = instance->PlayOrchestrionSound(file, x, y, z, true);
        if (sound == null) { RestorePath(instance); manager = null; return false; }
        sound->SetPosition(false, 0, 0, 0);
        return true;
    }

    public void SetVolume(float volume)
    {
        if (OwnsSound()) sound->SetVolume(Math.Clamp(volume, 0, 1), 0);
    }

    private bool OwnsSound()
    {
        if (sound == null || manager == null || SoundManager.Instance() != manager) return false;
        // Still loading counts as Haven's: a track swapped out mid-load (the Odyssey changes music between screens) was
        // otherwise left to start on its own and never stopped.
        if (!sound->IsActive && !sound->IsLoadingSoundResource || TakenOver()) return false;
        // An ended native slot may already have returned to the pool on zoning. The orchestrion path is shared: Haven's
        // other stream (the weather heard outside beside the music) may have asked for its file since, which must not
        // make this one's still-loading sound look like someone else's (it was then never stopped, and played twice).
        if (sound->SoundResourceHandle == null) return manager->OrchestrionPathString is var asked && (asked == path || Requested(asked));
        // The orchestrion path is what Haven asked the game for; it still names this request when the loaded file has
        // another name (a mod's file).
        var file = sound->GetFileName().ToString();
        return manager->OrchestrionPathString == path || !string.IsNullOrEmpty(file) && MusicPlaybackPolicy.SameFile(path, file);
    }

    public void Stop()
    {
        var owned = OwnsSound(); var oldSound = sound; var oldManager = manager;
        sound = null; manager = null;
        beganPlaying = false;
        if (oldManager == null || oldManager != SoundManager.Instance()) return;
        try
        {
            if (!owned && oldSound != null)
                log.Information("[MUSIC] not stopping a stream that is no longer Haven's (wanted {Path}).", path ?? "");
            if (owned)
            {
                // Mute before native cleanup's short fade, so restoring zone
                // BGM cannot audibly overlap this stream. The manager handles
                // release; releasing the pool entry here would leave its slot stale.
                oldSound->SetVolume(0, 0);
                stop!(oldManager, oldSound, 0);
            }
            RestorePath(oldManager);
        }
        catch (Exception ex) { log.Warning(ex, "[Haven] Orchestrion cleanup failed."); }
    }

    private void RestorePath(SoundManager* instance)
    {
        if (instance->OrchestrionPathString != path) return;
        previousPath.CopyTo(instance->OrchestrionPath);
        instance->OrchestrionPosX = previousPosition.X;
        instance->OrchestrionPosY = previousPosition.Y;
        instance->OrchestrionPosZ = previousPosition.Z;
    }
}
