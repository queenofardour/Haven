using System.Numerics;
using System.Text.Json.Serialization;

namespace Haven.Core;

public sealed record Furnishing
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public uint ItemId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildingPart { get; init; }
    // The wallpaper or flooring (an interior fixture item) a building piece's surface, or a partition, shows.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint SurfaceFinish { get; init; }
    // The painting (a Picture row) a picture frame, easel or wall frame shows; 0 leaves it blank.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Picture { get; init; }
    // The owner's own picture a frame shows instead: the content locker fingerprint (lowercase SHA-256 hex) of the
    // original JPEG, uploaded once however many frames show it. Null shows the painting or Blank.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomPicture { get; init; }
    // How this frame shows its custom picture (PictureCanvas); every PC builds the frame's picture from the original
    // with it. Null is the default: filling the frame, centred.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PictureLayout? Framing { get; init; }
    // Custom furniture: the content locker fingerprint of the package
    // whose files this furnishing draws with. ItemId stays its base item, which gives everything the files don't
    // change, and which anyone without the package (or who declined custom content) sees instead. A prop made from a
    // minion model has no base item (ItemId 0): only its package draws it, so without it nothing shows.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomFurniture { get; init; }
    // What a vase or an Angler's Canvas shows: a row of the game's VaseFlower sheet (its table of every display: a vase's
    // flowers, a canvas's fish print). 0 shows nothing.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Display { get; init; }
    // An aquarium's fish and decoration (Aquariums).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AquariumSetup? Aquarium { get; init; }
    // A wall picture frame stood upright: turned a quarter counter-clockwise about its canvas centre (PictureCanvas).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Portrait { get; init; }
    public float[] Position { get; init; } = [0, 0, 0];
    public float Rotation { get; init; }
    // A tilt (about the piece's own left-right axis, its front going down for positive degrees) and a lean (about its
    // front-back axis), applied before the turn (housing protocol 38). Vanilla housing only turns pieces; a tilted seat
    // can't be sat on.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Pitch { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Roll { get; init; }
    public float Scale { get; init; } = 1;
    public uint StainId { get; init; }
    public bool Collision { get; init; } = true;
    // The item's ambient sounds as a multiple of their authored volume, 0 to 1; null plays them unchanged.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? SoundVolume { get; init; }
    // Linked pieces (FurnishingGroups): furnishings sharing a group move, turn, scale, duplicate and delete as one.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? Group { get; init; }
    // Pose balls (PoseBalls.cs): the animation sets this furnishing offers, each a set of balls players take.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PoseSet[]? Poses { get; init; }
    [JsonIgnore] public Vector3 Translation => new(Position[0], Position[1], Position[2]);
    /// <summary>Turned on more than its upright axis (Pitch or Roll), so its seats can't be used.</summary>
    [JsonIgnore] public bool Tilted => Pitch != 0 || Roll != 0;
}

