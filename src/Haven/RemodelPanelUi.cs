using System.Numerics;
using Dalamud.Bindings.ImGui;
using static Haven.HavenTokens;

namespace Haven;

// The shell piece the panel shows: picked in the room (or a removed one picked from the list).
internal readonly record struct RemodelPiece(uint Id, string Label, string Category, bool Hidden, bool KeepCollision)
{
    /// <summary>Pillars and beams still standing along this piece (a floor's walls or structure), offered to remove with it.</summary>
    public int Attached { get; init; }
    /// <summary>How many of the attached pieces are windows.</summary>
    public int AttachedWindows { get; init; }
    /// <summary>Moved with Move it: a building piece stands in for it, and putting it back removes that.</summary>
    public bool Moved { get; init; }
    /// <summary>Whether it has collision at all (the outer shell doesn't), and whether Move it can copy it.</summary>
    public bool Solid { get; init; } = true;
    public bool Movable { get; init; } = true;
}
// A removed piece in the list: its label, whether it is still solid, and whether it was moved rather than removed.
internal readonly record struct RemodelRemoved(uint Id, string Label, bool KeepCollision)
{
    public bool Moved { get; init; }
}

internal sealed record RemodelPanelModel(RemodelPiece? Selected, IReadOnlyList<RemodelRemoved> Removed, bool Ready, string Status);

internal enum RemodelAction { None, Remove, Move, Restore, RestoreAll, Select, RestoreGroup, RemoveWithAttached }
internal readonly record struct RemodelCommand(RemodelAction Action, uint Piece = 0, bool KeepCollision = false, uint[]? Pieces = null);

// The Remodel panel: point at a piece of the house itself in the room to choose it, then remove it
// (optionally keeping it solid underfoot) or turn it into a building piece to move; removed pieces are listed to put back,
// grouped by kind. Hovered tells the room which removed piece to show as a ghost (a row pointed at).
internal static class RemodelPanelUi
{
    private static uint keepFor;
    private static bool keep;
    private static readonly HashSet<string> expanded = [];

    // Floors, stairs and terraces stay solid by default: a removed floor is most often replaced, and nobody should fall.
    public static bool SolidByDefault(string category) => category is "Floors" or "Stairs" or "Railings & trim" or "Ceilings & room sections";
    // An inn room's pieces (BuildingCatalogue.InnRoom): its floors and structure stay solid when removed, its furniture doesn't.
    private const string InnRoom = "Inn room";
    private static bool SolidByDefault(RemodelPiece piece) => piece.Category == InnRoom ? piece.Label is "Floor" or "Stairs" or "Room structure" : SolidByDefault(piece.Category);

    public static RemodelCommand Draw(RemodelPanelModel model, ref bool open, float top) => Draw(model, ref open, top, out _);
    public static RemodelCommand Draw(RemodelPanelModel model, ref bool open, float top, out uint hovered)
    {
        var command = default(RemodelCommand);
        hovered = 0;
        if (SidePanelUi.Begin("Remodel", "Haven.RemodelPanel", ref open, top))
        {
            if (!model.Ready) HavenKit.Caption("Reading the house's own pieces…");
            else if (model.Selected is not { } piece)
                HavenKit.EmptyState(HavenIcon.Wrench, "Remodel the house itself",
                    "Point at a pillar, beam, wall, floor, stair, window, door or ceiling light of the house and click it. You can remove it, or turn it into a piece you can move.", "");
            else Selected(piece, ref command);

            if (model.Removed.Count > 0)
            {
                ImGui.Dummy(Px(0, Space2));
                HavenKit.Rule();
                ImGui.Dummy(Px(0, Space2));
                HavenKit.SectionTitle("Removed", model.Removed.Count == 1 ? "1 piece" : $"{model.Removed.Count} pieces");
                HavenKit.Caption("Point at a row to see the piece in the room.");
                foreach (var group in model.Removed.GroupBy(x => x.Label))
                {
                    var pieces = group.ToArray();
                    if (pieces.Length == 1) { Removed(pieces[0], model, ref command, ref hovered); continue; }
                    var unfolded = expanded.Contains(group.Key);
                    if (HavenKit.ListRow($"##removed-group{group.Key}", group.Key, $"{pieces.Length} removed · {(unfolded ? "click to fold" : "click to list them")}", false, HavenIcon.Wrench))
                    { if (!expanded.Remove(group.Key)) expanded.Add(group.Key); }
                    ImGui.Dummy(Px(0, 2));
                    if (!unfolded) continue;
                    ImGui.Indent(Px(Space3));
                    for (var i = 0; i < pieces.Length; i++) Removed(pieces[i], model, ref command, ref hovered, $"{group.Key} {i + 1}");
                    if (HavenKit.Button($"Put these {pieces.Length} back##remodel-group{group.Key}", ButtonKind.Ghost, new(-1, 0)))
                        command = new(RemodelAction.RestoreGroup, Pieces: [.. pieces.Select(x => x.Id)]);
                    ImGui.Unindent(Px(Space3));
                }
                if (HavenKit.Button("Put everything back##remodel-all", ButtonKind.Ghost, new(-1, 0))) command = new(RemodelAction.RestoreAll);
            }
            if (model.Status.Length > 0) HavenKit.Caption(model.Status);
        }
        SidePanelUi.End(open);
        return command;
    }

