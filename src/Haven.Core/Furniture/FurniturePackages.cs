using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Haven.Core.Furniture;

/// <summary>
/// A custom furnishing's package: which vanilla item it is built on, and the files its
/// mod changed, each at a path of Haven's own under bgcommon/hou/hv/&lt;id&gt;/. Models maps each of the base item's
/// model paths that changed to its new one; the base item's prefab is drawn with those substituted.
/// </summary>
public sealed record FurnitureManifest(int Format, string Name, uint BaseItem, string Id, Dictionary<string, string> Models, string[] Files)
{
    /// <summary>
    /// A minion prop: a character model a mod changed, converted into a furnishing of its own with no base item
    /// (BaseItem 0). Models then maps the minion's model path to the prop's one model.
    /// </summary>
    public bool Prop { get; init; }
}

/// <summary>A package in hand: its manifest and files (new game path → bytes).</summary>
public sealed record FurniturePackage(FurnitureManifest Manifest, IReadOnlyDictionary<string, byte[]> Files);

public static class FurniturePackages
{
    // A package file's own name, as Haven writes them (t0.tex, m0001.mtrl, model2.mdl): letters, digits and underscores
    // and a kind. Each is written to disk on a guest's PC, so nothing Windows treats specially (con, nul, com1, a trailing
    // dot or space, "~") gets through.
    private static readonly System.Text.RegularExpressions.Regex FileName = new(@"^[a-z0-9_]{1,160}\.(mdl|mtrl|tex)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly HashSet<string> DeviceNames = ["con", "prn", "aux", "nul", .. Enumerable.Range(0, 10).SelectMany(i => new[] { $"com{i}", $"lpt{i}" })];
    internal static bool SafeFileName(string name) => FileName.IsMatch(name) && !DeviceNames.Contains(name[..name.IndexOf('.')]);

    public const int Format = 1;
    public const string Root = "bgcommon/hou/hv/";
    /// <summary>
    /// The largest package, compressed (custom furniture and pose animations alike): the player's whole allowance,
    /// 250 MB (decimal, as the Worker counts it). Only the allowance bounds packages (since
    /// 3 November 2026, when an animation mod of 108 MB failed against the old 25 MB). Cloudflare takes at most 100 MB a request, so
    /// larger packages go up in parts (ContentLocker.PartBytes, LockerUpload); the Worker streams them into storage.
    /// </summary>
    public const int MaxBytes = 250_000_000;
    /// <summary>The most its files may hold once unpacked, so a package can't unpack into something huge.</summary>
    public const long MaxUnpackedBytes = 1024L * 1024 * 1024;
    private const int MaxFiles = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>The minion models (chara/monster/…) a mod changes: each can become a prop of its own.</summary>
    public static IReadOnlyList<string> PropModels(PenumbraMod mod) =>
        [.. mod.Files.Keys.Where(k => System.Text.RegularExpressions.Regex.IsMatch(k, @"^chara/monster/m\d{4}/obj/body/b\d{4}/model/m\d{4}b\d{4}\.mdl$")).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The vanilla furniture material a prop's materials are made from: bg.shpk with diffuse, normal and specular maps
    /// and no detail texture, like most furniture (932 of 1,927 furniture materials share its settings). Until 0.37.2
    /// it was the Tonberry Floor Lamp's body, whose detail texture 9 laid a cloth weave over every prop.
    /// </summary>
    public const string PropMaterialTemplate = "bgcommon/hou/indoor/general/0010/material/fun_b0_m0010_0a.mtrl";
    private const string NoSpecular = "bgcommon/texture/dummy_s.tex";
    // Scenery shader constants (CRC-32 of their names): g_DetailID (0 = none) and g_EmissiveColor.
    private const uint DetailId = 0x8981D4D9, EmissiveColor = 0x38A64362;
    // Baked colour textures are at most this wide or tall.
    private const int MaxBakedSide = 1024;

    /// <summary>
    /// Builds a prop from a minion model a mod changed: the model drawn as a furnishing (its material names made full
    /// paths of the prop's own), each of its character materials remade as a furnishing material on the prop's own
    /// diffuse, normal and specular textures. Bones are kept but unused, so a prop draws in its resting pose; a rigid
    /// prop (everything on one bone, as gpose props are) looks as it does in the game.
    /// </summary>
    public static (byte[] Zip, FurnitureManifest Manifest) BuildProp(PenumbraMod mod, string model, Func<string, byte[]?> game, string name, int materialSet = 1)
    {
        model = PenumbraMod.GamePath(model) ?? throw new InvalidDataException("A model path is not valid.");
        var plan = new Plan(mod, game);
        var source = plan.Content(model);
        var template = game(PropMaterialTemplate) ?? throw new InvalidDataException("The game's furniture material is unavailable.");
        // A character model names its materials relatively; they live in its body folder's material/vNNNN folders.
        var body = model[..model.IndexOf("/model/", StringComparison.Ordinal)];
        string MaterialPath(string named)
        {
            if (PenumbraMod.GamePath(named) is { } full && full.StartsWith("chara/", StringComparison.Ordinal)) return full;
            // The minion's own material set (its variant's vNNNN folder, from the .imc file): several minions can share a
            // model with different materials (Wayward Hatchling v0001, Black Chocobo Chick v0002).
            var file = named.TrimStart('/').ToLowerInvariant();
            var own = $"{body}/material/v{materialSet:0000}/{file}";
            if (mod.Files.ContainsKey(own) || game(own) != null) return own;
            var modded = mod.Files.Keys.Where(k => k.StartsWith(body + "/material/v", StringComparison.Ordinal) && k.EndsWith("/" + file, StringComparison.Ordinal)).Order(StringComparer.Ordinal).FirstOrDefault();
            return modded ?? own;
        }
        var names = FurnitureFiles.ModelMaterialSlots(source);
        var sources = names.Select(MaterialPath).ToArray();
        var hashes = new List<string> { model + ":" + Convert.ToHexStringLower(SHA256.HashData(source)) };
        var textures = new Dictionary<string, string>(StringComparer.Ordinal);       // source texture → new path
        var materials = new List<(string Name, string Path, string Diffuse, string Normal, string Specular, CharacterLook? Look)>();
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var id = "";
        foreach (var (named, path) in names.Zip(sources))
        {
            var mtrl = plan.Content(path);
            hashes.Add(path + ":" + Convert.ToHexStringLower(SHA256.HashData(mtrl)));
            var (diffuse, normal, specular) = FurnitureFiles.CharacterTextures(mtrl);
            // The colour table: a tint baked into the colour texture, and the rows that glow.
            CharacterLook? look;
            try { look = CharacterLook.Read(mtrl, p => PenumbraMod.GamePath(p) is { } key ? plan.Resolve(key) : null); }
            catch (InvalidDataException) { look = null; }
            if (diffuse == null && look == null) throw new InvalidOperationException($"This minion's material has no colour texture Haven can use ({Path.GetFileName(path)}).");
            materials.Add((named, path, diffuse ?? "", normal ?? "", specular ?? "", look));
        }
        foreach (var texture in materials.SelectMany(m => new[] { m.Diffuse, m.Normal, m.Specular }).Where(t => t.Length > 0).Distinct())
            hashes.Add(texture + ":" + Convert.ToHexStringLower(SHA256.HashData(plan.Content(PenumbraMod.GamePath(texture)!))));
        foreach (var path in sources.Distinct()) foreach (var texture in FurnitureFiles.MaterialTextures(plan.Content(path)))
            if (PenumbraMod.GamePath(texture) is { } key && plan.Resolve(key) is { } bytes) hashes.Add(key + ":" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
        // "prop4": each change to the conversion (0.37.3: no detail texture, baked colour tables, glowing meshes; 0.37.4:
        // older tables' shine) gives new paths, never a cached older conversion's files (unpacking skips same-size files).
        id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("prop4\n" + materialSet + "\n" + string.Join("\n", hashes))))[..12];
        var prefix = Root + id + "/";
        var n = 0;
        string Texture(string source)
        {
            var key = PenumbraMod.GamePath(source)!;
            if (textures.TryGetValue(key, out var known)) return known;
            var next = $"{prefix}t{n++:x}.tex";
            var bytes = plan.Content(key);
            FurnitureFiles.Check(next, bytes);
            files[next] = bytes;
            return textures[key] = next;
        }
        // A colour texture with the colour table's tint baked in: uncompressed, on the template's colour texture header.
        var templateTextures = FurnitureFiles.MaterialTextures(template);
        byte[] Header(int i) => templateTextures.Count > i && game(templateTextures[i]) is { Length: >= 80 } header ? header : PlainTextureHeader();
        string Write(byte[] rgba, int w, int h, byte[] header)
        {
            var next = $"{prefix}t{n++:x}.tex";
            var bytes = CustomPictureFiles.Texture(rgba, w, h, header);
            FurnitureFiles.Check(next, bytes);
            files[next] = bytes;
            return next;
        }
        // The colour texture with the table's tint (and metals' colour) baked in, and the specular map an older table's
        // shine becomes; both uncompressed, on the template's own textures' headers.
        (string Colour, string? Specular) Baked(CharacterLook look, string colour)
        {
            var ((rgba, w, h), specular) = look.Baked(colour.Length > 0 ? plan.Content(PenumbraMod.GamePath(colour)!) : null);
            while (w > MaxBakedSide || h > MaxBakedSide)
            {
                if (specular != null) specular = Halve(specular, w, h).Rgba;
                (rgba, w, h) = Halve(rgba, w, h);
            }
            return (Write(rgba, w, h, Header(0)), specular != null ? Write(specular, w, h, Header(2)) : null);
        }
        var remade = new Dictionary<string, string>(StringComparer.Ordinal);       // the model's material name → new path
        var made = new Dictionary<string, byte[]>(StringComparer.Ordinal);         // new path → its material, for glowing copies
        foreach (var material in materials)
        {
            if (remade.ContainsKey(material.Name)) continue;
            var next = $"{prefix}m{n++:x}.mtrl";
            var (colour, specular) = material.Look is { } look && (look.Tints || look.Shines || material.Diffuse.Length == 0) ? Baked(look, material.Diffuse) : (Texture(material.Diffuse), null);
            // A character specular map means something else to a scenery material (whose blue is metalness), so it is
            // never passed on as it is: an older table's shine is baked into one of the prop's own, otherwise none.
            var bytes = FurnitureFiles.SceneryMaterial(template, colour,
                material.Normal.Length > 0 ? Texture(material.Normal) : "bgcommon/texture/dummy_n.tex", specular ?? NoSpecular);
            bytes = FurnitureFiles.WithConstant(bytes, DetailId, 0) ?? bytes;
            FurnitureFiles.Check(next, bytes);
            files[next] = bytes;
            made[next] = bytes;
            remade[material.Name] = next;
        }
        var newModel = $"{prefix}model{n++:x}.mdl";
        var converted = FurnitureFiles.StripSkeleton(FurnitureFiles.RewriteModelMaterials(source, named => remade.GetValueOrDefault(named)));
        // Glowing parts: a scenery material glows all over (g_EmissiveColor), so the triangles the colour table makes
        // glow move into a mesh of their own, drawn with a copy of the material that glows in their colour.
        var looks = names.Select(named => materials.First(m => m.Name == named).Look).ToArray();
        var glowNames = new List<string>();
        var splits = new List<ModelMeshes.Split>();
        var glowMaterial = new Dictionary<int, int>();          // material number → its glowing copy's number
        foreach (var mesh in ModelMeshes.Read(converted))
        {
            if (mesh.Material >= looks.Length || looks[mesh.Material] is not { Glows: true } look || mesh.Uvs.Length == 0) continue;
            var (moved, glow) = Glowing(mesh, look);
            if (glow is not { } colour) continue;
            if (!glowMaterial.TryGetValue(mesh.Material, out var slot))
            {
                var path = $"{prefix}m{n++:x}.mtrl";
                if (FurnitureFiles.WithConstant(made[remade[names[mesh.Material]]], EmissiveColor, colour.X, colour.Y, colour.Z) is not { } glowing) continue;
                FurnitureFiles.Check(path, glowing);
                files[path] = glowing;
                glowNames.Add(path);
                glowMaterial[mesh.Material] = slot = names.Count + glowNames.Count - 1;
            }
            splits.Add(new(mesh.Index, moved, slot));
        }
        if (splits.Count > 0) converted = ModelMeshes.AddMeshes(converted, glowNames, splits);
        FurnitureFiles.Check(newModel, converted);
        files[newModel] = converted;
        var manifest = new FurnitureManifest(Format, SafeText.Clean(name, 60).Trim() is { Length: > 0 } clean ? clean : "Custom prop", 0, id,
            new() { [model] = newModel }, [.. files.Keys]) { Prop = true };
        var zip = Pack(manifest, files);
        if (zip.Length > MaxBytes) throw new InvalidOperationException($"This prop's files come to {zip.Length / 1e6:0.0} MB; custom furniture is at most {MaxBytes / 1_000_000} MB.");
        return (zip, manifest);
    }

    /// <summary>
    /// Of a minion model's material sets (vNNNN), those whose materials the mod changes (a file of its own that isn't
    /// the game's, or a swap); every one of them when the mod changes none, as when it only retextures.
    /// </summary>
    public static IReadOnlyList<int> ChangedMaterialSets(PenumbraMod mod, string model, Func<string, byte[]?> game, IReadOnlyList<int> sets)
    {
        model = PenumbraMod.GamePath(model) ?? throw new InvalidDataException("A model path is not valid.");
        var plan = new Plan(mod, game);
        var body = model[..model.IndexOf("/model/", StringComparison.Ordinal)];
        var files = FurnitureFiles.ModelMaterials(plan.Content(model)).Where(n => n.StartsWith('/')).Select(n => n.TrimStart('/').ToLowerInvariant()).ToArray();
        bool Changes(int set) => files.Any(file =>
        {
            var path = $"{body}/material/v{set:0000}/{file}";
            if (!mod.Files.TryGetValue(path, out var entry)) return false;
            return entry.SwapTo != null || plan.Resolve(path) is not { } bytes || game(path) is not { } original || !bytes.AsSpan().SequenceEqual(original);
        });
        var changed = sets.Where(Changes).ToArray();
        return changed.Length > 0 ? changed : sets;
    }

    // Which of a mesh's triangles glow (at least two of its centre and corners on glowing texels), and their mean glow.
    private static (bool[] Moved, System.Numerics.Vector3? Glow) Glowing(ModelMeshes.Mesh mesh, CharacterLook look)
    {
        var moved = new bool[mesh.Indices.Length / 3];
        var sum = System.Numerics.Vector3.Zero;
        var count = 0;
        for (var t = 0; t < moved.Length; t++)
        {
            int a = mesh.Indices[t * 3], b = mesh.Indices[t * 3 + 1], c = mesh.Indices[t * 3 + 2];
            if (a >= mesh.Uvs.Length || b >= mesh.Uvs.Length || c >= mesh.Uvs.Length || a == b || b == c || a == c) continue;
            var centre = (mesh.Uvs[a] + mesh.Uvs[b] + mesh.Uvs[c]) / 3;
            var lit = 0;
            var here = System.Numerics.Vector3.Zero;
            foreach (var uv in new[] { centre, mesh.Uvs[a], mesh.Uvs[b], mesh.Uvs[c] })
            {
                var glow = look.At(uv.X, uv.Y).Glow;
                if (Math.Max(glow.X, Math.Max(glow.Y, glow.Z)) > CharacterLook.GlowThreshold) { lit++; here += glow; }
            }
            if (lit < 2) continue;
            moved[t] = true;
            sum += here / lit;
            count++;
        }
        return (moved, count > 0 ? sum / count : null);
    }

    // A plain 2D texture header, for when the template's colour texture can't be read.
    private static byte[] PlainTextureHeader()
    {
        var header = new byte[80];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 0x00800000);
        return header;
    }

