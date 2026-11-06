using System.Text.RegularExpressions;

namespace Haven.Core;

public static partial class BuildingParts
{
    public const int MaxPathLength = 240;

    // File paths are identifiers, never file payloads. The plugin additionally checks membership in the catalogue derived
    // from the installed residential layouts and inn rooms, and for world pieces in the world index its own PC builds
    // from its own game data.
    // A world effect or ambient sound is a building piece too: placed, moved and shared like one,
    // drawn by Haven's own effects and sounds. Accepting more paths keeps every older home valid.
    public static bool IsHousingAsset(string path) => path.Length <= MaxPathLength && (HousingAsset().IsMatch(path) || InnAsset().IsMatch(path) || WorldAsset().IsMatch(path)
        || World.WorldAmbient.IsEffectPath(path) || World.WorldAmbient.IsSoundPath(path));

    /// <summary>Whether a building piece is a world effect or ambient sound rather than something with a model.</summary>
    public static bool IsAmbient(string path) => path.EndsWith(".avfx", StringComparison.Ordinal) ? World.WorldAmbient.IsEffectPath(path)
        : path.EndsWith(".scd", StringComparison.Ordinal) && World.WorldAmbient.IsSoundPath(path);

    /// <summary>Whether a building piece may carry a sound volume: an ambient sound, or a world piece (a set's own sounds).</summary>
    public static bool TakesSoundVolume(string path) => World.WorldAmbient.IsSoundPath(path) || IsWorldAsset(path);

    /// <summary>
    /// Whether a path names a piece of the game's own world (housing protocol 40): any model a zone places, or a zone's
    /// shared group. Inn pieces are world pieces too, so a path both names is still a world path.
    /// </summary>
    public static bool IsWorldAsset(string path) => path.Length <= MaxPathLength && WorldAsset().IsMatch(path);

    [GeneratedRegex(@"\A(?:bg/ffxiv/[a-z0-9_]+/(?:ind/common/(?:bgparts/[a-z0-9_]+\.mdl)|shared/for_hou/(?:[a-z0-9_]+/)*[a-z0-9_]+\.sgb)|bgcommon/hou/dyna/(?:[a-z0-9_]+/)+(?:[a-z0-9_]+\.sgb)|bgcommon/hou/indoor/general/[0-9]{4}/asset/fun_b0_m[0-9]{4}\.sgb)\z", RegexOptions.CultureInvariant)]
    private static partial Regex HousingAsset();

    // An inn room's own pieces, moved in a Haven home (housing protocol 37): a city's models (bg/<expansion>/<zone>/
    // common, twn/<area> or evt/<area> bgparts) and groups (shared/for_bg), the game's common item models
    // (bgcommon/world/itm) and groups (bgcommon/world/sys and evt shared groups: The Unending Journey's book).
    [GeneratedRegex(@"\A(?:bg/(?:ffxiv|ex[1-9])/[a-z0-9_]+/(?:(?:common|(?:twn|evt)/[a-z0-9_]+)/bgparts/[a-z0-9_]+\.mdl|shared/for_bg/[a-z0-9_]+\.sgb)|bgcommon/world/(?:itm/[0-9]{3}/bgparts/[a-z0-9_]+\.mdl|(?:sys|evt)/shared/for_(?:bg|pln)/[a-z0-9_]+\.sgb))\z", RegexOptions.CultureInvariant)]
    private static partial Regex InnAsset();

    // The game's world (housing protocol 40): a zone's models (bg/<expansion>/…/bgparts, a few folders deep: a region's
    // common parts, a town's, a field's, a dungeon's), its shared groups (…/shared/for_bg) and the common models every
    // zone draws from (bgcommon/…/bgparts). Each folder is one lowercase name, so no path climbs out (no dots) or names
    // anything but a model or a group.
    [GeneratedRegex(@"\A(?:bg/(?:ffxiv|ex[1-9])/(?:[a-z0-9_]+/){1,6}(?:bgparts/[a-z0-9_]+\.mdl|shared/for_bg/[a-z0-9_]+\.sgb)|bgcommon/(?:[a-z0-9_]+/){1,6}bgparts/[a-z0-9_]+\.mdl)\z", RegexOptions.CultureInvariant)]
    private static partial Regex WorldAsset();
}
