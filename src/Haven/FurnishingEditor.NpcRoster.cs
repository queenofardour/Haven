using Haven.Core;

namespace Haven;

// The NPCs panel's saved NPCs and visitors (NpcVisitors, NpcRosterUi).
internal sealed partial class FurnishingEditor
{
    public NpcVisitors? Visitors { get; set; }

    // The roster and the visitors here, added to the panel's model.
    private NpcPanelModel WithRoster(NpcPanelModel model)
    {
        if (Visitors == null) return model;
        var me = engine.HousingCharacterId;
        return model with
        {
            Roster = [.. Visitors.Roster.Select(x => new RosterRow(x.Id, x.Name, x.Level ?? 0, x.Look.Design != null, x.Look.Body != null,
                NpcMods?.Stored(x.Id) != null, NpcMods?.Stored(x.Id)?.Mods ?? 0))],
            Visitors = [.. Visitors.Listed.Select(v => new VisitorRow(v.Npc.Id, v.Npc.Name, v.BroughtBy, v.Bringer == me, v.Welcomed,
                Npcs?.Present(v.Npc.Id) == true, Npcs?.IsPossessed(v.Npc.Id) == true, Npcs?.HeldByOther(v.Npc.Id) == true, Npcs?.MayPossess(v.Npc.Id) == true, v.Npc.Rights)
                { HasMods = NpcMods?.Stored(v.Npc.Id) != null, ModCount = NpcMods?.Stored(v.Npc.Id)?.Mods ?? 0, Mods = NpcMods?.Status(v.Npc.Id) ?? "", Glamoured = v.Npc.Look.Design != null })],
            CannotBring = Visitors.CannotBring,
            OwnsHome = housing.OwnsActiveHouse && housing.IsSharedVisit,
            RefusesVisitors = Visitors.Refusing,
            VisitNote = Visitors.Note,
        };
    }

    private void RosterCommand(RoomCommand command, NpcPanelModel model)
    {
        if (Visitors == null) return;
        var saved = command.Index >= 0 && command.Index < model.Roster.Count ? model.Roster[command.Index].Id : (Guid?)null;
        var visitor = command.Index >= 0 && command.Index < model.Visitors.Count ? model.Visitors[command.Index].Id : (Guid?)null;
        switch (command.Action)
        {
            case RoomAction.SaveNpcToRoster when command.Index >= 0 && command.Index < housing.Npcs.Count:
                var npc = housing.Npcs[command.Index];
                housing.Run(() => Visitors.SaveToRoster(npc));
                break;
            case RoomAction.RosterRename when saved is { } id: housing.Run(() => Visitors.Rename(id, command.Text ?? "")); break;
            case RoomAction.RosterRemove when saved is { } id: housing.Run(() => Visitors.Remove(id)); break;
            case RoomAction.RosterBringHome when saved is { } id && Npcs?.InFront() is { } spot:
                housing.Run(() => housing.AddRosterNpc(Visitors.Roster.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("That NPC isn't in Your NPCs any more."),
                    spot.Position, spot.Yaw));
                break;
            case RoomAction.RosterBringVisit when saved is { } id && Npcs?.InFront() is { } spot: housing.Run(() => Visitors.Bring(id, spot.Position, spot.Yaw)); break;
            case RoomAction.VisitorPossess when visitor is { } id && Npcs != null:
                if (Npcs.IsPossessed(id)) Npcs.Release();
                else if (Npcs.Possess(id) is { } said && !said.StartsWith("You're controlling", StringComparison.Ordinal)) housing.Run(() => throw new InvalidOperationException(said));
                break;
            case RoomAction.VisitorAway when visitor is { } id: housing.Run(() => Visitors.SendAway(id)); break;
            case RoomAction.VisitorRights when visitor is { } id && Enum.IsDefined((VisitRights)(byte)command.Level):
                housing.Run(() => Visitors.SetRights(id, (VisitRights)(byte)command.Level));
                break;
            case RoomAction.RefuseVisitors: housing.Run(() => Visitors.Refusing = command.Level > 0); break;
            case RoomAction.VisitorSendMods when visitor is { } id && NpcMods?.MayShareFor(id) == true: AskToSendNpcMods(id); break;
            case RoomAction.VisitorFinishMods: NpcMods?.FinishSharing(); break;
        }
    }
}