    // Half the size, each texel the mean of four.
    private static (byte[] Rgba, int Width, int Height) Halve(byte[] rgba, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var next = new byte[nw * nh * 4];
        for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
                for (var c = 0; c < 4; c++)
                {
                    int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1), y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                    next[(y * nw + x) * 4 + c] = (byte)((rgba[(y0 * w + x0) * 4 + c] + rgba[(y0 * w + x1) * 4 + c] + rgba[(y1 * w + x0) * 4 + c] + rgba[(y1 * w + x1) * 4 + c] + 2) / 4);
                }
        return (next, nw, nh);
    }

    /// <summary>
    /// Whether the mod changes anything the given models draw with: a model itself, a material one names, or a texture
    /// one of those names. <paramref name="game"/> reads the installed game's files.
    /// </summary>
    public static bool Changes(PenumbraMod mod, IEnumerable<string> models, Func<string, byte[]?> game)
    {
        try { return new Plan(mod, game).Walk(models.Select(m => PenumbraMod.GamePath(m)!).Where(m => m != null)).Count > 0; }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>
    /// Builds the package for one base item: every file its models draw with that the mod changed (or that names one
    /// that changed) gets a path of its own, and its paths inside are rewritten to match.
    /// </summary>
    public static (byte[] Zip, FurnitureManifest Manifest) Build(PenumbraMod mod, uint baseItem, IReadOnlyList<string> baseModels, Func<string, byte[]?> game, string name)
    {
        var plan = new Plan(mod, game);
        var models = baseModels.Select(m => PenumbraMod.GamePath(m) ?? throw new InvalidDataException("A model path is not valid.")).Distinct().ToArray();
        var changed = plan.Walk(models);
        if (changed.Count == 0) throw new InvalidOperationException("This mod doesn't change that furnishing.");
        // An id from the base item and the changed files' contents, so the same import gives the same paths.
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseItem + "\n" + string.Join("\n", changed.Order(StringComparer.Ordinal).Select(p => p + ":" + Convert.ToHexStringLower(SHA256.HashData(plan.Content(p)))))))) [..12];
        var prefix = Root + id + "/";
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var n = 0;
        foreach (var path in changed.Where(p => p.EndsWith(".tex", StringComparison.Ordinal)).Order(StringComparer.Ordinal)) paths[path] = $"{prefix}t{n++:x}.tex";
        foreach (var path in changed.Where(p => p.EndsWith(".mtrl", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            // Written in place inside each model, so exactly as long as the path it replaces.
            var middle = path.Length - prefix.Length - ".mtrl".Length;
            if (middle < 3) throw new InvalidDataException($"A material path is too short to give a path of its own: {path}");
            paths[path] = prefix + "m" + (n++).ToString("x").PadLeft(middle - 1, '0') + ".mtrl";
        }
        foreach (var path in changed.Where(p => p.EndsWith(".mdl", StringComparison.Ordinal)).Order(StringComparer.Ordinal)) paths[path] = $"{prefix}model{n++:x}.mdl";
        string? Replace(string original) => PenumbraMod.GamePath(original) is { } key && paths.TryGetValue(key, out var next) ? next : null;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (original, next) in paths)
        {
            var content = plan.Content(original);
            var bytes = original.EndsWith(".mdl", StringComparison.Ordinal) ? FurnitureFiles.RepathModel(content, Replace)
                : original.EndsWith(".mtrl", StringComparison.Ordinal) ? FurnitureFiles.RepathMaterial(content, Replace) : content;
            FurnitureFiles.Check(next, bytes);
            files[next] = bytes;
        }
        var manifest = new FurnitureManifest(Format, SafeText.Clean(name, 60).Trim() is { Length: > 0 } clean ? clean : "Custom furnishing", baseItem, id,
            models.Where(paths.ContainsKey).ToDictionary(m => m, m => paths[m]), [.. files.Keys]);
        var zip = Pack(manifest, files);
        if (zip.Length > MaxBytes) throw new InvalidOperationException($"This furnishing's files come to {zip.Length / 1e6:0.0} MB; custom furniture is at most {MaxBytes / 1_000_000} MB.");
        return (zip, manifest);
    }

    private static byte[] Pack(FurnitureManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] bytes)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open(); stream.Write(bytes);
            }
            Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
            foreach (var (path, bytes) in files) Add(path, bytes);
        }
        return output.ToArray();
    }

    /// <summary>
    /// A package's id (the folder it unpacks to), read from its manifest alone; null when it can't be read or isn't
    /// valid. Nothing else in the package is opened.
    /// </summary>
    public static string? ManifestId(Stream package)
    {
        try
        {
            using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.GetEntry("manifest.json") is not { } entry) return null;
            var id = JsonSerializer.Deserialize<FurnitureManifest>(Bytes(entry, 256 * 1024), Json)?.Id;
            return id is { Length: 12 } && id.All(Uri.IsHexDigit) && !id.Any(char.IsUpper) ? id : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Opens a package from anyone (a download), checking everything before any file reaches the game: its size, its
    /// manifest, that every file lives under its own id's folder and is one the manifest lists, the unpacked total,
    /// and each file's structure.
    /// </summary>
    public static FurniturePackage Read(byte[] package)
    {
        if (package.Length > MaxBytes) throw new InvalidDataException("A custom furnishing is larger than Haven accepts.");
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxFiles + 1) throw new InvalidDataException("A custom furnishing has too many files.");
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("A custom furnishing has no manifest.");
        FurnitureManifest manifest;
        try { manifest = JsonSerializer.Deserialize<FurnitureManifest>(Bytes(manifestEntry, 256 * 1024), Json) ?? throw new InvalidDataException("A custom furnishing's manifest is empty."); }
        catch (JsonException) { throw new InvalidDataException("A custom furnishing's manifest could not be read."); }
        if (manifest.Format != Format) throw new InvalidDataException("This custom furnishing needs a newer Haven.");
        if (manifest.Id is not { Length: 12 } || !manifest.Id.All(Uri.IsHexDigit) || manifest.Id.Any(char.IsUpper)) throw new InvalidDataException("A custom furnishing's id is not valid.");
        if (manifest.Files is not { Length: > 0 and <= MaxFiles } || manifest.Models is not { Count: > 0 }) throw new InvalidDataException("A custom furnishing lists no files.");
        var prefix = Root + manifest.Id + "/";
        long total = 0;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in manifest.Files)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || PenumbraMod.GamePath(path) != path || path.IndexOf('/', prefix.Length) >= 0 || files.ContainsKey(path)
                || !SafeFileName(path[prefix.Length..]))
                throw new InvalidDataException("A custom furnishing names a file outside its own folder.");
            var entry = zip.GetEntry(path) ?? throw new InvalidDataException("A custom furnishing is missing a file.");
            total += entry.Length;
            if (total > MaxUnpackedBytes) throw new InvalidDataException("A custom furnishing unpacks too large.");
            var bytes = Bytes(entry, MaxUnpackedBytes);
            FurnitureFiles.Check(path, bytes);
            // A model with bones makes a scenery object load a skeleton and animation, which crashed the game.
            if (path.EndsWith(".mdl", StringComparison.Ordinal) && FurnitureFiles.ModelBones(bytes) > 0) throw new InvalidDataException("A custom furnishing's model has a skeleton, which furniture can't draw.");
            files[path] = bytes;
        }
        if (zip.Entries.Any(e => e.FullName != "manifest.json" && !files.ContainsKey(e.FullName))) throw new InvalidDataException("A custom furnishing holds files its manifest doesn't list.");
        foreach (var (original, next) in manifest.Models)
            if (PenumbraMod.GamePath(original) != original || !original.EndsWith(".mdl", StringComparison.Ordinal) || !files.ContainsKey(next) || !next.EndsWith(".mdl", StringComparison.Ordinal))
                throw new InvalidDataException("A custom furnishing's models are not listed properly.");
        if (manifest.Prop ? manifest.BaseItem != 0 || manifest.Models.Count != 1 || !manifest.Models.Keys.Single().StartsWith("chara/", StringComparison.Ordinal) : manifest.BaseItem == 0)
            throw new InvalidDataException("A custom furnishing's kind is not listed properly.");
        return new(manifest with { Name = SafeText.Clean(manifest.Name, 60).Trim() is { Length: > 0 } clean ? clean : "Custom furnishing" }, files);
    }

    private static byte[] Bytes(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("A custom furnishing's file is too large.");
        using var stream = entry.Open();
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) throw new InvalidDataException("A custom furnishing's file is too large.");
        }
        return buffer.ToArray();
    }

    // Which of a model's files the mod changed, following models → materials → textures.
    private sealed class Plan(PenumbraMod mod, Func<string, byte[]?> game)
    {
        private readonly Dictionary<string, byte[]?> cache = new(StringComparer.Ordinal);

        // What the game would load at a path with the mod on: the mod's file, a swapped-to file, or the game's own.
        public byte[]? Resolve(string path, int depth = 0)
        {
            if (cache.TryGetValue(path, out var known)) return known;
            byte[]? bytes;
            if (depth < 4 && mod.Files.TryGetValue(path, out var file))
                bytes = file.Entry != null ? mod.Read(file.Entry) ?? throw new InvalidDataException($"The mod is missing {file.Entry}.") : Resolve(file.SwapTo!, depth + 1);
            else bytes = game(path);
            return cache[path] = bytes;
        }
        public byte[] Content(string path) => Resolve(path) ?? throw new InvalidDataException($"Neither the mod nor the game has {path}.");

        /// <summary>The files to give paths of their own: changed by the mod, or naming one that is.</summary>
        public HashSet<string> Walk(IEnumerable<string> models)
        {
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool Material(string path)
            {
                if (seen.TryGetValue(path, out var known)) return known;
                var textures = FurnitureFiles.MaterialTextures(Content(path)).Select(PenumbraMod.GamePath).OfType<string>().ToArray();
                var moved = false;
                foreach (var texture in textures.Where(mod.Files.ContainsKey)) { changed.Add(texture); moved = true; }
                moved |= mod.Files.ContainsKey(path);
                if (moved) changed.Add(path);
                return seen[path] = moved;
            }
            foreach (var model in models)
            {
                var bytes = Resolve(model);
                if (bytes == null) continue;
                var moved = mod.Files.ContainsKey(model);
                foreach (var material in FurnitureFiles.ModelMaterials(bytes).Select(PenumbraMod.GamePath).OfType<string>().Distinct())
                    moved |= Material(material);
                if (moved) changed.Add(model);
            }
            return changed;
        }
    }
}
