using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using Haven.Core;

namespace Haven;

/// <summary>
/// Furnishing sounds Haven plays itself, without Stagehand, when Settings → Advanced
/// → "Play furnishing sounds with Haven" is on; otherwise Stagehand plays them, as before. Two kinds, as Haven gives
/// Stagehand today: ambient and held loops (Show, from the same budgeted, keyed list BuildAmbience makes its stages
/// from), and one-shot cues (PlayCue/StopCue, at the times HousingController.Switches already keeps).
///
/// Each is a SoundData of the game's own, made and released as Stagehand 0.5.5's LiveObjectService.CreateSound and
/// LiveSoundObject do, with the arguments its compiled calls pass (read from its IL, 5 November 2026): PlaySound(path,
/// volume, fade-in 0, position, speed 1, 0, sound 0, not auto-released, BypassVolumeRules, false, midi -1, false, no
/// default fade-out, positional, false); released with Stop(20 ms), then TryReset → the resource's DecRef and
/// ReleaseSoundData, or else handed to the game's auto-release. Like Stagehand, an ambient sound that has finished is
/// played again (so a loop keeps going exactly as it did); a cue is not (Stagehand's restart was why cues repeated).
/// A change of place or volume makes the sound afresh, as Haven's create-once stages did. Only sounds made here are
/// touched, each checked to still be its own before use. Framework thread only.
/// </summary>
internal sealed unsafe class FurnitureSounds(IDataManager data, IPluginLog log) : IDisposable
{
    private sealed class Live(nint pointer, nint manager, string path, Vector3 position, float volume, bool restarts)
    {
        public nint Pointer = pointer;
        public readonly nint Manager = manager;
        public readonly string Path = path;
        public readonly Vector3 Position = position;
        public readonly float Volume = volume;
        public readonly bool Restarts = restarts;
        public long RetryAt;
        public readonly long Made = Environment.TickCount64;
        // The resource the slot loaded, noted the first time it's seen: the slot is Haven's while it still holds it.
        public nint Resource;
    }
    private readonly Dictionary<string, Live> ambient = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Live> cues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> installed = new(StringComparer.Ordinal);
    private bool failed, reported;
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);

    /// <summary>How many sounds Haven plays now (ambient and held loops, and cues).</summary>
    public int Count => ambient.Count + cues.Count;

    /// <summary>Plays exactly these ambient and held sounds: new keys made, changed ones made afresh, the rest released.</summary>
    public void Show(IReadOnlyList<AmbientEmitter> wanted)
    {
        if (failed) return;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sound in wanted)
        {
            var position = sound.World.Translation;
            if (!Sane(position, sound.Volume)) continue;
            keep.Add(sound.Key);
            if (ambient.TryGetValue(sound.Key, out var live) && (live.Path != sound.Path || live.Position != position || live.Volume != sound.Volume))
            { Release(live); ambient.Remove(sound.Key); live = null; }
            // One that couldn't start (every slot in use) waits and tries again (Update), never every frame.
            if (live == null) ambient[sound.Key] = Make(sound.Path, position, sound.Volume, restarts: true) ?? Waiting(sound.Path, position, sound.Volume, 5000);
        }
        foreach (var key in ambient.Keys.Where(k => !keep.Contains(k)).ToArray()) { Release(ambient[key]); ambient.Remove(key); }
        if (!reported && ambient.Count > 0) { reported = true; log.Information("[SOUNDS] Haven plays {Count} furnishing sound(s) itself.", ambient.Count); }
    }

    /// <summary>Each frame: an ambient sound that has finished is played again (as Stagehand does); one the game took back is made anew.</summary>
    public void Update()
    {
        if (failed || ambient.Count == 0) return;
        var now = Environment.TickCount64;
        foreach (var (key, live) in ambient.ToArray())
        {
            if (failed) return;
            if (live.Pointer != 0)
            {
                var sound = (SoundData*)live.Pointer;
                if (!Owns(live))
                {
                    // No longer Haven's (the game took the slot back): forgotten, never stopped or released, and made
                    // again only after a wait, so a wrong call here can never repeat every frame.
                    live.Pointer = 0;
                    ambient[key] = Waiting(live.Path, live.Position, live.Volume, 5000);
                    continue;
                }
                // As Stagehand tells a finished sound (the game's own test for finished auto-release sounds).
                if (sound->GetIsLoadingSoundResource() || sound->IsPlaying() || sound->FadeOutDuration > 0) continue;
                Release(live);
                // Finished the moment it began (a file that won't play): wait before trying again.
                if (now - live.Made < 100) { ambient[key] = Waiting(live.Path, live.Position, live.Volume, 2000); continue; }
            }
            else if (now < live.RetryAt) continue;
            ambient[key] = Make(live.Path, live.Position, live.Volume, restarts: true) ?? Waiting(live.Path, live.Position, live.Volume, 5000);
        }
    }
    // A sound waiting to be made again (no slot held).
    private static Live Waiting(string path, Vector3 position, float volume, int ms) => new(0, 0, path, position, volume, true) { RetryAt = Environment.TickCount64 + ms };

    /// <summary>Plays a one-shot cue under its key; it isn't played again when it ends.</summary>
    public void PlayCue(string key, string path, Vector3 position, float volume)
    {
        if (failed || !Sane(position, volume)) return;
        StopCue(key);
        if (Make(path, position, volume, restarts: false) is { } made) cues[key] = made;
    }

    /// <summary>Stops and releases a cue (at its stop time, or when its item's side changes).</summary>
    public void StopCue(string key)
    {
        if (cues.Remove(key, out var live)) Release(live);
    }

    private Live? Make(string path, Vector3 position, float volume, bool restarts)
    {
        if (failed || !Installed(path)) return null;
        var manager = SoundManager.Instance();
        if (manager == null) return null;
        SoundData* sound;
        try
        {
            sound = manager->PlaySound(path, volume, 0, position.X, position.Y, position.Z, 1f, 0, 0, false, SoundVolumeCategory.BypassVolumeRules,
                false, -1, false, false, true, false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failed = true;
            log.Error(ex, "[SOUNDS] The game couldn't play a furnishing sound; Haven stops playing its own until it's reloaded.");
            Clear();
            return null;
        }
        if (sound == null)
        {
            // Every sound slot of the game's is in use, or the file wouldn't load.
            if (warned.Add(path)) log.Warning("[SOUNDS] The game didn't play {Path}.", path);
            return null;
        }
        return new((nint)sound, (nint)manager, path, position, volume, restarts);
    }

    // Still Haven's sound: the same sound manager, active or loading, and once loaded the same resource it first loaded.
    // Not the file's name: with a Penumbra mod replacing the sound, the game names the mod's file instead, and a check
    // by name would let the slot go unreleased (the review of 5 November 2026). With auto-release off, the game frees a
    // slot only when Haven releases it, so a different resource there means the slot was given to another sound: that
    // one is never stopped or released.
    private static bool Owns(Live live)
    {
        if (live.Pointer == 0 || (nint)SoundManager.Instance() != live.Manager) return false;
        var sound = (SoundData*)live.Pointer;
        if (!sound->IsActive && !sound->IsLoadingSoundResource) return false;
        var resource = (nint)sound->SoundResourceHandle;
        if (resource == 0) return true;
        if (live.Resource == 0) live.Resource = resource;
        return live.Resource == resource;
    }

    // As Stagehand's LiveSoundObject.DestroySoundData: stopped with a 20 ms fade, then given back to the game.
    private void Release(Live live)
    {
        if (!Owns(live)) { live.Pointer = 0; return; }
        var sound = (SoundData*)live.Pointer; live.Pointer = 0;
        try
        {
            sound->Stop(20);
            if (sound->TryReset())
            {
                var resource = sound->GetSoundResourceHandle();
                if (resource != null) resource->DecRef();
                ((SoundManager*)live.Manager)->ReleaseSoundData(sound);
            }
            else sound->IsAutoReleaseEnabled = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.Warning(ex, "[SOUNDS] A furnishing sound could not be released."); }
    }

    private static bool Sane(Vector3 p, float volume) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
        && Math.Abs(p.X) < 100_000 && Math.Abs(p.Y) < 100_000 && Math.Abs(p.Z) < 100_000 && float.IsFinite(volume) && volume is >= 0 and <= 4;

    // A sound file this game has (as Stagehand asks first): one it lacks (a free trial's missing expansion) would close
    // the game, so it isn't played.
    private bool Installed(string path)
    {
        if (installed.TryGetValue(path, out var known)) return known;
        try { known = data.FileExists(path); } catch (Exception ex) when (ex is not OutOfMemoryException) { known = false; }
        if (!known) log.Warning("[SOUNDS] {Path} isn't in this game; that sound isn't played.", path);
        return installed[path] = known;
    }

    /// <summary>Releases every sound Haven plays (leaving, a scene change, Stagehand taking sounds back, unload).</summary>
    public void Clear()
    {
        foreach (var live in ambient.Values) Release(live);
        foreach (var live in cues.Values) Release(live);
        ambient.Clear(); cues.Clear();
        reported = false;
    }
    public void Dispose() => Clear();
}
