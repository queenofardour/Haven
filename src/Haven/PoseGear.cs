using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Haven.Core.Animation;

namespace Haven;

/// <summary>A prop to wear now: its package and index there, the prop, a name for it, the item that shows it, and the file of each package file name on this PC.</summary>
internal sealed record PropWanted(string Package, int Index, PoseProp Prop, string Name, ulong Item, Func<string, string?> File)
{
    public string Key => Package + ":" + Index;
}

/// <summary>
/// What Haven put on a character, and what to put back: the slot's item and dyes before, in the prop's mod folder.
/// Revert: the character had no Glamourer state to read (an NPC in its plain look), so taking it off reverts it.
/// </summary>
internal sealed record PropWorn(string Key, string Mod, string Slot, ulong Prop, ulong Item, byte[] Stains, bool Same, bool Revert = false);

/// <summary>The player's choices per package (wear its props or not), the prop mods Haven installed, and what it is wearing.</summary>
internal sealed class PoseGearRecords
{
    public Dictionary<string, bool> Choices { get; set; } = [];
    public Dictionary<string, string> Installed { get; set; } = [];
    public PropWorn? Worn { get; set; }
}

/// <summary>
/// Props worn as gear while an animation plays. A prop's files are installed once as a Penumbra
/// mod of its own in the "Haven Animation Props" folder, so the player can see it and sync plugins share it like any
/// mod. While the player's animation plays, the mod is switched on for them through Penumbra's temporary settings (never
/// saved to their collection: a crash or restart leaves it off) and Glamourer puts the prop's item in its slot, keeping
/// what was there; when the animation ends both are put back. A record of what was worn is kept on disk, so a reload
/// puts it back too. NPCs on balls wear their animation's props the same way, each PC dressing its own copy (by object
/// index, kept in memory only: NPC objects end with the visit).
/// </summary>
internal sealed class PoseGear : IDisposable
{
    private const string SortFolder = "Haven Animation Props";
    private const string Source = "Haven";
    // The lock on Haven's temporary settings, so nothing else changes them meanwhile.
    private const int Key = 0x48415645;
    private const string Marker = "haven-prop.json";
    private readonly IDalamudPluginInterface plugin;
    private readonly IPluginLog log;
    private readonly string recordsPath;
    private PoseGearRecords records = new();
    private readonly Dictionary<string, bool> sessionChoices = [];
    private readonly HashSet<string> failed = [];
    private readonly ConcurrentDictionary<string, Task<string>> installing = new();
    private readonly global::Glamourer.Api.IpcSubscribers.GetState getState;
    private readonly global::Glamourer.Api.IpcSubscribers.SetItem setItem;
    private readonly global::Glamourer.Api.IpcSubscribers.RevertState revertState;
    private bool recover;
    // What NPCs wear, by object index.
    private readonly Dictionary<int, PropWorn> npcWorn = [];
    /// <summary>Set by the plugin: refreshes Penumbra's identity of an NPC object before Glamourer is asked about it (NpcGlamour.Reidentify).</summary>
    public Action<int>? Prepare { get; set; }
    public readonly ConcurrentQueue<string> Failures = new();

