using System.IO.Compression;
using System.Text.Json;

namespace Haven.Core.Furniture;

/// <summary>What a mod puts at one game path: one of its own files, or another game file (a swap).</summary>
public sealed record ModFile(string? Entry, string? SwapTo);

/// <summary>One option of a mod group: its name and description, its priority, and the paths it changes.</summary>
public sealed record ModOption(string Name, string Description, int Priority, IReadOnlyDictionary<string, ModFile> Files)
{
    /// <summary>The option's meta manipulations, each as its JSON object (Type and Manipulation).</summary>
    public IReadOnlyList<string> Meta { get; init; } = [];
}
/// <summary>
/// A mod group: Single (one option, an alternative such as a style) or Multi (any number, add-ons), its priority,
/// and the options its own default settings pick (an index for Single, a bit mask for Multi).
/// </summary>
public sealed record ModGroup(string Name, bool Single, int Priority, ulong DefaultSettings, IReadOnlyList<ModOption> Options);

/// <summary>
/// Which options apply, by group index: for a Single group the option's index, for a Multi group a bit mask. A group
/// not named uses Haven's default: a Single group's own default option, every option of a Multi group.
/// </summary>
public sealed record ModSelection(IReadOnlyDictionary<int, ulong> Groups)
{
    public static readonly ModSelection Default = new(new Dictionary<int, ulong>());
}

