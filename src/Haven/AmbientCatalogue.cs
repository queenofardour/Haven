using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Plugin.Services;
using Haven.Core;
using Haven.Core.World;
using Ambience = Haven.Core.World.WorldAmbient;

namespace Haven;

/// <summary>
/// The world's effects and ambient sounds as placeable pieces, from the catalogue embedded in
/// Haven (World/world-ambient.json, written offline by the catalogue tool's --ambient). A piece is a building part with
/// no model: an effect brings one of Haven's own effects (FurniturePrefab.WorldEffects), a sound one of Haven's own sounds
/// (WorldSounds); BuildAmbience and FurnitureEffects/FurnitureSounds draw and play them like world sets' own. Only an
/// entry whose files this game has is offered: an effect outside the base game also needs every texture it names (a free
/// trial lacks Endwalker and Dawntrail, and a game asked for a file it doesn't have closes).
/// </summary>
internal sealed class AmbientCatalogue(IDataManager data, IPluginLog log)
{
    private Task<Loaded>? loading;
    private sealed record Loaded(WorldAmbientTable Table, FurnitureEntry[] Effects, FurnitureEntry[] Sounds, string[] EffectCategories, string[] SoundCategories);
    private readonly ConcurrentDictionary<string, BuildingPart> parts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> installed = new(StringComparer.Ordinal);

    /// <summary>Starts reading the catalogue in the background (the drawer's Effects or Sounds source opened).</summary>
    public void Start() => loading ??= Task.Run(Load);
    public bool Ready => loading is { IsCompletedSuccessfully: true };
    public string Status => loading is { IsFaulted: true } ? "The effects and sounds couldn't be read: " + loading.Exception!.GetBaseException().Message : "";
    public FurnitureEntry[] Effects => Ready ? loading!.Result.Effects : [];
    public FurnitureEntry[] Sounds => Ready ? loading!.Result.Sounds : [];
    public string[] EffectCategories => Ready ? loading!.Result.EffectCategories : [];
    public string[] SoundCategories => Ready ? loading!.Result.SoundCategories : [];

    private Loaded Load()
    {
        var table = WorldAmbientTable.Embedded();
        var usable = table.Entries.Where(e => Installed(e.Path, e.Kind)).ToArray();
        FurnitureEntry Entry(WorldAmbientEntry e) => new(0, 0, 0, 0, e.Name, e.Category, e.Zone.Length > 0 ? e.Zone : e.Kind == AmbientKind.Effect ? "World effect" : "World sound", 0, 0, e.Path);
        // In name order, a size word set aside ("Huge waterfall" beside "Waterfall curtain", not among the other huge things).
        FurnitureEntry[] Of(AmbientKind kind) => [.. usable.Where(e => e.Kind == kind).OrderBy(e => SortKey(e.Name), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Select(Entry)];
        var effects = Of(AmbientKind.Effect); var sounds = Of(AmbientKind.Sound);
        log.Information("[AMBIENT] {Effects} effects and {Sounds} sounds to place (of {All} in the catalogue).", effects.Length, sounds.Length, table.Count);
        return new(table, effects, sounds, [.. Ambience.EffectCategories.Where(c => effects.Any(e => e.Category == c))],
            [.. Ambience.SoundCategories.Where(c => sounds.Any(e => e.Category == c))]);
    }

    /// <summary>
    /// The scale a new effect piece starts at: one far too big for a room (a waterfall or a wall of fire tens of metres
    /// across) starts small enough to see whole, about <see cref="StartingSize"/> across; the inspector's Scale gives it
    /// back its full size.
    /// </summary>
    public float StartingScale(string path) => Find(path) is { Kind: AmbientKind.Effect, Size: > StartingSize * 1.5f } entry
        ? Math.Clamp(MathF.Round(StartingSize / entry.Size, 2), .05f, 1) : 1;
    public const float StartingSize = 6;

    /// <summary>Whether the catalogue is read and doesn't list this piece (a newer Haven's, or one since dropped).</summary>
    public bool Unlisted(string path) => Ready && Find(path) == null;

    /// <summary>The catalogue's entry for a piece (null when it isn't in this Haven's catalogue, or not read yet).</summary>
    public WorldAmbientEntry? Find(string path) => Ready ? loading!.Result.Table.Find(path) : null;

    /// <summary>
    /// An effect or sound piece's building part: the effect or sound at the piece's origin, or nothing (and a plain name)
    /// when this game hasn't its files. Only an entry in this Haven's catalogue plays: a home from someone else may name
    /// any effect or sound path the format allows (a spell's, a battle cue, which would repeat for ever), so one the
    /// catalogue doesn't list stays silent and unseen, just its marker (a newer Haven's entries play once this one is updated).
    /// </summary>
    public BuildingPart Part(string path)
    {
        if (parts.TryGetValue(path, out var known)) return known;
        var kind = Ambience.IsEffectPath(path) ? AmbientKind.Effect : AmbientKind.Sound;
        Start();
        // Read once: the catalogue may finish reading meanwhile, and a piece it lists mustn't be kept as one it doesn't.
        var ready = Ready;
        var entry = ready ? loading!.Result.Table.Find(path) : null;
        var name = entry?.Name ?? (kind == AmbientKind.Effect ? "World effect" : "World sound") + (ready ? " (not in this Haven's list)" : "");
        var category = entry?.Category ?? "Other";
        var zone = entry?.Zone is { Length: > 0 } z ? z : kind == AmbientKind.Effect ? "World effect" : "World sound";
        FurniturePrefab prefab = new([], [], [], []);
        if (entry != null && Installed(path, kind))
            prefab = kind == AmbientKind.Effect
                ? prefab with { WorldEffects = [new(path, entry?.Offset is [var ox, var oy, var oz] && float.IsFinite(ox + oy + oz) ? Matrix4x4.CreateTranslation(ox, oy, oz) : Matrix4x4.Identity,
                    entry?.Colour is [var r, var g, var b, var a] ? new Vector4(r, g, b, a) : Vector4.One)] }
                : prefab with { WorldSounds = [new(path, Matrix4x4.Identity)] };
        var part = new BuildingPart(new FurnitureEntry(0, 0, 0, 0, name, category, zone, 0, 0, path), prefab, zone, []);
        // A placeholder made before the catalogue was read isn't kept, so its proper name comes once it's ready.
        if (entry != null || ready) parts[path] = part;
        return part;
    }

    private static readonly string[] SizeWords = ["tiny ", "small ", "large ", "huge ", "enormous ", "vast ", "giant "];
    private static string SortKey(string name)
    {
        foreach (var word in SizeWords)
            if (name.StartsWith(word, StringComparison.OrdinalIgnoreCase) && name.Length > word.Length) return name[word.Length..];
        return name;
    }

    // An effect or sound this game can play: its file, and for an effect outside the base game each texture it names.
    private bool Installed(string path, AmbientKind kind) => installed.GetOrAdd(path, p =>
    {
        try
        {
            if (!data.FileExists(p)) return false;
            if (kind == AmbientKind.Sound || GameDataPaths.Expansion(p) == 0) return true;
            return data.GetFile(p)?.Data is { } avfx && Haven.Core.Animation.AvfxFiles.Textures(avfx).All(t => GameDataPaths.Expansion(t) == 0 || data.FileExists(t));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    });
}
