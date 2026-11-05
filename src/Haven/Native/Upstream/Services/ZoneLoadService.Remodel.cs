using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;

namespace HavenNative.Services;

// Remodelling a preset home's shell: the pieces the owner removed are hidden on this PC, each a
// layout instance of the shell's bg.lgb found by its key. A piece whose collision is kept stays solid; otherwise its
// colliders go too (the same reversible masks the custom canvas uses, through hiddenInstanceKeys). Applied again and
// again, since a shell reload (a changed wallpaper) brings its pieces back. Pieces shown again get their colliders back.
public unsafe partial class ZoneLoadService
{
    // Each hidden piece, by its key: whether its collision stays, and every instance key under it (a group's members).
    private readonly Dictionary<uint, (bool Keep, List<uint> Keys)> remodelHidden = new();
    private uint remodelOutlined;
    // A removed piece shown for a moment as a see-through ghost (pointed at in the room or in the removed list).
    private uint remodelGhost;
    // Lights inside removed pieces (a window's light on the floor), by instance: the colour they had, given back when shown.
    private readonly Dictionary<nint, System.Numerics.Vector4> remodelLights = new();
    // What goes with a removed inn piece (its lamp's light, its bed's collision box), by key: whether its collision stays.
    private readonly Dictionary<uint, bool> remodelAttached = new();

    /// <summary>
    /// Hides these shell pieces (key → keep collision) and shows any hidden before that aren't listed; returns how many were
    /// found. A piece may be an event object's model (keyed by the object's layout id: an inn's book). Attached lists, per
    /// piece, the layout lights to darken and collision boxes to drop with it.
    /// </summary>
    public int ApplyShellRemodel(IReadOnlyDictionary<uint, bool> hide, IReadOnlyDictionary<uint, IReadOnlyList<uint>>? attached = null)
    {
        var layout = ActiveLayoutForRemodel();
        if (layout == null) return 0;
        var found = 0;
        var shown = new List<uint>();
        found += ApplyEventObjectRemodel(hide, shown);
        ApplyAttached(layout, hide, attached, shown);
        foreach (var type in new[] { InstanceType.BgPart, InstanceType.SharedGroup })
        {
            if (!layout->InstancesByType.TryGetValuePointer(type, out var map) || map == null || map->Value == null) continue;
            foreach (var pair in *map->Value)
            {
                var instance = pair.Item2.Value;
                if (instance == null || instance->Id.InstanceKey == 0) continue;
                var key = instance->Id.InstanceKey;
                if (hide.TryGetValue(key, out var keep))
                {
                    found++;
                    var keys = new List<uint>();
                    SetPieceVisible(instance, key == remodelGhost, keys, 0);
                    // Collision goes only when it isn't kept; a changed choice gives the colliders back.
                    foreach (var k in keys) { if (keep) hiddenInstanceKeys.Remove(k); else hiddenInstanceKeys.Add(k); }
                    if (keep) shown.AddRange(keys);
                    remodelHidden[key] = (keep, keys);
                }
                else if (remodelHidden.Remove(key, out var was))
                {
                    var keys = new List<uint>();
                    SetPieceVisible(instance, true, keys, 0);
                    SetPieceTransparency(instance, 0, 0);
                    RestorePieceLights(instance, 0);
                    if (key == remodelGhost) remodelGhost = 0;
                    foreach (var k in was.Keys) hiddenInstanceKeys.Remove(k);
                    shown.AddRange(was.Keys);
                }
            }
        }
        if (shown.Count > 0) RestoreRemodelColliders(shown);
        KillHiddenColliders();
        return found;
    }

