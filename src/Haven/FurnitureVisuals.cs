using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Haven.Core;
using Haven.Native.PatchCheck;

namespace Haven;

// Own only models created here; never enumerate or mutate another plugin's objects.
// BgObject creation, transform notification and destruction follow the public client
// structs and Stagehand's documented lifetime pattern. See THIRD_PARTY_NOTICES.md.
internal sealed unsafe class FurnitureVisuals(FurnitureCatalogue catalogue, IPluginLog log, BuildingMaterials materials) : IDisposable
{
    public bool BuildingFinishesAvailable => materials.Available;
    private readonly record struct Key(Guid Id, int Part, bool Preview, Guid Proposal = default);
    // The glow canary: a new model's +0xCD reads 255 (the client's constructor) while the field is where Haven thinks.
    private static int glowReadings;
    private void GlowCanary(byte value)
    {
        if (value != 255) { PatchGuard.Canary(PatchFeature.FurnishingGlow, $"a new model's glow byte (BgObject +0xCD) read {value}, not 255"); return; }
        if (++glowReadings == 3) PatchGuard.CanaryPassed(PatchFeature.FurnishingGlow);
    }

    private sealed class Live(nint pointer, string path)
    {
        public nint Pointer = pointer;
        public readonly string Path = path;
        public string? Material;
        public bool MaterialApplied;
        public Matrix4x4 Transform;
        public uint AppliedStain = uint.MaxValue;
        public int Seen;
        public readonly DateTimeOffset Created = DateTimeOffset.UtcNow;
        public bool Warned;
        public bool WasReady;
        public bool GlowChecked;
    }
    private readonly Dictionary<Key, Live> objects = [];
    private readonly List<Key> removed = [];
    private int generation;
    private long clockSeconds;
    private long animationMilliseconds;
    // Milliseconds since an item's switch last changed; long.MaxValue when it has not changed during this visit.
    private Func<Furnishing, long>? switchAge;
    private IReadOnlyList<StagedFurniture>? stagedSource;
    private readonly Dictionary<Guid, StagedFurniture[]> stagedItems = [];
    public bool SceneReady => objects.Values.All(x => x.WasReady && (x.Material == null || x.MaterialApplied));
    /// <summary>
    /// Furnishings drawn so far, of those created (the loading screen's progress): a piece counts once every one of its
    /// models is drawn. Previews and proposals are left out.
    /// </summary>
    public (int Ready, int Count) Readiness
    {
        get
        {
            var pieces = objects.Where(x => !x.Key.Preview && x.Key.Proposal == default).GroupBy(x => x.Key.Id).ToArray();
            return (pieces.Count(p => p.All(x => x.Value.WasReady && (x.Value.Material == null || x.Value.MaterialApplied))), pieces.Length);
        }
    }
    // The hovered interactable furnishing gets the game's own housing-object outline, which follows its geometry.
    public Guid? Highlighted { get; set; }
    /// <summary>The one-shot timeline a furnishing is playing (an arcade machine's result) and how long ago it began.</summary>
    public Func<Furnishing, (int SubId, long Since)?>? MachineAction { get; set; }

