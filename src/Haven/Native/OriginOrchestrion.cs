using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace HavenNative;

// A real house's orchestrion plays through OrchestrionManager's own sound, which only the game's territory teardown
// stops. The synthetic load never runs that teardown, so the house track kept its slot and Haven's music never
// started. Entry stops it the way that teardown does; the return replays it. Client memory only: the stop and the
// replay send nothing, and both run while the packet filter holds every packet anyway.
internal sealed unsafe class OriginOrchestrion
{
    private delegate void StopOrchestrion(SoundManager* manager, SoundData* sound, uint flags);
    private readonly StopOrchestrion? stop;
    private readonly IPluginLog log;
    private ushort track;

    public OriginOrchestrion(ISigScanner scanner, IPluginLog log)
    {
        this.log = log;
        try { stop = Marshal.GetDelegateForFunctionPointer<StopOrchestrion>(scanner.ScanText(HousingMusicStream.StopSignature)); }
        catch (Exception ex) { log.Warning(ex, "[Haven] A real house's orchestrion cannot be paused during visits: native queue cleanup is unavailable."); }
    }

    public void Capture()
    {
        track = 0;
        var orchestrion = OrchestrionManager.Instance();
        var sounds = SoundManager.Instance();
        // Playing is the estate orchestrion the server started. Island Sanctuary's Looping replay would send a packet.
        if (stop == null || orchestrion == null || sounds == null || orchestrion->Mode != OrchestrionMode.Playing || orchestrion->TrackId == 0) return;
        track = orchestrion->TrackId;
        // The native stop only touches a sound it finds in its own slots, so a stale handle is safe.
        if (orchestrion->SoundData != null) stop(sounds, orchestrion->SoundData, 0);
        orchestrion->SoundData = null;
        orchestrion->Mode = OrchestrionMode.Off;
        orchestrion->TrackId = 0;
        log.Information($"[HavenNative] [ORIGIN] paused the house orchestrion (track {track}) for the visit.");
    }

    public void Restore()
    {
        if (track == 0) return;
        var saved = track; track = 0;
        var orchestrion = OrchestrionManager.Instance();
        if (orchestrion == null) return;
        // A server update that arrived after the return already set the house's current track.
        if (orchestrion->Mode != OrchestrionMode.Off || orchestrion->TrackId != 0) return;
        // Outside Island Sanctuary this replays locally ("Now playing", as on entering a house). It marks the replay
        // Looping, so put back the server's Playing: the in-game stop button then asks the server, as before.
        OrchestrionManager.PlayTrack(saved);
        orchestrion->Mode = OrchestrionMode.Playing;
        log.Information($"[HavenNative] [RETURN] resumed the house orchestrion (track {saved}).");
    }
}
