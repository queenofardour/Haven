using System.Numerics;
using Haven.Core;

namespace Haven;

// Saved NPCs brought into the home, and visiting NPCs beside the home's own.
// Visitors are never part of the home: the pose code only needs to know they are here.
internal sealed partial class HousingController
{
    /// <summary>Set by the plugin: the visiting NPCs drawn here now (NpcVisitors.Shown).</summary>
    public Func<IReadOnlyList<HomeNpc>>? Visitors { get; set; }

    /// <summary>Adds one of the player's saved NPCs to this home as a new NPC (a fresh id), standing where given.</summary>
    public void AddRosterNpc(RosterNpc saved, Vector3 position, float yaw)
    {
        if (active == null) return;
        if (!CanFurnish) throw new InvalidOperationException("Only the home's owner and its decorators can add NPCs to it.");
        if (Npcs.Count >= HomeNpcs.Max) throw new InvalidOperationException($"A home holds at most {HomeNpcs.Max} NPCs.");
        var npc = saved.ToHome([position.X, position.Y, position.Z], yaw);
        Commit(active with { Npcs = [.. Npcs, npc] }, visual: false);
        // The mods this PC keeps for the saved NPC come along (as for copies of a home).
        try { NpcsCopied?.Invoke(new Dictionary<Guid, Guid> { [saved.Id] = npc.Id }); }
            catch (Exception ex) { Failed?.Invoke($"{npc.Name} starts without its mods ({ex.Message})."); }
        Status = $"Added {npc.Name}.";
    }

    // The home's NPCs not away, and the visitors: those the pose code puts on balls.
    private bool NpcHere(Guid id) => active?.Npcs?.Any(n => n.Id == id && !n.Away) == true || Visitors?.Invoke().Any(n => n.Id == id) == true;
    private HomeNpc? NpcByKey(ulong key) =>
        active?.Npcs?.FirstOrDefault(n => PoseNpcs.Key(n.Id) == key) ?? Visitors?.Invoke().FirstOrDefault(n => PoseNpcs.Key(n.Id) == key);
}