/// <summary>
/// A Penumbra mod: its name, its groups and options, and every game path it redirects
/// with the options selected (<see cref="Select"/>). Read from a mod folder or a .pmp (the same folder zipped), following Penumbra's own format
/// (Penumbra/Mods/SubMods/SubMod.cs, Mods/Groups): meta.json, default_mod.json with Files and FileSwaps, and
/// group_NNN_*.json files (or Groups in meta.json) whose Single groups pick the option at DefaultSettings and Multi
/// groups every option, higher Priority winning. Meta manipulations are kept as their JSON (<see cref="Meta"/>): furniture
/// doesn't use them, but an animation's prop (PoseProps) needs its Eqdp entries to show on every body. Mod files are read
/// only through <see cref="Read"/>, which keeps every path inside the mod and caps sizes.
/// </summary>
public sealed class PenumbraMod : IDisposable
{
    public const long MaxFileBytes = 64L * 1024 * 1024;
    private const int MaxJsonBytes = 4 * 1024 * 1024;
    private readonly ZipArchive? zip;
    private readonly string? folder;
    private readonly Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; private set; } = "";
    public string Author { get; private set; } = "";
    /// <summary>Game path (lower case, forward slashes) → what the mod puts there, with the selected options.</summary>
    public IReadOnlyDictionary<string, ModFile> Files => files;
    private Dictionary<string, ModFile> files = new(StringComparer.Ordinal);
    /// <summary>The meta manipulations with the selected options, each as its JSON object (Type and Manipulation).</summary>
    public IReadOnlyList<string> Meta => meta;
    private List<string> meta = [];
    private readonly List<string> defaultMeta = [];
    /// <summary>The mod's groups, in the order they apply (higher priority last).</summary>
    public IReadOnlyList<ModGroup> Groups => groups;
    private readonly List<ModGroup> groups = [];
    private readonly Dictionary<string, ModFile> defaults = new(StringComparer.Ordinal);

    private PenumbraMod(ZipArchive? zip, string? folder)
    {
        this.zip = zip; this.folder = folder;
        if (zip != null) foreach (var entry in zip.Entries) entries[Normal(entry.FullName)] = entry;
    }

    /// <summary>Opens a Penumbra mod folder, or a .pmp file.</summary>
    public static PenumbraMod Open(string path)
    {
        PenumbraMod mod;
        if (Directory.Exists(path)) mod = new(null, Path.GetFullPath(path));
        else
        {
            var stream = File.OpenRead(path);
            try { mod = new(new ZipArchive(stream, ZipArchiveMode.Read), null); }
            catch (InvalidDataException) { stream.Dispose(); throw new InvalidDataException("That file is not a Penumbra mod (.pmp)."); }
        }
        try { mod.Load(); return mod; }
        catch { mod.Dispose(); throw; }
    }

    /// <summary>Opens a .pmp held in memory (tests, or a download).</summary>
    public static PenumbraMod Open(byte[] pmp)
    {
        var mod = new PenumbraMod(new ZipArchive(new MemoryStream(pmp), ZipArchiveMode.Read), null);
        try { mod.Load(); return mod; }
        catch { mod.Dispose(); throw; }
    }

    private void Load()
    {
        var found = new List<(int Priority, string Name, JsonDocument Json)>();
        using var meta = Json("meta.json");
        try
        {
            if (meta != null)
            {
                Name = Text(meta.RootElement, "Name");
                Author = Text(meta.RootElement, "Author");
            }
            // Newer Penumbra (FileVersion 4, seen in a mod installed on 28 October 2026) keeps the default files in
            // meta.json's DefaultData instead of default_mod.json; groups may sit there too, as an array.
            using (var defaultFile = Json("default_mod.json"))
                if (defaultFile != null) Apply(defaultFile.RootElement, defaults, defaultMeta);
                else if (meta?.RootElement.TryGetProperty("DefaultData", out var data) == true && data.ValueKind == JsonValueKind.Object) Apply(data, defaults, defaultMeta);
            if (meta?.RootElement.TryGetProperty("Groups", out var inMeta) == true && inMeta.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var group in inMeta.EnumerateArray())
                    if (group.ValueKind == JsonValueKind.Object) found.Add((Number(group, "Priority"), $"meta_{index++:D3}", JsonDocument.Parse(group.GetRawText())));
            }
            foreach (var name in Names().Where(n => n.StartsWith("group_", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !n.Contains('/')).Order(StringComparer.OrdinalIgnoreCase))
                if (Json(name) is { } group) found.Add((Number(group.RootElement, "Priority"), name, group));
            // Higher priority wins, so it is applied last.
            foreach (var (priority, _, group) in found.OrderBy(g => g.Priority).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                var root = group.RootElement;
                if (!root.TryGetProperty("Options", out var options) || options.ValueKind != JsonValueKind.Array) continue;
                var type = Text(root, "Type");
                if (type is not ("Single" or "Multi")) continue;
                var setting = root.TryGetProperty("DefaultSettings", out var d) && d.TryGetUInt64(out var v) ? v : 0;
                var parsed = options.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object).Select(option =>
                {
                    var changes = new Dictionary<string, ModFile>(StringComparer.Ordinal);
                    var manipulations = new List<string>();
                    Apply(option, changes, manipulations);
                    return new ModOption(SafeText.Clean(Text(option, "Name"), 60).Trim(), SafeText.Clean(Text(option, "Description"), 200).Trim(), Number(option, "Priority"), changes) { Meta = manipulations };
                }).ToArray();
                groups.Add(new(SafeText.Clean(Text(root, "Name"), 60).Trim(), type == "Single", priority, setting, parsed));
            }
        }
        finally { foreach (var group in found) group.Json.Dispose(); }
        Select(ModSelection.Default);
    }

    /// <summary>
    /// Applies a selection of options over the default files. Haven's default: a Single group's own default option
    /// (an alternative, such as a style), and every option of a Multi group (add-ons, which players switch on in their
    /// collection; a mod's own default is often none, as in Basic Urinal), higher option priority winning on a shared
    /// path. Each imported furnishing takes only the files its own models draw with, so add-ons for other items don't
    /// leak in.
    /// </summary>
    public void Select(ModSelection selection)
    {
        var next = new Dictionary<string, ModFile>(defaults, StringComparer.Ordinal);
        var nextMeta = new List<string>(defaultMeta);
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var chosen = group.Options.Select((option, index) => (option, index))
                .Where(o => selection.Groups.TryGetValue(g, out var picked)
                    ? group.Single ? (ulong)o.index == picked : o.index < 64 && (picked >> o.index & 1) != 0
                    : group.Single ? (ulong)o.index == group.DefaultSettings : true)
                .OrderBy(o => o.option.Priority).ThenBy(o => o.index);
            foreach (var (option, _) in chosen) { foreach (var (path, file) in option.Files) next[path] = file; nextMeta.AddRange(option.Meta); }
        }
        files = next; meta = nextMeta;
    }

    /// <summary>Every option of every group applied at once: to find every furnishing any option changes.</summary>
    public void SelectAll()
    {
        var next = new Dictionary<string, ModFile>(defaults, StringComparer.Ordinal);
        var nextMeta = new List<string>(defaultMeta);
        foreach (var group in groups)
            foreach (var option in group.Options.OrderBy(o => o.Priority)) { foreach (var (path, file) in option.Files) next[path] = file; nextMeta.AddRange(option.Meta); }
        files = next; meta = nextMeta;
    }

    private static void Apply(JsonElement container, Dictionary<string, ModFile> into, List<string> manipulations)
    {
        if (container.TryGetProperty("Manipulations", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var manipulation in list.EnumerateArray())
                if (manipulation.ValueKind == JsonValueKind.Object && manipulations.Count < 4096) manipulations.Add(manipulation.GetRawText());
        if (container.TryGetProperty("Files", out var redirections) && redirections.ValueKind == JsonValueKind.Object)
            foreach (var file in redirections.EnumerateObject())
                if (file.Value.ValueKind == JsonValueKind.String && GamePath(file.Name) is { } game && Normal(file.Value.GetString()!) is { Length: > 0 } entry)
                    into[game] = new(entry, null);
        if (container.TryGetProperty("FileSwaps", out var swaps) && swaps.ValueKind == JsonValueKind.Object)
            foreach (var swap in swaps.EnumerateObject())
                if (swap.Value.ValueKind == JsonValueKind.String && GamePath(swap.Name) is { } game && GamePath(swap.Value.GetString()!) is { } target)
                    into[game] = new(null, target);
    }

    /// <summary>A game path in the form Haven compares: lower case, forward slashes, no leading slash; null if not one.</summary>
    public static string? GamePath(string path)
    {
        var normal = path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
        return normal.Length is > 0 and <= 255 && !normal.Contains("..") && !normal.Contains(':') && normal.All(c => c is >= ' ' and < (char)127) ? normal : null;
    }

    /// <summary>The bytes of one of the mod's own files (a Files entry), or null when it has none at that path.</summary>
    public byte[]? Read(string entry)
    {
        entry = Normal(entry);
        if (entry.Length == 0 || entry.Split('/').Any(part => part is "" or "." or "..")) return null;
        if (zip != null)
        {
            if (!entries.TryGetValue(entry, out var item) || item.Length > MaxFileBytes) return null;
            using var stream = item.Open();
            var buffer = new MemoryStream((int)Math.Min(item.Length, MaxFileBytes));
            stream.CopyTo(buffer);
            return buffer.Length > MaxFileBytes ? null : buffer.ToArray();
        }
        var full = Path.GetFullPath(Path.Combine(folder!, entry));
        if (!full.StartsWith(folder! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || new FileInfo(full).Length > MaxFileBytes) return null;
        return File.ReadAllBytes(full);
    }

    private IEnumerable<string> Names() => zip != null ? entries.Keys
        : Directory.EnumerateFiles(folder!, "*.json", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>();

    private JsonDocument? Json(string name)
    {
        byte[]? bytes;
        if (zip != null) bytes = entries.TryGetValue(name, out var entry) && entry.Length <= MaxJsonBytes ? Read(name) : null;
        else bytes = File.Exists(Path.Combine(folder!, name)) && new FileInfo(Path.Combine(folder!, name)).Length <= MaxJsonBytes ? File.ReadAllBytes(Path.Combine(folder!, name)) : null;
        if (bytes == null) return null;
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { throw new InvalidDataException($"The mod's {name} could not be read."); }
    }

    private static string Normal(string path) => path.Replace('\\', '/').Trim().TrimStart('/');
    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    public void Dispose() => zip?.Dispose();
}
