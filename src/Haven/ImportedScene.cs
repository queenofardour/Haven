using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Haven.Core;
using Lumina.Data.Files;
using Stagehand.Definitions;
using Stagehand.Definitions.Objects;

namespace Haven;

internal sealed record ImportedObject(StageObjectEntry Definition, string Name, string Label, uint Icon, Vector3 Minimum, Vector3 Maximum);

internal sealed class ImportedScene(IDataManager data, FurnitureCatalogue catalogue)
{
    private JsonElement cached;
    private ImportedObject[] entries = [];
    private readonly ConcurrentDictionary<string, (Vector3, Vector3)> bounds = new(StringComparer.Ordinal);
    private readonly object cacheGate = new();

    public ImportedObject[] Objects(JsonElement stage)
    { lock (cacheGate) return ReadObjects(stage); }
    private ImportedObject[] ReadObjects(JsonElement stage)
    {
        if (stage.Equals(cached)) return entries;
        var definitions = StageObjects.Read(stage);
        var result = new ImportedObject[definitions.Length];
        for (var i = 0; i < definitions.Length; i++)
        {
            var definition = definitions[i]; var item = catalogue.FromModelPath(definition.ModelPath);
            var name = !string.IsNullOrWhiteSpace(definition.Name) && !definition.Name.StartsWith("fun_", StringComparison.Ordinal)
                ? definition.Name : item?.Name ?? (definition.Type == "Group" ? "Imported group" : definition.Type == "Light" ? "Imported light" : "Imported furnishing");
            var box = definition.ModelPath is {} path ? bounds.GetOrAdd(path, ReadBounds) : (new Vector3(-0.25f), new Vector3(0.25f));
            result[i] = new(definition, name, name + "##imported" + definition.Key, item?.Icon ?? 0u, box.Item1, box.Item2);
        }
        // Give groups the combined bounds of their visible model descendants in group-local space.
        for (var i = result.Length - 1; i >= 0; i--)
        {
            var group = result[i]; if (group.Definition.Type != "Group" || !Matrix4x4.Invert(group.Definition.Transform, out var inverse)) continue;
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue); var found = false;
            for (var j = i + 1; j < result.Length; j++)
            {
                var child = result[j];
                if (!child.Definition.Key.StartsWith(group.Definition.Key + "/", StringComparison.Ordinal)) break;
                if (child.Definition.Disabled || child.Definition.Type == "Group") continue;
                var transform = child.Definition.Transform * inverse;
                for (var corner = 0; corner < 8; corner++)
                {
                    var p = Vector3.Transform(new((corner & 1) == 0 ? child.Minimum.X : child.Maximum.X,
                        (corner & 2) == 0 ? child.Minimum.Y : child.Maximum.Y, (corner & 4) == 0 ? child.Minimum.Z : child.Maximum.Z), transform);
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p); found = true;
                }
            }
            if (found) result[i] = group with { Minimum = min, Maximum = max };
        }
        cached = stage; entries = result; return entries;
    }

    private (Vector3, Vector3) ReadBounds(string path)
    {
        var model = data.GetFile<MdlFile>(path);
        if (model == null) return (new(-0.3f), new(0.3f));
        var b = model.ModelBoundingBoxes;
        return (new(b.Min[0], b.Min[1], b.Min[2]), new(b.Max[0], b.Max[1], b.Max[2]));
    }

    public static void Remove(StageDefinition stage, string[] path)
    {
        var objects = stage.Objects;
        for (var i = 0; i < path.Length - 1; i++) objects = ((GroupDefinition)objects[path[i]]).Objects;
        objects.Remove(path[^1]);
    }

    public static (StageDefinition Stage, ObjectDefinition Target) Ghost(JsonElement source, string[] path)
    {
        if (!StageDefinition.TryParseDefinitionString(source.GetRawText(), out var stage)) throw new InvalidDataException("Invalid imported stage.");
        var selected = stage.Objects; var output = new StageDefinition { FormatVersion = StageDefinition.CurrentFormatVersion };
        var destination = output.Objects;
        for (var i = 0; i < path.Length - 1; i++)
        {
            var ancestor = (GroupDefinition)selected[path[i]];
            var shell = new GroupDefinition(); ancestor.CopyTo(shell); shell.Objects.Clear();
            destination[path[i]] = shell; destination = shell.Objects; selected = ancestor.Objects;
        }
        var target = selected[path[^1]].Clone(); Tint(target); destination[path[^1]] = target;
        return (output, target);
    }
    private static void Tint(ObjectDefinition value)
    {
        if (value is BgObjectDefinition model) model.Opacity *= 0.55f;
        if (value is LightDefinition) value.IsDisabled = true;
        if (value is GroupDefinition group) foreach (var child in group.Objects.Values) Tint(child);
    }
    public static JsonElement PreviewStage(JsonElement source, JsonElement original, IReadOnlyList<StagedImported> pending)
    {
        if (pending.Count == 0) return source;
        if (!StageDefinition.TryParseDefinitionString(source.GetRawText(), out var stage)) throw new InvalidDataException("Invalid staged preview.");
        foreach (var item in pending)
        {
            if (item.Deleted)
            {
                var (ghost, _) = Ghost(original, item.Item.Definition.Path);
                Merge(stage.Objects, ghost.Objects);
            }
            else
            {
                var objects = stage.Objects; var path = item.Item.Definition.Path;
                for (var i = 0; i < path.Length - 1; i++) objects = ((GroupDefinition)objects[path[i]]).Objects;
                if (objects.TryGetValue(path[^1], out var value)) Tint(value);
            }
        }
        return JsonSerializer.Deserialize<JsonElement>(stage.ToDefinitionString());
    }
    private static void Merge(Dictionary<string, ObjectDefinition> destination, Dictionary<string, ObjectDefinition> source)
    {
        foreach (var (key, value) in source)
            if (value is GroupDefinition group && destination.TryGetValue(key, out var old) && old is GroupDefinition oldGroup) Merge(oldGroup.Objects, group.Objects);
            else destination[key] = value;
    }
}
