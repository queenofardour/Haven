using System.Text.RegularExpressions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Lumina.Excel.Sheets;

namespace HavenNative;

// Saving a real house's layout (HousingCaptureRealInterior): what each display shows. Read-only, like the rest of the
// capture. A display furnishing (HousingFurniture usage 12) names what it shows through the game's VaseFlower sheet,
// grouped by Vase row = the furnishing's usage parameter (1 vases, 2 frames, 3 partitions, 4-7 Angler's Canvases):
//  - frames, partitions and canvases change a material, and their shared group's stain info holds the number at +0xCC
//    (VaseFlower column 0 within the group: a painting, a wallpaper, a fish print). The MakePlace plugin reads the same
//    field for its layouts' "material".
//  - a vase's flowers are a model, and column 0 is only the flower (each colour is its own row), so the flowers are read
//    from the model the vase draws: .../frm/<flower>/bgparts/han_f4_m<colour × 1000 + flower>.mdl, as Haven draws them.
//  - an aquarium's fish (usage 13) are looked for in the furniture's housing object data (HousingObjectManager.DataMap),
//    each value set's first number an AquariumFish item or row. Unconfirmed in game: the log lists what was read.
public sealed partial class NativeEngine
{
    private const byte RealDisplayUsage = 12, RealAquariumUsage = 13;
    private static readonly Regex FlowerModel = new(@"/frm/(\d{1,4})/bgparts/han_f\d_m(\d+)\.mdl$", RegexOptions.CultureInvariant);

    private sealed class RealDisplays
    {
        public readonly Dictionary<int, nint> Objects = [];
        public readonly List<nint> All = [];
        public readonly Dictionary<(uint Kind, ushort Number), uint> Shown = [];
        public readonly Dictionary<(ushort Flower, byte Colour), uint> Flowers = [];
        public readonly Dictionary<uint, uint> FishByItem = [];
        public readonly HashSet<uint> FishRows = [];
        public int Paintings, Wallpapers, Prints, Vases, Tanks, Fish, Blank, Unread;
        public readonly List<string> Notes = [];
        public void Note(string line) { if (Notes.Count < 24) Notes.Add(line); }
    }

    private unsafe RealDisplays PrepareRealDisplays(IndoorTerritory* indoor)
    {
        var read = new RealDisplays();
        ref var array = ref indoor->FurnitureManager.ObjectManager.ObjectArray;
        var objects = array.Objects;
        for (var k = 0; k < array.ObjectCount && k < objects.Length; k++)
        {
            var go = objects[k].Value;
            if (go == null || go->ObjectKind != ObjectKind.HousingEventObject) continue;
            read.Objects.TryAdd(((HousingObject*)go)->HousingFurnitureIndex, (nint)go);
            read.All.Add((nint)go);
        }
        var vases = dataManager.GetExcelSheet<Vase>();
        foreach (var row in dataManager.GetExcelSheet<VaseFlower>())
        {
            if (!row.Item.IsValid || row.Item.RowId == 0) continue;
            var kind = vases.FirstOrDefault(v => v.RowId != 0 && row.RowId >= v.VaseFlowerStart && row.RowId <= v.VaseFlowerEnd).RowId;
            if (kind == 1) read.Flowers.TryAdd((row.Unknown0, row.Unknown2), row.Item.RowId);
            else if (kind is >= 2 and <= 7) read.Shown.TryAdd((kind, row.Unknown0), row.Item.RowId);
        }
        foreach (var fish in dataManager.GetExcelSheet<AquariumFish>())
        {
            if (!fish.Item.IsValid || fish.Item.RowId == 0) continue;
            read.FishRows.Add(fish.RowId);
            read.FishByItem.TryAdd(fish.Item.RowId, fish.RowId);
        }
        return read;
    }