/// <summary>An aquarium's fish (AquariumFish rows, in the tank's slots in order) and its decoration (1 to 6, 0 for none).</summary>
public sealed record AquariumSetup
{
    public uint[] Fish { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Decor { get; init; }
}

/// <summary>
/// Aquarium rules, as the game's: a tier N tank has N slots and holds fish whose sizes (small 1, medium 2, large 3,
/// extra large 4) add up to at most N, all of one water (fresh or salt), which also fills the tank.
/// </summary>
public static class Aquariums
{
    public const int MaxTier = 4, MaxDecor = 6;
    public static bool Fits(int tier, IReadOnlyList<(int Size, uint Water)> fish) =>
        tier is >= 1 and <= MaxTier && fish.Count <= tier && fish.Sum(f => f.Size) <= tier && fish.All(f => f.Size is >= 1 and <= 4)
        && fish.Select(f => f.Water).Distinct().Count() <= 1;
}

public sealed record InteriorOptions
{
    // Null brightness retains the shell's native lighting. Fixture IDs are HousingInterior rows.
    public float? Brightness { get; init; }
    public InteriorFloor[] Floors { get; init; } = [new(), new(), new()];
    // Mansions have an additional central ceiling fixture in native slot 3/4.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint MainHallCeilingLight { get; init; }
    /// <summary>
    /// Pieces of the game's own shell the owner removed (remodelling; housing protocol 34):
    /// each a layout instance of the shell's bg.lgb, hidden on every PC. Null when none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShellPiece[]? HiddenPieces { get; init; }
    /// <summary>
    /// The weather the room shows, a Weather row (housing protocol 39). Only a weather the room's level holds is shown
    /// (housing interiors hold just Fair Skies; inns often several); null keeps the room's own.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Weather { get; init; }
    /// <summary>The time of day held in the home, in Eorzean minutes after midnight (0-1439); null follows Eorzea's clock.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeOfDay { get; init; }
    /// <summary>The weather heard outside (HomeAmbience: "rain", "thunderstorm"…), played from the game's own loops; null for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Outside { get; init; }
    /// <summary>The home's saved moods (HomeMoods), switched with one click; null when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HomeMood[]? Moods { get; init; }
}
/// <summary>
/// A hidden shell piece: its layout instance id, whether one can still stand on it (its collision kept), and, for a piece
/// moved with the remodel's Move it, the building piece that stands in for it (put back, the piece returns and that goes).
/// </summary>
public sealed record ShellPiece
{
    public uint Instance { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepCollision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? MovedTo { get; init; }
}
public sealed record InteriorFloor
{
    public uint Wallpaper { get; init; }
    public uint Flooring { get; init; }
    public uint CeilingLight { get; init; }
}

public sealed record MusicOptions
{
    public bool Playing { get; init; }
    // Retained for older saves/protocol peers; playback uses each listener's game audio settings.
    public float Volume { get; init; } = 0.6f;
    public bool Loop { get; init; } = true;
    public bool Shuffle { get; init; }
    public int ShuffleSeed { get; init; } = 1;
    public int GapSeconds { get; init; }
    public long StartedAtUnixMs { get; init; }
    public MusicTrack[] Playlist { get; init; } = [];
}
public sealed record MusicTrack
{
    /// <summary>A game orchestrion track, or 0 for a custom song.</summary>
    public uint OrchestrionId { get; init; }
    public int Repeats { get; init; } = 1;
    /// <summary>A custom song: the fingerprint of its Ogg Vorbis file (Haven.Core.Music), which every listener fetches.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CustomMusic { get; init; }
    /// <summary>A custom song's name, as its uploader chose it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Title { get; init; }
    /// <summary>A custom song's length, so every listener schedules it alike before the file arrives.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Milliseconds { get; init; }
    [JsonIgnore] public bool IsCustom => CustomMusic != null;
    // Accept this retired field in existing saves without applying or re-saving
    // it. Other unknown fields remain rejected by the strict house parser.
    [JsonPropertyName("playSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LegacyPlaySeconds { get => 0; init { } }
}
public readonly record struct MusicSlot(int PlaylistIndex, long Cycle, int Repetition, bool Silent, double RemainingSeconds);

public static class MusicSchedule
{
    // Compute from the shared start time, so late visitors use the same queue/transition boundaries.
    public static MusicSlot? Locate(MusicOptions music, long nowUnixMs, IReadOnlyList<long> durationsMs)
    {
        if (!music.Playing || music.Playlist.Length == 0) return null;
        if (durationsMs.Count != music.Playlist.Length || durationsMs.Any(x => x is <= 0 or > 86_400_000)) throw new InvalidDataException("Track durations are not ready.");
        var total = music.Playlist.Select((t, i) => (long)t.Repeats * (durationsMs[i] + music.GapSeconds * 1000L)).Sum();
        if (total <= 0) return null;
        var elapsed = Math.Max(0, (double)nowUnixMs - music.StartedAtUnixMs);
        if (!music.Loop && elapsed >= total) return null;
        var cycle = (long)(elapsed / total);
        var cursor = elapsed % total;
        var order = Enumerable.Range(0, music.Playlist.Length);
        if (music.Shuffle) order = order.OrderBy(i => Mix((uint)music.ShuffleSeed ^ (uint)i * 2654435761U));
        foreach (var index in order)
        {
            var track = music.Playlist[index];
            for (var repetition = 0; repetition < track.Repeats; repetition++)
            {
                if (cursor < durationsMs[index]) return new(index, cycle, repetition, false, (durationsMs[index] - cursor) / 1000);
                cursor -= durationsMs[index];
                if (cursor < music.GapSeconds * 1000L) return new(index, cycle, repetition, true, (music.GapSeconds * 1000L - cursor) / 1000);
                cursor -= music.GapSeconds * 1000L;
            }
        }
        return null;
    }
    private static uint Mix(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; return x ^ (x >> 16); }

    /// <summary>
    /// A new shared start time for an edited playlist (tracks added, removed, moved, repeats, gap or shuffle changed),
    /// chosen so the track playing now is still due at the same point in it: editing does not interrupt the music.
    /// Null when nothing is playing, between tracks, when the playing track was removed, or when a length the new order
    /// needs is not known yet (durationOf returns null); the start time then stays as it was.
    /// </summary>
    public static long? Rebase(MusicOptions old, MusicOptions updated, long nowUnixMs, IReadOnlyList<long> oldDurationsMs, Func<int, long?> durationOf)
    {
        if (!old.Playing || !updated.Playing || updated.Playlist.Length == 0) return null;
        if (Locate(old, nowUnixMs, oldDurationsMs) is not { Silent: false } slot) return null;
        var playing = old.Playlist[slot.PlaylistIndex];
        var into = oldDurationsMs[slot.PlaylistIndex] - (long)Math.Round(slot.RemainingSeconds * 1000);
        static bool Same(MusicTrack a, MusicTrack b) => a.OrchestrionId == b.OrchestrionId && a.CustomMusic == b.CustomMusic;
        // The same entry if it kept its place, otherwise the first copy of that track.
        var index = slot.PlaylistIndex < updated.Playlist.Length && Same(updated.Playlist[slot.PlaylistIndex], playing)
            ? slot.PlaylistIndex : Array.FindIndex(updated.Playlist, t => Same(t, playing));
        if (index < 0) return null;
        var repetition = Math.Min(slot.Repetition, updated.Playlist[index].Repeats - 1);
        var order = Enumerable.Range(0, updated.Playlist.Length);
        if (updated.Shuffle) order = order.OrderBy(i => Mix((uint)updated.ShuffleSeed ^ (uint)i * 2654435761U));
        long offset = 0;
        foreach (var i in order)
        {
            if (durationOf(i) is not { } length || length <= 0) return null;
            var per = length + updated.GapSeconds * 1000L;
            if (i == index) { offset += repetition * per; break; }
            offset += updated.Playlist[i].Repeats * per;
        }
        return nowUnixMs - offset - into;
    }
}

public static class PlacementMath
{
    // Portrait's quarter turn, the size, the tilt (Pitch about X, then Roll about Z, in the piece's own frame), the turn
    // about the upright axis, then the place.
    public static Matrix4x4 ItemMatrix(Furnishing item) => (item.Portrait ? Matrix4x4.CreateRotationZ(MathF.PI / 2) : Matrix4x4.Identity)
        * Matrix4x4.CreateScale(item.Scale) * Tilt(item) * Matrix4x4.CreateRotationY(item.Rotation * MathF.PI / 180f) * Matrix4x4.CreateTranslation(item.Translation);
    public static Matrix4x4 Tilt(Furnishing item) => !item.Tilted ? Matrix4x4.Identity
        : Matrix4x4.CreateRotationX(item.Pitch * MathF.PI / 180f) * Matrix4x4.CreateRotationZ(item.Roll * MathF.PI / 180f);
    public static Vector3? RayPlane(Vector3 origin, Vector3 direction, float elevation, float maximumDistance = 100)
    {
        if (Math.Abs(direction.Y) < 0.00001f) return null;
        var distance = (elevation - origin.Y) / direction.Y;
        return float.IsFinite(distance) && distance >= 0 && distance <= maximumDistance ? origin + direction * distance : null;
    }
    /// <summary>
    /// A new piece's rotation (degrees, a quarter turn at a time) that turns its front (+Z) toward the camera: against
    /// where the camera looks, on the home's floor plane. 0 when the camera looks straight down.
    /// </summary>
    public static float FacingCamera(Vector3 cameraForward, Matrix4x4 house)
    {
        if (!Matrix4x4.Invert(house, out var inverse)) return 0;
        var toward = Vector3.TransformNormal(-cameraForward, inverse);
        if (toward.X * toward.X + toward.Z * toward.Z < 1e-8f) return 0;
        var degrees = MathF.Round(MathF.Atan2(toward.X, toward.Z) * 180 / MathF.PI / 90) * 90;
        return (degrees % 360 + 360) % 360;
    }
    public static Vector3 Snap(Vector3 point, float spacing) => spacing > 0
        ? new(MathF.Round(point.X / spacing) * spacing, point.Y, MathF.Round(point.Z / spacing) * spacing) : point;

    public static float? RayBox(Vector3 origin, Vector3 direction, Vector3 minimum, Vector3 maximum, Matrix4x4 localToWorld, bool exitWhenInside = false)
    {
        if (!Matrix4x4.Invert(localToWorld, out var inverse)) return null;
        var o = Vector3.Transform(origin, inverse);
        var d = Vector3.TransformNormal(direction, inverse);
        var near = 0f; var far = float.MaxValue;
        for (var axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(d[axis]) < 0.000001f) { if (o[axis] < minimum[axis] || o[axis] > maximum[axis]) return null; continue; }
            var a = (minimum[axis] - o[axis]) / d[axis]; var b = (maximum[axis] - o[axis]) / d[axis];
            near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b));
            if (near > far) return null;
        }
        // Structural wall/ceiling assemblies can surround the camera. Their box
        // boundary must not score a zero-distance hit ahead of every chair inside.
        return exitWhenInside && near == 0 ? far : near;
    }