    public void Update(House house, Guid? moving, Furnishing? preview, IReadOnlyList<StagedFurniture> staged, FurnitureDoorScene doors, long eorzeaSeconds, long milliseconds,
        Func<Furnishing, bool> switchOn, Func<Furnishing, long>? switchAge = null, IReadOnlyList<Furnishing>? followers = null)
    {
        this.switchAge = switchAge;
        animationMilliseconds = milliseconds;
        clockSeconds = eorzeaSeconds;
        generation++;
        if (!ReferenceEquals(stagedSource, staged))
        { stagedSource = staged; stagedItems.Clear(); foreach (var group in staged.GroupBy(s => s.Item.Id)) stagedItems[group.Key] = group.ToArray(); }
        var parent = FurnitureCatalogue.HouseMatrix(house);
        if (house.CustomLayout) Apply(CustomCanvas.Platform, parent, false, doors: doors);
        foreach (var item in house.Furnishings)
        {
            if (item.Id == moving || followers != null && Following(followers, item.Id)) continue;
            // Keep the published object at its solid location while a proposed move is a
            // ghost elsewhere. Same-position appearance/removal previews replace its visual.
            if (stagedItems.TryGetValue(item.Id, out var pending) && pending.Any(p => p.Deleted
                || p.Item.Rotation == item.Rotation && p.Item.Scale == item.Scale && p.Item.Position.SequenceEqual(item.Position))) continue;
            Apply(item, parent, false, doors: doors, switchOn: switchOn);
        }
        if (preview != null) Apply(preview, parent, true);
        if (followers != null) foreach (var follower in followers) Apply(follower, parent, true);
        foreach (var item in staged) Apply(item.Item, parent, true, item.Proposal);
        removed.Clear();
        foreach (var (key, live) in objects) if (live.Seen != generation) removed.Add(key);
        foreach (var key in removed) { Destroy(objects[key]); objects.Remove(key); }
    }
    private static bool Following(IReadOnlyList<Furnishing> followers, Guid id)
    { for (var i = 0; i < followers.Count; i++) if (followers[i].Id == id) return true; return false; }
    public bool PlatformReady
    {
        get
        {
            var found = false;
            foreach (var (key, live) in objects)
                if (key.Id == CustomCanvas.PlatformId) { found = true; if (!live.WasReady) return false; }
            return found;
        }
    }
    private void Apply(Furnishing item, Matrix4x4 parent, bool preview, Guid proposal = default, FurnitureDoorScene? doors = null,
        Func<Furnishing, bool>? switchOn = null)
    {
        var entry = catalogue.Get(item); var prefab = catalogue.Prefab(item);
        // Previews and proposals show the item's default side; placed items follow their current side.
        var on = prefab.Switch == null || (switchOn?.Invoke(item) ?? prefab.Switch.DefaultOn);
        var matrix = FurnitureCatalogue.ItemMatrix(item) * parent;
        for (var i = 0; i < prefab.Models.Length; i++)
        {
            var model = prefab.Models[i]; var key = new Key(item.Id, i, preview, proposal);
            if (!PrefabState.Shown(model.Switch, on)) continue;
            if (objects.TryGetValue(key, out var old) && (old.Path != model.Path || old.Material != model.Material)) { Destroy(old); objects.Remove(key); }
            if (!objects.TryGetValue(key, out var live))
            {
                var pointer = BgObject.Create(model.Path, "Haven.Furnishing");
                if (pointer == null) throw new InvalidOperationException("The furnishing model could not be created: " + entry.Name);
                pointer->IsVisible = false;
                objects[key] = live = new((nint)pointer, model.Path) { Material = model.Material };
            }
            live.Seen = generation;
            var obj = (BgObject*)live.Pointer;
            var modelTransform = model.Transform;
            if (!preview && model.Door is {} motion && doors != null)
                modelTransform = DoorMovement.Transform(prefab.Doors[motion.Door].Action, motion.Panel, motion.Local, motion.Parent, doors.Progress(item.Id, motion.Door));
            else if (!preview && model.Clock is {} clock)
                modelTransform = ClockMovement.Transform(clock.Hour, clock.Local, clock.Parent, clockSeconds);
            else if (!preview && model.Actions != null && MachineAction?.Invoke(item) is { } act && model.Actions.TryGetValue(act.SubId, out var action))
                modelTransform = action.Pose(act.Since, model.Transform);
            else if (!preview && model.Loop is {} loop)
                modelTransform = LoopMovement.Transform(loop, animationMilliseconds, on, switchAge?.Invoke(item) ?? long.MaxValue, FurnitureCatalogue.MotionSeed(item));
            var world = modelTransform * matrix;
            var ready = obj->ModelResourceHandle != null && obj->ModelResourceHandle->LoadState >= 7;
            if (world != live.Transform)
            {
                if (!Matrix4x4.Decompose(world, out var scale, out var rotation, out var position)) throw new InvalidDataException("Invalid furnishing transform.");
                obj->Position = position; obj->Rotation = rotation; obj->Scale = scale;
                obj->NotifyTransformChanged();
                if (ready) obj->UpdateTransforms(false);
                live.Transform = world;
            }
            obj->SetTransparency(proposal != Guid.Empty ? .55f : preview ? 0.4f : 0);
            var outline = !preview && proposal == Guid.Empty && item.Id == Highlighted && prefab.Answers(model)
                ? ObjectHighlightColor.Blue : ObjectHighlightColor.None;
            if (obj->OutlineColor != outline) obj->OutlineColor = outline;
            // A switched part's glow (C110: a lamp shade dims when off), kept by the client as a byte at +0xCD that
            // its renderer draws the emissive at; 255 is the authored glow. Previews show their default side, settled.
            // A looping timeline's flicker (the Enigma Partition's) runs on the shared animation clock instead.
            var playing = preview ? null : MachineAction?.Invoke(item);
            var blink = playing is { } b && model.ActionLoopGlows?.FirstOrDefault(g => g.SubId == b.SubId) is { Glow: not null } started
                && b.Since / 1000.0 * SwitchSound.FramesPerSecond is var frame && frame >= started.Start && (started.Stop is not { } stop || frame < stop)
                ? started.Glow.Byte(b.Since - (long)(started.Start * 1000 / SwitchSound.FramesPerSecond)) : (byte?)null;
            var glow = blink is { } blinking ? blinking
                : playing is { } p && model.ActionGlows?.TryGetValue(p.SubId, out var fades) == true ? GlowFade.ToByte(SgbTimelines.GlowLevel(fades!, p.Since))
                : model.LoopGlow is {} lap ? lap.Byte(animationMilliseconds)
                : model.Glow is {} fade ? fade.Byte(on, preview ? long.MaxValue : switchAge?.Invoke(item) ?? long.MaxValue) : (byte)255;
            // +0xCD isn't in ClientStructs: its first reading on each new model checks it's still the glow (the client's
            // constructor sets 255), and after a game patch it's written only once that check passes (PatchGuard).
            if (!live.GlowChecked) { live.GlowChecked = true; GlowCanary(((byte*)obj)[0xCD]); }
            if (((byte*)obj)[0xCD] != glow && PatchGuard.Allows(PatchFeature.FurnishingGlow)) ((byte*)obj)[0xCD] = glow;
            // A one-shot's colour override (C113: a gathering machine's red flash), as the client's setter writes it:
            // +0xCE intensity, +0xD0..+0xD2 colour, +0xD3 blend; none (0,0,0,0 at intensity 1) otherwise.
            if (model.ActionColours != null)
            {
                var (intensity, r, g, bl, a) = SgbTimelines.ColourBytes(playing is { } c && model.ActionColours.TryGetValue(c.SubId, out var steps)
                    ? SgbTimelines.ColourAt(steps, c.Since) : Vector4.Zero);
                // ClientStructs' ColorIntensity (+0xCE) and Color (+0xD0..+0xD3).
                if (obj->ColorIntensity != intensity || obj->Color.R != r || obj->Color.G != g || obj->Color.B != bl || obj->Color.A != a)
                { obj->ColorIntensity = intensity; obj->Color.R = r; obj->Color.G = g; obj->Color.B = bl; obj->Color.A = a; }
            }
            if (ready)
            {
                if (!live.WasReady) { obj->UpdateTransforms(false); live.WasReady = true; }
                if (live.Material != null && !live.MaterialApplied)
                {
                    live.MaterialApplied = materials.Apply(obj, live.Material);
                    if (!live.MaterialApplied && DateTimeOffset.UtcNow - live.Created > TimeSpan.FromSeconds(10))
                        throw new InvalidOperationException("Building surface material buffer did not become ready: " + entry.Name);
                }
                // The colour-change shader takes the model's colour from the dye buffer alone, and a fresh buffer is
                // zero, which draws black. Undyed, the game writes the furnishing's own default dye (its prefab's
                // DefaultColorID), so Haven does too. The dye buffer can appear a moment after the model is ready, so
                // the write is retried like a dye's; a model still without one after ten seconds has none.
                var stain = entry.DyeSlots > 0 ? item.StainId : 0;
                if (live.AppliedStain != stain)
                {
                    var rgba = stain == 0 ? catalogue.UndyedRgba(item, model.Path) : catalogue.StainRgba(stain);
                    if (rgba is not uint colour) live.AppliedStain = stain;
                    else if (obj->TrySetStainColor(new ByteColor { RGBA = colour })) live.AppliedStain = stain;
                    else if (stain == 0) { if (DateTimeOffset.UtcNow - live.Created > TimeSpan.FromSeconds(10)) live.AppliedStain = 0; }
                    else if (!live.Warned && DateTimeOffset.UtcNow - live.Created > TimeSpan.FromSeconds(5))
                    { live.Warned = true; log.Warning("Furnishing dye buffer unavailable: {Item} ({Model})", entry.Name, model.Path); }
                }
                // A part a one-shot timeline shows and hides (Monster Toss's ball) is drawn only while it's shown.
                obj->IsVisible = playing is { } act2 && model.ActionShows?.TryGetValue(act2.SubId, out var changes) == true
                    ? SgbTimelines.ShownAt(changes!, act2.Since, !model.HiddenAtRest) : !model.HiddenAtRest;
            }
        }
    }
    private static void Destroy(Live live)
    {
        if (live.Pointer == 0) return;
        var obj = (BgObject*)live.Pointer; live.Pointer = 0;
        obj->CleanupRender(); obj->Dtor(1);
    }
    public void Clear() { foreach (var live in objects.Values) Destroy(live); objects.Clear(); }
    public void Dispose() => Clear();
}
