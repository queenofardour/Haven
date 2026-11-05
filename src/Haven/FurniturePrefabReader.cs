using System.Numerics;
using System.Collections.Concurrent;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using Lumina.Data.Parsing.Layer;
using Haven.Core;

namespace Haven;

internal sealed record PrefabDoorPart(int Door, int Panel, Matrix4x4 Local, Matrix4x4 Parent);
internal sealed record PrefabDoor(DoorAction Action, DoorVolume Trigger);
internal sealed record PrefabClockPart(bool Hour, Matrix4x4 Local, Matrix4x4 Parent);
internal sealed record PrefabModel(string Path, Matrix4x4 Transform, Vector3 Minimum, Vector3 Maximum, string? Material = null, uint? FinishPart = null, PrefabDoorPart? Door = null, PrefabClockPart? Clock = null, StateActivity? Switch = null, LoopLevel[]? Loop = null)
{
    // The glow a switch fades on this part (C110: a lamp shade dark while off); null keeps the authored glow.
    public SwitchGlow? Glow { get; init; }
    // The furnishing's one-shot timelines that move this part, by SubId (an arcade machine's hammer swing or recoil).
    public IReadOnlyDictionary<int, ActionMotion>? Actions { get; init; }
    // A one-shot timeline's glow fades (C110) on this part, by SubId: an arcade machine's lamps reacting to a result.
    public IReadOnlyDictionary<int, GlowFade[]>? ActionGlows { get; init; }
    // A one-shot timeline's shows and hides (C104/C019) of this part, by SubId; HiddenAtRest when the default timeline
    // hides it (Monster Toss's ball, seen only in the air).
    public IReadOnlyDictionary<int, (int Frame, bool Shown)[]>? ActionShows { get; init; }
    public bool HiddenAtRest { get; init; }
    // A one-shot timeline's colour overrides (C113) on this part, by SubId: a gathering machine flashing red on a miss.
    public IReadOnlyDictionary<int, (int Start, int Frames, Vector4 Rgba)[]>? ActionColours { get; init; }
    // A nested looping timeline a one-shot starts and stops (C082/C133) that flickers this part's glow: the Ale Tap's
    // lamp blinking while it pours. By the one-shot's SubId, from its start frame to its stop.
    public (int SubId, int Start, int? Stop, LoopGlow Glow)[]? ActionLoopGlows { get; init; }
    // The glow a looping timeline flickers on this part every lap (the Enigma Partition's); it overrides Glow.
    public LoopGlow? LoopGlow { get; init; }
    // A display's placeholder in the model's native replaceable material (its header's BGChangeMaterialIndex): a
    // frame's pic/ta/0000 or a partition's dyna/mat/wl/0000, which a chosen painting or wallpaper replaces.
    public string? ChangeSlot { get; init; }
}
internal sealed record PrefabCollider(string? Path, Matrix4x4 Transform, Vector3 Minimum, Vector3 Maximum, int Door = -1)
{
    // Solid only while the item's switch is on this side: a door leaf with no collision of its own blocks while closed.
    public bool? WhenOn { get; init; }
}
internal sealed record PrefabSeat(Matrix4x4 Transform, bool Bed);
internal sealed record PrefabLight(Matrix4x4 Transform, LayoutLight Light, StateActivity? Switch = null);
internal sealed record PrefabEffect(string Path, Matrix4x4 Transform, Vector4 Color, StateActivity? Switch = null);
internal sealed record PrefabSound(string Path, Matrix4x4 Transform);
// A sound one side of the switch starts on entering that side, with the frames (30 fps) it starts and stops at.
internal sealed record PrefabSwitchSound(string Path, Matrix4x4 Transform, SwitchSound Timing)
{
    public bool On => Timing.On;
    public int StartFrame => Timing.StartFrame;
    public int? StopFrame => Timing.StopFrame;
    public long? PlayMilliseconds(long? natural) => Timing.PlayMilliseconds(natural);
}
// A sound a looping animation plays once per lap, at its frame, in time with the motion.
internal sealed record PrefabLoopSound(string Path, Matrix4x4 Transform, LoopSound Loop);
// An effect a looping animation shows in a window of each lap (a drip, a splash), restarted every lap.
internal sealed record PrefabLoopEffect(string Path, Matrix4x4 Transform, Vector4 Color, LoopEffect Loop, bool AutoPlay);
// An effect a one-shot timeline shows (an arcade machine's punch flash), from its start frame to its stop (or 3 seconds).
internal sealed record PrefabActionEffect(int SubId, string Path, Matrix4x4 Transform, Vector4 Color, int Start, int? Stop)
{
    public bool Showing(long sinceMilliseconds)
    {
        var frame = sinceMilliseconds / 1000.0 * SwitchSound.FramesPerSecond;
        return frame >= Start && frame < (Stop ?? Start + 3 * SwitchSound.FramesPerSecond);
    }
}
internal sealed record FurniturePrefab(PrefabModel[] Models, PrefabCollider[] Colliders, PrefabSeat[] Seats, PrefabLight[] Lights)
{
    public PrefabDoor[] Doors { get; init; } = [];
    // Passive autoplay members outside every timeline: flames, steam, water and their ambient loops.
    public PrefabEffect[] Effects { get; init; } = [];
    public PrefabSound[] Sounds { get; init; } = [];
    public PrefabSwitchSound[] SwitchSounds { get; init; } = [];
    public PrefabLoopSound[] LoopSounds { get; init; } = [];
    public PrefabLoopEffect[] LoopEffects { get; init; } = [];
    // The dye an undyed furnishing shows: its prefab's HousingSettings.DefaultColorID, a Stain row. Every installed
    // dyeable furnishing has one (a Riviera Chair's is Wine Red); the game writes it like a dye when no dye is chosen.
    public ushort? DefaultDye { get; init; }
    // Timeline SubIds by index, as the game plays a furnishing's timelines by number (an arcade machine's results).
    public byte[] ActionTable { get; init; } = [];
    // The sounds those timelines play, by SubId.
    public (int SubId, PrefabSwitchSound Sound)[] ActionSounds { get; init; } = [];
    public PrefabActionEffect[] ActionEffects { get; init; } = [];
    // A furnishing used rather than switched (the Ale Tap): its one-shot timeline's SubId, played whole on a click.
    public int? UseTimeline { get; init; }
    // An on/off pair whose parts, effects or lights differ between sides; null when the item has none.
    public PrefabSwitch? Switch { get; init; }

