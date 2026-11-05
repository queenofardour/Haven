using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Haven.Core;

public sealed record EditingState(ulong Owner, bool Enabled, ulong[] Editors, long Revision, string Hash, Guid[]? Pending = null, int Protocol = 1)
{
    public bool CanEdit(ulong user) => user != 0 && (user == Owner || Enabled && Editors.Contains(user));
}
public sealed record EditingPermissions(bool Enabled, ulong[] Editors);
public sealed record EditOperation(string[] Path, bool BeforeExists, JsonElement? Before, bool AfterExists, JsonElement? After);
public sealed record EditBatch(Guid Id, Guid HouseId, string BaseHash, EditOperation[] Changes);
public sealed record EditProposal(ulong Author, string Name, EditBatch Batch);
public sealed record EditDecision(Guid Id, bool Accept);
public sealed record EditReview(Guid[] Ids, bool Accept);
public sealed record EditContribution(ulong Author, EditBatch Batch);
public sealed record EditApplied(ulong Author, EditBatch Batch, long Revision, string Hash, Guid[]? Pending = null, EditContribution[]? Contributions = null);
public sealed record EditResult(Guid Id, string Outcome, string Message, EditBatch? Rejected = null);

/// <summary>One explicit save produces one compressed transaction; cursor/axis previews never enter this API.</summary>
public static class HostedEditing
{
    public const int MaxExpanded = 4 * HouseFormat.MaxBytes;
    public const int MaxChanges = 8192;
    /// <summary>
    /// Staged changes waiting for the owner: per decorator and per home. The relay refuses past them and the owner's Haven
    /// shows up to them, so every change the relay keeps is one the owner sees.
    /// </summary>
    public const int MaxStagedPerAuthor = 128, MaxStagedPerHome = 512;
    public static readonly JsonSerializerOptions Json = new(HouseFormat.Json) { WriteIndented = false, MaxDepth = 40 };
    public static string Hash(House house) => new PublishedHouseScene(house).Hash;
    public static bool Same(House a, House b) => new PublishedHouseScene(a).Matches(b);

    public static bool Overlaps(string[] a, string[] b) => a.Take(Math.Min(a.Length, b.Length)).SequenceEqual(b.Take(Math.Min(a.Length, b.Length)));
    private static (bool Exists, JsonElement? Value) At(House house, string[] path)
    {
        JsonNode? node = Editable(house);
        foreach (var part in path)
            if (node is JsonObject obj && obj.TryGetPropertyValue(part, out var next) && next != null) node = next;
            else return (false, null);
        return (true, JsonSerializer.SerializeToElement(node, Json));
    }
    // Send only paths changed since the last acknowledged staging update. Before values
    // still describe the published scene, including explicit undo-to-published operations.
    public static EditBatch Stage(House published, House baseline, House working)
    {
        var delta = Create(baseline, working);
        var full = Create(published, working);
        var selected = full.Changes.Where(c => delta.Changes.Any(d => Overlaps(c.Path, d.Path))).ToList();
        foreach (var c in delta.Changes.Where(c => !selected.Any(d => Overlaps(c.Path, d.Path))))
        { var before = At(published, c.Path); selected.Add(c with { BeforeExists = before.Exists, Before = before.Value }); }
        return delta with { BaseHash = Hash(published), Changes = selected.ToArray() };
    }
    public static bool NoChange(EditOperation c) => c.BeforeExists == c.AfterExists
        && (!c.BeforeExists || JsonNode.DeepEquals(JsonNode.Parse(c.Before!.Value.GetRawText()), JsonNode.Parse(c.After!.Value.GetRawText())));
    public static House Reject(House working, House published, EditBatch rejected)
    {
        List<EditOperation> restore = [];
        foreach (var c in rejected.Changes)
        {
            var value = At(working, c.Path);
            var current = c with { BeforeExists = value.Exists, Before = value.Value };
            if (!NoChange(current)) continue; // A newer local edit must survive an older decision.
            var saved = At(published, c.Path);
            restore.Add(current with { AfterExists = saved.Exists, After = saved.Value });
        }
        return Apply(working, rejected with { Changes = restore.ToArray() });
    }

