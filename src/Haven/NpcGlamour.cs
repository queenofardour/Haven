using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;

namespace Haven;

/// <summary>
/// Glamourer for NPCs: the player's state to copy onto an NPC, the player's designs to dress one in,
/// and applying a saved state to this PC's copy once it is drawn. Glamourer knows a character by its name and home
/// world, and only a name in player-name form (HomeNpcs.GameNames), so each NPC's game character carries one. Every call fails quietly without Glamourer: the NPC
/// then shows its plain saved look. Framework thread only.
/// </summary>
internal sealed class NpcGlamour(IDalamudPluginInterface plugin, IPluginLog log)
{
    // Haven's key for the NPCs' states. States are not locked with it: the player edits an NPC in Glamourer's own
    // window (it lists the NPC by its game name) and then keeps the result (NpcPossession.ReadGlamour); a lock would
    // refuse those edits. Unlock stays, for states an earlier version locked.
    private const uint Key = 0x4E504348;
    private const ApplyFlag Flags = ApplyFlag.Equipment | ApplyFlag.Customization;
    private readonly global::Glamourer.Api.IpcSubscribers.GetStateBase64 getState = new(plugin);
    private readonly global::Glamourer.Api.IpcSubscribers.ApplyState applyState = new(plugin);
    private readonly global::Glamourer.Api.IpcSubscribers.ApplyDesign applyDesign = new(plugin);
    private readonly global::Glamourer.Api.IpcSubscribers.RevertState revertState = new(plugin);
    private readonly global::Glamourer.Api.IpcSubscribers.UnlockState unlockState = new(plugin);
    private readonly global::Glamourer.Api.IpcSubscribers.GetDesignList designList = new(plugin);
    private bool warned, penumbraWarned;

    /// <summary>
    /// Has Penumbra and Glamourer read a character's name again. Both keep a table of which object carries which name
    /// and rebuild it only when the game adds or removes an object; an NPC renamed, put away or reused in place keeps
    /// its old entry, so Glamourer accepted its look but found no object to show it on. Setting the NPC's cutscene
    /// parent (itself, as its own copy made it) asks every plugin sharing that table to rebuild it.
    /// </summary>
    public void Reidentify(int index)
    {
        try
        {
            var code = plugin.GetIpcSubscriber<int, int, int>("Penumbra.SetCutsceneParentIndex.V5").InvokeFunc(index, index);
            if (code != 0 && !penumbraWarned) { penumbraWarned = true; log.Warning("[NPC] Penumbra wouldn't refresh object {Index} (code {Code}).", index, code); }
        }
        catch (Exception ex)
        {
            if (penumbraWarned) return;
            penumbraWarned = true;
            log.Information("[NPC] Penumbra isn't available to refresh NPC names ({Message}).", ex.Message);
        }
    }

    /// <summary>The Glamourer state of a character by object index (0 is the player), or null.</summary>
    public string? State(int index)
    {
        try
        {
            var (code, state) = getState.Invoke(index, index == 0 ? 0 : Key);   // the player's own state isn't Haven's to unlock
            return code == GlamourerApiEc.Success && state != null && Core.NpcLook.ValidDesign(state) ? state : null;
        }
        catch (Exception ex) { Unavailable(ex); return null; }
    }

    /// <summary>Applies a saved state to a character made here; true when Glamourer took it.</summary>
    public bool Apply(int index, string state, bool quiet = false)
    {
        try
        {
            var code = applyState.Invoke(state, index, Key, Flags);
            if (code != GlamourerApiEc.Success && !quiet) log.Warning("[NPC] Glamourer refused the look for object {Index}: {Code}.", index, code);
            return code == GlamourerApiEc.Success;
        }
        catch (Exception ex) { Unavailable(ex); return false; }
    }

    /// <summary>
    /// Puts a state on the player's own character (sharing an NPC's mods: the player wears the NPC's look for a moment,
    /// then their own state again). Not locked, so the player's Glamourer stays theirs to change. Their own look goes
    /// back with Once (restore), as a one-off change rather than a state Glamourer then holds against the game: held,
    /// later gearset and glamour changes didn't show until the player reverted in Glamourer.
    /// </summary>
    public bool ApplyToPlayer(string state, bool restore = false)
    {
        try
        {
            var code = applyState.Invoke(state, 0, 0, restore ? Flags | ApplyFlag.Once : Flags);
            if (code != GlamourerApiEc.Success) log.Warning("[NPC-MODS] Glamourer refused a look for your character: {Code}.", code);
            return code == GlamourerApiEc.Success;
        }
        catch (Exception ex) { Unavailable(ex); return false; }
    }

    /// <summary>Puts the player's own character back to its game look (when no Glamourer state was kept to restore).</summary>
    public void RevertPlayer()
    {
        try { revertState.Invoke(0, 0, Flags | ApplyFlag.Once); }
        catch (Exception ex) { Unavailable(ex); }
    }

    /// <summary>Dresses a character made here in one of the player's designs, and returns its state as dressed.</summary>
    public string? Dress(int index, Guid design)
    {
        try
        {
            var code = applyDesign.Invoke(design, index, Key, Flags);
            if (code != GlamourerApiEc.Success) { log.Warning("[NPC] Glamourer refused the design for object {Index}: {Code}.", index, code); return null; }
            return State(index);
        }
        catch (Exception ex) { Unavailable(ex); return null; }
    }

    /// <summary>Puts a character back to its own (game) look, dropping any state Glamourer keeps for its name.</summary>
    public void Revert(int index)
    {
        try { revertState.Invoke(index, Key, ApplyFlag.Equipment | ApplyFlag.Customization); }
        catch (Exception ex) { Unavailable(ex); }
    }

    /// <summary>
    /// Lets go of Haven's lock on a character's state before it is renamed, put away or removed. Nothing is reverted:
    /// reverting redraws, and the object is about to change anyway.
    /// </summary>
    public void Unlock(int index)
    {
        try { unlockState.Invoke(index, Key); }
        catch (Exception ex) { Unavailable(ex); }
    }

    /// <summary>The player's Glamourer designs, by name; empty without Glamourer.</summary>
    public IReadOnlyList<(Guid Id, string Name)> Designs()
    {
        try { return designList.Invoke().Select(x => (x.Key, x.Value)).OrderBy(x => x.Value, StringComparer.CurrentCultureIgnoreCase).ToList(); }
        catch (Exception ex) { Unavailable(ex); return []; }
    }

    private void Unavailable(Exception ex)
    {
        if (warned) return;
        warned = true;
        log.Information("[NPC] Glamourer isn't available ({Message}); NPCs show their plain saved look.", ex.Message);
    }
}