    // A part the switch moves, starts, or shows on one side only: a door's leaves.
    public static bool SwitchPart(PrefabModel model) => model.Switch != null || model.Loop?.Any(x => x.Switch != null || x.WhenOn) == true;
    // A walk-through door set in a large frame (the Factory Automatic Door, the Gaol Partition Door) is opened at its
    // leaves alone: they are person-sized (2 m tall) yet cover under half the item's face. A locker's door is most of
    // the locker and a lid is too small to aim at, so those still answer anywhere on the item.
    public bool DoorPartsOnly => Switch != null && doorShaped;
    // An arcade machine answers at its play spot (Cuff-a-Cur's Gilgamesh, the claw machine's console), named by model.
    public string[]? AnswerParts { get; init; }
    // Whether only some parts answer the cursor and take the outline, and whether this one does.
    public bool PartsOnly => AnswerParts != null || DoorPartsOnly;
    public bool Answers(PrefabModel model) => AnswerParts is { } parts ? parts.Contains(System.IO.Path.GetFileNameWithoutExtension(model.Path))
        : !DoorPartsOnly || SwitchPart(model);
    private readonly bool doorShaped = Face(Models.Where(SwitchPart)) is var (parts, height) && height >= 2 && parts < Face(Models).Area / 2;
    private static (float Area, float Height) Face(IEnumerable<PrefabModel> models)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var model in models)
        { var box = PlacementBounds.Transform(model.Minimum, model.Maximum, model.Transform); min = Vector3.Min(min, box.Minimum); max = Vector3.Max(max, box.Maximum); }
        if (min.X > max.X) return (0, 0);
        float[] size = [max.X - min.X, max.Y - min.Y, max.Z - min.Z];
        Array.Sort(size);
        return (size[1] * size[2], max.Y - min.Y);
    }
}
// OnlySound: the pair only plays sound, so it starts on its silent side and falls silent when its piece ends.
// Verb: how its sides are named. InspectorOnly: a seat's look (a Broken Heart Chair's heart) is chosen in the
// inspector rather than by clicking where one sits.
// ClosedOn: the side a container's moving doors and lids rest on, which is how its model was built: closed.
// Lamp: the sides switch a light.
internal sealed record PrefabSwitch(bool DefaultOn, bool OnlySound = false, SwitchVerb Verb = SwitchVerb.TurnOn, bool InspectorOnly = false,
    bool ClosedOn = false, bool Lamp = false);
internal static class PrefabState
{
    // Parts without switch activity are always shown; switch-controlled ones follow the item's current side.
    public static bool Shown(StateActivity? activity, bool on) => activity is not {} a || a.Active(on, true);
}

// Parses the installed prefab, including nested parts and collision references. No game assets are copied to saves.
internal sealed class FurniturePrefabReader(Func<string, SgbFile?> readGroup, Func<string, MdlFile?> readModel, Func<string, bool> exists)
{
    private readonly ConcurrentDictionary<ushort, FurniturePrefab> cache = new();
    // The installed data names most sides only on/off, and containers move just as figurines do, so these are listed:
    // Mhachi Coffin, Crystarium Mechanical Till, Ironfrog Keeper, Factory Automatic Door, Icebox, Mogbureau, Steel Locker,
    // Gaol Partition Door.
    private static readonly HashSet<ushort> Containers = [403, 823, 958, 974, 1052, 1064, 1142, 1143];
    // Star Sapphire and Star Ruby Music Boxes: their on side plays a tune.
    private static readonly HashSet<ushort> MusicBoxes = [385, 386];
    // What the root switch's on side starts inside one group, following its chain (TimelineCascade): the group's own
    // repeating controllers, its timelines by ID, and "on" passed down by C160. Members: the same for each nested
    // member. Looping parts these move run only while the item is on; one-shot motion poses each side, with the
    // timelines the off side plays (a Factory Automatic Door's open and close).
    private sealed class SwitchPlan
    {
        public bool Controllers;
        public bool Broadcast, OffBroadcast;
        public readonly HashSet<int> Timelines = [], OffTimelines = [];
        public readonly Dictionary<uint, SwitchPlan> Members = [];
        public SwitchPlan Member(uint id) => Members.TryGetValue(id, out var plan) ? plan : Members[id] = new();
    }
    // Whether the chain moved anything, and whether it reached a command Haven does not play.
    private sealed class SwitchChain { public bool Moves; public bool Unsupported; }

    public FurniturePrefab GetBuildingGroup(string root, bool innRoom = false)
    {
        List<PrefabModel> models = []; List<PrefabCollider> colliders = []; List<PrefabSeat> seats = []; List<PrefabLight> lights = [];
        List<PrefabDoor> doors = [];
        lock (reading)
        {
            readingInn = innRoom;
            try { Read(root, Matrix4x4.Identity, 0, new HashSet<string>(StringComparer.Ordinal), models, colliders, seats, lights, doors, true); }
            finally { readingInn = false; }
        }
        if (models.Count == 0) throw new InvalidDataException("This building group has no supported model: " + root);
        return new(models.ToArray(), colliders.ToArray(), [], lights.ToArray()) { Doors = doors.ToArray() };
    }
    /// <summary>
    /// A world group as a building piece: read like an inn's groups, with up to
    /// <paramref name="maxModels"/> parts, and with any motion or door data this reader can't follow left still instead
    /// of refusing the group (a world group is scenery: the Alexandria airship's propeller swing, for one, is authored
    /// past the furniture limits).
    /// </summary>
    public FurniturePrefab GetWorldGroup(string root, int maxModels)
    {
        lock (reading)
        {
            readingWorld = true; WorldModelLimit = maxModels;
            try { return GetBuildingGroup(root, innRoom: true); }
            finally { readingWorld = false; }
        }
    }
    private bool readingWorld;
    private int WorldModelLimit = 128;
    // A group's motion data, or none for a world group whose data this reader refuses.
    private T Still<T>(Func<T> read, T none)
    {
        if (!readingWorld) return read();
        try { return read(); }
        catch (InvalidDataException) { return none; }
    }
    // Custom furniture imports read prefabs off the framework thread, so reads are one at a time.
    private readonly object reading = new();
    public FurniturePrefab Get(ushort modelKey) { lock (reading) return Read(modelKey); }
    // Aquarium fish swim their tank's ring paths at this speed, in metres a second (an estimate: the game moves them
    // in code; the medium tank's authored 1,024-frame lap on its ~3 m path is about 0.09).
    private const float FishSpeed = 0.12f;
    // A ClientPath's control points; the 0xFFFF point closes a ring and repeats the first point.
    private static Vector3[] PathPoints(LayerCommon.ClientPathInstanceObject path) =>
        [.. (path.ParentData.ControlPointsArray ?? []).Where(p => p.PointId != ushort.MaxValue).Select(p => new Vector3(p.Translation.X, p.Translation.Y, p.Translation.Z))];
    private static float Length(Vector3[] points) { var total = 0f; for (var i = 1; i < points.Length; i++) total += Vector3.Distance(points[i - 1], points[i]); return total; }

