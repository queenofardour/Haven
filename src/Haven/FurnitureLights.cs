using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Haven.Core;
using RenderLight = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Light;
using SceneLight = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Light;

namespace Haven;

/// <summary>
/// Furnishing lights Haven draws itself, without Stagehand, when Settings → Advanced → "Draw
/// furnishing lights with Haven" is on; otherwise Stagehand draws them, as before. Each light is a scene light of the
/// game's own (Scene.Light.Create), set up, updated and destroyed as Stagehand 0.5.5's LiveLight does
/// (UniversalConquistador, AGPL-3.0-or-later; THIRD_PARTY_NOTICES.md): the render light reads its transform from the
/// scene light, its range is unlimited, and its materials are updated every frame. Only lights made here are touched.
/// Framework thread only.
/// </summary>
internal sealed unsafe class FurnitureLights(FurnitureCatalogue catalogue, IDataManager data, IPluginLog log) : IDisposable
{
    private readonly record struct Key(Guid Id, int Light);
    private sealed class Live(nint pointer)
    {
        public nint Pointer = pointer;
        public NativeLight? Applied;
        public string Texture = "";
        public int Seen;
    }
    private readonly Dictionary<Key, Live> lights = [];
    private readonly List<Key> removed = [];
    private readonly Dictionary<string, bool> textures = new(StringComparer.Ordinal);
    private int generation;
    private bool failed, reported;
    private static readonly Guid CanvasFill = new("6a1f0c3e-2b7d-4e59-9c41-8d2e5f7a0b13");

    /// <summary>How many lights Haven draws now, and how many of them belong to world pieces.</summary>
    public (int All, int World) Count { get; private set; }

    /// <summary>
    /// Draws the home's lights as Stagehand would be given them: every shown light of every furnishing and building piece
    /// in <paramref name="display"/> (the home as drawn: without a piece being moved), world pieces' lights included (up
    /// to NativeLights.MaxWorldLights), and the blank canvas's fill light.
    /// </summary>
    public void Update(House display, Func<Furnishing, bool> switchOn)
    {
        if (failed) return;
        generation++;
        var parent = FurnitureCatalogue.HouseMatrix(display);
        int all = 0, world = 0;
        if (display.CustomLayout) { Apply(new(CanvasFill, 0), NativeLights.CanvasFill(parent, display.Interior.Brightness)); all++; }
        foreach (var item in display.Furnishings)
        {
            var prefab = catalogue.Prefab(item);
            if (prefab.Lights.Length == 0 && prefab.WorldLights.Length == 0) continue;
            var on = prefab.Switch == null || switchOn(item);
            var matrix = FurnitureCatalogue.ItemMatrix(item) * parent;
            // A furnishing's or building piece's lights, then a world group's (numbered after them).
            for (var i = 0; i < prefab.Lights.Length + prefab.WorldLights.Length; i++)
            {
                var worldPiece = i >= prefab.Lights.Length;
                var light = worldPiece ? prefab.WorldLights[i - prefab.Lights.Length] : prefab.Lights[i];
                if (!PrefabState.Shown(light.Switch, on)) continue;
                if (worldPiece && world >= NativeLights.MaxWorldLights) continue;
                // As Stagehand is given it: the light's own place and turn (its scale dropped) inside the piece.
                Matrix4x4.Decompose(light.Transform, out _, out var turn, out var at);
                var spec = NativeLights.FromLayout(light.Light, Matrix4x4.CreateFromQuaternion(turn) * Matrix4x4.CreateTranslation(at) * matrix);
                if (!spec.Sane) continue;
                if (Apply(new(item.Id, i), spec)) { all++; if (worldPiece) world++; }
            }
        }
        removed.Clear();
        foreach (var (key, live) in lights) if (live.Seen != generation) removed.Add(key);
        foreach (var key in removed) { Destroy(lights[key]); lights.Remove(key); }
        if (Count != (all, world))
        {
            Count = (all, world);
            if (!reported && all > 0) { reported = true; log.Information("[LIGHTS] Haven draws {All} light(s) itself ({World} from world pieces).", all, world); }
        }
    }

