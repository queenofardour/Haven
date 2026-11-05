using System.IO.Compression;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Haven;

/// <summary>
/// An unlisted, read-only diagnostic for modded NPC looks: which files Penumbra
/// draws a character with on this PC, and where they live. For a friend whose sync plugin (Lightless, PlayerSync,
/// Snowcloak) dresses them here, their modded files should resolve into that plugin's download cache, so Haven could
/// copy an NPC's files from there while the owner wears the NPC's look. Also reads the character's meta changes
/// (Penumbra's manipulation string). Nothing is changed. /haven syncprobe [name]: the named player, else your target,
/// else yourself.
/// </summary>
internal sealed class SyncProbe(IDalamudPluginInterface plugin, IObjectTable objects, IChatGui chat, IPluginLog log)
{
    public void Run(string name)
    {
        var target = name.Length > 0
            ? objects.FirstOrDefault(o => o.ObjectKind == ObjectKind.Pc && o.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase))
            : objects.LocalPlayer?.TargetObject is { ObjectKind: ObjectKind.Pc } targeted ? targeted : objects.LocalPlayer;
        if (target == null) { chat.Print($"[Haven] syncprobe: no player called \"{name}\" here."); return; }
        var index = target.ObjectIndex;
        Dictionary<string, HashSet<string>>? paths;
        string meta;
        try
        {
            paths = plugin.GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5").InvokeFunc([index])[0];
            meta = plugin.GetIpcSubscriber<int, string>("Penumbra.GetMetaManipulations.V5").InvokeFunc(index);
        }
        catch (Exception ex) { chat.Print($"[Haven] syncprobe: Penumbra didn't answer ({ex.Message})."); return; }
        if (paths == null) { chat.Print($"[Haven] syncprobe: Penumbra has no resources for {target.Name} (object {index})."); return; }

        // Modded files are disk paths; vanilla and swapped ones are game paths.
        var onDisk = paths.Where(p => Path.IsPathRooted(p.Key)).ToList();
        var missing = onDisk.Count(p => !File.Exists(p.Key));
        var bytes = onDisk.Where(p => File.Exists(p.Key)).Sum(p => new FileInfo(p.Key).Length);
        var groups = onDisk.GroupBy(p => Root(p.Key)).OrderByDescending(g => g.Count()).ToList();
        var (metaCount, metaNote) = Meta(meta);
        chat.Print($"[Haven] syncprobe {target.Name} (object {index}): {paths.Count} files drawn, {onDisk.Count} from mods ({bytes / 1048576.0:F1} MB{(missing > 0 ? $", {missing} missing on disk" : "")}), {paths.Count - onDisk.Count} from the game; meta: {metaNote}.");
        foreach (var group in groups.Take(6)) chat.Print($"[Haven]   {group.Count()} from {group.Key}");
        log.Information("[SYNC-PROBE] {Name} (object {Index}): {Total} files, {Disk} on disk, {Missing} missing, {Bytes} bytes, meta {Meta} ({Count}).",
            target.Name, index, paths.Count, onDisk.Count, missing, bytes, metaNote, metaCount);
        foreach (var group in groups) log.Information("[SYNC-PROBE]   {Count} from {Root}", group.Count(), group.Key);
        foreach (var (actual, games) in onDisk.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            log.Information("[SYNC-PROBE]   {Game} <- {Actual}", string.Join(", ", games), actual);
    }

    // Where a file lives: the drive and first folder, or two folders for Penumbra's own mod folders (one per mod).
    private static string Root(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? path : string.Join('\\', parts.Take(parts.Length > 3 && parts[1].Contains("enumbra", StringComparison.OrdinalIgnoreCase) ? 3 : 2));
    }

    // Penumbra's manipulation string: a version byte and gzipped JSON, base64; counted roughly by its entries.
    private static (int Count, string Note) Meta(string meta)
    {
        if (string.IsNullOrEmpty(meta)) return (0, "none");
        try
        {
            var raw = Convert.FromBase64String(meta);
            // The version byte sits before the compressed data (as in Glamourer's states) or as its first byte.
            string Read(int skip)
            {
                using var zip = new GZipStream(new MemoryStream(raw, skip, raw.Length - skip), CompressionMode.Decompress);
                using var reader = new StreamReader(zip, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            string json;
            try { json = Read(1); } catch (InvalidDataException) { json = Read(0)[1..]; }
            var count = System.Text.RegularExpressions.Regex.Matches(json, "\"Type\"").Count;
            return (count, $"version {raw[0]}, {json.Length} characters, about {count} entries");
        }
        catch (Exception ex) { return (0, $"{meta.Length} characters, not read ({ex.GetType().Name})"); }
    }
}