    public PoseGear(IDalamudPluginInterface plugin, IPluginLog log)
    {
        this.plugin = plugin; this.log = log;
        recordsPath = Path.Combine(plugin.ConfigDirectory.FullName, "content", "animations", "props.json");
        try { if (File.Exists(recordsPath)) records = JsonSerializer.Deserialize<PoseGearRecords>(File.ReadAllText(recordsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { log.Warning(ex, "[Haven] The animation prop list could not be read."); }
        getState = new(plugin);
        setItem = new(plugin);
        revertState = new(plugin);
        // Something was left on (a crash, or Haven unloaded mid-animation): put it back once the player is there.
        recover = records.Worn != null;
    }

    /// <summary>The player's choice for a package's props: remembered, for this game session, or null to ask.</summary>
    public bool? Choice(string package) => records.Choices.TryGetValue(package, out var wear) ? wear : sessionChoices.TryGetValue(package, out var now) ? now : null;

    public void Choose(string package, bool wear, bool remember)
    {
        sessionChoices[package] = wear;
        if (remember) { records.Choices[package] = wear; Save(); }
        if (wear) failed.RemoveWhere(k => k.StartsWith(package + ":", StringComparison.Ordinal));
    }

    /// <summary>Forgets every remembered choice, so each prop asks again.</summary>
    public void ForgetChoices() { records.Choices.Clear(); sessionChoices.Clear(); Save(); }

    /// <summary>The prop mods Haven installed in Penumbra (folder → name).</summary>
    public IReadOnlyDictionary<string, string> Installed => records.Installed;
    public bool Wearing => records.Worn != null;
    /// <summary>How many packages have a remembered choice.</summary>
    public int Remembered => records.Choices.Count;

    /// <summary>
    /// Each frame: wears the prop wanted (or none). A change takes off what's worn first. Installing runs off the
    /// framework thread; Penumbra and Glamourer are called here.
    /// </summary>
    public void Update(PropWanted? want, bool playerReady)
    {
        if (!playerReady) return;
        if (recover) { recover = false; if (records.Worn is { } left) { log.Information("[PROP] putting back what an earlier session left on."); TakeOff(left, 0); } }
        if (records.Worn is { } worn && (want == null || worn.Key != want.Key)) TakeOff(worn, 0);
        if (want == null || records.Worn != null || failed.Contains(want.Key) || Ready(want) is not { } mod) return;
        try { records.Worn = Wear(want, mod, 0); Save(); }
        catch (Exception ex) { Fail(want, ex.Message.TrimEnd('.'), ex); }
    }

    /// <summary>
    /// Each frame: the props NPCs on playing balls wear on this PC (by object index); others are taken off. Nothing is
    /// kept on disk: the NPC objects end with the visit, and Penumbra's temporary settings with the game.
    /// </summary>
    public void UpdateNpcs(IReadOnlyDictionary<int, PropWanted> wants)
    {
        foreach (var (index, worn) in npcWorn.ToArray())
            if (!wants.TryGetValue(index, out var want) || want.Key != worn.Key) { npcWorn.Remove(index); TakeOff(worn, index); }
        foreach (var (index, want) in wants)
        {
            if (npcWorn.ContainsKey(index) || failed.Contains(want.Key) || Ready(want) is not { } mod) continue;
            try { npcWorn[index] = Wear(want, mod, index); }
            catch (Exception ex) { Fail(want, ex.Message.TrimEnd('.'), ex); }
        }
    }

    // The prop's mod folder once its files are installed (started off the framework thread the first time), else null.
    private string? Ready(PropWanted want)
    {
        var mod = ModFolder(want);
        if (!installing.TryGetValue(want.Key, out var task))
        {
            string root;
            try { root = plugin.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc(); }
            catch (Exception ex) { Fail(want, "Penumbra isn't available", ex); return null; }
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Fail(want, "Penumbra's mod folder isn't set", null); return null; }
            installing[want.Key] = Task.Run(() => Install(root, mod, want));
            return null;
        }
        if (!task.IsCompleted) return null;
        if (task.IsFaulted) { installing.TryRemove(want.Key, out _); Fail(want, "its files couldn't be installed", task.Exception?.GetBaseException()); return null; }
        return mod;
    }

    // The prop's folder in Penumbra's mod folder: one per package prop, kept for next time.
    private static string ModFolder(PropWanted want) => $"Haven Prop {want.Package[..12]}-{want.Index}";

    // Writes the prop as a Penumbra mod: meta.json, default_mod.json (its files and manipulations) and the files.
    private string Install(string root, string mod, PropWanted want)
    {
        var folder = Path.Combine(root, mod);
        var marker = Path.Combine(folder, Marker);
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == want.Key) return folder;
        Directory.CreateDirectory(folder);
        var files = new JsonObject();
        foreach (var (path, file) in want.Prop.Files)
        {
            var from = want.File(file) ?? throw new FileNotFoundException("A prop file isn't unpacked yet.");
            var relative = Path.Combine("files", path.Replace('/', Path.DirectorySeparatorChar));
            var to = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
            files[path] = relative;
        }
        var manipulations = new JsonArray();
        foreach (var json in want.Prop.Meta) manipulations.Add(JsonNode.Parse(json));
        var meta = new JsonObject
        {
            ["FileVersion"] = 3, ["Name"] = $"{want.Name}", ["Author"] = "Haven",
            ["Description"] = "A prop for one of your Haven animations, installed by Haven. Haven switches it on for you only while that animation plays; leave it off otherwise. Remove it in Haven's Settings → Custom content → Animations.",
            ["Version"] = "1", ["Website"] = "", ["ModTags"] = new JsonArray("Haven"),
        };
        var data = new JsonObject { ["Name"] = "", ["Priority"] = 0, ["Files"] = files, ["FileSwaps"] = new JsonObject(), ["Manipulations"] = manipulations };
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(folder, "meta.json"), meta.ToJsonString(options));
        File.WriteAllText(Path.Combine(folder, "default_mod.json"), data.ToJsonString(options));
        File.WriteAllText(marker, want.Key);
        return folder;
    }

