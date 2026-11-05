using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Haven.Core;

/// <summary>A detached scene identity. Playback controls and save timestamps are live metadata.</summary>
public sealed class PublishedHouseScene
{
    private readonly byte[] scene;
    // Indented JSON uses platform-specific newlines. Hash the compact form so
    // Windows clients and Linux relays identify the same scene identically.
    private static readonly JsonSerializerOptions IdentityJson = new(HouseFormat.Json) { WriteIndented = false };
    public string Hash { get; }

    public PublishedHouseScene(House house) : this(SceneOf(house)) { }
    /// <summary>From <see cref="SceneOf"/>'s bytes, computed once (the relay prepares them outside its lock).</summary>
    public PublishedHouseScene(byte[] sceneBytes)
    {
        scene = sceneBytes;
        Hash = Convert.ToHexStringLower(SHA256.HashData(scene));
    }

    public bool Matches(House house) => Matches(SceneOf(house));
    public bool Matches(ReadOnlySpan<byte> sceneBytes) => scene.AsSpan().SequenceEqual(sceneBytes);
    /// <summary>The canonical scene bytes a published scene is identified by (validates the house).</summary>
    public static byte[] SceneOf(House house) => SceneBytes(house);

    public static string SnapshotHash(House house) => Convert.ToHexStringLower(SHA256.HashData(SceneBytes(house, includeMusic: true)));

    private static byte[] SceneBytes(House house, bool includeMusic = false)
    {
        HouseFormat.Validate(house);
        var node = JsonSerializer.SerializeToNode(house with { SavedAt = DateTimeOffset.UnixEpoch, Music = includeMusic ? house.Music : new(),
            Furnishings = house.Furnishings.OrderBy(x => x.Id).ToArray() }, IdentityJson);
        JsonNode? Canonical(JsonNode? value) => value switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new KeyValuePair<string, JsonNode?>(x.Key, Canonical(x.Value)))),
            JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
            JsonValue number when number.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number
                => JsonNode.Parse(element.TryGetDecimal(out var exact) ? exact.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)
                    : element.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
            _ => value?.DeepClone()
        };
        return JsonSerializer.SerializeToUtf8Bytes(Canonical(node), IdentityJson);
    }
}
