using System.Numerics;
using Dalamud.Bindings.ImGui;
using Haven.Core;

namespace Haven;

// Remodel mode: with the Remodel panel open, the pointer picks the house's own pieces instead of
// furnishings. The game's collision ray says which collider it hit, and a shell piece's colliders carry its layout key,
// so that piece is the one (a wall behind a beam is the wall, not the beam). Pieces without collision (windows, doors,
// ceiling lights) win where the hit lies within their bounds, since the ray passes through them; with no collider hit,
// or a hit on something else, the smallest piece whose bounds hold the point stands in, visible ones before removed
// ones. A removed piece pointed at shows as a ghost. The piece under the pointer is outlined.
internal sealed partial class FurnishingEditor
{
    private uint remodelSelected;
    private uint remodelListHover;   // a removed piece's row pointed at in the panel (last frame)
    // A piece being moved with Move it: its key, the building piece standing in for it, and where that stood at first.
    private (uint Instance, Guid Copy, long Since, bool Seen)? remodelMoving;
    private Haven.Core.Furnishing? remodelMovedFrom;

    // Once a Move it ends: a piece set down where it stood (or a move cancelled) is simply put back, so no building piece
    // stands in for it. The move starts a frame or two later (housing actions run on the framework thread).
    private void SettleRemodelMove(bool placing)
    {
        if (remodelMoving is not { } moving) return;
        if (placing) { if (!moving.Seen) remodelMoving = moving with { Seen = true }; return; }
        if (!moving.Seen && Environment.TickCount64 - moving.Since < 2000) return;
        remodelMoving = null;
        var copy = housing.ActiveHouse?.Furnishings.FirstOrDefault(x => x.Id == moving.Copy);
        if (copy == null || remodelMovedFrom is not { } from) return;
        if (copy.Position.SequenceEqual(from.Position) && copy.Rotation == from.Rotation && copy.Scale == from.Scale && housing.IsShellPieceHidden(moving.Instance))
            housing.Run(() => housing.RestoreShellPieces([moving.Instance]));
    }

    private void RemodelPointer(bool focused, bool underUi, bool click)
    {
        ShellPieceInfo? hovered = null;
        Guid? movedCopy = null;
        if (focused && !underUi && TryRay(out var origin, out var direction))
        {
            // A moved piece where it stands now, if it's nearer than the house behind it.
            if (MovedPieceAt(origin, direction) is { } moved) (hovered, movedCopy) = (moved.Piece, moved.Copy);
            else hovered = PickShellPiece(origin, direction);
        }
        housing.HighlightedFurnishing = movedCopy;
        var removed = hovered != null && movedCopy == null && housing.IsShellPieceHidden(hovered.Instance);
        // A removed piece pointed at (in the room, or its row in the panel) shows as a see-through ghost.
        engine.HousingGhostShellPiece(remodelListHover != 0 ? remodelListHover : removed ? hovered!.Instance : 0);
        engine.HousingOutlineShellPiece(movedCopy != null ? 0 : hovered?.Instance ?? remodelSelected);
        if (hovered != null) ImGui.SetTooltip(movedCopy != null ? $"Moved: {hovered.Label} · click to choose" : removed ? $"Removed: {hovered.Label} · click to choose" : $"{hovered.Label} · click to choose");
        if (click && hovered != null) remodelSelected = hovered.Instance;
    }

    private (ShellPieceInfo Piece, Guid Copy)? MovedPieceAt(Vector3 origin, Vector3 direction)
    {
        if (housing.ActiveHouse is not { } house || house.Interior.HiddenPieces is not { Length: > 0 } hidden) return null;
        var wall = SurfaceHit(origin, direction) is { } hit ? Vector3.Distance(origin, hit.Point) : float.MaxValue;
        (ShellPieceInfo, Guid)? best = null;
        var distance = wall + .05f;
        foreach (var piece in hidden)
        {
            if (piece.MovedTo is not Guid copy || house.Furnishings.FirstOrDefault(x => x.Id == copy) is not { } item) continue;
            if (housing.ShellPieces.FirstOrDefault(x => x.Instance == piece.Instance) is not { } info) continue;
            foreach (var model in Catalogue.Prefab(item).Models)
                if (PlacementMath.RayBox(origin, direction, model.Minimum, model.Maximum,
                    housing.FurnitureModelTransform(item, model) * FurnitureCatalogue.ItemMatrix(item) * FurnitureCatalogue.HouseMatrix(house)) is float d && d < distance)
                { distance = d; best = (info, copy); }
        }
        return best;
    }

