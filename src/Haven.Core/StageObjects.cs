using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Haven.Core;

public sealed record StageObjectEntry(string Key, string[] Path, string Name, string Type, string? ModelPath,
    Vector3 Position, Vector3 Rotation, Vector3 Scale, Matrix4x4 ParentTransform, bool Disabled)
{
    public Matrix4x4 Transform => StageObjects.Matrix(Position, Rotation, Scale) * ParentTransform;
}

// Paths use JSON-pointer escaping for identity; array segments are used for navigation so
// imported names containing slashes, tildes or dots cannot address the wrong object.
public static class StageObjects
{
    public static StageObjectEntry[] Read(JsonElement stage)
    {
        List<StageObjectEntry> result = [];
        Walk(stage.GetProperty("Objects"), [], Matrix4x4.Identity, false, result);
        return result.ToArray();
    }

    private static void Walk(JsonElement objects, string[] parents, Matrix4x4 parent, bool disabled, List<StageObjectEntry> result)
    {
        foreach (var property in objects.EnumerateObject())
        {
            var value = property.Value;
            var path = parents.Append(property.Name).ToArray();
            var type = value.GetProperty("Type").GetString()!;
            var name = value.TryGetProperty("DisplayName", out var label) ? label.GetString() ?? "" : "";
            var position = Vector(value, "Position", Vector3.Zero);
            var rotation = Vector(value, "RotationPitchYawRollDegrees", Vector3.Zero);
            var scale = Vector(value, "Scale", Vector3.One);
            var hidden = disabled || value.TryGetProperty("IsDisabled", out var off) && off.GetBoolean();
            var model = value.TryGetProperty("ModelGamePath", out var modelPath) ? modelPath.GetString() : null;
            result.Add(new("/" + string.Join('/', path.Select(Escape)), path, name, type, model, position, rotation, scale, parent, hidden));
            if (type == "Group") Walk(value.GetProperty("Objects"), path, Matrix(position, rotation, scale) * parent, hidden, result);
        }
    }

    public static Matrix4x4 Matrix(Vector3 position, Vector3 rotation, Vector3 scale) => Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateFromYawPitchRoll(rotation.Y * MathF.PI / 180, rotation.X * MathF.PI / 180, rotation.Z * MathF.PI / 180)
        * Matrix4x4.CreateTranslation(position);

    public static JsonElement Remove(JsonElement stage, string[] path)
    {
        var root = JsonNode.Parse(stage.GetRawText())!;
        var (objects, name) = Parent(root, path);
        if (!objects.Remove(name)) throw new InvalidOperationException("That imported object no longer exists.");
        return JsonSerializer.SerializeToElement(root);
    }

    public static JsonElement Transform(JsonElement stage, string[] path, Vector3 position, Vector3 rotation, Vector3 scale)
    {
        if (!Finite(position) || !Finite(rotation) || !Finite(scale) || Math.Abs(position.X) > 10000 || Math.Abs(position.Y) > 10000 || Math.Abs(position.Z) > 10000)
            throw new InvalidDataException("Invalid imported object transform.");
        var root = JsonNode.Parse(stage.GetRawText())!;
        var (objects, name) = Parent(root, path);
        var value = objects[name] ?? throw new InvalidOperationException("That imported object no longer exists.");
        value["Position"] = Node(position); value["RotationPitchYawRollDegrees"] = Node(rotation); value["Scale"] = Node(scale);
        return JsonSerializer.SerializeToElement(root);
    }

    private static (JsonObject, string) Parent(JsonNode root, string[] path)
    {
        if (path.Length == 0 || path.Length > 9) throw new InvalidDataException("Invalid imported object path.");
        var objects = root["Objects"]?.AsObject() ?? throw new InvalidDataException("Missing stage objects.");
        for (var i = 0; i < path.Length - 1; i++)
            objects = objects[path[i]]?["Objects"]?.AsObject() ?? throw new InvalidOperationException("That imported group no longer exists.");
        return (objects, path[^1]);
    }
    private static string Escape(string name) => name.Replace("~", "~0").Replace("/", "~1");
    private static Vector3 Vector(JsonElement value, string name, Vector3 fallback) => value.TryGetProperty(name, out var vector)
        ? new(vector.GetProperty("X").GetSingle(), vector.GetProperty("Y").GetSingle(), vector.GetProperty("Z").GetSingle()) : fallback;
    private static JsonObject Node(Vector3 value) => new() { ["X"] = value.X, ["Y"] = value.Y, ["Z"] = value.Z };
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
