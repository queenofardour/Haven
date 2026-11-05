using System.Numerics;

namespace Haven.Core;

public static class EditorAccess
{
    // Becoming the relay host during a visit never grants ownership of that home.
    public static bool CanEditHome(bool ready, bool enteredOwnHome, bool hasAuthority, bool publishedVisit = false)
        => ready && enteredOwnHome && hasAuthority && !publishedVisit;
}

public readonly record struct FurnitureAnchor(Guid FurnitureId, int Marker, Vector3 Position, bool Bed);
public static class FurnitureInteractions
{
    public const ushort Sit = 50, Doze = 13, Sleep = 88;
    public static bool? WantsBed(ushort emote) => emote switch { Sit => false, Doze or Sleep => true, _ => null };
    public static FurnitureAnchor? Nearest(IEnumerable<FurnitureAnchor> anchors, Vector3 player, bool bed, float range = 3, float vertical = 1.5f)
    {
        FurnitureAnchor? nearest = null; var distance = range * range;
        foreach (var anchor in anchors)
        {
            if (anchor.Bed != bed || Math.Abs(anchor.Position.Y - player.Y) > vertical) continue;
            var d = Vector3.DistanceSquared(anchor.Position, player);
            if (!float.IsFinite(d) || d > distance) continue;
            nearest = anchor; distance = d;
        }
        return nearest;
    }
}