    // What one piece shows: the item for LayoutPiece.Material (a painting, wallpaper, flowers or fish print), and an
    // aquarium's fish (AquariumFish rows).
    private unsafe (uint Material, uint[]? Fish) ReadRealDisplay(RealDisplays read, IndoorTerritory* indoor, FFXIVClientStructs.FFXIV.Client.Game.HousingFurniture* piece, long index,
        Lumina.Excel.Sheets.HousingFurniture row, string name)
    {
        if (row.UsageType is not (RealDisplayUsage or RealAquariumUsage)) return (0, null);
        // Its housing object: by its furniture index or list position, as long as it stands where the piece does;
        // otherwise whichever object stands there.
        var at = new System.Numerics.Vector3(piece->Position.X, piece->Position.Y, piece->Position.Z);
        bool Here(nint candidate) => System.Numerics.Vector3.DistanceSquared(((GameObject*)candidate)->Position, at) < .0025f;
        var address = read.Objects.TryGetValue(piece->Index, out var byIndex) && Here(byIndex) ? byIndex
            : read.Objects.TryGetValue((int)index, out var byPosition) && Here(byPosition) ? byPosition
            : read.All.FirstOrDefault(Here);
        if (address == 0)
        {
            read.Unread++;
            read.Note($"{name}: no housing object found (furniture index {piece->Index}, list position {index}, {read.All.Count} objects)");
            return (0, null);
        }
        var obj = (HousingObject*)address;
        var group = ((GameObject*)obj)->SharedGroupLayoutInstance;
        if (row.UsageType == RealAquariumUsage) return (0, ReadRealFish(read, indoor, piece, index, obj, name));
        var kind = (uint)row.UsageParameter;
        if (kind == 1)
        {
            if (group != null && FindFlowers((ILayoutInstance*)group, 0) is var (flower, colour, path))
            {
                if (read.Flowers.TryGetValue((flower, colour), out var flowers)) { read.Vases++; return (flowers, null); }
                read.Note($"{name}: flowers {path} match no VaseFlower row");
            }
            else read.Blank++;
            return (0, null);
        }
        if (kind is < 2 or > 7) return (0, null);
        var info = group == null ? null : group->StainInfo;
        var number = info == null ? (ushort)0 : *(ushort*)((byte*)info + 0xCC);
        if (number == 0) { read.Blank++; return (0, null); }
        if (!read.Shown.TryGetValue((kind, number), out var item))
        {
            read.Note($"{name}: shows number {number} (display kind {kind}), which matches no VaseFlower row");
            return (0, null);
        }
        if (kind == 2) read.Paintings++; else if (kind == 3) read.Wallpapers++; else read.Prints++;
        return (item, null);
    }

    // A vase's flowers: the first model under its shared group from a flower's folder.
    private static unsafe (ushort Flower, byte Colour, string Path)? FindFlowers(ILayoutInstance* instance, int depth)
    {
        if (instance == null || depth >= 5) return null;
        if (instance->Id.Type == InstanceType.BgPart)
        {
            var bg = ((BgPartsLayoutInstance*)instance)->GraphicsObject;
            if (bg == null || bg->ModelResourceHandle == null) return null;
            var path = bg->ModelResourceHandle->FileName.ToString();
            if (FlowerModel.Match(path) is not { Success: true } match || !ushort.TryParse(match.Groups[1].Value, out var flower)
                || !int.TryParse(match.Groups[2].Value, out var model) || model < flower || (model - flower) % 1000 != 0 || (model - flower) / 1000 > 255)
                return null;
            return (flower, (byte)((model - flower) / 1000), path);
        }
        if (instance->Id.Type != InstanceType.SharedGroup) return null;
        var children = ((SharedGroupLayoutInstance*)instance)->Instances.Instances;
        for (long i = 0; i < children.LongCount; i++)
            if (children[i].Value != null && FindFlowers(children[i].Value->Instance, depth + 1) is { } found) return found;
        return null;
    }

    // An aquarium's fish from its housing object data, looked up by each number that might key it; every number read is
    // noted, so the log shows which key and values hold the fish if this guess is wrong.
    private unsafe uint[]? ReadRealFish(RealDisplays read, IndoorTerritory* indoor, FFXIVClientStructs.FFXIV.Client.Game.HousingFurniture* piece, long index, HousingObject* obj, string name)
    {
        read.Tanks++;
        ref var map = ref indoor->FurnitureManager.ObjectManager.DataMap;
        HousingObjectManager.HousingObjectData* data = null;
        var keyed = "";
        foreach (var (label, candidate) in new[] { ("furniture index", piece->Index), ("list position", (int)index),
                     ("object furniture index", (int)obj->HousingFurnitureIndex), ("object entry", (int)obj->HousingObjectId.EntryId) })
        {
            if (candidate is < 0 or > ushort.MaxValue) continue;
            var key = (ushort)candidate;
            if (map.TryGetValuePointer(in key, out data) && data != null) { keyed = $"{label} {key}"; break; }
        }
        if (data == null)
        {
            read.Note($"{name}: no object data (furniture index {piece->Index}, list position {index}, object {obj->HousingFurnitureIndex}/{obj->HousingObjectId.EntryId}; {map.LongCount} entries)");
            return null;
        }
        List<uint> fish = [];
        List<string> raw = [];
        foreach (ref var set in data->ValueSets)
        {
            raw.Add($"{set.Value1}/{set.Value2}/{set.Value3}/{set.Value4}/{set.Value5}");
            if (set.Value1 == 0) continue;
            if (read.FishByItem.TryGetValue(set.Value1, out var byItem)) fish.Add(byItem);
            else if (read.FishRows.Contains(set.Value1)) fish.Add(set.Value1);
        }
        read.Fish += fish.Count;
        read.Note($"{name}: object data by {keyed}: {string.Join(" ", raw)} -> {fish.Count} fish");
        return fish.Count > 0 ? fish.ToArray() : null;
    }
}
