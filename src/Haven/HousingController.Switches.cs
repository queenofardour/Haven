using System.Numerics;
using System.Text.Json;
using Haven.Core;
using Stagehand.Api;

namespace Haven;

// Clickable furnishing switches (lamps, instruments, cupboards, table food). The current side lives here, not in
// the house format: snapshots and collaborative edits are unchanged. The owner's non-food sides are remembered per
// home in a local sidecar (solo or hosting, whoever clicked); food always starts on its default side. In a
// gathering every click is shared through the relay, which replays the room's table to late joiners, and the
// owner seeds the room with the saved sides once its scene is ready (including after recovery).
internal sealed partial class HousingController
{
    private readonly string switchesPath;
    private Dictionary<Guid, Dictionary<Guid, bool>> savedSwitches = [];
    private readonly Dictionary<Guid, bool> switchStates = [];
    private bool persistSwitches;

    public Guid? HighlightedFurnishing { set => visuals.Highlighted = value; }    // When each switch last changed during this visit, for its lids and doors to play their motion from.
    private readonly Dictionary<Guid, long> switchChanged = [];
    public long SwitchAge(Furnishing item) => switchChanged.TryGetValue(item.Id, out var at) ? Environment.TickCount64 - at : long.MaxValue;
    public bool SwitchOn(Furnishing item) => switchStates.TryGetValue(item.Id, out var on) ? on : DefaultSwitch(item);
    private bool DefaultSwitch(Furnishing item) => Catalogue.Prefab(item).Switch?.DefaultOn ?? true;

    public bool CanInteract(Furnishing item) => IsReady && !EndingVisit && item.BuildingPart == null
        && (Catalogue.Get(item).Orchestrion || Catalogue.Get(item).TripleTriad || Catalogue.Get(item).Arcade != null
            || Catalogue.Get(item).Interactive && (Catalogue.Prefab(item).Switch != null || Catalogue.Prefab(item).UseTimeline != null));

    /// <summary>Uses a furnishing used rather than switched (the Ale Tap pours): its one-shot plays here and for the gathering.</summary>
    public void UseFurnishing(Guid id)
    {
        if (active?.Furnishings.FirstOrDefault(f => f.Id == id) is not { } item || Catalogue.Prefab(item).UseTimeline is not { } use) return;
        PlayAction(item, use, share: true);
    }
    // Lamps vanilla gives no click script (the Riviera, Glade and Oasis lanterns and floor lamps) are switched from the
    // inspector instead; like a click, the side is shared with the gathering and remembered.
    public bool InspectorLamp(Furnishing item) => item.BuildingPart == null && Catalogue.Prefab(item).Switch is { Lamp: true }
        && !Catalogue.Get(item).Interactive && !Catalogue.Get(item).Orchestrion;
    private bool Switchable(Furnishing item) => Catalogue.Get(item).Interactive || InspectorLamp(item) || DoorToggle(item);
    // A furnishing with a proximity door and no switch of its own (the Thick Bookshelf Partition's hidden door): its
    // inspector turns the door off, so it stays shut and solid. Off is its switch's off side, so it's shared with the
    // gathering and remembered like a lamp's.
    public bool DoorToggle(Furnishing item) => item.BuildingPart == null && Catalogue.Prefab(item) is { Switch: null, Doors.Length: > 0 };

    public void ToggleSwitch(Guid id)
    {
        if (active?.Furnishings.FirstOrDefault(f => f.Id == id) is not {} item || !(CanInteract(item) || IsReady && !EndingVisit && (InspectorLamp(item) || DoorToggle(item)))
            || Catalogue.Get(item).Orchestrion || Catalogue.Prefab(item).Switch == null && !DoorToggle(item)) return;
        var on = !SwitchOn(item);
        SetSwitch(item, on);
        switchChanged[item.Id] = Environment.TickCount64;
        PlaySwitchCues(item, on);
        if (IsSharedVisit) _ = engine.HousingRelay.SendSwitchStates([new(id, on)]);
    }

    // One-off switch sounds (clicks, a Tonberry's greeting, an instrument's piece). Each plays in its own create-once
    // stage from its start frame. Stagehand restarts a sound that finishes, so a cue is destroyed exactly at its
    // natural end, or at its stop frame plus fade if that comes first; one whose length is unknown is not played.
    // Changing an item's side cuts its running cues, as each installed pair's C057 does. Held loops follow the
    // switch through BuildAmbience instead. Local presentation only: nothing here is saved or sent.
    // Looping animations' lap sounds (a Drinking Apkallu's drip, the garden bamboo's clack) are the same kind of cue,
    // fired when the shared animation clock crosses their frame, so they keep time with the motion on every client.
    private sealed class SwitchCue(Guid item, string path, string stage, long startAt, long stopAt, string definition)
    {
        public Guid Item => item; public string Path => path; public string Stage => stage; public long StartAt => startAt; public long StopAt => stopAt;
        public string Definition => definition; public bool Started { get; set; }
    }
    private readonly List<SwitchCue> cues = [];
    private long cueSerial;
    // Relay changes that arrive while a scene settles are the room's replayed table, not someone's click.
    private long switchCuesAfter;
    private const int MaxCues = 12;
    // Like ambient loops, one sound plays from a few places at most: a row of Apkallu drips once, not six times.
    private const int MaxCuesPerSound = 3;
    private long? lapClock;