    private static JsonObject Editable(House house)
    {
        var furniture = new JsonObject();
        foreach (var item in house.Furnishings) furniture.Add(item.Id.ToString("N"), JsonSerializer.SerializeToNode(item, Json));
        var npcs = new JsonObject();
        foreach (var npc in house.Npcs ?? []) npcs.Add(npc.Id.ToString("N"), JsonSerializer.SerializeToNode(npc, Json));
        return new() { ["furnishings"] = furniture, ["npcs"] = npcs, ["stage"] = JsonNode.Parse(house.Stage.GetRawText()),
            ["interior"] = JsonSerializer.SerializeToNode(house.Interior, Json), ["transform"] = JsonSerializer.SerializeToNode(house.Transform, Json) };
    }
    private static JsonElement? Value(JsonNode? node, bool exists) => exists ? JsonSerializer.SerializeToElement(node, Json) : null;
    public static EditBatch Create(House before, House after)
    {
        HouseFormat.Validate(before); HouseFormat.Validate(after);
        if (before.Id != after.Id || before.TerritoryId != after.TerritoryId || before.CustomLayout != after.CustomLayout || before.Name != after.Name)
            throw new InvalidDataException("Hosted edits cannot replace the home, its name or its shell.");
        List<EditOperation> changes = [];
        void Diff(JsonNode? old, bool oldExists, JsonNode? next, bool nextExists, string[] path)
        {
            if (oldExists == nextExists && JsonNode.DeepEquals(old, next)) return;
            var atomic = path.Length == 2 && path[0] is "furnishings" or "npcs"
                || path.Length != 0 && path[^1] is "Position" or "RotationPitchYawRollDegrees" or "Scale";
            if (!atomic && oldExists && nextExists && old is JsonObject a && next is JsonObject b)
            {
                foreach (var key in a.Select(x => x.Key).Union(b.Select(x => x.Key)).Order(StringComparer.Ordinal))
                {
                    // A property set to null counts as absent: a null "before" or "after" can't cross the wire (an
                    // operation's JSON null reads back as no value), so the relay refused every such edit.
                    var ae = a.TryGetPropertyValue(key, out var av) && av != null; var be = b.TryGetPropertyValue(key, out var bv) && bv != null;
                    Diff(av, ae, bv, be, [..path, key]);
                }
            }
            else changes.Add(new(path, oldExists, Value(old, oldExists), nextExists, Value(next, nextExists)));
        }
        Diff(Editable(before), true, Editable(after), true, []);
        var result = new EditBatch(Guid.NewGuid(), before.Id, Hash(before), changes.ToArray());
        Validate(result); return result;
    }
    public static void Validate(EditBatch batch)
    {
        if (batch.Id == Guid.Empty || batch.HouseId == Guid.Empty || batch.BaseHash is not { Length: 64 } || !batch.BaseHash.All(Uri.IsHexDigit)
            || batch.Changes == null || batch.Changes.Length > MaxChanges) throw new InvalidDataException("Invalid edit transaction.");
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var change in batch.Changes)
        {
            if (change == null || change.Path == null || change.Path.Length is < 1 or > 32
                || change.Path[0] is not ("furnishings" or "npcs" or "stage" or "interior" or "transform")
                || change.Path.Any(x => string.IsNullOrEmpty(x) || x.Length > 512 || x.Any(char.IsControl))
                || change.Before is {ValueKind: JsonValueKind.Undefined} || change.After is {ValueKind: JsonValueKind.Undefined}
                || change.BeforeExists != change.Before.HasValue || change.AfterExists != change.After.HasValue)
                throw new InvalidDataException("Invalid edit operation.");
            if (change.Path[0] is "furnishings" or "npcs" && (change.Path.Length != 2 || !Guid.TryParseExact(change.Path[1], "N", out _)))
                throw new InvalidDataException("Furniture edits must be whole objects with stable identities.");
            if (!paths.Add(string.Join('\0', change.Path))) throw new InvalidDataException("Duplicate edit path.");
        }
        string? previous = null;
        foreach (var path in paths)
        {
            if (previous != null && path.StartsWith(previous + '\0', StringComparison.Ordinal)) throw new InvalidDataException("Overlapping edit paths.");
            previous = path;
        }
    }
    public static House Apply(House current, EditBatch batch, bool overwriteConflicts = false) => ApplyAll(current, [batch], overwriteConflicts);
    /// <summary>
    /// Applies batches one after another, as Apply would in turn (each checked against the home as the ones before it
    /// left it), but turns the home into JSON and back once: approving many proposals rebuilt the whole home per
    /// proposal, under the relay's lock.
    /// </summary>
    public static House ApplyAll(House current, IReadOnlyList<EditBatch> batches, bool overwriteConflicts = false)
    {
        foreach (var batch in batches)
        {
            Validate(batch);
            if (current.Id != batch.HouseId) throw new InvalidDataException("The edit belongs to a different home.");
        }
        var root = Editable(current);
        foreach (var change in batches.SelectMany(b => b.Changes))
        {
            var parent = root;
            for (var i = 0; i < change.Path.Length - 1; i++)
                parent = parent[change.Path[i]] as JsonObject ?? throw new InvalidOperationException("A containing group changed. Reload or resolve the draft.");
            var key = change.Path[^1];
            var exists = parent.TryGetPropertyValue(key, out var value) && value != null;   // null counts as absent (Create)
            bool Equal(bool expectedExists, JsonElement? expected) => exists == expectedExists
                && (!exists || JsonNode.DeepEquals(value, JsonNode.Parse(expected!.Value.GetRawText())));
            if (Equal(change.AfterExists, change.After)) continue; // A repeated save/ack is idempotent.
            if (!overwriteConflicts && !Equal(change.BeforeExists, change.Before))
                throw new InvalidOperationException("Another saved edit changed " + string.Join(" / ", change.Path) + ". Your draft is preserved.");
            if (change.AfterExists) parent[key] = JsonNode.Parse(change.After!.Value.GetRawText()); else parent.Remove(key);
        }
        var furniture = root["furnishings"] as JsonObject ?? throw new InvalidDataException("Missing furniture group.");
        var items = furniture.Select(x => x.Value?.Deserialize<Furnishing>(Json) ?? throw new InvalidDataException("Missing furnishing.")).ToArray();
        if (furniture.Where((x, i) => x.Key != items[i].Id.ToString("N")).Any()) throw new InvalidDataException("Furniture identity changed.");
        var npcs = (root["npcs"] as JsonObject ?? []).Select(x => x.Value?.Deserialize<HomeNpc>(Json) ?? throw new InvalidDataException("Missing NPC.")).ToArray();
        var result = current with { FormatVersion = 2, SavedAt = DateTimeOffset.UtcNow, Furnishings = items, Npcs = HomeNpcs.Stored(npcs),
            Stage = JsonSerializer.SerializeToElement(root["stage"], Json), Interior = root["interior"]?.Deserialize<InteriorOptions>(Json) ?? throw new InvalidDataException("Missing interior."),
            Transform = root["transform"]?.Deserialize<StageTransform>(Json) ?? throw new InvalidDataException("Missing transform.") };
        _ = HouseFormat.Serialize(result); return result;
    }
    public static byte[] Pack<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaxExpanded) throw new InvalidDataException("Saved edits exceed the transaction budget. Save smaller batches.");
        var buffer = new byte[BrotliEncoder.GetMaxCompressedLength(bytes.Length) + 4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, bytes.Length);
        if (!BrotliEncoder.TryCompress(bytes, buffer.AsSpan(4), out var count, quality: 4, window: 22) || count + 4 > HouseFormat.MaxBytes)
            throw new InvalidDataException("Saved edits exceed the transfer budget. Save smaller batches.");
        return buffer.AsSpan(0, count + 4).ToArray();
    }
    public static T Unpack<T>(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 5 or > HouseFormat.MaxBytes) throw new InvalidDataException("Invalid edit frame size.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (length is <= 0 or > MaxExpanded) throw new InvalidDataException("Invalid expanded edit size.");
        var bytes = new byte[length]; using var decoder = new BrotliDecoder();
        if (decoder.Decompress(payload[4..], bytes, out var consumed, out var written) != OperationStatus.Done || consumed != payload.Length - 4 || written != length)
            throw new InvalidDataException("Invalid compressed edits.");
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 40 });
        HouseFormat.CheckJson(document.RootElement);
        return document.RootElement.Deserialize<T>(Json) ?? throw new InvalidDataException("Empty edit message.");
    }
    public static int ExpandedSize(byte[] payload) => BinaryPrimitives.ReadInt32LittleEndian(payload);
}

