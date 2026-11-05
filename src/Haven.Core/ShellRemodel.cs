using System.Numerics;

namespace Haven.Core;

/// <summary>
/// Remodelling a preset home's shell: which of its pieces are hidden, how a piece becomes a
/// movable copy, and when someone who fell through a removed floor is brought back.
/// </summary>
public static class ShellRemodel
{
    public const int MaxHidden = 1024;
    /// <summary>How far below the shell's lowest floor, or how far out, a player is brought back to where they last stood.</summary>
    public const float FallMargin = 8, FarOut = 256;
    /// <summary>
    /// The EObj rows of a preset shell's "Exit" markers (houses' LVD_entrance_01/02, apartments' LVD_gimmick_01/02 in
    /// planmap.lgb). The "entrance to additional chambers" markers use other rows.
    /// </summary>
    public static readonly IReadOnlySet<uint> ExitMarkers = new HashSet<uint> { 2002738, 2002740, 2004361, 2007444,
        // The inn rooms' doors (planmap.lgb LVD_gimmick_01): Mizzenmast, the Hourglass, the Roost, Cloud Nine, Bokairo, the
        // Pendants, the Andron, the For'ard Cabins.
        2001010, 2001011, 2000087, 2005628, 2007707, 2009759, 2011853, 2014367 };
    /// <summary>
    /// The inn rooms' other event objects (planevent/planmap.lgb): beds, armoires, summoning and crystal bells, the
    /// orchestrion, the glamour dresser, the toy chest and The Unending Journey. They answer only the server, so a Haven
    /// home hides them; their furniture stays, to remodel.
    /// </summary>
    public static readonly IReadOnlySet<uint> InnFixtures = new HashSet<uint>
    {
        2001012, 2001270, 2000088, 2005629, 2007708, 2010433,     // beds
        2001405, 2001406, 2001407, 2005630, 2007709,              // armoires
        2000403, 2010284, 2004358,                                // summoning bells, the crystal bell
        2006999, 2009439, 2004359,                                // orchestrion, glamour dresser, toy chest
        2001901, 2009274, 2010156,                                // The Unending Journey
    };
    /// <summary>The "entrance to additional chambers" markers (EObjName): a company's, never part of a Haven home.</summary>
    public static readonly IReadOnlySet<uint> ChamberEntrances = new HashSet<uint> { 2004353, 2004624, 2004625, 2004626, 2008126, 2011571 };

    /// <summary>
    /// The interior with a piece hidden (or its collision setting changed); movedTo names the building piece standing in
    /// for it, and a piece already moved keeps its own.
    /// </summary>
    public static InteriorOptions Hide(InteriorOptions interior, uint instance, bool keepCollision, Guid? movedTo = null)
    {
        var pieces = interior.HiddenPieces ?? [];
        var was = pieces.FirstOrDefault(x => x.Instance == instance);
        var rest = pieces.Where(x => x.Instance != instance);
        return interior with { HiddenPieces = [.. rest, new ShellPiece { Instance = instance, KeepCollision = keepCollision, MovedTo = movedTo ?? was?.MovedTo }] };
    }

    /// <summary>The building pieces standing in for these moved pieces (null: every piece), which go when they're put back.</summary>
    public static IReadOnlySet<Guid> MovedCopies(InteriorOptions interior, IReadOnlyCollection<uint>? instances) =>
        (interior.HiddenPieces ?? []).Where(x => x.MovedTo is not null && (instances == null || instances.Contains(x.Instance))).Select(x => x.MovedTo!.Value).ToHashSet();

    /// <summary>The interior with pieces shown again; null removes every one.</summary>
    public static InteriorOptions Restore(InteriorOptions interior, IReadOnlyCollection<uint>? instances)
    {
        var kept = instances == null ? [] : (interior.HiddenPieces ?? []).Where(x => !instances.Contains(x.Instance)).ToArray();
        return interior with { HiddenPieces = kept.Length == 0 ? null : kept };
    }

    public static bool IsHidden(InteriorOptions interior, uint instance) => interior.HiddenPieces?.Any(x => x.Instance == instance) == true;

    /// <summary>
    /// A furnishing transform that draws a building piece exactly where a shell instance stands: Haven's building pieces
    /// sit on the middle of their bounds' bottom (BuildingCatalogue.Normalize), so the copy's position is that point of
    /// the instance. Null when the instance is tilted or unevenly scaled, which a furnishing can't express.
    /// </summary>
    public static (Vector3 Position, float Yaw, float Scale)? Copy(Matrix4x4 instance, Vector3 boundsMinimum, Vector3 boundsMaximum)
    {
        if (!Matrix4x4.Decompose(instance, out var scale, out var rotation, out var translation)) return null;
        if (Math.Abs(scale.X - scale.Y) > .001f * Math.Abs(scale.X) || Math.Abs(scale.X - scale.Z) > .001f * Math.Abs(scale.X) || scale.X <= 0) return null;
        var up = Vector3.Transform(Vector3.UnitY, rotation);
        if (Vector3.Dot(up, Vector3.UnitY) < .999f) return null;
        var side = Vector3.Transform(Vector3.UnitX, rotation);
        // Furnishing.ItemMatrix turns by CreateRotationY, which carries +X to (cos, 0, -sin).
        var foot = new Vector3((boundsMinimum.X + boundsMaximum.X) / 2, boundsMinimum.Y, (boundsMinimum.Z + boundsMaximum.Z) / 2);
        return (Vector3.Transform(foot, instance), HomeLayouts.Degrees(MathF.Atan2(-side.Z, side.X)), scale.X);
    }

    /// <summary>Whether a player has fallen well below the shell's lowest floor or wandered far off.</summary>
    public static bool NeedsRecovery(Vector3 player, float lowestFloor) => !float.IsFinite(player.X) || !float.IsFinite(player.Y) || !float.IsFinite(player.Z)
        || player.Y < lowestFloor - FallMargin || Math.Abs(player.X) > FarOut || Math.Abs(player.Z) > FarOut;
}