    private static void Removed(RemodelRemoved removed, RemodelPanelModel model, ref RemodelCommand command, ref uint hovered, string? name = null)
    {
        var detail = removed.Moved ? "Moved elsewhere" : removed.KeepCollision ? "Still solid underfoot" : "Gone";
        if (HavenKit.ListRow($"##removed{removed.Id}", name ?? removed.Label, detail, model.Selected?.Id == removed.Id, HavenIcon.Wrench)) command = new(RemodelAction.Select, removed.Id);
        if (ImGui.IsItemHovered()) hovered = removed.Id;
        ImGui.Dummy(Px(0, 2));
    }

    private static void Selected(RemodelPiece piece, ref RemodelCommand command)
    {
        if (keepFor != piece.Id) { keepFor = piece.Id; keep = piece.Hidden ? piece.KeepCollision : SolidByDefault(piece); }
        HavenKit.SectionTitle(piece.Label, piece.Moved ? "Moved" : piece.Hidden ? "Removed" : piece.Category == InnRoom ? "Part of the room" : "Part of the house");
        if (piece.Category is "Walls" or "Floors" or "Ceilings & room sections")
            HavenKit.Caption("The game builds this as one piece for the whole floor, so it comes off all at once.");
        if (piece.Category == InnRoom)
            HavenKit.Caption(!piece.Movable ? "Part of the inn room as the game builds it. It can be removed and put back, not moved."
                : piece.Label == "Lamp" ? "Part of the inn room as the game builds it. Moved, it leaves its light behind, switched off."
                : "Part of the inn room as the game builds it.");
        else if (!piece.Movable)
            HavenKit.Caption("The hull around the whole house. Without it you can see out into the space beyond and build there; outdoor light may reach in.");
        if (piece.Moved)
            HavenKit.Caption("It stands somewhere else now. Point at it there to choose it again. Putting it back returns it to its place in the house.");
        ImGui.Dummy(Px(0, Space1));
        if (piece.Solid && !piece.Moved)
        {
            if (HavenKit.Toggle("Keep it solid##remodel-keep", ref keep) && piece.Hidden) command = new(RemodelAction.Remove, piece.Id, keep);
            HavenKit.Caption(keep ? "Invisible, but you can still stand on it and bump into it." : "You can walk and fall through where it was. If you fall out of the house, you're brought back.");
            ImGui.Dummy(Px(0, Space1));
        }
        var half = (ImGui.GetContentRegionAvail().X - Px(Space2)) / 2;
        if (piece.Hidden)
        {
            if (HavenKit.Button("Put it back##remodel-restore", ButtonKind.Primary, new(piece.Moved ? half : -1, 0))) command = new(RemodelAction.Restore, piece.Id);
            if (piece.Moved)
            {
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Returns it to where it was in the house.");
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Move it##remodel-move", ButtonKind.Secondary, new(half, 0))) command = new(RemodelAction.Move, piece.Id);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pick it up again from where it stands now.");
            }
        }
        else
        {
            if (HavenKit.Button("Remove##remodel-remove", ButtonKind.Primary, new(piece.Movable ? half : -1, 0))) command = new(RemodelAction.Remove, piece.Id, keep && piece.Solid);
            if (piece.Movable)
            {
                ImGui.SameLine(0, Px(Space2));
                if (HavenKit.Button("Move it##remodel-move", ButtonKind.Secondary, new(half, 0))) command = new(RemodelAction.Move, piece.Id);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pick it up to move, turn or scale it. Set it down where it was (or cancel) and nothing changes; put it back later to return it to its place.");
            }
            if (piece.Attached > 0)
            {
                ImGui.Dummy(Px(0, Space1));
                var label = AttachedText(piece.Attached - piece.AttachedWindows, piece.AttachedWindows);
                if (HavenKit.Button($"{label}##remodel-attached", ButtonKind.Secondary, new(-1, 0))) command = new(RemodelAction.RemoveWithAttached, piece.Id, keep);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(piece.AttachedWindows > 0 ? "The pillars and beams along it and its windows come off too, in one step (one undo)." : "The pillars and beams standing along it come off too, in one step (one undo).");
            }
        }
    }

    // "Remove with its 9 pillars and 4 windows": short enough for the panel's one button.
    internal static string AttachedText(int pillars, int windows)
    {
        var parts = new List<string>();
        if (pillars > 0) parts.Add(pillars == 1 ? "1 pillar" : $"{pillars} pillars");
        if (windows > 0) parts.Add(windows == 1 ? "1 window" : $"{windows} windows");
        return "Remove with its " + string.Join(" and ", parts);
    }
}