    /// <summary>
    /// Keeps the hidden pieces hidden this frame: the game shows some parts again by itself (a window's light shaft follows
    /// the time of day). Returns how many draw slots it had shown again.
    /// </summary>
    public int HoldShellRemodel()
    {
        if (remodelHidden.Count == 0) return 0;
        var layout = ActiveLayoutForRemodel();
        if (layout == null) return 0;
        var reshown = 0;
        foreach (var (key, model) in EventObjectModels())
            if (remodelHidden.ContainsKey(key) && key != remodelGhost) reshown += HoldHidden((ILayoutInstance*)model, 0);
        if (remodelAttached.Count > 0 && layout->InstancesByType.TryGetValuePointer(InstanceType.Light, out var lights) && lights != null && lights->Value != null)
            foreach (var pair in *lights->Value)
                if (pair.Item2.Value is var light && light != null && remodelAttached.ContainsKey(light->Id.InstanceKey)) DarkenLight(light);
        foreach (var type in new[] { InstanceType.BgPart, InstanceType.SharedGroup })
        {
            if (!layout->InstancesByType.TryGetValuePointer(type, out var map) || map == null || map->Value == null) continue;
            foreach (var pair in *map->Value)
            {
                var instance = pair.Item2.Value;
                if (instance == null) continue;
                var key = instance->Id.InstanceKey;
                if (key != 0 && key != remodelGhost && remodelHidden.ContainsKey(key)) reshown += HoldHidden(instance, 0);
                else if (key != 0 && key == remodelGhost) DarkenPieceLights(instance, 0);
            }
        }
        return reshown;
    }

    // A window's light shaft is drawn by the game's light-shaft pass even with its draw slot hidden, so a removed piece's
    // parts are also made fully see-through, and its lights (the window's light on the floor) are held dark: the game
    // sets both again as the time of day changes.
    private int HoldHidden(ILayoutInstance* instance, int depth)
    {
        if (instance == null || depth >= 4) return 0;
        if (instance->Id.Type == InstanceType.Light) { DarkenLight(instance); return 0; }
        GetAllSlots(instance, out var fld, out _, out var g23, out var g24);
        var reshown = 0;
        if (fld != null && fld->IsVisible) { fld->IsVisible = false; reshown++; }
        if (g23 != null && g23 != fld && g23->IsVisible) { g23->IsVisible = false; reshown++; }
        if (g24 != null && g24 != fld && g24 != g23 && g24->IsVisible) { g24->IsVisible = false; reshown++; }
        if (instance->Id.Type == InstanceType.BgPart && ((BgPartsLayoutInstance*)instance)->GraphicsObject is var bg && bg != null
            && bg->GetTransparency() < 1) bg->SetTransparency(1);
        if (instance->Id.Type is not InstanceType.SharedGroup) return reshown;
        var children = ((SharedGroupLayoutInstance*)instance)->Instances.Instances;
        for (long i = 0; i < children.LongCount; i++)
            if (children[i].Value != null) reshown += HoldHidden(children[i].Value->Instance, depth + 1);
        return reshown;
    }

    private void DarkenLight(ILayoutInstance* light)
    {
        System.Numerics.Vector4 colour = default;
        if (!remodelLights.ContainsKey((nint)light)) { light->GetColor(&colour); remodelLights[(nint)light] = colour; }
        else
        {
            light->GetColor(&colour);
            if (colour == default) return;
        }
        System.Numerics.Vector4 dark = default;
        light->SetColor(&dark);
    }

    // A ghost shows the piece, not its light.
    private void DarkenPieceLights(ILayoutInstance* instance, int depth)
    {
        if (instance == null || depth >= 4) return;
        if (instance->Id.Type == InstanceType.Light) { DarkenLight(instance); return; }
        if (instance->Id.Type is not InstanceType.SharedGroup) return;
        var children = ((SharedGroupLayoutInstance*)instance)->Instances.Instances;
        for (long i = 0; i < children.LongCount; i++)
            if (children[i].Value != null) DarkenPieceLights(children[i].Value->Instance, depth + 1);
    }

    private void RestorePieceLights(ILayoutInstance* instance, int depth)
    {
        if (instance == null || depth >= 4) return;
        if (instance->Id.Type == InstanceType.Light)
        {
            if (remodelLights.Remove((nint)instance, out var colour)) instance->SetColor(&colour);
            return;
        }
        if (instance->Id.Type is not InstanceType.SharedGroup) return;
        var children = ((SharedGroupLayoutInstance*)instance)->Instances.Instances;
        for (long i = 0; i < children.LongCount; i++)
            if (children[i].Value != null) RestorePieceLights(children[i].Value->Instance, depth + 1);
    }

    /// <summary>Shows every hidden piece again (leaving the home, or a home without remodelling).</summary>
    public void ClearShellRemodel()
    {
        if (remodelHidden.Count > 0 || remodelAttached.Count > 0) ApplyShellRemodel(new Dictionary<uint, bool>());
        remodelHidden.Clear();
        remodelAttached.Clear();
        remodelLights.Clear();
        remodelGhost = 0;
        OutlineShellPiece(0);
    }