    // A furnishing with its empty groups filled (a vase's flowers, an aquarium's fish, water and decoration): swap gives
    // the group to read instead of a nested group's path, by how many times that path came before (a tank's fish
    // spots share one), or null to keep it. Cached under the key.
    private readonly Dictionary<(ushort, string), FurniturePrefab> filled = [];
    // The root's one-shot timelines' sounds, gathered while a prefab is read.
    private readonly List<(int SubId, PrefabSwitchSound Sound)> actionSoundList = [];
    private readonly List<PrefabActionEffect> actionEffectList = [];
    private Func<string, int, string?>? swap;
    private readonly Dictionary<string, int> swapSeen = new(StringComparer.Ordinal);
    public FurniturePrefab Get(ushort modelKey, string key, Func<string, int, string?> fill)
    {
        lock (reading)
        {
            if (filled.TryGetValue((modelKey, key), out var known)) return known;
            swap = fill; swapSeen.Clear();
            try { return filled[(modelKey, key)] = Read(modelKey, cacheIt: false); }
            finally { swap = null; }
        }
    }
    private FurniturePrefab Read(ushort modelKey, bool cacheIt = true)
    {
        if (cacheIt && cache.TryGetValue(modelKey, out var cached)) return cached;
        var root = $"bgcommon/hou/indoor/general/{modelKey:D4}/asset/fun_b0_m{modelKey:D4}.sgb";
        List<PrefabModel> models = []; List<PrefabCollider> colliders = []; List<PrefabSeat> seats = []; List<PrefabLight> lights = [];
        List<PrefabDoor> doors = []; List<PrefabEffect> effects = []; List<PrefabSound> sounds = []; List<PrefabSwitchSound> switchSounds = [];
        List<PrefabLoopSound> loopSounds = []; List<PrefabLoopEffect> loopEffects = [];
        actionSoundList.Clear(); actionEffectList.Clear();
        bool? defaultOn = null; var switches = false; SgbSwitch? toggle = null;
        // Which side each moving part is modelled in; the parts must agree for it to decide the default side.
        HashSet<bool?> resting = [];
        var verb = Containers.Contains(modelKey) ? SwitchVerb.Open : MusicBoxes.Contains(modelKey) ? SwitchVerb.Play : SwitchVerb.TurnOn;
        var switchChain = new SwitchChain();
        Read(root, Matrix4x4.Identity, 0, new HashSet<string>(StringComparer.Ordinal), models, colliders, seats, lights, doors, effects: effects, sounds: sounds,
            onSwitch: s => { defaultOn ??= s.DefaultOn; switches = true; toggle ??= s; }, switchSounds: switchSounds, loopSounds: loopSounds,
            onRest: side => resting.Add(side), loopEffects: loopEffects, closedColliders: verb == SwitchVerb.Open, switchChain: switchChain);
        if (models.Count == 0) throw new InvalidDataException("This furnishing has no supported housing model.");
        SeatTops(colliders, seats);
        var onlySound = toggle?.OnlySound == true;
        if (onlySound) verb = SwitchVerb.Play;
        // A pair that only moves, sounds or glows counts when Haven plays every command on both sides and along its
        // chain.
        var moves = toggle?.Motions.Count > 0 || switchChain.Moves;
        var varies = models.Any(m => m.Switch is {} a && a.Varies(true)) || effects.Any(e => e.Switch is {} a && a.Varies(true))
            || lights.Any(l => l.Switch is {} a && a.Varies(true))
            || toggle is { Playable: true } && !switchChain.Unsupported && (switchSounds.Count > 0 || moves || models.Any(m => m.Glow != null));
        // A lamp keeps its lit look even when a moving part rests on its unlit side. Parts that end at their authored
        // pose on both sides say nothing; the rest must agree (a Leg of Prosciutto's slices rest on its served side).
        var lit = lights.Any(l => l.Switch is {} a && a.Varies(true));
        var decided = resting.OfType<bool>().Distinct().ToArray();
        var restingSide = !lit && decided.Length == 1 ? decided[0] : (bool?)null;
        // Gears, propellers and spits a switch starts are still until it is turned on: their groups start stopped, or
        // an autoplay "off" stops them (an airship miniature's).
        if (switchChain.Moves && !lit) restingSide ??= false;
        // A switch Haven does not offer never starts what it would have: those parts keep their authored pose.
        if (!(switches && varies) && switchChain.Moves)
            for (var i = 0; i < models.Count; i++)
                if (models[i].Loop is {} chainLevels && chainLevels.Any(l => l.WhenOn))
                {
                    var still = chainLevels.Select(l => l.WhenOn ? l with { Motion = null, WhenOn = false } : l).ToArray();
                    models[i] = models[i] with { Loop = still.Any(l => l.Motion != null || l.Switch != null) ? still : null };
                }
        var prefab = new FurniturePrefab(models.ToArray(), colliders.ToArray(), seats.ToArray(), lights.ToArray())
        {
            Doors = doors.ToArray(), Effects = effects.ToArray(), Sounds = sounds.ToArray(), SwitchSounds = switchSounds.ToArray(),
            LoopSounds = loopSounds.ToArray(), LoopEffects = loopEffects.ToArray(),
            DefaultDye = readGroup(root)?.ChunkHeader.HousingSettings?.DefaultColorID is ushort dye and not 0 ? dye : null,
            ActionTable = readGroup(root) is { } rootGroup ? SgbTimelines.ActionTable(rootGroup.Data) : [],
            ActionSounds = actionSoundList.ToArray(),
            ActionEffects = actionEffectList.ToArray(),
            AnswerParts = ArcadeMachines.ByModel(modelKey)?.Parts,
            UseTimeline = switches ? null : readGroup(root) is { } used ? SgbTimelines.OneShotUse(used.Data) : null,
            // Without an authored default side a lamp starts lit and food served (their authored look), an instrument
            // silent, and anything else on the side its moving parts are modelled in (a closed lid or door).
            Switch = switches && varies ? new(defaultOn ?? restingSide ?? !onlySound, onlySound, verb, seats.Count > 0 && !lit && verb == SwitchVerb.TurnOn,
                restingSide ?? false, lit) : null,
        };
        if (cacheIt) cache[modelKey] = prefab;
        return prefab;
    }