    /// <summary>
    /// The nearest of a model's triangles (index triples into its vertex positions) that a ray crosses, from either
    /// side, as a distance along the ray; null when it crosses none. The ray is taken into the model's own space, so
    /// the distance is in the ray's units however the model is scaled.
    /// </summary>
    public static float? RayTriangles(Vector3 origin, Vector3 direction, ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> triangles, Matrix4x4 localToWorld)
    {
        if (!Matrix4x4.Invert(localToWorld, out var inverse)) return null;
        var o = Vector3.Transform(origin, inverse);
        var d = Vector3.TransformNormal(direction, inverse);
        var nearest = float.MaxValue;
        for (var i = 0; i + 2 < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            if ((uint)a >= (uint)positions.Length || (uint)b >= (uint)positions.Length || (uint)c >= (uint)positions.Length) continue;
            // Möller–Trumbore, without culling.
            var v0 = positions[a]; var e1 = positions[b] - v0; var e2 = positions[c] - v0;
            var p = Vector3.Cross(d, e2);
            var det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f) continue;
            var inv = 1 / det;
            var s = o - v0;
            var u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, e1);
            var v = Vector3.Dot(d, q) * inv;
            if (v < 0 || u + v > 1) continue;
            var t = Vector3.Dot(e2, q) * inv;
            if (t > 1e-4f && t < nearest) nearest = t;
        }
        return nearest < float.MaxValue ? nearest : null;
    }
}

public static class FurnitureDyes
{
    // The game's background stain buffer holds ordinary RGB bytes and full strength, squared into a linear colour
    // that the colour-change shader uses as the model's colour. Zero is black, not "undyed": an undyed model holds
    // its material's own default colour (see FromLinear).
    public static uint Rgba(uint rgb, bool dyed) => dyed
        ? ((rgb >> 16) & 255) | (rgb & 0xFF00) | ((rgb & 255) << 16) | 0xFF000000
        : 0;
    // A material's linear g_DiffuseColor as the 0xRRGGBB a dye would write: the inverse of the buffer's squaring.
    public static uint FromLinear(float r, float g, float b)
    {
        static uint Byte(float v) => (uint)MathF.Round(MathF.Sqrt(Math.Clamp(float.IsFinite(v) ? v : 0, 0, 1)) * 255);
        return Byte(r) << 16 | Byte(g) << 8 | Byte(b);
    }
    public static Vector4 Linear(uint rgb) => new(Square((rgb >> 16) & 255), Square((rgb >> 8) & 255), Square(rgb & 255), 1);
    private static float Square(uint component) { var value = component / 255f; return value * value; }
}