    private void PlaySwitchCues(Furnishing item, bool on)
    {
        StopSwitchCues(item.Id);
        if (active != null && rendered && engine.HousingAmbientSounds && (item.SoundVolume ?? 1) > 0)
        {
            var house = stagedScene != null ? active with { Transform = stagedScene.Transform } : active;
            var world = FurnitureCatalogue.ItemMatrix(item) * FurnitureCatalogue.HouseMatrix(house);
            var now = Environment.TickCount64;
            foreach (var sound in Catalogue.Prefab(item).SwitchSounds.Where(s => s.On == on && !Catalogue.Held(s)))
            {
                if (sound.PlayMilliseconds(Catalogue.SoundMilliseconds(sound.Path)) is not long length || length <= 0) continue;
                var start = now + sound.StartFrame * 1000L / SwitchSound.FramesPerSecond;
                AddCue(item, sound.Path, (sound.Transform * world).Translation, start, length);
            }
        }
        // An instrument with nothing to play here (sounds off, or its volume at Off) stays silent.
        if (Catalogue.Prefab(item).Switch is { OnlySound: true } && !cues.Any(c => c.Item == item.Id)) switchStates.Remove(item.Id);
    }

    // A one-shot timeline's sounds (an arcade machine's punch or fanfare), as cues from their frames.
    private void PlayActionSounds(Furnishing item, int subId)
    {
        if (active == null || !rendered || !engine.HousingAmbientSounds || (item.SoundVolume ?? 1) <= 0) return;
        var house = stagedScene != null ? active with { Transform = stagedScene.Transform } : active;
        var world = FurnitureCatalogue.ItemMatrix(item) * FurnitureCatalogue.HouseMatrix(house);
        var now = Environment.TickCount64;
        foreach (var (timeline, sound) in Catalogue.Prefab(item).ActionSounds)
        {
            if (timeline != subId || sound.PlayMilliseconds(Catalogue.SoundMilliseconds(sound.Path)) is not long length || length <= 0) continue;
            AddCue(item, sound.Path, (sound.Transform * world).Translation, now + sound.StartFrame * 1000L / SwitchSound.FramesPerSecond, length);
        }
    }

    private void AddCue(Furnishing item, string path, Vector3 position, long start, long length)
    {
        cues.Add(new(item.Id, path, $"{AmbientStage}.cue{++cueSerial}", start, start + length, FurnitureCatalogue.CueStage(path, position, item.SoundVolume ?? 1)));
        while (cues.Count > MaxCues) { Destroy(cues[0]); cues.RemoveAt(0); }
    }

    // Lap sounds fire as the animation clock passes their frame. A jump in the clock (a resync, a long hitch) skips
    // the missed laps rather than playing a backlog.
    private void UpdateLapCues(long clock)
    {
        var previous = lapClock; lapClock = clock;
        if (previous is not long from || clock - from is <= 0 or > 1000 || active == null || !engine.HousingAmbientSounds) return;
        var house = stagedScene != null ? active with { Transform = stagedScene.Transform } : active;
        var parent = FurnitureCatalogue.HouseMatrix(house);
        var now = Environment.TickCount64;
        foreach (var item in active.Furnishings)
        {
            if (item.BuildingPart != null || item.Id == MovingId || (item.SoundVolume ?? 1) <= 0) continue;
            var laps = Catalogue.Prefab(item).LoopSounds;
            for (var i = 0; i < laps.Length; i++)
            {
                var lap = laps[i];
                if (!lap.Loop.Crossed(from, clock) || cues.Count(c => c.Path == lap.Path) >= MaxCuesPerSound) continue;
                if (lap.Loop.Timing.PlayMilliseconds(Catalogue.SoundMilliseconds(lap.Path)) is not long length || length <= 0) continue;
                AddCue(item, lap.Path, (lap.Transform * FurnitureCatalogue.ItemMatrix(item) * parent).Translation, now, length);
            }
        }
    }

