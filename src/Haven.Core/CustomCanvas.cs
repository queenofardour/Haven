using System.Numerics;

namespace Haven.Core;

// Audited apartment donor; the protected platform is generated, never serialized.
public static class CustomCanvas
{
    public const uint TerritoryId = 608;
    public const uint PlatformItemId = 24511; // Wooden Loft, model 0763.
    public static readonly Guid PlatformId = Guid.Parse("a928ff15-4e4d-4865-b3c6-f562661119f0");
    public static Vector3 Spawn => new(0, .15f, 0);
    /// <summary>
    /// Where the canvas's Exit stands: on the starter platform (X ±4, Z ±4, top Y 0), towards the side the donor
    /// apartment's door was (its markers stand at about (0, 0, 7.4), out of reach from the platform).
    /// </summary>
    public static Vector3 Exit => new(0, 0, 3.2f);
    public static Furnishing Platform { get; } = new()
        { Id = PlatformId, ItemId = PlatformItemId, Position = [0, -.5f, -4], Scale = 2, Collision = false };
    public static bool NeedsRecovery(Vector3 position) => !float.IsFinite(position.X) || !float.IsFinite(position.Y)
        || !float.IsFinite(position.Z) || position.Y < -8 || Math.Abs(position.X) > 256 || Math.Abs(position.Z) > 256;

    public static void Validate(House house)
    {
        if (!house.CustomLayout) return;
        if (house.TerritoryId != TerritoryId || house.Transform.Position != Vector3.Zero
            || house.Transform.Orientation != Quaternion.Identity || house.Transform.Scale != 1
            || house.Furnishings.Any(x => x.Id == PlatformId)
            || house.Interior.MainHallCeilingLight != 0
            || house.Interior.Floors.Any(x => x.Wallpaper != 0 || x.Flooring != 0 || x.CeilingLight != 0))
            throw new InvalidDataException("This custom-layout prototype requires its original blank-canvas context and protected platform; shell finishes are unavailable.");
    }
}

public sealed class CanvasArrival
{
    public bool Ready { get; private set; }
    public bool Update(bool platformAndSceneReady, Vector3? player)
    {
        var ready = platformAndSceneReady && player != null;
        var first = ready && !Ready; Ready = ready;
        return ready && (first || CustomCanvas.NeedsRecovery(player!.Value));
    }
    public void Clear() => Ready = false;
}
