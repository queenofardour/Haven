using Haven.Core.Animation;
using Haven.Core.Furniture;

namespace Haven.Core;

/// <summary>
/// The game files a shared package (custom furniture, a minion prop, pose animations and their effects) points at
/// without carrying them: a mod keeps the game's own textures, effects or sounds where it changed nothing. A free trial
/// has no Endwalker or Dawntrail data, and a game asked for a file it doesn't have closes (c0500, GameDataPaths). So
/// every PC, before a package it received is used, checks that those of its paths that live in an expansion's data are
/// installed there; a package that needs one the PC lacks is left out on that PC.
/// </summary>
public static class PackageNeeds
{
    /// <summary>The paths one of a package's files names, by its kind (its name's extension); none for a kind that names none.</summary>
    public static IEnumerable<string> References(string name, byte[] bytes)
    {
        try
        {
            IEnumerable<string> found = Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".mdl" => FurnitureFiles.ModelMaterials(bytes),
                ".mtrl" => FurnitureFiles.MaterialTextures(bytes),
                ".avfx" => AvfxFiles.Textures(bytes),
                ".pap" => PapFiles.TimelineStrings(bytes),
                ".tmb" => PapFiles.TimelineEntries(bytes).Select(e => e.Value),
                _ => [],
            };
            return [.. found.Select(p => p.Replace('\\', '/').ToLowerInvariant())];
        }
        // A file that doesn't read was refused when the package was opened; here it names nothing.
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException) { return []; }
    }

    /// <summary>
    /// The first path the files name that lives in an expansion's data and isn't installed (<paramref name="installed"/>:
    /// IDataManager.FileExists in the plugin), with its expansion; null when everything they name is there or is the
    /// base game's (always installed) or Haven's own.
    /// </summary>
    public static (string Path, int Expansion)? Missing(IEnumerable<(string Name, byte[] Bytes)> files, Func<string, bool> installed)
    {
        var asked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, bytes) in files)
            foreach (var path in References(name, bytes))
                if (GameDataPaths.Expansion(path) is > 0 and var expansion && asked.Add(path) && !installed(path)) return (path, expansion);
        return null;
    }

    /// <summary>What the player is told about a package left out because their game lacks data it needs.</summary>
    public static string Message(string what, int expansion) =>
        $"{what} needs {GameDataPaths.ExpansionName(expansion)}'s game data, which this game doesn't have (a free trial, or an account without {GameDataPaths.ExpansionName(expansion)}). It's left out here so the game doesn't close.";
}
