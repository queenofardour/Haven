using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Haven.Core;

namespace Haven;

/// <summary>
/// Furnishing effects (flames, steam, water) Haven draws itself, without Stagehand,
/// when Settings → Advanced → "Draw furnishing effects with Haven" is on; otherwise Stagehand draws them, as before.
/// Given the same effects Stagehand's ambient stage is built from (FurnitureCatalogue.BuildAmbience), keyed alike, each
/// is a VfxObject of the game's own, made, updated and destroyed as Stagehand 0.5.5's LiveObjectService.CreateVfx and
/// LiveVfxObject do (UniversalConquistador, AGPL-3.0-or-later; THIRD_PARTY_NOTICES.md): made only from a file the game
/// has, the flag that can keep one hidden cleared, started; a new place restarts it, a new file remakes it, and one no
/// longer wanted is destroyed. Only effects made here are touched. Framework thread only.
/// </summary>
internal sealed unsafe class FurnitureEffects(IDataManager data, IPluginLog log) : IDisposable
{
    private sealed class Live(nint pointer, string path)
    {
        public nint Pointer = pointer;
        public readonly string Path = path;
        public Vector3 Position, Scale;
        public Quaternion Rotation;
    }
    private readonly Dictionary<string, Live> effects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> installed = new(StringComparer.Ordinal);
    private bool failed, reported;

    /// <summary>How many effects Haven draws now.</summary>
    public int Count => effects.Count;

    /// <summary>Whether Haven draws an effect whose key starts so (a piece's, "wf-{id}-").</summary>
    public bool Draws(string keyPrefix) => effects.Keys.Any(k => k.StartsWith(keyPrefix, StringComparison.Ordinal));

    /// <summary>Shows exactly these effects: new keys are made, kept ones updated in place, the rest destroyed.</summary>
    /// <param name="restartOnMove">Whether a moved effect starts again to take its new place (a placement preview, moved
    /// every frame, isn't: it would never get past its start).</param>
    public void Show(IReadOnlyList<AmbientEmitter> wanted, bool restartOnMove = true)
    {
        if (failed) return;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var effect in wanted)
        {
            if (!Matrix4x4.Decompose(effect.World, out var scale, out var rotation, out var position) || !Sane(position, rotation, scale, effect.Color)) continue;
            keep.Add(effect.Key);
            if (effects.TryGetValue(effect.Key, out var live) && live.Path != effect.Path) { Destroy(live); effects.Remove(effect.Key); live = null; }
            if (live == null)
            {
                if (!Installed(effect.Path)) continue;
                VfxObject* made;
                try { made = VfxObject.Create(effect.Path, "Haven.VfxObject"); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed = true;
                    log.Error(ex, "[EFFECTS] The game couldn't make an effect; Haven stops drawing its own until it's reloaded.");
                    Clear();
                    return;
                }
                if (made == null) { log.Warning("[EFFECTS] The game made no effect for {Path}.", effect.Path); continue; }
                // As Stagehand's LiveVfxObject starts one: the flag that sometimes keeps an effect hidden cleared, and started.
                made->SomeFlags &= 0xF7;
                // Flags at ClientStructs' default, -1, as Stagehand 0.5.5's compiled call passes them (checked in its IL); 0.66.2
                // passed 0, and no effect showed in game.
                made->Update(0);
                effects[effect.Key] = live = new((nint)made, effect.Path);
                Place(live, position, rotation, scale);
            }
            else if (live.Position != position || live.Rotation != rotation || live.Scale != scale)
            {
                Place(live, position, rotation, scale);
                // An effect often has to start again to take its new place (as Stagehand does).
                if (restartOnMove) ((VfxObject*)live.Pointer)->Update(0);
            }
            ((VfxObject*)live.Pointer)->Color = effect.Color;
        }
        foreach (var key in effects.Keys.Where(k => !keep.Contains(k)).ToArray()) { Destroy(effects[key]); effects.Remove(key); }
        if (!reported && effects.Count > 0) { reported = true; log.Information("[EFFECTS] Haven draws {Count} effect(s) itself.", effects.Count); }
    }

    private static void Place(Live live, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var effect = (VfxObject*)live.Pointer;
        effect->Position = position; effect->Rotation = rotation; effect->Scale = scale;
        effect->NotifyTransformChanged();
        (live.Position, live.Rotation, live.Scale) = (position, rotation, scale);
    }

    private static bool Sane(Vector3 p, Quaternion r, Vector3 s, Vector4 c) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) && Math.Abs(p.X) < 100_000 && Math.Abs(p.Y) < 100_000 && Math.Abs(p.Z) < 100_000
        && float.IsFinite(r.X) && float.IsFinite(r.Y) && float.IsFinite(r.Z) && float.IsFinite(r.W)
        && float.IsFinite(s.X) && float.IsFinite(s.Y) && float.IsFinite(s.Z)
        && float.IsFinite(c.X) && float.IsFinite(c.Y) && float.IsFinite(c.Z) && float.IsFinite(c.W);

    // An effect file this game has (as Stagehand asks first): one it lacks (a free trial's missing expansion) would close
    // the game, so it isn't shown.
    private bool Installed(string path)
    {
        if (installed.TryGetValue(path, out var known)) return known;
        try { known = data.FileExists(path); } catch (Exception ex) when (ex is not OutOfMemoryException) { known = false; }
        if (!known) log.Warning("[EFFECTS] {Path} isn't in this game; that effect isn't shown.", path);
        return installed[path] = known;
    }

    private static void Destroy(Live live)
    {
        if (live.Pointer == 0) return;
        var effect = (VfxObject*)live.Pointer; live.Pointer = 0;
        effect->CleanupRender(); effect->Dtor(1);
    }
    public void Clear()
    {
        foreach (var live in effects.Values) Destroy(live);
        effects.Clear();
        reported = false;
    }
    public void Dispose() => Clear();
}