    private void UpdateSwitchCues()
    {
        var now = Environment.TickCount64;
        for (var i = cues.Count - 1; i >= 0; i--)
        {
            var cue = cues[i];
            if (now >= cue.StopAt)
            {
                Destroy(cue); cues.RemoveAt(i);
                // An instrument falls silent on its own once its piece ends; every client times this alike.
                if (!cues.Any(c => c.Item == cue.Item) && active?.Furnishings.FirstOrDefault(f => f.Id == cue.Item) is {} item
                    && Catalogue.Prefab(item).Switch is { OnlySound: true } && SwitchOn(item) != DefaultSwitch(item)) switchStates.Remove(item.Id);
                continue;
            }
            if (cue.Started || now < cue.StartAt) continue;
            cue.Started = true;
            if (!stagehand.TryCreateOrUpdateTemporaryStageWithTransform(cue.Definition, cue.Stage, Vector3.Zero, Quaternion.Identity, 1, "Furnishing sound"))
                log.Warning("Stagehand could not play a furnishing sound cue.");
        }
    }

    private void StopSwitchCues(Guid? item = null)
    {
        foreach (var cue in cues.Where(c => item == null || c.Item == item).ToArray()) { Destroy(cue); cues.Remove(cue); }
        if (item == null) lapClock = null;
    }
    private void Destroy(SwitchCue cue)
    {
        if (cue.Started && StagehandStatus == StagehandApiAvailability.Available) stagehand.TryDestroyTemporaryStage(cue.Stage);
    }

    private void SetSwitch(Furnishing item, bool on, bool save = true)
    {
        switchStates[item.Id] = on;
        needsRender = true;
        if (!persistSwitches || active == null || Catalogue.Get(item).Food || Catalogue.Prefab(item).Switch?.OnlySound == true) return;
        if (!savedSwitches.TryGetValue(active.Id, out var saved)) savedSwitches[active.Id] = saved = [];
        // Only sides that differ from the item's default are kept.
        if (on == DefaultSwitch(item)) saved.Remove(item.Id); else saved[item.Id] = on;
        if (saved.Count == 0) savedSwitches.Remove(active.Id);
        if (save) SaveSwitches();
    }

    private void OnSwitchStates(SwitchState[] states) => Run(() =>
    {
        if (active == null || !IsSharedVisit) return;
        var audible = Environment.TickCount64 >= switchCuesAfter;
        // One lookup and at most one save per frame: a frame can list every switch in the home (the owner's table on
        // scene ready, or a guest flipping them all), and a save per entry stalled the owner's game.
        Dictionary<Guid, Furnishing>? byId = null;
        if (states.Length > 4) { byId = []; foreach (var f in active.Furnishings) byId.TryAdd(f.Id, f); }
        var changed = false;
        foreach (var state in states)
            if ((byId != null ? byId.GetValueOrDefault(state.Furnishing) : active.Furnishings.FirstOrDefault(f => f.Id == state.Furnishing)) is {} item
                && item.BuildingPart == null
                && Switchable(item) && (Catalogue.Prefab(item).Switch != null || DoorToggle(item)) && SwitchOn(item) != state.On)
            {
                SetSwitch(item, state.On, save: false);
                changed = true;
                // A replayed table snaps to each side's final pose; a live click animates.
                if (audible) { switchChanged[item.Id] = Environment.TickCount64; PlaySwitchCues(item, state.On); }
                else switchChanged.Remove(item.Id);
            }
        if (changed && persistSwitches) SaveSwitches();
    });

    // The owner's remembered sides become the room's table; the relay keeps it for anyone who joins later.
    private void OnSwitchSceneReady() => Run(() =>
    {
        switchCuesAfter = Environment.TickCount64 + 3000;
        if (!persistSwitches || !IsSharedVisit || !OwnsActiveHouse || switchStates.Count == 0) return;
        _ = engine.HousingRelay.SendSwitchStates(switchStates.Select(s => new SwitchState(s.Key, s.Value)).ToArray());
    });

    private void BeginSwitches(House house, bool persist)
    {
        switchStates.Clear();
        switchChanged.Clear();
        StopSwitchCues();
        switchCuesAfter = Environment.TickCount64 + 3000;
        persistSwitches = persist;
        if (!persist || !savedSwitches.TryGetValue(house.Id, out var saved)) return;
        foreach (var (id, on) in saved)
            if (house.Furnishings.FirstOrDefault(f => f.Id == id) is {} item && item.BuildingPart == null && !Catalogue.Get(item).Food
                && Catalogue.Prefab(item).Switch?.OnlySound != true)
                switchStates[id] = on;
    }

    private void LoadSwitches()
    {
        if (!File.Exists(switchesPath)) return;
        try { savedSwitches = JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<Guid, bool>>>(File.ReadAllText(switchesPath)) ?? []; }
        catch (Exception ex) { log.Warning(ex, "Saved furnishing switch states could not be loaded."); }
    }

    private void SaveSwitches()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(switchesPath)!);
            var temporary = switchesPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(savedSwitches));
            File.Move(temporary, switchesPath, true);
        }
        catch (Exception ex) { log.Warning(ex, "Furnishing switch states could not be saved."); }
    }
}