    /// <summary>Outlines one shell piece as the game outlines its target (0: none).</summary>
    public void OutlineShellPiece(uint key)
    {
        if (key == remodelOutlined) return;
        var layout = ActiveLayoutForRemodel();
        if (layout != null)
        {
            if (remodelOutlined != 0 && FindRemodelInstance(layout, remodelOutlined) is var old && old != null) SetPieceOutline(old, ObjectHighlightColor.None, 0);
            if (key != 0 && FindRemodelInstance(layout, key) is var next && next != null) SetPieceOutline(next, ObjectHighlightColor.Blue, 0);
        }
        remodelOutlined = key;
    }

    /// <summary>Shows one removed piece as a see-through ghost, putting any other ghost away (0: none).</summary>
    public void GhostShellPiece(uint key)
    {
        if (key == remodelGhost) return;
        var layout = ActiveLayoutForRemodel();
        if (layout != null)
        {
            if (remodelGhost != 0 && remodelHidden.ContainsKey(remodelGhost) && FindRemodelInstance(layout, remodelGhost) is var old && old != null)
            { SetPieceVisible(old, false, new List<uint>(), 0); SetPieceTransparency(old, 0, 0); }
            if (key != 0 && remodelHidden.ContainsKey(key) && FindRemodelInstance(layout, key) is var ghost && ghost != null)
            { SetPieceVisible(ghost, true, new List<uint>(), 0); SetPieceTransparency(ghost, .55f, 0); }
            else key = 0;
        }
        remodelGhost = key;
    }

    private static void SetPieceTransparency(ILayoutInstance* instance, float value, int depth)
    {
        if (instance == null || depth >= 4) return;
        if (instance->Id.Type == InstanceType.BgPart && ((BgPartsLayoutInstance*)instance)->GraphicsObject is var bg && bg != null) bg->SetTransparency(value);
        if (instance->Id.Type is not InstanceType.SharedGroup) return;
        var children = ((SharedGroupLayoutInstance*)instance)->Instances.Instances;
        for (long i = 0; i < children.LongCount; i++)
            if (children[i].Value != null) SetPieceTransparency(children[i].Value->Instance, value, depth + 1);
    }

    private static LayoutManager* ActiveLayoutForRemodel()
    {
        var world = LayoutWorld.Instance();
        var layout = world == null ? null : world->ActiveLayout;
        return layout == null || layout->InitState != 7 ? null : layout;
    }

    private ILayoutInstance* FindRemodelInstance(LayoutManager* layout, uint key)
    {
        foreach (var type in new[] { InstanceType.BgPart, InstanceType.SharedGroup })
        {
            if (!layout->InstancesByType.TryGetValuePointer(type, out var map) || map == null || map->Value == null) continue;
            foreach (var pair in *map->Value)
                if (pair.Item2.Value is var instance && instance != null && instance->Id.InstanceKey == key) return instance;
        }
        foreach (var (eventKey, model) in EventObjectModels()) if (eventKey == key) return (ILayoutInstance*)model;
        return null;
    }

