using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Haven;

/// <summary>
/// The skeletons NPCs are drawn with. Penumbra gives a character with no collection of its own the
/// Default one, so an NPC lacked the skeleton mods the player's own collection carries: an animation made for a modded
/// skeleton (more bones than the game's) didn't fit it, and the NPC T-posed on a pose ball. The player's collection
/// itself would also put their body and other cosmetic mods on the NPC, so for each visit Haven makes a temporary
/// collection holding only the base skeleton files (skeleton, skeleton parameters, physics) that the player's
/// collection changes, for every race, and assigns each NPC's game name to it before the NPC is drawn. Haven's own
/// temporary mods for every collection (pose animations, custom furniture) reach it too. Nothing is kept in Penumbra's
/// settings; the collection is deleted when the visit ends. Without any skeleton mods the NPCs keep the Default
/// collection, and an NPC the player gave a collection of its own in Penumbra keeps it. Every call fails quietly
/// without Penumbra. Framework thread only.
/// </summary>
internal sealed class NpcCollections(IDalamudPluginInterface plugin, IPluginLog log)
{
    private const string Tag = "Haven.NpcSkeletons";
    private const int Success = 0, NothingChanged = 1, CharacterCollectionExists = 6;
    // The base skeletons' race codes: Hyur (Midlander, Highlander), Elezen, Miqo'te, Roegadyn, Lalafell, Au Ra,
    // Hrothgar and Viera, each male then female.
    private static readonly string[] Races =
        ["0101", "0201", "0301", "0401", "0501", "0601", "0701", "0801", "0901", "1001", "1101", "1201", "1301", "1401", "1501", "1601", "1701", "1801"];

    private const string ModTag = "Haven.NpcMods", SkeletonUnderTag = "Haven.NpcModSkeletons";
    private Guid? collection;          // this visit's collection, once made; Guid.Empty when there was nothing to carry
    // Each game name Haven assigned, and to which of its collections. Penumbra keeps a temporary assignment on a name
    // until it is replaced or its collection deleted, so a name an NPC no longer carries (a rename, a removed NPC) would
    // otherwise keep another NPC's mods for whoever carries it next.
    private readonly Dictionary<string, Guid> boundTo = [];
    private Dictionary<string, string>? skeletons;    // the skeleton files the player's collection changes, once read
    // NPCs with mods of their own (NpcModShare): one collection each for the visit, holding those files and meta, and
    // the player's skeletons under them (an NPC's own skeleton wins).
    private readonly Dictionary<Guid, Guid> modCollections = [];
    private bool warned;

    /// <summary>Set by the plugin: an NPC's mods as this PC keeps them (game path → file, and meta), or null.</summary>
    public Func<Guid, (Dictionary<string, string> Paths, string Meta)?>? Mods { get; set; }

    /// <summary>
    /// Puts the NPC at this object index in its own mods' collection when this PC keeps mods for it, else in the
    /// visit's skeleton collection (below). Before it is drawn, or followed by a redraw.
    /// </summary>
    public void Assign(int index, string name, Guid npc)
    {
        if (Mods?.Invoke(npc) is { } mods && AssignMods(index, name, npc, mods)) return;
        Assign(index, name);
    }

    /// <summary>Drops an NPC's mods collection (its mods changed or were forgotten); assign it again after.</summary>
    public void Forget(Guid npc)
    {
        if (!modCollections.Remove(npc, out var id)) return;
        foreach (var name in boundTo.Where(x => x.Value == id).Select(x => x.Key).ToList()) boundTo.Remove(name);   // deleting it unassigns them
        try { plugin.GetIpcSubscriber<Guid, int>("Penumbra.DeleteTemporaryCollection.V5").InvokeFunc(id); }
        catch (Exception ex) { Unavailable(ex); }
    }