    private unsafe ShellPieceInfo? PickShellPiece(Vector3 origin, Vector3 direction)
    {
        // An inn room's pieces are mostly small and without collision, and a group's members answer to their own keys:
        // they're picked by the triangles under the pointer (the shown ones first, then the removed).
        if (housing.ShellPieces is { Count: > 0 } all && all[0].Models != null)
            return InnPieceAt(origin, direction, false) ?? InnPieceAt(origin, direction, true);
        if (!FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule.RaycastMaterialFilter(origin, direction, out var hit, 100))
            return OuterShellAt(origin, direction, null);
        Vector3 point = hit.Point;
        if (!float.IsFinite(point.LengthSquared())) return null;
        if (OuterShellAt(origin, direction, Vector3.Distance(origin, point)) is { } hull) return hull;
        var pieces = housing.ShellPieces;
        // A piece without collision the ray went through (a window in the wall it hit).
        if (SmallestAt(point, pieces.Where(p => !p.Collision && !housing.IsShellPieceHidden(p.Instance))) is { } open) return open;
        var key = hit.Object == null ? 0u : (uint)(hit.Object->LayoutObjectId & 0xFFFFFFFF);
        if (key != 0 && pieces.FirstOrDefault(p => p.Instance == key) is { } struck) return struck;
        return SmallestAt(point, pieces.Where(p => !housing.IsShellPieceHidden(p.Instance))) ?? SmallestAt(point, pieces.Where(p => housing.IsShellPieceHidden(p.Instance)));
    }

    private ShellPieceInfo? InnPieceAt(Vector3 origin, Vector3 direction, bool removed)
    {
        ShellPieceInfo? best = null; var nearest = float.MaxValue;
        foreach (var piece in housing.ShellPieces)
        {
            if (piece.Models is not { } models || housing.IsShellPieceHidden(piece.Instance) != removed) continue;
            if (PlacementMath.RayBox(origin, direction, piece.Minimum, piece.Maximum, piece.World) is not float near || near >= nearest) continue;
            foreach (var (path, local) in models)
            {
                var world = local * piece.World;
                // Until a model's triangles are read, the piece's box stands in.
                var hit = housing.PickMeshes.Get(path) is { } mesh ? PlacementMath.RayTriangles(origin, direction, mesh.Positions, mesh.Triangles, world) : near;
                if (hit is float d && d < nearest) { nearest = d; best = piece; }
            }
        }
        return best;
    }

    // The outer shell has no collision, so the ray never hits it: it's the piece when the ray leaves its box before
    // reaching anything solid (pointing at the hull past removed walls, or at the void where it was).
    private ShellPieceInfo? OuterShellAt(Vector3 origin, Vector3 direction, float? hitDistance)
    {
        foreach (var piece in housing.ShellPieces)
        {
            if (piece.Category != BuildingCatalogue.OuterShell || !Matrix4x4.Invert(piece.World, out var toPiece)) continue;
            var from = Vector3.Transform(origin, toPiece);
            var way = Vector3.TransformNormal(direction, toPiece);
            if (from.X < piece.Minimum.X || from.Y < piece.Minimum.Y || from.Z < piece.Minimum.Z
                || from.X > piece.Maximum.X || from.Y > piece.Maximum.Y || from.Z > piece.Maximum.Z) continue;
            var exit = float.MaxValue;
            for (var axis = 0; axis < 3; axis++)
            {
                var d = axis == 0 ? way.X : axis == 1 ? way.Y : way.Z;
                var o = axis == 0 ? from.X : axis == 1 ? from.Y : from.Z;
                var face = d > 0 ? (axis == 0 ? piece.Maximum.X : axis == 1 ? piece.Maximum.Y : piece.Maximum.Z)
                    : (axis == 0 ? piece.Minimum.X : axis == 1 ? piece.Minimum.Y : piece.Minimum.Z);
                if (Math.Abs(d) > 1e-6f) exit = Math.Min(exit, (face - o) / d);
            }
            // The ray's length in world units (the transform may scale).
            var length = Vector3.Distance(origin, Vector3.Transform(from + way * exit, piece.World));
            if (hitDistance is not float distance || distance > length + .3f) return piece;
        }
        return null;
    }