    // Puts a prop on a character (0: the player; else an NPC's object index) and returns what to put back.
    private PropWorn Wear(PropWanted want, string mod, int index)
    {
        // Known to Penumbra (added once), sorted into Haven's folder.
        var known = plugin.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc();
        if (!known.ContainsKey(mod))
        {
            var added = plugin.GetIpcSubscriber<string, int>("Penumbra.AddMod.V5").InvokeFunc(mod);
            if (added != 0) throw new InvalidOperationException($"Penumbra didn't add its mod (code {added})");
            plugin.GetIpcSubscriber<string, string, string, int>("Penumbra.SetModPath.V5").InvokeFunc(mod, "", $"{SortFolder}/{want.Name.Replace('/', '-')}");
            log.Information("[PROP] installed {Name} in Penumbra as {Mod}.", want.Name, mod);
        }
        if (!records.Installed.ContainsKey(mod)) { records.Installed[mod] = want.Name; Save(); }
        if (!Enum.TryParse<ApiEquipSlot>(want.Prop.Slot, out var slot)) throw new InvalidOperationException("its slot is unknown");
        // What's in the slot now, to put back. An NPC Glamourer has no state for (its plain look) is reverted instead.
        if (index != 0) Prepare?.Invoke(index);
        var (code, state) = getState.Invoke(index);
        var read = code == GlamourerApiEc.Success && state?["Equipment"]?[want.Prop.Slot] is Newtonsoft.Json.Linq.JObject { } found && found["ItemId"] != null ? found : null;
        if (read == null && index == 0) throw new InvalidOperationException("Glamourer couldn't read your gear" + (code != GlamourerApiEc.Success ? $" ({code})" : ""));
        var before = read?["ItemId"]!.ToObject<ulong>() ?? 0;
        byte[] stains = [read?["Stain"]?.ToObject<byte>() ?? 0, read?["Stain2"]?.ToObject<byte>() ?? 0];
        SwitchOn(mod, index);
        var worn = new PropWorn(want.Key, mod, want.Prop.Slot, want.Item, before, stains, read != null && before == want.Item, Revert: read == null);
        if (index == 0) { records.Worn = worn; Save(); }
        var set = setItem.Invoke(index, slot, want.Item, [0, 0]);
        if (set is not (GlamourerApiEc.Success or GlamourerApiEc.NothingDone)) { TakeOff(worn, index); throw new InvalidOperationException($"Glamourer couldn't put it on ({set})"); }
        // Already wearing that item: Glamourer changed nothing, so the model is drawn again with the mod's files.
        if (worn.Same) Redraw(index);
        log.Information("[PROP] {Who} wearing {Name} ({Slot}, item {Item}; was {Before}).", index == 0 ? "you are" : $"object {index} is", want.Name, want.Prop.Slot, want.Item, before);
        return worn;
    }

    // The mod on for the collection a character draws from, through Penumbra's temporary settings (never saved).
    private void SwitchOn(string mod, int index)
    {
        var settings = new Dictionary<string, IReadOnlyList<string>>();
        var on = plugin.GetIpcSubscriber<int, string, string, (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>), string, int, int>("Penumbra.SetTemporaryModSettingsPlayer.V5")
            .InvokeFunc(index, mod, "", (false, true, 1_000_000, settings), Source, Key);
        if (on != 0) throw new InvalidOperationException($"Penumbra didn't switch its mod on (code {on})");
    }