    private bool AssignMods(int index, string name, Guid npc, (Dictionary<string, string> Paths, string Meta) mods)
    {
        try
        {
            if (!modCollections.TryGetValue(npc, out var id))
            {
                var (made, created) = plugin.GetIpcSubscriber<string, string, (int, Guid)>("Penumbra.CreateTemporaryCollection.V6").InvokeFunc("Haven", $"Haven NPC {name}");
                if (made != Success || created == Guid.Empty) { log.Warning("[NPC-MODS] Penumbra wouldn't make {Name}'s collection (code {Code}).", name, made); return false; }
                var add = plugin.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5");
                var added = add.InvokeFunc(ModTag, created, mods.Paths, mods.Meta, 0);
                if (added != Success)
                {
                    log.Warning("[NPC-MODS] Penumbra wouldn't take {Name}'s mods (code {Code}).", name, added);
                    plugin.GetIpcSubscriber<Guid, int>("Penumbra.DeleteTemporaryCollection.V5").InvokeFunc(created);
                    return false;
                }
                if (Skeletons() is { Count: > 0 } under) add.InvokeFunc(SkeletonUnderTag, created, under, "", -1);
                modCollections[npc] = id = created;
                log.Information("[NPC-MODS] {Name} is drawn with {Count} file(s) of its own mods.", name, mods.Paths.Count);
            }
            // Forced: it replaces the skeleton collection the NPC may have been given before its mods arrived.
            var code = AssignAsPlayer(id, index, true);
            if (code is Success or NothingChanged) { boundTo[name] = id; return true; }
            log.Warning("[NPC-MODS] Penumbra wouldn't give {Name} its mods (code {Code}).", name, code);
            return false;
        }
        catch (Exception ex) { Unavailable(ex); return false; }
    }

    /// <summary>
    /// Dresses the player (object 0) in a temporary collection of these files and meta, forced over their own: how an
    /// NPC's kept mods are sent to guests again (NpcModShare). Nothing is written to Penumbra's settings; deleting the
    /// collection (Unwear) gives the player their own collection back. Its id, or null when Penumbra refused.
    /// </summary>
    public Guid? Wear(string label, (Dictionary<string, string> Paths, string Meta) files)
    {
        try
        {
            var (made, id) = plugin.GetIpcSubscriber<string, string, (int, Guid)>("Penumbra.CreateTemporaryCollection.V6").InvokeFunc("Haven", label);
            if (made != Success || id == Guid.Empty) { log.Warning("[NPC-MODS] Penumbra wouldn't make a collection to dress you in (code {Code}).", made); return null; }
            var added = plugin.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5").InvokeFunc(ModTag, id, files.Paths, files.Meta, 0);
            var code = added == Success ? plugin.GetIpcSubscriber<Guid, int, bool, int>("Penumbra.AssignTemporaryCollection.V5").InvokeFunc(id, 0, true) : added;
            if (code is Success or NothingChanged) return id;
            log.Warning("[NPC-MODS] Penumbra wouldn't dress you in the NPC's mods (code {Code}).", code);
            Unwear(id);
            return null;
        }
        catch (Exception ex) { Unavailable(ex); return null; }
    }

    /// <summary>Deletes a collection made by Wear, which gives the player their own collection back.</summary>
    public void Unwear(Guid id)
    {
        try { plugin.GetIpcSubscriber<Guid, int>("Penumbra.DeleteTemporaryCollection.V5").InvokeFunc(id); }
        catch (Exception ex) { Unavailable(ex); }
    }

    // The base skeleton files the player's collection changes (empty without any); read once per visit.
    private Dictionary<string, string> Skeletons()
    {
        if (skeletons != null) return skeletons;
        var paths = SkeletonPaths().ToArray();
        var (resolved, _) = plugin.GetIpcSubscriber<string[], string[], (string[], string[][])>("Penumbra.ResolvePlayerPaths").InvokeFunc(paths, []);
        skeletons = [];
        for (var i = 0; i < paths.Length && i < resolved.Length; i++)
            if (!string.IsNullOrEmpty(resolved[i]) && !string.Equals(resolved[i], paths[i], StringComparison.OrdinalIgnoreCase))
                skeletons[paths[i]] = resolved[i];
        return skeletons;
    }

