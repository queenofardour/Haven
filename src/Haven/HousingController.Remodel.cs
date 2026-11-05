using System.Numerics;
using Haven.Core;

namespace Haven;

// Remodelling a preset home's shell: its own pillars, walls, floors, stairs, windows, doors and
// lights can be removed (hidden on every PC, kept in Interior.HiddenPieces) or turned into a building piece of the
// same model where they stood, which then moves, turns, scales and undoes like any furnishing. Custom layouts have no
// shell to remodel.
internal sealed partial class HousingController
{
    /// <summary>The active home's shell pieces (empty for a custom layout or before the building catalogue is read).</summary>
    public IReadOnlyList<ShellPieceInfo> ShellPieces => active == null || active.CustomLayout ? [] : Catalogue.ShellPieces(active.TerritoryId);
    public bool CanRemodel => CanFurnish && active is { CustomLayout: false };
    public bool IsShellPieceHidden(uint instance) => active != null && ShellRemodel.IsHidden(active.Interior, instance);
    public bool KeepsShellCollision(uint instance) => active?.Interior.HiddenPieces?.FirstOrDefault(x => x.Instance == instance)?.KeepCollision == true;
    /// <summary>Whether a removed piece was moved: a building piece stands in for it somewhere (until that's deleted).</summary>
    public bool IsShellPieceMoved(uint instance) => MovedShellCopy(instance) != null;
    /// <summary>The building piece standing in for a moved shell piece, if it still exists.</summary>
    public Guid? MovedShellCopy(uint instance) => active?.Interior.HiddenPieces?.FirstOrDefault(x => x.Instance == instance)?.MovedTo is Guid copy
        && active.Furnishings.Any(x => x.Id == copy) ? copy : null;
    /// <summary>The moved shell pieces' stand-ins: handled in Remodel, so Furnish leaves them out.</summary>
    public IReadOnlySet<Guid> MovedShellCopies => active == null ? new HashSet<Guid>() : ShellRemodel.MovedCopies(active.Interior, null);

    /// <summary>Removes a shell piece; keepCollision leaves it solid (an invisible floor still carries everyone).</summary>
    public void HideShellPiece(uint instance, bool keepCollision)
    {
        RequireRemodel();
        var piece = ShellPieces.FirstOrDefault(x => x.Instance == instance) ?? throw new InvalidOperationException("That piece isn't part of this home's shell.");
        Commit(active! with { Interior = ShellRemodel.Hide(active!.Interior, instance, keepCollision) }, visual: false);
        Status = keepCollision ? $"{piece.Label} removed; it's still solid underfoot." : $"{piece.Label} removed.";
    }

    /// <summary>Removes several shell pieces in one step (a wall and the pillars along it).</summary>
    public void HideShellPieces(IReadOnlyCollection<uint> instances, bool keepCollision)
    {
        RequireRemodel();
        if (instances.Count == 1) { HideShellPiece(instances.First(), keepCollision); return; }
        var known = ShellPieces.Select(x => x.Instance).ToHashSet();
        var interior = active!.Interior;
        foreach (var instance in instances.Where(known.Contains).Distinct()) interior = ShellRemodel.Hide(interior, instance, keepCollision);
        Commit(active! with { Interior = interior }, visual: false);
        Status = $"{instances.Count} pieces removed.";
    }

    /// <summary>Puts removed pieces back (null: every one); a moved piece's stand-in building piece goes with it.</summary>
    public void RestoreShellPieces(IReadOnlyCollection<uint>? instances)
    {
        RequireRemodel();
        var copies = ShellRemodel.MovedCopies(active!.Interior, instances);
        var furnishings = copies.Count == 0 ? active.Furnishings : [.. active.Furnishings.Where(x => !copies.Contains(x.Id))];
        Commit(active with { Interior = ShellRemodel.Restore(active.Interior, instances), Furnishings = furnishings }, visual: furnishings.Length != active.Furnishings.Length);
        Status = instances == null ? "Every removed piece is back." : instances.Count == 1 ? "Piece put back." : $"{instances.Count} pieces put back.";
    }

    /// <summary>
    /// Removes a shell piece and places a building piece of the same model exactly where it stood, so it can be moved;
    /// returns the new furnishing. Refused for a piece the game tilts or stretches, which a furnishing can't repeat.
    /// </summary>
    public Guid DetachShellPiece(uint instance)
    {
        RequireRemodel();
        var piece = ShellPieces.FirstOrDefault(x => x.Instance == instance) ?? throw new InvalidOperationException("That piece isn't part of this home's shell.");
        if (ShellRemodel.Copy(piece.World, piece.Minimum, piece.Maximum) is not { } copy) throw new InvalidOperationException("This piece is tilted or stretched in the game's layout, so it can only be removed, not moved.");
        var position = Matrix4x4.Invert(FurnitureCatalogue.HouseMatrix(active!), out var toHouse) ? Vector3.Transform(copy.Position, toHouse) : copy.Position;
        var item = new Furnishing { Id = Guid.NewGuid(), BuildingPart = piece.Path, Position = [position.X, position.Y, position.Z], Rotation = copy.Yaw, Scale = copy.Scale, Collision = piece.Collision };
        // Linked, so putting the piece back later removes this copy instead of leaving a second one.
        Commit(active! with { Interior = ShellRemodel.Hide(active!.Interior, instance, false, item.Id), Furnishings = [.. active!.Furnishings, item] });
        Status = $"{piece.Label} is now a building piece you can move.";
        return item.Id;
    }

    // Once per shell: how many pieces Haven found, for the log (the first in-game check of remodelling).
    private uint shellCounted;
    public void NoteShellPieces(int count)
    {
        if (active is not { } home || shellCounted == home.TerritoryId || count == 0) return;
        shellCounted = home.TerritoryId;
        log.Information($"[REMODEL] {count} shell pieces in {ShellName(home.TerritoryId)}.");
    }

    private void RequireRemodel()
    {
        if (!CanRemodel) throw new InvalidOperationException(active?.CustomLayout == true ? "Custom layouts have no shell to remodel." : "You need decorating access in a ready home to remodel it.");
    }
}