    // The models event objects bring with them (an inn's Unending Journey book), by the object's layout id: they aren't
    // in the layout's own instances, so the piece is found through the object.
    private List<(uint Key, nint Model)> EventObjectModels()
    {
        var result = new List<(uint, nint)>();
        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj) continue;
            var native = (GameObject*)obj.Address;
            if (native == null || native->LayoutId == 0 || native->SharedGroupLayoutInstance == null) continue;
            result.Add((native->LayoutId, (nint)native->SharedGroupLayoutInstance));
        }
        return result;
    }

    private int ApplyEventObjectRemodel(IReadOnlyDictionary<uint, bool> hide, List<uint> shown)
    {
        var found = 0;
        foreach (var (key, address) in EventObjectModels())
        {
            var model = (ILayoutInstance*)address;
            if (hide.ContainsKey(key))
            {
                found++;
                SetPieceVisible(model, key == remodelGhost, new List<uint>(), 0);
                remodelHidden[key] = (true, new List<uint>());
            }
            else if (remodelHidden.Remove(key))
            {
                SetPieceVisible(model, true, new List<uint>(), 0);
                SetPieceTransparency(model, 0, 0);
                if (key == remodelGhost) remodelGhost = 0;
            }
        }
        return found;
    }

    // An inn piece's lights are held dark and its collision boxes dropped (unless its collision stays) while it's removed.
    private void ApplyAttached(LayoutManager* layout, IReadOnlyDictionary<uint, bool> hide, IReadOnlyDictionary<uint, IReadOnlyList<uint>>? attached, List<uint> shown)
    {
        var wanted = new Dictionary<uint, bool>();
        if (attached != null)
            foreach (var (piece, keep) in hide)
                if (attached.TryGetValue(piece, out var extras))
                    foreach (var extra in extras) wanted[extra] = keep;
        if (wanted.Count == 0 && remodelAttached.Count == 0) return;
        foreach (var (extra, keep) in wanted)
        {
            if (keep) { if (hiddenInstanceKeys.Remove(extra)) shown.Add(extra); }
            else hiddenInstanceKeys.Add(extra);
        }
        foreach (var gone in remodelAttached.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            if (hiddenInstanceKeys.Remove(gone)) shown.Add(gone);
            remodelAttached.Remove(gone);
            if (layout->InstancesByType.TryGetValuePointer(InstanceType.Light, out var map) && map != null && map->Value != null)
                foreach (var pair in *map->Value)
                    if (pair.Item2.Value is var light && light != null && light->Id.InstanceKey == gone) RestorePieceLights(light, 0);
        }
        foreach (var (extra, keep) in wanted) remodelAttached[extra] = keep;
        if (layout->InstancesByType.TryGetValuePointer(InstanceType.Light, out var lights) && lights != null && lights->Value != null)
            foreach (var pair in *lights->Value)
                if (pair.Item2.Value is var light && light != null && wanted.ContainsKey(light->Id.InstanceKey)) DarkenLight(light);
    }

    // A piece's every draw slot shown or hidden, a group's members included; keys collects every instance key touched.
    private static void SetPieceVisible(ILayoutInstance* instance, bool visible, List<uint> keys, int depth)
    {
        if (instance == null || depth >= 4) return;
        SetAllSlotsVisible(instance, visible);
        if (instance->Id.InstanceKey != 0) keys.Add(instance->Id.InstanceKey);
        if (instance->Id.Type is not InstanceType.SharedGroup) return;
        var children = ((SharedGroupLayoutInstance*)instance)->Instances.Instances;
        for (long i = 0; i < children.LongCount; i++)
            if (children[i].Value != null) SetPieceVisible(children[i].Value->Instance, visible, keys, depth + 1);
    }

    private static void SetPieceOutline(ILayoutInstance* instance, ObjectHighlightColor colour, int depth)
    {
        if (instance == null || depth >= 4) return;
        if (instance->Id.Type == InstanceType.BgPart && ((BgPartsLayoutInstance*)instance)->GraphicsObject is var bg && bg != null) bg->OutlineColor = colour;
        if (instance->Id.Type is not InstanceType.SharedGroup) return;
        var children = ((SharedGroupLayoutInstance*)instance)->Instances.Instances;
        for (long i = 0; i < children.LongCount; i++)
            if (children[i].Value != null) SetPieceOutline(children[i].Value->Instance, colour, depth + 1);
    }

    // Colliders of pieces shown again (or kept solid) get their saved flags and mask back.
    private void RestoreRemodelColliders(List<uint> keys)
    {
        var wanted = new HashSet<uint>(keys);
        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        var scene = framework == null || framework->BGCollisionModule == null || framework->BGCollisionModule->SceneManager == null
            || framework->BGCollisionModule->SceneManager->FirstScene == null ? null : framework->BGCollisionModule->SceneManager->FirstScene->Scene;
        if (scene == null) return;
        foreach (var collider in scene->Colliders)
        {
            var owner = (uint)(collider->LayoutObjectId & 0xFFFFFFFF);
            if (owner == 0 || !wanted.Contains(owner) || !colliderSavedState.TryGetValue(owner, out var saved)) continue;
            collider->VisibilityFlags = saved.vf;
            collider->LayerMask = saved.layerMask;
        }
        foreach (var key in wanted) colliderSavedState.Remove(key);
    }
}