    private static ShellPieceInfo? SmallestAt(Vector3 point, IEnumerable<ShellPieceInfo> pieces)
    {
        ShellPieceInfo? best = null; var bestVolume = float.MaxValue;
        foreach (var piece in pieces)
        {
            if (!Matrix4x4.Invert(piece.World, out var toPiece)) continue;
            var local = Vector3.Transform(point, toPiece);
            var margin = new Vector3(.15f);
            if (local.X < piece.Minimum.X - margin.X || local.Y < piece.Minimum.Y - margin.Y || local.Z < piece.Minimum.Z - margin.Z
                || local.X > piece.Maximum.X + margin.X || local.Y > piece.Maximum.Y + margin.Y || local.Z > piece.Maximum.Z + margin.Z) continue;
            // A whole floor's walls or structure encloses the room: it counts only near its outside (walls at their sides;
            // the structure also at its top and bottom, where its ceiling, footings and rafters are), not anywhere inside.
            if (piece.Category == BuildingCatalogue.OuterShell) continue; // found by OuterShellAt
            if (piece.Category is "Walls" or "Ceilings & room sections")
            {
                var inside = Vector3.Min(local - piece.Minimum, piece.Maximum - local);
                var nearSide = Math.Min(inside.X, inside.Z) <= .6f;
                var nearTopOrBottom = piece.Maximum.Y - local.Y <= .6f || local.Y - piece.Minimum.Y <= .8f;
                if (piece.Category == "Walls" ? !nearSide : !nearSide && !nearTopOrBottom) continue;
            }
            var size = Vector3.Max(piece.Maximum - piece.Minimum, new Vector3(.05f));
            var volume = size.X * size.Y * size.Z;
            if (volume < bestVolume) { best = piece; bestVolume = volume; }
        }
        return best;
    }

    // The pillars and beams that go with a whole floor's walls or structure: those along the walls' sides, or within the
    // structure's height, that are still there.
    private uint[] AttachedPieces(ShellPieceInfo piece)
    {
        if (piece.Category is not ("Walls" or "Ceilings & room sections") || !Matrix4x4.Invert(piece.World, out var toPiece)) return [];
        var result = new List<uint>();
        foreach (var other in housing.ShellPieces)
        {
            // Pillars and beams, and for walls their windows too.
            var window = other.Category == "Windows";
            if (!(other.Category == "Pillars" || window && piece.Category == "Walls") || housing.IsShellPieceHidden(other.Instance)) continue;
            var (min, max) = Corners(other).Select(c => Vector3.Transform(c, toPiece)).Aggregate((Min: new Vector3(float.MaxValue), Max: new Vector3(float.MinValue)),
                (box, c) => (Vector3.Min(box.Min, c), Vector3.Max(box.Max, c)));
            // Its middle within the piece's height (a floor's pillars, not the ones of the floors above and below that
            // reach into it), and inside its footprint.
            var middle = (min.Y + max.Y) / 2;
            if (middle < piece.Minimum.Y || middle > piece.Maximum.Y) continue;
            var reach = window ? 1.2f : .5f;   // a window's sill and frame stand out past the wall
            if (max.X < piece.Minimum.X - reach || min.X > piece.Maximum.X + reach || max.Z < piece.Minimum.Z - reach || min.Z > piece.Maximum.Z + reach) continue;
            if (piece.Category == "Walls")
            {
                // Along the walls: touching the ring of the walls' sides.
                var gap = Math.Min(Math.Min(min.X - piece.Minimum.X, piece.Maximum.X - max.X), Math.Min(min.Z - piece.Minimum.Z, piece.Maximum.Z - max.Z));
                if (gap > (window ? 1.2f : .6f)) continue;
            }
            result.Add(other.Instance);
        }
        return [.. result];
    }

