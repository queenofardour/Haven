namespace Haven.Core;

public static class HousingAssetPolicy
{
    // Fixtures also live under bgcommon/hou. Only general-item trees are furniture.
    public static bool IsPlacedFurniture(string? path) => path != null &&
        (path.StartsWith("bgcommon/hou/indoor/general/", StringComparison.Ordinal)
         || path.StartsWith("bgcommon/hou/outdoor/general/", StringComparison.Ordinal));
    public static bool IsPlacedFurniture(ReadOnlySpan<byte> path) => path.StartsWith("bgcommon/hou/indoor/general/"u8)
        || path.StartsWith("bgcommon/hou/outdoor/general/"u8);
}