    // Makes or updates one light; false when it couldn't be made.
    private bool Apply(Key key, NativeLight spec)
    {
        if (lights.TryGetValue(key, out var live) && live.Applied?.Shape != spec.Shape) { Destroy(live); lights.Remove(key); live = null; }
        if (live == null)
        {
            SceneLight* made;
            try { made = SceneLight.Create((LightShape)spec.Shape, "Haven.Light", null); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed = true;
                log.Error(ex, "[LIGHTS] The game couldn't make a light; Haven stops drawing its own until it's reloaded.");
                return false;
            }
            if (made == null) { log.Warning("[LIGHTS] The game made no light."); return false; }
            // As Stagehand's LiveLight starts one: the render light follows the scene light's transform, its range unlimited.
            made->RenderLight->Transform = (Transform*)&made->Position;
            made->RenderLight->MaxRange = RenderLight.UnlimitedMaxRange;
            lights[key] = live = new((nint)made);
        }
        live.Seen = generation;
        var light = (SceneLight*)live.Pointer;
        if (live.Applied != spec)
        {
            var render = light->RenderLight;
            render->LightShape = (LightShape)spec.Shape;
            render->LightFlags = (LightFlags)spec.Flags;
            render->Color = spec.Color;
            render->Intensity = spec.Intensity;
            SetTexture(live, light, spec.Texture);
            render->ShadowPlaneNear = spec.ShadowNear;
            render->ShadowPlaneFar = spec.ShadowFar;
            render->FalloffType = spec.Curve switch { LightCurve.Linear => LightFalloffType.Linear, LightCurve.Quadratic => LightFalloffType.Quadratic, _ => LightFalloffType.Cubic };
            // The render light keeps the skew in radians, whatever its field's name says (as Stagehand writes it).
            render->FlatLightSkewAngleDegrees = spec.FlatSkewDegrees * MathF.PI / 180;
            render->FalloffFactor = spec.FalloffFactor;
            render->SpotLightAngleDegrees = spec.SpotDegrees;
            render->AngularFalloffDegrees = spec.ConeFalloffDegrees;
            render->Range = spec.Range;
            render->CharacterShadowRange = spec.CharacterShadowRange;
            light->Position = spec.Position; light->Rotation = spec.Rotation; light->Scale = spec.Scale;
            light->NotifyTransformChanged();
            light->UpdateTransforms(false);
            live.Applied = spec;
        }
        light->UpdateMaterials();
        return true;
    }

    // A projected texture (a lamp's pattern), as Stagehand sets one: the old one released, the new one loaded, and the
    // scene light told which it has. Only a texture this game has is asked for: one it lacks (a free trial's missing
    // expansion) would close the game, so the light shines without it.
    private void SetTexture(Live live, SceneLight* light, string path)
    {
        if (path.Length > 0 && !Installed(path)) path = "";
        if (live.Texture == path) return;
        var next = path.Length > 0 ? ResourceHandle.GetAsync(path) : null;
        var old = light->ProjectedTexture;
        light->ProjectedTexture = (TextureResourceHandle*)next;
        if (old != null)
        {
            old->DecRef();
            var kernel = light->RenderLight->ProjectedTexture;
            light->RenderLight->ProjectedTexture = null;
            if (kernel != null) kernel->DecRef();
        }
        light->OutlineFlags = (byte)(light->OutlineFlags & 0xF0 | (next != null ? 2 : 4));
        live.Texture = path;
    }
    private bool Installed(string path)
    {
        if (textures.TryGetValue(path, out var known)) return known;
        try { known = data.FileExists(path); } catch (Exception ex) when (ex is not OutOfMemoryException) { known = false; }
        if (!known) log.Warning("[LIGHTS] A light's projected texture {Path} isn't in this game; the light shines without it.", path);
        return textures[path] = known;
    }

    private static void Destroy(Live live)
    {
        if (live.Pointer == 0) return;
        var light = (SceneLight*)live.Pointer; live.Pointer = 0;
        light->CleanupRender(); light->Dtor(1);
    }
    public void Clear()
    {
        foreach (var live in lights.Values) Destroy(live);
        lights.Clear();
        Count = (0, 0);
        reported = false;
    }
    public void Dispose() => Clear();
}
