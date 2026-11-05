using System.Numerics;
using Haven.Core;

namespace Haven;

// NPCs saved in the home. Edits go through Commit like furnishings, so they are undoable, saved and
// shared (published by the owner, staged by decorators).
internal sealed partial class HousingController
{
    public IReadOnlyList<HomeNpc> Npcs => active?.Npcs ?? [];
    /// <summary>Set by the plugin: the player's Glamourer state, saved with a copied look (null without Glamourer).</summary>
    public Func<string?>? PlayerDesign { get; set; }
    /// <summary>Set by the plugin: the Customize+ profile on the player's character, packed (null with none).</summary>
    public Func<string?>? PlayerBody { get; set; }

    // The player's look as drawn, with their Glamourer state and Customize+ profile when they have them.
    private NpcLook PlayerLook() => (engine.HousingPlayerLook() ?? throw new InvalidOperationException("Your character isn't ready."))
        with { Design = PlayerDesign?.Invoke(), Body = PlayerBody?.Invoke() };

    /// <summary>Adds an NPC in the player's current look, standing where given.</summary>
    public void AddNpc(Vector3 position, float yaw)
    {
        if (active == null) return;
        var look = PlayerLook();
        if (Npcs.Count >= HomeNpcs.Max) throw new InvalidOperationException($"A home holds at most {HomeNpcs.Max} NPCs.");
        var npc = new HomeNpc { Name = HomeNpcs.NewName(Npcs.Select(x => x.Name)), Position = [position.X, position.Y, position.Z], Yaw = yaw, Look = look };
        Commit(active with { Npcs = [.. Npcs, npc] }, visual: false);
        Status = $"Added {npc.Name}.";
    }

    /// <summary>
    /// Stores where an NPC stands now and the emote it rests in (null: standing), and the pose ball it's on, with its
    /// fit there (it takes that ball whenever it's in the home; it stands at the spot when it leaves it).
    /// </summary>
    public void SaveNpcSpot(Guid id, Vector3 position, float yaw, ushort? emote)
    {
        var seat = EntryOf(PoseNpcs.Key(id)) is { Holds: true } entry && FindSet(entry.Furnishing, entry.Set) is { } found && entry.Role < found.Set.Roles.Length
            ? NpcSeat.From(entry with { Move = entry.Move + Vector3.UnitY * NpcLift(id) }, found.Set.Roles[entry.Role]) : null;
        ChangeNpc(id, x => x with { Position = [position.X, position.Y, position.Z], Yaw = yaw, Emote = seat == null ? emote : null, Seat = seat });
        if (seat != null) dismissedSeats.Remove(id);
        Status = seat != null ? "Saved: it takes this pose ball whenever it's here." : "Saved where it stands.";
    }
    /// <summary>Sends an NPC away (kept in the home, shown to nobody) or brings it back.</summary>
    public void SetNpcAway(Guid id, bool away) => ChangeNpc(id, x => x with { Away = away });
    /// <summary>Brings an NPC to a spot (in front of the player), standing, and back if it was away.</summary>
    public void BringNpc(Guid id, Vector3 position, float yaw) =>
        ChangeNpc(id, x => x with { Position = [position.X, position.Y, position.Z], Yaw = yaw, Emote = null, Seat = null, Away = false });
    public void CopyLookToNpc(Guid id)
    {
        var look = PlayerLook();
        ChangeNpc(id, x => x with { Look = look });
    }
    /// <summary>Saves a look made elsewhere (a Glamourer design the NPC was dressed in).</summary>
    public void SetNpcLook(Guid id, NpcLook look)
    {
        if (!look.Valid) throw new InvalidOperationException("That look can't be saved.");
        // A Glamourer look leaves the NPC's Customize+ profile as it was.
        ChangeNpc(id, x => x with { Look = look with { Body = look.Body ?? x.Look.Body } });
        Status = "Saved the NPC's new look.";
    }
    /// <summary>
    /// Lets go of every advanced customization colour in an NPC's Glamourer look, so its ordinary skin, hair, eye and lip
    /// colours show (an older look can keep advanced colours that no longer match them).
    /// </summary>
    public void ResetNpcAdvancedColours(Guid id)
    {
        if (Npcs.FirstOrDefault(x => x.Id == id)?.Look is not { Design: { } design } look || GlamourerStates.WithoutParameters(design) is not { } plain) return;
        ChangeNpc(id, x => x with { Look = look with { Design = plain } });
        Status = "Its advanced colours were reset; the ordinary colours show now.";
    }
    public void RenameNpc(Guid id, string name)
    {
        name = name.Trim();
        // A first and last name, as a player's: Glamourer and Penumbra recognise a character only by such a name.
        if (!HomeNpcs.ValidName(name) || !HomeNpcs.PlayerStyleName(name))
            throw new InvalidOperationException("An NPC needs a first and last name, each starting with a capital letter (letters, ' and - only).");
        ChangeNpc(id, x => x with { Name = name });
    }
    public void RemoveNpc(Guid id) => ChangeNpc(id, _ => null);
    /// <summary>The level its nameplate shows, 1 to HomeNpcs.MaxLevel; null shows none.</summary>
    public void SetNpcLevel(Guid id, byte? level)
    {
        if (level is 0 or > HomeNpcs.MaxLevel) throw new InvalidOperationException($"An NPC's level is 1 to {HomeNpcs.MaxLevel}, or none.");
        ChangeNpc(id, x => x with { Level = level });
    }

    /// <summary>Shapes an NPC with a Customize+ profile (packed, NpcBodies), or none.</summary>
    public void SetNpcBody(Guid id, string? body)
    {
        if (body != null && !NpcLook.ValidDesign(body)) throw new InvalidOperationException("That Customize+ profile is too large to keep with an NPC.");
        ChangeNpc(id, x => x with { Look = x.Look with { Body = body } });
        Status = body == null ? "Took the NPC's Customize+ profile away." : "Saved the NPC's Customize+ profile.";
    }

    private void ChangeNpc(Guid id, Func<HomeNpc, HomeNpc?> change)
    {
        if (active == null || Npcs.All(x => x.Id != id)) return;
        var list = Npcs.Select(x => x.Id == id ? change(x) : x).OfType<HomeNpc>();
        Commit(active with { Npcs = HomeNpcs.Stored(list) }, visual: false);
    }
}