/// <summary>Preserve private drafts through remote saves; merge disjoint changes and retain conflicting work.</summary>
public sealed class HostedDraft(House initial)
{
    public House Published { get; private set; } = initial;
    public House Working { get; private set; } = initial;
    private House origin = initial;
    public House Origin => origin;
    public string? Conflict { get; private set; }
    public bool Dirty { get; private set; }
    public void Change(House house) { Working = house; Dirty = !HostedEditing.Same(Published, Working); }
    public EditBatch Batch() => HostedEditing.Create(origin, Working);
    public void Receive(House incoming, House? acknowledgedDraft = null)
    {
        var patch = HostedEditing.Create(acknowledgedDraft ?? origin, Working);
        Published = incoming;
        try { Working = HostedEditing.Apply(incoming, patch); origin = incoming; Conflict = null; }
        catch (InvalidOperationException ex) { Conflict = ex.Message; }
        Working = Working with { Music = incoming.Music };
        Dirty = !HostedEditing.Same(Published, Working);
    }
    public void Discard() { Working = origin = Published; Conflict = null; Dirty = false; }
    public void Reject(EditBatch rejected)
    {
        Working = HostedEditing.Reject(Working, Published, rejected);
        var patch = HostedEditing.Create(origin, Working);
        try { Working = HostedEditing.Apply(Published, patch); origin = Published; Conflict = null; }
        catch (InvalidOperationException ex) { Conflict = ex.Message; }
        Dirty = !HostedEditing.Same(Published, Working);
    }
    public void KeepLocalChanges()
    {
        Working = HostedEditing.Apply(Published, Batch(), overwriteConflicts: true);
        origin = Published; Conflict = null; Dirty = !HostedEditing.Same(Published, Working);
    }
}
