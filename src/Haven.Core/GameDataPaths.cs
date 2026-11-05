namespace Haven.Core;

/// <summary>
/// Which expansion's data a game path belongs to. A game install holds only the expansions its account may play: a
/// free trial (through Shadowbringers since patch 7.5) has no Endwalker or Dawntrail data at all, and asking its game
/// for such a file closes it with c0500 "Could not locate data files" (a friend's trial, 1-2 November 2026: Dawntrail
/// music in the Crystal Odyssey, then The For'ard Cabins as a Haven home). The second path segment names it:
/// bg/ex5/…, music/ex4/…; everything else (bgcommon, chara, music/ffxiv, the orchestrion rolls) is the base game's.
/// </summary>
public static class GameDataPaths
{
    public static readonly IReadOnlyList<string> Expansions = ["A Realm Reborn", "Heavensward", "Stormblood", "Shadowbringers", "Endwalker", "Dawntrail"];

    /// <summary>0 for the base game, N for the exN folder a path's data lives in.</summary>
    public static int Expansion(string path)
    {
        var start = path.IndexOf('/');
        if (start < 0 || path.Length < start + 5 || path[start + 4] != '/' || path[start + 1] != 'e' || path[start + 2] != 'x' || !char.IsAsciiDigit(path[start + 3]))
            return 0;
        return path[start + 3] - '0';
    }

    public static string ExpansionName(int expansion) => expansion >= 0 && expansion < Expansions.Count ? Expansions[expansion] : $"expansion {expansion}";

    /// <summary>
    /// The newest expansion among the paths this install lacks, or null when it has them all. Each path is looked up
    /// once, through <paramref name="installed"/> (the game's own data index; IDataManager.FileExists in the plugin).
    /// </summary>
    public static int? Needs(IEnumerable<string> paths, Func<string, bool> installed)
    {
        int? needs = null;
        foreach (var path in paths)
            if (!installed(path)) needs = Math.Max(needs ?? 0, Expansion(path));
        return needs;
    }

    /// <summary>What the player is told when a place or a piece needs data their game doesn't have.</summary>
    public static string Missing(string what, int expansion) => expansion > 0
        ? $"{what} needs {ExpansionName(expansion)}'s game data, which this game doesn't have (a free trial, or an account without {ExpansionName(expansion)}). Entering would close the game."
        : $"{what} needs game data this game doesn't have. Entering would close the game.";

    /// <summary>
    /// A guest who leaves because their game lacks the home's zone says which expansion (LeaveRoom's payload: one byte,
    /// the expansion's number); the relay passes it to the others in PeerLeft, so the host learns why. Empty otherwise.
    /// </summary>
    public static byte[] LeaveReason(int expansion) => expansion is > 0 and < 16 ? [(byte)expansion] : [];
    /// <summary>The expansion a LeaveRoom payload names, or 0 (none, or anything else).</summary>
    public static int LeaveExpansion(ReadOnlySpan<byte> payload) => payload is [var expansion and > 0 and < 16] ? expansion : 0;
    /// <summary>What the host is told when a guest's game can't load the home.</summary>
    public static string GuestMissing(string guest, int expansion) =>
        $"{(guest.Length > 0 ? guest : "Someone")} tried to join your gathering, but this home needs {ExpansionName(expansion)}, which their game doesn't have (a free trial, or an account without {ExpansionName(expansion)}).";

    /// <summary>The file a territory's zone loads first: its level's .lvb, from TerritoryType.Bg.</summary>
    public static string TerritoryFile(string bg) => $"bg/{bg}.lvb";
}
