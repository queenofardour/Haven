using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Haven.Core;
using Haven.Core.World;

namespace Haven;

/// <summary>A territory for the world index: its row, its level (TerritoryType.Bg) and its place name.</summary>
internal sealed record WorldTerritory(uint Row, string Bg, string Place);

/// <summary>An effect or sound a zone's layout places (listed by the offline tool for later; not placeable).</summary>
internal sealed record WorldAmbient(string Path, string Kind, int Zone);

/// <summary>
/// Builds the world index from the game data: every territory's bg.lgb, each model and zone
/// shared group it places, and the models inside those groups. Only what the path rule takes (BuildingParts.IsWorldAsset)
/// is kept.
/// </summary>
internal static class WorldIndexBuilder
{
    // A group's own pieces, read once per group: its models (with their collision) and the groups inside it.
    private sealed record GroupMembers((string Path, WorldCollision Collision, string? CollisionPath)[] Models, string[] Groups, int Lights, int Effects, int Sounds);
    private sealed class Stat { public int Uses; public WorldCollision Collision; public string? CollisionPath; public readonly Dictionary<int, int> ByZone = []; }

    /// <param name="ambient">Effects and sounds the layouts place, when the caller wants them (the offline tool).</param>
    public static WorldIndex Build(IEnumerable<WorldTerritory> territories, Func<string, LgbFile?> readLayout, Func<string, SgbFile?> readGroup,
        string gameVersion, CancellationToken cancel = default, Action<int, int>? progress = null, List<WorldAmbient>? ambient = null)
    {
        // One zone per level folder: several territories share one (a town and its instanced copies), named after the
        // place most of them give (the lowest row on a tie).
        var zones = territories.Where(t => t.Bg.Contains("/level/", StringComparison.Ordinal))
            .GroupBy(t => t.Bg[..t.Bg.IndexOf("/level/", StringComparison.Ordinal)], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var place = g.Where(t => t.Place.Length > 0).GroupBy(t => t.Place).OrderByDescending(p => p.Count()).ThenBy(p => p.Min(t => t.Row)).FirstOrDefault()?.Key;
                var level = g.OrderBy(t => t.Row).First().Bg;
                return (Zone: new WorldZone(g.Key, place ?? g.Key[(g.Key.LastIndexOf('/') + 1)..]), Layout: "bg/" + level[..(level.LastIndexOf('/') + 1)] + "bg.lgb");
            }).ToArray();
        var stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
        var groups = new Dictionary<string, GroupMembers?>(StringComparer.Ordinal);
        void Use(string path, int zone, WorldCollision collision = WorldCollision.None, string? collisionPath = null)
        {
            if (!BuildingParts.IsWorldAsset(path)) return;
            if (!stats.TryGetValue(path, out var stat)) stats[path] = stat = new();
            stat.Uses++; stat.ByZone[zone] = stat.ByZone.GetValueOrDefault(zone) + 1;
            if (stat.Collision == WorldCollision.None && collision != WorldCollision.None) (stat.Collision, stat.CollisionPath) = (collision, collisionPath);
        }
        GroupMembers? Members(string path)
        {
            if (groups.TryGetValue(path, out var known)) return known;
            groups[path] = null; // a group that holds itself stops here
            GroupMembers? members = null;
            try
            {
                if (readGroup(path) is { } group)
                {
                    var objects = group.LayerGroups.SelectMany(g => g.Layers).SelectMany(l => l.InstanceObjects).ToArray();
                    members = new([.. objects.Select(o => o.Object).OfType<LayerCommon.BGInstanceObject>().Select(Model)],
                        [.. objects.Select(o => o.Object).OfType<LayerCommon.SharedGroupInstanceObject>().Select(g => g.AssetPath)],
                        objects.Count(o => o.Object is LayerCommon.LightInstanceObject),
                        // Always-playing effects no timeline addresses (what the furniture reader keeps for a world group).
                        Passive(objects, group), PassiveSounds(objects, group));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { members = null; } // an unreadable group adds nothing
            return groups[path] = members;
        }
        static int Passive(IEnumerable<LayerCommon.InstanceObject> objects, SgbFile group)
        {
            IReadOnlySet<uint> timed;
            try { timed = SgbTimelines.Targets(group.Data); } catch (Exception ex) when (ex is not OutOfMemoryException) { timed = new HashSet<uint>(); }
            return objects.Count(o => o.Object is LayerCommon.VFXInstanceObject { AssetPath: { } vfxPath } vfx && vfx.IsAutoPlay != 0 && !timed.Contains(o.InstanceId)
                && vfxPath.EndsWith(".avfx", StringComparison.Ordinal) && (vfxPath.StartsWith("bg/", StringComparison.Ordinal) || vfxPath.StartsWith("bgcommon/", StringComparison.Ordinal)));
        }
        // Always-playing sounds no timeline addresses (what the furniture reader keeps for a world group).
        static int PassiveSounds(IEnumerable<LayerCommon.InstanceObject> objects, SgbFile group)
        {
            IReadOnlySet<uint> timed;
            try { timed = SgbTimelines.Targets(group.Data); } catch (Exception ex) when (ex is not OutOfMemoryException) { timed = new HashSet<uint>(); }
            return objects.Count(o => o.Object is LayerCommon.SoundInstanceObject { AssetPath: { } soundPath } sound && sound.SEParam.AutoPlay != 0 && !timed.Contains(o.InstanceId)
                && soundPath.EndsWith(".scd", StringComparison.Ordinal) && (soundPath.StartsWith("bg/", StringComparison.Ordinal) || soundPath.StartsWith("bgcommon/", StringComparison.Ordinal)));
        }
        var soundsIn = new Dictionary<string, int>(StringComparer.Ordinal);
        int SoundsIn(string path, int depth)
        {
            if (depth > 4 || Members(path) is not { } members) return 0;
            if (soundsIn.TryGetValue(path, out var known)) return known;
            soundsIn[path] = 0;
            return soundsIn[path] = Math.Min(members.Sounds + members.Groups.Sum(g => SoundsIn(g, depth + 1)), FurniturePrefabReader.MaxWorldGroupSounds);
        }
        var effectsIn = new Dictionary<string, int>(StringComparer.Ordinal);
        int EffectsIn(string path, int depth)
        {
            if (depth > 4 || Members(path) is not { } members) return 0;
            if (effectsIn.TryGetValue(path, out var known)) return known;
            effectsIn[path] = 0;
            return effectsIn[path] = Math.Min(members.Effects + members.Groups.Sum(g => EffectsIn(g, depth + 1)), FurniturePrefabReader.MaxWorldGroupEffects);
        }
        // A group's lights with its nested groups', as far down as Expand goes (the furniture reader reads them all).
        var lightsIn = new Dictionary<string, int>(StringComparer.Ordinal);
        int LightsIn(string path, int depth)
        {
            if (depth > 4 || Members(path) is not { } members) return 0;
            if (lightsIn.TryGetValue(path, out var known)) return known;
            lightsIn[path] = 0; // a group that holds itself counts once
            return lightsIn[path] = members.Lights + members.Groups.Sum(g => LightsIn(g, depth + 1));
        }
        void Expand(string path, int zone, int depth)
        {
            if (depth > 4 || Members(path) is not { } members) return;
            foreach (var (model, collision, collisionPath) in members.Models) Use(model, zone, collision, collisionPath);
            foreach (var nested in members.Groups) { Use(nested, zone); Expand(nested, zone, depth + 1); }
        }

        for (var z = 0; z < zones.Length; z++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Invoke(z, zones.Length);
            LgbFile? layout;
            try { layout = readLayout(zones[z].Layout); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            if (layout == null) continue;
            foreach (var obj in layout.Layers.SelectMany(l => l.InstanceObjects))
                switch (obj.Object)
                {
                    case LayerCommon.BGInstanceObject bg: var (path, collision, collisionPath) = Model(bg); Use(path, z, collision, collisionPath); break;
                    case LayerCommon.SharedGroupInstanceObject group: Use(group.AssetPath, z); Expand(group.AssetPath, z, 0); break;
                    case LayerCommon.VFXInstanceObject vfx when ambient != null && vfx.AssetPath.Length > 0: ambient.Add(new(vfx.AssetPath, "effect", z)); break;
                    case LayerCommon.SoundInstanceObject sound when ambient != null && sound.AssetPath.Length > 0: ambient.Add(new(sound.AssetPath, "sound", z)); break;
                }
        }
        progress?.Invoke(zones.Length, zones.Length);
        // Only zones a piece names stay, renumbered.
        var used = stats.Values.SelectMany(s => s.ByZone.Keys).Distinct().Order().ToArray();
        var renumber = used.Select((zone, i) => (zone, i)).ToDictionary(x => x.zone, x => x.i);
        var pieces = stats.Select(kv =>
        {
            var stat = kv.Value;
            var zone = stat.ByZone.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key;
            // A collision mesh whose path isn't one the index keeps is left out, never guessed at.
            var mesh = stat.Collision == WorldCollision.Mesh && stat.CollisionPath is { } c && WorldIndex.IsCollisionPath(c);
            var collision = stat.Collision == WorldCollision.Mesh && !mesh ? WorldCollision.None : stat.Collision;
            return new WorldPiece(kv.Key, renumber[zone], stat.Uses, stat.ByZone.Count, collision, mesh ? stat.CollisionPath : null,
                kv.Key.EndsWith(".sgb", StringComparison.Ordinal) ? LightsIn(kv.Key, 0) : 0,
                kv.Key.EndsWith(".sgb", StringComparison.Ordinal) ? EffectsIn(kv.Key, 0) : 0,
                kv.Key.EndsWith(".sgb", StringComparison.Ordinal) ? SoundsIn(kv.Key, 0) : 0);
        });
        if (ambient != null)
        {
            var kept = ambient.Where(a => renumber.ContainsKey(a.Zone)).Select(a => a with { Zone = renumber[a.Zone] }).ToArray();
            ambient.Clear(); ambient.AddRange(kept);
        }
        return new WorldIndex(gameVersion, [.. used.Select(z => zones[z].Zone)], pieces);
    }

    /// <summary>
    /// The models a world piece draws, each with its place in the piece: a model itself, or a group's members (and its
    /// nested groups', a few levels down). For thumbnails; drawing in a home goes through the building part.
    /// </summary>
    public static List<(string Model, System.Numerics.Matrix4x4 Transform)> PieceModels(string path, Func<string, SgbFile?> readGroup)
    {
        var models = new List<(string, System.Numerics.Matrix4x4)>();
        if (path.EndsWith(".mdl", StringComparison.Ordinal)) models.Add((path, System.Numerics.Matrix4x4.Identity));
        else GroupModels(path, System.Numerics.Matrix4x4.Identity, readGroup, models, 0);
        return models;
    }

    private static void GroupModels(string path, System.Numerics.Matrix4x4 parent, Func<string, SgbFile?> readGroup, List<(string, System.Numerics.Matrix4x4)> models, int depth)
    {
        if (depth > 4 || readGroup(path) is not { } group) return;
        foreach (var obj in group.LayerGroups.SelectMany(g => g.Layers).SelectMany(l => l.InstanceObjects))
        {
            var t = obj.Transform;
            var local = LayoutRotation.Transform(new(t.Translation.X, t.Translation.Y, t.Translation.Z), new(t.Rotation.X, t.Rotation.Y, t.Rotation.Z),
                new(t.Scale.X, t.Scale.Y, t.Scale.Z)) * parent;
            if (obj.Object is LayerCommon.BGInstanceObject bg) models.Add((bg.AssetPath, local));
            else if (obj.Object is LayerCommon.SharedGroupInstanceObject nested) GroupModels(nested.AssetPath, local, readGroup, models, depth + 1);
        }
    }

    // A placed model and the collision its placement gives it: its box, a collision mesh of its own, or none.
    private static (string Path, WorldCollision Collision, string? CollisionPath) Model(LayerCommon.BGInstanceObject bg) => bg.CollisionType switch
    {
        ModelCollisionType.Box => (bg.AssetPath, WorldCollision.Box, null),
        ModelCollisionType.Replace when !string.IsNullOrEmpty(bg.CollisionAssetPath) => (bg.AssetPath, WorldCollision.Mesh, bg.CollisionAssetPath),
        _ => (bg.AssetPath, WorldCollision.None, null),
    };
}