    // A Box collider is its whole model's bounds, which for a high-backed chair or a bed with a headboard roofs over
    // the seat: one cannot jump in to sit, and /sit lands inside the box (a Broken Heart Chair's box is 2.17 m tall).
    // Such a box is cut at its highest seat, so it stays solid below the seat and one sits or stands on top of it.
    internal static void SeatTops(List<PrefabCollider> colliders, IReadOnlyList<PrefabSeat> seats)
    {
        for (var i = 0; i < colliders.Count; i++)
        {
            var box = colliders[i];
            if (box.Path != null || box.Door >= 0 || box.WhenOn != null || !Matrix4x4.Invert(box.Transform, out var inverse)) continue;
            var top = float.MinValue;
            foreach (var seat in seats)
            {
                var at = Vector3.Transform(seat.Transform.Translation, inverse);
                if (at.X > box.Minimum.X && at.X < box.Maximum.X && at.Z > box.Minimum.Z && at.Z < box.Maximum.Z
                    && at.Y > box.Minimum.Y && at.Y < box.Maximum.Y) top = Math.Max(top, at.Y);
            }
            if (top > float.MinValue && box.Maximum.Y > top + .05f) colliders[i] = box with { Maximum = box.Maximum with { Y = top } };
        }
    }

    private void Read(string path, Matrix4x4 parent, int depth, HashSet<string> chain, List<PrefabModel> models,
        List<PrefabCollider> colliders, List<PrefabSeat> seats, List<PrefabLight> lights, List<PrefabDoor> doors, bool building = false,
        bool rotating = true, bool transformsPlaying = true,
        List<PrefabEffect>? effects = null, List<PrefabSound>? sounds = null, Action<SgbSwitch>? onSwitch = null, LoopLevel[]? ancestors = null,
        List<PrefabSwitchSound>? switchSounds = null, List<PrefabLoopSound>? loopSounds = null, Action<bool?>? onRest = null,
        List<PrefabLoopEffect>? loopEffects = null, bool closedColliders = false, SwitchChain? switchChain = null, SwitchPlan? plan = null,
        (int SubId, int Timeline, int Start, int? Stop)[]? actionLoops = null)
    {
        if (depth > 4 || !chain.Add(path)) throw new InvalidDataException("Furniture prefab recursion exceeded.");
        try
        {
            var group = readGroup(path) ?? throw new InvalidDataException("The game's furniture prefab is unavailable.");
            var objects = group.LayerGroups.SelectMany(g => g.Layers).SelectMany(l => l.InstanceObjects).ToArray();
            var metadata = Still(() => SgbDoors.Read(group.Data), new DoorFileData([], new Dictionary<uint, int>()));
            var clocks = Still(() => SgbClocks.Read(group.Data), []);
            var spins = Still(() => SgbSpins.Read(group.Data), []);
            var oscillations = Still(() => SgbOscillations.Read(group.Data), new OscillationFile(false, []));
            // Switch/triggered state belongs to timelines; only members no timeline addresses are passive.
            var timed = effects == null && sounds == null ? null : SgbTimelines.Targets(group.Data);
            // The root's one-shot timelines (an arcade machine's results) move its own parts when played by index.
            var actions = depth == 0 && !building ? SgbTimelines.Actions(group.Data) : null;
            var actionSounds = depth == 0 && !building && switchSounds != null ? SgbTimelines.ActionSounds(group.Data) : new Dictionary<int, SwitchSound[]>();
            var actionEffects = depth == 0 && !building && effects != null ? SgbTimelines.ActionEffects(group.Data) : new Dictionary<int, (uint Member, int Start, int? Stop)[]>();
            var actionGlows = depth == 0 && !building ? SgbTimelines.ActionGlows(group.Data) : new Dictionary<int, IReadOnlyDictionary<uint, GlowFade[]>>();
            var actionShows = depth == 0 && !building ? SgbTimelines.ActionShows(group.Data) : new Dictionary<int, IReadOnlyDictionary<uint, (int Frame, bool Shown)[]>>();
            var actionPlays = depth == 0 && !building ? SgbTimelines.ActionPlays(group.Data) : new Dictionary<int, (uint Member, int Timeline, int Start, int? Stop)[]>();
            var actionColours = depth == 0 && !building ? SgbTimelines.ActionColours(group.Data) : new Dictionary<int, IReadOnlyDictionary<uint, (int Start, int Frames, Vector4 Rgba)[]>>();
            // Started by a one-shot above: this group's looping glows during that one-shot.
            var startedGlows = actionLoops?.SelectMany(a => SgbTimelines.LoopGlows(group.Data, a.Timeline).Select(g => (Member: g.Key, Loop: (a.SubId, a.Start, a.Stop, g.Value)))).ToArray() ?? [];
            IReadOnlyDictionary<int, T>? PerAction<T>(IReadOnlyDictionary<int, IReadOnlyDictionary<uint, T>> all, uint id)
            {
                var mine = all.Where(a => a.Value.ContainsKey(id)).ToDictionary(a => a.Key, a => a.Value[id]);
                return mine.Count > 0 ? mine : null;
            }
            // A clickable on/off pair keeps every side's parts with their activity; any other autoplay
            // switch-like timeline is applied once as the item's fixed default side.
            var toggle = effects == null ? null : SgbTimelines.Switch(group.Data);
            if (toggle != null) onSwitch?.Invoke(toggle);
            var defaults = toggle != null ? new DefaultTimelineState(new HashSet<uint>(), new HashSet<uint>())
                : Still(() => SgbTimelines.DefaultState(group.Data), new DefaultTimelineState(new HashSet<uint>(), new HashSet<uint>()));
            StateActivity? Activity(uint id) => toggle != null && toggle.Members.TryGetValue(id, out var a) ? a : null;
            SwitchGlow? GlowOf(uint id) => toggle != null && toggle.GlowMembers.TryGetValue(id, out var g) ? g : null;
            // The root switch's chain: its on side starts here, and each group passes on what it starts in its members.
            if (onSwitch != null && toggle != null && switchChain != null)
            { plan = new(); plan.Timelines.Add(toggle.OnTimeline); plan.OffTimelines.Add(toggle.OffTimeline); }
            HashSet<int> played = [], offPlayed = [];
            var cascades = Still(() => SgbTimelines.Cascades(group.Data), []);
            // Controllers a group's own autoplay timelines toggle (the Monster Toss Machine's baskets) belong to scripts
            // Haven does not run; they stay still unless the switch chain starts them.
            var scripted = cascades.Where(c => c.AutoPlay).SelectMany(c => c.Toggles).ToHashSet();
            if (plan != null && switchChain != null)
            {
                var pending = new Queue<int>(plan.Timelines.Concat(plan.Broadcast ? cascades.Where(c => c.Type == "on").Select(c => c.Id) : []));
                while (pending.TryDequeue(out var id))
                {
                    if (!played.Add(id) || cascades.FirstOrDefault(c => c.Id == id) is not {} cascade) continue;
                    // The root's own on side is checked by the switch itself (SgbSwitch.Playable).
                    if (cascade.Unsupported.Count > 0 && !(onSwitch != null && id == toggle?.OnTimeline)) switchChain.Unsupported = true;
                    foreach (var member in cascade.Toggles)
                        if (member == uint.MaxValue) plan.Controllers = true; else plan.Member(member).Controllers = true;
                    foreach (var (timeline, member) in cascade.Plays)
                        if (member == uint.MaxValue) pending.Enqueue(timeline); else plan.Member(member).Timelines.Add(timeline);
                    if (cascade.Broadcast)
                        foreach (var nestedGroup in objects.Where(o => o.Object is LayerCommon.SharedGroupInstanceObject)) plan.Member(nestedGroup.InstanceId).Broadcast = true;
                }
                // The off side only poses one-shot motion, so only its timeline plays are followed.
                pending = new Queue<int>(plan.OffTimelines.Concat(plan.OffBroadcast ? cascades.Where(c => c.Type is "off" or "of").Select(c => c.Id) : []));
                while (pending.TryDequeue(out var id))
                {
                    if (!offPlayed.Add(id) || cascades.FirstOrDefault(c => c.Id == id) is not {} cascade) continue;
                    foreach (var (timeline, member) in cascade.Plays)
                        if (member == uint.MaxValue) pending.Enqueue(timeline); else plan.Member(member).OffTimelines.Add(timeline);
                    if (cascade.Broadcast)
                        foreach (var nestedGroup in objects.Where(o => o.Object is LayerCommon.SharedGroupInstanceObject)) plan.Member(nestedGroup.InstanceId).OffBroadcast = true;
                }
            }
            // One-shot motion in nested timelines the chain plays: the root's own pair is SgbSwitch.Motions.
            var chainMotions = plan == null || onSwitch != null ? new Dictionary<uint, SwitchMotion>()
                : SgbTimelines.ChainMotions(group.Data, played, offPlayed);
            // Autoplay looping timelines (Akabeko, Marimo Lamp, pond fish...). Each part keeps the chain of levels
            // above it so an animated nested group carries its whole subtree, including animated children. This is
            // independent of passive effects, which a timeline-targeted subtree switches off. Looping timelines the
            // switch chain plays (an airship's propellers) run only while the item is on.
            var loops = building ? new Dictionary<uint, LoopCurve>() : SgbTimelines.Loops(group.Data);
            var switchedLoops = building || played.Count == 0 ? new Dictionary<uint, LoopCurve>() : SgbTimelines.Loops(group.Data, played);
            IReadOnlyList<LoopSound> lapSounds = loopSounds == null ? [] : SgbTimelines.LoopSounds(group.Data);
            IReadOnlyList<LoopEffect> lapEffects = loopEffects == null ? [] : SgbTimelines.LoopEffects(group.Data);
            // Loop-driven glow, read only at the root like its loop sounds and effects.
            var lapGlows = loopEffects == null ? new Dictionary<uint, LoopGlow>() : SgbTimelines.LoopGlows(group.Data);
            IReadOnlyDictionary<int, ActionMotion>? ActionsOf(LayerCommon.InstanceObject member)
            {
                if (actions == null) return null;
                var t = member.Transform;
                Vector3 position = new(t.Translation.X, t.Translation.Y, t.Translation.Z), angles = new(t.Rotation.X, t.Rotation.Y, t.Rotation.Z), size = new(t.Scale.X, t.Scale.Y, t.Scale.Z);
                var mine = actions.Where(a => a.Value.ContainsKey(member.InstanceId))
                    .ToDictionary(a => a.Key, a => a.Value[member.InstanceId].WithRest(position, angles, size));
                return mine.Count > 0 ? mine : null;
            }
            LoopLevel[] Levels(LayerCommon.InstanceObject member)
            {
                var t = member.Transform;
                Vector3 position = new(t.Translation.X, t.Translation.Y, t.Translation.Z), angles = new(t.Rotation.X, t.Rotation.Y, t.Rotation.Z),
                    size = new(t.Scale.X, t.Scale.Y, t.Scale.Z);
                ILoopMotion? motion = loops.GetValueOrDefault(member.InstanceId)?.WithRest(position, angles, size);
                var whenOn = false;
                if (motion == null && switchedLoops.GetValueOrDefault(member.InstanceId) is {} switched) { motion = switched.WithRest(position, angles, size); whenOn = true; }
                if (motion == null && !building && member.Object is LayerCommon.SharedGroupInstanceObject travelling) motion = Travel(travelling, member.InstanceId);
                if (motion == null) (motion, whenOn) = Controllers(member);
                if (whenOn && switchChain != null) switchChain.Moves = true;
                // The root switch's lids, doors and drawers: a pose from the item's side, like a loop, but played once.
                var moved = (onSwitch != null ? toggle?.Motions.GetValueOrDefault(member.InstanceId) : chainMotions.GetValueOrDefault(member.InstanceId))
                    ?.WithRest(position, angles, size);
                if (moved != null) onRest?.Invoke(moved.RestingSide(ToMatrix(t)));
                if (moved != null && onSwitch == null && switchChain != null) switchChain.Moves = true;
                return [.. ancestors ?? [], new(ToMatrix(t), motion, moved, whenOn)];
            }
            // The client's repeating controllers on a member, a part or a whole nested group (a mobile's arm): spins
            // run while the group's rotation state is Rounding (the root's always is), repeating transforms while its
            // transform state plays. Controllers the switch chain toggles (C138) run only while the item is on.
            (ILoopMotion?, bool) Controllers(LayerCommon.InstanceObject member)
            {
                if (member.InstanceId is 0 or > byte.MaxValue) return (null, false);
                var id = (byte)member.InstanceId;
                var toggled = plan != null && (plan.Controllers || plan.Members.GetValueOrDefault(member.InstanceId)?.Controllers == true);
                if (!toggled && (scripted.Contains(uint.MaxValue) || scripted.Contains(member.InstanceId))) return (null, false);
                var turning = spins.Where(s => s.Member == id && (rotating || toggled)).ToArray();
                var swinging = oscillations.Actions.Where(a => a.Members.Contains(id) && (transformsPlaying || toggled)).ToArray();
                if (turning.Length == 0 && swinging.Length == 0) return (null, false);
                return (new ControllerMotion(ToMatrix(member.Transform), turning, swinging, depth * 256 + id), toggled);
            }
            // A nested group bound to an autoplay, looping ClientPath in this prefab swims along it.
            PathMotion? Travel(LayerCommon.SharedGroupInstanceObject group, uint id)
            {
                var settings = group.MovePathSettings;
                if (group.BoundCLientPathInstanceId == 0 || settings.AutoPlay == 0 || settings.Loop == 0 || (int)settings.Mode != 1) return null;
                var bound = objects.FirstOrDefault(o => o.InstanceId == group.BoundCLientPathInstanceId);
                if (bound.Object is not LayerCommon.ClientPathInstanceObject path) return null;
                var points = PathPoints(path);
                int frames = settings.Time;
                // An aquarium's fish spots all wait on one zero-length path in the middle of the tank (so read as
                // authored they sit stacked); the game sends each fish round its own ring path, of which the tank has
                // one per spot, unbound. Spot k takes the k-th, at a steady swimming speed. A bowl with no ring path
                // (the one-fish tanks) keeps its fish resting at its spot.
                if (Length(points) < 0.05f)
                {
                    var spots = objects.Where(o => o.Object is LayerCommon.SharedGroupInstanceObject g && g.BoundCLientPathInstanceId == group.BoundCLientPathInstanceId).ToList();
                    var rings = objects.Where(o => o.Object is LayerCommon.ClientPathInstanceObject r && r.Ring != 0
                        && !objects.Any(b => b.Object is LayerCommon.SharedGroupInstanceObject g && g.BoundCLientPathInstanceId == o.InstanceId)).ToArray();
                    var spot = spots.FindIndex(o => o.InstanceId == id);
                    if (rings.Length == 0 || spot < 0) return null;
                    bound = rings[spot % rings.Length];
                    path = (LayerCommon.ClientPathInstanceObject)bound.Object;
                    points = PathPoints(path);
                    frames = (int)Math.Clamp(MathF.Round(Length(points) / FishSpeed * 30), 30, 30 * 3600);
                }
                try { return new PathMotion(points, path.Ring != 0, frames, (int)settings.Rotation == 2, ToMatrix(bound.Transform)); }
                catch (InvalidDataException) { return null; }
            }
            var range = objects.FirstOrDefault(o => (int)o.AssetType == 58);
            var action = metadata.Actions.FirstOrDefault();
            var door = -1;
            if (action != null && (int)range.AssetType == 58 && metadata.Shapes.TryGetValue(range.InstanceId, out var shape) && shape is >= 1 and <= 3)
            {
                door = doors.Count; doors.Add(new(action, new(shape, ToMatrix(range.Transform) * parent)));
            }
            foreach (var layerGroup in group.LayerGroups)
            foreach (var layer in layerGroup.Layers)
            foreach (var item in layer.InstanceObjects)
            {
                var transform = ToMatrix(item.Transform) * parent;
                switch (item.Object)
                {
                    // A part the default timeline hides is left out, unless a one-shot timeline shows it (a ball in flight).
                    case LayerCommon.BGInstanceObject bg when !string.IsNullOrEmpty(bg.AssetPath)
                        && (!defaults.Hidden.Contains(item.InstanceId) || actionShows.Values.Any(a => a.ContainsKey(item.InstanceId))):
                        if (!Allowed(bg.AssetPath, building)) continue;
                        if (building && bg.AssetPath.Contains("/common/lightshaft/", StringComparison.Ordinal)) continue;
                        if (models.Count >= (readingWorld ? WorldModelLimit : 128)) throw new InvalidDataException("Furniture prefab has too many model parts.");
                        var partSwitch = Activity(item.InstanceId);
                        if (partSwitch is {} shown && !shown.Active(true, true) && !shown.Active(false, true)) continue;
                        var mdl = readModel(bg.AssetPath);
                        if (mdl == null) continue;
                        var bb = mdl.ModelBoundingBoxes;
                        var min = new Vector3(bb.Min[0], bb.Min[1], bb.Min[2]);
                        var max = new Vector3(bb.Max[0], bb.Max[1], bb.Max[2]);
                        var panel = door < 0 || item.InstanceId is 0 or > byte.MaxValue ? -1 : Array.IndexOf(action!.Panels, (byte)item.InstanceId);
                        var clock = clocks.FirstOrDefault(c => c.Hour == item.InstanceId || c.Minute == item.InstanceId);
                        // Door leaves and clock hands keep their own motion; everything else that moves (loops, paths,
                        // spins, swings, switch poses, here or in any group above) follows its chain of levels.
                        var levels = Levels(item);
                        var loop = panel < 0 && clock == default && levels.Any(l => l.Motion != null || l.Switch != null) ? levels : null;
                        models.Add(new(bg.AssetPath, transform, min, max, Door: panel < 0 ? null : new(door, panel, ToMatrix(item.Transform), parent),
                            Clock: clock == default ? null : new(clock.Hour == item.InstanceId, ToMatrix(item.Transform), parent),
                            Switch: partSwitch is {} varying && varying.Varies(true) ? varying : null,
                            Loop: loop)
                        {
                            Glow = GlowOf(item.InstanceId), LoopGlow = lapGlows.GetValueOrDefault(item.InstanceId), ChangeSlot = ChangeSlot(mdl), Actions = ActionsOf(item),
                            ActionGlows = PerAction(actionGlows, item.InstanceId), ActionShows = PerAction(actionShows, item.InstanceId),
                            ActionColours = PerAction(actionColours, item.InstanceId),
                            HiddenAtRest = defaults.Hidden.Contains(item.InstanceId),
                            ActionLoopGlows = startedGlows.Where(g => g.Member == item.InstanceId).Select(g => g.Loop).ToArray() is { Length: > 0 } blinks ? blinks : null,
                        });
                        // A part seen only during a one-shot timeline is never solid.
                        if (defaults.Hidden.Contains(item.InstanceId)) break;
                        // Parts that change with the switch are not solid: collision is built once per item.
                        if (partSwitch is {} changing && changing.Varies(true)) break;
                        // A container's door or lid authored without collision (the Gaol Partition Door's bars) blocks
                        // where it rests, while the item is on that side; open, it stays passable as vanilla has it.
                        if (closedColliders && bg.CollisionType == ModelCollisionType.None && levels[^1].Switch is {} swing
                            && swing.RestingSide(ToMatrix(item.Transform)) is bool closed)
                        { colliders.Add(new(null, transform, min, max) { WhenOn = closed }); break; }
                        if (bg.CollisionType == ModelCollisionType.Box) colliders.Add(new(null, transform, min, max, panel < 0 ? -1 : door));
                        else if (bg.CollisionType == ModelCollisionType.Replace && !string.IsNullOrEmpty(bg.CollisionAssetPath)
                            && Allowed(bg.CollisionAssetPath, building) && exists(bg.CollisionAssetPath))
                            colliders.Add(new(bg.CollisionAssetPath, transform, min, max, panel < 0 ? -1 : door));
                        break;
                    case LayerCommon.SharedGroupInstanceObject nested when !string.IsNullOrEmpty(nested.AssetPath) && !defaults.Hidden.Contains(item.InstanceId):
                        var asset = nested.AssetPath;
                        if (swap != null)
                        {
                            var occurrence = swapSeen.GetValueOrDefault(asset);
                            swapSeen[asset] = occurrence + 1;
                            if (swap(asset, occurrence) is { } filledPath && exists(filledPath)) asset = filledPath;
                        }
                        if (Allowed(asset, building))
                            Read(asset, transform, depth + 1, chain, models, colliders, seats, lights, doors, building,
                                nested.InitialRotationState == RotationState.Rounding,
                                nested.InitialTransformState is TransformState.TransformStatePlay or TransformState.TransformStateReplay,
                                timed?.Contains(item.InstanceId) == false ? effects : null, timed?.Contains(item.InstanceId) == false ? sounds : null,
                                ancestors: Levels(item), onRest: onRest, closedColliders: closedColliders, switchChain: switchChain,
                                plan: plan?.Members.GetValueOrDefault(item.InstanceId),
                                actionLoops: actionPlays.SelectMany(a => a.Value.Where(p => p.Member == item.InstanceId).Select(p => (a.Key, p.Timeline, p.Start, p.Stop))).ToArray() is { Length: > 0 } started ? started : null);
                        break;
                    case LayerCommon.CollisionBoxInstanceObject when door >= 0
                        && metadata.Shapes.GetValueOrDefault(item.InstanceId) == 1:
                        // SGB collision boxes use their transform's scale as half extents.
                        // Only door blockers are added here; unrelated trigger volumes stay non-solid.
                        colliders.Add(new(null, transform, new(-1), new(1), door));
                        break;
                    case LayerCommon.ChairMarkerInstanceObject chair:
                        seats.Add(new(transform, chair.ObjectType == ObjectType.ObjectBed));
                        break;
                    case LayerCommon.VFXInstanceObject vfx when effects != null && Activity(item.InstanceId) is {} effectSwitch
                        && (effectSwitch.Active(true, vfx.IsAutoPlay != 0) || effectSwitch.Active(false, vfx.IsAutoPlay != 0))
                        && Ambient(vfx.AssetPath, "bgcommon/hou/", ".avfx"):
                        if (effects.Count >= 16) throw new InvalidDataException("Furniture prefab has too many effects.");
                        var authored = vfx.IsAutoPlay != 0;
                        effects.Add(new(vfx.AssetPath, transform, new Vector4(vfx.Color.Red, vfx.Color.Green, vfx.Color.Blue, vfx.Color.Alpha) / 255f,
                            effectSwitch.Varies(authored) ? new(effectSwitch.Active(true, authored), effectSwitch.Active(false, authored)) : null));
                        break;
                    // The root prefab's loop-driven effects, read only at the root like its loop sounds.
                    case LayerCommon.VFXInstanceObject lapped when loopEffects != null && Activity(item.InstanceId) == null
                        && lapEffects.Any(e => e.Member == item.InstanceId) && Ambient(lapped.AssetPath, "bgcommon/hou/", ".avfx"):
                        foreach (var e in lapEffects.Where(e => e.Member == item.InstanceId))
                            loopEffects.Add(new(lapped.AssetPath, transform, new Vector4(lapped.Color.Red, lapped.Color.Green, lapped.Color.Blue, lapped.Color.Alpha) / 255f, e, lapped.IsAutoPlay != 0));
                        break;
                    case LayerCommon.VFXInstanceObject vfx when effects != null && Activity(item.InstanceId) == null
                        && (vfx.IsAutoPlay != 0 && !timed!.Contains(item.InstanceId) || defaults.Started.Contains(item.InstanceId))
                        && Ambient(vfx.AssetPath, "bgcommon/hou/", ".avfx"):
                        if (effects.Count >= 16) throw new InvalidDataException("Furniture prefab has too many effects.");
                        effects.Add(new(vfx.AssetPath, transform, new Vector4(vfx.Color.Red, vfx.Color.Green, vfx.Color.Blue, vfx.Color.Alpha) / 255f));
                        break;
                    // Only the root prefab's switch is clickable, so only its sound members are switch sounds.
                    case LayerCommon.SoundInstanceObject cue when switchSounds != null && toggle != null
                        && toggle.Sounds.Any(s => s.Member == item.InstanceId) && Ambient(cue.AssetPath, "bgcommon/sound/", ".scd"):
                        foreach (var s in toggle.Sounds.Where(s => s.Member == item.InstanceId))
                            switchSounds.Add(new(cue.AssetPath, transform, s));
                        break;
                    // The root's one-shot timelines' effects (an arcade machine's punch flash or sparkles), shown with them;
                    // the arcade machines keep theirs with the Gold Saucer's own (bg/ffxiv/wil_w1/common/vfx/).
                    case LayerCommon.VFXInstanceObject resultFx when actionEffects.Values.Any(list => list.Any(e => e.Member == item.InstanceId))
                        && (Ambient(resultFx.AssetPath, "bgcommon/hou/", ".avfx") || Ambient(resultFx.AssetPath, "bg/ffxiv/wil_w1/common/vfx/", ".avfx")):
                        foreach (var (subId, list) in actionEffects)
                            foreach (var e in list.Where(e => e.Member == item.InstanceId))
                                actionEffectList.Add(new(subId, resultFx.AssetPath, transform, new Vector4(resultFx.Color.Red, resultFx.Color.Green, resultFx.Color.Blue, resultFx.Color.Alpha) / 255f, e.Start, e.Stop));
                        break;
                    // The root's one-shot timelines' sounds (an arcade machine's punch, bell or fanfare), played with them.
                    case LayerCommon.SoundInstanceObject result when actionSounds.Values.Any(list => list.Any(s => s.Member == item.InstanceId))
                        && Ambient(result.AssetPath, "bgcommon/sound/", ".scd"):
                        foreach (var (subId, list) in actionSounds)
                            foreach (var s in list.Where(s => s.Member == item.InstanceId)) actionSoundList.Add((subId, new(result.AssetPath, transform, s)));
                        break;
                    // The root prefab's looping animations, like its switch, are read only at the root.
                    case LayerCommon.SoundInstanceObject lap when loopSounds != null && lapSounds.Any(s => s.Timing.Member == item.InstanceId)
                        && Ambient(lap.AssetPath, "bgcommon/sound/", ".scd"):
                        foreach (var s in lapSounds.Where(s => s.Timing.Member == item.InstanceId)) loopSounds.Add(new(lap.AssetPath, transform, s));
                        break;
                    case LayerCommon.SoundInstanceObject sound when sounds != null && sound.SEParam.AutoPlay != 0 && !timed!.Contains(item.InstanceId)
                        && Ambient(sound.AssetPath, "bgcommon/sound/", ".scd"):
                        if (sounds.Count >= 8) throw new InvalidDataException("Furniture prefab has too many sounds.");
                        sounds.Add(new(sound.AssetPath, transform));
                        break;
                    case LayerCommon.LightInstanceObject light:
                        var color = light.DiffuseColorHDRI;
                        var lightSwitch = Activity(item.InstanceId);
                        float Finite(float value, float minimum, float maximum) => float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;
                        var layoutLight = new LayoutLight((int)light.LightType, Finite(light.Attenuation, 0, 100), Finite(light.RangeRate, 0, 100),
                            new Vector3(color.Red, color.Green, color.Blue) / 255f, Finite(color.Intensity, 0, 100), Finite(light.ConeDegree, 0, 180),
                            Finite(light.AttenuationConeCoefficient, 0, 180), string.IsNullOrEmpty(light.TexturePath) || !exists(light.TexturePath) ? null : light.TexturePath,
                            light.SpecularEnabled != 0, light.BGShadowEnabled != 0, light.CharacterShadowEnabled != 0, Finite(light.ShadowClipRange, 0, 100),
                            new Vector2(Finite(light.PlaneLightRotationX, -360, 360), Finite(light.PlaneLightRotationY, -360, 360)));
                        lights.Add(new(transform, layoutLight, lightSwitch is {} lit && lit.Varies(true) ? lit : null));
                        break;
                }
            }
        }
        finally { chain.Remove(path); }
    }
    private bool Ambient(string? path, string root, string extension) => !string.IsNullOrEmpty(path)
        && path.StartsWith(root, StringComparison.Ordinal) && path.EndsWith(extension, StringComparison.Ordinal) && exists(path);
    private static bool AllowedPath(string path, bool building) => path.StartsWith("bgcommon/hou/", StringComparison.Ordinal)
        || building && path.StartsWith("bg/ffxiv/", StringComparison.Ordinal) && (path.Contains("/ind/") || path.Contains("/shared/for_hou/"));
    // An inn room's group (GetBuildingGroup with innRoom) draws its city's and the game's common files; a housing group
    // keeps to housing files, so its parts stay as they were.
    private bool readingInn;
    private bool Allowed(string path, bool building) => AllowedPath(path, building)
        || readingInn && building && (path.StartsWith("bg/", StringComparison.Ordinal) || path.StartsWith("bgcommon/", StringComparison.Ordinal))
            && !path.Contains("/lightshaft/", StringComparison.Ordinal);
    private static string? ChangeSlot(MdlFile mdl)
    {
        var index = mdl.ModelHeader.BGChangeMaterialIndex;
        if (index >= mdl.MaterialNameOffsets.Length) return null;
        var start = (int)mdl.MaterialNameOffsets[index]; var end = Array.IndexOf(mdl.Strings, (byte)0, start);
        var path = end < 0 ? null : System.Text.Encoding.ASCII.GetString(mdl.Strings, start, end - start);
        return path != null && (path.Contains("/pic/ta/0000/", StringComparison.Ordinal) || path.Contains("/dyna/mat/wl/0000/", StringComparison.Ordinal)
            || path.Contains("/indoor/gyo/", StringComparison.Ordinal) && path.Contains("/0000/material/", StringComparison.Ordinal)) ? path : null;
    }
    private static Matrix4x4 ToMatrix(Common.Transformation t) => LayoutRotation.Transform(new(t.Translation.X, t.Translation.Y, t.Translation.Z),
        new(t.Rotation.X, t.Rotation.Y, t.Rotation.Z), new(t.Scale.X, t.Scale.Y, t.Scale.Z));
}