    /// <summary>The game paths of every race's base skeleton files.</summary>
    internal static IEnumerable<string> SkeletonPaths()
        => Races.SelectMany(c => new[] { "sklb", "skp" }.Select(x => $"chara/human/c{c}/skeleton/base/b0001/skl_c{c}b0001.{x}")
            .Append($"chara/human/c{c}/skeleton/base/b0001/phy_c{c}b0001.phyb"));

    /// <summary>Puts the NPC at this object index (carrying this game name) in the visit's skeleton collection.</summary>
    public void Assign(int index, string name)
    {
        try
        {
            if (Prepare() is not { } id) return;
            // Forced only over another of Haven's collections (an NPC with mods carried this name before); a collection
            // the player gave this name in Penumbra is kept.
            var code = AssignAsPlayer(id, index, boundTo.TryGetValue(name, out var was) && was != id);
            if (code is Success or NothingChanged) { boundTo[name] = id; return; }
            if (code == CharacterCollectionExists)
            {
                if (!boundTo.ContainsKey(name)) log.Information("[NPC] {Name} keeps the Penumbra collection assigned to it.", name);
                return;
            }
            log.Warning("[NPC] Penumbra wouldn't give {Name} your skeletons (code {Code}).", name, code);
        }
        // Penumbra may not be ready yet: the next NPC shown (or the next visit) asks again.
        catch (Exception ex) { if (collection == Guid.Empty) collection = null; Unavailable(ex); }
    }

    // Penumbra assigns by the identity it draws the NPC with only while the NPC reads as a player (NativeEngine.HousingNpcAsPlayer).
    private int AssignAsPlayer(Guid id, int index, bool force)
        => HavenNative.NativeEngine.HousingNpcAsPlayer(index, () => plugin.GetIpcSubscriber<Guid, int, bool, int>("Penumbra.AssignTemporaryCollection.V5").InvokeFunc(id, index, force));

    /// <summary>Deletes the visit's collection (the visit ended or the home changed), which unassigns every NPC.</summary>
    public void Clear()
    {
        var id = collection;
        collection = null;
        boundTo.Clear();
        skeletons = null;
        foreach (var npc in modCollections.Keys.ToList()) Forget(npc);
        if (id is not { } made || made == Guid.Empty) return;
        try { plugin.GetIpcSubscriber<Guid, int>("Penumbra.DeleteTemporaryCollection.V5").InvokeFunc(made); }
        catch (Exception ex) { Unavailable(ex); }
    }

    // Makes the visit's collection from the skeleton files the player's collection changes; null without any. Penumbra's
    // names: ResolvePlayerPaths has no version suffix; CreateTemporaryCollection is V6 (it takes the caller's identity).
    private Guid? Prepare()
    {
        if (collection is { } known) return known == Guid.Empty ? null : known;
        collection = Guid.Empty;
        var changed = Skeletons();
        if (changed.Count == 0) { log.Information("[NPC] your Penumbra collection changes no skeletons; NPCs keep the Default collection."); return null; }
        var (made, id) = plugin.GetIpcSubscriber<string, string, (int, Guid)>("Penumbra.CreateTemporaryCollection.V6").InvokeFunc("Haven", "Haven NPCs");
        if (made != Success || id == Guid.Empty) { log.Warning("[NPC] Penumbra wouldn't make the NPCs' collection (code {Code}).", made); return null; }
        var added = plugin.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5").InvokeFunc(Tag, id, changed, "", 0);
        if (added != Success)
        {
            log.Warning("[NPC] Penumbra wouldn't take the NPCs' skeletons (code {Code}).", added);
            plugin.GetIpcSubscriber<Guid, int>("Penumbra.DeleteTemporaryCollection.V5").InvokeFunc(id);
            return null;
        }
        collection = id;
        log.Information("[NPC] NPCs are drawn with {Count} skeleton file(s) from your Penumbra collection, and nothing else of it.", changed.Count);
        return id;
    }

    private void Unavailable(Exception ex)
    {
        if (warned) return;
        warned = true;
        log.Information("[NPC] Penumbra isn't available to give NPCs your skeletons ({Message}).", ex.Message);
    }
}
