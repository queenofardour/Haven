using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace HavenNative;

// Sharing an NPC's mods (Haven.NpcModShare): finding a gathering member's character, reading how the player is drawn,
// and /haven peers. Framework thread only.
public sealed partial class NativeEngine
{
    /// <summary>The object-table index of a gathering member's character here (0 for the local player), or null.</summary>
    public unsafe int? HousingMemberObjectIndex(ulong contentId)
    {
        if (contentId == 0) return null;
        if (objectTable.LocalPlayer is { } local && LocalContentId() == contentId) return 0;
        foreach (var (peer, actor) in stateApply.Peers)
            if (actor.ContentId == contentId && relay.HousingMembers.ContainsKey(peer) && actor.ObjectIndex is { } index
                && objectTable[(int)index] is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player && ((Character*)player.Address)->ContentId == contentId)
                return (int)index;
        return null;
    }

    /// <summary>
    /// Runs a call (a Penumbra collection assignment) with an NPC's object reading as a player character. Penumbra
    /// draws Haven's NPCs (BattleNpcs without a name id) with the collection of the player their name and world make,
    /// but its AssignTemporaryCollection identifies them without that allowance, as a nameless NPC it can't assign, and
    /// answers UnknownError (255). As a player character the same object gives the same player identity it is drawn by.
    /// </summary>
    public static unsafe T HousingNpcAsPlayer<T>(int objectIndex, Func<T> call)
    {
        var npc = NpcIn((uint)(objectIndex - ClientObjectBase));
        if (npc == null || npc->GameObject.ObjectKind != ObjectKind.BattleNpc) return call();
        npc->GameObject.ObjectKind = ObjectKind.Pc;
        try { return call(); }
        finally { npc->GameObject.ObjectKind = ObjectKind.BattleNpc; }
    }

    /// <summary>
    /// The game conditions sync plugins pause on (Lightless halts its scans while any is set: combat, performing, a duty,
    /// a cutscene, between areas), that are on now; "none" when none is. Inside a home Lightless sent no new looks.
    /// </summary>
    public string HousingSyncPauses()
    {
        Dalamud.Game.ClientState.Conditions.ConditionFlag[] flags =
        [
            Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas, Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent, Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene78, Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty56, Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty95,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat, Dalamud.Game.ClientState.Conditions.ConditionFlag.Performing,
        ];
        var on = flags.Where(f => condition[f]).ToList();
        return on.Count == 0 ? "none" : string.Join(", ", on);
    }

    /// <summary>The local player's race, gender and clan as their model is drawn now (null without a human model).</summary>
    public unsafe (byte Race, byte Gender, byte Clan)? HousingDrawnBody()
    {
        if (objectTable.LocalPlayer is not { } player) return null;
        var draw = ((GameObject*)player.Address)->DrawObject;
        if (draw == null || ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw)->GetModelType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType.Human) return null;
        var customize = ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human*)draw)->Customize;
        return (customize.Race, customize.Sex, customize.Tribe);
    }

    /// <summary>For /haven peers (read-only): you and each gathering member here, with what decides whether they show.</summary>
    public unsafe List<string> HousingMemberDrawStates()
    {
        var lines = new List<string>();
        if (objectTable.LocalPlayer is { } local) lines.Add($"you [{local.ObjectIndex}]: {Services.ZoneLoadService.DrawState(local.Address)}");
        foreach (var (peer, actor) in stateApply.Peers)
        {
            var name = relay.HousingMembers.TryGetValue(peer, out var member) && member.Name.Length > 0 ? member.Name : peer;
            lines.Add(actor.ObjectIndex is { } index && objectTable[(int)index] is { } obj
                ? $"{name} [{index}]: {Services.ZoneLoadService.DrawState(obj.Address)}" : $"{name}: no character bound here");
        }
        // Outside a gathering (a friend not showing after a visit ended): every other player here.
        if (lines.Count <= 1)
            foreach (var other in objectTable.OfType<Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter>())
                if (other.Address != objectTable.LocalPlayer?.Address)
                    lines.Add($"{other.Name} [{other.ObjectIndex}]: {Services.ZoneLoadService.DrawState(other.Address)}");
        return lines;
    }
}