    /// <summary>Takes off what Haven put on: the slot's item and dyes go back, and the mod's temporary setting goes.</summary>
    /// <summary>Takes off what Haven put on the player and on NPCs.</summary>
    public void TakeOff()
    {
        if (records.Worn is { } worn) TakeOff(worn, 0);
        foreach (var (index, npc) in npcWorn.ToArray()) { npcWorn.Remove(index); TakeOff(npc, index); }
    }

    private void TakeOff(PropWorn worn, int index)
    {
        if (index == 0) { records.Worn = null; Save(); }
        // The mod goes off first, then the item comes back; the character is then always drawn again, because a model
        // already loaded keeps the prop's files until it is redrawn (the chains stayed after standing up).
        if (index != 0) Prepare?.Invoke(index);
        try { plugin.GetIpcSubscriber<int, string, string, int, int>("Penumbra.RemoveTemporaryModSettingsPlayer.V5").InvokeFunc(index, worn.Mod, "", Key); }
        catch (Exception ex) { log.Warning(ex, "[PROP] Penumbra couldn't switch {Mod} off.", worn.Mod); }
        try
        {
            if (worn.Revert) revertState.Invoke(index);
            else if (Enum.TryParse<ApiEquipSlot>(worn.Slot, out var slot))
            {
                var set = setItem.Invoke(index, slot, worn.Item, worn.Stains);
                if (set is not (GlamourerApiEc.Success or GlamourerApiEc.NothingDone)) log.Warning("[PROP] Glamourer couldn't put the {Slot} item back ({Code}).", worn.Slot, set);
            }
        }
        catch (Exception ex) { log.Warning(ex, "[PROP] Glamourer couldn't put the {Slot} item back.", worn.Slot); }
        // Characters drawing from one collection share its temporary settings: whoever still wears this mod gets it back.
        foreach (var (other, still) in npcWorn.Where(w => w.Key != index && w.Value.Mod == worn.Mod).Append(records.Worn is { } mine && index != 0 && mine.Mod == worn.Mod ? new(0, mine) : default))
            if (still != null) try { SwitchOn(still.Mod, other); } catch (Exception ex) { log.Debug("[PROP] couldn't keep {Mod} on: {Message}", still.Mod, ex.Message); }
        Redraw(index);
        log.Information("[PROP] took off {Mod} ({Who}); the {Slot} item is back.", worn.Mod, index == 0 ? "you" : $"object {index}", worn.Slot);
    }

    private void Redraw(int index)
    {
        try { plugin.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5").InvokeAction(index, 0); }
        catch (Exception ex) { log.Debug("[PROP] redraw failed: " + ex.Message); }
    }

    /// <summary>Removes every prop mod Haven installed from Penumbra (and its files), after taking off what's worn.</summary>
    public int RemoveInstalled()
    {
        TakeOff();
        var removed = 0;
        foreach (var mod in records.Installed.Keys.ToArray())
        {
            try
            {
                var code = plugin.GetIpcSubscriber<string, string, int>("Penumbra.DeleteMod.V5").InvokeFunc(mod, "");
                if (code == 0) removed++;
                else log.Warning("[PROP] Penumbra didn't delete {Mod} (code {Code}).", mod, code);
            }
            catch (Exception ex) { log.Warning(ex, "[PROP] Penumbra couldn't delete {Mod}.", mod); }
            records.Installed.Remove(mod);
        }
        installing.Clear(); failed.Clear();
        Save();
        return removed;
    }

    private void Fail(PropWanted want, string why, Exception? ex)
    {
        failed.Add(want.Key);
        log.Warning(ex, "[PROP] {Name} couldn't be worn: {Why}.", want.Name, why);
        Failures.Enqueue($"Your animation's prop couldn't be put on: {why}. The animation plays without it.");
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recordsPath)!);
            var temporary = recordsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(records));
            File.Move(temporary, recordsPath, overwrite: true);
        }
        catch (IOException ex) { log.Warning(ex, "[Haven] The animation prop list could not be saved."); }
    }

    public void Dispose() => TakeOff();
}
