using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace HavenNative;

/// <summary>
/// A looping ambient sound (the weather heard outside a home), played the way the game plays its own weather sounds:
/// SoundManager.PlayWeatherSound calls PlaySystemSound(path, volume, sound 0, no fade-in, not auto-released, category
/// BypassVolumeRules) and keeps the sound to stop it later (read from the client, 4 November 2026). Not the orchestrion
/// player: a second request there never starts and leaves the home's music unable to play (0.64.0 and 0.64.1, where the
/// real zone's own background track came through as a drone). Haven keeps its own sound, never the game's weather slot.
/// </summary>
internal sealed unsafe class HousingAmbientSound(IPluginLog log)
{
    private SoundManager* manager;
    private SoundData* sound;
    private string path = "";
    private long startedAt;

    public string Playing => sound == null ? "" : path;
    /// <summary>The game's sound Haven plays, so it is told apart from the game's own.</summary>
    public nint Handle => (nint)sound;

    /// <summary>Whether Haven's sound is still the one it asked for and still playing (or about to).</summary>
    public bool Alive
    {
        get
        {
            if (!Owns()) return false;
            return sound->IsPlaying() || sound->IsLoadingSoundResource || Environment.TickCount64 - startedAt < 5000;
        }
    }

    private bool Owns()
    {
        if (sound == null || manager == null || SoundManager.Instance() != manager) return false;
        if (!sound->IsActive && !sound->IsLoadingSoundResource) return false;
        if (sound->SoundResourceHandle == null) return true;
        // A slot the game has since handed to another sound isn't Haven's.
        var file = sound->GetFileName().ToString().Replace('\\', '/');
        return file.Length == 0 || file.EndsWith(path, StringComparison.OrdinalIgnoreCase);
    }

    public bool Play(string file, float volume)
    {
        Stop();
        var instance = SoundManager.Instance();
        if (instance == null) return false;
        var played = instance->PlaySystemSound(file, Math.Clamp(volume, 0, 1), 0, 0, false, SoundVolumeCategory.BypassVolumeRules);
        if (played == null) return false;
        manager = instance; sound = played; path = file; startedAt = Environment.TickCount64;
        return true;
    }

    public void SetVolume(float volume)
    {
        if (Owns()) sound->SetVolume(Math.Clamp(volume, 0, 1), 0);
    }

    public void Stop(uint fadeMs = 500)
    {
        var owned = Owns(); var old = sound;
        sound = null; manager = null;
        if (!owned) return;
        try { old->Stop(fadeMs); }
        catch (Exception ex) { log.Warning(ex, "[ATMOSPHERE] the outside sound could not be stopped."); }
    }
}