    private static IEnumerable<Vector3> Corners(ShellPieceInfo piece)
    {
        var (a, b) = (piece.Minimum, piece.Maximum);
        foreach (var x in new[] { a.X, b.X }) foreach (var y in new[] { a.Y, b.Y }) foreach (var z in new[] { a.Z, b.Z })
            yield return Vector3.Transform(new Vector3(x, y, z), piece.World);
    }

    private void DrawRemodelPanel(ref bool open)
    {
        if (housing.ActiveHouse is not { } house) { open = false; return; }
        var pieces = housing.ShellPieces.ToDictionary(x => x.Instance);
        housing.NoteShellPieces(pieces.Count);
        var chosen = remodelSelected != 0 ? pieces.GetValueOrDefault(remodelSelected) : null;
        var attached = chosen != null && !housing.IsShellPieceHidden(chosen.Instance) ? AttachedPieces(chosen) : [];
        RemodelPiece? selected = chosen == null ? null
            : new RemodelPiece(chosen.Instance, chosen.Label, chosen.Category, housing.IsShellPieceHidden(chosen.Instance), housing.KeepsShellCollision(chosen.Instance))
            { Attached = attached.Length, AttachedWindows = attached.Count(x => pieces.GetValueOrDefault(x)?.Category == "Windows"), Moved = housing.IsShellPieceMoved(chosen.Instance), Solid = chosen.Collision, Movable = chosen.Category != BuildingCatalogue.OuterShell && Catalogue.CanBuild(chosen.Path)
                // A tilted or stretched piece is refused by Move it (DetachShellPiece): say so instead of offering it.
                && ShellRemodel.Copy(chosen.World, chosen.Minimum, chosen.Maximum) != null };
        var removed = (house.Interior.HiddenPieces ?? []).Select(x => new RemodelRemoved(x.Instance, pieces.TryGetValue(x.Instance, out var p) ? p.Label : "A piece of the house", x.KeepCollision)
            { Moved = housing.IsShellPieceMoved(x.Instance) }).ToArray();
        var command = RemodelPanelUi.Draw(new RemodelPanelModel(selected, removed, Catalogue.BuildingsReady, ""), ref open, SidePanelUi.Top, out remodelListHover);
        var id = command.Piece;
        switch (command.Action)
        {
            case RemodelAction.Select: remodelSelected = id; break;
            case RemodelAction.Remove: housing.Run(() => housing.HideShellPieces([id], command.KeepCollision)); break;
            case RemodelAction.RemoveWithAttached: housing.Run(() => housing.HideShellPieces([id, .. attached], command.KeepCollision)); break;
            case RemodelAction.Restore: housing.Run(() => housing.RestoreShellPieces([id])); break;
            case RemodelAction.RestoreAll: housing.Run(() => housing.RestoreShellPieces(null)); break;
            case RemodelAction.RestoreGroup when command.Pieces is { } group: housing.Run(() => housing.RestoreShellPieces(group)); break;
            case RemodelAction.Move when housing.MovedShellCopy(id) is Guid standing:
                // Moved before: pick its stand-in up again where it stands; setting it down unchanged keeps it there.
                remodelMovedFrom = null;
                remodelMoving = (id, standing, Environment.TickCount64, false);
                Select(standing, null); OpenInspector(); BeginMove();
                break;
            case RemodelAction.Move:
                housing.Run(() =>
                {
                    var copy = housing.DetachShellPiece(id);
                    remodelSelected = 0;
                    remodelMovedFrom = housing.ActiveHouse?.Furnishings.FirstOrDefault(x => x.Id == copy);
                    remodelMoving = (id, copy, Environment.TickCount64, false);
                    Select(copy, null); OpenInspector(); BeginMove();
                });
                break;
        }
    }
}
